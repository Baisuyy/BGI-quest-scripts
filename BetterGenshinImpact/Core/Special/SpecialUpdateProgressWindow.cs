using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterGenshinImpact.Infrastructure.Network.Download;
using TextBlock = System.Windows.Controls.TextBlock;

namespace BetterGenshinImpact.Core.Special;

/// <summary>
///     增量更新下载时的进度窗。
///     下载可能持续几十秒，不给反馈的话用户会以为程序卡死。
///     直接代码建窗，不进 XAML，避免为一个临时窗口增加一套资源。
/// </summary>
public sealed class SpecialUpdateProgressWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _detail;
    private readonly TextBlock _title;

    /// <summary>创建一个进度窗。owner 为空时居中屏幕。</summary>
    public SpecialUpdateProgressWindow(string title)
    {
        Title = "特别版更新";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = ThemeBrush("ApplicationBackgroundBrush", Brushes.White);

        _title = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };

        _bar = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
        };

        _detail = new TextBlock
        {
            Text = "正在连接…",
            Opacity = 0.7,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(_title);
        panel.Children.Add(_bar);
        panel.Children.Add(_detail);
        Content = panel;
    }

    /// <summary>按下载进度刷新界面，可从任意线程调用。</summary>
    public void Report(DownloadProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Report(progress));
            return;
        }

        if (progress.TotalBytes is > 0)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = progress.Percentage ?? 0;
        }
        else
        {
            _bar.IsIndeterminate = true;
        }

        string speed = progress.BytesPerSecond > 0
            ? $"{progress.BytesPerSecond / 1024d / 1024d:0.0} MB/s"
            : "—";
        string size = progress.TotalBytes is > 0
            ? $"{progress.ReceivedBytes / 1024d / 1024d:0.0} / {progress.TotalBytes.Value / 1024d / 1024d:0.0} MB"
            : $"{progress.ReceivedBytes / 1024d / 1024d:0.0} MB";

        string remaining = progress.Remaining is { } left
            ? "，剩余 " + (left.TotalSeconds > 60
                ? $"{(int)left.TotalMinutes} 分 {left.Seconds} 秒"
                : $"{Math.Max(1, (int)left.TotalSeconds)} 秒")
            : "";

        _detail.Text = progress.State switch
        {
            DownloadState.Queued => "排队中…",
            DownloadState.Verifying => "校验中…",
            DownloadState.Completed => "下载完成，正在解压校验…",
            DownloadState.Failed => "下载失败：" + (progress.Error ?? "未知原因"),
            DownloadState.Canceled => "已取消。",
            _ => $"{size}　{speed}{remaining}",
        };
    }

    /// <summary>换一行提示，例如「正在解压」。保持窗口打开。</summary>
    public void SetTitle(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetTitle(text));
            return;
        }

        _title.Text = text;
        _bar.IsIndeterminate = true;
        _detail.Text = "请稍候…";
    }

    private static Brush ThemeBrush(string key, Brush fallback)
    {
        try
        {
            return Application.Current?.TryFindResource(key) as Brush ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
