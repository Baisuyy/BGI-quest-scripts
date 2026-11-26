using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.Core.Plugins.Market;

/// <summary>
/// 插件市场的索引文件。用 <see cref="Global.ManifestJsonOptions" /> 读取，snake_case。
/// <para>
/// 格式见仓库里的 <c>plugin-market/README.md</c>。索引只描述「有哪些插件、从哪下载」，
/// 插件本身还是标准的 zip / nupkg 安装包，和从文件安装走同一条安装路径。
/// </para>
/// </summary>
public sealed class PluginMarketIndex
{
    /// <summary>
    /// 索引格式版本，目前为 1。比客户端支持的新时整份索引忽略。
    /// </summary>
    public int ManifestVersion { get; set; } = 1;

    /// <summary>
    /// 索引更新时间，仅供界面展示。
    /// </summary>
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>
    /// 收录的插件。
    /// </summary>
    public List<PluginMarketEntry> Plugins { get; set; } = [];
}

/// <summary>
/// 市场里的一个插件。字段与 <see cref="PluginManifest" /> 对齐，额外带下载地址与校验值。
/// </summary>
public sealed class PluginMarketEntry
{
    /// <summary>
    /// 插件标识，用于和已安装插件比对。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 市场里的最新版本，Semver。
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// 简介，列表里显示。
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 作者。
    /// </summary>
    public List<string> Authors { get; set; } = [];

    /// <summary>
    /// 项目主页，可为空。
    /// </summary>
    public string Homepage { get; set; } = string.Empty;

    /// <summary>
    /// 分类，第一个作为卡片上的标签显示。
    /// </summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>
    /// 搜索用的关键词，名称与简介里没写到的别名放这里。
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// 安装包地址，zip 或 nupkg。
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// 安装包的 SHA-256（十六进制，大小写不限）。必填：插件有管理员权限，下载完必须校验。
    /// </summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// 安装包字节数，仅用于显示与下载进度，0 表示未知。
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// 要求的最低 BetterGI 版本，空字符串表示不限。
    /// </summary>
    public string MinHostVersion { get; set; } = string.Empty;

    /// <summary>
    /// 这个版本的发布时间。
    /// </summary>
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>
    /// 索引条目是否可用：缺下载地址或校验值的条目直接丢弃，不显示成可安装。
    /// </summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Id)
                            && !string.IsNullOrWhiteSpace(Version)
                            && Uri.TryCreate(DownloadUrl, UriKind.Absolute, out _)
                            && Sha256.Length == 64;
}
