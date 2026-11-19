using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service;

namespace BetterGenshinImpact.Core.Special;

/// <summary>日志上传的三种状态。</summary>
public enum LogUploadMode
{
    /// <summary>完全不上传。选它就不再有任何出站请求。</summary>
    Off = 0,

    /// <summary>只在用户点「上传当前日志」时上传。默认值。</summary>
    Manual = 1,

    /// <summary>出错和正常退出时自动上传，不用用户动手。</summary>
    Auto = 2,
}

/// <summary>一次上传的结果。</summary>
public sealed record LogUploadResult(bool Ok, string Message, string? Id = null);

/// <summary>
///     把当次日志脱敏、压缩、签名后发到自建接收端。
///     <para>
///         设计上的几条硬规矩：
///         1. 上传前一定过 <see cref="Redact" />，账号、Cookie、token 不出去；
///         2. 任何异常都在内部吞掉，只写一条 WRN，绝不弹窗、绝不影响任务；
///         3. 默认 <see cref="LogUploadMode.Manual" />，用户不点就不发；
///         4. 只取日志尾部 8MB，挂机一整天的日志没必要整份搬走。
///     </para>
/// </summary>
public static class LogUploader
{
    /// <summary>默认接收地址。域名解析直连源站，不走 CDN。</summary>
    public const string DefaultEndpoint = "https://log.kelibb.top/loghub";

    /// <summary>签名路径。要与服务端的 base_path + API 拼接结果完全一致，改动要同步。</summary>
    private const string SignPath = "/loghub/api/v1/logs";

    private const string SignScheme = "BGI-LOG1";

    // 内置密钥：它随二进制分发，藏不住，所以服务端那边靠配额、限流、内容校验兜底。
    // 真要换钥匙，改这两行 + 服务端建新 key 即可，老版本会收到 401 并安静失败。
    private const string KeyId = "bgi_9d8723790a0d";
    private const string KeySecret = "MAPeIZTYPFMGViccywPjtVx1fPr14SxDJZxfbFGfFm8";

    /// <summary>单次最多带多少日志（脱敏前的字节数）。</summary>
    private const long MaxTailBytes = 8L * 1024 * 1024;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    /// <summary>串行化上传：手动连点、自动与退出同时触发时，只跑一份。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>保护 <see cref="_lastAuto" /> 的普通对象锁，别拿上面的信号量当锁用。</summary>
    private static readonly object AutoLock = new();

    private static DateTime _lastAuto = DateTime.MinValue;

    /// <summary>当前生效的上传模式。</summary>
    public static LogUploadMode Mode
    {
        get
        {
            try
            {
                return ConfigService.Config?.CommonConfig.LogUploadMode ?? LogUploadMode.Manual;
            }
            catch
            {
                return LogUploadMode.Manual;
            }
        }
    }

    /// <summary>当前生效的接收地址，末尾不带斜杠。</summary>
    public static string Endpoint
    {
        get
        {
            string url;
            try
            {
                url = ConfigService.Config?.CommonConfig.LogUploadUrl ?? string.Empty;
            }
            catch
            {
                url = string.Empty;
            }

            url = (url ?? string.Empty).Trim().TrimEnd('/');
            return url.Length == 0 ? DefaultEndpoint : url;
        }
    }

