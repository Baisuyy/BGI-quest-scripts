using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterGenshinImpact.Core.Mys;
using BetterGenshinImpact.Service.RemoteTask;
using Wpf.Ui.Controls;

namespace BetterGenshinImpact.View.Windows;

/// <summary>
/// 米游社扫码绑定窗口：显示二维码、轮询扫码结果、多角色时让用户挑一个。
/// <para>
/// 和 QQ 机器人共用 <see cref="MysBindingService"/>，只是这里的会话键固定为
/// <see cref="DesktopKey"/> —— 桌面端一次只绑一个账号，没有「多个人同时扫」的问题。
/// </para>
/// </summary>
public partial class MysLoginWindow : FluentWindow
{
    /// <summary>
    /// 桌面端的会话键。
    /// </summary>
    private const string DesktopKey = "desktop";

    private readonly MysBindingService _binding;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    private bool _polling;

    private bool _finished;

    public MysLoginWindow()
        : this(App.GetService<MysBindingService>()
               ?? throw new InvalidOperationException("米游社绑定服务未注册"))
    {
    }

    public MysLoginWindow(MysBindingService binding)
    {
        _binding = binding;
        InitializeComponent();
        _timer.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// 生成二维码并开始轮询。
    /// </summary>
    private async Task StartAsync()
    {
        _finished = false;
        ResetRolePicker();
        StatusText.Text = "正在生成二维码…";
        try
        {
            var start = await _binding.StartAsync(DesktopKey);
            QrImage.Source = ToImage(start.QrImage);
            StatusText.Text = start.Text;
            _timer.Start();
        }
        catch (Exception e) when (e is MysLoginException or InvalidOperationException)
        {
            StatusText.Text = "生成二维码失败：" + e.Message;
        }
    }

    /// <summary>
    /// 轮询一次；确认后停表并展示结果。
    /// </summary>
    private async Task PollAsync()
    {
        if (_polling || _finished)
        {
            return;
        }

        _polling = true;
        try
        {
            var outcome = await _binding.PollAsync(DesktopKey);
            StatusText.Text = outcome.Text;
            if (outcome.Confirmed)
            {
                Finish();
                StatusText.Text = outcome.Text + "\n可以关掉这个窗口了。";
                return;
            }

            var roles = _binding.PeekRoles(DesktopKey);
            if (roles is { Count: > 1 })
            {
                ShowRolePicker(roles);
            }
        }
        catch (Exception e)
        {
            StatusText.Text = "查询扫码状态失败：" + e.Message;
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// 多角色时把下拉框放出来。
    /// </summary>
    private void ShowRolePicker(IReadOnlyList<MysGameRole> roles)
    {
        if (RolePicker.Visibility == Visibility.Visible)
        {
            return;
        }

        _timer.Stop();
        RolePicker.ItemsSource = roles.Select(r => new MysRoleItem(r)).ToList();
        RolePicker.SelectedIndex = 0;
        RolePicker.Visibility = Visibility.Visible;
        ConfirmRoleButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 绑定选中的角色。
    /// </summary>
    private async void OnConfirmRoleClick(object sender, RoutedEventArgs e)
    {
        if (RolePicker.SelectedIndex < 0)
        {
            StatusText.Text = "先选一个角色。";
            return;
        }

        var (ok, text) = await _binding.TrySelectRoleAsync(DesktopKey, RolePicker.SelectedIndex + 1);
        if (ok)
        {
            StatusText.Text = text + "\n可以关掉这个窗口了。";
            Finish();
        }
        else
        {
            StatusText.Text = "选择已过期，重新扫一次码。";
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        QrImage.Source = null;
        await StartAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 结束轮询：保留状态文字，收起需要交互的控件。
    /// </summary>
    private void Finish()
    {
        _finished = true;
        _timer.Stop();
        RolePicker.Visibility = Visibility.Collapsed;
        ConfirmRoleButton.Visibility = Visibility.Collapsed;
    }

    private void ResetRolePicker()
    {
        RolePicker.ItemsSource = null;
        RolePicker.Visibility = Visibility.Collapsed;
        ConfirmRoleButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// PNG 字节转 WPF 图像；必须先 OnLoad 再 EndInit，否则流关掉后图就空了。
    /// </summary>
    private static BitmapImage ToImage(byte[] png)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// 下拉框里的一行。
    /// </summary>
    private sealed class MysRoleItem(MysGameRole role)
    {
        public MysGameRole Role { get; } = role;

        public string Display =>
            $"{Role.Nickname}（{Role.Uid}·{Role.ServerName}"
            + (Role.Level > 0 ? $"·{Role.Level} 级" : string.Empty)
            + "）";
    }
}
