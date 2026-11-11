using System;
using System.IO;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>路径解析结果。<see cref="InsideSandbox"/> 为 false 表示越界，需要单独授权。</summary>
public sealed record AgentPath(string FullPath, bool InsideSandbox);

/// <summary>
/// 本地助手的工作边界。默认沙箱就是软件自身目录，
/// 模型给出的相对路径一律相对沙箱解析，<c>..</c> 与符号链接都在规范化之后再判断，
/// 避免用 <c>..\..\Windows\System32</c> 这种写法绕过检查。
/// </summary>
public sealed class AgentSandbox
{
    private readonly string _root;

    public AgentSandbox(string? root = null)
    {
        var candidate = string.IsNullOrWhiteSpace(root) ? AppContext.BaseDirectory : root;
        _root = Normalize(candidate);
    }

    /// <summary>沙箱根目录（绝对路径，无尾部分隔符）。</summary>
    public string Root => _root;

    /// <summary>把模型给的路径变成绝对路径，并判断是否落在沙箱里。</summary>
    public AgentPath Resolve(string? path)
    {
        var raw = string.IsNullOrWhiteSpace(path) ? "." : path.Trim().Trim('"', '\'');
        raw = Environment.ExpandEnvironmentVariables(raw);
        var combined = Path.IsPathRooted(raw) ? raw : Path.Combine(_root, raw);
        var full = Normalize(combined);
        return new AgentPath(full, IsInside(full));
    }

    /// <summary>绝对路径是否在沙箱内（含根目录本身）。</summary>
    public bool IsInside(string fullPath)
    {
        var normalized = Normalize(fullPath);
        if (string.Equals(normalized, _root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>给模型与界面看的路径：沙箱内用相对路径，沙箱外保留绝对路径。</summary>
    public string Display(string fullPath)
    {
        if (!IsInside(fullPath))
        {
            return fullPath;
        }

        var relative = Path.GetRelativePath(_root, fullPath);
        return relative == "." ? "." : relative.Replace('\\', '/');
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 盘符根 "C:\" 去掉分隔符会变成 "C:"，那是个相对当前盘的写法，必须保留原样。
        return trimmed.Length <= 2 && trimmed.EndsWith(':') ? full : trimmed;
    }
}
