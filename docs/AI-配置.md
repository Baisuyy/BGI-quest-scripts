# 内置 AI 配置

特别版自带三处 AI，都能指向自己的服务：

| 功能 | 用途 | 服务类型 |
|---|---|---|
| AI 智能决策（特别版新增） | 跑图卡死时接管角色脱困；脚本 `ai.Chat()` 共用 | OpenAI 兼容 / Anthropic |
| 自动连招（实验） | 识别队伍后让大模型生成连招行为树 | OpenAI 兼容 / Anthropic |
| 任务语音 | 把任务文本念出来 | OpenAI 兼容 TTS / VOICEVOX / VITS |

**新装的特别版只需要配第一处**：任务设置 → AI 智能决策（内置），字段与下表一致，
另外多了接管开关与预算。完整说明见 [`AI-内置决策.md`](<https://github.com/Baisuyy/BGI-quest-scripts/blob/bgi-special/Docs/AI-内置决策.md>)。

下面主要讲自动连招，它的配置项与内置决策同源（`provider` / `endpoint` / `modelName` / `apiKey`），
可以照抄。

## 自动连招的 AI

### 入口

任务设置页 → **自动连招（实验）** 卡片，四个字段：

| 字段 | 配置键 | 说明 |
|---|---|---|
| LLM 服务商 | `provider` | `openai`（OpenAI 兼容）/ `anthropic` |
| 决策 LLM 服务地址 | `planningLlmEndpoint` | 见下方填写规则 |
| 模型名 | `modelName` | 可手填，也可点「拉取模型列表」选 |
| 密钥 | `apiKey` | 只随请求头发送，不写日志 |

下面还有三个提示词框（`extraPrompt` / `mainPrompt` / `fallbackPrompt`），留空用内置正文。

### 地址怎么填

代码里拼路径的方式决定了写法：

- **OpenAI 兼容**：请求发到 `{你填的地址}/chat/completions`，模型列表发到 `{地址}/models`。所以**必须填到 `/v1`**。
- **Anthropic**：SDK 自己拼 `/v1/messages`，所以**填根地址、不要带 `/v1`**；留空则用官方 `https://api.anthropic.com`。

硬性约束：非本机地址必须 HTTPS，否则直接报错（密钥会明文传输）。`localhost` / `127.0.0.1` / `::1` 例外，可以跑 `http://`。

### 常见服务对照

| 服务 | 服务商 | 地址 | 模型名示例 |
|---|---|---|---|
| OpenAI | OpenAI 兼容 | `https://api.openai.com/v1` | `gpt-4.1`、`gpt-4o` |
| DeepSeek | OpenAI 兼容 | `https://api.deepseek.com/v1` | `deepseek-chat` |
| 通义千问 | OpenAI 兼容 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen-max` |
| 智谱 GLM | OpenAI 兼容 | `https://open.bigmodel.cn/api/paas/v4` | `glm-4-plus` |
| Kimi | OpenAI 兼容 | `https://api.moonshot.cn/v1` | `moonshot-v1-128k` |
| 硅基流动 | OpenAI 兼容 | `https://api.siliconflow.cn/v1` | `deepseek-ai/DeepSeek-V3` |
| Ollama（本机） | OpenAI 兼容 | `http://127.0.0.1:11434/v1` | `qwen2.5:14b` |
| LM Studio（本机） | OpenAI 兼容 | `http://127.0.0.1:1234/v1` | 载入的模型标识 |
| Anthropic | Anthropic | 留空 | `claude-sonnet-4-5` |

模型名以「拉取模型列表」返回的为准，上表只是示例。

### 必须选支持工具调用的模型

建树不是让模型写一段话，而是让它调用一整套工具（加节点、连边、设条件）。宿主最多允许 **128 轮**工具调用，Anthropic 单次输出上限 8192 token。

不支持 function calling 的模型也能跑——代码里有 `XmlToolCallFallbackParseChatClient` 会把模型塞在正文里的 XML 工具调用抠出来——但成功率和速度都明显更差。选型时优先看「Function Calling / Tool Use」这一项。

### 自检

填完地址和密钥，点 **拉取模型列表**：

- 能列出模型 → 地址、密钥、路径拼接都对
- 报 401 → 密钥错
- 报 404 → 地址少了或多了 `/v1`
- 报「地址必须使用 HTTPS」→ 换成 https 或改成本机地址

拉不到也不影响手填模型名直接跑，只是没法确认地址是否可用。

### 直接改配置文件

配置在 `User/config.json`，camelCase 命名。**改之前先退出 BetterGI**，否则内存里的配置会覆盖回去：

```json
{
  "autoComboBuildConfig": {
    "provider": "openai",
    "planningLlmEndpoint": "https://api.deepseek.com/v1",
    "modelName": "deepseek-chat",
    "apiKey": "sk-xxxx",
    "extraPrompt": "",
    "mainPrompt": "",
    "fallbackPrompt": "",
    "avatarDescriptionOverrides": []
  }
}
```

这个文件里还有账号、路径等配置，改动前先备份。

### 用在哪

- **自动连招**：任务设置页开关，启动后识别队伍 → 后台建树 → 战斗时按树执行
- **自动秘境**：勾选连招策略且未指定队伍时，秘境外识别队伍后并行建树，进本前等待完成

建树是**每次启动都重新算**的，不缓存。同一队伍反复刷本，等于反复付 token 费用；想省钱就在 `extraPrompt` 里写死偏好，或者直接用已经建好的连招配置，不要每次重建。

## 任务语音（TTS）

`User/config.json` 的 `questVoiceConfig`，三个可选引擎：

| 引擎 | 默认地址 | 说明 |
|---|---|---|
| `voicevox` | `http://127.0.0.1:50021/` | 本地，日语为主 |
| `vits-simple-api` | `http://127.0.0.1:23456/` | 本地，多语言 |
| `openai-speech` | `https://api.openai.com/v1/` | OpenAI 兼容，默认 `tts-1` + `alloy` |

OpenAI 兼容 TTS 只要求 `/audio/speech`，任何兼容服务（如硅基流动、自建 one-api）都能填，地址同样填到 `/v1/`。

## 和脚本的关系

脚本侧（JS）目前**拿不到** AI 能力：宿主没有暴露 LLM 客户端，`ocr` / `quest` 是特别版才有的识图接口。所以「让脚本自己问大模型」只能靠 `http` 对象直连 API。

三道门槛，缺一不可：

1. 调度器通用设置里启用 **JS HTTP 权限**
2. `manifest.json` 里声明允许的地址，支持 `*` 通配：
   ```json
   {
     "http_allowed_urls": ["https://api.deepseek.com/*"]
   }
   ```
3. 请求体在第三位、请求头在第四位，且是 JSON 字符串：

```js
const resp = await http.Request(
    'POST',
    'https://api.deepseek.com/v1/chat/completions',
    JSON.stringify({
        model: 'deepseek-chat',
        messages: [{ role: 'user', content: '这一步该往哪走？' }]
    }),
    JSON.stringify({
        'Authorization': 'Bearer sk-xxxx',
        'Content-Type': 'application/json'
    })
);
log.info(resp.status_code + ' ' + resp.body);
```

方法名按 C# 原样暴露（`http.Request`），不是小写。

密钥明文写在脚本里、脚本仓库又是公开的，等于公开密钥。真要用就走本地代理（`http://127.0.0.1:端口`），密钥留在代理进程里，脚本只配这个回环地址。
