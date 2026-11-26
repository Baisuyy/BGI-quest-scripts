using System;
using System.Linq;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Plugins.Hosting;
using BetterGenshinImpact.Core.Plugins.Market;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterGenshinImpact.ViewModel.Pages.Plugins;

/// <summary>
/// 市场里的一张插件卡片。
/// <para>
/// 卡片上要回答三个问题：装没装、能不能装、装了会变成什么版本。
/// 这三件事都从 <see cref="PluginManager" /> 的已装列表现算，不另存状态——
/// 从文件装了一个同 id 的插件，市场里的卡片要立刻跟着变。
/// </para>
/// </summary>
public sealed partial class PluginMarketItemViewModel : ObservableObject
{
    private readonly PluginManager _manager;
    private readonly Func<PluginMarketItemViewModel, Task> _install;

    /// <summary>
    /// 创建卡片。
    /// </summary>
    /// <param name="entry">索引里的条目</param>
    /// <param name="manager">插件宿主，用来查是否已装</param>
    /// <param name="install">点击安装/更新时执行的动作，由页面统一处理下载与确认</param>
    public PluginMarketItemViewModel(PluginMarketEntry entry, PluginManager manager,
        Func<PluginMarketItemViewModel, Task> install)
    {
        Entry = entry;
        _manager = manager;
        _install = install;
        Refresh();
    }

    /// <summary>
    /// 索引条目。
    /// </summary>
    public PluginMarketEntry Entry { get; }

    /// <summary>
    /// 插件标识。
    /// </summary>
    public string Id => Entry.Id;

    /// <summary>
    /// 显示名称。
    /// </summary>
    public string Name => string.IsNullOrWhiteSpace(Entry.Name) ? Entry.Id : Entry.Name;

    /// <summary>
    /// 市场版本。
    /// </summary>
    public string Version => Entry.Version;

    /// <summary>
    /// 简介。
    /// </summary>
    public string Description => Entry.Description;

    /// <summary>
    /// 作者，显示成「张三、李四」。
    /// </summary>
    public string AuthorsText => Entry.Authors.Count > 0 ? string.Join("、", Entry.Authors) : "未署名";

    /// <summary>
    /// 第一个分类，作为卡片右上角的标签。
    /// </summary>
    public string Category => Entry.Categories.FirstOrDefault() ?? "插件";

    /// <summary>
    /// 是否有主页。
    /// </summary>
    public bool HasHomepage => !string.IsNullOrWhiteSpace(Entry.Homepage);

    /// <summary>
    /// 发布时间，只保留日期部分。
    /// </summary>
    public string UpdatedText => string.IsNullOrWhiteSpace(Entry.UpdatedAt)
        ? string.Empty
        : Entry.UpdatedAt.Length >= 10 ? Entry.UpdatedAt[..10] : Entry.UpdatedAt;

    /// <summary>
    /// 当前本机装的版本，空字符串表示没装。
    /// </summary>
    [ObservableProperty]
    private string _installedVersion = string.Empty;

    /// <summary>
    /// 状态文案：未安装、已是最新、可更新到 x、要求更高版本。
    /// </summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// 状态是否为「需要注意」，界面用警示色显示。
    /// </summary>
    [ObservableProperty]
    private bool _isWarning;

    /// <summary>
    /// 状态是否为「已就绪」，界面用强调色显示。
    /// </summary>
    [ObservableProperty]
    private bool _isInstalled;

    /// <summary>
    /// 按钮文字：安装 / 更新 / 重新安装。
    /// </summary>
    [ObservableProperty]
    private string _actionText = "安装";

    /// <summary>
    /// 按钮是否可用。
    /// </summary>
    [ObservableProperty]
    private bool _canInstall;

    /// <summary>
    /// 正在下载或安装。
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// 进度 0~1。
    /// </summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>
    /// 进度文案：下载中 45%，安装中等。
    /// </summary>
    [ObservableProperty]
    private string _busyText = string.Empty;

    /// <summary>
    /// 搜索用：名称、标识、简介、作者、分类、标签拼在一起。
    /// </summary>
    public bool Matches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        var haystack = string.Join('\n',
            Name, Entry.Id, Entry.Description, AuthorsText, Category, string.Join(' ', Entry.Tags));
        return keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 按当前已装状态重算文案与按钮。
    /// </summary>
    public void Refresh()
    {
        var installed = _manager.Find(Entry.Id);
        InstalledVersion = installed?.Manifest.Version ?? string.Empty;
        IsInstalled = installed is not null;

        if (!string.IsNullOrWhiteSpace(Entry.MinHostVersion) && !Global.IsRequirementMet(Entry.MinHostVersion))
        {
            StatusText = $"需要 BetterGI {Entry.MinHostVersion} 或更高";
            IsWarning = true;
            CanInstall = false;
            ActionText = "不可安装";
            return;
        }

        IsWarning = false;
        CanInstall = _manager.CanManagePackages;

        if (installed is null)
        {
            StatusText = "未安装";
            ActionText = "安装";
            return;
        }

        if (Global.IsNewVersion(installed.Manifest.Version, Entry.Version))
        {
            StatusText = $"可更新 {installed.Manifest.Version} → {Entry.Version}";
            ActionText = "更新";
            return;
        }

        StatusText = $"已安装 {installed.Manifest.Version}";
        ActionText = Global.IsNewVersion(Entry.Version, installed.Manifest.Version) ? "降级安装" : "重新安装";
        if (ActionText == "降级安装")
        {
            IsWarning = true;
        }
    }

    /// <summary>
    /// 安装 / 更新。
    /// </summary>
    [RelayCommand]
    private Task InstallAsync() => _install(this);
}
