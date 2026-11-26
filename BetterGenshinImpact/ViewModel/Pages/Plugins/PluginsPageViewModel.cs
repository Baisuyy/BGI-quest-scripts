using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Plugins.Hosting;
using BetterGenshinImpact.Core.Plugins.Market;
using BetterGenshinImpact.Core.Plugins.Pluggables;
using BetterGenshinImpact.View.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace BetterGenshinImpact.ViewModel.Pages.Plugins;

/// <summary>
/// 插件页：功能选择（各可插拔能力使用哪个实现）与已安装插件（启用、停用、安装、卸载）。
/// 设计见 Docs/design/plugin-system.md 第 13 节。
/// </summary>
public sealed partial class PluginsPageViewModel : ViewModel
{
    /// <summary>
    /// 插件宿主。
    /// </summary>
    private readonly PluginManager _manager;

    /// <summary>
    /// 可插拔能力注册表。
    /// </summary>
    private readonly PluggableRegistry _registry;

    /// <summary>
    /// 日志。
    /// </summary>
    private readonly ILogger<PluginsPageViewModel> _logger;

    /// <summary>
    /// 插件设置弹窗，交给每个插件项使用。
    /// </summary>
    private readonly IPluginSettingsDialogs _settingsDialogs;

    /// <summary>
    /// 市场客户端：拉索引、下载安装包。
    /// </summary>
    private readonly PluginMarketClient _market;

    /// <summary>
    /// 全局配置，读市场索引地址。
    /// </summary>
    private readonly AllConfig _config;

    /// <summary>
    /// 市场全量条目，过滤后的视图放在 <see cref="MarketItems" />。
    /// </summary>
    private readonly List<PluginMarketItemViewModel> _allMarket = [];

    /// <summary>
    /// 当前这一次市场操作的取消源，切页或点取消时用。
    /// </summary>
    private CancellationTokenSource? _marketCts;

    /// <summary>
    /// 市场是否已经成功加载过一次，避免每次进页面都打网络。
    /// </summary>
    private bool _marketLoaded;

    /// <summary>
    /// 按插件条目复用插件项，避免重复订阅条目的变化。
    /// </summary>
    private readonly Dictionary<PluginEntry, InstalledPluginItemViewModel> _items = [];

    /// <summary>
    /// 是否没有任何插件。
    /// </summary>
    [ObservableProperty]
    private bool _hasNoPlugins;

    /// <summary>
    /// 是否正在安装或卸载。
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// 创建插件页 ViewModel。
    /// </summary>
    public PluginsPageViewModel(PluginManager manager, PluggableRegistry registry, IPluginSettingsDialogs settingsDialogs,
        PluginMarketClient market, AllConfig config, ILogger<PluginsPageViewModel> logger)
    {
        _manager = manager;
        _registry = registry;
        _settingsDialogs = settingsDialogs;
        _market = market;
        _config = config;
        _logger = logger;
        _indexUrl = string.IsNullOrWhiteSpace(config.PluginMarketConfig.IndexUrl)
            ? PluginMarketClient.DefaultIndexUrl
            : config.PluginMarketConfig.IndexUrl;
        // 每行自己订阅注册表的变化
        Pluggables = new ObservableCollection<PluggableItemViewModel>(
            registry.All.Select(descriptor => new PluggableItemViewModel(registry, descriptor)));
        _manager.Plugins.CollectionChanged += (_, _) => RebuildPlugins();
        RebuildPlugins();
    }

    /// <summary>
    /// 功能选择：每项可插拔能力一行。
    /// </summary>
    public ObservableCollection<PluggableItemViewModel> Pluggables { get; }

    /// <summary>
    /// 已安装的插件。
    /// </summary>
    public ObservableCollection<InstalledPluginItemViewModel> Plugins { get; } = [];

    /// <summary>
    /// 是否可以安装、卸载插件（只有主实例可以）。
    /// </summary>
    public bool CanManagePackages => _manager.CanManagePackages;

    /// <summary>
    /// 进入插件页时重新扫描插件目录。
    /// </summary>
    public override void OnNavigatedTo()
    {
        Refresh();
        if (!_marketLoaded)
        {
            _ = LoadMarketAsync();
        }
    }

    // ------------------------------------------------------------------ 插件市场

    /// <summary>
    /// 市场里过滤后的插件。
    /// </summary>
    public ObservableCollection<PluginMarketItemViewModel> MarketItems { get; } = [];

    /// <summary>
    /// 分类下拉选项，第一项是「全部」。
    /// </summary>
    public ObservableCollection<string> MarketCategories { get; } = ["全部"];

