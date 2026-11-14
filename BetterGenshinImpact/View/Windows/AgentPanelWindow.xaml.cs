using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BetterGenshinImpact.Core.Ai;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.View.Pages;
using Fischless.HotkeyCapture;
using Microsoft.Extensions.Logging;
using Wpf.Ui;

namespace BetterGenshinImpact.View.Windows;

/// <summary>
/// 常驻小面板：游戏运行时按 <c>Ctrl+Alt+A</c> 唤起，用一句话让内置模型去下任务。
/// <para>
/// 它不新开一套执行链路——模型仍然走 <see cref="LocalAgent"/>，
/// 只是工具集里多了「下发内置任务 / 运行脚本」，由 BGI 自己的任务锁与停止按钮收口。
/// </para>
/// <para>
/// 窗口只创建一次，关闭按钮做的是隐藏；热键与进程同寿命，避免反复注册抢占快捷键。
/// </para>
/// </summary>
public partial class AgentPanelWindow : Window
{
    private static readonly ILogger Logger = App.GetLogger<AgentPanelWindow>();

    /// <summary>默认唤起快捷键。与 BGI 其他默认热键不冲突，且不会误触游戏按键。</summary>
    private const string DefaultHotkey = "Ctrl+Alt+A";

    private static AgentPanelWindow? _instance;
    private static HotkeyHook? _hotkeyHook;
    private static bool _hotkeyAttempted;
    private static bool _hotkeyReady;

    private readonly LocalAgent? _agent;
    private readonly AiKernel? _kernel;
    private readonly AllConfig? _config;
    private readonly IProgress<string> _progress;
    private CancellationTokenSource? _cts;
    private bool _running;

    private AgentPanelWindow()
    {
        InitializeComponent();
        _progress = new Progress<string>(Append);

        _kernel = TryGet<AiKernel>();
        _config = TryGet<AllConfig>();
        if (_kernel != null && _config != null)
        {
            _agent = new LocalAgent(_kernel, _config);
            // 面板可能先于 AI 助手页出现，授权闸门在这里补默认实现
            AgentConsent.EnsureDefaultHandler();
        }

        RefreshStatus();
        ApplyLayout();
    }

    /// <summary>
    /// 显示面板（已存在则只切到前台）。供热键与菜单调用。
    /// </summary>
    public static void ShowPanel()
    {
        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        app.Dispatcher.Invoke(() =>
        {
            if (_instance == null)
            {
                _instance = new AgentPanelWindow();
                _instance.Closed += (_, _) => _instance = null;
            }

            var window = _instance;
            window.RefreshStatus();
            window.Show();
            window.Activate();
            window.InputBox.Focus();
            window.EnsureHotkey();
        });
    }

    /// <summary>热键唤起/隐藏同一个面板。</summary>
    private static void Toggle()
    {
        if (_instance is { IsVisible: true })
        {
            _instance.Hide();
            return;
        }

        ShowPanel();
    }

