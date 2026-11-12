using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Pathing;
using BetterGenshinImpact.Core.Ai;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.View.Windows;
using BetterGenshinImpact.ViewModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterGenshinImpact.ViewModel.Pages;

/// <summary>
/// 「AI 助手」页：模型配置之外，再给一个能直接下任务的本地助手入口。
/// <para>
/// 授权弹窗在这里注册：模型要动沙箱外的东西时，走 <see cref="AgentConsent"/> 回到 UI 线程问人。
/// </para>
/// </summary>
public partial class AiSettingsPageViewModel : ObservableObject, IViewModel
{
    private readonly AllConfig _config;
    private readonly AiKernel _kernel;
    private readonly LocalAgent _agent;
    private readonly GameRuntimeService _runtimes;
    private readonly PathRecorder _recorder = new();
    private readonly IProgress<string> _progress;
    private CancellationTokenSource? _cts;

    public AiSettingsPageViewModel(AllConfig config, AiKernel kernel, GameRuntimeService runtimes)
    {
        _config = config;
        _kernel = kernel;
        _runtimes = runtimes;
        _agent = new LocalAgent(kernel, config);
        _progress = new Progress<string>(AppendOutput);
        AgentConsent.Handler = RequestConsentAsync;
        _config.AiConfig.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SandboxRoot));
            OnPropertyChanged(nameof(AiStatusText));
        };
    }

    /// <summary>
    /// 一行说清「现在能不能用」：开关、协议、模型、密钥、不可用原因。
    /// 用户测「AI 没生效」时先看这行，不用翻日志。
    /// </summary>
    public string AiStatusText
    {
        get
        {
            var settings = _config.AiConfig;
            var reason = _kernel.UnavailableReason;
            var key = string.IsNullOrWhiteSpace(settings.ApiKey) ? "密钥 空" : "密钥 已填";
            var endpoint = string.IsNullOrWhiteSpace(settings.Endpoint) ? "地址 空" : settings.Endpoint;
            return $"{(_kernel.Enabled ? "已启用" : "未启用")} · {settings.Provider} · "
                   + $"{(string.IsNullOrWhiteSpace(settings.ModelName) ? "模型 空" : settings.ModelName)} · {key} · "
                   + $"{endpoint} · {(reason ?? "可发请求")} · 本次已用 {_kernel.Calls}/{_kernel.MaxCalls}";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonText))]
    private bool _recording;

    [ObservableProperty]
    private string _recordStatus = "未开始。开始后正常走动即可，站着不动不记点。";

    public string RecordButtonText => Recording ? "停止并保存" : "开始录制";

    /// <summary>配置根，供内嵌的 <c>AiSettingsCard</c> 绑定 <c>Config.AiConfig.*</c>。</summary>
    public AllConfig Config => _config;

    /// <summary>当前沙箱根目录（只读展示）。</summary>
    public string SandboxRoot => new AgentSandbox(_config.AiConfig.AgentSandboxRoot).Root;

    /// <summary>本次运行已用请求数 / 上限。</summary>
    public string BudgetText => $"{_kernel.Calls} / {_kernel.MaxCalls}";

    /// <summary>连通性测试的结果文本（内嵌的 AiSettingsCard 直接绑定这个）。</summary>
    [ObservableProperty]
    private string _aiTestResult = "";

    /// <summary>
    /// 发一条最小请求验证地址、密钥、模型名。不占运行额度、不参与熔断。
    /// <para>
    /// 卡片在「任务设置」和「AI 助手」两处复用，两个页面各有自己的 ViewModel，
    /// 所以命令名与结果属性必须在两边都存在，否则按钮点了没有任何反馈。
    /// </para>
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task TestAiConnection()
    {
        AiTestResult = "正在测试…";
        try
        {
            var reply = await _kernel.TestConnectionAsync(CancellationToken.None);
            AiTestResult = "连接正常：" + reply;
        }
        catch (OperationCanceledException)
        {
            AiTestResult = "测试超时：模型没有在设定时间内返回，检查地址与网络";
        }
        catch (Exception e)
        {
            AiTestResult = "测试失败：" + e.Message;
        }
    }

    [ObservableProperty]
    private string _taskInput = "";

    [ObservableProperty]
    private string _output = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    private bool _running;

    public string RunButtonText => Running ? "停止" : "执行";

    [RelayCommand]
    private async Task RunAsync()
    {
        if (Running)
        {
            _cts?.Cancel();
            return;
        }

        var task = TaskInput?.Trim();
        if (string.IsNullOrWhiteSpace(task))
        {
            AppendOutput("先在下面写一句要做什么，例如：读一下 User 目录里最新的配置文件，再列出所有 Cost 字段。");
            return;
        }

        Running = true;
        Output = "";
        _cts = new CancellationTokenSource();
        try
        {
            AppendOutput($"> {task}");
            var result = await _agent.RunAsync(task, _progress, _cts.Token);
            AppendOutput((result.Success ? "✔ " : "✖ ") + result.Summary
                         + $"（{result.Steps} 步 / {result.Calls} 请求）");
        }
        catch (Exception e)
        {
            AppendOutput("✖ " + e.Message);
        }
        finally
        {
            Running = false;
            _cts?.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(BudgetText));
        }
    }

    [RelayCommand]
    private void Clear() => Output = "";

    /// <summary>
    /// 开始/停止路径录制。录制依赖截图器，没启动时直接提示，不静默失败。
    /// </summary>
    [RelayCommand]
    private async Task ToggleRecordAsync()
    {
        if (Recording)
        {
            var result = await _recorder.StopAsync();
            Recording = false;
            RecordStatus = result == null
                ? "没有采到有效点，未保存（确认地图匹配正常、人确实走动过）"
                : $"已保存 {result.Kept} 点（采样 {result.Sampled}）· {result.TotalMeters:F0}m · 状态 {result.MotionSummary}\n{result.FilePath}";
            return;
        }

        var runtime = _runtimes.Current;
        if (runtime == null || !_runtimes.IsRunning)
        {
            RecordStatus = "截图器没启动：先启动 BGI（让左上角出现遮罩窗口），再开始录制";
            return;
        }

        if (!_recorder.Start(runtime))
        {
            RecordStatus = "录制启动失败：运行环境不可用";
            return;
        }

        Recording = true;
        RecordStatus = "录制中…（每 200ms 采样一次，静止与非地图区域自动跳过）";
    }

    [RelayCommand]
    private void OpenRecordFolder()
    {
        try
        {
            var dir = Path.Combine(Global.Absolute("User"), "PathRecord");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception e)
        {
            RecordStatus = "打不开目录：" + e.Message;
        }
    }

    [RelayCommand]
    private void OpenSandbox()
    {
        try
        {
            var root = SandboxRoot;
            if (Directory.Exists(root))
            {
                Process.Start(new ProcessStartInfo { FileName = root, UseShellExecute = true });
            }
        }
        catch (Exception e)
        {
            AppendOutput("打不开目录：" + e.Message);
        }
    }

    private void AppendOutput(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        Output = string.IsNullOrEmpty(Output) ? line : Output + Environment.NewLine + line;
    }

    /// <summary>
    /// 授权回调。必须回到 UI 线程弹窗；用户点「否」或窗口不存在时一律按拒绝处理。
    /// </summary>
    private static Task<bool> RequestConsentAsync(AgentRequest request)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return Task.FromResult(false);
        }

        return dispatcher.InvokeAsync(() =>
        {
            var body = $"""
                        操作：{request.Action}
                        目标：{request.Target}
                        详情：{request.Detail}

                        该操作不在软件目录（沙箱）内，或当前配置要求逐次确认。
                        允许本次执行吗？拒绝不会中断任务，模型会换一种做法。
                        """;
            var result = ThemedMessageBox.Show(body, "本地助手请求授权",
                MessageBoxButton.YesNo, ThemedMessageBox.MessageBoxIcon.Warning, MessageBoxResult.No);
            return result == MessageBoxResult.Yes;
        }).Task;
    }
}