    /// <summary>
    /// 正在拉索引。
    /// </summary>
    [ObservableProperty]
    private bool _isMarketLoading;

    /// <summary>
    /// 当前页签：0 市场，1 已安装，2 功能选择。
    /// </summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// 正在下载或安装某个插件。
    /// </summary>
    [ObservableProperty]
    private bool _isMarketInstalling;

    /// <summary>
    /// 搜索关键词。
    /// </summary>
    [ObservableProperty]
    private string _marketKeyword = string.Empty;

    /// <summary>
    /// 选中的分类。
    /// </summary>
    [ObservableProperty]
    private string _selectedCategory = "全部";

    /// <summary>
    /// 加载失败的原因，非空时界面显示错误条。
    /// </summary>
    [ObservableProperty]
    private string _marketError = string.Empty;

    /// <summary>
    /// 有没有加载错误，界面绑这个比绑字符串省一个转换器。
    /// </summary>
    [ObservableProperty]
    private bool _hasMarketError;

    /// <summary>
    /// 过滤后没有任何条目。
    /// </summary>
    [ObservableProperty]
    private bool _isMarketEmpty;

    /// <summary>
    /// 市场概况：共几个插件、索引什么时候更新。
    /// </summary>
    [ObservableProperty]
    private string _marketSummary = string.Empty;

    /// <summary>
    /// 索引地址，改完点「保存并刷新」。
    /// </summary>
    [ObservableProperty]
    private string _indexUrl = string.Empty;

    partial void OnMarketKeywordChanged(string value) => ApplyMarketFilter();

    partial void OnSelectedCategoryChanged(string value) => ApplyMarketFilter();

    partial void OnMarketErrorChanged(string value) => HasMarketError = !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// 拉取索引并重建卡片。网络失败只留错误条，不弹窗——进页面就弹窗太吵。
    /// </summary>
    [RelayCommand]
    private async Task LoadMarketAsync()
    {
        if (IsMarketLoading)
        {
            return;
        }

        IndexUrl ??= string.Empty;
        var url = string.IsNullOrWhiteSpace(IndexUrl) ? PluginMarketClient.DefaultIndexUrl : IndexUrl.Trim();
        IsMarketLoading = true;
        MarketError = string.Empty;
        try
        {
            var index = await _market.LoadIndexAsync(url, CancellationToken.None);
            _allMarket.Clear();
            MarketCategories.Clear();
            MarketCategories.Add("全部");
            foreach (var entry in index.Plugins)
            {
                _allMarket.Add(new PluginMarketItemViewModel(entry, _manager, InstallFromMarketAsync));
                foreach (var category in entry.Categories.Where(c => !string.IsNullOrWhiteSpace(c)))
                {
                    if (!MarketCategories.Contains(category))
                    {
                        MarketCategories.Add(category);
                    }
                }
            }

            _marketLoaded = true;
            MarketSummary = string.IsNullOrWhiteSpace(index.UpdatedAt)
                ? $"共 {_allMarket.Count} 个插件"
                : $"共 {_allMarket.Count} 个插件 · 索引更新于 {index.UpdatedAt}";
            if (!MarketCategories.Contains(SelectedCategory))
            {
                SelectedCategory = "全部";
            }

            ApplyMarketFilter();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException
                                      or System.Text.Json.JsonException or IOException)
        {
            MarketError = e is TaskCanceledException ? "读取插件索引超时，检查网络后重试。" : e.Message;
            _logger.LogWarning(e, "读取插件索引失败：{Url}", url);
        }
        finally
        {
            IsMarketLoading = false;
        }
    }

    /// <summary>
    /// 保存索引地址并重新拉取。
    /// </summary>
    [RelayCommand]
    private async Task SaveIndexUrlAsync()
    {
        var url = (IndexUrl ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(url))
        {
            url = PluginMarketClient.DefaultIndexUrl;
            IndexUrl = url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            MarketError = "索引地址需要是 http 或 https 开头的完整网址。";
            return;
        }

        _config.PluginMarketConfig.IndexUrl = url;
        _marketLoaded = false;
        await LoadMarketAsync();
    }

    /// <summary>
    /// 恢复默认索引地址。
    /// </summary>
    [RelayCommand]
    private async Task ResetIndexUrlAsync()
    {
        IndexUrl = PluginMarketClient.DefaultIndexUrl;
        await SaveIndexUrlAsync();
    }