    /// <summary>
    ///     上传最新一份日志。失败返回 Ok=false，不抛异常。
    /// </summary>
    /// <param name="reason">触发原因，写进服务端记录的 instance 前缀，方便对账。</param>
    public static async Task<LogUploadResult> UploadLatestAsync(
        string reason, CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
        {
            return new LogUploadResult(false, "上一次上传还没结束");
        }

        try
        {
            var file = LatestLogFile();
            if (file == null)
            {
                return new LogUploadResult(false, "没找到日志文件");
            }

            // 读 8MB、跑七条正则、再压缩，全是同步 CPU 活。放进线程池，
            // 否则手动上传时界面会僵住一两秒。
            byte[]? payload = await Task.Run<byte[]?>(() =>
            {
                string text = ReadTail(file, MaxTailBytes);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                return Gzip(Encoding.UTF8.GetBytes(Redact(text)));
            }, ct).ConfigureAwait(false);

            if (payload == null)
            {
                return new LogUploadResult(false, "日志是空的");
            }

            return await PostAsync(payload, file.Name, reason, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "日志上传失败");
            return new LogUploadResult(false, "上传失败：" + e.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    ///     自动模式下的上传入口：出错或退出时调用。
    ///     不等待、不抛异常，10 分钟内只发一次，避免崩溃循环把服务端刷满。
    /// </summary>
    public static void TryAutoUpload(string reason)
    {
        if (Mode != LogUploadMode.Auto)
        {
            return;
        }

        lock (AutoLock)
        {
            if (DateTime.Now - _lastAuto < TimeSpan.FromMinutes(10))
            {
                return;
            }

            _lastAuto = DateTime.Now;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // 退出时也要给足时间：网络慢的时候 15 秒基本够 8MB 以内。
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var r = await UploadLatestAsync(reason, cts.Token).ConfigureAwait(false);
                if (r.Ok)
                {
                    Serilog.Log.Information("日志已上传，编号 {Id}", r.Id);
                }
                else
                {
                    Serilog.Log.Warning("日志上传未完成：{Message}", r.Message);
                }
            }
            catch (Exception e)
            {
                Serilog.Log.Debug(e, "自动上传日志失败");
            }
        });
    }

    /// <summary>
    ///     退出前的最后一把上传。只在 <see cref="LogUploadMode.Auto" /> 下动作。
    ///     <para>
    ///         退出路径上不能无限等：给一个时间预算，到点就放弃。进程马上要结束了，
    ///         没传完的日志留在本地，下次出错还会再传一遍最新的。
    ///     </para>
    /// </summary>
    public static async Task UploadBeforeExitAsync(TimeSpan budget)
    {
        if (Mode != LogUploadMode.Auto)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(budget);
            var r = await UploadLatestAsync("exit", cts.Token).ConfigureAwait(false);
            if (!r.Ok)
            {
                Serilog.Log.Debug("退出时未上传日志：{Message}", r.Message);
            }
        }
        catch (Exception e)
        {
            Serilog.Log.Debug(e, "退出时上传日志失败");
        }
    }

    // -- 网络 --------------------------------------------------------------- //

