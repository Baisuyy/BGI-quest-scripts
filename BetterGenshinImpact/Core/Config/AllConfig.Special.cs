using System;
using System.Text.Json.Serialization;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
///     <see cref="AllConfig" /> 的特别版扩展。
///     <para>
///         这些节点原先直接写在 AllConfig.cs 里，靠 patch_special.py 的锚点每轮同步后重新注入。
///         锚点一漂就得改脚本，而且漏注入时是「编译失败」而不是「少个功能」，代价很高。
///     </para>
///     <para>
///         AllConfig 是 partial，放到这个文件后同步覆盖 AllConfig.cs 不再影响它们，
///         patch_special.py 里那段 <c>CONFIG_BLOCK</c> 也可以删掉。
///     </para>
/// </summary>
public partial class AllConfig
{
    /// <summary>
    ///     内置大模型配置（特别版）
    /// </summary>
    public AiConfig AiConfig { get; set; } = new();

    /// <summary>
    ///     旧版脚本 AI 配置的兼容入口：读到时自动并入 <see cref="AiConfig" />，不再单独写出。
    /// </summary>
    [JsonPropertyName("aiAgentConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyAiAgentConfig? LegacyAiConfig
    {
        get => null;
        set
        {
            if (value == null)
            {
                return;
            }

            // 旧配置只有端点/模型/密钥等基础项，接管相关开关按默认值补齐
            AiConfig.Enabled = value.Enabled;
            if (!string.IsNullOrWhiteSpace(value.Endpoint)) AiConfig.Endpoint = value.Endpoint;
            if (!string.IsNullOrWhiteSpace(value.ModelName)) AiConfig.ModelName = value.ModelName;
            if (!string.IsNullOrWhiteSpace(value.ApiKey)) AiConfig.ApiKey = value.ApiKey;
            if (value.Temperature > 0) AiConfig.Temperature = value.Temperature;
            if (value.TimeoutSeconds > 0) AiConfig.TimeoutSeconds = value.TimeoutSeconds;
            if (value.MaxCallsPerRun > 0) AiConfig.MaxCallsPerRun = value.MaxCallsPerRun;
            AiConfig.VisionMode = string.Equals(value.VisionMode, "multimodal", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(value.SystemPromptExtra)) AiConfig.SystemPromptExtra = value.SystemPromptExtra;
        }
    }

    /// <summary>
    ///     旧版（脚本专用）AI 配置结构，仅用于读取历史 config.json。
    /// </summary>
    public sealed class LegacyAiAgentConfig
    {
        public bool Enabled { get; set; }

        public string Endpoint { get; set; } = "";

        public string ModelName { get; set; } = "";

        public string ApiKey { get; set; } = "";

        /// <summary>旧版是字符串：<c>off</c> / <c>multimodal</c>。</summary>
        public string VisionMode { get; set; } = "off";

        public int MaxCallsPerRun { get; set; }

        public int TimeoutSeconds { get; set; }

        public double Temperature { get; set; }

        public string SystemPromptExtra { get; set; } = "";
    }

    /// <summary>
    ///     远程任务配置（QQ 机器人下发任务与截图回传）
    /// </summary>
    public RemoteTaskConfig RemoteTaskConfig { get; set; } = new();

    /// <summary>
    ///     定时任务配置（按时间点或树脂阈值自动触发任务）
    /// </summary>
    public RemoteScheduleConfig RemoteScheduleConfig { get; set; } = new();
}
