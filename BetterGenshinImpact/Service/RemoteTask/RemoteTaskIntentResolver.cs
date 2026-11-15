using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Ai;
using BetterGenshinImpact.Service.Interface;
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
    IConfigService configService,
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

        // 以前判断对过一次的说法直接复用：既省一次模型调用，也不受模型换代影响
        if (MaterialAliasStore.TryGet(text) is { } known && PathingRouteLibrary.Find(known).Count > 0)
        {
            logger.LogInformation("素材对照命中「{Text}」→ {Material}", text, known);
            return new RemoteTaskCommand(RemoteTaskCommandKind.Path, Args: known);
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
        var persona = PersonaPresets.Resolve(configService.Get().RemoteTaskConfig);

        // 把盘上真正有的素材喂给模型：它可以不知道「米提亚」是谁，
        // 但必须从这份清单里挑，否则给出来的名字本地一条路线都匹配不上。
        var materials = PathingRouteLibrary.Materials();
        var materialHint = materials.Count == 0
            ? "本地还没有任何采集路线，涉及采集时只能说明需要先下载路线。"
            : $"本地已有路线的采集物（共 {materials.Count} 种，只能从这里挑）：{string.Join("、", materials.Take(80))}"
              + (materials.Count > 80 ? "……" : string.Empty);

        // 用 $$ 前缀：raw string 里单大括号是字面量，插值要写双大括号。
        return $$"""
                {{persona}}

                ===== 以下是指令解析规则，优先级高于人设的语气 =====
                你是 BetterGI 的指令解析器。把用户这一句话变成一条指令，只输出一个 JSON 对象，不要解释、不要代码块。

                可下发的任务：
                {{taskLines}}

                {{materialHint}}

                输出四选一：
                1. 要干活：{"intent":"run","task_id":"上面的某个标识","say":"一句中文确认"}
                   兑换码要把码本身带上：{"intent":"run","task_id":"redeem_code","code":"8HZH86K6E2PW","say":"一句中文"}
                   采集素材要把采集物名带上：{"intent":"run","task_id":"auto_pathing","material":"霓裳花","say":"一句中文"}
                2. 这些日常操作不用跑任务，直接说要做哪个：
                   {"intent":"control","action":"status|screenshot|stop|help|list|resin|inventory|wish|close_game","say":"一句中文"}
                3. 清单里没有对应任务，但可以在游戏界面上点出来（查看背包、翻菜单、点某个按钮）：
                   {"intent":"pilot","goal":"一句话说清要做什么","say":"一句中文"}
                4. 与游戏操作无关，或信息不够：{"intent":"chat","say":"用一两句话回答或追问"}

                规则：
                - task_id 必须来自上面的清单，不要编造。
                - 用户说「重新执行」「再来一次」时，如果没法确定是哪个任务，用 chat 追问，不要猜。
                - 只有确实是「去游戏里点几下就能完成」的事才用 pilot；问知识、问你在干嘛、闲聊一律 chat。
                - pilot 的 goal 要写成能直接执行的祈使句，例如「打开背包看有多少摩拉」。
                - 用户单独发来一串字母数字（例如 8HZH86K6E2PW），就是兑换码，用 run + redeem_code，并把码放进 code。
                - 要采素材（「采清心」「胡桃突破材料」「米提亚材料」「帮我弄点霓裳花」）用 run + auto_pathing。
                  material 填的是**采集物名，不是角色名**，并且必须出现在上面的本地清单里：
                  胡桃→霓裳花、雷电将军→天云草实、钟离→琉璃袋。
                - 用户说的是某个角色名，你要自己判断这个角色的突破材料是什么，再在清单里选对应的采集物。
                  新角色你不认识时不要硬编：从清单里挑 1–3 个最可能的候选，用 chat 问一句「是这三个里的哪个」，
                  并把候选名字写全。
                - 问「还有多少树脂/体力」用 control + resin；「清体力」「把体力刷完」才用 run + resin_plan。
                - say 用上面人设的语气，说人话，不超过 40 字，不要复述 JSON。
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
                "run" => ResolveRun(GetString(root, "task_id"), GetString(root, "code"), GetString(root, "material"), say),
                "control" => ResolveControl(GetString(root, "action"), say),
                "pilot" => ResolvePilot(GetString(root, "goal"), say),
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
    private RemoteTaskCommand? ResolveRun(string? taskId, string? code, string? material, string? say)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        var matched = handler.Tasks.FirstOrDefault(t =>
            string.Equals(t.TaskId, taskId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(matched.TaskId))
        {
            return null;
        }

        // 兑换码是唯一需要模型回填内容的任务：码本身不在任务清单里，只能从这句话里带出来
        var args = string.Equals(matched.TaskId, "redeem_code", StringComparison.OrdinalIgnoreCase)
            ? code?.Trim()
            : material?.Trim();
        return new RemoteTaskCommand(RemoteTaskCommandKind.RunTask, matched.TaskId, matched.DisplayName, say, args);
    }

    /// <summary>
    /// 模型判断「这事得在界面上点」，转到自由操作。goal 为空时退回聊天，避免空目标乱点。
    /// </summary>
    private static RemoteTaskCommand? ResolvePilot(string? goal, string? say)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return string.IsNullOrWhiteSpace(say)
                ? null
                : new RemoteTaskCommand(RemoteTaskCommandKind.Chat, Say: say);
        }

        return new RemoteTaskCommand(RemoteTaskCommandKind.Pilot, Say: say, Args: goal.Trim());
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
            "resin" or "stamina" => new RemoteTaskCommand(RemoteTaskCommandKind.Resin, Say: say),
            "inventory" or "bag" => new RemoteTaskCommand(RemoteTaskCommandKind.Inventory, Say: say),
            "wish" or "gacha" => new RemoteTaskCommand(RemoteTaskCommandKind.Wish, Say: say),
            "close_game" or "closegame" => new RemoteTaskCommand(RemoteTaskCommandKind.CloseGame, Say: say),
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
