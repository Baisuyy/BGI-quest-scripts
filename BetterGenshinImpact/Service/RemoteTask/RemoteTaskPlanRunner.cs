using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.SoloTask.Execution;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 计划里某一步的执行结果。
/// </summary>
/// <param name="TaskId">任务标识。</param>
/// <param name="TaskName">任务显示名。</param>
/// <param name="Ok">是否跑完（任务自己判定失败也算跑完，看 Reason）。</param>
/// <param name="Reason">失败或跳过原因。</param>
/// <param name="Seconds">耗时秒数。</param>
public sealed record RemoteTaskPlanStepResult(string TaskId, string TaskName, bool Ok, string? Reason, double Seconds);

/// <summary>
/// 多步计划的串行执行器：一步跑完再跑下一步。
/// <para>
/// 为什么不让模型排：模型排出来的步骤经常是「一个任务被拆成三个」，既慢又容易中途失败；
/// 而用户的原始描述里本来就已经写好了顺序（「先合成树脂，然后打幽境危战」），
/// 按句子顺序串行执行比任何规划都稳。
/// </para>
/// <para>
/// 每步都用新的 RunId 走 <see cref="SoloTaskExecutionService"/>，与手动点「开始」完全一样：
/// 任务锁、日志、通知、截图都照常。某一步失败不会中断整条计划——合成树脂可能因为树脂不足失败，
/// 但后面的「打幽境危战」仍然值得跑。
/// </para>
/// </summary>
public sealed class RemoteTaskPlanRunner(SoloTaskExecutionService execution, ILogger<RemoteTaskPlanRunner> logger)
{
    /// <summary>
    /// 单步最长等待。自动秘境这类任务偶尔会跑很久，给足余量。
    /// </summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(90);

    /// <summary>
    /// 两步之间的间隔，让上一个任务的收尾动画走完。
    /// </summary>
    private static readonly TimeSpan StepGap = TimeSpan.FromSeconds(4);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<RemoteTaskPlanStepResult> _results = [];

    private CancellationTokenSource? _cts;
    private IReadOnlyList<RemoteTaskStep> _steps = [];
    private Guid? _currentRunId;
    private int _index;

    /// <summary>
    /// 计划结束时触发，参数是给用户看的汇总文本。聊天侧订阅它来主动回报结果。
    /// </summary>
    public event Action<string>? Finished;

    /// <summary>
    /// 是否有计划在跑。
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// 当前计划标识。
    /// </summary>
    public Guid? PlanId { get; private set; }

    /// <summary>
    /// 启动一条计划，立刻返回；执行在后台进行。
    /// </summary>
    /// <param name="steps">按顺序执行的步骤。</param>
    /// <returns>计划标识。</returns>
    public Guid Start(IReadOnlyList<RemoteTaskStep> steps)
    {
        PlanId = Guid.NewGuid();
        _steps = steps;
        _results.Clear();
        _index = 0;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var planId = PlanId.Value;
        _ = Task.Run(() => RunAsync(planId, steps, _cts.Token), CancellationToken.None);
        logger.LogInformation("计划开始：{Plan}，共 {Count} 步", planId, steps.Count);
        return planId;
    }

    /// <summary>
    /// 取消当前计划：停掉正在跑的那一步，后续步骤不再启动。
    /// </summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_currentRunId is { } runId)
        {
            try
            {
                await execution.StopAsync(new SoloTaskStopRequest { RunId = runId }).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "停止计划当前步骤失败");
            }
        }
    }

    /// <summary>
    /// 给聊天用的一段进度描述。
    /// </summary>
    public string Describe()
    {
        if (!IsRunning || _steps.Count == 0)
        {
            return "当前没有计划在跑。";
        }

        var current = Math.Min(_index + 1, _steps.Count);
        var name = _steps[Math.Min(_index, _steps.Count - 1)].TaskName;
        return $"计划进行中：第 {current}/{_steps.Count} 步「{name}」";
    }

    /// <summary>
    /// 计划结束后的一段汇总。
    /// </summary>
    public string Summarize()
    {
        if (_results.Count == 0)
        {
            return "计划没有执行任何步骤。";
        }

        var lines = _results.Select(result =>
            $"{(result.Ok ? "✅" : "⚠️")} {result.TaskName}（{result.Seconds:F0}s）{(result.Ok ? string.Empty : " — " + result.Reason)}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 顺序执行每一步。
    /// </summary>
    private async Task RunAsync(Guid planId, IReadOnlyList<RemoteTaskStep> steps, CancellationToken ct)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                _index = i;
                var step = steps[i];
                var startedAt = DateTime.UtcNow;

                if (i > 0)
                {
                    await Task.Delay(StepGap, ct).ConfigureAwait(false);
                }

                var runId = Guid.NewGuid();
                _currentRunId = runId;
                var completion = new TaskCompletionSource<SoloTaskRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnCompleted(object? sender, SoloTaskRunCompletedEventArgs args)
                {
                    if (args.RunId == runId)
                    {
                        completion.TrySetResult(args.Result);
                    }
                }

                execution.RunCompleted += OnCompleted;
                try
                {
                    var response = await execution.StartAsync(new SoloTaskStartRequest
                    {
                        RunId = runId,
                        TaskId = step.TaskId
                    }).ConfigureAwait(false);

                    if (!response.Accepted)
                    {
                        Record(step, false, response.ErrorMessage, startedAt);
                        continue;
                    }

                    var winner = await Task.WhenAny(completion.Task, Task.Delay(StepTimeout, ct)).ConfigureAwait(false);
                    if (winner != completion.Task)
                    {
                        await execution.StopAsync(new SoloTaskStopRequest { RunId = runId }).ConfigureAwait(false);
                        Record(step, false, "单步超时，已停止", startedAt);
                        continue;
                    }

                    var result = await completion.Task.ConfigureAwait(false);
                    var ok = result.Outcome == SoloTaskRunOutcome.Completed;
                    Record(step, ok, ok ? null : (result.ErrorMessage ?? result.Outcome.ToString()), startedAt);
                }
                catch (OperationCanceledException)
                {
                    Record(step, false, "已取消", startedAt);
                    break;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "计划步骤 {Task} 异常", step.TaskId);
                    Record(step, false, e.Message, startedAt);
                }
                finally
                {
                    execution.RunCompleted -= OnCompleted;
                }
            }
        }
        finally
        {
            _currentRunId = null;
            IsRunning = false;
            _gate.Release();
            var summary = Summarize();
            logger.LogInformation("计划结束：{Plan}\n{Summary}", planId, summary);
            try
            {
                Finished?.Invoke(summary);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "计划结束回调失败");
            }
        }
    }

    /// <summary>
    /// 记一步的结果，同时写日志。
    /// </summary>
    private void Record(RemoteTaskStep step, bool ok, string? reason, DateTime startedAt)
    {
        var seconds = (DateTime.UtcNow - startedAt).TotalSeconds;
        _results.Add(new RemoteTaskPlanStepResult(step.TaskId, step.TaskName, ok, reason, seconds));
        logger.LogInformation("计划步骤 {Task}：{Result}{Reason}（{Seconds:F0}s）",
            step.TaskId, ok ? "完成" : "未完成", reason is null ? string.Empty : " — " + reason, seconds);
    }
}
