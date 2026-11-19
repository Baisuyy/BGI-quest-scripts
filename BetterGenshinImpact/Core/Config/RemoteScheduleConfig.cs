using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 定时任务的触发方式。
/// </summary>
public enum RemoteScheduleTrigger
{
    /// <summary>
    /// 每天固定时刻触发，例如 04:00 清一次日常。
    /// </summary>
    Time,

    /// <summary>
    /// 树脂达到阈值时触发，例如满 160 自动清体力。
    /// </summary>
    Resin
}

/// <summary>
/// 一条定时规则：什么条件下、跑哪个任务。
/// </summary>
[Serializable]
public partial class RemoteScheduleRule : ObservableObject
{
    /// <summary>
    /// 规则标识，新建时自动生成，用于在聊天里引用与删除。
    /// </summary>
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 规则名，聊天里显示用。
    /// </summary>
    [ObservableProperty]
    private string _name = "新规则";

    /// <summary>
    /// 是否启用。
    /// </summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>
    /// 触发方式。
    /// </summary>
    [ObservableProperty]
    private RemoteScheduleTrigger _trigger = RemoteScheduleTrigger.Time;

    /// <summary>
    /// 触发时刻，24 小时制 <c>HH:mm</c>，仅 <see cref="RemoteScheduleTrigger.Time"/> 使用。
    /// </summary>
    [ObservableProperty]
    private string _time = "04:00";

    /// <summary>
    /// 树脂阈值，仅 <see cref="RemoteScheduleTrigger.Resin"/> 使用。
    /// </summary>
    [ObservableProperty]
    private int _resinThreshold = 160;

    /// <summary>
    /// 触发的任务标识或内置技能标识，例如 <c>resin_plan</c>、<c>@resin</c>。
    /// </summary>
    [ObservableProperty]
    private string _action = "resin_plan";

    /// <summary>
    /// 动作参数，例如脚本名、角色序号；不需要时留空。
    /// </summary>
    [ObservableProperty]
    private string _actionArgs = string.Empty;

    /// <summary>
    /// 上次触发时间（本地时间），用于「每天只跑一次」的判断。
    /// </summary>
    [ObservableProperty]
    private DateTime? _lastRunAt;

    /// <summary>
    /// 上次触发的结果摘要，聊天查询时直接展示。
    /// </summary>
    [ObservableProperty]
    private string _lastResult = string.Empty;

    /// <summary>
    /// 一天内是否已经跑过。跨天自动失效，不需要手工清理。
    /// </summary>
    /// <param name="now">当前本地时间。</param>
    public bool HasRunOn(DateTime now) => LastRunAt is { } last && last.Date == now.Date;
}

/// <summary>
/// 定时任务配置：把「几点做什么」和「树脂到多少做什么」交给后台自己跑。
/// <para>
/// 只做调度，不重新实现任务：命中的动作仍然走独立任务执行服务，与手动点「开始」完全同一条路径，
/// 因此任务锁、日志、通知、截图回传都自动生效。
/// </para>
/// </summary>
[Serializable]
public partial class RemoteScheduleConfig : ObservableValidator
{
    /// <summary>
    /// 是否启用定时任务。
    /// </summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>
    /// 树脂检查间隔（秒）。树脂 8 分钟恢复 1 点，默认 5 分钟一查足够，且不频繁打扰游戏。
    /// </summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(60, 3600, ErrorMessage = "检查间隔必须在 60 到 3600 秒之间。")]
    private int _pollSeconds = 300;

    /// <summary>
    /// 全部规则。
    /// </summary>
    public ObservableCollection<RemoteScheduleRule> Rules { get; set; } = [];

    /// <summary>
    /// 上次轮询时间，仅用于日志与去抖。
    /// </summary>
    [ObservableProperty]
    private DateTime? _lastPollAt;
}
