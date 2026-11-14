using System.Collections.Generic;
using System.Threading.Tasks;
using GlassDock.Models;

namespace GlassDock.Music.LyricsProviders;

/// <summary>
/// 歌词提供者接口
/// </summary>
public interface ILyricsProvider
{
    string Name { get; }
    int Priority { get; }
    Task<List<LyricLine>?> GetLyricsAsync(MediaInfo mediaInfo);
}
