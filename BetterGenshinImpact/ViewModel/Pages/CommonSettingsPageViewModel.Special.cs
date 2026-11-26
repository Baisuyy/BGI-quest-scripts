using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Special;
using BetterGenshinImpact.View.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterGenshinImpact.ViewModel.Pages;

/// <summary>
/// 设置页的特别版字段：日志落盘级别、日志上传、生成诊断包。
/// 单独成 partial 文件，是为了在上游同步覆盖 CommonSettingsPageViewModel.cs 后不用手工补回。
/// </summary>
public partial class CommonSettingsPageViewModel
{
    /// <summary>
    /// 写入日志文件的详细程度。改完要重启才生效——Serilog 的级别在启动时就定死了。
    /// </summary>
    public int SelectedLogFileLevel
    {
        get => (int)Config.CommonConfig.LogFileLevel;
        set
        {
            if (value < 0 || value > 3 || SelectedLogFileLevel == value)
            {
                return;
            }

            Config.CommonConfig.LogFileLevel = (LogFileLevel)value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 日志上传方式。0 关闭 / 1 仅手动 / 2 自动。
    /// </summary>
    public int SelectedLogUploadMode
    {
        get => (int)Config.CommonConfig.LogUploadMode;
        set
        {
            if (value < 0 || value > 2 || SelectedLogUploadMode == value)
            {
                return;
            }

            Config.CommonConfig.LogUploadMode = (LogUploadMode)value;
            OnPropertyChanged();
        }
    }

    /// <summary>上传进行中：按钮置灰，避免连点发两份。</summary>
    [ObservableProperty]
    private bool _isLogUploading;

    /// <summary>
    /// 上传当前日志。走的是和自动模式同一条链路：脱敏 → 压缩 → 签名 → POST。
    /// </summary>
    [RelayCommand]
    public async Task OnUploadLog()
    {
        if (IsLogUploading)
        {
            return;
        }

        if (Config.CommonConfig.LogUploadMode == LogUploadMode.Off)
        {
            _ = ThemedMessageBox.ShowAsync("日志上传已关闭。要上传请先把上面的选项改成「仅手动」或「自动」。",
                "日志上传", MessageBoxButton.OK, ThemedMessageBox.MessageBoxIcon.Information);
            return;
        }

        IsLogUploading = true;
        try
        {
            var r = await LogUploader.UploadLatestAsync("manual");
            if (r.Ok)
            {
                _ = ThemedMessageBox.ShowAsync(
                    $"日志已上传（编号 {r.Id}）。服务端已经收到，不用再发一次。",
                    "日志上传", MessageBoxButton.OK, ThemedMessageBox.MessageBoxIcon.Success);
            }
            else
            {
                _ = ThemedMessageBox.ShowAsync(
                    $"没传上去：{r.Message}\n\n网络不通的时候不影响游戏和任务，日志仍在本地 log 目录里。",
                    "日志上传", MessageBoxButton.OK, ThemedMessageBox.MessageBoxIcon.Warning);
            }
        }
        finally
        {
            IsLogUploading = false;
        }
    }

    /// <summary>
    /// 手动生成一个诊断包并打开所在目录。出错时也会自动生成，这里给的是「现在就想发一个」的入口。
    /// </summary>
    [RelayCommand]
    public void OnBuildDiagnostics()
    {
        var path = Core.Special.SpecialDiagnostics.Build("用户手动生成");
        if (path == null)
        {
            _ = ThemedMessageBox.ShowAsync("诊断包生成失败，请直接打开日志目录手动反馈。", "生成诊断包",
                MessageBoxButton.OK, ThemedMessageBox.MessageBoxIcon.Error);
            return;
        }

        // 直接打开目录比弹一个「成功了」更省事：用户下一步就是把 zip 拖出去。
        Core.Special.SpecialDiagnostics.OpenFolder();
    }
}
