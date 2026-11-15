using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.GameTask.SoloTask;
using BetterGenshinImpact.GameTask.SoloTask.Execution;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 一条回复：文本必填，图片可选。
/// </summary>
/// <param name="Text">回复正文。</param>
/// <param name="Image">要一并回传的 JPEG 截图。</param>
/// <param name="RunId">本次指令启动的独立任务运行标识，未启动任务时为 null。</param>
public sealed record RemoteTaskReply(string Text, byte[]? Image = null, Guid? RunId = null);

/// <summary>
/// 把远程指令映射到游戏内操作：启动/停止独立任务、截图、汇报状态。
/// <para>
/// 只使用已有的独立任务框架，不新增任务类型；任务清单与界面上「独立任务」页完全一致。
/// </para>
/// </summary>
public sealed class RemoteTaskCommandHandler(
    SoloTaskExecutionService executionService,
    SoloTaskRegistry registry,
    GameRuntimeService runtimeService,
    IConfigService configService,
    ILogger<RemoteTaskCommandHandler> logger)
{
    /// <summary>
    /// 可下发的任务清单：只保留可以在独立任务入口直接运行的任务。
    /// </summary>
    public IReadOnlyList<(string TaskId, string DisplayName)> Tasks { get; } = registry.All
        .Where(d => d.Scopes.HasFlag(SoloTaskScopes.Standalone))
        .Select(d => (d.TaskId, d.DisplayName))
        .ToList();

    /// <summary>
    /// 取任务的显示名，未注册时回退到任务标识。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    public string DisplayNameOf(string? taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            return "任务";
        }

        return registry.TryGet(taskId, out var descriptor) ? descriptor.DisplayName : taskId;
    }

    /// <summary>
    /// 处理一条远程指令并生成回复。
    /// </summary>
    /// <param name="message">已去除 @ 的入站消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>回复内容。</returns>
    public async Task<RemoteTaskReply> HandleAsync(QqBotIncomingMessage message, CancellationToken cancellationToken)
    {
        var command = RemoteTaskCommandParser.Parse(message.Content, Tasks);
        return command.Kind switch
        {
            RemoteTaskCommandKind.Help => new RemoteTaskReply(BuildHelp()),
            RemoteTaskCommandKind.TaskList => new RemoteTaskReply(BuildTaskList()),
            RemoteTaskCommandKind.Status => new RemoteTaskReply(BuildStatus()),
            RemoteTaskCommandKind.Screenshot => new RemoteTaskReply("当前画面如下。", await CaptureAsync(cancellationToken).ConfigureAwait(false)),
            RemoteTaskCommandKind.Stop => new RemoteTaskReply(await StopAsync().ConfigureAwait(false)),
            RemoteTaskCommandKind.RunTask => await StartAsync(command.TaskId!, command.TaskName ?? command.TaskId!, cancellationToken).ConfigureAwait(false),
            _ => new RemoteTaskReply($"没看懂「{message.Content}」。\n\n{BuildHelp()}")
        };
    }

    /// <summary>
    /// 抓取当前画面并压成 JPEG。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="allowStartCapture">截图器未启动时是否尝试自动启动。</param>
    /// <returns>JPEG 字节；无法截图时返回 null。</returns>
    public async Task<byte[]?> CaptureAsync(CancellationToken cancellationToken, bool allowStartCapture = true)
    {
        var config = configService.Get().RemoteTaskConfig;
        try
        {
            var runtime = runtimeService.Current;
            if (runtime is null && allowStartCapture)
            {
                runtime = await StartRuntimeAsync(cancellationToken).ConfigureAwait(false);
            }

            if (runtime is null)
            {
                logger.LogInformation("远程截图失败：截图器未启动");
                return null;
            }

            using var frame = TaskControl.CaptureGameImage(runtime.Capture);
            return await Task.Run(() => EncodeJpeg(frame, config.ScreenshotMaxWidth, config.ScreenshotQuality), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "远程截图失败");
            return null;
        }
    }

    /// <summary>
    /// 启动一个独立任务。
    /// </summary>
    private async Task<RemoteTaskReply> StartAsync(string taskId, string taskName, CancellationToken cancellationToken)
    {
        var request = new SoloTaskStartRequest
        {
            RunId = Guid.NewGuid(),
            TaskId = taskId
        };
        var response = await executionService.StartAsync(request).ConfigureAwait(false);
        if (!response.Accepted)
        {
            return new RemoteTaskReply($"没能启动「{taskName}」：{response.ErrorMessage}");
        }

        logger.LogInformation("远程下发任务：{TaskId}（{RunId}）", taskId, request.RunId);
        return new RemoteTaskReply(
            $"已开始「{taskName}」。\n发送「状态」查看进度，发送「停止」可中断，跑完会自动回传截图。",
            RunId: request.RunId);
    }

    /// <summary>
    /// 停止当前任务。
    /// </summary>
    private async Task<string> StopAsync()
    {
        var status = executionService.GetStatus();
        if (status.RunId is not { } runId)
        {
            return "当前没有在跑的任务。";
        }

        var response = await executionService.StopAsync(new SoloTaskStopRequest { RunId = runId }).ConfigureAwait(false);
        return response.Accepted
            ? "已发送停止指令，任务正在收尾。"
            : $"停止失败：{response.ErrorMessage}";
    }

    /// <summary>
    /// 生成状态描述。
    /// </summary>
    private string BuildStatus()
    {
        var status = executionService.GetStatus();
        if (!status.IsActive)
        {
            return status.ErrorMessage is { Length: > 0 } error
                ? $"当前没有在跑的任务。\n上次结束原因：{error}"
                : "当前没有在跑的任务。";
        }

        var name = Tasks.FirstOrDefault(t => t.TaskId == status.TaskId).DisplayName ?? status.TaskId ?? "任务";
        var state = status.State switch
        {
            SoloTaskRunState.Starting => "正在启动",
            SoloTaskRunState.Running => "正在运行",
            SoloTaskRunState.Stopping => "正在停止",
            _ => "空闲"
        };
        return $"{state}：{name}";
    }

    /// <summary>
    /// 生成使用说明。
    /// </summary>
    private string BuildHelp()
    {
        return """
               支持这些指令：
               · 直接发任务名即可开跑，例如：一条龙、清体力、秘境、领邮件
               · 状态 —— 查看当前任务
               · 截图 —— 回传一张当前画面
               · 停止 —— 中断当前任务
               · 任务 —— 列出全部可下发的任务
               任务结束后会自动回传结果与截图。
               """;
    }

    /// <summary>
    /// 生成任务清单，按分组归并成紧凑文本。
    /// </summary>
    private string BuildTaskList()
    {
        if (Tasks.Count == 0)
        {
            return "当前版本没有可远程下发的任务。";
        }

        var lines = registry.All
            .Where(d => d.Scopes.HasFlag(SoloTaskScopes.Standalone))
            .GroupBy(d => d.Group)
            .Select(group => $"{group.Key}：{string.Join("、", group.Select(d => d.DisplayName))}");
        return "可下发的任务：\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// 在 UI 线程上启动截图器；游戏未启动或窗口找不到时返回 null。
    /// </summary>
    private async Task<GameRuntime?> StartRuntimeAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("截图器未运行，尝试为远程截图启动截图器");
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return null;
            }

            return await dispatcher.InvokeAsync(
                () => runtimeService.StartAsync(cancellationToken),
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken).Task.Unwrap().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "启动截图器失败");
            return null;
        }
    }

    /// <summary>
    /// 缩放到目标宽度并编码为 JPEG。传 0 宽度时不缩放。
    /// </summary>
    private static byte[] EncodeJpeg(Mat frame, int maxWidth, int quality)
    {
        using var resized = Resize(frame, maxWidth);
        Cv2.ImEncode(".jpg", resized, out var buffer, new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
        return buffer;
    }

    /// <summary>
    /// 按最长边限制等比缩放；已在范围内时返回副本，保证调用方总是可以释放结果。
    /// </summary>
    private static Mat Resize(Mat frame, int maxWidth)
    {
        var longest = Math.Max(frame.Cols, frame.Rows);
        if (maxWidth <= 0 || longest <= maxWidth)
        {
            return frame.Clone();
        }

        var scale = (double)maxWidth / longest;
        return frame.Resize(new Size(Math.Max(1, (int)Math.Round(frame.Cols * scale)), Math.Max(1, (int)Math.Round(frame.Rows * scale))));
    }
}
