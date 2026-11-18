using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BetterGenshinImpact.Core.Ai;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.View.Pages;
using Fischless.HotkeyCapture;
using Microsoft.Extensions.Logging;
using Wpf.Ui;

namespace BetterGenshinImpact.View.Windows;

/// <summary>面板里的一条消息，决定气泡的颜色与对齐。</summary>
public enum PanelMessageKind
{
    /// <summary>灰色小字提示，不是模型说的话。</summary>
    Hint,
    User,
    Ai,
    /// <summary>工具调用步骤，压成等宽小字。</summary>
    Step,
    Ok,
    Fail
}

/// <summary>面板消息。只读，产生后不再变化。</summary>
public sealed class PanelMessage
{
    public string Text { get; init; } = "";

    public PanelMessageKind Kind { get; init; }
}

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

    /// <summary>默认唤起快捷键。Ctrl+Alt+A 是 QQ 的截图键，装了 QQ 必然抢不到，所以后面还排了备选。</summary>
    private const string DefaultHotkey = "Ctrl+Alt+A";

    /// <summary>
    /// 候选快捷键，按顺序尝试，第一个注册成功的生效。
    /// <para>
    /// Ctrl+Alt+A 被 QQ 截图占用时注册会抛 <c>Hotkey already registered</c>，
    /// 以前只记一条日志，用户看到面板「打不开」却不知道去哪改。
    /// </para>
    /// </summary>
    private static readonly string[] HotkeyCandidates = ["Ctrl+Alt+A", "Ctrl+Alt+G", "Ctrl+Shift+A"];

    /// <summary>实际生效的快捷键，注册失败时为空。</summary>
    private static string _activeHotkey = DefaultHotkey;

    private static AgentPanelWindow? _instance;
    private static HotkeyHook? _hotkeyHook;
    private static bool _hotkeyAttempted;
    private static bool _hotkeyReady;

    private readonly LocalAgent? _agent;
    private readonly AiKernel? _kernel;
    private readonly AllConfig? _config;
    private readonly IProgress<string> _progress;

    /// <summary>对话内容。面板只保留本次会话，换任务就清空，免得看起来像有多轮上下文。</summary>
    private readonly ObservableCollection<PanelMessage> _messages = [];

    /// <summary>面板最多留多少条消息，超出丢最早的。</summary>
    private const int MaxMessages = 200;

    private CancellationTokenSource? _cts;
    private bool _running;

    private AgentPanelWindow()
    {
        InitializeComponent();
        _progress = new Progress<string>(Append);

        OutputList.ItemsSource = _messages;
        ShowWelcome();

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
    /// 默认键被占用时自动换下一个候选键；全部失败也不抛出——面板仍可从 AI 助手页打开。
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
        foreach (var candidate in HotkeyCandidates)
        {
            HotkeyHook? hook = null;
            try
            {
                var hotkey = new Hotkey(candidate);
                hook = new HotkeyHook();
                hook.KeyPressed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(Toggle));
                hook.RegisterHotKey(hotkey.ModifierKey, hotkey.Key);
                _hotkeyHook = hook;
                _activeHotkey = candidate;
                _hotkeyReady = true;
                if (candidate != DefaultHotkey)
                {
                    Logger.LogInformation("助手面板快捷键 {Default} 被占用，改用 {Actual}", DefaultHotkey, candidate);
                }

                return true;
            }
            catch (Exception e)
            {
                hook?.Dispose();
                Logger.LogWarning(e, "助手面板快捷键 {Key} 注册失败，试下一个", candidate);
            }
        }

        _hotkeyReady = false;
        Logger.LogWarning("助手面板快捷键全部被占用（{Keys}），可从 AI 助手页打开", string.Join(" / ", HotkeyCandidates));
        return false;
    }

    /// <summary>
    /// 注册全局快捷键。失败不抛出——被别的软件占用时面板仍可通过 AI 助手页打开。
    /// </summary>
    private void EnsureHotkey()
    {
        EnsureGlobalHotkey();
        HintText.Text = _hotkeyReady ? _activeHotkey : "快捷键被占用，从 AI 助手页打开";
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
            Append("先写一句要做什么，例如：把今天的体力清掉。", PanelMessageKind.Hint);
            return;
        }

        if (_agent == null)
        {
            Append("内置模型未初始化，无法执行。", PanelMessageKind.Fail);
            return;
        }

        InputBox.Clear();

        // 每次都是独立会话，旧气泡留着会让人以为模型记得上一轮
        _messages.Clear();
        Append(text, PanelMessageKind.User);

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

            // 模型最后那句话通常已经作为气泡显示过了，重复贴一遍只是噪音
            var last = _messages.Count > 0 ? _messages[^1] : null;
            var alreadyShown = last is { Kind: PanelMessageKind.Ai } && last.Text == result.Summary;
            if (!alreadyShown && !string.IsNullOrWhiteSpace(result.Summary))
            {
                Append(result.Summary, result.Success ? PanelMessageKind.Ai : PanelMessageKind.Fail);
            }

            Append($"{(result.Success ? "完成" : "中止")} · {result.Steps} 步 · {result.Calls} 次模型请求",
                result.Success ? PanelMessageKind.Hint : PanelMessageKind.Fail);
        }
        catch (OperationCanceledException)
        {
            Append("已取消", PanelMessageKind.Fail);
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "助手面板执行失败");
            Append(e.Message, PanelMessageKind.Fail);
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

    /// <summary>追加一条消息并滚到底；超过 <see cref="MaxMessages"/> 条时丢掉最早的。</summary>
    private void Append(string line, PanelMessageKind kind)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _messages.Add(new PanelMessage { Text = line.Trim(), Kind = kind });
        while (_messages.Count > MaxMessages)
        {
            _messages.RemoveAt(0);
        }

        ScrollToEnd();
    }

    /// <summary>未标注类型的消息按内容猜：勾叉是结果，[n/m] 是工具步骤，其余当作模型说的话。</summary>
    private void Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Append(line, GuessKind(line));
    }

    private static PanelMessageKind GuessKind(string line)
    {
        if (line.StartsWith('✔'))
        {
            return PanelMessageKind.Ok;
        }

        if (line.StartsWith('✖'))
        {
            return PanelMessageKind.Fail;
        }

        // 步骤行形如「[3/16] 调用 run_task」，斜杠必须出现在方括号内
        if (line[0] == '[')
        {
            var close = line.IndexOf(']');
            var slash = line.IndexOf('/');
            if (close > 2 && slash > 0 && slash < close)
            {
                return PanelMessageKind.Step;
            }
        }

        return PanelMessageKind.Ai;
    }

    /// <summary>新气泡还没参与布局，立刻滚动常停在上一条，等一轮再滚。</summary>
    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OutputScroll.ScrollToEnd()));
    }

    private void ShowWelcome()
    {
        Append("说一句要做什么，例如：", PanelMessageKind.Hint);
        Append("· 把今天的体力清掉", PanelMessageKind.Hint);
        Append("· 领一下每日和邮件", PanelMessageKind.Hint);
        Append("· 跑「第七章第五幕」脚本", PanelMessageKind.Hint);
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
