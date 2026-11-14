using System;
using System.Collections.Generic;

namespace GlassDock.Models;

/// <summary>
/// 歌词行
/// </summary>
public class LyricLine
{
    public TimeSpan Time { get; set; }
    public TimeSpan? Duration { get; set; }
    public string Text { get; set; } = "";
    public string? Translation { get; set; }
    public List<LyricWord>? Words { get; set; }
}

/// <summary>
/// 歌词字（用于逐字显示）
/// </summary>
public class LyricWord
{
    public string Text { get; set; } = "";
    public TimeSpan Time { get; set; }
    public TimeSpan Duration { get; set; }
}
