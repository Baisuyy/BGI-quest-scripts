using System.Collections.Generic;
using BetterGenshinImpact.Service.RemoteTask;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

/// <summary>
/// 远程更新指令：只认整句，且不抢「更新脚本」这类别的请求。
/// <para>
/// 更新是少数几个会自己退出进程的操作，解析错了代价比别的指令大，
/// 所以把「该认的」和「不该认的」都钉一遍。
/// </para>
/// </summary>
public sealed class RemoteUpdateTests
{
    private static readonly IReadOnlyList<(string TaskId, string DisplayName)> Tasks =
    [
        ("one_dragon", "一条龙"),
        ("auto_pathing", "地图追踪")
    ];

    /// <summary>
    /// 「更新」这类短词必须能认出来。
    /// </summary>
    [Theory]
    [InlineData("更新")]
    [InlineData("升级")]
    [InlineData("更新一下")]
    [InlineData("更新软件")]
    [InlineData("更新BetterGI")]
    [InlineData("检查更新")]
    public void Parse_Update(string text)
    {
        var command = RemoteTaskCommandParser.Parse(text, Tasks);
        Assert.Equal(RemoteTaskCommandKind.Update, command.Kind);
    }

    /// <summary>
    /// 第二步单独一句话：这一句才会真的退出进程。
    /// </summary>
    [Theory]
    [InlineData("重启更新")]
    [InlineData("应用更新")]
    [InlineData("安装更新")]
    [InlineData("重启并更新")]
    public void Parse_ApplyUpdate(string text)
    {
        var command = RemoteTaskCommandParser.Parse(text, Tasks);
        Assert.Equal(RemoteTaskCommandKind.ApplyUpdate, command.Kind);
    }

    /// <summary>
    /// 带「更新」二字、但说的是别的事，不能被抢成更新软件。
    /// </summary>
    [Theory]
    [InlineData("更新脚本")]
    [InlineData("更新一下脚本")]
    [InlineData("更新我的采集路线")]
    public void Parse_NotUpdate(string text)
    {
        var command = RemoteTaskCommandParser.Parse(text, Tasks);
        Assert.NotEqual(RemoteTaskCommandKind.Update, command.Kind);
        Assert.NotEqual(RemoteTaskCommandKind.ApplyUpdate, command.Kind);
    }

    /// <summary>
    /// 「重启」不能单独当更新：用户说「重启游戏」时更可能是想要关掉再开。
    /// </summary>
    [Fact]
    public void Parse_RestartAlone_IsNotApplyUpdate()
    {
        var command = RemoteTaskCommandParser.Parse("重启", Tasks);
        Assert.NotEqual(RemoteTaskCommandKind.ApplyUpdate, command.Kind);
    }
}
