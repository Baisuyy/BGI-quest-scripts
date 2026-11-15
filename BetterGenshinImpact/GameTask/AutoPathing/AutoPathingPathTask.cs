using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.GameTask.SoloTask;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>
/// 跑一条地图追踪路线。
/// <para>
/// 地图追踪本身不是独立任务，只能在地图追踪页里点。这里把它包成任务，
/// 于是它和内置任务一样有任务锁、能被停止、能回传截图，也能被远程指令或助手选中。
/// </para>
/// </summary>
public class AutoPathingPathParam
{
    /// <summary>
    /// 路线文件相对 <c>User\AutoPathing</c> 的路径，例如 <c>蒙德\风车菊\风车菊-1.json</c>。
    /// </summary>
    public string File { get; set; } = string.Empty;
}

/// <summary>
/// 地图追踪任务：执行 <see cref="AutoPathingPathParam.File"/> 指定的路线。
/// </summary>
[SoloTaskInfo(TaskId, "地图追踪", Group = "采集与生活", Order = 300, Scopes = SoloTaskScopes.Standalone)]
public class AutoPathingPathTask : ISoloTask<AutoPathingPathParam, NoData>
{
    /// <summary>
    /// 稳定任务标识
    /// </summary>
    public const string TaskId = "auto_pathing";

    /// <summary>
    /// 路线文件不存在时的原因码
    /// </summary>
    public const string FileMissingReason = "pathing_file_missing";

    /// <summary>
    /// 路线未跑完的原因码
    /// </summary>
    public const string PathFailedReason = "pathing_failed";

    private readonly ILogger<AutoPathingPathTask> _logger = App.GetLogger<AutoPathingPathTask>();

    /// <summary>
    /// 本次运行使用的运行环境
    /// </summary>
    private readonly GameRuntime _runtime;

    /// <summary>
    /// 配置根，由创建方传入
    /// </summary>
    private readonly AllConfig _config;

    /// <summary>
    /// 创建地图追踪任务
    /// </summary>
    /// <param name="runtime">本次运行使用的运行环境</param>
    /// <param name="config">配置根</param>
    public AutoPathingPathTask(GameRuntime runtime, AllConfig config)
    {
        _runtime = runtime;
        _config = config;
    }

    /// <summary>
    /// 读取路线并执行，返回整条路线是否跑完与本次拾取统计。
    /// </summary>
    /// <param name="param">路线参数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>任务结果</returns>
    public async Task<SoloTaskResult<NoData>> RunAsync(AutoPathingPathParam param, CancellationToken ct)
    {
        var relative = (param.File ?? string.Empty).Trim();
        if (relative.Length == 0)
        {
            return SoloTaskResult.Failed(FileMissingReason, "没有指定路线文件");
        }

        var fullPath = ResolvePath(relative);
        if (fullPath is null)
        {
            return SoloTaskResult.Failed(FileMissingReason, $"找不到路线「{relative}」，先在地图追踪页或脚本仓库里下载。");
        }

        PathingTask task;
        try
        {
            task = PathingTask.BuildFromFilePath(fullPath)
                   ?? throw new InvalidOperationException("这条路线要求的 BetterGI 版本比当前高");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogError(e, "读取路线失败：{File}", fullPath);
            return SoloTaskResult.Failed(FileMissingReason, $"读取路线失败：{e.Message}");
        }

        _logger.LogInformation("地图追踪开始：{File}", relative);

        // PathExecutor 自己处理战斗、拾取与传送，这里只负责把结果翻成人话
        var result = await new PathExecutor(_runtime, _config, ct).Pathing(task);
        if (!result.IsSuccess)
        {
            return SoloTaskResult.Failed(PathFailedReason,
                string.IsNullOrWhiteSpace(result.ErrorMessage) ? "路线没有跑完" : result.ErrorMessage);
        }

        return SoloTaskResult.Success(Summarize(result));
    }

    /// <summary>
    /// 把相对路径解析成绝对路径，并挡掉跳出 AutoPathing 目录的写法。
    /// </summary>
    private static string? ResolvePath(string relative)
    {
        var root = Path.GetFullPath(MapPathingViewModel.PathJsonPath);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// 汇总本次拾取到的物品。
    /// </summary>
    private static string Summarize(PathingResult result)
    {
        var parts = new List<string>();
        if (result.CollectedItems is { Count: > 0 } items)
        {
            parts.Add(string.Join("、", items.Select(pair => $"{pair.Key}×{pair.Value}")));
        }

        if (result.SuccessFightCount > 0)
        {
            parts.Add($"战斗 {result.SuccessFightCount} 次");
        }

        return parts.Count == 0 ? "路线跑完了。" : $"路线跑完了：{string.Join("，", parts)}。";
    }
}
