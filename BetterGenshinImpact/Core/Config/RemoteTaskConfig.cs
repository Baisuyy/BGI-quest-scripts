using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.ComponentModel.DataAnnotations;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 远程任务配置：通过聊天软件把任务下发给 BetterGI，并回传执行截图。
/// <para>
/// 当前只接入 QQ 官方机器人（WebSocket 事件订阅 + C2C/群被动回复）。
/// 机器人凭证留空时回退到「通知设置」里已绑定的 QQ 凭证，避免重复配置。
/// </para>
/// </summary>
[Serializable]
public partial class RemoteTaskConfig : ObservableValidator
{
    /// <summary>
    /// 是否启用 QQ 机器人远程任务。
    /// </summary>
    [ObservableProperty]
    private bool _qqBotEnabled;

    /// <summary>
    /// 是否允许群聊 @机器人 下发任务。关闭时只接受私聊。
    /// </summary>
    [ObservableProperty]
    private bool _qqBotAllowGroup = true;

    /// <summary>
    /// QQ 开放平台 AppID，留空时使用通知设置里的 AppID。
    /// </summary>
    [ObservableProperty]
    private string _qqBotAppId = string.Empty;

    /// <summary>
    /// QQ 开放平台 AppSecret，留空时使用通知设置里的 AppSecret。
    /// </summary>
    [ObservableProperty]
    private string _qqBotAppSecret = string.Empty;

    /// <summary>
    /// 允许下发任务的 OpenID 白名单，逗号、分号或换行分隔；留空表示不限制。
    /// <para>
    /// C2C 的 OpenID 与群成员 OpenID 不同，群聊场景可填群主或任意成员的 OpenID。
    /// 空名单配合群聊使用时风险较高，建议至少填上自己的 OpenID。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _qqBotAllowedOpenIds = string.Empty;

    /// <summary>
    /// 忽略 QQ 网关的 TLS 证书校验，仅用于本机代理（Clash / Fiddler 之类）做了中间人解密时。
    /// <para>
    /// 代理开了 MITM 之后，<c>api.sgroup.qq.com</c> 返回的是代理自签的证书，
    /// 系统信任链校验必然失败，机器人永远连不上。把域名加进直连是更干净的做法；
    /// 如果代理没法按域名分流，只能打开这个开关——它会把机器人流量暴露给代理进程，
    /// 所以默认关闭，且改动后需要重启程序（连接循环是常驻的）。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _qqBotAllowInsecureTls;

    /// <summary>
    /// 任务没有正常结束时回传一张游戏截图。
    /// <para>
    /// 只在失败或取消时截：成功后的画面就是角色站在终点，看不出任何东西。
    /// 成功时的回传改成「结果 + 过程中出现的告警」，比一张空镜有用。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _screenshotOnFinish = true;

    /// <summary>
    /// 回传截图的最长边像素，0 表示不缩放。
    /// </summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(0, 3840, ErrorMessage = "截图最长边必须在 0 到 3840 之间。")]
    private int _screenshotMaxWidth = 1280;

    /// <summary>
    /// 回传截图 JPEG 质量（1-100）。数值越低，上传越快，QQ 侧限流风险越小。
    /// </summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 100, ErrorMessage = "截图质量必须在 1 到 100 之间。")]
    private int _screenshotQuality = 80;

    /// <summary>
    /// 关闭主窗口时留在托盘后台运行，保持 QQ 机器人在线，随时可以被下发任务。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveShowTrayIcon))]
    private bool _keepAliveInTray;

    /// <summary>
    /// 是否在任务栏通知区域显示 BetterGI 小图标。
    /// <para>
    /// 关掉之后只能靠重新启动 BetterGI 唤回主界面，因此与「后台保活」互斥：
    /// 保活开启时托盘图标强制显示，避免程序在后台跑却没有任何入口。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveShowTrayIcon))]
    private bool _showTrayIcon = true;

    /// <summary>
    /// 实际是否显示托盘图标：后台保活时必须有入口，所以强制显示。
    /// </summary>
    public bool EffectiveShowTrayIcon => ShowTrayIcon || KeepAliveInTray;

    /// <summary>
    /// 单条回复的最大字符数，超出时截断并在末尾提示。
    /// </summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(100, 4000, ErrorMessage = "回复长度上限必须在 100 到 4000 之间。")]
    private int _replyMaxChars = 1200;

    /// <summary>
    /// 对话人格预设标识，见 <c>PersonaPresets</c>。空串等同 <c>default</c>。
    /// <para>
    /// 人格只影响聊天与解释类回复的措辞，不影响任务解析：同一个任务不会因为换了人格就解析不出来。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _personaPreset = "default";

    /// <summary>
    /// 自定义人格描述。填写后覆盖预设，直接作为系统提示词里的角色设定。
    /// </summary>
    [ObservableProperty]
    private string _customPersona = string.Empty;

    /// <summary>
    /// 允许模型在「任务没注册」时自己开一个操作循环，边看画面边点击。
    /// <para>
    /// 关掉后，未注册的请求只会被拒绝并建议可用的任务，不会动键鼠——适合只想用确定性任务的场景。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _selfLearningEnabled = true;

    /// <summary>
    /// 一次自由操作最多走多少步。步数越多越可能做成复杂操作，也越费 token。
    /// <para>
    /// 默认 30：登录、下载资源、过场这类流程十几步走不完，16 步会在半路被硬截断。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(6, 60, ErrorMessage = "自由操作步数必须在 6 到 60 之间。")]
    private int _selfLearningMaxSteps = 30;

    /// <summary>
    /// 米游社 Cookie（含 ltoken / ltuid 或 cookie_token），用于直接查实时树脂。
    /// <para>
    /// 留空时改为「打开地图截一张图再用 OCR 读数」，不需要任何账号凭据。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _mysCookie = string.Empty;

    /// <summary>
    /// 米游社查询用的 UID。留空时由 Cookie 自动推断。
    /// </summary>
    [ObservableProperty]
    private string _mysUid = string.Empty;

    /// <summary>
    /// 树脂达到该值时，定时任务里的「满树脂提醒 / 自动清体力」才会触发。
    /// </summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(20, 200, ErrorMessage = "树脂阈值必须在 20 到 200 之间。")]
    private int _resinAlertThreshold = 160;
}