    private static async Task<LogUploadResult> PostAsync(
        byte[] payload, string fileName, string reason, CancellationToken ct)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        string sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        string canonical = string.Join("\n", SignScheme, "POST", SignPath,
            ts.ToString(CultureInfo.InvariantCulture), nonce, sha);
        string signature;
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(KeySecret)))
        {
            signature = Convert.ToHexString(
                hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

        string url = $"{Endpoint}/api/v1/logs"
                     + $"?name={Uri.EscapeDataString(SafeName(fileName))}"
                     + "&app=bgi-special"
                     + $"&version={Uri.EscapeDataString(Global.Version ?? "unknown")}"
                     + $"&kind=log"
                     + $"&instance={Uri.EscapeDataString(InstanceTag(reason))}";

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(payload),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentEncoding.Add("gzip");
        req.Headers.Add("X-BGI-Key", KeyId);
        req.Headers.Add("X-BGI-Timestamp", ts.ToString(CultureInfo.InvariantCulture));
        req.Headers.Add("X-BGI-Nonce", nonce);
        req.Headers.Add("X-BGI-Signature", signature);
        req.Headers.UserAgent.ParseAdd($"BetterGI/{Global.Version ?? "0"} (special; loghub-client)");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            string msg = TryReadError(body) ?? $"HTTP {(int)resp.StatusCode}";
            return new LogUploadResult(false, msg);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            string? id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            return new LogUploadResult(true, "已上传", id);
        }
        catch
        {
            return new LogUploadResult(true, "已上传");
        }
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg))
            {
                return msg.GetString();
            }
        }
        catch
        {
            // 不是 JSON 就原样截一段回去
        }

        return string.IsNullOrWhiteSpace(body) ? null : body[..Math.Min(body.Length, 120)];
    }

    private static string InstanceTag(string reason)
    {
        // 只用机器名的哈希：能区分「是不是同一台机器」，但不把机器名本身传出去。
        string raw = Environment.MachineName ?? "pc";
        string h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..6];
        string tag = string.IsNullOrWhiteSpace(reason) ? "auto" : reason.Trim();
        return $"{tag}-{h}";
    }

    private static string SafeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        }

        return sb.Length == 0 ? "log" : sb.ToString();
    }

    // -- 文件 --------------------------------------------------------------- //

    private static FileInfo? LatestLogFile()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "log");
        if (!Directory.Exists(folder))
        {
            return null;
        }

        return new DirectoryInfo(folder)
            .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();
    }

    /// <summary>
    ///     读文件尾部。日志正被 Serilog 写着，必须用 FileShare.ReadWrite 打开，
    ///     否则整点滚动时会撞上「文件被另一进程占用」。
    /// </summary>
    private static string ReadTail(FileInfo file, long maxBytes)
    {
        using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);

        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        byte[] buf = ms.ToArray();

        // 从中间截断时，第一行多半是半截的，丢掉
        int skip = 0;
        if (start > 0)
        {
            int nl = Array.IndexOf(buf, (byte)'\n');
            skip = nl >= 0 ? nl + 1 : 0;
        }

        return Encoding.UTF8.GetString(buf, skip, buf.Length - skip);
    }

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(data, 0, data.Length);
        }

        return ms.ToArray();
    }

    // -- 脱敏 --------------------------------------------------------------- //

    /// <summary>
    ///     把可能指向具体个人的东西替换掉。宁可多遮，也不要让用户的账号信息
    ///     出现在别人的机器上——日志上传是「默认关、开了也脱敏」，这条不能破。
    /// </summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // 顺序有讲究：先吃 Bearer 和裸 JWT，再吃 key=value。
        // 反过来的话，「Authorization: Bearer xxx」会被 key=value 规则整段吃掉，
        // 只剩一个裸令牌留在行里。
        text = BearerRe.Replace(text, "Bearer ***");
        text = JwtRe.Replace(text, "***");
        text = SecretPairRe.Replace(text, "$1=***");
        text = EmailRe.Replace(text, "***@***");
        text = PhoneRe.Replace(text, m => m.Value[..3] + "****" + m.Value[^4..]);
        text = LongHexRe.Replace(text, "***");
        text = UserPathRe.Replace(text, @"${1}***");
        text = UidRe.Replace(text, m => m.Value[..2] + "***" + m.Value[^2..]);
        return text;
    }

    // JWT：三段用点连接，头段固定以 eyJ 开头
    private static readonly Regex JwtRe = new(
        @"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*", RegexOptions.Compiled);

    // key=value 形式的凭据；值里允许带引号
    private static readonly Regex SecretPairRe = new(
        @"(?i)\b(password|passwd|pwd|token|secret|api[_-]?key|cookie|authorization)\b\s*[:=]\s*(""[^""\r\n]{0,200}""|'[^'\r\n]{0,200}'|[^\s,;]{1,200})",
        RegexOptions.Compiled);

    private static readonly Regex BearerRe = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._\-+/=]{8,}", RegexOptions.Compiled);

    private static readonly Regex EmailRe = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    private static readonly Regex PhoneRe = new(
        @"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.Compiled);

    // 32 位以上的连续十六进制：md5/sha/token 的典型长相
    private static readonly Regex LongHexRe = new(
        @"\b[0-9a-fA-F]{32,}\b", RegexOptions.Compiled);

    // Windows 用户目录：C:\Users\张三\
    private static readonly Regex UserPathRe = new(
        @"(?i)([A-Z]:\\Users\\)[^\\/\r\n""]{1,64}", RegexOptions.Compiled);

    // 9 位纯数字：米游社 UID 的常见长度
    private static readonly Regex UidRe = new(
        @"(?<![\d.])[1-9]\d{8}(?![\d.])", RegexOptions.Compiled);
}
