using System;
using System.Text.Json.Nodes;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Runtime;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>
/// 游戏状态快照：把「模型需要知道的东西」压成一小段结构化文本。
/// <para>
/// 刻意不发整屏图：结构 JSON 通常 200–400 token，一张截图要 1500–2000 visual token，
/// 而坐标与距离已经足够判断「卡在哪、该往哪绕」。
/// </para>
/// </summary>
public sealed record GameState(
    string MapName,
    double X,
    double Y,
    double? TargetX = null,
    double? TargetY = null,
    double? Distance = null,
    string Motion = "未知",
    string Note = "")
{
    /// <summary>给模型看的紧凑文本，字段名用英文短键，省 token。</summary>
    public string ToPromptText()
    {
        var position = X == 0 && Y == 0 ? "未知（地图匹配失败）" : $"({X:F1}, {Y:F1})";
        var target = TargetX.HasValue ? $"({TargetX:F1}, {TargetY:F1})" : "无";
        var distance = Distance.HasValue ? $"{Distance:F1} 米" : "未知";
        var extra = string.IsNullOrWhiteSpace(Note) ? "" : $"；备注：{Note}";
        return $"map={MapName}；pos={position}；target={target}；distance={distance}；motion={Motion}{extra}";
    }

    /// <summary>工具结果里的 JSON 形式。</summary>
    public string ToJson()
    {
        var node = new JsonObject
        {
            ["map"] = MapName,
            ["x"] = Math.Round(X, 1),
            ["y"] = Math.Round(Y, 1),
            ["motion"] = Motion,
        };
        if (TargetX.HasValue)
        {
            node["target_x"] = Math.Round(TargetX.Value, 1);
            node["target_y"] = Math.Round(TargetY.Value, 1);
        }

        if (Distance.HasValue)
        {
            node["distance"] = Math.Round(Distance.Value, 1);
        }

        if (!string.IsNullOrWhiteSpace(Note))
        {
            node["note"] = Note;
        }

        return node.ToJsonString();
    }
}

/// <summary>
/// 从运行环境里取状态：坐标、运动状态、任务面板文字、压缩截图。
/// </summary>
public sealed class GameStateProbe(GameRuntime runtime)
{
    private static readonly ILogger Logger = App.GetLogger<GameStateProbe>();

    /// <summary>
    /// 读一次状态。坐标匹配失败时返回 (0,0)，由调用方保留上一次有效值。
    /// </summary>
    public GameState Read(ImageRegion screen, string mapName, string matchMethod,
        double? targetX = null, double? targetY = null, string note = "")
    {
        double x = 0, y = 0;
        try
        {
            var position = Navigation.GetPosition(screen, mapName, matchMethod);
            x = position.X;
            y = position.Y;
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "读取坐标失败，沿用上一次有效值");
        }

        double? distance = null;
        if (targetX.HasValue && targetY.HasValue && !(x == 0 && y == 0))
        {
            var dx = x - targetX.Value;
            var dy = y - targetY.Value;
            distance = Math.Sqrt(dx * dx + dy * dy);
        }

        return new GameState(mapName, x, y, targetX, targetY, distance, ReadMotion(screen), note);
    }

    /// <summary>
    /// 读左上角任务面板：标题、当前步骤、目标动作、计数与距离。
    /// <para>一次 OCR 约 100–300ms，只在接管开始、交互之后和每隔几步刷新，不放进每步循环。</para>
    /// </summary>
    public QuestProgress ReadQuest()
    {
        try
        {
            var tracker = new QuestHost(new OcrHost(runtime)).Tracker();
            return QuestParser.Parse(tracker.Title, tracker.Step, tracker.RawText);
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "读取任务面板失败");
            return QuestProgress.Empty;
        }
    }

    /// <summary>只需要一行文字时的便捷入口。</summary>
    public string ReadQuestText() => ReadQuest().ToPromptText();

    /// <summary>
    /// 把当前画面压成 base64 JPEG。宽度限制在 768，避免把额度花在像素上。
    /// </summary>
    public string? EncodeScreenshot(ImageRegion screen, int maxWidth = 768, int quality = 65)
    {
        try
        {
            var source = screen.SrcMat;
            if (source.Empty())
            {
                return null;
            }

            var scale = Math.Min(1d, (double)maxWidth / source.Cols);
            var width = Math.Max(1, (int)Math.Round(source.Cols * scale));
            var height = Math.Max(1, (int)Math.Round(source.Rows * scale));
            using var resized = source.Resize(new Size(width, height));
            Cv2.ImEncode(".jpg", resized, out var buffer, new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
            return Convert.ToBase64String(buffer);
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "截图编码失败");
            return null;
        }
    }

    private static string ReadMotion(ImageRegion screen)
    {
        try
        {
            return Bv.GetMotionStatus(screen) switch
            {
                MotionStatus.Climb => "攀爬中（前面多半是墙或陡坡）",
                MotionStatus.Fly => "飞行中",
                _ => "地面站立/奔跑",
            };
        }
        catch (Exception)
        {
            return "未知";
        }
    }
}
