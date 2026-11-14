using System;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace BetterGenshinImpact.Helpers;

/// <summary>
/// 折叠「连续完全相同」的日志：只保留第一条，后续只计数，等出现不同内容时补一条「重复 N 次」。
/// <para>
/// 焦点检查、等待重试这类循环每秒写一条同样的 WRN，一天下来文件几万行同文，
/// 真正有用的上下文被淹掉。这里按内容判重，不引入定时器，也不会打乱日志顺序。
/// </para>
/// </summary>
public sealed class RepeatSuppressorSink : ILogEventSink, IDisposable
{
    private static readonly MessageTemplate RepeatTemplate =
        new MessageTemplateParser().Parse("（上一条重复 {Count} 次，已省略）");

    private readonly ILogEventSink _inner;

    private string? _lastKey;
    private LogEventLevel _lastLevel;
    private LogEventProperty[] _lastContext = [];
    private int _repeat;

    public RepeatSuppressorSink(ILogEventSink inner)
    {
        _inner = inner;
    }

    public void Emit(LogEvent logEvent)
    {
        var key = logEvent.RenderMessage();
        if (_lastKey != null
            && logEvent.Level == _lastLevel
            && string.Equals(key, _lastKey, StringComparison.Ordinal))
        {
            _repeat++;
            return;
        }

        FlushRepeat();

        _lastKey = key;
        _lastLevel = logEvent.Level;
        _lastContext = CollectContext(logEvent);
        _inner.Emit(logEvent);
    }

    private void FlushRepeat()
    {
        if (_repeat <= 0)
        {
            return;
        }

        var count = _repeat;
        _repeat = 0;

        var properties = new LogEventProperty[_lastContext.Length + 1];
        properties[0] = new LogEventProperty("Count", new ScalarValue(count));
        Array.Copy(_lastContext, 0, properties, 1, _lastContext.Length);

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
        FlushRepeat();
        (_inner as IDisposable)?.Dispose();
    }
}
