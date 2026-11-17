using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>
/// 路径工具：让模型能「看」录制的路线、脚本自带的路线，以及跑到哪了。
/// <para>
/// 这是路径录制与脚本执行的闭环——录完不是结束，还要能回答「我这条比仓库里那条好在哪」；
/// 脚本跑到一半断了，也要能回答「上次跑到第几条、接下来从哪开始」。
/// 只读 User 目录下的 json，不碰游戏、不写文件，因此不需要授权弹窗。
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
        new("list_paths",
            "列出可以读取的路径文件（User/PathRecord 录制结果，以及 User/JsScript 下脚本自带的路线），带点数、长度与断点状态。" +
            "脚本跑多条路线时用 scope=script 只看脚本目录。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["keyword"] = new JsonObject { ["type"] = "string", ["description"] = "按路径或文件名过滤，可省略" },
                    ["scope"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("user", "script", "all"),
                        ["description"] = "user=录制与本地路线（默认），script=JsScript 下脚本自带的路线，all=两者",
                    },
                    ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "最多列出多少条，默认 40，最大 200" },
                },
            }),
        new("read_path",
            "读取一条路线的点位明细：分段、长度、起点终点、每段的动作与采样坐标。用来回答「这条路线怎么走」「哪里需要爬山」。" +
            "path 用 list_paths 返回的相对路径。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "相对 User 目录的路径，来自 list_paths" },
                },
                ["required"] = new JsonArray("path"),
            }),
        new("path_progress",
            "查看或清除路线断点：上次跑到哪条、哪一段、哪一点，下次会不会自动接着跑。action=list 查看（默认），clear 清除指定路线，clear_all 清空全部未完成进度。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("list", "clear", "clear_all"),
                    },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "action=clear 时的路线路径" },
                },
            }),
        new("review_script",
            "只读检查一个用户脚本：入口文件、代码行数、引用了多少条路线、可疑的硬编码与无关输出（用户常说的「私货」）。" +
            "只给结论，不改文件；要改再用 read / write_file。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["script"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "脚本目录名或唯一片段，来自 list_scripts",
                    },
                },
                ["required"] = new JsonArray("script"),
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

    public static bool Handles(string name)
        => name is "list_paths" or "read_path" or "path_progress" or "review_script" or "compare_path";

    public Task<string> ExecuteAsync(string name, JsonObject args, CancellationToken ct)
    {
        var text = name switch
        {
            "list_paths" => ListPaths(GetString(args, "keyword"), GetString(args, "scope") ?? "user",
                Math.Clamp(GetInt(args, "limit") ?? 40, 1, 200)),
            "read_path" => ReadPath(GetString(args, "path")),
            "path_progress" => Progress(GetString(args, "action") ?? "list", GetString(args, "path")),
            "review_script" => ReviewScript(GetString(args, "script")),
            "compare_path" => Compare(GetString(args, "a"), GetString(args, "b")),
            _ => $"未知动作 {name}",
        };
        return Task.FromResult(text);
    }

    private static string UserRoot => Path.GetFullPath(Global.Absolute("User"));

    private static string ScriptRoot => Path.GetFullPath(Global.Absolute(Path.Combine("User", "JsScript")));

    // ─────────────────────────── list_paths ───────────────────────────

    private static string ListPaths(string? keyword, string scope, int limit)
    {
        if (!Directory.Exists(UserRoot))
        {
            return "User 目录不存在。";
        }

        var includeUser = scope is "user" or "all";
        var includeScript = scope is "script" or "all";

        var items = new List<(string Relative, int Points, double Meters)>();
        foreach (var file in EnumeratePathFiles(includeUser, includeScript))
        {
            var points = ReadPoints(file);
            if (points.Count < MinPoints)
            {
                continue;
            }

            var relative = Path.GetRelativePath(UserRoot, file).Replace('\\', '/');
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
                ? "没有找到路径文件。录制结果在 User/PathRecord，脚本自带的路线在 User/JsScript/<脚本名>/ 下。"
                : $"没有路径名包含「{keyword}」。";
        }

        // 按目录分组，脚本的 30 多条路线挤在一起时才看得清是哪一组。
        var total = items.Count;
        var builder = new StringBuilder("共 ").Append(total).Append(" 条路径（相对 User 目录）：");
        foreach (var group in items
                     .OrderByDescending(i => i.Meters)
                     .Take(limit)
                     .GroupBy(i => Path.GetDirectoryName(i.Relative)?.Replace('\\', '/') ?? "."))
        {
            builder.AppendLine().Append('【').Append(group.Key).Append('】');
            foreach (var (relative, count, meters) in group)
            {
                builder.Append("  ").Append(Path.GetFileName(relative))
                    .Append("  ").Append(count).Append(" 点  ")
                    .Append(meters.ToString("F0")).Append('m');

                var progress = TryDescribeProgress(relative);
                if (progress != null)
                {
                    builder.Append("  ← ").Append(progress);
                }

                builder.AppendLine();
            }
        }

        if (total > limit)
        {
            builder.Append("（只显示前 ").Append(limit).Append(" 条，用 keyword 精确过滤）").AppendLine();
        }

        builder.Append("read_path 读明细，compare_path 比对，path_progress 看断点。");
        return builder.ToString().TrimEnd();
    }

    /// <summary>路径文件进度的一句话摘要；没有进度返回 null。</summary>
    private static string? TryDescribeProgress(string relative)
    {
        var record = PathProgressStore.All()
            .FirstOrDefault(r => string.Equals(r.Key, relative, StringComparison.OrdinalIgnoreCase));
        if (record == null)
        {
            return null;
        }

        if (record.Completed)
        {
            return "已跑完";
        }

        return record.SegmentCount > 0
            ? $"断点 {record.Segment + 1}/{record.SegmentCount} 段"
            : "有未完成进度";
    }

    /// <summary>找出 User 目录下的路径 json。脚本目录只认带 positions 的文件，避免把配置读成路线。</summary>
    private static IEnumerable<string> EnumeratePathFiles(bool includeUser, bool includeScript)
    {
        if (includeUser)
        {
            foreach (var file in SafeEnumerate(UserRoot))
            {
                var relative = Path.GetRelativePath(UserRoot, file);
                // 脚本目录单独处理，避免和 includeScript 重复
                if (relative.StartsWith("JsScript", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (relative.Contains("PathRecord", StringComparison.OrdinalIgnoreCase)
                    || relative.Contains("pathing", StringComparison.OrdinalIgnoreCase)
                    || relative.Contains("路线", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }

        if (includeScript && Directory.Exists(ScriptRoot))
        {
            foreach (var file in SafeEnumerate(ScriptRoot))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories);
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "枚举路径文件失败 {Root}", root);
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }
    }

    // ─────────────────────────── read_path ───────────────────────────

    private static string ReadPath(string? path)
    {
        var full = Resolve(path);
        if (full == null)
        {
            return $"找不到路径「{path}」，先 list_paths。";
        }

        var task = LoadTask(full);
        if (task == null)
        {
            return $"「{Path.GetFileName(full)}」不是可识别的路线文件（没有 positions，或要求的 BGI 版本高于当前）。";
        }

        if (task.Positions.Count == 0)
        {
            return "这条路线没有路径点。";
        }

        var points = task.Positions.Select(p => (p.X, p.Y)).ToList();
        var segments = SplitSegments(task);
        var builder = new StringBuilder();

        builder.Append("文件：").Append(Path.GetRelativePath(UserRoot, full).Replace('\\', '/')).AppendLine();
        builder.Append("名称：").Append(string.IsNullOrWhiteSpace(task.Info.Name) ? "（未填）" : task.Info.Name);
        if (!string.IsNullOrWhiteSpace(task.Info.Type))
        {
            builder.Append("　类型：").Append(task.Info.Type);
        }

        builder.AppendLine();
        builder.Append("规模：").Append(task.Positions.Count).Append(" 点，总长 ")
            .Append(Length(points).ToString("F0")).Append("m，分 ").Append(segments.Count).AppendLine(" 段（按传送点切分）");

        var start = task.Positions[0];
        var end = task.Positions[^1];
        builder.Append("起止：(").Append(start.X.ToString("F0")).Append(", ").Append(start.Y.ToString("F0"))
            .Append(") → (").Append(end.X.ToString("F0")).Append(", ").Append(end.Y.ToString("F0")).AppendLine(")");

        var actionCount = task.Positions
            .Where(p => !string.IsNullOrWhiteSpace(p.Action))
            .GroupBy(p => p.Action!)
            .OrderByDescending(g => g.Count())
            .ToList();
        if (actionCount.Count > 0)
        {
            builder.Append("动作：")
                .Append(string.Join("、", actionCount.Select(g => $"{DescribeAction(g.Key)}×{g.Count()}")))
                .AppendLine();
        }

        builder.AppendLine().AppendLine("分段：");
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            builder.Append("  ").Append(i + 1).Append(". ").Append(segment.Count).Append(" 点，")
                .Append(segment.Meters.ToString("F0")).Append("m，")
                .Append(segment.IsTeleport ? "段首传送" : "段首非传送");

            if (segment.Actions.Count > 0)
            {
                builder.Append("，动作 ").Append(string.Join("/", segment.Actions));
            }

            builder.AppendLine();

            // 每段给 4 个采样点，够模型判断走向，又不至于把上下文撑爆。
            var step = Math.Max(1, segment.Count / 4);
            var samples = new List<string>();
            for (var k = 0; k < segment.Count; k += step)
            {
                var p = segment.Points[k];
                samples.Add($"({p.X:F0},{p.Y:F0})");
            }

            builder.Append("     坐标：").Append(string.Join(" → ", samples));
            if (segment.Count > 1)
            {
                var delta = segment.Points[^1].X - segment.Points[0].X;
                var deltaY = segment.Points[^1].Y - segment.Points[0].Y;
                builder.Append("　位移 (").Append(delta.ToString("F0")).Append(", ")
                    .Append(deltaY.ToString("F0")).Append(')');
            }

            builder.AppendLine();
        }

        var progress = PathProgressStore.All()
            .FirstOrDefault(r => string.Equals(r.Key, PathProgressStore.KeyOf(task), StringComparison.OrdinalIgnoreCase));
        if (progress != null)
        {
            builder.AppendLine().Append("进度：").Append(PathProgressStore.Describe(progress));
            if (!progress.Completed)
            {
                builder.Append("（下次执行会从这一段自动接着走）");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private sealed record SegmentInfo(List<(double X, double Y)> Points, double Meters, bool IsTeleport,
        List<string> Actions)
    {
        public int Count => Points.Count;
    }

    private static List<SegmentInfo> SplitSegments(PathingTask task)
    {
        var result = new List<SegmentInfo>();
        var buffer = new List<(double X, double Y)>();
        var actions = new List<string>();
        var isTeleport = false;

        void Flush()
        {
            if (buffer.Count == 0)
            {
                return;
            }

            result.Add(new SegmentInfo(buffer, Length(buffer), isTeleport, actions.Distinct().ToList()));
            buffer = [];
            actions = [];
        }

        foreach (var point in task.Positions)
        {
            if (string.Equals(point.Type, "teleport", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                isTeleport = true;
            }

            buffer.Add((point.X, point.Y));
            if (!string.IsNullOrWhiteSpace(point.Action))
            {
                actions.Add(DescribeAction(point.Action));
            }
        }

        Flush();
        return result;
    }

    // ─────────────────────────── path_progress ───────────────────────────

    private static string Progress(string action, string? path)
    {
        switch (action)
        {
            case "clear":
            {
                var full = Resolve(path);
                if (full == null)
                {
                    return $"找不到路径「{path}」，先 list_paths。";
                }

                var task = LoadTask(full);
                if (task == null)
                {
                    return "不是可识别的路线文件，无法清除进度。";
                }

                return PathProgressStore.Clear(task) ? "已清除这条路线的进度，下次从头跑。" : "这条路线本来就没有进度。";
            }
            case "clear_all":
            {
                var count = PathProgressStore.ClearUnfinished();
                return count > 0 ? $"已清除 {count} 条未完成进度。" : "没有未完成的进度。";
            }
            default:
            {
                var all = PathProgressStore.All();
                if (all.Count == 0)
                {
                    return "还没有任何路线进度记录。跑过一次地图追踪后才会出现。";
                }

                var unfinished = all.Where(r => !r.Completed).ToList();
                var builder = new StringBuilder();
                builder.Append("共 ").Append(all.Count).Append(" 条记录，其中 ").Append(unfinished.Count)
                    .AppendLine(" 条未完成（下次执行会自动接着走）：");
                foreach (var record in all.Take(30))
                {
                    builder.Append("  ").Append(PathProgressStore.Describe(record)).AppendLine();
                }

                return builder.ToString().TrimEnd();
            }
        }
    }

    // ─────────────────────────── review_script ───────────────────────────

    /// <summary>
    /// 只读体检一个用户脚本。
    /// <para>
    /// 用户说「私货太多」时，其实是在问三件事：这脚本到底在干什么、哪些输出与功能无关、
    /// 哪些写死的东西换台机器就会坏。这里只负责把事实列出来，改不改由人和模型商量。
    /// </para>
    /// </summary>
    private static string ReviewScript(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "需要脚本名。先 list_scripts 看有哪些。";
        }

        if (!Directory.Exists(ScriptRoot))
        {
            return "User/JsScript 目录不存在，还没装过脚本。";
        }

        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(ScriptRoot).ToList();
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "枚举脚本目录失败");
            return "读取脚本目录失败。";
        }

        var dir = dirs.FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase))
                  ?? dirs.FirstOrDefault(d => Path.GetFileName(d).Contains(name, StringComparison.OrdinalIgnoreCase));

        if (dir == null)
        {
            var cands = dirs.Select(d => Path.GetFileName(d)).Take(12).ToList();
            return $"没找到脚本「{name}」。现有：{string.Join("、", cands)}";
        }

        var jsFiles = new List<string>();
        try
        {
            jsFiles = Directory.EnumerateFiles(dir, "*.js", SearchOption.AllDirectories)
                .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(f => f.Length)
                .Take(40)
                .ToList();
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "枚举脚本文件失败 {Dir}", dir);
        }

        var builder = new StringBuilder();
        builder.Append("脚本：").Append(Path.GetFileName(dir)).Append("（")
            .Append(Path.GetRelativePath(ScriptRoot, dir).Replace('\\', '/')).AppendLine("）");

        if (jsFiles.Count == 0)
        {
            builder.AppendLine("没有 .js 文件，可能只是路线包或配置目录。");
            return builder.ToString().TrimEnd();
        }

        var totalLines = 0;
        var entry = jsFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("main.js", StringComparison.OrdinalIgnoreCase))
                    ?? jsFiles[0];
        builder.Append("入口：").Append(Path.GetFileName(entry)).Append("　代码文件：").Append(jsFiles.Count).AppendLine(" 个");

        // 路线：脚本目录下所有带 positions 的 json
        var routeCount = 0;
        var routePoints = 0;
        foreach (var file in SafeEnumerate(dir))
        {
            var points = ReadPoints(file);
            if (points.Count >= MinPoints)
            {
                routeCount++;
                routePoints += points.Count;
            }
        }

        if (routeCount > 0)
        {
            builder.Append("路线：").Append(routeCount).Append(" 条，共 ").Append(routePoints).AppendLine(" 点");
        }

        // 逐行扫可疑模式。只取前几条示例，够模型判断，不刷屏。
        var noise = new List<string>();
        var hardcoded = new List<string>();
        var fileIo = new List<string>();
        var absolute = new List<string>();
        var links = new List<string>();

        foreach (var file in jsFiles)
        {
            string[] lines;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 4 * 1024 * 1024)
                {
                    continue;
                }

                lines = File.ReadAllLines(file);
            }
            catch
            {
                continue;
            }

            totalLines += lines.Length;
            var shortName = Path.GetFileName(file);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length > 400)
                {
                    line = line[..400];
                }

                var where = $"{shortName}:{i + 1}";

                if (ChineseText.IsMatch(line) && LogCall.IsMatch(line))
                {
                    noise.Add($"{where}  {Clip(line.Trim(), 60)}");
                }

                if (SwitchParty.IsMatch(line))
                {
                    hardcoded.Add($"{where}  {Clip(line.Trim(), 60)}");
                }

                if (FileWrite.IsMatch(line))
                {
                    fileIo.Add($"{where}  {Clip(line.Trim(), 60)}");
                }

                if (AbsolutePath.IsMatch(line))
                {
                    absolute.Add($"{where}  {Clip(line.Trim(), 60)}");
                }

                if ((line.Contains("http://", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("https://", StringComparison.OrdinalIgnoreCase))
                    && !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    links.Add($"{where}  {Clip(line.Trim(), 60)}");
                }
            }
        }

        builder.Append("代码行数：").AppendLine(totalLines.ToString());

        AppendSection(builder, "无关输出（日志里带中文的长句）", noise);
        AppendSection(builder, "写死的队伍名", hardcoded);
        AppendSection(builder, "写文件 / 建目录（可能是断点续跑记录）", fileIo);
        AppendSection(builder, "绝对路径（换机器会失效）", absolute);
        AppendSection(builder, "外链（广告或更新地址）", links);

        if (noise.Count == 0 && hardcoded.Count == 0 && fileIo.Count == 0
            && absolute.Count == 0 && links.Count == 0)
        {
            builder.AppendLine("没扫到明显的硬编码或无关输出。");
        }
        else
        {
            builder.AppendLine();
            builder.Append("注意：「写文件」与「写死队伍名」有时就是脚本的断点续跑和换队机制，")
                .Append("直接删会让脚本每次从头跑或组不到队。改之前先说明这一点。");
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder builder, string title, List<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.Append("· ").Append(title).Append("：").Append(items.Count).AppendLine(" 处");
        foreach (var item in items.Take(3))
        {
            builder.Append("    ").AppendLine(item);
        }

        if (items.Count > 3)
        {
            builder.Append("    （还有 ").Append(items.Count - 3).AppendLine(" 处）");
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static readonly System.Text.RegularExpressions.Regex ChineseText =
        new(@"[\u4e00-\u9fa5]{4,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex LogCall =
        new(@"log\s*\.\s*(info|warn|error|debug)|console\s*\.\s*log", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex SwitchParty =
        new("switchParty\\s*\\(|switch_party\\s*\\(|toParty\\s*\\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex FileWrite =
        new(@"writeTextSync|writeText\s*\(|createDirectory|readPathSync", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex AbsolutePath =
        new(@"[A-Za-z]:\\", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ─────────────────────────── compare_path ───────────────────────────

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

    // ─────────────────────────── 公共 ───────────────────────────

    private static PathingTask? LoadTask(string fullPath)
    {
        try
        {
            var json = File.ReadAllText(fullPath);
            var task = PathingTask.BuildFromJson(json, fullPath);
            task.FileName = Path.GetFileName(fullPath);
            task.FullPath = fullPath;
            return task;
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "读取路线失败 {File}", fullPath);
            return null;
        }
    }

    /// <summary>把相对路径解析到 User 目录下；拒绝跳出 User 的绝对路径。</summary>
    private static string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var root = UserRoot;
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

    private static string DescribeAction(string action) => action switch
    {
        "fight" => "战斗",
        "pick_around" => "周围拾取",
        "nahida_collect" => "纳西妲采集",
        "up_down_grab_leaf" => "四叶印",
        "combat_script" => "战斗脚本",
        "log_output" => "日志输出",
        "mining" => "挖矿",
        "open_chest" => "开箱",
        "chat" => "对话",
        _ => action,
    };

    private static string FileName(string? path)
        => string.IsNullOrWhiteSpace(path) ? "?" : Path.GetFileNameWithoutExtension(path);

    private static string? GetString(JsonObject args, string key)
        => args.TryGetPropertyValue(key, out var node) && node is JsonValue value
           && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static int? GetInt(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : null;
    }
}
