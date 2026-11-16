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

    public async Task<List<LyricLine>?> GetLyricsAsync(MediaInfo mediaInfo,CancellationToken cancellationToken=default)
    {
        if (string.IsNullOrEmpty(mediaInfo.Title))
            return null;

        try
        {
            // 搜索歌曲
            var searchResults = await SearchAsync(mediaInfo.Title, mediaInfo.Artist,cancellationToken);
            if (searchResults == null || searchResults.Count == 0)
                return null;

            var song=searchResults.Select(song=>new {Song=song,Score=MatchScore(song,mediaInfo)})
                .OrderByDescending(match=>match.Score).FirstOrDefault(match=>match.Score>=6)?.Song;
            if(song is null)return null;
            var lyrics = await GetLyricsByMidAsync(song.Mid,cancellationToken);

            return lyrics;
        }
        catch
        {
            return null;
        }
    }

    private static int MatchScore(QQSong song,MediaInfo media)
    {
        string title=Normalize(media.Title),candidate=Normalize(song.Name);
        if(title.Length==0 || candidate.Length==0)return 0;
        int score=candidate==title?6:(candidate.Contains(title) || title.Contains(candidate)?4:0);
        if(score==0)return 0;
        if(!string.IsNullOrWhiteSpace(media.Artist))
        {
            string artist=Normalize(media.Artist);
            bool matches=song.Singers.Any(singer=>Normalize(singer.Name) is {Length:>0} name
                && (artist.Contains(name) || name.Contains(artist)));
            score+=matches?4:-4;
        }
        if(media.Duration>TimeSpan.Zero && song.Interval>0)
        {
            double difference=Math.Abs(media.Duration.TotalSeconds-song.Interval);
            if(difference<4)score+=2;
            else if(difference>15 && score<10)score-=5;
        }
        return score;
    }

    private static string Normalize(string value)=>string.Concat(value.Normalize(System.Text.NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private async Task<List<QQSong>?> SearchAsync(string title, string artist,CancellationToken cancellationToken)
    {
        try
        {
            var query = $"{title} {artist}";
            var url = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?p=1&n=5&w={Uri.EscapeDataString(query)}&format=json";

            using var response = await _httpClient.GetAsync(url,cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<QQSearchResponse>(json);

            return result?.Data?.Song?.List ?? new List<QQSong>();
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<LyricLine>?> GetLyricsByMidAsync(string mid,CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={mid}&format=json&nobase64=1";

            using var response = await _httpClient.GetAsync(url,cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
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
        _httpClient.Dispose();
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

        [JsonPropertyName("interval")]
        public int Interval {get;set;}

        [JsonPropertyName("singer")]
        public List<QQSinger> Singers {get;set;}=new();

    }

    private sealed class QQSinger
    {
        [JsonPropertyName("name")]
        public string Name {get;set;}="";
    }

    private class QQLyricsResponse
    {
        [JsonPropertyName("lyric")]
        public string? Lyric { get; set; }
    }
}
