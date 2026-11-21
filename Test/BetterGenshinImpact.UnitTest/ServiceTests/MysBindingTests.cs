using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BetterGenshinImpact.Core.Mys;
using BetterGenshinImpact.Service.RemoteTask;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

/// <summary>
/// 米游社扫码绑定：指令识别、登录态拼装、二维码渲染。
/// <para>
/// 不联网：扫码接口的返回值用构造出来的 JSON 与 Set-Cookie 模拟，
/// 真实链路只能在有账号的机器上验证。
/// </para>
/// </summary>
public sealed class MysBindingTests
{
    /// <summary>
    /// 解析器需要的任务清单，这里只要一个占位项。
    /// </summary>
    private static readonly IReadOnlyList<(string TaskId, string DisplayName)> Tasks = [("one_dragon", "一条龙")];

    /// <summary>
    /// 常见说法都要识别成「绑定米游社」。
    /// </summary>
    [Theory]
    [InlineData("绑定米游社")]
    [InlineData("米游社登录")]
    [InlineData("绑定mys")]
    [InlineData("扫码绑定米游社")]
    public void Parse_BindMysKeywords(string text)
    {
        var command = RemoteTaskCommandParser.Parse(text, Tasks);
        Assert.Equal(RemoteTaskCommandKind.BindMys, command.Kind);
    }

    /// <summary>
    /// 多角色时带序号：序号必须落到 Args 里，不能被当成新的绑定。
    /// </summary>
    [Theory]
    [InlineData("绑定米游社 2", "2")]
    [InlineData("绑定米游社2", "2")]
    [InlineData("绑定米游社第二", "二")]
    public void Parse_BindMysWithIndex(string text, string expected)
    {
        var command = RemoteTaskCommandParser.Parse(text, Tasks);
        Assert.Equal(RemoteTaskCommandKind.BindMys, command.Kind);
        Assert.Equal(expected, command.Args);
    }

    /// <summary>
    /// 换号指令不能被「米游社」相关关键词抢走。
    /// </summary>
    [Theory]
    [InlineData("切号")]
    [InlineData("扫码登录")]
    [InlineData("登录界面")]
    public void Parse_LoginNotHijacked(string text)
    {
        var command = RemoteTaskCommandParser.Parse(text, Tasks);
        Assert.Equal(RemoteTaskCommandKind.Login, command.Kind);
    }

    /// <summary>
    /// Set-Cookie 与 tokens 两路来源都要能拼出可用的 Cookie。
    /// </summary>
    [Fact]
    public void Cookie_Collect_FromHeadersAndTokens()
    {
        string[] headers =
        [
            "ltuid_v2=123456789; Path=/; HttpOnly; Secure",
            "ltoken_v2=ltoken-value; Path=/",
            "cookie_token_v2=cookie-value; Path=/",
            "mid=abcdef; Path=/",
            "unrelated=1; Path=/"
        ];

        var dictionary = MysCookieBuilder.Collect(headers, null);
        var cookie = MysCookieBuilder.Compose(dictionary);

        Assert.Contains("ltuid_v2=123456789", cookie);
        Assert.Contains("account_id_v2=123456789", cookie);
        Assert.Contains("ltoken_v2=ltoken-value", cookie);
        Assert.Contains("cookie_token_v2=cookie-value", cookie);
        Assert.Contains("mid=abcdef", cookie);
        Assert.Contains("unrelated=1", cookie);
        Assert.Null(MysCookieBuilder.Validate(cookie));
        Assert.Contains("123456789", MysCookieBuilder.Describe(cookie));
        Assert.DoesNotContain("ltoken-value", MysCookieBuilder.Describe(cookie));
    }

