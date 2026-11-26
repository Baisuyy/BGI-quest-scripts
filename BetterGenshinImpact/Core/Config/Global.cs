using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Semver;

namespace BetterGenshinImpact.Core.Config;

public class Global
{
    public static string Version { get; } = Assembly.GetEntryAssembly()?.
        GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.
        InformationalVersion!;

    public static string StartUpPath { get; set; } = AppContext.BaseDirectory;

    public static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string Absolute(string relativePath)
    {
        return Path.Combine(StartUpPath, relativePath);
    }

    public static string ScriptPath()
    {
        return Absolute("User\\JsScript");
    }

    public static string? ReadAllTextIfExist(string relativePath)
    {
        var path = Absolute(relativePath);
        if (File.Exists(path)) return File.ReadAllText(path);
        return null;
    }

    /// <summary>
    ///     新获取到的版本号与当前版本号比较，判断是否为新版本
    /// </summary>
    /// <param name="currentVersion">新获取到的版本</param>
    /// <returns></returns>
    public static bool IsNewVersion(string currentVersion)
    {
        return IsNewVersion(Version, currentVersion);
    }

    /// <summary>
    ///     新获取到的版本号与当前版本号比较，判断是否为新版本
    /// </summary>
    /// <param name="oldVersion">老版本</param>
    /// <param name="currentVersion">新获取到的版本</param>
    /// <returns>是否需要更新</returns>
    public static bool IsNewVersion(string oldVersion, string currentVersion)
    {
        try
        {
            var oldVersionX = SemVersion.Parse(oldVersion);
            var currentVersionX = SemVersion.Parse(currentVersion);

            if (currentVersionX.CompareSortOrderTo(oldVersionX) > 0)
                // 需要更新
                return true;
        }
        catch
        {
            ///
        }

        // 不需要更新
        return false;
    }

    /// <summary>
    ///     脚本 / 路线里写的 bgiVersion 是「至少要这个版本」，不是「必须精确等于」。
    ///     <para>
    ///         特别版版本号是 0.67.0-special.7.7.3 这种：SemVer 把 "-" 后面当预发布，
    ///         排序在 0.67.0 之前，要求 0.67.0 的路线在特别版上会全被判成「版本不够」。
    ///         特别版是在该内核版本上继续开发，比较时去掉 -special 段即可。
    ///     </para>
    ///     <para>
    ///         只用于「要求的最低版本」，不要拿去比较两个特别版之间的新旧——
    ///         那会把 0.67.0-special.8 和 0.67.0-special.7.7.3 看成一样。
    ///     </para>
    /// </summary>
    /// <param name="requiredVersion">脚本或路线要求的最低版本</param>
    /// <returns>当前版本是否满足要求；版本号解析失败时放行</returns>
    public static bool IsRequirementMet(string requiredVersion)
    {
        try
        {
            var required = SemVersion.Parse(StripSpecialSuffix(requiredVersion));
            var current = SemVersion.Parse(StripSpecialSuffix(Version));
            return required.CompareSortOrderTo(current) <= 0;
        }
        catch
        {
            // 写错的版本号不该让脚本直接不可用，交给其它校验
            return true;
        }
    }

    /// <summary>
    /// 去掉特别版的 -special.x 后缀，只留内核版本。
    /// <para>
    /// SemVer 里 <c>0.67.0-special.7.7.3</c> 的优先级**低于** <c>0.67.0</c>，
    /// 直接比会被判成「特别版比同版本的官方版还老」，脚本的 bgi_version 与插件的 sdk_version 都会误判。
    /// </para>
    /// </summary>
    public static string StripSpecialSuffix(string version)
    {
        var index = version.IndexOf("-special", StringComparison.OrdinalIgnoreCase);
        return index > 0 ? version[..index] : version;
    }

    public static void WriteAllText(string relativePath, string blackListJson)
    {
        var path = Absolute(relativePath);
        File.WriteAllText(path, blackListJson);
    }
}
