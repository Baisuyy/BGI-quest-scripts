using BetterGenshinImpact.Core.Special;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
///     <see cref="CommonConfig" /> 的特别版扩展。
///     <para>
///         这些字段原先写在 CommonConfig.cs 里，但那个文件上游也有，同步脚本（sync_source.py）
///         会用上游源码整份覆盖它——每轮同步都把这些字段抹掉，2026-11-20 那次因此让远端分支
///         编译不过（LogUploader 引用 LogUploadMode 时字段已经不存在了）。
///     </para>
///     <para>
///         CommonConfig 是 partial，把特别版成员放到这个文件里，同步时就完全不受覆盖影响，
///         也不需要再往 patch_special.py 里加锚点去重新注入。
///     </para>
/// </summary>
public partial class CommonConfig
{
    /// <summary>
    ///     上次已经弹过更新提示的特别版版本号。
    ///     <para>启动时的自动检查对每个版本只提醒一次，记在这里；空字符串表示还没提醒过。</para>
    /// </summary>
    [ObservableProperty]
    private string _lastNotifiedSpecialVersion = string.Empty;

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