    /// <summary>
    /// <c>ltmid_v2</c> / <c>account_mid_v2</c> 一个都不能丢。
    /// <para>
    /// v2 令牌靠配对的 mid 定位签发会话，早期版本按白名单挑字段，把 mid 丢掉了，
    /// 结果扫码绑定显示成功、查树脂永远 Please login。这个用例就是那条防线的守门人。
    /// </para>
    /// </summary>
    [Fact]
    public void Cookie_KeepsMidFields()
    {
        string[] headers =
        [
            "ltuid_v2=123456789; Path=/",
            "ltmid_v2=mid-value; Path=/",
            "ltoken_v2=v2_ltoken; Path=/"
        ];

        var cookie = MysCookieBuilder.Compose(MysCookieBuilder.Collect(headers, null));

        Assert.Contains("ltmid_v2=mid-value", cookie);
        Assert.Contains("account_mid_v2=mid-value", cookie);
        Assert.Contains("mid=mid-value", cookie);
    }

    /// <summary>
    /// 令牌不能新旧互抄：<c>v2_</c> 开头的值塞进 <c>ltoken</c> 会被当成格式错误。
    /// </summary>
    [Fact]
    public void Cookie_DoesNotCopyTokenAcrossVersions()
    {
        string[] headers = ["ltuid_v2=1; Path=/", "ltoken_v2=v2_abc; Path=/"];

        var cookie = MysCookieBuilder.Compose(MysCookieBuilder.Collect(headers, null));

        Assert.Contains("ltoken_v2=v2_abc", cookie);
        Assert.DoesNotContain("ltoken=v2_abc", cookie);
    }

    /// <summary>
    /// 只有旧名字时补出 v2 的 ID；令牌保持原样，不跨版本抄。
    /// </summary>
    [Fact]
    public void Cookie_FillMissingV2Names()
    {
        string[] headers = ["ltuid=987654321; Path=/", "ltoken=old-token; Path=/"];

        var cookie = MysCookieBuilder.Compose(MysCookieBuilder.Collect(headers, null));

        Assert.Contains("ltuid_v2=987654321", cookie);
        Assert.Contains("ltuid=987654321", cookie);
        Assert.Contains("ltoken=old-token", cookie);
        Assert.DoesNotContain("ltoken_v2=old-token", cookie);
        Assert.Null(MysCookieBuilder.Validate(cookie));
    }

    /// <summary>
    /// 只拿到 tokens 时按类型补令牌，账号 ID 从 user_info 里取。
    /// </summary>
    [Fact]
    public void Cookie_FallbackToTokensAndUserInfo()
    {
        var data = JsonNode.Parse("""
            {
              "tokens": [
                {"token_type": 2, "token": "lt-from-token"},
                {"token_type": 4, "token": "cookie-from-token"}
              ],
              "user_info": {"aid": "100200300"}
            }
            """);

        var cookie = MysCookieBuilder.Compose(MysCookieBuilder.Collect([], data));

        Assert.Contains("ltuid_v2=100200300", cookie);
        Assert.Contains("ltoken_v2=lt-from-token", cookie);
        Assert.Contains("cookie_token_v2=cookie-from-token", cookie);
    }

    /// <summary>
    /// 缺令牌时给出可读的原因，而不是留一条半残的 Cookie。
    /// </summary>
    [Fact]
    public void Cookie_Validate_ReportsMissingToken()
    {
        var cookie = MysCookieBuilder.Compose(MysCookieBuilder.Collect(["ltuid_v2=1; Path=/"], null));
        Assert.NotNull(MysCookieBuilder.Validate(cookie));
    }

    /// <summary>
    /// UID 里混进空格、引号时只留数字。
    /// </summary>
    [Fact]
    public void DigitsOnly_StripsNoise()
    {
        Assert.Equal("100000000", MysCookieBuilder.DigitsOnly(" 100 000-000 "));
        Assert.Equal(string.Empty, MysCookieBuilder.DigitsOnly(null));
    }

    /// <summary>
    /// 二维码要能直接发给聊天客户端，必须是合法 PNG。
    /// </summary>
    [Fact]
    public void QrCode_Render_ProducesPng()
    {
        var png = MysQrCode.Render("https://user.mihoyo.com/login-platform/mobile.html?tk=test#/login/qr");

        Assert.True(png.Length > 200);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png.Take(4).ToArray());
    }
}
