using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Ai;
using BetterGenshinImpact.Core.Config;
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
    private readonly IProgress<string> _progress;
    private CancellationTokenSource? _cts;

    public AiSettingsPageViewModel(AllConfig config, AiKernel kernel)
    {
        _config = config;
        _kernel = kernel;
        _agent = new LocalAgent(kernel, config);
        _progress = new Progress<string>(AppendOutput);
        AgentConsent.Handler = RequestConsentAsync;
        _config.AiConfig.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SandboxRoot));
    }

    /// <summary>配置根，供内嵌的 <c>AiSettingsCard</c> 绑定 <c>Config.AiConfig.*</c>。</summary>
    public AllConfig Config => _config;

    /// <summary>当前沙箱根目录（只读展示）。</summary>
    public string SandboxRoot => new AgentSandbox(_config.AiConfig.AgentSandboxRoot).Root;

    /// <summary>本次运行已用请求数 / 上限。</summary>
    public string BudgetText => $"{_kernel.Calls} / {_kernel.MaxCalls}";

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
