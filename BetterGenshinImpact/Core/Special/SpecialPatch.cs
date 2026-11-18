using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Core.Special;

/// <summary>
///     特别版的增量更新。
///     <para>
///         完整包 546 MB，走 GitHub 代理镜像常常只有几百 KB/s，一次升级要等十几分钟。
///         而一次发版真正变化的通常只有主程序 DLL 与前端产物，所以流水线额外产出一个
///         <c>patch-from-{旧版本}.zip</c>，只装变化的文件。客户端优先下它，通常几 MB 到几十 MB。
///     </para>
///     <para>
///         安装分两步：先解压到 <c>update/staging</c> 并逐文件校验，再让一个独立脚本在
///         BetterGI 退出后覆盖文件并重启。运行中的 exe/dll 被系统锁住，进程自己换不了自己。
///     </para>
///     <para>
///         <c>User</c> 目录永远不参与：配置、脚本、截图、地图缓存都在里面，热更新只替换
///         程序自己的文件。构建端不把 User 放进增量包，客户端解析清单时再剔除一次，
///         脚本覆盖前还会挡一次——三道都留着，是因为一旦漏了就是把用户配置换回默认值。
///     </para>
/// </summary>
public static class SpecialPatch
{
    /// <summary>增量包文件名里夹带的标记，后面跟起始版本。</summary>
    public const string FromMarker = "_patch-from-";

    /// <summary>用户数据目录，热更新从不触碰。</summary>
    private const string UserFolder = "User";

    private const string ManifestName = "manifest.json";
    private const string PendingName = "pending.json";
    private const string StagingName = "staging";

    /// <summary>
    ///     更新目录，位于程序目录下 <c>update</c>。放在程序目录而不是 %TEMP%，
    ///     是因为多数用户把 BetterGI 解压在普通目录，覆盖时不需要管理员权限。
    /// </summary>
    public static string Root => Path.Combine(Global.StartUpPath, "update");

    private static string Staging => Path.Combine(Root, StagingName);

    private static string Pending => Path.Combine(Root, PendingName);

    /// <summary>是否已经有一个等着重启应用的更新。</summary>
    public static bool IsPending => File.Exists(Pending);

