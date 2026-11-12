using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.Service;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Pathing;

/// <summary>录制时采样到的一个点。<see cref="Motion"/> 来自画面上的按键图标，不是模型猜的。</summary>
public sealed record RecordedPoint(double X, double Y, string Motion, int ElapsedMs);

/// <summary>录制结果。</summary>
public sealed record PathRecordResult(string FilePath, int Sampled, int Kept, double TotalMeters, string MotionSummary);

/// <summary>
/// 路径录制：按固定间隔读小地图坐标，停下来后简化成 BetterGI 能直接跑的路径文件。
/// <para>
/// 只记平面坐标——小地图与大地图配准是二维的，拿不到高度。能读到的是「滑翔 / 攀爬 / 地面」这类状态，
/// 会在 meta 里按段统计，用于后续人工判断哪些段是风之翼。
/// </para>
/// </summary>
public sealed class PathRecorder
{
    private static readonly ILogger Logger = App.GetLogger<PathRecorder>();

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private List<RecordedPoint> _points = [];
    private string _mapName = "Teyvat";
    private string _matchMethod = "TemplateMatch";

    public bool IsRecording { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _points.Count;
            }
        }
    }

    /// <summary>开始录制。截图器没启动时返回 false。</summary>
    public bool Start(GameRuntime? runtime, string mapName = "Teyvat", string? matchMethod = null, int intervalMs = 200)
    {
        if (runtime == null)
        {
            return false;
        }

        if (IsRecording)
        {
            return true;
        }

        _mapName = string.IsNullOrWhiteSpace(mapName) ? "Teyvat" : mapName;
        _matchMethod = matchMethod ?? ConfigService.Config!.PathingConditionConfig.MapMatchingMethod;
        _points = [];
        _cts = new CancellationTokenSource();
        IsRecording = true;
        _loop = Task.Run(() => SampleLoopAsync(runtime, Math.Clamp(intervalMs, 100, 1000), _cts.Token));
        Logger.LogInformation("[录制] 开始 · 地图 {Map} · 采样 {Ms}ms", _mapName, intervalMs);
        return true;
    }

    /// <summary>停止并保存，返回文件路径。没有采到点时返回 null。</summary>
    public async Task<PathRecordResult?> StopAsync(string? name = null)
    {
        if (!IsRecording)
        {
            return null;
        }

        _cts?.Cancel();
        if (_loop != null)
        {
            try
            {
                await _loop;
            }
            catch (Exception)
            {
                // 采样线程的异常在循环里已经记过，这里只等它退出
            }
        }

        IsRecording = false;
        _cts?.Dispose();
        _cts = null;
        _loop = null;

        List<RecordedPoint> raw;
        lock (_gate)
        {
            raw = _points.ToList();
        }

        if (raw.Count < 2)
        {
            Logger.LogWarning("[录制] 停止 · 有效点不足（{Count} 个），未保存", raw.Count);
            return null;
        }

        var kept = Simplify(raw, 1.2);
        var total = TotalMeters(kept);
        var path = Save(kept, raw.Count, total, name);
        var motions = SummarizeMotions(kept);
        Logger.LogInformation("[录制] 停止 · 采样 {Sampled} → 保留 {Kept} 点 · {Total:F0}m · {File}",
            raw.Count, kept.Count, total, path);
        return new PathRecordResult(path, raw.Count, kept.Count, total, motions);
    }

    private async Task SampleLoopAsync(GameRuntime runtime, int intervalMs, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var screen = runtime.CaptureToRectArea();
                var position = Navigation.GetPosition(screen, _mapName, _matchMethod);
                if (position.X == 0 && position.Y == 0)
                {
                    // 地图匹配失败：多半是被 UI 挡住或站在未解锁的黑雾地块上，不记脏点
                    await Task.Delay(intervalMs, ct);
                    continue;
                }

                var motion = ReadMotion(screen);
                lock (_gate)
                {
                    var shouldAdd = true;
                    if (_points.Count > 0)
                    {
                        var last = _points[^1];
                        var step = Math.Sqrt(Math.Pow(position.X - last.X, 2) + Math.Pow(position.Y - last.Y, 2));
                        // 站着不动：不记点，避免一条路径里几百个重复坐标
                        shouldAdd = step >= 0.6;
                    }

                    if (shouldAdd)
                    {
                        _points.Add(new RecordedPoint(position.X, position.Y, motion, (int)watch.ElapsedMilliseconds));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Logger.LogDebug(e, "[录制] 采样失败，跳过这一帧");
            }

            await Task.Delay(intervalMs, ct);
        }
    }

    private static string ReadMotion(BetterGenshinImpact.GameTask.Model.Area.ImageRegion screen)
    {
        try
        {
            return Bv.GetMotionStatus(screen) switch
            {
                MotionStatus.Fly => "fly",
                MotionStatus.Climb => "climb",
                _ => "walk",
            };
        }
        catch (Exception)
        {
            return "walk";
        }
    }

    /// <summary>Douglas-Peucker：把近似直线的采样点压掉，阈值按米算。</summary>
    private static List<RecordedPoint> Simplify(List<RecordedPoint> points, double tolerance)
    {
        if (points.Count < 3)
        {
            return points;
        }

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        SimplifySegment(points, 0, points.Count - 1, tolerance, keep);
        return points.Where((_, i) => keep[i]).ToList();
    }

    private static void SimplifySegment(List<RecordedPoint> points, int first, int last, double tolerance, bool[] keep)
    {
        if (last <= first + 1)
        {
            return;
        }

        var maxDistance = 0d;
        var index = -1;
        for (var i = first + 1; i < last; i++)
        {
            var distance = PointToSegment(points[i], points[first], points[last]);
            if (distance > maxDistance)
            {
                maxDistance = distance;
                index = i;
            }
        }

        if (maxDistance <= tolerance || index < 0)
        {
            return;
        }

        keep[index] = true;
        SimplifySegment(points, first, index, tolerance, keep);
        SimplifySegment(points, index, last, tolerance, keep);
    }

    private static double PointToSegment(RecordedPoint p, RecordedPoint a, RecordedPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-9)
        {
            return Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2));
        }

        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        var px = a.X + t * dx;
        var py = a.Y + t * dy;
        return Math.Sqrt(Math.Pow(p.X - px, 2) + Math.Pow(p.Y - py, 2));
    }

    private static double TotalMeters(List<RecordedPoint> points)
    {
        var total = 0d;
        for (var i = 1; i < points.Count; i++)
        {
            total += Math.Sqrt(Math.Pow(points[i].X - points[i - 1].X, 2) + Math.Pow(points[i].Y - points[i - 1].Y, 2));
        }

        return total;
    }

    private static string SummarizeMotions(List<RecordedPoint> points)
    {
        if (points.Count == 0)
        {
            return "无";
        }

        var groups = points.GroupBy(p => p.Motion).OrderByDescending(g => g.Count()).ToList();
        return string.Join(" / ", groups.Select(g => $"{g.Key} {g.Count()}"));
    }

    private string Save(List<RecordedPoint> points, int sampled, double total, string? name)
    {
        var dir = Global.Absolute(Path.Combine("User", "PathRecord"));
        Directory.CreateDirectory(dir);
        var title = string.IsNullOrWhiteSpace(name) ? $"录制-{DateTime.Now:MMdd-HHmmss}" : name.Trim();
        var file = Path.Combine(dir, $"{title}.json");

        var positions = new JsonArray();
        for (var i = 0; i < points.Count; i++)
        {
            positions.Add(new JsonObject
            {
                ["id"] = i + 1,
                ["x"] = Math.Round(points[i].X, 1),
                ["y"] = Math.Round(points[i].Y, 1),
                ["type"] = "path",
                ["move_mode"] = points[i].Motion,
                ["action"] = "",
                ["action_params"] = "",
            });
        }

        var motionCounts = new JsonObject();
        foreach (var group in points.GroupBy(p => p.Motion))
        {
            motionCounts[group.Key] = group.Count();
        }

        var root = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["name"] = title,
                ["description"] = "特别版路径录制，未经人工校验，跑之前先看一眼",
                ["author"] = "bgi-special",
                ["version"] = "1.0",
                ["bgi_version"] = Global.Version,
                ["type"] = "collect",
                ["map_name"] = _mapName,
                ["map_match_method"] = _matchMethod,
                ["tags"] = new JsonArray(),
                ["enable_monster_loot_split"] = false,
            },
            ["positions"] = positions,
            ["special_meta"] = new JsonObject
            {
                ["recorder"] = "bgi-special",
                ["recorded_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["sampled_points"] = sampled,
                ["kept_points"] = points.Count,
                ["total_meters"] = Math.Round(total, 1),
                ["motion_counts"] = motionCounts,
                ["height"] = "unavailable",
                ["height_note"] = "地图匹配只给平面坐标，路径文件没有 z 字段；fly/climb 来自画面按键图标",
            },
        };

        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return file;
    }
}
