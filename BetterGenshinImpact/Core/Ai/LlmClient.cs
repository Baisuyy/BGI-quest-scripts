using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoCombo.ComboBuild;
using BetterGenshinImpact.Infrastructure.Network;
using Microsoft.Extensions.DependencyInjection;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>一轮对话。图片只在 <see cref="LlmUserTurn"/> 上出现。</summary>
public abstract record LlmTurn;

/// <summary>用户输入（可选附带一张 base64 JPEG）。</summary>
public sealed record LlmUserTurn(string Text, string? ImageBase64 = null) : LlmTurn;

/// <summary>模型输出：文本 + 零到多个工具调用。</summary>
public sealed record LlmAssistantTurn(string Text, IReadOnlyList<LlmToolCall> Calls) : LlmTurn;

/// <summary>工具执行结果，回填给模型。</summary>
public sealed record LlmToolResultTurn(string CallId, string Content) : LlmTurn;

/// <summary>模型请求调用一个工具。</summary>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>工具声明（JSON Schema 参数）。</summary>
public sealed record LlmTool(string Name, string Description, JsonObject Parameters);

/// <summary>一次模型回复。</summary>
public sealed record LlmReply(string Text, IReadOnlyList<LlmToolCall> Calls)
{
    /// <summary>模型是否要求调用工具。</summary>
    public bool WantsTool => Calls.Count > 0;
}

/// <summary>
/// 一段可跨轮复用的对话。协议差异（OpenAI / Anthropic）在发请求时才展开。
/// </summary>
public sealed class LlmConversation
{
    private readonly List<LlmTurn> _turns = [];

    public IReadOnlyList<LlmTurn> Turns => _turns;

    public void AddUser(string text, string? imageBase64 = null) => _turns.Add(new LlmUserTurn(text, imageBase64));

    public void AddAssistant(LlmReply reply) => _turns.Add(new LlmAssistantTurn(reply.Text, reply.Calls));

    public void AddToolResult(string callId, string content) => _turns.Add(new LlmToolResultTurn(callId, content));
}

/// <summary>
/// 大模型传输层。只做「把对话发出去、把回复解析回来」，不关心调用方是内置决策还是脚本。
/// <para>
/// 地址校验复用自动连招那套规则：非本机回环必须 HTTPS，避免密钥明文传输；
/// 客户端关闭自动重定向，密钥不会被 3xx 带去别的地址。
/// </para>
/// </summary>
public sealed class LlmClient
{
    private const int AnthropicMaxTokens = 1024;

