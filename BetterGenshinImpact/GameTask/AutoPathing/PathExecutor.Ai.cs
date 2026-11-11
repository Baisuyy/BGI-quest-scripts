using System;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Ai;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>
/// 路径执行器的内置 AI 接管入口（特别版）。
/// <para>
/// 规则脱困（<c>TrapEscaper</c>）是固定的：松 W、下落、普攻、左右平移。
/// 遇到空气墙、夹缝、错层这类规则覆盖不了的地形，它只会重复同一套动作，直到「卡死 3 次」放弃整条路线。
/// 这里在放弃之前加一道兜底：把当前坐标与目标交给内置模型，由它决定怎么走。
/// </para>
/// </summary>
public partial class PathExecutor
{
    /// <summary>接管后至少要拉近这么多米才算有效，避免模型原地动一下就宣布成功。</summary>
    private const double AiRescueMinProgress = 5d;

    /// <summary>
    /// 尝试让内置 AI 接管脱困。返回 true 表示已经脱离，可以继续走原路线。
    /// </summary>
    private async Task<bool> TryAiRescueAsync(ImageRegion screen, WaypointForTrack waypoint, Point2f position)
    {
        var settings = _config.AiConfig;
        if (!settings.Enabled || !settings.RescueOnStuck)
        {
            Logger.LogInformation("[AI] 跳过接管：{Reason}，按原逻辑放弃本段路线",
                !settings.Enabled ? "总开关未启用" : "跑图卡死接管已关闭");
            return false;
        }

        var kernel = App.GetService<AiKernel>();
        if (kernel == null)
        {
            return false;
        }

        var unavailable = kernel.UnavailableReason;
        if (unavailable != null)
        {
            Logger.LogInformation("[AI] 跳过接管：{Reason}，按原逻辑放弃本段路线", unavailable);
            return false;
        }

        var startDistance = Distance(position, waypoint);

        var goal = new AiGoal(
            Title: $"走到地图 {waypoint.MapName} 上的点位 ({waypoint.X:F1}, {waypoint.Y:F1})",
            MapName: waypoint.MapName,
            MatchMethod: waypoint.MapMatchMethod,
            TargetX: waypoint.X,
            TargetY: waypoint.Y,
            ExtraContext: $"路线已经连续卡死 {_inTrap} 次；脱困起点 ({position.X:F1}, {position.Y:F1})，距目标 {startDistance:F1} 米",
            AllowTeleport: settings.AllowTeleport);

        var operatorInstance = new AiOperator(_runtime, _config, kernel, ct);
        AiOperatorResult result;
        try
        {
            result = await operatorInstance.RunAsync(goal, $"规则脱困连续 {_inTrap} 次无效 · 距目标 {startDistance:F0}m");
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "内置 AI 接管异常，按原逻辑继续");
            return false;
        }

        if (result.Success)
        {
            Logger.LogInformation("[AI] → 交还规则逻辑，继续本段路线");
            return true;
        }

        // 没走到目标，但明显拉近了距离，也当作脱离成功：剩下的路规则逻辑能接上
        if (!double.IsNaN(result.EndDistance) && startDistance - result.EndDistance >= AiRescueMinProgress)
        {
            Logger.LogInformation("[AI] → 已拉近 {Delta:F1}m，交还规则逻辑", startDistance - result.EndDistance);
            return true;
        }

        Logger.LogWarning("[AI] → 未奏效，按原逻辑重试或放弃本段路线");
        return false;
    }

    private static double Distance(Point2f position, WaypointForTrack waypoint)
    {
        var dx = position.X - waypoint.X;
        var dy = position.Y - waypoint.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
