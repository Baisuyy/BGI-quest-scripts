using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>一条决策审计记录，供设置页和日志回看。</summary>
public sealed record AiAuditEntry(
    DateTime Time,
    string Scene,
    string Request,
    string Reply,
    int LatencyMs,
    bool Ok,
    string? Error = null);

/// <summary>
/// 内置大模型内核：全进程唯一的大模型出口。
/// <para>
/// 职责只有四件事：校验配置、控预算、防雪崩、留痕迹。
/// 真正「做什么」由调用方决定——跑图卡死救援走 <see cref="AiOperator"/>，脚本走 <c>ai.Chat()</c>，
/// 两者共用这里的额度与密钥，避免出现两套配置打架。
/// </para>
/// </summary>
public sealed class AiKernel(AllConfig config, ILogger<AiKernel> logger)
{
    /// <summary>连续失败多少次后进入冷却。</summary>
    private const int FailureThreshold = 3;

    /// <summary>冷却时长。冷却期内所有调用方直接走降级，不再发请求。</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    private readonly LlmClient _client = new();
    private readonly object _gate = new();
    private readonly Queue<DateTime> _hourWindow = new();
    private readonly LinkedList<AiAuditEntry> _audits = new();
    private int _runCalls;
    private int _consecutiveFailures;
    private DateTime _cooldownUntil = DateTime.MinValue;

    /// <summary>当前配置。</summary>
    public AiConfig Settings => config.AiConfig;

    /// <summary>总开关是否打开。</summary>
    public bool Enabled => Settings.Enabled;

    /// <summary>当前模型名，仅供展示与日志。</summary>
    public string Model => Settings.ModelName;

    /// <summary>当前服务地址，仅供展示与日志。</summary>
    public string Endpoint => Settings.Endpoint;

    /// <summary>本次运行已发起的请求数。</summary>
    public int Calls
    {
        get
        {
            lock (_gate)
            {
                return _runCalls;
            }
        }
    }

    /// <summary>本次运行允许的最大请求数。</summary>
    public int MaxCalls => Settings.MaxCallsPerRun;

    /// <summary>是否允许附带截图。</summary>
    public bool VisionEnabled => Settings.VisionMode;

    /// <summary>最近若干条决策记录（新在前）。</summary>
    public IReadOnlyList<AiAuditEntry> RecentAudits
    {
        get
        {
            lock (_gate)
            {
                return _audits.ToArray();
            }
        }
    }

    /// <summary>
    /// 现在能不能发请求。返回 null 表示可以，否则是给用户看的原因。
    /// </summary>
    public string? UnavailableReason
    {
        get
        {
            if (!Settings.Enabled)
            {
                return "内置 AI 未启用";
            }

            if (string.IsNullOrWhiteSpace(Settings.ModelName))
            {
                return "未配置模型名";
            }

            lock (_gate)
            {
                if (DateTime.UtcNow < _cooldownUntil)
                {
                    return $"连续失败已冷却至 {_cooldownUntil.ToLocalTime():HH:mm:ss}";
                }

                PruneHourWindow();
                if (_hourWindow.Count >= Settings.MaxCallsPerHour)
                {
                    return $"已达每小时上限 {Settings.MaxCallsPerHour} 次";
                }

                if (_runCalls >= Settings.MaxCallsPerRun)
                {
                    return $"已达本次运行上限 {Settings.MaxCallsPerRun} 次";
                }
            }

            return null;
        }
    }

    /// <summary>现在是否可用。</summary>
    public bool Available => UnavailableReason == null;

