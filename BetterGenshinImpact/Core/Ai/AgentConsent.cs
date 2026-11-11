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
