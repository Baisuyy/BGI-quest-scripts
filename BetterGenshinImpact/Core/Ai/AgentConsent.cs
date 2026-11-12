using System;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>
/// 一次越界操作的授权申请。<see cref="Detail"/> 是给用户看的关键信息（命令、写入长度等）。
/// </summary>
public sealed record AgentRequest(string Action, string Target, string Detail);

/// <summary>
/// 授权闸门。本地助手只能「请示」，能不能放行由界面层注册的 <see cref="Handler"/> 决定。
/// <para>
/// 没有注册处理器时一律拒绝——后台运行、无人值守时不会因为弹不出窗口就默认同意。
/// </para>
/// </summary>
public static class AgentConsent
{
    /// <summary>由界面层注入；返回 true 表示用户同意。</summary>
    public static Func<AgentRequest, Task<bool>>? Handler { get; set; }

    /// <summary>
    /// 界面还没注册处理器时补一个默认的「弹窗问人」实现。
    /// <para>
    /// 常驻小面板和 AI 助手页共用同一个闸门，谁先用谁补默认值，后来者不会覆盖成静默拒绝。
    /// </para>
    /// </summary>
    public static void EnsureDefaultHandler() => Handler ??= AskByDialog;

    /// <summary>默认实现：回到 UI 线程弹一个是否允许的对话框；没有窗口时按拒绝处理。</summary>
    private static Task<bool> AskByDialog(AgentRequest request)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return Task.FromResult(false);
        }

        return dispatcher.InvokeAsync(() =>
        {
            var body = $"""
                        操作：{request.Action}
                        目标：{request.Target}
                        详情：{request.Detail}

                        该操作不在软件目录（沙箱）内，或当前配置要求逐次确认。
                        允许本次执行吗？拒绝不会中断任务，模型会换一种做法。
                        """;
            var result = View.Windows.ThemedMessageBox.Show(body, "本地助手请求授权",
                System.Windows.MessageBoxButton.YesNo,
                View.Windows.ThemedMessageBox.MessageBoxIcon.Warning,
                System.Windows.MessageBoxResult.No);
            return result == System.Windows.MessageBoxResult.Yes;
        }).Task;
    }

    /// <summary>询问是否可以执行。</summary>
    public static async Task<bool> AskAsync(AgentRequest request)
    {
        var handler = Handler;
        if (handler == null)
        {
            return false;
        }

        try
        {
            return await handler(request).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }
}
