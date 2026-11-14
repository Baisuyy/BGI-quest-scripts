using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Core.Script.Project;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.SoloTask;
using BetterGenshinImpact.GameTask.SoloTask.Execution;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>
/// 任务下发桥：把 BGI 自己的独立任务与脚本仓库包成工具，交给内置模型调度。
/// <para>
/// 模型不直接操作游戏——它只说「跑哪个任务、带什么参数」，真正的执行走
/// <see cref="SoloTaskExecutionService"/> 与 <see cref="TaskRunner"/>，
/// 也就是界面上点「开始」时走的同一条路：同样加任务锁、同样起截图器、同样能被停止按钮打断。
/// </para>
/// <para>
/// 这样做的意义是「复用」而不是「另起一套」：任务状态、日志、通知、防重复启动全部沿用既有实现，
/// AI 只是把手动点按钮这一步换成了自然语言。
/// </para>
/// </summary>
public sealed class AgentTasks(SoloTaskRegistry registry, SoloTaskExecutionService execution, IScriptService scripts)
{
    private static readonly ILogger Logger = App.GetLogger<AgentTasks>();

    /// <summary>默认等待时长上限，避免模型一次调用把整个任务预算耗在一次长任务上。</summary>
    private const int MaxWaitSeconds = 600;

    /// <summary>默认参数模板的展示上限，超出部分截断，避免把上下文塞满。</summary>
    private const int MaxParamChars = 3_000;

    /// <summary>一次列出的脚本条数上限。</summary>
    private const int MaxScripts = 40;

    /// <summary>按运行标识记录开始时刻，用于算「已运行多久」。</summary>
    private readonly Dictionary<Guid, DateTime> _startedAt = [];

