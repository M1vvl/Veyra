using System;

namespace GlassDock.Models;

/// <summary>
/// 媒体播放信息
/// </summary>
public class MediaInfo
{
    public long SourceTrackId { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumTitle { get; set; } = "";
    public byte[]? Thumbnail { get; set; }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public bool IsPlaying { get; set; }
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// 生成唯一标识符
    /// </summary>
    public string GetTrackKey() => $"{Title}|{Artist}";
}
