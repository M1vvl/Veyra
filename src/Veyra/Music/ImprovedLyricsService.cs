using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GlassDock.Models;
using GlassDock.Music.LyricsProviders;

namespace GlassDock.Music;

/// <summary>
/// 改进的歌词服务 - 多源获取，智能缓存
/// </summary>
public class ImprovedLyricsService : IDisposable
{
    private readonly List<ILyricsProvider> _providers;
    private readonly Dictionary<string, CachedLyrics> _cache = new();
    private readonly object _cacheGate=new();
    private bool _disposed;
    private readonly CancellationTokenSource _lifetime=new();

    public ImprovedLyricsService()
    {
        // 按优先级排序：本地文件 > 网易云 > QQ音乐
        _providers = new List<ILyricsProvider>
        {
            new LocalLrcProvider(),
            new NeteaseLyricsProvider(),
            new QQMusicLyricsProvider()
        };

        _providers = _providers.OrderByDescending(p => p.Priority).ToList();
    }

    internal ImprovedLyricsService(IEnumerable<ILyricsProvider> providers)
    {
        _providers=providers.OrderByDescending(provider=>provider.Priority).ToList();
    }

    /// <summary>
    /// 获取歌词（带缓存和多源回退）
    /// </summary>
    public async Task<List<LyricLine>?> GetLyricsAsync(MediaInfo mediaInfo,CancellationToken cancellationToken=default)
    {
        if (_disposed || mediaInfo == null || string.IsNullOrEmpty(mediaInfo.Title))
            return null;

        var cacheKey = mediaInfo.GetTrackKey();

        // 检查缓存
        using var request=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,cancellationToken);
        var token=request.Token;
        lock(_cacheGate)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                var maxAge=cached.Lines is not null?TimeSpan.FromHours(2):TimeSpan.FromSeconds(5);
                if(DateTime.Now-cached.FetchedAt<maxAge)return cached.Lines;
            }
        }

        try
        {
            // 尝试所有提供者
            bool qqMusic=mediaInfo.SourceAppId.Contains("qqmusic",StringComparison.OrdinalIgnoreCase);
            foreach (var provider in _providers.OrderByDescending(provider=>
                provider.Priority+(qqMusic && provider is QQMusicLyricsProvider?2:0)))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    Program.Log($"尝试从 {provider.Name} 获取歌词: {mediaInfo.Title} - {mediaInfo.Artist}");

                    var lines = await provider.GetLyricsAsync(mediaInfo,token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    if (lines != null && lines.Count > 0)
                    {
                        // 过滤无效歌词
                        var validLines = lines
                            .Where(l => LyricsParser.IsValidLyricText(l.Text))
                            .ToList();

                        if (validLines.Count > 0)
                        {
                            Program.Log($"✓ 从 {provider.Name} 成功获取 {validLines.Count} 行歌词");
                            lock(_cacheGate)CacheResult(cacheKey, validLines);
                            return validLines;
                        }
                    }
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested) {return null;}
                catch (Exception ex)
                {
                    Program.Log($"✗ {provider.Name} 获取失败: {ex.Message}");
                }
            }

            // 所有提供者都失败
            if(_disposed || token.IsCancellationRequested)return null;
            Program.Log($"所有提供者都无法获取歌词: {mediaInfo.Title}");
            lock(_cacheGate)CacheEmpty(cacheKey);
            return null;
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) {return null;}
        catch (Exception ex)
        {
            Program.Log($"获取歌词时出错: {ex.Message}");
            return null;
        }
    }

    private void CacheResult(string key, List<LyricLine> lines)
    {
        _cache[key] = new CachedLyrics
        {
            Lines = lines,
            FetchedAt = DateTime.Now
        };

        CleanupCache();
    }

    private void CacheEmpty(string key)
    {
        _cache[key] = new CachedLyrics
        {
            Lines = null,
            FetchedAt = DateTime.Now
        };

        CleanupCache();
    }

    private void CleanupCache()
    {
        // 限制缓存大小为 100 项
        while (_cache.Count > 100)
        {
            var oldest = _cache.OrderBy(x => x.Value.FetchedAt).First();
            _cache.Remove(oldest.Key);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _=_lifetime.CancelAsync();

        foreach (var provider in _providers.OfType<IDisposable>())
        {
            provider.Dispose();
        }

        lock(_cacheGate)_cache.Clear();
    }

    private class CachedLyrics
    {
        public List<LyricLine>? Lines { get; set; }
        public DateTime FetchedAt { get; set; }
    }
}
