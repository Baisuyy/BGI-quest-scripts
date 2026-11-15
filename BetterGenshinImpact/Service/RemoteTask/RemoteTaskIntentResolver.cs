using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Ai;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 把聊天里的一句话变成指令：先走本地规则，规则不认再用配置里的大模型兜底。
/// <para>
/// 规则快且免费，覆盖「一条龙」「截图」这类固定说法；大模型负责「帮我把树脂清一下，别动委托」这种自由表达。
/// 模型不可用、超时或输出不合规时一律退回规则结果，不让聊天链路依赖大模型可用性。
/// </para>
/// </summary>
public sealed class RemoteTaskIntentResolver(
    AiKernel kernel,
    RemoteTaskCommandHandler handler,
    ILogger<RemoteTaskIntentResolver> logger)
{
    /// <summary>
    /// 单次解析的超时，超过就走规则结果，避免聊天里等太久。
    /// </summary>
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 解析一条消息。
    /// </summary>
    /// <param name="text">已去掉 @ 与首尾空白的正文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>指令；完全无法理解时 <see cref="RemoteTaskCommandKind.Unknown"/>。</returns>
    public async Task<RemoteTaskCommand> ResolveAsync(string text, CancellationToken cancellationToken)
    {
        var quick = RemoteTaskCommandParser.Parse(text, handler.Tasks);
        if (quick.Kind != RemoteTaskCommandKind.Unknown)
        {
            return quick;
        }

        if (!kernel.Available)
        {
            return quick;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResolveTimeout);
        try
        {
            var answer = await kernel.AskAsync("远程任务解析", text, BuildPrompt(), timeout.Token).ConfigureAwait(false);
            var command = ParseAnswer(answer);
            if (command is not null)
            {
                logger.LogInformation("大模型解析「{Text}」→ {Kind} {TaskId}", text, command.Kind, command.TaskId);
                return command;
            }

            logger.LogDebug("大模型返回无法解析的内容：{Answer}", answer);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("大模型解析超时，回退规则结果");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "大模型解析失败，回退规则结果");
        }

        return quick;
    }

    /// <summary>
    /// 构造解析提示词，任务清单每次都现取，保证和客户端注册的任务一致。
    /// </summary>
    private string BuildPrompt()
    {
        var taskLines = string.Join("\n", handler.Tasks.Select(t => $"- {t.TaskId} = {t.DisplayName}"));
        // 用 $$ 前缀：raw string 里单大括号是字面量，插值要写双大括号。
        return $$"""
                你是 BetterGI 的指令解析器。把用户这一句话变成一条指令，只输出一个 JSON 对象，不要解释、不要代码块。

                可下发的任务：
                {{taskLines}}

                输出三选一：
                1. 要干活：{"intent":"run","task_id":"上面的某个标识","say":"一句中文确认"}
                2. 要看状态/截图/停止/帮助/任务清单：{"intent":"control","action":"status|screenshot|stop|help|list","say":"一句中文"}
                3. 与下发任务无关，或信息不够：{"intent":"chat","say":"用一两句话回答或追问"}

                规则：
                - task_id 必须来自上面的清单，不要编造。
                - 用户说「重新执行」「再来一次」时，如果没法确定是哪个任务，用 chat 追问，不要猜。
                - say 要说人话，不超过 40 字，不要复述 JSON。
                """;
    }

    /// <summary>
    /// 把模型输出解析成指令；格式不合规时返回 null。
    /// </summary>
    private RemoteTaskCommand? ParseAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        var json = ExtractJson(answer);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var intent = GetString(root, "intent");
            var say = GetString(root, "say");
            return intent switch
            {
                "run" => ResolveRun(GetString(root, "task_id"), say),
                "control" => ResolveControl(GetString(root, "action"), say),
                "chat" => new RemoteTaskCommand(RemoteTaskCommandKind.Chat, Say: say),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 校验模型给出的任务标识一定在注册表里，避免把编造的标识传给状态机。
    /// </summary>
    private RemoteTaskCommand? ResolveRun(string? taskId, string? say)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        var matched = handler.Tasks.FirstOrDefault(t =>
            string.Equals(t.TaskId, taskId.Trim(), StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(matched.TaskId)
            ? null
            : new RemoteTaskCommand(RemoteTaskCommandKind.RunTask, matched.TaskId, matched.DisplayName, say);
    }

    /// <summary>
    /// 映射控制类动作。
    /// </summary>
    private static RemoteTaskCommand? ResolveControl(string? action, string? say)
    {
        return action?.Trim().ToLowerInvariant() switch
        {
            "status" => new RemoteTaskCommand(RemoteTaskCommandKind.Status, Say: say),
            "screenshot" => new RemoteTaskCommand(RemoteTaskCommandKind.Screenshot, Say: say),
            "stop" => new RemoteTaskCommand(RemoteTaskCommandKind.Stop, Say: say),
            "help" => new RemoteTaskCommand(RemoteTaskCommandKind.Help, Say: say),
            "list" => new RemoteTaskCommand(RemoteTaskCommandKind.TaskList, Say: say),
            _ => null
        };
    }

    /// <summary>
    /// 从可能带说明文字或代码块的回答里截出第一个 JSON 对象。
    /// </summary>
    private static string? ExtractJson(string answer)
    {
        var start = answer.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var end = answer.LastIndexOf('}');
        return end <= start ? null : answer[start..(end + 1)];
    }

    /// <summary>
    /// 读字符串字段，缺失时返回 null。
    /// </summary>
    private static string? GetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
