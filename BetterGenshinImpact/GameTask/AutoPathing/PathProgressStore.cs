using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoPathing.Model;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>
/// 一条路线的执行进度。
/// <para>
/// 粒度分两级：<see cref="Segment"/> 是按传送点切出来的段，<see cref="Point"/> 是段内第几个路径点。
/// 恢复时优先按段走——段首通常就是传送点，重跑一段的代价远小于「跳过一个没执行的传送、
/// 结果人在提瓦特另一头」的风险；只有确认人还站在断点附近时，才允许点级续跑。
/// </para>
/// </summary>
public sealed class PathProgressRecord
{
    /// <summary>相对 User 目录的路径；不在 User 下时存绝对路径，仅用于识别。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>给界面和模型看的文件名。</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>文件指纹（长度 + 最后写入时间）。文件被改过，旧进度直接作废。</summary>
    public string Stamp { get; set; } = string.Empty;

    public int TotalPoints { get; set; }

    /// <summary>下一个要走的段（0 起）。等于段数表示整条路线走完了。</summary>
    public int Segment { get; set; }

    /// <summary>段内下一个点（0 起）。</summary>
    public int Point { get; set; }

    public int SegmentCount { get; set; }

    public bool Completed { get; set; }

    /// <summary>累计跑过几次（含中断）。</summary>
    public int Runs { get; set; }

    public DateTime UpdatedAt { get; set; }

    [JsonIgnore]
    public double Ratio => TotalPoints <= 0 ? 0 : Math.Clamp((Segment * 1.0) / Math.Max(SegmentCount, 1), 0, 1);
}

/// <summary>
/// 路线进度的落盘存储。
/// <para>
/// 为什么要有它：脚本跑 30 多条路线，跑到第 12 条崩了、或者被用户按了停止，
/// 之前所有脚本都只能从头再来。进度存到 <c>User/PathProgress.json</c> 之后，
/// 下一次执行同一条路线可以直接接着走，也让 AI 能回答「上次跑到哪了」。
/// </para>
/// <para>
/// 写盘按「段变化」触发并加 2 秒节流，走一步写一次会把 SSD 写坏，也没必要。
/// </para>
/// </summary>
public static class PathProgressStore
{
    private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext(typeof(PathProgressStore));

    private static readonly object Gate = new();

    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(2);

    private static Dictionary<string, PathProgressRecord>? _cache;

    private static DateTime _lastSave = DateTime.MinValue;

    private static string FilePath => Global.Absolute(Path.Combine("User", "PathProgress.json"));

    /// <summary>一条路线最多记多少条，防止脚本天天换路线把文件涨到几 MB。</summary>
    private const int MaxRecords = 400;

