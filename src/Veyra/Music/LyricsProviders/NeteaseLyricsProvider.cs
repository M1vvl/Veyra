using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using GlassDock.Models;

namespace GlassDock.Music.LyricsProviders;

/// <summary>
/// 网易云音乐歌词提供者
/// </summary>
public class NeteaseLyricsProvider : ILyricsProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private const string API_BASE = "https://music.163.com/api";
    private bool _disposed;

    public string Name => "网易云音乐";
    public int Priority => 10;

    public NeteaseLyricsProvider()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://music.163.com");
    }

    public async Task<List<LyricLine>?> GetLyricsAsync(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.Title))
            return null;

        try
        {
            // 搜索歌曲
            if(mediaInfo.SourceTrackId>0)
            {
                var direct=await GetLyricsByIdAsync(mediaInfo.SourceTrackId);
                if(direct is not null)
                {
                    var parsed=!string.IsNullOrEmpty(direct.YRC)?LyricsParser.ParseYRC(direct.YRC):LyricsParser.ParseLRC(direct.LRC);
                    if(parsed.Count>0)return parsed;
                }
            }
            var searchResults = await SearchAsync(mediaInfo.Title, mediaInfo.Artist);
            if (searchResults == null || searchResults.Count == 0)
                return null;

            // 匹配最佳结果
            var bestMatch = FindBestMatch(searchResults, mediaInfo.Title, mediaInfo.Artist, mediaInfo.Duration);
            if (bestMatch == null)
                return null;

            // 获取歌词
            var lyricsData = await GetLyricsByIdAsync(bestMatch.Id);
            if (lyricsData == null)
                return null;

            // 解析歌词
            var lines = !string.IsNullOrEmpty(lyricsData.YRC)
                ? LyricsParser.ParseYRC(lyricsData.YRC)
                : LyricsParser.ParseLRC(lyricsData.LRC);

            return lines.Count > 0 ? lines : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<NeteaseSong>?> SearchAsync(string title, string artist)
    {
        try
        {
            var query = $"{title} {artist}";
            var url = $"{API_BASE}/search/get?s={Uri.EscapeDataString(query)}&type=1&limit=10";
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
            var url = $"{API_BASE}/song/lyric?id={songId}&lv=1&tv=-1";
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
        return best.Score > 0.5 ? best.Song : null;
    }

    private double CalculateMatchScore(NeteaseSong song, string title, string artist, TimeSpan duration)
    {
        double score = 0;

        // 标题相似度（40%）
        score += 0.4 * StringSimilarity(song.Name, title);

        // 艺术家相似度（40%）
        var songArtists = string.Join(" ", song.Artists.Select(a => a.Name));
        score += 0.4 * StringSimilarity(songArtists, artist);

        // 时长匹配（20%）
        if (duration.TotalMilliseconds > 0 && song.Duration > 0)
        {
            var durationDiff = Math.Abs((duration.TotalMilliseconds - song.Duration) / song.Duration);
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

    private class LyricsData
    {
        public string LRC { get; set; } = "";
        public string? TranslatedLRC { get; set; }
        public string? YRC { get; set; }
    }

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

        [JsonPropertyName("artists")]
        public List<Artist> Artists { get; set; } = new();

        [JsonPropertyName("duration")]
        public long Duration { get; set; }
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
