using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using GlassDock.Models;

namespace GlassDock.Music;

/// <summary>Resolve music artwork by its track/album identity, independently of transport events.</summary>
internal sealed class MediaArtworkService : IDisposable
{
    private readonly HttpClient _http=new() {Timeout=TimeSpan.FromSeconds(3)};
    private readonly QQMusicArtworkSource _qqNative=new();

    internal MediaArtworkService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        _http.DefaultRequestHeaders.Referrer=new Uri("https://y.qq.com/");
    }

    internal async Task<byte[]?> ReadAsync(MediaInfo media,IRandomAccessStreamReference? thumbnail,CancellationToken token)
    {
        bool netease=media.SourceAppId.Contains("cloudmusic",StringComparison.OrdinalIgnoreCase);
        bool qq=media.SourceAppId.Contains("qqmusic",StringComparison.OrdinalIgnoreCase);
        string? url=media.ArtworkUrl;
        if(qq)url=_qqNative.ReadUrl(media);
        if(netease || qq)
        {
            // These players can expose the preceding song's SMTC thumbnail after a switch.
            // Accept artwork only from the identified album for the requested track.
            if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!=Uri.UriSchemeHttps)return null;
            if(netease && !uri.Host.EndsWith(".music.126.net",StringComparison.OrdinalIgnoreCase))return null;
            if(qq && !uri.Host.Equals("y.gtimg.cn",StringComparison.OrdinalIgnoreCase))return null;
            if(qq)
            {
                var local=await _qqNative.ReadCachedAsync(uri,token).ConfigureAwait(false);
                if(local is not null)return local;
            }
            if(netease)
            {
                var resized=new UriBuilder(uri) {Query=string.IsNullOrEmpty(uri.Query)?"param=300y300":uri.Query.TrimStart('?')+"&param=300y300"};
                uri=resized.Uri;
            }
            using var response=await _http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
            if(!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is >8*1024*1024)return null;
            using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output=new System.IO.MemoryStream();
            byte[] buffer=new byte[16384];
            int read;
            while((read=await stream.ReadAsync(buffer,token).ConfigureAwait(false))>0)
            {
                if(output.Length+read>8*1024*1024)return null;
                output.Write(buffer,0,read);
            }
            return output.Length>0?output.ToArray():null;
        }
        if(thumbnail is null)return null;
        using var native=await thumbnail.OpenReadAsync().AsTask(token).ConfigureAwait(false);
        if(native.Size==0 || native.Size>8*1024*1024)return null;
        var bytes=new byte[(int)native.Size];
        var result=await native.ReadAsync(bytes.AsBuffer(),(uint)bytes.Length,InputStreamOptions.None).AsTask(token).ConfigureAwait(false);
        return result.Length==bytes.Length?bytes:null;
    }

    public void Dispose() {_qqNative.Dispose();_http.Dispose();}
}
