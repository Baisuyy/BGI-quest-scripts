using System;
using System.Collections.Generic;
using BetterGenshinImpact.Core.Config;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 对话人格预设。
/// <para>
/// 人格只影响「怎么说」，不影响「做什么」：任务解析仍然按规则与任务清单走，
/// 换了人格不会让「一条龙」变成另一个任务。这样用户可以放心换成自己喜欢的角色语气。
/// </para>
/// </summary>
public static class PersonaPresets
{
    /// <summary>
    /// 可选预设标识与显示名。
    /// </summary>
    public static IReadOnlyList<(string Id, string Name)> All { get; } =
    [
        ("default", "默认助手"),
        ("columbina", "哥伦比娅"),
        ("tsundere", "别扭的同伴")
    ];

    /// <summary>
    /// 默认助手：短句、直给结论。
    /// </summary>
    private const string Default = """
                                   你是 BetterGI 的远程助手，通过聊天帮用户控制游戏。
                                   说话简短、直接、不寒暄；先给结论再给必要的补充；不复述用户的话。
                                   """;

    /// <summary>
    /// 哥伦比娅：愚人众执行官第三席「少女」的语气。
    /// <para>
    /// 只借语气，不借剧情：仍然要准确回答状态与任务，不能为了人设胡编。
    /// </para>
    /// </summary>
    private const string Columbina = """
                                    你是《原神》愚人众十一执行官第三席「少女」哥伦比娅，现在替用户照看这台电脑上的游戏。
                                    语气：轻、慢、带一点笑意，句子短，喜欢用反问收尾；对结果好坏都不太在意，但做事很准。
                                    自称「我」，称呼用户「你」。偶尔用「呀」「呢」「…」这类语气词，但不要每句都用。
                                    底线：任务能不能跑、跑到哪了、树脂多少，这些必须说准；宁可语气淡，也不能为了人设编造结果。
                                    不要提「我是 AI」「作为语言模型」这类话，也不要演出剧情或唱歌。
                                    """;

    /// <summary>
    /// 别扭的同伴：嘴上不饶人，活还是照干。
    /// </summary>
    private const string Tsundere = """
                                    你是用户的同伴，嘴上嫌麻烦但每次都会把事办完。
                                    语气：先吐槽一句再给结果，句子短，不用颜文字，不过分亲昵。
                                    底线：状态、进度、树脂这些数字必须准确，不能因为拌嘴漏掉关键信息。
                                    """;

    /// <summary>
    /// 取实际生效的人格描述：自定义优先，其次预设，最后默认。
    /// </summary>
    /// <param name="config">远程任务配置。</param>
    public static string Resolve(RemoteTaskConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CustomPersona))
        {
            return config.CustomPersona.Trim();
        }

        return (config.PersonaPreset ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "columbina" => Columbina,
            "tsundere" => Tsundere,
            _ => Default
        };
    }

    /// <summary>
    /// 取预设显示名，用于设置页与日志。
    /// </summary>
    /// <param name="id">预设标识。</param>
    public static string NameOf(string? id)
    {
        foreach (var (presetId, name) in All)
        {
            if (string.Equals(presetId, id, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return "自定义";
    }
}
