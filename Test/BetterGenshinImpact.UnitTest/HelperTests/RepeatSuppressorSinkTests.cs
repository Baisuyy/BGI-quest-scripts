using BetterGenshinImpact.Helpers;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace BetterGenshinImpact.UnitTest.HelperTests;

/// <summary>
/// 重复日志折叠：真实日志里「重试」和「HTTP 失败」是交替出现的，只折叠紧邻的两条等于没折。
/// </summary>
public sealed class RepeatSuppressorSinkTests
{
    /// <summary>中间插了其它模块的日志，相同的那条仍然要折叠。</summary>
    [Fact]
    public void InterleavedMessages_StillCollapsed()
    {
        var capture = new RecordingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new RepeatSuppressorSink(capture))
            .CreateLogger();

        for (var i = 0; i < 5; i++)
        {
            logger.Warning("QQ 机器人网关连不上：{Summary}，{Retry} 秒后重试", "证书校验未通过", 60);
            logger.Debug("HTTP GET {Host} 失败：{Error}，耗时 {ElapsedMs} ms", "api.sgroup.qq.com", "HttpRequestException", i * 3);
        }

        // 两组各只剩第一条，重复的 8 条被吃掉
        Assert.Equal(2, capture.Events.Count);
        Assert.DoesNotContain(capture.Events, e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("重复"));
    }

    /// <summary>
    /// 耗时这种每次都在变的数字不参与判重，否则「12 ms / 9 ms / 23 ms」会被当成三条不同消息。
    /// </summary>
    [Fact]
    public void VolatileNumbers_DoNotSplitSameMessage()
    {
        var capture = new RecordingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new RepeatSuppressorSink(capture))
            .CreateLogger();

        logger.Warning("连接失败，耗时 {ElapsedMs} ms", 12);
        logger.Warning("连接失败，耗时 {ElapsedMs} ms", 9);
        logger.Warning("连接失败，耗时 {ElapsedMs} ms", 23);

        Assert.Single(capture.Events);
    }

    /// <summary>
    /// 退避档位变了说明状态有变化，各留一行，不能并成一条。
    /// </summary>
    [Fact]
    public void DifferentRetryInterval_KeepsSeparateLines()
    {
        var capture = new RecordingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new RepeatSuppressorSink(capture))
            .CreateLogger();

        logger.Warning("网关仍连不上，{Retry} 后重试", "1 分钟后重试");
        logger.Warning("网关仍连不上，{Retry} 后重试", "5 分钟后重试");

        Assert.Equal(2, capture.Events.Count);
    }

    /// <summary>关闭时把还没补上的计数吐出来，不然最后一段重复就丢了。</summary>
    [Fact]
    public void Dispose_FlushesPendingCount()
    {
        var capture = new RecordingSink();
        var sink = new RepeatSuppressorSink(capture);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Warning("同一条错误");
        logger.Warning("同一条错误");
        logger.Warning("同一条错误");

        sink.Dispose();

        // 第一条 + 「重复 2 次」的汇总
        Assert.Equal(2, capture.Events.Count);
        Assert.Contains(capture.Events, e => e.RenderMessage().Contains("重复 2 次"));
    }

    private sealed class RecordingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
        }
    }
}
