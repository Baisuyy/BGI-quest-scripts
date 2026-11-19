# 日志：落盘、诊断包与「上传」的现状

> 相关代码：`App.xaml.cs`（Serilog 装配）、`Helpers/RepeatSuppressorSink.cs`、
> `Helpers/ExceptionDedupTextFormatter.cs`、`Core/Special/SpecialDiagnostics.cs`、
> `GameTask/LogParse/LogParse.cs`

## 1. 现在的链路

```
ILogger / Serilog.Log
   ├─ 文件 sink      log/better-genshin-impact<日期>.log   ← 本文关注的部分
   ├─ 悬浮框 sink    仅「遮罩启用且日志框可见」时写，按 UI 级别过滤
   ├─ ExternalLogHub 给网页 / 外部端的日志回放
   └─ ErrorDiagnosticsSink  出现 ERR 就攒一个诊断包（10 分钟一次）
```

- 落盘级别由 `CommonConfig.LogFileLevel` 控制，**默认 Debug**：排查问题时够用，代价是一天可能几十 MB、上百万行。
- 按天滚动，保留 31 个文件 / 21 天；`shared: true`，别的进程（看日志的工具、我们自己的诊断包）能同时读。
- 诊断包 = `log/diagnostics/BGI-diag-<时间>.zip`，内含 `summary.txt`（版本、系统、脱敏配置摘要、最近 200 条 ERR）+ 最近 4 个日志的尾部（单文件最多 6 MB、总预算 24 MB）。界面在「通用设置 → 生成诊断包」。
- **没有任何自动上传**：客户端不往服务器发日志，也没有云端日志服务。所谓「反馈」就是把 zip 拖进 issue / 群聊。

## 2. 这次是从一份真实日志倒推的

对象：用户提供的 `better-genshin-impact20261118.log`（993 KB，6064 行，00:03 → 22:37）。

| 现象 | 数字 |
|---|---|
| 调用栈行（`at …` / `---> …` / `Exception:`） | 4562 行，占 **75%** |
| 「网关连接中断，60 秒后重连」 | 253 次，每次附 19 行 `HttpRequestException` 栈 |
| 「连不上：证书校验未通过」 | 133 次 |
| `POST bots.qq.com 失败` / `GET api.sgroup.qq.com 失败` | 275 / 115 次（DBG） |
| 真正的 ERR | 1 条 |

也就是说：**一条可预期的重连循环，把 3/4 的日志变成了重复的栈**。`RepeatSuppressorSink` 只折叠「连续相同」的行，而这里重连告警总被 HTTP 那行 DBG 隔开，一次都没命中。

另外两个结构性缺陷：

1. 行首只有 `HH:mm:ss.fff`。按天滚动时够用，但跨午夜后既排不出先后，`ProblemsSince` 也只能退化成「不看时间、只取最后几条」。
2. 单文件没有大小上限。级别设 Debug + 长时间挂机，一个文件可以长到几百 MB，记事本打不开，诊断包还得靠「只截尾部」来兜。

## 3. 改了什么

| 改动 | 位置 | 效果 |
|---|---|---|
| 行首加日期 | `App.xaml.cs` 模板 | `[2026-11-18 12:34:56.789] [WRN] …`，跨午夜可排序 |
| 异常栈按签名去重 | `Helpers/ExceptionDedupTextFormatter.cs` | 同一条错误 5 分钟内只展开一次栈，其余写「栈同上，首次 HH:mm:ss；第 N 次，已省略」 |
| 单文件 64 MB 上限 + 滚动 | `App.xaml.cs` | 超过就切 `_001`，不会长出打不开的文件 |
| 解析器兼容新旧两种行 | `SpecialDiagnostics.ProblemLine`、`LogParse` | 老日志照样能读；新日志的 `ProblemsSince` 精确到「任务开始之后」 |

签名 = 最内层异常类型 + 消息 + 最内层栈帧。只按类型去重会把「同一个 `HttpRequestException`、不同调用点」混成一条，所以栈帧也在里面。

实测（同一段 253 次重连 + 253 行 HTTP DBG，Debug 级别）：

| | 行数 |
|---|---|
| 改动前 | 约 5300 行（506 条事件 + 253 × 19 行栈） |
| 改动后 | 769 行（506 条事件 + 252 条省略提示 + 11 行栈） |

## 4. 上传：自建接收端（已落地）

原先的判断是「没有服务端就别做上传」，现在服务器有了（`154.37.219.158`，我们的机器），这一节改写成实际方案。

服务端是独立的零依赖单文件，代码与协议在 `bgi-loghub` 仓库：

- 地址：`https://kelibb.top/loghub`（子域名定了之后只换域名，路径与签名规则不变）
- 协议：`BGI-LOG1`，HMAC-SHA256 覆盖「方法 + 路径 + 时间戳 + nonce + 正文摘要」
- 特性：5 分钟时间窗、nonce 一次性防重放、按 sha256 幂等去重、单次 64 MB、单 key 512 MB/天
- 客户端参考实现：`loghub/docs/client-example.cs`，是一个可以直接拷进来的 `LogUploader`

当初列的几个成本，现在这样处理：

| 成本 | 处理 |
|---|---|
| 鉴权 | 每客户端一套 key，泄露就 `key disable`，不用改代码 |
| 存储 | 按 sha256 寻址，30 天自动清理，磁盘低于 2 GB 停收 |
| 隐私 | **默认关闭**。开关旁写清传哪些文件、传到哪；只传 `log/` 下的日志，不碰 `config/` |
| 合规 | 自建单租户，不对外提供服务，不收第三方数据 |

客户端还没接。接入时按这个顺序：

1. 配置项 `LogUploadEnabled`（默认 false）+ `LogUploadUrl` + `LogUploadKey` + `LogUploadSecret`；
2. `LogUploader.UploadAsync` 包一层，失败只写 WRN，绝不弹窗、不阻塞任务；
3. 界面上给「上传最近日志」按钮，成功回一个可复制的 id，方便对着 id 查。

真正让「反馈」变轻的仍然是包要小、要有信息量：日志瘦身（第 3 节）、诊断包带环境摘要、`ProblemsSince` 能让机器人直接说「刚才出过什么」。上传只是省掉「让用户找文件」这一步。

## 5. 还没做

- **结构化事件**：现在全靠人读文本。把「重连 / 证书失败 / 任务失败」写成固定字段的事件，机器人才好统计。
- **按小时的计数摘要**：现在要靠 grep。日志尾部附一段「本小时 WRN 频次 TOP5」会更好用。
- **默认级别**：Debug 对普通用户偏啰嗦，等结构化事件补上以后再考虑降到 Information。
