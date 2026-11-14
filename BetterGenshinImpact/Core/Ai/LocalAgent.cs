using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>一次本地任务的执行结果。</summary>
public sealed record AgentRunResult(bool Success, string Summary, int Steps, int Calls);

/// <summary>
/// 本地助手：让内置模型在软件目录（沙箱）里读写文件、运行程序。
/// <para>
/// 权限模型只有两条：沙箱内按配置放行，沙箱外一律弹窗申请。
/// 模型每次动手前都要过 <see cref="AuthorizeAsync"/>，被拒绝时拿到的是「用户拒绝」而不是异常，
/// 这样它会自己换方案，而不是把整个任务崩掉。所有放行与拒绝都写进日志块。
/// </para>
/// </summary>
public sealed class LocalAgent(AiKernel kernel, AllConfig config)
{
    private static readonly ILogger Logger = App.GetLogger<LocalAgent>();

    /// <summary>单次读写的文本上限，防止模型把几十兆日志塞进上下文。</summary>
    private const int MaxTextChars = 200_000;

    /// <summary>回给模型的工具结果上限。</summary>
    private const int MaxResultChars = 8_000;

    /// <summary>
    /// 文件类工具。任务下发工具在 <see cref="AgentTasks"/> 里，运行时合并成一个工具集。
    /// </summary>
    private static readonly LlmTool[] FileTools =
    [
        new("list_dir", "列出目录内容。路径相对沙箱根目录；不填表示根目录。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "目录路径，可省略" },
                },
            }),
        new("read_file", "读取文本文件。超过上限会截断。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string" },
                    ["max_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最多读取字符数，默认 20000" },
                },
                ["required"] = new JsonArray("path"),
            }),
        new("write_file", "写入文本文件，自动创建父目录。沙箱外需要用户授权。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string" },
                    ["content"] = new JsonObject { ["type"] = "string" },
                    ["append"] = new JsonObject { ["type"] = "boolean", ["description"] = "true 表示追加，默认覆盖" },
                },
                ["required"] = new JsonArray("path", "content"),
            }),
        new("make_dir", "创建目录，已存在则视为成功。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("path"),
            }),
        new("run", "运行可执行文件或脚本（.exe/.bat/.cmd/.ps1）。沙箱内也需按配置或授权。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["program"] = new JsonObject { ["type"] = "string", ["description"] = "程序或脚本路径" },
                    ["args"] = new JsonObject { ["type"] = "string", ["description"] = "命令行参数，原样传递" },
                    ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "工作目录，默认程序所在目录" },
                    ["timeout_ms"] = new JsonObject { ["type"] = "integer", ["description"] = "超时毫秒，默认 30000，最大 300000" },
                },
                ["required"] = new JsonArray("program"),
            }),
        new("done", "任务结束。无论成功还是放弃都要调用，并说明做了什么。",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["success"] = new JsonObject { ["type"] = "boolean" },
                    ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "一两句话" },
                },
                ["required"] = new JsonArray("success", "summary"),
            }),
    ];

    private AgentSandbox Sandbox => new(config.AiConfig.AgentSandboxRoot);

    /// <summary>
    /// 任务下发桥。软件还没初始化完（或作为纯库调用）时为 null，此时只剩文件能力。
    /// </summary>
    private AgentTasks? _tasks;

    /// <summary>首次使用时解析一次；失败后下次调用再试，避免一次时序问题永久降级。</summary>
    private AgentTasks? Tasks => _tasks ??= AgentTasks.TryCreate();

    /// <summary>路径工具无状态，直接复用。</summary>
    private static readonly AgentPathing Pathing = new();

    /// <summary>本次可用的工具集：文件类 + 任务下发类 + 路径比对类。</summary>
    private IReadOnlyList<LlmTool> BuildTools()
        => [.. FileTools, .. AgentTasks.Tools, .. AgentPathing.Tools];


    /// <summary>
    /// 执行一个自然语言任务。<paramref name="progress"/> 每步回调一次，供界面实时显示。
    /// </summary>
    public async Task<AgentRunResult> RunAsync(string task, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var settings = config.AiConfig;
        if (!settings.AgentEnabled)
        {
            return new AgentRunResult(false, "本地助手未启用", 0, 0);
        }

        var reason = kernel.UnavailableReason;
        if (reason != null)
        {
            return new AgentRunResult(false, reason, 0, 0);
        }

        var sandbox = Sandbox;
        var maxSteps = Math.Clamp(settings.AgentMaxSteps, 1, 30);
        var callsAtStart = kernel.Calls;
        var startedAt = DateTime.UtcNow;
        var blockId = AiLog.BeginAgent(task, sandbox.Root, settings, callsAtStart);
        var step = 0;
        var stopwatch = Stopwatch.StartNew();
        var conversation = new LlmConversation();
        conversation.AddUser(task);

        try
        {
            while (step < maxSteps)
            {
                if (ct.IsCancellationRequested)
                {
                    return Finish(false, "已取消", step, callsAtStart, stopwatch, blockId);
                }

                if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(Math.Clamp(settings.AgentTimeoutSeconds, 10, 1800)))
                {
                    return Finish(false, "任务超时", step, callsAtStart, stopwatch, blockId);
                }

                var reply = await kernel.SendAsync("本地助手", conversation, BuildTools(),
                    BuildSystemPrompt(sandbox, settings), ct).ConfigureAwait(false);

                if (!reply.WantsTool)
                {
                    var text = string.IsNullOrWhiteSpace(reply.Text) ? "（模型没有给出内容）" : reply.Text.Trim();
                    AiLog.Say(blockId, text);
                    progress?.Report("模型：" + text);
                    return Finish(true, text, step, callsAtStart, stopwatch, blockId);
                }

                conversation.AddAssistant(reply);
                foreach (var call in reply.Calls)
                {
                    step++;
                    var outcome = await ExecuteAsync(call, sandbox, blockId, step, maxSteps, progress, ct)
                        .ConfigureAwait(false);
                    conversation.AddToolResult(call.Id, outcome.Result);
                    AiLog.AgentStep(blockId, step, maxSteps, outcome.Describe, outcome.Result);
                    progress?.Report($"[{step}/{maxSteps}] {outcome.Describe}");

                    if (outcome.Finished)
                    {
                        return Finish(outcome.Success, outcome.Summary, step, callsAtStart, stopwatch, blockId);
                    }

                    if (step >= maxSteps)
                    {
                        break;
                    }
                }
            }

            return Finish(false, "达到步数上限", step, callsAtStart, stopwatch, blockId);
        }
        catch (AiUnavailableException e)
        {
            AiLog.Fail("本地助手", e.Message);
            return Finish(false, e.Message, step, callsAtStart, stopwatch, blockId);
        }
        catch (OperationCanceledException)
        {
            return Finish(false, "已取消", step, callsAtStart, stopwatch, blockId);
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "本地助手执行失败");
            return Finish(false, e.Message, step, callsAtStart, stopwatch, blockId);
        }
    }

    private AgentRunResult Finish(bool success, string summary, int steps, int callsAtStart,
        Stopwatch stopwatch, int blockId)
    {
        stopwatch.Stop();
        AiLog.AgentEnd(blockId, success, summary, steps, kernel.Calls - callsAtStart, stopwatch.Elapsed);
        return new AgentRunResult(success, summary, steps, kernel.Calls - callsAtStart);
    }

    private static string BuildSystemPrompt(AgentSandbox sandbox, AiConfig settings)
    {
        var rootEntries = SafeEntries(sandbox.Root);
        var prompt = $"""
                       你是运行在 BetterGI 客户端里的本地助手。你有两类能力：
                       ① 在沙箱里读写文件、运行程序；② 把游戏内的活儿交给 BGI 自己去做（下发内置任务、跑用户脚本）。

                       沙箱根目录：{sandbox.Root}
                       根目录内容：{rootEntries}

                       规则：
                       1. 相对路径一律相对沙箱根目录解析，不要拼盘符。
                       2. 沙箱内的读取可以直接做；写入与运行是否需要确认由配置决定，配置拒绝了就换路径，不要重复尝试同一操作。
                       3. 沙箱外的任何路径都会弹窗向用户申请，被拒绝时换方案或直接 done(success=false)，不要反复申请。
                       4. 删除文件没有专用工具，确需删除时用 run 调用系统命令，并在参数里写清楚要删什么。
                       5. 不要修改 BetterGI.exe、Assets、dicts 之外你不了解的东西；改动配置前先读一遍原文件。
                       6. 用户要求「刷本、清体力、领日常、讨伐、钓鱼、伐木、跑脚本」这类游戏内操作时，用 list_tasks / run_task / run_script 下发，
                          不要用键鼠模拟或自己写程序去替代——那些能力 BGI 本身就有，而且带任务锁与停止入口。
                       7. 下任务前先 list_tasks 确认标识；参数不确定时先 describe_task，params 只填要改的字段（snake_case），其余保持默认。
                           有些任务依赖用户在界面里建好的配置（如「一条龙」需要 OneDragon 配置）。返回「找不到配置」时不要原样重试，
                           先说明缺什么、让用户去对应页面建一份，或改下不需要配置的任务。
                       7.1 run_task 返回「启动后立即失败」时，按给出的原因改参数或换任务，最多再试一次；连续失败就直接 done 并说明缺什么。
                       8. 长任务（刷本、讨伐、脚本）不要 wait，让它在后台跑，再用 task_status 回报；短任务才值得等。
                       9. 同一时刻只能跑一个任务，被拒绝时先 task_status，必要时 stop_task。
                       10. 用户问「刚录的路线怎么样、和成品比一下」时，用 list_paths 找到文件再 compare_path 比对，不要自己算。
                       11. 完成任务或确认做不到时调用 done。
                       12. 全程用中文，说明简短；不要编造任务标识或脚本名。
                       """;

        if (settings.AgentAllowWrite)
        {
            prompt += "\n当前配置：沙箱内写入已放行，无需逐次确认。";
        }

        if (settings.AgentAllowRun)
        {
            prompt += "\n当前配置：沙箱内运行程序已放行，无需逐次确认。";
        }

        if (!string.IsNullOrWhiteSpace(settings.SystemPromptExtra))
        {
            prompt += "\n额外约束：" + settings.SystemPromptExtra.Trim();
        }

        return prompt;
    }

    private static string SafeEntries(string dir)
    {
        try
        {
            var items = Directory.EnumerateFileSystemEntries(dir)
                .Take(40)
                .Select(p => Path.GetFileName(p) + (Directory.Exists(p) ? "/" : ""))
                .ToArray();
            return items.Length == 0 ? "（空）" : string.Join("、", items);
        }
        catch
        {
            return "（无法读取）";
        }
    }

    private sealed record ToolOutcome(string Describe, string Result, bool Finished = false,
        bool Success = false, string Summary = "");

    private async Task<ToolOutcome> ExecuteAsync(LlmToolCall call, AgentSandbox sandbox, int blockId,
        int step, int maxSteps, IProgress<string>? progress, CancellationToken ct)
    {
        var args = ParseArguments(call.ArgumentsJson);
        try
        {
            // 路径比对只读 User 目录，不碰游戏，因此不需要授权弹窗
            if (AgentPathing.Handles(call.Name))
            {
                var report = await Pathing.ExecuteAsync(call.Name, args, ct).ConfigureAwait(false);
                return new ToolOutcome(call.Name, report);
            }

            // 游戏内任务与脚本走任务桥：它复用 BGI 的任务锁、截图器与停止按钮，不另起一套执行
            if (AgentTasks.Handles(call.Name))
            {
                if (Tasks is not { } tasks)
                {
                    return new ToolOutcome(call.Name, "任务下发不可用：软件尚未初始化完成，请改用文件类操作或稍后再试。");
                }

                var text = await tasks.ExecuteAsync(call.Name, args, ct).ConfigureAwait(false);
                return new ToolOutcome(DescribeTaskCall(call.Name, args), text);
            }

            switch (call.Name)
            {
                case "list_dir":
                    return await ListDirAsync(args, sandbox, ct).ConfigureAwait(false);
                case "read_file":
                    return await ReadFileAsync(args, sandbox, ct).ConfigureAwait(false);
                case "write_file":
                    return await WriteFileAsync(args, sandbox, ct).ConfigureAwait(false);
                case "make_dir":
                    return await MakeDirAsync(args, sandbox, ct).ConfigureAwait(false);
                case "run":
                    return await RunProcessAsync(args, sandbox, ct).ConfigureAwait(false);
                case "done":
                    return new ToolOutcome("done",
                        GetString(args, "summary") ?? "已结束",
                        true,
                        GetBool(args, "success"),
                        GetString(args, "summary") ?? "");
                default:
                    return new ToolOutcome(call.Name, $"未知工具：{call.Name}");
            }
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "本地助手工具 {Name} 执行失败", call.Name);
            return new ToolOutcome(call.Name, "执行失败：" + e.Message);
        }
    }

    /// <summary>任务类调用的一行描述，用于日志与界面进度。</summary>
    private static string DescribeTaskCall(string name, JsonObject args)
    {
        var taskId = GetString(args, "task_id");
        var script = GetString(args, "name");
        return name switch
        {
            "list_tasks" => "list_tasks" + (GetString(args, "group") is { } group ? " " + group : ""),
            "describe_task" => "describe_task " + (taskId ?? ""),
            "run_task" => "run_task " + (taskId ?? ""),
            "task_status" => "task_status",
            "stop_task" => "stop_task",
            "list_scripts" => "list_scripts" + (GetString(args, "keyword") is { } keyword ? " " + keyword : ""),
            "run_script" => "run_script " + (script ?? ""),
            _ => name,
        };
    }

    private async Task<ToolOutcome> ListDirAsync(JsonObject args, AgentSandbox sandbox, CancellationToken ct)
    {
        var target = sandbox.Resolve(GetString(args, "path"));
        if (!await EnsureAccessAsync("list_dir", target, "列出目录", target.InsideSandbox, ct).ConfigureAwait(false))
        {
            return Denied("list_dir", target);
        }

        if (!Directory.Exists(target.FullPath))
        {
            return new ToolOutcome($"list_dir {sandbox.Display(target.FullPath)}", "目录不存在");
        }

        var lines = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(target.FullPath).Take(200))
        {
            var isDir = Directory.Exists(entry);
            var name = Path.GetFileName(entry) + (isDir ? "/" : "");
            if (isDir)
            {
                lines.Add(name);
                continue;
            }

            try
            {
                var info = new FileInfo(entry);
                lines.Add($"{name}  {info.Length}B  {info.LastWriteTime:MM-dd HH:mm}");
            }
            catch
            {
                lines.Add(name);
            }
        }

        var body = lines.Count == 0 ? "（空目录）" : string.Join("\n", lines);
        return new ToolOutcome($"list_dir {sandbox.Display(target.FullPath)}", body);
    }

    private async Task<ToolOutcome> ReadFileAsync(JsonObject args, AgentSandbox sandbox, CancellationToken ct)
    {
        var target = sandbox.Resolve(GetString(args, "path"));
        if (!await EnsureAccessAsync("read_file", target, "读取文件", target.InsideSandbox, ct).ConfigureAwait(false))
        {
            return Denied("read_file", target);
        }

        if (!File.Exists(target.FullPath))
        {
            return new ToolOutcome($"read_file {sandbox.Display(target.FullPath)}", "文件不存在");
        }

        var max = Math.Clamp(GetInt(args, "max_chars") ?? 20_000, 200, MaxTextChars);
        await using var stream = File.OpenRead(target.FullPath);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[max];
        var read = await reader.ReadBlockAsync(buffer, ct).ConfigureAwait(false);
        var text = new string(buffer, 0, read);
        var truncated = !reader.EndOfStream;
        return new ToolOutcome($"read_file {sandbox.Display(target.FullPath)}",
            truncated ? text + "\n…（已截断）" : text);
    }

    private async Task<ToolOutcome> WriteFileAsync(JsonObject args, AgentSandbox sandbox, CancellationToken ct)
    {
        var target = sandbox.Resolve(GetString(args, "path"));
        var content = GetString(args, "content") ?? "";
        var append = GetBool(args, "append");
        var allow = target.InsideSandbox && config.AiConfig.AgentAllowWrite;
        if (!await EnsureAccessAsync(append ? "append_file" : "write_file", target,
                $"{(append ? "追加" : "写入")} {content.Length} 字符", allow, ct).ConfigureAwait(false))
        {
            return Denied("write_file", target);
        }

        if (content.Length > MaxTextChars)
        {
            content = content[..MaxTextChars];
        }

        var dir = Path.GetDirectoryName(target.FullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (append)
        {
            await File.AppendAllTextAsync(target.FullPath, content, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(target.FullPath, content, Encoding.UTF8, ct).ConfigureAwait(false);
        }

        var bytes = new FileInfo(target.FullPath).Length;
        return new ToolOutcome($"{(append ? "append" : "write")} {sandbox.Display(target.FullPath)}",
            $"已写入，当前 {bytes} 字节");
    }

    private async Task<ToolOutcome> MakeDirAsync(JsonObject args, AgentSandbox sandbox, CancellationToken ct)
    {
        var target = sandbox.Resolve(GetString(args, "path"));
        var allow = target.InsideSandbox && config.AiConfig.AgentAllowWrite;
        if (!await EnsureAccessAsync("make_dir", target, "创建目录", allow, ct).ConfigureAwait(false))
        {
            return Denied("make_dir", target);
        }

        Directory.CreateDirectory(target.FullPath);
        return new ToolOutcome($"make_dir {sandbox.Display(target.FullPath)}", "已就绪");
    }

    private async Task<ToolOutcome> RunProcessAsync(JsonObject args, AgentSandbox sandbox, CancellationToken ct)
    {
        var program = GetString(args, "program");
        if (string.IsNullOrWhiteSpace(program))
        {
            return new ToolOutcome("run", "缺少 program");
        }

        var resolved = sandbox.Resolve(program);
        var commandLine = program + (string.IsNullOrWhiteSpace(GetString(args, "args")) ? "" : " " + GetString(args, "args"));
        var allow = resolved.InsideSandbox && config.AiConfig.AgentAllowRun;
        if (!await EnsureAccessAsync("run", resolved, commandLine, allow, ct).ConfigureAwait(false))
        {
            return Denied("run", resolved);
        }

        if (!File.Exists(resolved.FullPath))
        {
            return new ToolOutcome("run " + sandbox.Display(resolved.FullPath), "程序不存在");
        }

        var timeout = Math.Clamp(GetInt(args, "timeout_ms") ?? 30_000, 1_000, 300_000);
        var cwd = GetString(args, "cwd");
        var workingDir = string.IsNullOrWhiteSpace(cwd)
            ? Path.GetDirectoryName(resolved.FullPath)!
            : sandbox.Resolve(cwd).FullPath;

        var info = BuildStartInfo(resolved.FullPath, GetString(args, "args"), workingDir);
        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null && stdout.Length < MaxResultChars)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null && stderr.Length < MaxResultChars)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程已经自己退出了
            }

            return new ToolOutcome("run " + Path.GetFileName(resolved.FullPath), $"执行超时（{timeout}ms），已终止");
        }

        var output = stdout.ToString().Trim();
        var error = stderr.ToString().Trim();
        var summary = new StringBuilder();
        summary.Append("退出码 ").Append(process.ExitCode);
        if (output.Length > 0)
        {
            summary.Append("\nstdout:\n").Append(Truncate(output));
        }

        if (error.Length > 0)
        {
            summary.Append("\nstderr:\n").Append(Truncate(error));
        }

        return new ToolOutcome("run " + Path.GetFileName(resolved.FullPath), summary.ToString());
    }

    private static ProcessStartInfo BuildStartInfo(string path, string? args, string workingDir)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var info = new ProcessStartInfo
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (extension is ".bat" or ".cmd")
        {
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(path);
        }
        else if (extension == ".ps1")
        {
            info.FileName = "powershell.exe";
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-ExecutionPolicy");
            info.ArgumentList.Add("Bypass");
            info.ArgumentList.Add("-File");
            info.ArgumentList.Add(path);
        }
        else
        {
            info.FileName = path;
        }

        if (!string.IsNullOrWhiteSpace(args))
        {
            foreach (var part in SplitArguments(args))
            {
                info.ArgumentList.Add(part);
            }
        }

        return info;
    }

    /// <summary>按空格切参数，支持双引号包裹。不经过 shell，避免拼接注入。</summary>
    private static IEnumerable<string> SplitArguments(string args)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in args)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>
    /// 授权判定：<paramref name="preApproved"/> 为 true 时直接放行，否则弹窗询问。
    /// 拒绝也记一行日志，方便事后追模型到底想干什么。
    /// </summary>
    private static async Task<bool> EnsureAccessAsync(string action, AgentPath target, string detail,
        bool preApproved, CancellationToken ct)
    {
        if (preApproved)
        {
            return true;
        }

        if (ct.IsCancellationRequested)
        {
            return false;
        }

        var allowed = await AgentConsent.AskAsync(new AgentRequest(action, target.FullPath, detail))
            .ConfigureAwait(false);
        AiLog.Consent(action, target.FullPath, target.InsideSandbox, allowed);
        return allowed;
    }

    private static ToolOutcome Denied(string action, AgentPath target)
        => new(action + " " + target.FullPath, "用户拒绝了这次操作，不要重复申请同一路径，换方案或结束任务。");

    private static JsonObject ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string? GetString(JsonObject args, string key)
        => args.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static int? GetInt(JsonObject args, string key)
        => args.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : null;

    private static bool GetBool(JsonObject args, string key)
        => args.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string Truncate(string text)
        => text.Length <= MaxResultChars ? text : text[..MaxResultChars] + "…";
}
