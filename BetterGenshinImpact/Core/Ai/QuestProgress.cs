using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>
/// 任务面板进度：把左上角追踪面板的 OCR 结果压成模型能直接用的几个字段。
/// <para>
/// 面板一般只有「任务标题 + 当前一句目标」，计数（0/3）和距离（120m）都在这一句里。
/// 所以这里不解析任务树，只回答三件事：在做什么、到哪一步、还差多远。
/// </para>
/// </summary>
public sealed record QuestProgress(
    string Title,
    string Step,
    string Objective,
    int? Current,
    int? Total,
    double? Distance)
{
    /// <summary>面板为空时的占位值，不要改字段。</summary>
    public static readonly QuestProgress Empty = new("", "", "", null, null, null);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Step);

    /// <summary>
    /// 相对上一次是否推进：步骤文字变了，或计数变大。用于日志标出「任务推进」。
    /// </summary>
    public bool AdvancedOver(QuestProgress? previous)
    {
        if (previous == null || previous.IsEmpty)
        {
            return false;
        }

        if (!string.Equals(Step, previous.Step, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(Step))
        {
            return true;
        }

        return Current.HasValue && previous.Current.HasValue && Current.Value > previous.Current.Value;
    }

    /// <summary>给模型看的紧凑文本。</summary>
    public string ToPromptText()
    {
        if (IsEmpty)
        {
            return "";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Title))
        {
            parts.Add($"《{Title}》");
        }

        if (!string.IsNullOrWhiteSpace(Step))
        {
            parts.Add(Step);
        }

        if (Current.HasValue && Total.HasValue)
        {
            parts.Add($"进度 {Current}/{Total}");
        }
        else if (Current.HasValue)
        {
            parts.Add($"进度 {Current}");
        }

        if (Distance.HasValue)
        {
            parts.Add($"目标距离 {Distance.Value:F0}m");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>工具结果里的 JSON 形式。</summary>
    public string ToJson()
    {
        if (IsEmpty)
        {
            return new JsonObject { ["quest"] = "", ["note"] = "任务面板为空（未追踪任务，或面板被界面遮挡）" }.ToJsonString();
        }

        var node = new JsonObject
        {
            ["title"] = Title,
            ["step"] = Step,
        };
        if (!string.IsNullOrWhiteSpace(Objective))
        {
            node["objective"] = Objective;
        }

        if (Current.HasValue)
        {
            node["current"] = Current.Value;
        }

        if (Total.HasValue)
        {
            node["total"] = Total.Value;
        }

        if (Distance.HasValue)
        {
            node["distance"] = Math.Round(Distance.Value, 1);
        }

        return node.ToJsonString();
    }
}

/// <summary>
/// 任务面板文本解析。<see cref="QuestProgress"/> 的识别规则都集中在这里，方便单独调试。
/// </summary>
public static class QuestParser
{
    /// <summary>目标动作。按关键词在句中的出现位置取最靠前的那个，避免「前往…对话」被判成对话。</summary>
    private static readonly (string Name, string[] Keywords)[] Objectives =
    [
        ("前往", ["前往", "抵达", "到达", "回到", "返回", "离开"]),
        ("对话", ["对话", "交谈", "询问", "打听", "汇报", "告知"]),
        ("击败", ["击败", "讨伐", "消灭", "打倒", "击退"]),
        ("收集", ["收集", "采集", "拾取", "获取", "寻找"]),
        ("交付", ["交付", "提交", "交给", "给予", "递交"]),
        ("调查", ["调查", "查看", "观察", "搜寻", "探索"]),
        ("护送", ["护送", "跟随", "保护"]),
        ("放置", ["放置", "安放", "激活", "启动", "使用"]),
        ("拍照", ["拍照", "拍摄", "留影"]),
        ("制作", ["烹饪", "制作", "加工", "合成"]),
        ("开启", ["开启", "打开", "解锁", "解除"]),
    ];

    /// <summary>「0/3」「0 / 3」「０/３」这类计数。</summary>
    private static readonly Regex CountPattern = new(@"(\d{1,3})\s*[/／]\s*(\d{1,3})", RegexOptions.Compiled);

    /// <summary>「120m」「12.5 米」这类距离。</summary>
    private static readonly Regex DistancePattern = new(@"(\d{1,4}(?:\.\d{1,2})?)\s*(?:m\b|M\b|米)", RegexOptions.Compiled);

    /// <summary>空白压缩：OCR 常把一句话拆成多行，拼回去再解析。</summary>
    private static readonly Regex SpacePattern = new(@"\s+", RegexOptions.Compiled);

    public static QuestProgress Parse(string? title, string? step, string? raw = null)
    {
        var stepText = Normalize(step);
        if (string.IsNullOrWhiteSpace(stepText))
        {
            // 标题缺失时，整块文本也能兜底：第一行当标题，其余当步骤
            var lines = NormalizeLines(raw);
            if (lines.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = lines[0];
                }

                if (lines.Count > 1)
                {
                    stepText = string.Join(" ", lines.GetRange(1, lines.Count - 1));
                }
            }
        }

        title = Normalize(title);

        int? current = null;
        int? total = null;
        var count = CountPattern.Match(stepText);
        if (count.Success
            && int.TryParse(count.Groups[1].Value, out var c)
            && int.TryParse(count.Groups[2].Value, out var t))
        {
            current = c;
            total = t;
        }

        double? distance = null;
        var distanceMatch = DistancePattern.Match(stepText);
        if (distanceMatch.Success
            && double.TryParse(distanceMatch.Groups[1].Value, out var d)
            && d > 0)
        {
            distance = d;
        }

        return new QuestProgress(title, stepText, MatchObjective(stepText), current, total, distance);
    }

    /// <summary>句子里最早出现的动作关键词。</summary>
    private static string MatchObjective(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var bestIndex = int.MaxValue;
        var bestName = "";
        foreach (var (name, words) in Objectives)
        {
            foreach (var word in words)
            {
                var index = text.IndexOf(word, StringComparison.Ordinal);
                if (index >= 0 && index < bestIndex)
                {
                    bestIndex = index;
                    bestName = name;
                }
            }
        }

        return bestName;
    }

    private static string Normalize(string? text)
        => string.IsNullOrWhiteSpace(text) ? "" : SpacePattern.Replace(text, " ").Trim();

    private static List<string> NormalizeLines(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var line in raw.Split('\n', '\r'))
        {
            var text = Normalize(line);
            if (text.Length > 0)
            {
                result.Add(text);
            }
        }

        return result;
    }
}