    /// <summary>发一轮请求。<paramref name="tools"/> 为空表示纯文本对话。</summary>
    public async Task<LlmReply> SendAsync(
        AiConfig config,
        string systemPrompt,
        LlmConversation conversation,
        IReadOnlyList<LlmTool>? tools,
        CancellationToken ct)
    {
        var isAnthropic = config.IsAnthropic;
        var baseUri = AutoComboLlmEndpoint.Resolve(config.Endpoint,
                           isAnthropic ? AutoComboLlmProvider.Anthropic : AutoComboLlmProvider.OpenAiCompatible,
                           out _)
                       ?? throw new InvalidOperationException("未配置大模型服务地址");

        var url = isAnthropic
            ? new Uri(AutoComboLlmEndpoint.ResolveAnthropicBaseUrl(baseUri).TrimEnd('/') + "/v1/messages")
            : BuildOpenAiChatUrl(baseUri);

        var payload = isAnthropic
            ? BuildAnthropicPayload(config, systemPrompt, conversation, tools)
            : BuildOpenAiPayload(config, systemPrompt, conversation, tools);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            if (isAnthropic)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", config.ApiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
            else
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + config.ApiKey);
            }
        }

        var client = App.GetService<IHttpClientFactory>()!.CreateClient(HttpClientNames.Script);
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"大模型返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        return isAnthropic ? ParseAnthropic(body) : ParseOpenAi(body);
    }

    private static Uri BuildOpenAiChatUrl(Uri baseUri)
    {
        var root = baseUri.ToString().TrimEnd('/');
        return root.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? new Uri(root)
            : new Uri(root + "/chat/completions");
    }

    private static string BuildOpenAiPayload(AiConfig config, string systemPrompt,
        LlmConversation conversation, IReadOnlyList<LlmTool>? tools)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        }

        foreach (var turn in conversation.Turns)
        {
            switch (turn)
            {
                case LlmUserTurn user:
                    messages.Add(BuildOpenAiUserMessage(user));
                    break;
                case LlmAssistantTurn assistant:
                {
                    var message = new JsonObject { ["role"] = "assistant", ["content"] = assistant.Text };
                    if (assistant.Calls.Count > 0)
                    {
                        var calls = new JsonArray();
                        foreach (var call in assistant.Calls)
                        {
                            calls.Add(new JsonObject
                            {
                                ["id"] = call.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = call.Name,
                                    ["arguments"] = call.ArgumentsJson,
                                },
                            });
                        }

                        message["tool_calls"] = calls;
                    }

                    messages.Add(message);
                    break;
                }
                case LlmToolResultTurn result:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = result.CallId,
                        ["content"] = result.Content,
                    });
                    break;
            }
        }

        var payload = new JsonObject
        {
            ["model"] = config.ModelName,
            ["temperature"] = config.Temperature,
            ["messages"] = messages,
        };
        if (tools is { Count: > 0 })
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                toolArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = tool.Parameters.DeepClone(),
                    },
                });
            }

            payload["tools"] = toolArray;
            payload["tool_choice"] = "auto";
        }

        return payload.ToJsonString();
    }

    private static JsonObject BuildOpenAiUserMessage(LlmUserTurn user)
    {
        if (string.IsNullOrEmpty(user.ImageBase64))
        {
            return new JsonObject { ["role"] = "user", ["content"] = user.Text };
        }

        var parts = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = user.Text },
            new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + user.ImageBase64 },
            },
        };
        return new JsonObject { ["role"] = "user", ["content"] = parts };
    }

    private static string BuildAnthropicPayload(AiConfig config, string systemPrompt,
        LlmConversation conversation, IReadOnlyList<LlmTool>? tools)
    {
        var messages = new JsonArray();
        foreach (var turn in conversation.Turns)
        {
            switch (turn)
            {
                case LlmUserTurn user:
                {
                    var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = user.Text } };
                    if (!string.IsNullOrEmpty(user.ImageBase64))
                    {
                        parts.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = "image/jpeg",
                                ["data"] = user.ImageBase64,
                            },
                        });
                    }

                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                    break;
                }
                case LlmAssistantTurn assistant:
                {
                    var parts = new JsonArray();
                    if (!string.IsNullOrWhiteSpace(assistant.Text))
                    {
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = assistant.Text });
                    }

                    foreach (var call in assistant.Calls)
                    {
                        parts.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.Id,
                            ["name"] = call.Name,
                            ["input"] = ParseArguments(call.ArgumentsJson),
                        });
                    }

                    if (parts.Count == 0)
                    {
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = "（无输出）" });
                    }

                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = parts });
                    break;
                }
                case LlmToolResultTurn result:
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = result.CallId,
                                ["content"] = result.Content,
                            },
                        },
                    });
                    break;
            }
        }

        var payload = new JsonObject
        {
            ["model"] = config.ModelName,
            ["max_tokens"] = AnthropicMaxTokens,
            ["temperature"] = config.Temperature,
            ["messages"] = messages,
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            payload["system"] = systemPrompt;
        }

        if (tools is { Count: > 0 })
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                toolArray.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = tool.Parameters.DeepClone(),
                });
            }

            payload["tools"] = toolArray;
        }

        return payload.ToJsonString();
    }

    private static LlmReply ParseOpenAi(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message))
        {
            throw new InvalidOperationException("响应缺少 choices[0].message：" + Truncate(body, 200));
        }

        var text = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? ""
            : "";

        var calls = new List<LlmToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in toolCalls.EnumerateArray())
            {
                if (!item.TryGetProperty("function", out var function))
                {
                    continue;
                }

                var id = item.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
                var name = function.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                var args = function.TryGetProperty("arguments", out var argsNode) ? argsNode.GetString() : "{}";
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                calls.Add(new LlmToolCall(id ?? Guid.NewGuid().ToString("N"), name, string.IsNullOrWhiteSpace(args) ? "{}" : args));
            }
        }

        return new LlmReply(text.Trim(), calls);
    }

    private static LlmReply ParseAnthropic(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("响应缺少 content：" + Truncate(body, 200));
        }

        var text = new StringBuilder();
        var calls = new List<LlmToolCall>();
        foreach (var block in content.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
            if (type == "text" && block.TryGetProperty("text", out var textNode))
            {
                text.Append(textNode.GetString());
            }
            else if (type == "tool_use")
            {
                var id = block.TryGetProperty("id", out var idNode) ? idNode.GetString() : Guid.NewGuid().ToString("N");
                var name = block.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var args = block.TryGetProperty("input", out var inputNode) ? inputNode.GetRawText() : "{}";
                calls.Add(new LlmToolCall(id!, name, args));
            }
        }

        return new LlmReply(text.ToString().Trim(), calls);
    }

    private static JsonNode ParseArguments(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
