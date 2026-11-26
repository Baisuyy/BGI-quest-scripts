using System;
using System.IO;
using BetterGenshinImpact.Core.Config;
using Serilog.Events;

namespace BetterGenshinImpact.Core.Special;

/// <summary>
/// 特别版的日志写盘策略：落盘级别可配、旧格式日志归档。
/// 放在独立文件里，避免上游同步覆盖 App.xaml.cs 时把这些一并带走。
/// </summary>
public static class SpecialLogging
{
    public static LogEventLevel FileLogLevel(AllConfig all) => all.CommonConfig.LogFileLevel switch
    {
        LogFileLevel.Error => LogEventLevel.Error,
        LogFileLevel.Warning => LogEventLevel.Warning,
        LogFileLevel.Information => LogEventLevel.Information,
        _ => LogEventLevel.Debug,
    };

    /// <summary>
    /// 把旧版本写下的当天日志改名归档，让新日志从干净文件开始。
    /// <para>
    /// 旧版行首是 <c>[HH:mm:ss.fff]</c>，新版是 <c>[yyyy-MM-dd HH:mm:ss.fff]</c>。
    /// 升级当天两者会落进同一个按天滚动的文件，行首对不齐，也没法判断某段是哪个版本写的。
    /// 只在首行确实是旧格式时动手，读不出或改名失败（多实例并发写）就原样留着，不影响日志功能。
    /// </para>
    /// </summary>
    public static void ArchiveLegacyLog(string logFolder)
    {
        try
        {
            var todayFile = Path.Combine(logFolder, $"better-genshin-impact{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(todayFile))
            {
                return;
            }

            string? firstLine;
            using (var stream = new FileStream(todayFile, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                firstLine = reader.ReadLine();
            }

            // 空文件或已经是带日期的格式，不用管
            if (string.IsNullOrWhiteSpace(firstLine) || firstLine.StartsWith("[20", StringComparison.Ordinal))
            {
                return;
            }

            var stamp = DateTime.Now.ToString("HHmmss");
            File.Move(todayFile,
                Path.Combine(logFolder, $"better-genshin-impact{DateTime.Now:yyyyMMdd}.legacy-{stamp}.log"));
        }
        catch (Exception)
        {
            // 归档只是让日志更好读，失败不该挡住启动
        }
    }
}