    /// <summary>生成进度记录的键：优先相对 User，便于换机器后仍然对得上。</summary>
    public static string KeyOf(PathingTask task)
    {
        var full = SafeFullPath(task.FullPath);
        if (string.IsNullOrEmpty(full))
        {
            return "map:" + (task.Info?.Name ?? task.FileName ?? "未命名");
        }

        var userRoot = SafeFullPath(Global.Absolute("User"));
        if (!string.IsNullOrEmpty(userRoot)
            && full.StartsWith(userRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetRelativePath(userRoot, full).Replace('\\', '/');
        }

        return full;
    }

    /// <summary>文件指纹：改了路径文件（重新录制、换了点位）就不该沿用旧进度。</summary>
    private static string StampOf(PathingTask task)
    {
        try
        {
            var full = SafeFullPath(task.FullPath);
            if (string.IsNullOrEmpty(full) || !File.Exists(full))
            {
                return "points:" + task.Positions.Count;
            }

            var info = new FileInfo(full);
            return info.Length + "@" + info.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return "points:" + task.Positions.Count;
        }
    }

    /// <summary>
    /// 取出可用的进度。文件指纹对不上、或已标记完成，都当作没有进度。
    /// </summary>
    public static PathProgressRecord? GetResumable(PathingTask task)
    {
        var record = Get(task);
        if (record == null || record.Completed)
        {
            return null;
        }

        if (record.Segment <= 0 && record.Point <= 0)
        {
            return null;
        }

        return record;
    }

    public static PathProgressRecord? Get(PathingTask task)
    {
        lock (Gate)
        {
            var map = Load();
            var key = KeyOf(task);
            if (!map.TryGetValue(key, out var record))
            {
                return null;
            }

            if (!string.Equals(record.Stamp, StampOf(task), StringComparison.Ordinal))
            {
                // 文件变了，旧进度没有意义。保留记录用于展示历史，但标记成已完成避免被误用。
                map.Remove(key);
                return null;
            }

            return record;
        }
    }

    /// <summary>
    /// 记录「即将开始走」的位置。调用点放在每个路径点执行之前，
    /// 这样程序被强杀时，最后写下的位置就是要接着走的位置。
    /// </summary>
    public static void Note(PathingTask task, int segment, int point, int segmentCount, bool force = false)
    {
        lock (Gate)
        {
            var map = Load();
            var key = KeyOf(task);
            map.TryGetValue(key, out var record);
            if (record == null)
            {
                record = new PathProgressRecord
                {
                    Key = key,
                    File = task.FileName ?? Path.GetFileName(task.FullPath) ?? key,
                    TotalPoints = task.Positions.Count,
                    SegmentCount = segmentCount,
                    Runs = 1,
                    // 指纹只在建记录时算一次：每走一个点都去问一次文件元数据没必要，
                    // 而且执行期间路线文件不会被改写。
                    Stamp = StampOf(task),
                };
                map[key] = record;
            }

            record.Segment = segment;
            record.Point = point;
            record.SegmentCount = segmentCount;
            record.TotalPoints = task.Positions.Count;
            record.Completed = false;
            record.UpdatedAt = DateTime.Now;

            if (force || DateTime.Now - _lastSave >= SaveInterval)
            {
                Save(map);
            }
        }
    }

    /// <summary>整条路线执行完毕。完成记录保留，用于展示「这条跑过了」。</summary>
    public static void MarkCompleted(PathingTask task, int segmentCount)
    {
        lock (Gate)
        {
            var map = Load();
            var key = KeyOf(task);
            map.TryGetValue(key, out var record);
            if (record == null)
            {
                record = new PathProgressRecord
                {
                    Key = key,
                    File = task.FileName ?? Path.GetFileName(task.FullPath) ?? key,
                    Runs = 1,
                };
                map[key] = record;
            }

            record.Segment = segmentCount;
            record.Point = 0;
            record.SegmentCount = segmentCount;
            record.TotalPoints = task.Positions.Count;
            record.Segment = segmentCount;
            record.Completed = true;
            record.Stamp = StampOf(task);
            record.UpdatedAt = DateTime.Now;
            Save(map);
        }
    }

    /// <summary>清掉一条路线的进度，下次从头跑。</summary>
    public static bool Clear(PathingTask task)
    {
        lock (Gate)
        {
            var map = Load();
            var removed = map.Remove(KeyOf(task));
            if (removed)
            {
                Save(map);
            }

            return removed;
        }
    }

    /// <summary>清掉所有未完成的进度。</summary>
    public static int ClearUnfinished()
    {
        lock (Gate)
        {
            var map = Load();
            var keys = map.Where(kv => !kv.Value.Completed).Select(kv => kv.Key).ToList();
            foreach (var key in keys)
            {
                map.Remove(key);
            }

            if (keys.Count > 0)
            {
                Save(map);
            }

            return keys.Count;
        }
    }

    public static IReadOnlyList<PathProgressRecord> All()
    {
        lock (Gate)
        {
            return Load().Values.OrderByDescending(r => r.UpdatedAt).ToList();
        }
    }

    /// <summary>把进度写成一句人话，界面和模型共用。</summary>
    public static string Describe(PathProgressRecord record)
    {
        var when = record.UpdatedAt.ToString("MM-dd HH:mm");
        if (record.Completed)
        {
            return $"{record.File}：已跑完（{record.TotalPoints} 点，{when}）";
        }

        return record.SegmentCount > 0
            ? $"{record.File}：第 {record.Segment + 1}/{record.SegmentCount} 段、段内第 {record.Point + 1} 点，未完成（{when}）"
            : $"{record.File}：未完成（{when}）";
    }

    private static Dictionary<string, PathProgressRecord> Load()
    {
        if (_cache != null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<PathProgressRecord>>(json, Options) ?? [];
                _cache = list.Where(r => !string.IsNullOrEmpty(r.Key))
                    .GroupBy(r => r.Key)
                    .ToDictionary(g => g.Key, g => g.First());
            }
        }
        catch (Exception e)
        {
            Logger.Debug(e, "读取路径进度失败，按空进度处理");
        }

        return _cache ??= [];
    }

    private static void Save(Dictionary<string, PathProgressRecord> map)
    {
        try
        {
            // 超量时先丢最老的已完成记录，未完成的必须留着。
            if (map.Count > MaxRecords)
            {
                var stale = map.Values
                    .Where(r => r.Completed)
                    .OrderBy(r => r.UpdatedAt)
                    .Take(map.Count - MaxRecords + 40)
                    .Select(r => r.Key)
                    .ToList();
                foreach (var key in stale)
                {
                    map.Remove(key);
                }
            }

            var folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var list = map.Values.OrderByDescending(r => r.UpdatedAt).ToList();
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Options), Encoding.UTF8);
            File.Move(tmp, FilePath, true);
            _lastSave = DateTime.Now;
        }
        catch (Exception e)
        {
            Logger.Warning(e, "写入路径进度失败");
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }
}
