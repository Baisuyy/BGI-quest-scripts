using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.SoloTask.Execution;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 定时任务宿主：按「每天几点」或「树脂到多少」自动把任务跑起来。
/// <para>
/// 只做调度，不重新实现任务：命中的动作仍然交给 <see cref="SoloTaskExecutionService"/>，
/// 与手动点「开始」同一条路径，因此任务锁、日志、通知、截图回传全部自动生效。
/// </para>
/// <para>
/// 树脂规则优先走米游社接口——那条路不需要游戏在前台，不会因为「游戏在跑任务」而读不到数。
/// 没配 Cookie 时才退回画面识别，此时要求游戏正开着。
/// </para>
/// </summary>
public sealed class RemoteScheduleService(
    IConfigService configService,
    SoloTaskExecutionService executionService,
    GameOps ops,
    InstanceService instanceService,
    ILogger<RemoteScheduleService> logger) : IHostedService, IDisposable
{
    /// <summary>
    /// 主循环的取消源。
    /// </summary>
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 主循环任务。
    /// </summary>
    private Task? _loop;

    /// <summary>
    /// 上次树脂读数，用于避免同一阈值反复触发与日志刷屏。
    /// </summary>
    private int _lastResin = -1;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!instanceService.Context.IsRoot)
        {
            logger.LogInformation("非主实例，跳过定时任务");
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        logger.LogInformation("定时任务已启动");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts == null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop != null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 停止时循环卡在等待里是正常现象，不必报错
            }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <inheritdoc />
    public void Dispose() => _cts?.Dispose();

    /// <summary>
    /// 轮询循环：每 <see cref="RemoteScheduleConfig.PollSeconds"/> 秒检查一次规则。
    /// </summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        // 启动后先等一下，避开软件刚起来时截图器与任务系统还没就绪的窗口
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(300);
            try
            {
                delay = TimeSpan.FromSeconds(Math.Clamp(configService.Get().RemoteScheduleConfig.PollSeconds, 60, 3600));
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "定时任务轮询异常");
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 检查一轮规则。
    /// </summary>
    private async Task TickAsync(CancellationToken ct)
    {
        var config = configService.Get();
        var schedule = config.RemoteScheduleConfig;
        schedule.LastPollAt = DateTime.Now;
        if (!schedule.Enabled || schedule.Rules.Count == 0)
        {
            return;
        }

        var now = DateTime.Now;
        var enabled = schedule.Rules.Where(rule => rule.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            return;
        }

        // 树脂只在有树脂规则时才去读，避免为了一个没用到的数字反复截图
        var resin = enabled.Any(rule => rule.Trigger == RemoteScheduleTrigger.Resin)
            ? await ReadResinAsync(ct).ConfigureAwait(false)
            : null;

        foreach (var rule in enabled)
        {
            if (rule.HasRunOn(now))
            {
                continue;
            }

            var due = rule.Trigger switch
            {
                RemoteScheduleTrigger.Time => IsTimeDue(rule, now),
                RemoteScheduleTrigger.Resin => resin is { } value && value >= rule.ResinThreshold,
                _ => false
            };
            if (!due)
            {
                continue;
            }

            if (executionService.GetStatus().IsActive)
            {
                logger.LogInformation("定时任务 {Name} 到点了，但有任务在跑，等他跑完再说", rule.Name);
                continue;
            }

            await FireAsync(rule, now, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 时间规则是否已到点。<paramref name="now"/> 超过目标时刻两小时内都算「还来得及」，
    /// 这样软件中途重启、或上一次被任务占用时仍会在当天补跑。
    /// </summary>
    private static bool IsTimeDue(RemoteScheduleRule rule, DateTime now)
    {
        if (!TimeSpan.TryParse(rule.Time, out var target))
        {
            return false;
        }

        var late = now.TimeOfDay - target;
        return late >= TimeSpan.Zero && late <= TimeSpan.FromHours(2);
    }

    /// <summary>
    /// 读一次树脂。米游社优先；退回画面识别时要求游戏在跑，否则视为不可用。
    /// </summary>
    private async Task<int?> ReadResinAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = await ops.ReadResinAsync(ct).ConfigureAwait(false);
            if (snapshot == null)
            {
                return null;
            }

            if (snapshot.Original != _lastResin)
            {
                logger.LogInformation("定时任务读到树脂：{Resin}/{Max}（{Source}）", snapshot.Original, snapshot.Max, snapshot.Source);
                _lastResin = snapshot.Original;
            }

            return snapshot.Original;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "定时任务读取树脂失败");
            return null;
        }
    }

    /// <summary>
    /// 触发一条规则。
    /// </summary>
    private async Task FireAsync(RemoteScheduleRule rule, DateTime now, CancellationToken ct)
    {
        if (rule.Action.StartsWith('@'))
        {
            logger.LogInformation("定时任务 {Name} 使用了暂不支持的内置动作 {Action}，已跳过", rule.Name, rule.Action);
            rule.LastRunAt = now;
            rule.LastResult = "内置动作暂不支持";
            return;
        }

        var request = new SoloTaskStartRequest
        {
            RunId = Guid.NewGuid(),
            TaskId = rule.Action
        };
        var response = await executionService.StartAsync(request).ConfigureAwait(false);
        if (response.Accepted)
        {
            rule.LastRunAt = now;
            rule.LastResult = "已触发";
            logger.LogInformation("定时任务 {Name} 已触发：{TaskId}（{RunId}）", rule.Name, rule.Action, request.RunId);
        }
        else
        {
            rule.LastResult = "启动失败：" + response.ErrorMessage;
            logger.LogWarning("定时任务 {Name} 启动失败：{Reason}", rule.Name, response.ErrorMessage);
        }

        // 规则本身也要落盘，否则下次启动会重复触发同一条
        configService.Save();
    }
}