    /// <summary>
    /// 从市场安装或更新：下载 → 校验哈希 → 交给插件宿主安装。
    /// <para>
    /// 走的是和「从文件安装」完全相同的 InstallAsync，唯一多出来的是下载和哈希校验。
    /// </para>
    /// </summary>
    private async Task InstallFromMarketAsync(PluginMarketItemViewModel item)
    {
        if (IsMarketInstalling || item.IsBusy)
        {
            return;
        }

        IsMarketInstalling = true;
        item.IsBusy = true;
        item.CanInstall = false;
        item.Progress = 0;
        item.BusyText = "下载中 0%";
        _marketCts = new CancellationTokenSource();
        string? file = null;
        try
        {
            var progress = new Progress<double>(value =>
            {
                item.Progress = value;
                item.BusyText = $"下载中 {value * 100:F0}%";
            });

            file = await _market.DownloadAsync(item.Entry, progress, _marketCts.Token);
            item.BusyText = "校验并安装…";

            var package = _manager.InspectPackage(file);
            if (package.CompatibilityError is { } error)
            {
                await ThemedMessageBox.ErrorAsync($"插件“{package.Manifest.Name}”无法在当前版本中使用：{error}", "插件市场");
                return;
            }

            if (await ThemedMessageBox.QuestionAsync(BuildInstallConfirmation(package), "从市场安装") != MessageBoxResult.Yes)
            {
                return;
            }

            var result = await _manager.InstallAsync(package);
            if (result.RequiresRestart)
            {
                await ThemedMessageBox.InformationAsync("该插件已加载，新版本将在重启 BetterGI 后生效。", "插件市场");
            }
            else if (result.Entry.State == PluginState.Disabled
                     && await ThemedMessageBox.QuestionAsync($"插件“{package.Manifest.Name}”已安装，是否立即启用？", "插件市场") == MessageBoxResult.Yes
                     && !_manager.Enable(result.Entry))
            {
                await ThemedMessageBox.ErrorAsync($"启用插件“{package.Manifest.Name}”失败：{result.Entry.Error}", "插件市场");
            }
        }
        catch (OperationCanceledException)
        {
            // 用户点了取消，什么都不用说
        }
        catch (Exception e)
        {
            _logger.LogError(e, "从市场安装插件 {Id} 失败", item.Id);
            await ThemedMessageBox.ErrorAsync($"安装插件失败：{e.Message}", "插件市场");
        }
        finally
        {
            PluginMarketClient.TryDelete(file);
            _marketCts?.Dispose();
            _marketCts = null;
            item.IsBusy = false;
            item.BusyText = string.Empty;
            item.Progress = 0;
            IsMarketInstalling = false;
            // 装完立刻改卡片状态，不用等下次进页面
            foreach (var card in _allMarket)
            {
                card.Refresh();
            }
        }
    }

    /// <summary>
    /// 取消正在进行的下载。
    /// </summary>
    [RelayCommand]
    private void CancelMarketInstall() => _marketCts?.Cancel();

