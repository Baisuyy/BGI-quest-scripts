using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>
/// 记录本地助手发起过的运行，解决两个问题：
/// <para>
/// 一是脚本走的是 TaskRunner，不进 <c>SoloTaskExecutionService</c> 状态机，<c>task_status</c> 只看状态机会误报「空闲」，
/// 模型据此重复下发，白白耗掉好几步；二是每次对话都是新会话，用户说「重新执行」时模型看不到上一轮做过什么。
/// </para>
/// </summary>
public static class AgentRunMemory
{
    /// <summary>
    /// 保留的最近运行条数，够模型对照「刚才那个」即可，不宜让提示词膨胀。
    /// </summary>
    private const int MaxRecent = 6;

    /// <summary>
    /// 保护活动脚本与历史列表。
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>
    /// 当前正在运行的脚本；与内置任务互斥，同一时刻最多一个。
    /// </summary>
    private static ActiveScript? _activeScript;

    /// <summary>
    /// 最近完成的运行，新的在前。
    /// </summary>
    private static readonly LinkedList<RunRecord> Recent = new();

    /// <summary>
    /// 登记一次脚本运行开始。
    /// </summary>
    /// <param name="name">脚本名。</param>
    public static void BeginScript(string name)
    {
        lock (Gate)
        {
            _activeScript = new ActiveScript(name, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// 登记一次脚本运行结束。名称不匹配时忽略，避免上一轮的迟到回调覆盖当前状态。
    /// </summary>
    /// <param name="name">脚本名。</param>
    /// <param name="success">是否正常结束。</param>
    /// <param name="summary">结果摘要。</param>
    public static void EndScript(string name, bool success, string summary)
    {
        lock (Gate)
        {
            if (_activeScript is null || !string.Equals(_activeScript.Name, name, StringComparison.Ordinal))
            {
                return;
            }

            var elapsed = (int)(DateTime.UtcNow - _activeScript.StartedAt).TotalSeconds;
            _activeScript = null;
            Push(new RunRecord(name, "脚本", success, summary, elapsed, DateTime.UtcNow));
        }
    }

    /// <summary>
    /// 登记一次内置任务结束。
    /// </summary>
    /// <param name="name">任务名。</param>
    /// <param name="success">是否正常结束。</param>
    /// <param name="summary">结果摘要。</param>
    /// <param name="elapsedSeconds">耗时秒数。</param>
    public static void RecordTask(string name, bool success, string summary, int elapsedSeconds)
    {
        lock (Gate)
        {
            Push(new RunRecord(name, "任务", success, summary, elapsedSeconds, DateTime.UtcNow));
        }
    }

    /// <summary>
    /// 描述当前正在运行的脚本，没有时返回 null。
    /// </summary>
    public static string? DescribeActive()
    {
        lock (Gate)
        {
            if (_activeScript is not { } active)
            {
                return null;
            }

            var seconds = (int)(DateTime.UtcNow - active.StartedAt).TotalSeconds;
            return $"运行中：脚本「{active.Name}」，已运行 {seconds} 秒。要停下用 stop_task。";
        }
    }

    /// <summary>
    /// 拼出最近几次执行的摘要，供系统提示词使用；没有记录时返回空串。
    /// </summary>
    /// <param name="max">最多条数。</param>
    public static string DescribeRecent(int max = MaxRecent)
    {
        lock (Gate)
        {
            if (Recent.Count == 0)
            {
                return string.Empty;
            }

            var lines = Recent
                .Take(max)
                .Select((run, index) =>
                    $"{index + 1}. {run.FinishedAt.ToLocalTime():HH:mm} {run.Kind}「{run.Name}」{(run.Success ? "完成" : "未完成")}"
                    + $"（{run.ElapsedSeconds} 秒）{(string.IsNullOrWhiteSpace(run.Summary) ? "" : "：" + run.Summary)}");
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// 清空历史，仅测试使用。
    /// </summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _activeScript = null;
            Recent.Clear();
        }
    }

    /// <summary>
    /// 压入一条历史并裁剪长度。
    /// </summary>
    private static void Push(RunRecord record)
    {
        Recent.AddFirst(record);
        while (Recent.Count > MaxRecent)
        {
            Recent.RemoveLast();
        }
    }

    /// <summary>
    /// 正在运行的脚本。
    /// </summary>
    /// <param name="Name">脚本名。</param>
    /// <param name="StartedAt">开始时刻（UTC）。</param>
    private sealed record ActiveScript(string Name, DateTime StartedAt);

    /// <summary>
    /// 一条已结束的运行记录。
    /// </summary>
    private sealed record RunRecord(
        string Name,
        string Kind,
        bool Success,
        string Summary,
        int ElapsedSeconds,
        DateTime FinishedAt);
}