    /// <summary>
    /// 启动时注册一次全局快捷键，让用户不打开主界面也能唤起面板。
    /// 失败不抛出——被别的软件占用时面板仍可从 AI 助手页打开。
    /// </summary>
    public static bool EnsureGlobalHotkey()
    {
        if (_hotkeyHook != null)
        {
            return true;
        }

        if (_hotkeyAttempted)
        {
            return _hotkeyReady;
        }

        _hotkeyAttempted = true;
        try
        {
            var hotkey = new Hotkey(DefaultHotkey);
            var hook = new HotkeyHook();
            hook.KeyPressed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(Toggle));
            hook.RegisterHotKey(hotkey.ModifierKey, hotkey.Key);
            _hotkeyHook = hook;
            _hotkeyReady = true;
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "助手面板快捷键 {Key} 注册失败，可从 AI 助手页打开", DefaultHotkey);
            _hotkeyReady = false;
        }

        return _hotkeyReady;
    }

    /// <summary>
    /// 注册全局快捷键。失败不抛出——被别的软件占用时面板仍可通过 AI 助手页打开。
    /// </summary>
    private void EnsureHotkey()
    {
        EnsureGlobalHotkey();
        HintText.Text = _hotkeyReady ? DefaultHotkey : DefaultHotkey + "（被占用）";
    }

    /// <summary>把面板放到工作区右下角，避开任务栏。</summary>
    private void ApplyLayout()
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, area.Right - Width - 24);
        Top = Math.Max(area.Top, area.Bottom - Height - 24);
    }

    /// <summary>刷新状态灯与提示：能不能用，一眼看出来，不用翻日志。</summary>
    private void RefreshStatus()
    {
        if (_agent == null || _kernel == null)
        {
            SetDot("#FF6B6B");
            HintText.Text = "内置模型未初始化";
            SendButton.IsEnabled = false;
            return;
        }

        var reason = _kernel.UnavailableReason;
        if (reason != null)
        {
            SetDot("#FF6B6B");
            HintText.Text = reason;
            SendButton.IsEnabled = true; // 仍允许点，点了会给出具体原因
            return;
        }

        if (_config is { AiConfig.AgentEnabled: false })
        {
            SetDot("#FFB020");
            HintText.Text = "本地助手未启用（AI 助手页打开）";
            SendButton.IsEnabled = true;
            return;
        }

        SetDot(_running ? "#4CC2FF" : "#5BD68A");
        HintText.Text = _hotkeyReady ? DefaultHotkey : "点「发送」开始";
        SendButton.IsEnabled = true;
    }

    private void SetDot(string color)
        => StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private async void OnSend(object sender, RoutedEventArgs e) => await SendOrStopAsync();

    private async Task SendOrStopAsync()
    {
        if (_running)
        {
            _cts?.Cancel();
            return;
        }

        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            Append("先写一句要做什么，例如：把今天的体力清掉。");
            return;
        }

        if (_agent == null)
        {
            Append("内置模型未初始化，无法执行。");
            return;
        }

        InputBox.Clear();
        Append("> " + text);

        // 面板是 Topmost，输入结束后焦点还在输入框上。任务一开始就会因为「前台不是原神」被暂停，
        // 传送这类需要连续点击的操作尤其明显。这里在交给模型之前先把焦点还给游戏窗口，
        // 面板本身留在原地显示进度——不隐藏，用户还要看。
        ReturnFocusToGame();

        _running = true;
        _cts = new CancellationTokenSource();
        SendButton.Content = "停止";
        RefreshStatus();

        try
        {
            var result = await _agent.RunAsync(text, _progress, _cts.Token);
            Append((result.Success ? "✔ " : "✖ ") + result.Summary
                   + $"（{result.Steps} 步 / {result.Calls} 请求）");
        }
        catch (OperationCanceledException)
        {
            Append("✖ 已取消");
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "助手面板执行失败");
            Append("✖ " + e.Message);
        }
        finally
        {
            _running = false;
            _cts?.Dispose();
            _cts = null;
            SendButton.Content = "发送";
            RefreshStatus();
        }
    }

    /// <summary>回车发送；Shift+回车不拦截（输入框本身是单行，保留习惯）。</summary>
    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            await SendOrStopAsync();
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
        }
    }

    /// <summary>
    /// 把前台让给游戏窗口。找不到游戏时静默返回——焦点由任务侧的检查兜底。
    /// </summary>
    private static void ReturnFocusToGame()
    {
        try
        {
            var handle = BetterGenshinImpact.GameTask.SystemControl.FindGenshinImpactHandle();
            if (handle != 0)
            {
                BetterGenshinImpact.GameTask.SystemControl.FocusWindow(handle);
            }
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "归还焦点给游戏窗口失败");
        }
    }

    /// <summary>只在空白处拖动，避免在输入框里按下鼠标时窗口跟着跑。</summary>
    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideTextInput(source))
        {
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标已经抬起，忽略即可
            }
        }
    }

    private static bool IsInsideTextInput(DependencyObject source)
    {
        for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.TextBox or System.Windows.Controls.ScrollViewer)
            {
                return true;
            }
        }

        return false;
    }

    private void OnHide(object sender, RoutedEventArgs e) => Hide();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            App.GetService<INavigationService>()?.Navigate(typeof(AiSettingsPage));
            Application.Current?.MainWindow?.Activate();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "打开 AI 助手设置页失败");
        }
    }

    /// <summary>失焦后稍微压暗，避免挡住游戏画面；不自动隐藏，方便边看边操作。</summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_running)
        {
            return;
        }

        Opacity = 0.88;
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Opacity = 1;
    }

    /// <summary>追加一行并滚到底；超过 200 行时丢掉前半，防止长时间运行把内存撑大。</summary>
    private void Append(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        var text = OutputText.Text;
        if (string.IsNullOrEmpty(text) || text.StartsWith("说一句要做什么", StringComparison.Ordinal))
        {
            text = line;
        }
        else
        {
            text += Environment.NewLine + line;
        }

        var lines = text.Split('\n');
        if (lines.Length > 200)
        {
            text = string.Join('\n', lines[(lines.Length - 200)..]);
        }

        OutputText.Text = text;
        OutputScroll.ScrollToEnd();
    }

    private static T? TryGet<T>() where T : class
    {
        try
        {
            return App.GetService<T>();
        }
        catch
        {
            return null;
        }
    }
}
