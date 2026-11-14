using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using GlassDock.Models;

namespace GlassDock.Music;

/// <summary>
/// 歌词服务 - 从网易云音乐 API 获取歌词
/// </summary>
public class LyricsService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, CachedLyrics> _cache = new();
    private const string API_BASE = "http://localhost:3000"; // 需要本地部署 NeteaseCloudMusicApi
    private bool _disposed;

    public LyricsService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// 获取歌词（带缓存）
    /// </summary>
    public async Task<List<LyricLine>?> GetLyricsAsync(MediaInfo mediaInfo)
    {
        if (mediaInfo == null || string.IsNullOrEmpty(mediaInfo.Title))
            return null;

        var cacheKey = mediaInfo.GetTrackKey();

        // 检查缓存
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            // 成功的缓存 1 小时，失败的缓存 5 分钟
            var maxAge = cached.Lines != null ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);
            if (DateTime.Now - cached.FetchedAt < maxAge)
                return cached.Lines;
        }

        try
        {
            // 搜索歌曲
            var searchResults = await SearchAsync(mediaInfo.Title, mediaInfo.Artist);
            if (searchResults == null || searchResults.Count == 0)
            {
                CacheEmpty(cacheKey);
                return null;
            }

            // 匹配最佳结果
            var bestMatch = FindBestMatch(searchResults, mediaInfo.Title, mediaInfo.Artist, mediaInfo.Duration);
            if (bestMatch == null)
            {
                CacheEmpty(cacheKey);
                return null;
            }

            // 获取歌词
            var lyricsData = await GetLyricsByIdAsync(bestMatch.Id);
            if (lyricsData == null)
            {
                CacheEmpty(cacheKey);
                return null;
            }

            // 解析歌词（优先 YRC）
            var lines = !string.IsNullOrEmpty(lyricsData.YRC)
                ? LyricsParser.ParseYRC(lyricsData.YRC)
                : LyricsParser.ParseLRC(lyricsData.LRC);

            // 缓存结果
            _cache[cacheKey] = new CachedLyrics
            {
                Lines = lines,
                FetchedAt = DateTime.Now
            };

            // 限制缓存大小
            while (_cache.Count > 50)
            {
                var oldest = _cache.OrderBy(x => x.Value.FetchedAt).First();
                _cache.Remove(oldest.Key);
            }

            return lines;
        }
        catch (Exception ex)
        {
            Program.Log($"获取歌词失败: {ex.Message}");
            return null;
        }
    }

    private void CacheEmpty(string key)
    {
        _cache[key] = new CachedLyrics
        {
            Lines = null,
            FetchedAt = DateTime.Now
        };
    }

    private async Task<List<NeteaseSong>?> SearchAsync(string title, string artist)
    {
        try
        {
            var query = $"{title} {artist}";
            var url = $"{API_BASE}/cloudsearch?keywords={Uri.EscapeDataString(query)}";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<NeteaseSearchResponse>(json);

            return result?.Result?.Songs ?? new List<NeteaseSong>();
        }
        catch
        {
            return null;
        }
    }

    private async Task<LyricsData?> GetLyricsByIdAsync(long songId)
    {
        try
        {
            var url = $"{API_BASE}/lyric?id={songId}";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<NeteaseLyricsResponse>(json);

            if (result?.Lrc?.Lyric == null)
                return null;

            return new LyricsData
            {
                LRC = result.Lrc.Lyric,
                TranslatedLRC = result.Tlyric?.Lyric,
                YRC = result.Yrc?.Lyric
            };
        }
        catch
        {
            return null;
        }
    }

    private NeteaseSong? FindBestMatch(List<NeteaseSong> results, string title, string artist, TimeSpan duration)
    {
        if (results.Count == 0)
            return null;

        var scored = results.Select(song => new
        {
            Song = song,
            Score = CalculateMatchScore(song, title, artist, duration)
        }).OrderByDescending(x => x.Score);

        var best = scored.First();
        return best.Score > 0.6 ? best.Song : null;
    }

    private double CalculateMatchScore(NeteaseSong song, string title, string artist, TimeSpan duration)
    {
        double score = 0;

        // 标题相似度（40%）
        score += 0.4 * StringSimilarity(song.Name, title);

        // 艺术家相似度（40%）
        var songArtists = string.Join(" ", song.Ar.Select(a => a.Name));
        score += 0.4 * StringSimilarity(songArtists, artist);

        // 时长匹配（20%）
        if (duration.TotalMilliseconds > 0 && song.Dt > 0)
        {
            var durationDiff = Math.Abs((duration.TotalMilliseconds - song.Dt) / song.Dt);
            score += 0.2 * (1 - Math.Min(durationDiff, 1));
        }

        return score;
    }

    private double StringSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            return 0;

        s1 = s1.ToLowerInvariant();
        s2 = s2.ToLowerInvariant();

        int distance = LevenshteinDistance(s1, s2);
        int maxLen = Math.Max(s1.Length, s2.Length);

        return 1.0 - (double)distance / maxLen;
    }

    private int LevenshteinDistance(string s1, string s2)
    {
        int[,] d = new int[s1.Length + 1, s2.Length + 1];

        for (int i = 0; i <= s1.Length; i++)
            d[i, 0] = i;
        for (int j = 0; j <= s2.Length; j++)
            d[0, j] = j;

        for (int i = 1; i <= s1.Length; i++)
        {
            for (int j = 1; j <= s2.Length; j++)
            {
                int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(
                    d[i - 1, j] + 1,
                    d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[s1.Length, s2.Length];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient?.Dispose();
    }

    private class CachedLyrics
    {
        public List<LyricLine>? Lines { get; set; }
        public DateTime FetchedAt { get; set; }
    }

    private class LyricsData
    {
        public string LRC { get; set; } = "";
        public string? TranslatedLRC { get; set; }
        public string? YRC { get; set; }
    }

    // 网易云 API 响应模型
    private class NeteaseSearchResponse
    {
        [JsonPropertyName("result")]
        public SearchResult? Result { get; set; }
    }

    private class SearchResult
    {
        [JsonPropertyName("songs")]
        public List<NeteaseSong>? Songs { get; set; }
    }

    private class NeteaseSong
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("ar")]
        public List<Artist> Ar { get; set; } = new();

        [JsonPropertyName("dt")]
        public long Dt { get; set; }
    }

    private class Artist
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }

    private class NeteaseLyricsResponse
    {
        [JsonPropertyName("lrc")]
        public LyricContent? Lrc { get; set; }

        [JsonPropertyName("tlyric")]
        public LyricContent? Tlyric { get; set; }

        [JsonPropertyName("yrc")]
        public LyricContent? Yrc { get; set; }
    }

    private class LyricContent
    {
        [JsonPropertyName("lyric")]
        public string? Lyric { get; set; }
    }
}
