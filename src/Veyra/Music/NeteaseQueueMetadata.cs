using System.IO;
using System.Text.Json;
using GlassDock.Models;

namespace GlassDock.Music;

internal static class NeteaseQueueMetadata
{
    internal static void Complete(MediaInfo media)
    {
        string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Netease","CloudMusic","webdata","file","playingList");
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(stream.Length>8*1024*1024)return;
            using var json=JsonDocument.Parse(stream);
            foreach(var item in json.RootElement.GetProperty("list").EnumerateArray())
            {
                if(!item.TryGetProperty("track",out var track) || !track.TryGetProperty("name",out var name) || !string.Equals(name.GetString(),media.Title,StringComparison.OrdinalIgnoreCase))continue;
                if(track.TryGetProperty("artists",out var artists) && !string.IsNullOrWhiteSpace(media.Artist) && !artists.EnumerateArray().Any(artist=>artist.TryGetProperty("name",out var artistName) && media.Artist.Contains(artistName.GetString()??"",StringComparison.OrdinalIgnoreCase)))continue;
                if(track.TryGetProperty("id",out var id))media.SourceTrackId=id.ValueKind==JsonValueKind.Number?id.GetInt64():long.Parse(id.GetString()!);
                if(track.TryGetProperty("duration",out var duration))media.Duration=TimeSpan.FromMilliseconds(duration.GetDouble());
                return;
            }
        }
        catch(Exception ex) when(ex is IOException or JsonException or InvalidOperationException or FormatException or UnauthorizedAccessException) {Program.Log("NetEase queue: "+ex.Message);}
    }
}
