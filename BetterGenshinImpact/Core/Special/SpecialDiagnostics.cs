using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service;
using Serilog.Core;
using Serilog.Events;

namespace BetterGenshinImpact.Core.Special;

/// <summary>
/// 出错时自动收集诊断包。
/// <para>
/// 「自动上报」在没有服务端的前提下只有一种诚实做法：把该给的东西整理好，
/// 让人一键发出去。所以这里负责打包——当日日志、最近一次错误、环境信息、脱敏后的配置摘要——
/// 生成一个 zip 放在 <c>log/diagnostics</c> 下，反馈问题的时候直接拖进 issue 就行。
/// </para>
/// <para>
/// 触发条件是日志里出现 Error 级别：一次运行只打包一次，10 分钟内不重复。
/// 打包在后台线程做，不阻塞出错时的主流程。
/// </para>
/// </summary>
public static class SpecialDiagnostics
{
    private static readonly object Gate = new();

    private static DateTime _lastCapture = DateTime.MinValue;

    private static int _running;

    /// <summary>诊断包目录。</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "log", "diagnostics");

    /// <summary>单次打包的日志总上限，避免把几百 MB 日志塞进 zip。</summary>
    private const long MaxLogBytes = 24L * 1024 * 1024;

    /// <summary>保留多少个诊断包。</summary>
    private const int Keep = 5;

    /// <summary>
    /// 触发一次诊断包收集。失败不影响主流程，也绝不抛异常。
    /// </summary>
    /// <param name="reason">触发原因，写进摘要。</param>
    public static void CaptureOnError(string reason)
    {
        lock (Gate)
        {
            if (DateTime.Now - _lastCapture < TimeSpan.FromMinutes(10))
            {
                return;
            }

            if (Interlocked.Exchange(ref _running, 1) == 1)
            {
                return;
            }

            _lastCapture = DateTime.Now;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var path = Build(reason);
                if (path != null)
                {
                    Serilog.Log.Information("已生成诊断包：{Path}（反馈问题时可直接附上）", path);
                }
            }
            catch (Exception e)
            {
                Serilog.Log.Debug(e, "生成诊断包失败");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    /// <summary>
    /// 手动生成一个诊断包，返回文件路径；失败返回 null。
    /// </summary>
    public static string? Build(string reason)
    {
        Directory.CreateDirectory(Folder);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(Folder, $"BGI-diag-{stamp}.zip");

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteText(zip, "summary.txt", BuildSummary(reason));
            AddLogs(zip);
        }

        Trim();
        return zipPath;
    }

    private static string BuildSummary(string reason)
    {
        var builder = new StringBuilder();
        builder.AppendLine("BetterGI 诊断包");
        builder.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine("触发原因：" + reason);
        builder.AppendLine("版本：" + Global.Version);
        builder.AppendLine("进程位数：" + (Environment.Is64BitProcess ? "x64" : "x86"));
        builder.AppendLine("系统：" + Environment.OSVersion.VersionString);
        builder.AppendLine("运行时：" + Environment.Version);
        builder.AppendLine("CPU 核数：" + Environment.ProcessorCount);
        builder.AppendLine();

        // 配置摘要只写「会影响行为的开关」，不写密钥与路径。
        try
        {
            var config = ConfigService.Config;
            if (config != null)
            {
                var ai = config.AiConfig;
                builder.AppendLine("── 配置摘要 ──");
                builder.AppendLine("AI 启用：" + ai.Enabled + "　模型：" + ai.ModelName + "　接口：〈已隐去〉");
                builder.AppendLine("AI 助手启用：" + ai.AgentEnabled + "　视觉模式：" + ai.VisionMode);
                builder.AppendLine("AI 限流：" + ai.LimitCalls + "　每小时上限：" + ai.MaxCallsPerHour);
                builder.AppendLine("日志落盘级别：" + config.CommonConfig.LogFileLevel);
                builder.AppendLine("遮罩窗口：" + config.MaskWindowConfig.MaskEnabled);
                builder.AppendLine();
            }
        }
        catch (Exception e)
        {
            builder.AppendLine("配置摘要读取失败：" + e.Message);
            builder.AppendLine();
        }

        builder.AppendLine("── 最近错误（最多 200 行）──");
        var errors = ReadRecentErrors(200);
        builder.AppendLine(errors.Length > 0 ? errors : "（当前日志里没有 ERR 行）");
        return builder.ToString();
    }

    private static void AddLogs(ZipArchive zip)
    {
        var logFolder = Path.Combine(AppContext.BaseDirectory, "log");
        if (!Directory.Exists(logFolder))
        {
            return;
        }

        var files = new DirectoryInfo(logFolder)
            .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTime)
            .Take(4)
            .ToList();

        long budget = MaxLogBytes;
        foreach (var file in files)
        {
            if (budget <= 0)
            {
                break;
            }

            try
            {
                // 只取尾部：一次上午的启动日志对定位晚上那次崩溃没有帮助。
                var take = (int)Math.Min(file.Length, Math.Min(budget, 6L * 1024 * 1024));
                var entry = zip.CreateEntry("logs/" + file.Name, CompressionLevel.Fastest);
                using var output = entry.Open();
                using var input = file.OpenRead();
                if (file.Length > take)
                {
                    input.Seek(file.Length - take, SeekOrigin.Begin);
                }

                input.CopyTo(output);
                budget -= take;
            }
            catch (Exception e)
            {
                Serilog.Log.Debug(e, "打包日志失败 {File}", file.FullName);
            }
        }
    }

    private static string ReadRecentErrors(int maxLines)
    {
        var logFolder = Path.Combine(AppContext.BaseDirectory, "log");
        if (!Directory.Exists(logFolder))
        {
            return string.Empty;
        }

        var latest = new DirectoryInfo(logFolder)
            .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();
        if (latest == null)
        {
            return string.Empty;
        }

        try
        {
            var lines = File.ReadLines(latest.FullName).Where(line => line.Contains("[ERR]", StringComparison.Ordinal)).ToList();
            return lines.Count == 0
                ? string.Empty
                : string.Join(Environment.NewLine, lines.TakeLast(maxLines));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void Trim()
    {
        try
        {
            var stale = new DirectoryInfo(Folder)
                .EnumerateFiles("BGI-diag-*.zip")
                .OrderByDescending(f => f.CreationTime)
                .Skip(Keep)
                .ToList();
            foreach (var file in stale)
            {
                file.Delete();
            }
        }
        catch
        {
            // 清不掉就算了，下次再清。
        }
    }

    /// <summary>
    /// 打开诊断包所在目录，方便直接把 zip 拖进反馈渠道。
    /// </summary>
    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = Folder,
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "打开诊断目录失败");
        }
    }
}

/// <summary>
/// 只关心 Error 的 Serilog 接收器：出现错误就触发一次诊断包，其余日志直接丢弃。
/// </summary>
public sealed class ErrorDiagnosticsSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Error)
        {
            return;
        }

        try
        {
            if (ConfigService.Config?.CommonConfig.DiagnosticsOnErrorEnabled == false)
            {
                return;
            }
        }
        catch
        {
            // 配置还没加载好也照常收集，报错现场比配置重要。
        }

        var message = logEvent.RenderMessage();
        SpecialDiagnostics.CaptureOnError(
            logEvent.Exception != null ? $"{message}（{logEvent.Exception.GetType().Name}）" : message);
    }
}
