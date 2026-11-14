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
/// QQ 音乐歌词提供者
/// </summary>
public class QQMusicLyricsProvider : ILyricsProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public string Name => "QQ音乐";
    public int Priority => 9;

    public QQMusicLyricsProvider()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://y.qq.com");
    }

    public async Task<List<LyricLine>?> GetLyricsAsync(MediaInfo mediaInfo)
    {
        if (string.IsNullOrEmpty(mediaInfo.Title))
            return null;

        try
        {
            // 搜索歌曲
            var searchResults = await SearchAsync(mediaInfo.Title, mediaInfo.Artist);
            if (searchResults == null || searchResults.Count == 0)
                return null;

            // 获取第一个结果的歌词
            var song = searchResults.First();
            var lyrics = await GetLyricsByMidAsync(song.Mid);

            return lyrics;
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<QQSong>?> SearchAsync(string title, string artist)
    {
        try
        {
            var query = $"{title} {artist}";
            var url = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?p=1&n=5&w={Uri.EscapeDataString(query)}&format=json";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<QQSearchResponse>(json);

            return result?.Data?.Song?.List ?? new List<QQSong>();
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<LyricLine>?> GetLyricsByMidAsync(string mid)
    {
        try
        {
            var url = $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={mid}&format=json&nobase64=1";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<QQLyricsResponse>(json);

            if (string.IsNullOrEmpty(result?.Lyric))
                return null;

            var lines = LyricsParser.ParseLRC(result.Lyric);
            return lines.Count > 0 ? lines : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient?.Dispose();
    }

    private class QQSearchResponse
    {
        [JsonPropertyName("data")]
        public QQSearchData? Data { get; set; }
    }

    private class QQSearchData
    {
        [JsonPropertyName("song")]
        public QQSongData? Song { get; set; }
    }

    private class QQSongData
    {
        [JsonPropertyName("list")]
        public List<QQSong>? List { get; set; }
    }

    private class QQSong
    {
        [JsonPropertyName("songmid")]
        public string Mid { get; set; } = "";

        [JsonPropertyName("songname")]
        public string Name { get; set; } = "";
    }

    private class QQLyricsResponse
    {
        [JsonPropertyName("lyric")]
        public string? Lyric { get; set; }
    }
}
