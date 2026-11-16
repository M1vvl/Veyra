using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GlassDock.Models;

namespace GlassDock.Music.LyricsProviders;

/// <summary>
/// 本地 LRC 文件提供者
/// </summary>
public class LocalLrcProvider : ILyricsProvider,IDisposable
{
    private volatile bool _disposed;
    public void Dispose()=>_disposed=true;
    public string Name => "本地歌词文件";
    public int Priority => 20; // 优先使用本地文件

    private static readonly string[] CommonMusicFolders = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music"),
        @"C:\Users\Public\Music"
    };

    public async Task<List<LyricLine>?> GetLyricsAsync(MediaInfo mediaInfo,CancellationToken cancellationToken=default)
    {
        if (_disposed || string.IsNullOrEmpty(mediaInfo.Title))
            return null;

        return await Task.Run(() =>
        {
            try
            {
                // 生成可能的文件名
                var possibleNames = GenerateFileNames(mediaInfo.Title, mediaInfo.Artist);

                // 搜索所有可能的音乐文件夹
                foreach (var folder in CommonMusicFolders)
                {
                    if(_disposed || cancellationToken.IsCancellationRequested)return null;
                    if (!Directory.Exists(folder))
                        continue;

                    var matchedFiles=new string?[possibleNames.Count];
                    foreach(string path in Directory.EnumerateFiles(folder,"*.lrc",SearchOption.AllDirectories))
                    {
                        if(_disposed || cancellationToken.IsCancellationRequested)return null;
                        int rank=possibleNames.FindIndex(name=>Path.GetFileNameWithoutExtension(path).Equals(name,StringComparison.OrdinalIgnoreCase));
                        if(rank>=0 && matchedFiles[rank] is null)matchedFiles[rank]=path;
                    }
                    foreach(string? matchedFile in matchedFiles)
                    {
                        if(_disposed || cancellationToken.IsCancellationRequested)return null;
                        if(matchedFile is null)continue;
                        var content=File.ReadAllText(matchedFile);
                        if(_disposed || cancellationToken.IsCancellationRequested)return null;
                        var lines=LyricsParser.ParseLRC(content);
                        if(lines.Count>0)return lines;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        },cancellationToken);
    }

    private List<string> GenerateFileNames(string title, string artist)
    {
        var names = new List<string>();
        var cleanTitle = CleanFileName(title);
        var cleanArtist = CleanFileName(artist);

        // 各种可能的命名格式
        names.Add(cleanTitle);
        names.Add($"{cleanArtist} - {cleanTitle}");
        names.Add($"{cleanTitle} - {cleanArtist}");
        names.Add($"{cleanArtist}-{cleanTitle}");
        names.Add($"{cleanTitle}-{cleanArtist}");

        return names;
    }

    private string CleanFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "";

        // 移除非法字符
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Join("", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));

        return cleaned.Trim();
    }
}
