using System;

namespace GlassDock.Models;

/// <summary>
/// 媒体播放信息
/// </summary>
public class MediaInfo
{
    public string SourceAppId { get; set; } = "";
    public long Revision { get; set; }
    public long SourceTrackId { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumTitle { get; set; } = "";
    public byte[]? Thumbnail { get; set; }
    public string? ArtworkUrl {get;set;}
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public bool IsPlaying { get; set; }
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// 生成唯一标识符
    /// </summary>
    public string GetTrackKey() => SourceTrackId>0
        ? $"{SourceAppId}|id:{SourceTrackId}"
        : $"{SourceAppId}|{Title}|{Artist}|{AlbumTitle}";
}
