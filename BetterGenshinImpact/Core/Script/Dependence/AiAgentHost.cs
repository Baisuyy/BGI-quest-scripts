using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Ai;

namespace BetterGenshinImpact.Core.Script.Dependence;

/// <summary>
/// 脚本侧的大模型入口。
/// <para>
/// 它只是 <see cref="AiKernel"/> 的一层薄壳：地址、密钥、预算、熔断全在内核里，
/// 脚本拿不到 apiKey 原文，也没法绕过设置页的限制。
/// 「BGI 自己用大模型」走的是同一个内核，两边不会出现两套配置互相打架。
/// </para>
/// </summary>
public sealed class AiAgentHost(AiKernel kernel)
{
    /// <summary>配置里的总开关。脚本可据此决定走规则还是走模型。</summary>
    public bool Enabled => kernel.Enabled;

    /// <summary>当前配置的模型名，仅供脚本打印日志。</summary>
    public string Model => kernel.Model;

    /// <summary>当前配置的服务地址，仅供脚本打印日志。</summary>
    public string Endpoint => kernel.Endpoint;

    /// <summary>是否允许多模态（脚本可传截图给支持视觉的模型）。</summary>
    public bool VisionEnabled => kernel.VisionEnabled;

    /// <summary>本次运行已调用次数。</summary>
    public int Calls => kernel.Calls;

    /// <summary>本次运行允许的最大调用次数。</summary>
    public int MaxCalls => kernel.MaxCalls;

    /// <summary>现在能不能调用。返回 null 表示可以。</summary>
    public string? UnavailableReason => kernel.UnavailableReason;

    public Task<string> Chat(string userContent) => Chat(userContent, "");

    /// <summary>
    /// 发一轮对话，返回模型输出的纯文本。失败一律抛异常，由脚本决定降级策略。
    /// </summary>
    public Task<string> Chat(string userContent, string systemPrompt)
        => kernel.AskAsync("脚本", userContent, systemPrompt, CancellationToken.None);
}
