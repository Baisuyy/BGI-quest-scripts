using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace BetterGenshinImpact.Helpers;

/// <summary>
/// 落盘用的行格式器：行首带日期，同一异常在时间窗内只留一次完整调用栈。
/// <para>
/// 起因是一份 6064 行的客户端日志：其中 4562 行（75%）是调用栈，而且基本是同一段——
/// 「QQ 机器人网关连接中断，60 秒后重连」重复 253 次，每次把 19 行 <c>HttpRequestException</c>
/// 原样贴一遍。真正有用的「第几次重试、隔了多久」被埋在栈里，grep 也得先跳过 19 行噪音。
/// </para>
/// <para>
/// <see cref="RepeatSuppressorSink"/> 只折叠「连续相同」的行，这里补上另一种情况：
/// 同一条错误消息被别的日志隔开、反复出现时，消息照留（那是重试节奏的证据），
/// 只把重复的栈换成一行说明。
/// </para>
/// </summary>
public sealed class ExceptionDedupTextFormatter : ITextFormatter
{
    /// <summary>同一异常签名在多长时间内只展开一次调用栈。</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>缓存上限，超出时清掉过期项，避免长期运行的无界增长。</summary>
    private const int MaxEntries = 256;

    private readonly MessageTemplateTextFormatter _inner;

    private readonly object _gate = new();

    private readonly Dictionary<string, Seen> _seen = new(StringComparer.Ordinal);

    public ExceptionDedupTextFormatter(string outputTemplateWithoutException)
    {
        _inner = new MessageTemplateTextFormatter(outputTemplateWithoutException, CultureInfo.InvariantCulture);
    }

    public void Format(LogEvent logEvent, TextWriter output)
    {
        _inner.Format(logEvent, output);

        if (logEvent.Exception is not { } exception)
        {
            return;
        }

        var now = logEvent.Timestamp;
        var signature = Signature(exception);
        int repeat;
        DateTimeOffset first;

        lock (_gate)
        {
            if (_seen.TryGetValue(signature, out var seen) && now - seen.Last < Window)
            {
                repeat = seen.Count + 1;
                first = seen.First;
                _seen[signature] = seen with { Last = now, Count = repeat };
                // 记满就顺手清一遍过期的，没到窗口的留着不动
                if (_seen.Count > MaxEntries)
                {
                    Prune(now);
                }
            }
            else
            {
                _seen[signature] = new Seen(now, now, 1);
                Prune(now);
                output.Write(exception);
                output.Write(Environment.NewLine);
                return;
            }
        }

        // 栈已在上文出现过：保留一条定位信息，够判断「是不是同一个错、隔了多久」
        output.Write("（栈同上，首次 ");
        output.Write(first.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        output.Write("；5 分钟内第 ");
        output.Write(repeat.ToString(CultureInfo.InvariantCulture));
        output.Write(" 次，已省略）");
        output.Write(Environment.NewLine);
    }

    /// <summary>只清超出窗口的项；全部有效时按最旧的丢，保证有界。</summary>
    private void Prune(DateTimeOffset now)
    {
        if (_seen.Count <= MaxEntries && _seen.All(pair => now - pair.Value.Last < Window))
        {
            return;
        }

        foreach (var key in _seen.Where(pair => now - pair.Value.Last >= Window).Select(pair => pair.Key).ToList())
        {
            _seen.Remove(key);
        }

        if (_seen.Count <= MaxEntries)
        {
            return;
        }

        foreach (var key in _seen.OrderBy(pair => pair.Value.Last).Take(_seen.Count - MaxEntries).Select(pair => pair.Key).ToList())
        {
            _seen.Remove(key);
        }
    }

    /// <summary>
    /// 异常签名：类型 + 消息 + 最内层栈帧。
    /// 同一个异常类型在不同调用点的含义完全不同，只按类型去重会把两处错误混成一条。
    /// </summary>
    private static string Signature(Exception exception)
    {
        var root = exception;
        while (root.InnerException != null)
        {
            root = root.InnerException;
        }

        var frame = (root.StackTrace ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

        return string.Join('|', root.GetType().FullName, root.Message, frame);
    }

    private readonly record struct Seen(DateTimeOffset First, DateTimeOffset Last, int Count);
}
