using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 内置大模型配置（特别版）。
/// <para>
/// 这是 BGI 自身的决策能力：跑图卡死、路线走不通时，由内置模型直接读取游戏状态并操作角色，
/// 而不是靠脚本调接口。密钥只存在于客户端配置里，日志与导出都不会带出原文。
/// </para>
/// </summary>
public partial class AiConfig : ObservableObject
{
    /// <summary>
    /// 总开关。关闭后内核不会发出任何请求，所有接入点退回纯规则逻辑。
    /// </summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>
    /// 服务协议：<c>openai</c>（OpenAI 兼容，含 DeepSeek / 通义 / 智谱 / Kimi / Ollama / LM Studio）
    /// 或 <c>anthropic</c>（Claude 原生协议）。
    /// </summary>
    [ObservableProperty]
    private string _provider = "openai";

    /// <summary>
    /// 服务地址。OpenAI 兼容协议填到 <c>/v1</c>；Anthropic 填根地址，客户端自行拼 <c>/v1/messages</c>。
    /// 非本机回环地址必须是 HTTPS。
    /// </summary>
    [ObservableProperty]
    private string _endpoint = "https://api.deepseek.com/v1";

    /// <summary>
    /// 模型名，如 <c>deepseek-chat</c>、<c>qwen2.5:14b</c>。必须支持工具调用（function calling）才能接管操作。
    /// </summary>
    [ObservableProperty]
    private string _modelName = "deepseek-chat";

    /// <summary>
    /// 服务密钥。本机回环地址可留空。
    /// </summary>
    [ObservableProperty]
    private string _apiKey = "";

    /// <summary>
    /// 采样温度。决策类任务建议 0–0.3，太高会做出无意义动作。
    /// </summary>
    [ObservableProperty]
    private double _temperature = 0.2;

    /// <summary>
    /// 单次请求超时（秒）。本地小模型加载慢时可调大。
    /// </summary>
    [ObservableProperty]
    private int _timeoutSeconds = 60;

    /// <summary>
    /// 每小时调用上限。内置决策和脚本共用同一个预算，防止循环里把额度烧光。
    /// </summary>
    [ObservableProperty]
    private int _maxCallsPerHour = 60;

    /// <summary>
    /// 本次运行调用上限，重启 BGI 后清零。
    /// </summary>
    [ObservableProperty]
    private int _maxCallsPerRun = 300;

    /// <summary>
    /// 跑图卡死接管：路线连续卡死、即将放弃时，让内置模型直接操作角色脱困。
    /// </summary>
    [ObservableProperty]
    private bool _rescueOnStuck = true;

    /// <summary>
    /// 单次接管允许的模型步数（一步一次工具调用）。
    /// </summary>
    [ObservableProperty]
    private int _rescueMaxSteps = 6;

    /// <summary>
    /// 单次接管总超时（秒），超时立刻交还规则逻辑。
    /// </summary>
    [ObservableProperty]
    private int _rescueTimeoutSeconds = 45;

    /// <summary>
    /// 第几次卡死交给内置 AI。默认 3（前两次仍走规则脱困）。
    /// 设成 1 可以在测试时让第一次卡死就接管，用来确认链路是否通。
    /// </summary>
    [ObservableProperty]
    private int _rescueTriggerAfterStuck = 3;

    /// <summary>
    /// 允许模型在距离过远时直接传送到目标点位。关闭后模型只能用走位脱困。
    /// </summary>
    [ObservableProperty]
    private bool _allowTeleport = true;

    /// <summary>
    /// 读取左上角任务面板：把任务标题、当前步骤、目标动作与计数（0/3）并入决策上下文。
    /// <para>
    /// 每步不 OCR，只在接管开始时读一次，交互之后与每 3 步刷新；模型也可以主动调 quest 工具重读。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _readQuestProgress = true;

    /// <summary>
    /// 视觉模式：<c>false</c> 只发结构化状态（坐标、距离、运动状态、任务面板文字），
    /// <c>true</c> 额外附带一张压缩截图，供多模态模型判断地形。
    /// </summary>
    [ObservableProperty]
    private bool _visionMode;

    /// <summary>
    /// 把每次决策写进日志（请求摘要、动作、耗时），便于回看模型到底做了什么。
    /// </summary>
    [ObservableProperty]
    private bool _logDecisions = true;

    /// <summary>
    /// 追加到系统提示末尾的额外约束，留空则不追加。
    /// </summary>
    [ObservableProperty]
    private string _systemPromptExtra = "";

    /// <summary>
    /// 本地助手（Agent）开关。打开后可以在「AI 助手」页直接下任务，由模型读写文件、运行程序。
    /// </summary>
    [ObservableProperty]
    private bool _agentEnabled;

    /// <summary>
    /// 沙箱根目录。留空表示软件自身目录；相对路径同样相对软件目录。
    /// 沙箱内的操作默认放行，沙箱外一律逐次申请授权。
    /// </summary>
    [ObservableProperty]
    private string _agentSandboxRoot = "";

    /// <summary>
    /// 允许直接写入沙箱内的文件，不再逐次确认。关闭后连沙箱内的写入也要点同意。
    /// </summary>
    [ObservableProperty]
    private bool _agentAllowWrite = true;

    /// <summary>
    /// 允许直接运行沙箱内的程序，不再逐次确认。默认关闭，执行任何命令都要点头。
    /// </summary>
    [ObservableProperty]
    private bool _agentAllowRun;

    /// <summary>
    /// 单次任务最多几步（一步一次工具调用）。
    /// 实测一次「看清有哪些任务 → 查参数 → 确认状态 → 下发 → 核实」要 5~8 步，
    /// 遇到配置缺失再补一次就要 10 步以上，默认 16 才不会中途截断。
    /// </summary>
    [ObservableProperty]
    private int _agentMaxSteps = 16;

    /// <summary>
    /// 单次任务总超时（秒）。
    /// </summary>
    [ObservableProperty]
    private int _agentTimeoutSeconds = 300;


    /// <summary>是否使用 Anthropic 原生协议。</summary>
    [JsonIgnore]
    public bool IsAnthropic => string.Equals(Provider, "anthropic", System.StringComparison.OrdinalIgnoreCase);
}
