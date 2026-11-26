using BetterGenshinImpact.Core.Plugins.Market;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 插件市场配置（特别版）。
/// <para>
/// 只有一个索引地址：换镜像、换自建源、指到本地目录都不用重新发版。
/// </para>
/// </summary>
public sealed partial class PluginMarketConfig : ObservableObject
{
    /// <summary>
    /// 索引地址，指向一份 index.json。
    /// </summary>
    [ObservableProperty]
    private string _indexUrl = PluginMarketClient.DefaultIndexUrl;
}