    /// <summary>
    /// 打开插件主页。
    /// </summary>
    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "打开链接失败：{Url}", url);
        }
    }

    /// <summary>
    /// 按关键词和分类过滤市场卡片。
    /// </summary>
    private void ApplyMarketFilter()
    {
        MarketItems.Clear();
        foreach (var item in _allMarket)
        {
            if (SelectedCategory is not null && SelectedCategory != "全部" && item.Category != SelectedCategory)
            {
                continue;
            }

            if (item.Matches(MarketKeyword ?? string.Empty))
            {
                MarketItems.Add(item);
            }
        }

        IsMarketEmpty = MarketItems.Count == 0;
    }


    /// <summary>
    /// 重新扫描插件目录，并刷新功能选择。
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        _manager.Refresh();
        foreach (var pluggable in Pluggables)
        {
            pluggable.Refresh();
        }

        // 插件列表变了，市场卡片的「已安装 / 可更新」也要跟着变
        foreach (var card in _allMarket)
        {
            card.Refresh();
        }
    }

    /// <summary>
    /// 离开插件页时取消还在进行的下载，别让它在后台偷偷跑完。
    /// </summary>
    public override void OnNavigatedFrom()
    {
        _marketCts?.Cancel();
    }

    /// <summary>
    /// 打开插件目录，不存在时创建。
    /// </summary>
    [RelayCommand]
    private void OpenPluginsDirectory()
    {
        try
        {
            Directory.CreateDirectory(_manager.PluginsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_manager.PluginsDirectory}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            _ = ThemedMessageBox.ErrorAsync($"无法打开插件目录：{e.Message}", "插件");
        }
    }

    /// <summary>
    /// 从本地 zip 或 nupkg 安装插件：检查安装包、确认、安装，新插件询问是否立即启用。
    /// </summary>
    [RelayCommand]
    private async Task InstallFromFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择插件安装包",
            Filter = "BetterGI 插件 (*.zip;*.nupkg)|*.zip;*.nupkg",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 1. 检查安装包
        PluginPackage package;
        try
        {
            package = _manager.InspectPackage(dialog.FileName);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await ThemedMessageBox.ErrorAsync($"无法读取安装包：{e.Message}", "安装插件");
            return;
        }

        var manifest = package.Manifest;
        if (package.CompatibilityError is { } error)
        {
            await ThemedMessageBox.ErrorAsync($"插件“{manifest.Name}”无法在当前版本中使用：{error}", "安装插件");
            return;
        }

        // 2. 确认
        if (await ThemedMessageBox.QuestionAsync(BuildInstallConfirmation(package), "安装插件") != MessageBoxResult.Yes)
        {
            return;
        }

        // 3. 安装；同标识的插件正在使用时，新版本在下次启动时生效
        IsBusy = true;
        try
        {
            var result = await _manager.InstallAsync(package);
            if (result.RequiresRestart)
            {
                await ThemedMessageBox.InformationAsync("该插件已加载，新版本将在重启 BetterGI 后生效。", "安装插件");
            }
            else if (result.Entry.State == PluginState.Disabled
                     && await ThemedMessageBox.QuestionAsync($"插件“{manifest.Name}”已安装，是否立即启用？", "安装插件") == MessageBoxResult.Yes
                     && !_manager.Enable(result.Entry))
            {
                await ThemedMessageBox.ErrorAsync($"启用插件“{manifest.Name}”失败：{result.Entry.Error}", "启用插件");
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "安装插件 {Id} 失败", manifest.Id);
            await ThemedMessageBox.ErrorAsync($"安装插件失败：{e.Message}", "安装插件");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 卸载插件：确认后停用并删除，已加载的插件在下次启动时删除文件。插件数据保留。
    /// </summary>
    [RelayCommand]
    private async Task UninstallAsync(InstalledPluginItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var dataDirectory = Global.Absolute(Path.Combine("User", "PluginData", item.Id));
        if (await ThemedMessageBox.QuestionAsync(
                $"确定卸载插件“{item.Name}”吗？\n\n插件的数据保留在 {dataDirectory}，重新安装后恢复。",
                "卸载插件") != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (await _manager.UninstallAsync(item.Entry))
            {
                await ThemedMessageBox.InformationAsync("插件已停用，文件将在下次启动 BetterGI 时删除。", "卸载插件");
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "卸载插件 {Id} 失败", item.Id);
            await ThemedMessageBox.ErrorAsync($"卸载插件失败：{e.Message}", "卸载插件");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 安装确认的内容：插件信息、版本变化和权限提示。
    /// </summary>
    private string BuildInstallConfirmation(PluginPackage package)
    {
        var manifest = package.Manifest;
        var installed = _manager.Find(manifest.Id);
        var text = new StringBuilder();
        text.AppendLine($"名称：{manifest.Name}");
        text.AppendLine($"标识：{manifest.Id}");
        text.AppendLine(installed is null
            ? $"版本：{manifest.Version}"
            : $"版本：{installed.Manifest.Version} → {manifest.Version}");
        if (manifest.Authors.Count > 0)
        {
            text.AppendLine($"作者：{string.Join("、", manifest.Authors)}");
        }

        text.AppendLine($"大小：{package.TotalBytes / 1024d / 1024d:F1} MB，{package.FileCount} 个文件");
        if (installed is not null && Global.IsNewVersion(manifest.Version, installed.Manifest.Version))
        {
            text.AppendLine("注意：安装包的版本低于已安装的版本。");
        }

        text.AppendLine();
        text.Append("插件拥有与 BetterGI 相同的系统权限（管理员），请只安装可信来源的插件。是否安装？");
        return text.ToString();
    }

    /// <summary>
    /// 按插件宿主的列表同步插件项：复用已有项，移除的项停止跟踪。
    /// </summary>
    private void RebuildPlugins()
    {
        foreach (var removed in _items.Keys.Except(_manager.Plugins).ToList())
        {
            _items[removed].Detach();
            _items.Remove(removed);
        }

        Plugins.Clear();
        foreach (var entry in _manager.Plugins)
        {
            if (!_items.TryGetValue(entry, out var item))
            {
                item = new InstalledPluginItemViewModel(entry, _manager, GetPluggableName, _settingsDialogs);
                _items[entry] = item;
            }

            Plugins.Add(item);
        }

        HasNoPlugins = Plugins.Count == 0;
    }

    /// <summary>
    /// 按可插拔能力标识取显示名称。
    /// </summary>
    private string GetPluggableName(string pluggableId)
        => _registry.All.FirstOrDefault(d => d.Id == pluggableId)?.DisplayName ?? pluggableId;
}
