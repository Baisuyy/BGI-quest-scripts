using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>
/// 路径工具：让模型能「看」录制的路线与成品路线，做比对与取舍。
/// <para>
/// 这是路径录制功能的闭环——录完不是结束，还要能回答「我这条比仓库里那条好在哪」。
/// 只读 User 目录下的 pathing 文件，不碰游戏、不写文件，因此不需要授权弹窗。
/// </para>
/// </summary>
public sealed class AgentPathing
{
    private static readonly ILogger Logger = App.GetLogger<AgentPathing>();

    /// <summary>参与比对的最少点数，太少比较没有意义。</summary>
    private const int MinPoints = 2;

    /// <summary>一次比对的最大点数，防止把几千点的大文件两两比较。</summary>
    private const int MaxComparePoints = 4_000;

    public static IReadOnlyList<LlmTool> Tools { get; } =
    [
        new("list_paths", "列出可以比对的路径文件（User/PathRecord 录制结果、User/JsScript 里的成品路线），带点数与长度。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["keyword"] = new JsonObject { ["type"] = "string", ["description"] = "按路径或文件名过滤，可省略" },
                },
            }),
        new("compare_path", "比对两条路径：点数、长度、互相的最大/平均偏差。用来判断录制结果能不能替代成品路线。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["a"] = new JsonObject { ["type"] = "string", ["description"] = "基准路径，通常是成品路线" },
                    ["b"] = new JsonObject { ["type"] = "string", ["description"] = "待比对路径，通常是刚录制的结果" },
                },
                ["required"] = new JsonArray("a", "b"),
            }),
    ];

    public static bool Handles(string name) => name is "list_paths" or "compare_path";

    public Task<string> ExecuteAsync(string name, JsonObject args, CancellationToken ct)
    {
        var text = name switch
        {
            "list_paths" => ListPaths(GetString(args, "keyword")),
            "compare_path" => Compare(GetString(args, "a"), GetString(args, "b")),
            _ => $"未知动作 {name}",
        };
        return Task.FromResult(text);
    }

    /// <summary>列出 User 目录下所有带 positions 的路径文件。</summary>
    private static string ListPaths(string? keyword)
    {
        var root = Path.Combine(Global.Absolute("User"));
        if (!Directory.Exists(root))
        {
            return "User 目录不存在。";
        }

        var items = new List<(string Relative, int Points, double Meters)>();
        foreach (var file in EnumeratePathFiles(root))
        {
            var points = ReadPoints(file);
            if (points.Count < MinPoints)
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file);
            if (!string.IsNullOrWhiteSpace(keyword)
                && !relative.Contains(keyword!, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add((relative, points.Count, Length(points)));
        }

        if (items.Count == 0)
        {
            return string.IsNullOrWhiteSpace(keyword)
                ? "没有找到路径文件。录制后文件在 User/PathRecord 下。"
                : $"没有路径名包含「{keyword}」。";
        }

        var builder = new StringBuilder("共 ").Append(items.Count).AppendLine(" 条路径（相对 User 目录）：");
        foreach (var (relative, count, meters) in items.OrderByDescending(i => i.Meters).Take(40))
        {
            builder.Append("  ").Append(relative).Append("  ").Append(count).Append(" 点  ")
                .Append(meters.ToString("F0")).AppendLine("m");
        }

        builder.Append("compare_path 的 a / b 用上面的相对路径。");
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 双向最近点比对。单向比会漏掉「多绕了一大段」的情况，所以两侧都算一遍。
    /// </summary>
    private static string Compare(string? first, string? second)
    {
        var pathA = Resolve(first);
        var pathB = Resolve(second);
        if (pathA == null || pathB == null)
        {
            return pathA == null ? $"找不到路径「{first}」，先 list_paths。" : $"找不到路径「{second}」，先 list_paths。";
        }

        var a = ReadPoints(pathA);
        var b = ReadPoints(pathB);
        if (a.Count < MinPoints || b.Count < MinPoints)
        {
            return $"点数不足（{a.Count} / {b.Count}），无法比对。";
        }

        if (a.Count > MaxComparePoints)
        {
            a = Downsample(a, MaxComparePoints);
        }

        if (b.Count > MaxComparePoints)
        {
            b = Downsample(b, MaxComparePoints);
        }

        var lengthA = Length(a);
        var lengthB = Length(b);
        var forward = NearestStats(a, b);
        var backward = NearestStats(b, a);

        var builder = new StringBuilder();
        builder.Append("基准 ").Append(FileName(first)).Append("：").Append(a.Count).Append(" 点 / ")
            .Append(lengthA.ToString("F0")).AppendLine("m");
        builder.Append("待比 ").Append(FileName(second)).Append("：").Append(b.Count).Append(" 点 / ")
            .Append(lengthB.ToString("F0")).AppendLine("m");
        builder.Append("偏差（基准看待比）：最大 ").Append(forward.Max.ToString("F1"))
            .Append("m，平均 ").Append(forward.Average.ToString("F1")).AppendLine("m");
        builder.Append("偏差（待比看基准）：最大 ").Append(backward.Max.ToString("F1"))
            .Append("m，平均 ").Append(backward.Average.ToString("F1")).AppendLine("m");
        builder.Append("长度比 ").Append((lengthB / Math.Max(lengthA, 0.001)).ToString("F2")).AppendLine("×");

        // 结论只给事实区间，是否替换成品由人决定
        var worst = Math.Max(forward.Max, backward.Max);
        builder.Append(worst <= 3
            ? "结论：两条基本重合，待比可以直接替代基准。"
            : worst <= 10
                ? "结论：局部有偏移（绕路或走位不同），可替代但建议先跑一遍。"
                : "结论：差异较大，可能是不同路线或录制时绕了远路。");

        return builder.ToString();
    }

    /// <summary>找出 User 目录下所有 pathing 相关的 json。</summary>
    private static IEnumerable<string> EnumeratePathFiles(string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories);
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "枚举路径文件失败");
            yield break;
        }

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Contains("PathRecord", StringComparison.OrdinalIgnoreCase)
                || relative.Contains("pathing", StringComparison.OrdinalIgnoreCase)
                || relative.Contains("路线", StringComparison.Ordinal))
            {
                yield return file;
            }
        }
    }

    /// <summary>把相对路径解析到 User 目录下；拒绝跳出 User 的绝对路径。</summary>
    private static string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var root = Path.GetFullPath(Global.Absolute("User"));
        var candidate = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
        {
            return null;
        }

        return candidate;
    }

    private static List<(double X, double Y)> ReadPoints(string file)
    {
        var result = new List<(double, double)>();
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            if (root?["positions"] is not JsonArray positions)
            {
                return result;
            }

            foreach (var item in positions)
            {
                if (item is not JsonObject point)
                {
                    continue;
                }

                var x = point["x"]?.GetValue<double>();
                var y = point["y"]?.GetValue<double>();
                if (x.HasValue && y.HasValue)
                {
                    result.Add((x.Value, y.Value));
                }
            }
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "读取路径失败 {File}", file);
        }

        return result;
    }

    private static double Length(List<(double X, double Y)> points)
    {
        var total = 0d;
        for (var i = 1; i < points.Count; i++)
        {
            total += Math.Sqrt(Math.Pow(points[i].X - points[i - 1].X, 2) + Math.Pow(points[i].Y - points[i - 1].Y, 2));
        }

        return total;
    }

    /// <summary>每个 source 点到 target 的最近距离统计。</summary>
    private static (double Max, double Average) NearestStats(List<(double X, double Y)> source,
        List<(double X, double Y)> target)
    {
        var max = 0d;
        var sum = 0d;
        foreach (var point in source)
        {
            var nearest = double.MaxValue;
            foreach (var other in target)
            {
                var distance = (point.X - other.X) * (point.X - other.X) + (point.Y - other.Y) * (point.Y - other.Y);
                if (distance < nearest)
                {
                    nearest = distance;
                }
            }

            nearest = Math.Sqrt(nearest);
            max = Math.Max(max, nearest);
            sum += nearest;
        }

        return (max, sum / Math.Max(source.Count, 1));
    }

    private static List<(double X, double Y)> Downsample(List<(double X, double Y)> points, int limit)
    {
        if (points.Count <= limit)
        {
            return points;
        }

        var step = (double)points.Count / limit;
        var result = new List<(double, double)>(limit);
        for (var i = 0; i < limit; i++)
        {
            result.Add(points[(int)(i * step)]);
        }

        result[^1] = points[^1];
        return result;
    }

    private static string FileName(string? path)
        => string.IsNullOrWhiteSpace(path) ? "?" : Path.GetFileNameWithoutExtension(path);

    private static string? GetString(JsonObject args, string key)
        => args.TryGetPropertyValue(key, out var node) && node is JsonValue value
           && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