    /// <summary>从增量包文件名里取起始版本，例如 <c>..._patch-from-0.67.0-special.6.zip</c>。</summary>
    public static string? FromVersionOf(string assetName)
    {
        int at = assetName.IndexOf(FromMarker, StringComparison.OrdinalIgnoreCase);
        if (at < 0 || !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return assetName[(at + FromMarker.Length)..^".zip".Length];
    }

    /// <summary>
    ///     解压增量包并校验。全部通过才写入 pending 标记，避免把半套文件当成可用更新。
    /// </summary>
    /// <param name="archivePath">已下载的 zip</param>
    /// <param name="logger">日志</param>
    /// <param name="cancellationToken">取消标记</param>
    public static async Task<(bool Ok, string Message)> StageAsync(
        string archivePath,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(Staging))
            {
                Directory.Delete(Staging, true);
            }

            Directory.CreateDirectory(Staging);
            await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, Staging, true), cancellationToken)
                .ConfigureAwait(false);

            string manifestPath = Path.Combine(Staging, ManifestName);
            if (!File.Exists(manifestPath))
            {
                return (false, "增量包里没有 manifest.json，可能是完整包或文件损坏。");
            }

            var manifest = ParseManifest(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
            if (manifest.Files is not { Count: > 0 })
            {
                return (false, "增量包清单为空。");
            }

            // 逐文件核对哈希：下载被中间层截断或替换过的话，这里会拦下来
            var missing = new List<string>();
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = ResolveStaged(file.Path);
                if (path is null || !File.Exists(path))
                {
                    missing.Add(file.Path);
                    continue;
                }

                string actual = await Sha256Async(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    missing.Add(file.Path);
                }
            }

            if (missing.Count > 0)
            {
                return (false, $"增量包校验失败（{missing.Count} 个文件不一致），请改下完整包。");
            }

            // 把清单一起搬到根目录，方便脚本读取要删除的文件
            File.Copy(manifestPath, Pending, true);

            logger.LogInformation("增量更新已就绪：{From} → {To}，{Count} 个文件",
                manifest.From, manifest.To, manifest.Files.Count);
            return (true, $"更新已下载并校验完成（{manifest.Files.Count} 个文件，{manifest.From} → {manifest.To}）。");
        }
        catch (OperationCanceledException)
        {
            return (false, "已取消。");
        }
        catch (Exception e)
        {
            logger.LogError(e, "解压增量包失败");
            return (false, "增量包解压失败：" + e.Message);
        }
    }

    /// <summary>
    ///     启动一个独立脚本：等 BetterGI 退出 → 备份 → 覆盖 → 删掉清单里标记为移除的文件 → 重新启动。
    ///     <para>
    ///         调用后当前进程需要自行退出，否则脚本会一直等下去。
    ///     </para>
    /// </summary>
    /// <returns>脚本是否成功拉起。失败时调用方应提示用户手动解压。</returns>
    public static bool StartApplyAndRestart(ILogger logger)
    {
        try
        {
            if (!File.Exists(Pending))
            {
                return false;
            }

            string script = Path.Combine(Path.GetTempPath(), $"bgi-update-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(script, BuildScript(), new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(startInfo);
            logger.LogInformation("更新脚本已启动，等待 BetterGI 退出来覆盖文件");
            return true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "启动更新脚本失败");
            return false;
        }
    }

    /// <summary>
    ///     读取待应用的清单。文件不存在或损坏时返回 null。
    /// </summary>
    public static SpecialPatchManifest? ReadPending()
    {
        try
        {
            return File.Exists(Pending) ? ParseManifest(File.ReadAllText(Pending)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     解析清单，同时容忍 <c>files</c> 是数组或单个对象。
    ///     <para>
    ///         PowerShell 的 ConvertTo-Json 遇到单元素数组会写成对象而不是数组，
    ///         只变了一个文件的版本正好会踩到，直接按 List 反序列化会得到 null。
    ///     </para>
    /// </summary>
    private static SpecialPatchManifest ParseManifest(string json)
    {
        var root = JObject.Parse(json);
        var manifest = new SpecialPatchManifest
        {
            From = root.Value<string>("from") ?? "",
            To = root.Value<string>("to") ?? "",
            Base = root.Value<string>("base") ?? "BetterGI",
        };

        foreach (var token in AsArray(root["files"]))
        {
            if (token.ToObject<SpecialPatchFile>() is not { Path.Length: > 0 } file)
            {
                continue;
            }

            // 老流水线生成的清单里可能夹着 User，这里直接丢掉，不参与校验也不参与覆盖
            if (IsUserPath(file.Path))
            {
                continue;
            }

            manifest.Files.Add(file);
        }

        foreach (var token in AsArray(root["removed"]))
        {
            if (token.Type == JTokenType.String && token.Value<string>() is { Length: > 0 } path && !IsUserPath(path))
            {
                manifest.Removed.Add(path);
            }
        }

        return manifest;
    }

    /// <summary>
    ///     是否是用户数据目录下的路径。反斜杠、前导斜杠、大小写都要兼容：
    ///     清单由 PowerShell 生成，路径分隔符与客户端不一致。
    /// </summary>
    private static bool IsUserPath(string relative)
    {
        string normalized = (relative ?? string.Empty).Replace('\\', '/').TrimStart('/');
        return normalized.Equals(UserFolder, StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith(UserFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把「数组或单个值」统一成可枚举的集合。</summary>
    private static IEnumerable<JToken> AsArray(JToken? token)
    {
        return token switch
        {
            JArray array => array,
            JObject or JValue => [token],
            _ => [],
        };
    }

    /// <summary>
    ///     清掉待应用的标记与暂存目录，用于「已经装上了」或用户放弃的场景。
    /// </summary>
    public static void ClearPending()
    {
        try
        {
            if (File.Exists(Pending))
            {
                File.Delete(Pending);
            }

            if (Directory.Exists(Staging))
            {
                Directory.Delete(Staging, true);
            }
        }
        catch
        {
            // 清理失败不影响启动
        }
    }

    /// <summary>
    ///     上一次更新脚本留下的结果。成功返回 null；失败返回原因，由调用方提示。
    /// </summary>
    public static string? ConsumeLastError()
    {
        string marker = Path.Combine(Root, "last-error.txt");
        try
        {
            if (!File.Exists(marker))
            {
                return null;
            }

            string text = File.ReadAllText(marker);
            File.Delete(marker);
            return text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     脚本正文。用绝对路径，避免工作目录不同导致覆盖错地方。
    /// </summary>
    private static string BuildScript()
    {
        string app = Global.StartUpPath.TrimEnd('\\');
        string stage = Staging;
        string root = Root;
        string pending = Pending;
        string exe = Path.Combine(app, "BetterGI.exe");
        string backup = Path.Combine(root, "backup");
        string log = Path.Combine(root, "update.log");
        string error = Path.Combine(root, "last-error.txt");

        return $$"""
                 $ErrorActionPreference = 'Stop'
                 $app = '{{app}}'
                 $stage = '{{stage}}'
                 $root = '{{root}}'
                 $pending = '{{pending}}'
                 $exe = '{{exe}}'
                 $backup = '{{backup}}'
                 $logFile = '{{log}}'
                 $errorFile = '{{error}}'

                 function Write-Log([string]$message) {
                     try {
                         Add-Content -Path $logFile -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message)
                     } catch { }
                 }

                 # User 是用户数据（配置、脚本、截图、地图缓存），热更新绝不碰。
                 # 清单里已经不含它，这里再挡一次：老版本流水线生成的包可能带着。
                 function Test-UserPath([string]$path) {
                     $p = $path.Replace('/', '\').TrimStart('\')
                     return $p -eq 'User' -or $p -like 'User\*'
                 }

                 try {
                     Write-Log '等待 BetterGI 退出'
                     # 分身实例也同名，等全部退出才不会撞上文件锁
                     $deadline = (Get-Date).AddMinutes(5)
                     while (Get-Process -Name 'BetterGI' -ErrorAction SilentlyContinue) {
                         if ((Get-Date) -gt $deadline) { throw '等待 BetterGI 退出超时' }
                         Start-Sleep -Milliseconds 500
                     }
                     Start-Sleep -Milliseconds 800

                     if (-not (Test-Path $pending)) { Write-Log '没有待应用的更新，退出'; exit 0 }
                     $manifest = Get-Content $pending -Raw | ConvertFrom-Json

                     # 覆盖前先备份，出错能回滚
                     if (Test-Path $backup) { Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }
                     New-Item -ItemType Directory $backup -Force | Out-Null
                     foreach ($f in $manifest.files) {
                         if (Test-UserPath $f.path) { continue }
                         $target = Join-Path $app $f.path
                         if (Test-Path $target) {
                             $save = Join-Path $backup $f.path
                             New-Item -ItemType Directory (Split-Path $save -Parent) -Force | Out-Null
                             Copy-Item $target $save -Force
                         }
                     }

                     Write-Log ("开始覆盖 {0} 个文件" -f $manifest.files.Count)
                     foreach ($f in $manifest.files) {
                         if (Test-UserPath $f.path) { Write-Log ("跳过用户数据：" + $f.path); continue }
                         $source = Join-Path $stage ("files/" + $f.path)
                         $target = Join-Path $app $f.path
                         New-Item -ItemType Directory (Split-Path $target -Parent) -Force | Out-Null
                         Copy-Item $source $target -Force
                     }

                     foreach ($rel in $manifest.removed) {
                         if (Test-UserPath $rel) { continue }
                         $target = Join-Path $app $rel
                         if (Test-Path $target) { Remove-Item $target -Force -ErrorAction SilentlyContinue }
                     }

                     Remove-Item $pending -Force -ErrorAction SilentlyContinue
                     Write-Log '更新完成，重新启动'
                     Start-Process $exe -WorkingDirectory $app
                     exit 0
                 }
                 catch {
                     Write-Log ("失败：" + $_.Exception.Message)
                     Set-Content -Path $errorFile -Value $_.Exception.Message -Encoding utf8
                     # 覆盖中途失败时把已备份的文件还回去，尽量留一个能启动的版本
                     try {
                         if (Test-Path $backup) {
                             Get-ChildItem $backup -Recurse -File | ForEach-Object {
                                 $rel = $_.FullName.Substring((Resolve-Path $backup).Path.Length).TrimStart('\')
                                 Copy-Item $_.FullName (Join-Path $app $rel) -Force
                             }
                         }
                     } catch { }
                     exit 1
                 }
                 """;
    }

    /// <summary>
    ///     把清单里的相对路径映射到 staging 内的绝对路径，并挡住 <c>..</c> 越界。
    /// </summary>
    private static string? ResolveStaged(string relative)
    {
        try
        {
            string baseDir = Path.GetFullPath(Path.Combine(Staging, "files"));
            string full = Path.GetFullPath(Path.Combine(baseDir, relative));
            return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>增量包清单，由 build-special.yml 生成。</summary>
    public sealed class SpecialPatchManifest
    {
        [JsonProperty("from")] public string From { get; set; } = "";

        [JsonProperty("to")] public string To { get; set; } = "";

        [JsonProperty("base")] public string Base { get; set; } = "BetterGI";

        [JsonProperty("files")] public List<SpecialPatchFile> Files { get; set; } = [];

        /// <summary>新版本里已经没有的文件，覆盖时需要删掉。</summary>
        [JsonProperty("removed")] public List<string> Removed { get; set; } = [];
    }

    /// <summary>清单里的一个文件。</summary>
    public sealed class SpecialPatchFile
    {
        [JsonProperty("path")] public string Path { get; set; } = "";

        [JsonProperty("size")] public long Size { get; set; }

        [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
    }
}
