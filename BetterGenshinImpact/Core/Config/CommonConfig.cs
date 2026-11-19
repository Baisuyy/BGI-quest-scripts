using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Core.Special;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 主题类型配置
/// </summary>
public enum ThemeType
{
    DarkNone,
    DarkMica,
    DarkAcrylic,
    LightNone,
    LightMica,
    LightAcrylic,
}


/// <summary>
///     遮罩窗口配置
/// </summary>
[Serializable]
public partial class CommonConfig : ObservableObject
{
    /// <summary>
    ///     是否启用遮罩窗口
    /// </summary>
    [ObservableProperty]
    private bool _screenshotEnabled;

    /// <summary>
    ///     UID遮盖是否启用
    /// </summary>
    [ObservableProperty]
    private bool _screenshotUidCoverEnabled = true;

    /// <summary>
    ///     是否保存奖励识别调试截图
    /// </summary>
    [ObservableProperty]
    private bool _rewardRecognitionScreenshotEnabled;

    /// <summary>
    ///     退出时最小化至托盘
    /// </summary>
    [ObservableProperty]
    private bool _exitToTray;

    /// <summary>
    /// 当前主题类型（新版主题）
    /// </summary>
    [ObservableProperty]
    private ThemeType _currentThemeType = OsVersionHelper.IsWindows11_22523_OrGreater ? ThemeType.DarkMica : ThemeType.DarkNone;

    /// <summary>
    /// 主题（旧版主题，兼容性保留）
    /// </summary>
    [ObservableProperty]
    private WindowBackdropType _currentBackdropType = WindowBackdropType.Mica;

    /// <summary>
    /// 是否启用主窗口自定义背景图
    /// </summary>
    [ObservableProperty]
    private bool _mainBackgroundEnabled;

    /// <summary>
    /// 主窗口自定义背景图路径，空字符串表示未设置
    /// </summary>
    [ObservableProperty]
    private string _mainBackgroundImagePath = string.Empty;

    /// <summary>
    /// 主窗口背景图不透明度，数值越小背景越淡，文字越清晰
    /// </summary>
    [ObservableProperty]
    private double _mainBackgroundOpacity = 0.35;

    /// <summary>
    /// 主窗口背景图拉伸模式
    /// </summary>
    [ObservableProperty]
    private Stretch _mainBackgroundStretch = Stretch.UniformToFill;

    /// <summary>
    /// 是否是第一次运行
    /// </summary>
    [ObservableProperty]
    private bool _isFirstRun = true;

    /// <summary>
    /// 这个版本是否运行过
    /// </summary>
    [ObservableProperty]
    private string _runForVersion = string.Empty;

    /// <summary>
    ///     上次已经弹过更新提示的特别版版本号。
    ///     <para>启动时的自动检查对每个版本只提醒一次，记在这里；空字符串表示还没提醒过。</para>
    /// </summary>
    [ObservableProperty]
    private string _lastNotifiedSpecialVersion = string.Empty;

    /// <summary>
    /// 一个设备只运行一次的已运行设备ID列表
    /// </summary>
    [ObservableProperty]
    private List<string> _onceHadRunDeviceIdList = new();
    
    
    /// <summary>
    /// 当前看过的兑换码推送版本
    /// </summary>
    [ObservableProperty]
    private string _redeemCodeFeedsUpdateVersion = "20251013";

    /// <summary>
    /// 国服兑换码更新是否允许通知
    /// </summary>
    [ObservableProperty]
    private bool _redeemCodeCnFeedsNotificationEnabled = true;

    /// <summary>
    /// 国际服兑换码更新是否允许通知
    /// </summary>
    [ObservableProperty]
    private bool _redeemCodeGlobalFeedsNotificationEnabled;

    /// <summary>
    /// 当前看过的国际服兑换码推送版本
    /// </summary>
    [ObservableProperty]
    private string _redeemCodeGlobalFeedsUpdateVersion = string.Empty;

    /// <summary>
    ///     写入日志文件的最低级别。
    ///     <para>
    ///         默认 Debug，排查问题方便，但一天能写出几十 MB、上百万行。
    ///         设成 Error 之后只留错误，长时间挂机、只关心「有没有出事」时用这个。
    ///         界面日志框不受影响（它自己按级别过滤），这里只管落盘。
    ///     </para>
    /// </summary>
    [ObservableProperty]
    private LogFileLevel _logFileLevel = LogFileLevel.Debug;

    /// <summary>
    ///     出错时自动打包诊断信息（日志 + 环境），方便反馈问题时直接发出去。
    ///     <para>只在本机生成 zip，不会自动上传；上传需要用户自己点。</para>
    /// </summary>
    [ObservableProperty]
    private bool _diagnosticsOnErrorEnabled = true;

    /// <summary>
    ///     日志上传方式：<see cref="LogUploadMode.Off" /> / Manual / Auto。
    ///     <para>
    ///         默认 Manual——打包好了放在本地，用户点了才发。选 Auto 之后，
    ///         出错和正常退出时会自动把当前日志（已脱敏、已压缩）发到自建接收端。
    ///         不想有任何出站请求就选 Off。
    ///     </para>
    /// </summary>
    [ObservableProperty]
    private LogUploadMode _logUploadMode = LogUploadMode.Manual;

    /// <summary>
    ///     日志接收地址，默认指向自建服务。留空则回落到内置默认值。
    /// </summary>
    [ObservableProperty]
    private string _logUploadUrl = LogUploader.DefaultEndpoint;

    /// <summary>
    ///     地图追踪断点续跑。
    ///     <para>
    ///         开启后，一条路线被中断（停止、崩溃、断电）时记下走到第几段，
    ///         下次执行同一条路线会接着走，不用从头再来。脚本跑几十条路线时差别很大。
    ///         关掉则每次都从第一个点开始，适合「这条路线我重新录过、要完整验证一遍」的场景。
    ///     </para>
    /// </summary>
    [ObservableProperty]
    private bool _pathingResumeEnabled = true;
}

/// <summary>日志文件保留级别。</summary>
public enum LogFileLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
}