    /// <summary>
    /// 发一轮对话，带预算、超时与熔断。
    /// </summary>
    /// <param name="scene">场景名，写进日志与审计，如 <c>卡死救援</c></param>
    /// <param name="conversation">对话内容</param>
    /// <param name="tools">可用工具，纯文本对话传 null</param>
    /// <param name="systemPrompt">系统提示</param>
    /// <param name="ct">取消令牌</param>
    public async Task<LlmReply> SendAsync(string scene, LlmConversation conversation,
        IReadOnlyList<LlmTool>? tools, string systemPrompt, CancellationToken ct)
    {
        var reason = UnavailableReason;
        if (reason != null)
        {
            throw new AiUnavailableException(reason);
        }

        var requestPreview = SummarizeRequest(conversation);
        var timeout = Math.Clamp(Settings.TimeoutSeconds, 5, 300);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            ReserveCall();
            var reply = await _client.SendAsync(Settings, systemPrompt, conversation, tools, cts.Token)
                .ConfigureAwait(false);
            stopwatch.Stop();
            MarkSuccess();
            Record(new AiAuditEntry(DateTime.Now, scene, requestPreview, SummarizeReply(reply), (int)stopwatch.ElapsedMilliseconds, true));
            return reply;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            MarkFailure();
            var message = $"大模型请求超时（{timeout} 秒）";
            Record(new AiAuditEntry(DateTime.Now, scene, requestPreview, "", (int)stopwatch.ElapsedMilliseconds, false, message));
            throw new AiUnavailableException(message);
        }
        catch (Exception e) when (e is not AiUnavailableException)
        {
            stopwatch.Stop();
            MarkFailure();
            Record(new AiAuditEntry(DateTime.Now, scene, requestPreview, "", (int)stopwatch.ElapsedMilliseconds, false, e.Message));
            throw new AiUnavailableException($"大模型调用失败：{e.Message}");
        }
    }

    /// <summary>
    /// 纯文本问答，给脚本与简单场景用。
    /// </summary>
    public async Task<string> AskAsync(string scene, string userContent, string? systemPrompt,
        CancellationToken ct)
    {
        var conversation = new LlmConversation();
        conversation.AddUser(userContent);
        var reply = await SendAsync(scene, conversation, null, systemPrompt ?? "", ct).ConfigureAwait(false);
        return reply.Text;
    }

    /// <summary>
    /// 连通性测试：不占本次运行额度，也不参与熔断，只验证地址、密钥、模型名是否可用。
    /// </summary>
    public async Task<string> TestConnectionAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Settings.ModelName))
        {
            throw new AiUnavailableException("未配置模型名");
        }

        var conversation = new LlmConversation();
        conversation.AddUser("只回复两个字：可用", null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Settings.TimeoutSeconds, 5, 120)));

        var started = DateTime.UtcNow;
        try
        {
            var reply = await _client.SendAsync(Settings, "你是连通性测试端点，只回一句话。", conversation, null, cts.Token)
                .ConfigureAwait(false);
            var text = string.IsNullOrWhiteSpace(reply.Text) ? "（模型返回了空内容）" : reply.Text;
            AiLog.Once("连通性测试", (int)(DateTime.UtcNow - started).TotalMilliseconds, text);
            return text;
        }
        catch (Exception e)
        {
            AiLog.Fail("连通性测试", e.Message);
            throw;
        }
    }

    /// <summary>清空审计记录。</summary>
    public void ClearAudits()
    {
        lock (_gate)
        {
            _audits.Clear();
        }
    }

    private void ReserveCall()
    {
        lock (_gate)
        {
            PruneHourWindow();
            _hourWindow.Enqueue(DateTime.UtcNow);
            _runCalls++;
        }
    }

    private void PruneHourWindow()
    {
        var threshold = DateTime.UtcNow.AddHours(-1);
        while (_hourWindow.Count > 0 && _hourWindow.Peek() < threshold)
        {
            _hourWindow.Dequeue();
        }
    }

    private void MarkSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
        }
    }

    private void MarkFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= FailureThreshold)
            {
                _cooldownUntil = DateTime.UtcNow + Cooldown;
                _consecutiveFailures = 0;
                logger.LogWarning("内置 AI 连续失败 {Count} 次，冷却 {Seconds} 秒", FailureThreshold, Cooldown.TotalSeconds);
            }
        }
    }

    private void Record(AiAuditEntry entry)
    {
        lock (_gate)
        {
            _audits.AddFirst(entry);
            while (_audits.Count > 100)
            {
                _audits.RemoveLast();
            }
        }

        if (!Settings.LogDecisions)
        {
            return;
        }

        if (entry.Ok)
        {
            AiLog.Once(entry.Scene, entry.LatencyMs, entry.Reply);
        }
        else
        {
            AiLog.Fail(entry.Scene, entry.Error ?? "");
        }
    }

    private static string SummarizeRequest(LlmConversation conversation)
    {
        var lastUser = conversation.Turns.OfType<LlmUserTurn>().LastOrDefault();
        return lastUser == null ? "" : Truncate(lastUser.Text.ReplaceLineEndings(" "), 200);
    }

    private static string SummarizeReply(LlmReply reply)
    {
        var action = reply.WantsTool
            ? "调用 " + string.Join(",", reply.Calls.Select(c => c.Name))
            : "";
        var text = Truncate(reply.Text.ReplaceLineEndings(" "), 160);
        return string.IsNullOrEmpty(action) ? text : $"{action}｜{text}";
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>内置 AI 不可用（未启用、超预算、冷却中、请求失败）。调用方捕获后退回规则逻辑。</summary>
public sealed class AiUnavailableException(string message) : Exception(message);