    /// <summary>模型可用的下发动作。</summary>
    public static IReadOnlyList<LlmTool> Tools { get; } =
    [
        new("list_tasks", "列出 BGI 内置的可下发任务（标识、名称、分组、是否有参数）。下任务前先看这个，不要凭记忆猜标识。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["group"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "只看某一组，如「日常奖励」「树脂与战斗」；省略表示全部",
                    },
                },
            }),
        new("describe_task", "查看某个任务的参数模板。返回该任务参数的默认 JSON，params 只需填要改的字段，其余保持默认。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["task_id"] = new JsonObject { ["type"] = "string", ["description"] = "任务标识，如 auto_domain" },
                },
                ["required"] = new JsonArray("task_id"),
            }),
        new("run_task", "下发一个 BGI 内置任务并开始执行。这是「让软件自己跑」的正路，不要用键鼠模拟去替代。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["task_id"] = new JsonObject { ["type"] = "string", ["description"] = "任务标识，来自 list_tasks" },
                    ["params"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "参数覆盖，snake_case，与默认值深合并；可省略表示全用默认",
                    },
                    ["wait"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "是否等任务结束再返回。短任务可用 true；刷本、讨伐这类长任务建议 false，之后用 task_status 查",
                    },
                    ["wait_seconds"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "wait 为 true 时的最长等待秒数，默认 120，最大 600",
                    },
                },
                ["required"] = new JsonArray("task_id"),
            }),
        new("task_status", "查看当前有没有任务在跑、跑了多久、上一次失败的原因。",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        new("stop_task", "停止当前正在运行的任务或脚本，等同于界面上的停止按钮。",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        new("list_scripts", "列出用户脚本目录（User/JsScript）里可运行的脚本，用于下发第三方任务脚本。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["keyword"] = new JsonObject { ["type"] = "string", ["description"] = "按名称或目录名过滤，可省略" },
                },
            }),
        new("run_script", "运行一个用户脚本（JS 任务脚本）。脚本通常跑很久，默认只等几秒确认启动成功就返回。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "脚本名或目录名，来自 list_scripts，支持唯一片段匹配",
                    },
                    ["wait_seconds"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "启动确认的等待秒数，默认 5，最大 120",
                    },
                },
                ["required"] = new JsonArray("name"),
            }),
    ];

    /// <summary>工具名是否由本类处理，供上层分派。</summary>
    public static bool Handles(string name) => name is "list_tasks" or "describe_task" or "run_task"
        or "task_status" or "stop_task" or "list_scripts" or "run_script";

    /// <summary>在软件未完全初始化时尝试构造；缺少依赖时返回 null，由调用方退回纯文件助手。</summary>
    public static AgentTasks? TryCreate()
    {
        try
        {
            var registry = App.GetService<SoloTaskRegistry>();
            var execution = App.GetService<SoloTaskExecutionService>();
            var scriptService = App.GetService<IScriptService>();
            return registry is null || execution is null || scriptService is null
                ? null
                : new AgentTasks(registry, execution, scriptService);
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "任务下发桥不可用");
            return null;
        }
    }

    /// <summary>执行一次工具调用，返回给模型看的文本。</summary>
    public async Task<string> ExecuteAsync(string name, JsonObject args, CancellationToken ct)
    {
        try
        {
            return name switch
            {
                "list_tasks" => ListTasks(GetString(args, "group")),
                "describe_task" => DescribeTask(GetString(args, "task_id")),
                "run_task" => await RunTaskAsync(args, ct).ConfigureAwait(false),
                "task_status" => Status(),
                "stop_task" => await StopAsync().ConfigureAwait(false),
                "list_scripts" => ListScripts(GetString(args, "keyword")),
                "run_script" => await RunScriptAsync(args, ct).ConfigureAwait(false),
                _ => $"未知动作 {name}",
            };
        }
        catch (KeyNotFoundException e)
        {
            return e.Message;
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "任务下发失败 {Name}", name);
            return "下发失败：" + e.Message;
        }
    }

    /// <summary>把内置任务列成清单，按分组归拢，顺便标出当前是否被占用。</summary>
    private string ListTasks(string? group)
    {
        var items = registry.All
            .Where(d => string.IsNullOrWhiteSpace(group)
                        || d.Group.Contains(group!.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (items.Count == 0)
        {
            return string.IsNullOrWhiteSpace(group)
                ? "没有已注册的内置任务。"
                : $"没有分组名包含「{group}」的任务。现有分组：{string.Join("、", registry.All.Select(d => d.Group).Distinct())}";
        }

        var active = execution.GetStatus();
        var builder = new StringBuilder();
        builder.Append("共 ").Append(items.Count).Append(" 个内置任务");
        if (active.IsActive)
        {
            builder.Append("；当前正在运行 ").Append(registry.GetDisplayName(active.TaskId)).Append("，重复下发会被拒绝");
        }

        builder.AppendLine("。");
        foreach (var byGroup in items.GroupBy(d => d.Group).OrderBy(g => g.Min(d => d.Order)))
        {
            builder.Append("\n【").Append(byGroup.Key).Append("】\n");
            foreach (var task in byGroup.OrderBy(d => d.Order))
            {
                builder.Append("  ").Append(task.TaskId).Append(" — ").Append(task.DisplayName);
                if (task.HasParameters)
                {
                    builder.Append("（有参数，可 describe_task 查看）");
                }

                if (!string.IsNullOrWhiteSpace(task.Description))
                {
                    builder.Append("：").Append(Trim(task.Description, 60));
                }

                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>给出参数模板。默认值序列化后如果太长就截断，并明确告诉模型「只填要改的」。</summary>
    private string DescribeTask(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || !registry.TryGet(taskId.Trim(), out var descriptor))
        {
            return $"没有任务「{taskId}」。先用 list_tasks 看可用标识。";
        }

        var head = $"{descriptor.TaskId}（{descriptor.DisplayName}）· 分组 {descriptor.Group}";
        if (!descriptor.HasParameters)
        {
            return head + "\n该任务没有参数，run_task 直接传 task_id 即可。";
        }

        string template;
        try
        {
            template = descriptor.SerializeParam(descriptor.CreateDefaultParam());
        }
        catch (Exception e)
        {
            return head + "\n参数模板生成失败（" + e.Message + "），可直接省略 params 使用默认值。";
        }

        var pretty = Pretty(template);
        var truncated = pretty.Length > MaxParamChars;
        if (truncated)
        {
            pretty = pretty[..MaxParamChars];
        }

        return head + "\n默认参数（只需填要改的字段，run_task 会与这份深合并）：\n"
               + pretty + (truncated ? "\n…（已截断，只填你确知的字段）" : "");
    }

    /// <summary>下发任务。<paramref name="wait"/> 为 false 时立即返回，让模型继续做别的事。</summary>
    private async Task<string> RunTaskAsync(JsonObject args, CancellationToken ct)
    {
        var taskId = GetString(args, "task_id");
        if (string.IsNullOrWhiteSpace(taskId) || !registry.TryGet(taskId.Trim(), out var descriptor))
        {
            return $"没有任务「{taskId}」。先用 list_tasks 看可用标识。";
        }

        var status = execution.GetStatus();
        if (status.IsActive)
        {
            return $"当前正在运行「{registry.GetDisplayName(status.TaskId)}」，先 task_status 看进度，"
                   + "要换任务得先 stop_task。";
        }

        var runId = Guid.NewGuid();
        var parameters = BuildParameters(descriptor, args["params"] as JsonObject, out var note);
        var response = await execution.StartAsync(new SoloTaskStartRequest
        {
            RunId = runId,
            TaskId = descriptor.TaskId,
            Parameters = parameters,
        }).ConfigureAwait(false);

        if (!response.Accepted)
        {
            return "下发被拒绝：" + (response.ErrorMessage ?? "实例正忙");
        }

        _startedAt[runId] = DateTime.UtcNow;
        var wait = GetBool(args, "wait");
        var head = $"已下发 {descriptor.TaskId}（{descriptor.DisplayName}）" + (note.Length > 0 ? "，" + note : "");
        if (!wait)
        {
            // 配置缺失、路径不可达这类错误在启动后一两秒内就会抛。
            // 如果直接回「已下发」，模型会以为成功，下一轮再查状态才发现失败，白花一步。
            // 这里短暂观察一下，把失败原因当场带回去，让它能立刻改参数或换任务。
            var immediate = await ProbeImmediateFailureAsync(ct).ConfigureAwait(false);
            if (immediate is { Length: > 0 })
            {
                return $"{head}，但启动后立即失败：{immediate}。"
                       + "先看 describe_task 的参数与依赖（例如某类任务要先在界面里建好配置），不要原样重试。";
            }

            return head + "，任务在后台运行。用 task_status 查进度，stop_task 停止。";
        }

        var seconds = Math.Clamp(GetInt(args, "wait_seconds") ?? 120, 5, MaxWaitSeconds);
        var result = await WaitForAsync(runId, TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        return result is null
            ? head + $"，已等 {seconds} 秒仍在运行。用 task_status 继续观察。"
            : head + "，已结束：" + DescribeResult(result);
    }

    /// <summary>
    /// 下发后观察一小段时间，返回「已停止且带错误」的原因；仍在跑或正常结束都返回空。
    /// </summary>
    private async Task<string?> ProbeImmediateFailureAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var status = execution.GetStatus();
        return !status.IsActive && status.ErrorMessage is { Length: > 0 } error ? error : null;
    }

    /// <summary>把模型给的部分参数与任务默认参数深合并，避免它漏字段导致数值被清零。</summary>
    private static JObject? BuildParameters(SoloTaskDescriptor descriptor, JsonObject? patch, out string note)
    {
        note = "";
        if (!descriptor.HasParameters)
        {
            return null;
        }

        JsonObject defaults;
        try
        {
            defaults = JsonNode.Parse(descriptor.SerializeParam(descriptor.CreateDefaultParam())) as JsonObject ?? [];
        }
        catch (Exception e)
        {
            note = "参数模板不可用，使用默认值（" + e.Message + "）";
            return null;
        }

        if (patch is not null)
        {
            MergeInto(defaults, patch);
            note = "参数已合并";
        }

        return JObject.Parse(defaults.ToJsonString());
    }

    /// <summary>递归合并：对象逐键合并，其它类型整体替换。</summary>
    private static void MergeInto(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (value is JsonObject child && target[key] is JsonObject existing)
            {
                MergeInto(existing, child);
                continue;
            }

            target[key] = value?.DeepClone();
        }
    }

    /// <summary>等一次运行结束；超时返回 null，任务继续在后台跑。</summary>
    private async Task<SoloTaskRunResult?> WaitForAsync(Guid runId, TimeSpan timeout, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<SoloTaskRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, SoloTaskRunCompletedEventArgs e)
        {
            if (e.RunId == runId)
            {
                completion.TrySetResult(e.Result);
            }
        }

        execution.RunCompleted += OnCompleted;
        try
        {
            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            return finished == completion.Task ? await completion.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            execution.RunCompleted -= OnCompleted;
        }
    }

    /// <summary>当前状态：空闲、启动中、运行中、正在停止，附已运行时长与上次错误。</summary>
    private string Status()
    {
        var status = execution.GetStatus();
        if (!status.IsActive)
        {
            return status.ErrorMessage is { Length: > 0 }
                ? "空闲。上一次运行失败：" + status.ErrorMessage
                : "空闲，没有任务在跑。";
        }

        var elapsed = status.RunId is { } id && _startedAt.TryGetValue(id, out var started)
            ? $"，已运行 {(DateTime.UtcNow - started).TotalSeconds:F0} 秒"
            : "";
        var state = status.State switch
        {
            SoloTaskRunState.Starting => "正在准备",
            SoloTaskRunState.Running => "运行中",
            SoloTaskRunState.Stopping => "正在停止",
            _ => "空闲",
        };
        return $"{state}：{registry.GetDisplayName(status.TaskId)}（{status.TaskId}）{elapsed}";
    }

    /// <summary>停止当前运行，等同界面上的停止按钮。</summary>
    private async Task<string> StopAsync()
    {
        var status = execution.GetStatus();
        if (status.IsActive && status.RunId is { } runId)
        {
            var response = await execution.StopAsync(new SoloTaskStopRequest { RunId = runId }).ConfigureAwait(false);
            return response.Accepted ? "已请求停止当前独立任务。" : "停止失败：" + response.ErrorMessage;
        }

        // 脚本运行不经过独立任务服务，统一由停止服务广播
        var stop = App.GetService<TaskStopService>();
        if (stop is null)
        {
            return "当前没有可停止的任务。";
        }

        stop.StopAll(TaskStopReason.UserRequested);
        return "没有独立任务在跑，已向脚本运行发送停止信号。";
    }

    /// <summary>列出用户脚本目录里可运行的脚本，读 manifest 拿显示名。</summary>
    private static string ListScripts(string? keyword)
    {
        var root = Global.ScriptPath();
        if (!Directory.Exists(root))
        {
            return "脚本目录不存在：" + root;
        }

        var items = new List<(string Folder, string Name, string Desc)>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest))
            {
                continue;
            }

            var folder = Path.GetFileName(dir);
            try
            {
                using var stream = File.OpenRead(manifest);
                var node = JsonNode.Parse(stream) as JsonObject;
                var name = node?["name"]?.GetValue<string>() ?? folder;
                var desc = node?["description"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(keyword)
                    && !name.Contains(keyword!, StringComparison.OrdinalIgnoreCase)
                    && !folder.Contains(keyword!, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                items.Add((folder, name, desc));
            }
            catch
            {
                // 清单坏了就跳过，不在列表里堆异常信息
            }
        }

        if (items.Count == 0)
        {
            return string.IsNullOrWhiteSpace(keyword)
                ? "脚本目录里没有可运行的脚本。"
                : $"没有名称包含「{keyword}」的脚本。";
        }

        var builder = new StringBuilder("共 ").Append(items.Count).AppendLine(" 个脚本：");
        foreach (var (folder, name, desc) in items.Take(MaxScripts))
        {
            builder.Append("  ").Append(name).Append("  [").Append(folder).Append(']');
            if (!string.IsNullOrWhiteSpace(desc))
            {
                builder.Append("：").Append(Trim(desc, 50));
            }

            builder.AppendLine();
        }

        if (items.Count > MaxScripts)
        {
            builder.Append("…（还有 ").Append(items.Count - MaxScripts).AppendLine(" 个未列出）");
        }

        builder.Append("run_script 的 name 用上面的名称或目录名。");
        return builder.ToString().TrimEnd();
    }

    /// <summary>运行脚本。默认只等几秒确认启动，长脚本让它自己在后台跑。</summary>
    private async Task<string> RunScriptAsync(JsonObject args, CancellationToken ct)
    {
        var name = GetString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return "缺少 name，先 list_scripts。";
        }

        var project = ResolveScript(name.Trim());
        if (project is null)
        {
            return $"找不到脚本「{name}」。先 list_scripts 看可用名称。";
        }

        var status = execution.GetStatus();
        if (status.IsActive)
        {
            return $"当前正在运行「{registry.GetDisplayName(status.TaskId)}」，先 stop_task 再跑脚本。";
        }

        var seconds = Math.Clamp(GetInt(args, "wait_seconds") ?? 5, 2, 120);
        var head = $"已启动脚本 {project.Manifest.Name}";
        var run = new TaskRunner().RunCurrentWithResultAsync(
            (runtime, token) => scripts.RunMulti(runtime, [new ScriptGroupProject(project)], null, token),
            waitForMainUi: true,
            requestToken: CancellationToken.None,
            taskName: project.Manifest.Name);

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(seconds), ct)).ConfigureAwait(false);
        if (finished != run)
        {
            return head + $"，仍在运行（已确认启动 {seconds} 秒）。task_status 可查，stop_task 可停。";
        }

        var result = await run.ConfigureAwait(false);
        return head + "，" + DescribeResult(result);
    }

    /// <summary>按显示名、目录名或唯一片段定位脚本。</summary>
    private static ScriptProject? ResolveScript(string name)
    {
        var root = Global.ScriptPath();
        if (!Directory.Exists(root))
        {
            return null;
        }

        var folders = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => f!)
            .ToList();

        var exact = folders.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        var candidate = exact ?? folders.FirstOrDefault(f => f.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            // 目录名对不上时，再按 manifest 里的显示名找一遍
            candidate = folders.FirstOrDefault(f =>
            {
                try
                {
                    using var stream = File.OpenRead(Path.Combine(root, f, "manifest.json"));
                    var node = JsonNode.Parse(stream) as JsonObject;
                    return node?["name"]?.GetValue<string>() is { } display
                           && display.Contains(name, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
        }

        if (candidate is null)
        {
            return null;
        }

        try
        {
            return new ScriptProject(candidate);
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "脚本 {Folder} 载入失败", candidate);
            return null;
        }
    }

    /// <summary>把运行结果压成一句人话，模型和用户都能看懂。</summary>
    private string DescribeResult(SoloTaskRunResult result)
    {
        var outcome = result.Outcome switch
        {
            SoloTaskRunOutcome.Completed => "完成",
            SoloTaskRunOutcome.Cancelled => "已取消",
            SoloTaskRunOutcome.Rejected => "被拒绝",
            SoloTaskRunOutcome.Faulted => "异常",
            _ => result.Outcome.ToString(),
        };
        var message = result.ErrorMessage ?? result.TaskResult?.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return outcome;
        }

        // 失败原因里常带换行，压成一行以免撑破日志块
        return outcome + "：" + message.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string Pretty(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) ?? json;
        }
        catch
        {
            return json;
        }
    }

    private static string Trim(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private static string? GetString(JsonObject args, string key)
        => args.TryGetPropertyValue(key, out var node) && node is JsonValue value
           && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static int? GetInt(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text)
               && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool GetBool(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) && parsed;
    }
}
