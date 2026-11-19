using System;
using System.Collections.Generic;
using System.Text;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace BetterGenshinImpact.Helpers;

/// <summary>
/// 折叠一段时间内重复出现的日志：只保留第一条，等安静下来后补一条「重复 N 次」。
/// <para>
/// 焦点检查、等待重试这类循环每秒写一条同样的 WRN，一天下来文件几万行同文，
/// 真正有用的上下文被淹掉。判重按「消息模板 + 非数值属性」，所以「耗时 12 ms / 9 ms / 23 ms」
/// 这种只差数字的消息算同一条；也不要求两条紧挨着——中间插了别的模块日志照样折叠。
/// 不引入定时器，只在写入时顺带清理过期条目，不打乱日志顺序。
/// </para>
/// </summary>
public sealed class RepeatSuppressorSink : ILogEventSink, IDisposable
{
    private static readonly MessageTemplate RepeatTemplate =
        new MessageTemplateParser().Parse("（上一条重复 {Count} 次，已省略）");

    /// <summary>
    /// 只在这段时间内折叠。超过窗口再出现，说明是另一件事，值得单独留一行。
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

    /// <summary>
    /// 兜底：消息种类无限多时词典不能跟着涨。
    /// </summary>
    private const int MaxTrackedKeys = 1024;

    /// <summary>
    /// 这些属性是纯测量值，每次都在变。参与判重的话「耗时 12 ms / 9 ms」会被当成三条不同消息，
    /// 一条也折叠不掉。只排测量值：Count、Retry 这类承载状态，不同值必须各留一行。
    /// </summary>
    private static readonly HashSet<string> VolatileProperties = new(StringComparer.Ordinal)
    {
        "ElapsedMs", "Elapsed", "Duration", "Ms", "Milliseconds",
    };

    private readonly ILogEventSink _inner;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public RepeatSuppressorSink(ILogEventSink inner)
    {
        _inner = inner;
    }

    public void Emit(LogEvent logEvent)
    {
        var now = DateTimeOffset.Now;
        FlushExpired(now);

        var key = BuildKey(logEvent);
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.Count++;
            entry.LastSeen = now;
            return;
        }

        if (_entries.Count >= MaxTrackedKeys)
        {
            FlushAll();
        }

        _entries[key] = new Entry
        {
            Count = 0,
            Level = logEvent.Level,
            Context = CollectContext(logEvent),
            FirstSeen = now,
            LastSeen = now,
        };

        _inner.Emit(logEvent);
    }

    /// <summary>
    /// 判重用的键：模板原文 + 级别 + 非数值属性。
    /// <para>
    /// 不直接用 <c>RenderMessage()</c>，因为它带着每次都会变的耗时、延迟，
    /// 这些差异会让「同一条错误」被判成不同消息，一条也折叠不掉。
    /// </para>
    /// </summary>
    private static string BuildKey(LogEvent logEvent)
    {
        var builder = new StringBuilder(logEvent.MessageTemplate.Text);
        builder.Append('|').Append(logEvent.Level);

        foreach (var property in logEvent.Properties)
        {
            if (property.Key is "BgiInstance" or "SourceContext"
                || VolatileProperties.Contains(property.Key))
            {
                continue;
            }

            builder.Append('|').Append(property.Key).Append('=').Append(property.Value);
        }

        return builder.ToString();
    }

    private void FlushExpired(DateTimeOffset now)
    {
        List<string>? stale = null;
        foreach (var pair in _entries)
        {
            if (now - pair.Value.LastSeen <= Window)
            {
                continue;
            }

            stale ??= [];
            stale.Add(pair.Key);
        }

        if (stale is null)
        {
            return;
        }

        foreach (var key in stale)
        {
            var entry = _entries[key];
            _entries.Remove(key);
            EmitRepeat(entry);
        }
    }

    private void FlushAll()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        foreach (var entry in _entries.Values)
        {
            EmitRepeat(entry);
        }

        _entries.Clear();
    }

    private void EmitRepeat(Entry entry)
    {
        if (entry.Count <= 0)
        {
            return;
        }

        var properties = new LogEventProperty[entry.Context.Length + 1];
        properties[0] = new LogEventProperty("Count", new ScalarValue(entry.Count));
        Array.Copy(entry.Context, 0, properties, 1, entry.Context.Length);

        var note = new LogEvent(DateTimeOffset.Now, LogEventLevel.Debug, null, RepeatTemplate, properties);
        _inner.Emit(note);
    }

    /// <summary>保留实例标识等常量属性，避免折叠提示行缺字段。</summary>
    private static LogEventProperty[] CollectContext(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("BgiInstance", out var instance))
        {
            return [];
        }

        return [new LogEventProperty("BgiInstance", instance)];
    }

    public void Dispose()
    {
        FlushAll();
        (_inner as IDisposable)?.Dispose();
    }

    private sealed class Entry
    {
        public int Count;

        public LogEventLevel Level;

        public LogEventProperty[] Context = [];

        public DateTimeOffset FirstSeen;

        public DateTimeOffset LastSeen;
    }
}
