using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GlassDock.Models;

namespace GlassDock.Music;

/// <summary>
/// 歌词解析器 - 支持 LRC 和 YRC 格式
/// </summary>
public static class LyricsParser
{
    /// <summary>
    /// 解析 LRC 格式歌词
    /// </summary>
    public static List<LyricLine> ParseLRC(string lrcContent)
    {
        if (string.IsNullOrWhiteSpace(lrcContent))
            return new List<LyricLine>();

        var lines = new List<LyricLine>();

        foreach (var line in lrcContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // 匹配时间戳 [mm:ss.xx] 或 [mm:ss]
            var matches = Regex.Matches(trimmed, @"\[(\d{2}):(\d{2})(?:\.(\d{2,3}))?\]");

            if (matches.Count == 0)
                continue;

            // 提取歌词文本（去除所有时间戳）
            var text = Regex.Replace(trimmed, @"\[\d{2}:\d{2}(?:\.\d{2,3})?\]", "").Trim();

            if (string.IsNullOrEmpty(text) || text.StartsWith("[")) // 跳过元数据
                continue;

            // 同一行可能有多个时间戳（合唱部分）
            foreach (Match match in matches)
            {
                int minutes = int.Parse(match.Groups[1].Value);
                int seconds = int.Parse(match.Groups[2].Value);
                int milliseconds = match.Groups[3].Success
                    ? int.Parse(match.Groups[3].Value.PadRight(3, '0'))
                    : 0;

                var time = TimeSpan.FromMinutes(minutes)
                    .Add(TimeSpan.FromSeconds(seconds))
                    .Add(TimeSpan.FromMilliseconds(milliseconds));

                lines.Add(new LyricLine
                {
                    Time = time,
                    Text = text
                });
            }
        }

        return lines.OrderBy(l => l.Time).ToList();
    }

    /// <summary>
    /// 解析 YRC 格式歌词（网易云逐字歌词）
    /// </summary>
    public static List<LyricLine> ParseYRC(string yrcContent)
    {
        if (string.IsNullOrWhiteSpace(yrcContent))
            return new List<LyricLine>();

        var lines = new List<LyricLine>();

        foreach (var line in yrcContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // YRC 格式: [start,duration]text(wordStart,wordDuration,wordType)...
            var match = Regex.Match(trimmed, @"^\[(\d+),(\d+)\](.*)$");
            if (!match.Success)
                continue;

            long startMs = long.Parse(match.Groups[1].Value);
            long durationMs = long.Parse(match.Groups[2].Value);
            string content = match.Groups[3].Value;

            // 提取逐字时间戳
            var words = new List<LyricWord>();
            var wordMatches = Regex.Matches(content, @"\((\d+),(\d+),\d+\)");

            int lastIndex = 0;
            bool prefixTiming=wordMatches.Count>0 && string.IsNullOrWhiteSpace(content.Substring(0,wordMatches[0].Index));
            for(int wordIndex=0;wordIndex<wordMatches.Count;wordIndex++)
            {
                Match wordMatch=wordMatches[wordIndex];
                int textStart=wordMatch.Index+wordMatch.Length;
                int textEnd=wordIndex+1<wordMatches.Count?wordMatches[wordIndex+1].Index:content.Length;
                var wordText=prefixTiming?content.Substring(textStart,textEnd-textStart)
                    :content.Substring(lastIndex,wordMatch.Index-lastIndex);
                if (!string.IsNullOrEmpty(wordText))
                {
                    long wordStartMs = long.Parse(wordMatch.Groups[1].Value);
                    long wordDurMs = long.Parse(wordMatch.Groups[2].Value);

                    // 处理相对时间和绝对时间
                    long absoluteStartMs = wordStartMs >= startMs - 500 ? wordStartMs : startMs + wordStartMs;

                    words.Add(new LyricWord
                    {
                        Text = wordText,
                        Time = TimeSpan.FromMilliseconds(absoluteStartMs),
                        Duration = TimeSpan.FromMilliseconds(Math.Max(60, wordDurMs))
                    });
                }
                lastIndex = wordMatch.Index + wordMatch.Length;
            }

            // 移除所有时间戳标记，获取纯文本
            var text = Regex.Replace(content, @"\(\d+,\d+,\d+\)", "").Trim();

            if (!string.IsNullOrEmpty(text))
            {
                lines.Add(new LyricLine
                {
                    Time = TimeSpan.FromMilliseconds(startMs),
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    Text = text,
                    Words = words.Count > 0 ? words : null
                });
            }
        }

        return lines.OrderBy(l => l.Time).ToList();
    }

    /// <summary>
    /// 判断文本是否为有效歌词
    /// </summary>
    public static bool IsValidLyricText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();

        // 跳过纯音乐标记
        if (Regex.IsMatch(text, @"^(?:纯音乐[，,、\s]*(?:请欣赏)?|instrumental|no lyrics)$", RegexOptions.IgnoreCase))
            return false;

        // 跳过 JSON 元数据
        if (text.StartsWith("{"))
            return false;

        return true;
    }
}
