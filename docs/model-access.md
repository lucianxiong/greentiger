# 模型接入

绿虎只有一条模型通道：**OpenAI 兼容端点**。所有接入都是「把这个通道指向谁」，所以下面几种方式可以随时来回切，不需要重装。

## 1. 内置目录：5 家官方服务商

| 服务商 | 端点 | 模型 |
|---|---|---|
| DeepSeek 开放平台 | `https://api.deepseek.com` | `deepseek-v4-pro` |
| 阿里通义千问（百炼） | `https://dashscope.aliyuncs.com/compatible-mode/v1` | `qwen3.8-max` |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` | `glm-5.3` |
| Moonshot Kimi | `https://api.moonshot.cn/v1` | `kimi-k3.1-pro` |
| Claude 官方（OpenAI 兼容层） | `https://api.anthropic.com` | `claude-fable-5-2` / `claude-opus-4.6` |

在设置里选服务商、填 API Key 即可用，模型列表就是上表这些。

**目录里没有的档位**：各家 flash / plus、免 Key 公益池、本机占位端点都不进目录。它们能验证连通性，但撑不起 Agent 任务的参数质量——框架里的兜底解析是安全网，不是质量保证。需要时用「自定义端点」自己填。

## 2. Claude 官方兼容层

Anthropic 提供 OpenAI SDK 兼容层，不用改协议就能接：

```bash
/connect claude sk-ant-xxxx
```

**兼容层的已知差异**（绿虎已适配前两条）：

| 差异 | 影响 | 绿虎处理 |
|---|---|---|
| `strict` 参数被忽略 | 工具参数不保证严格符合 schema | 本地 strict 校验不依赖服务端 |
| Claude 4.7+ / Fable / Mythos 对 `temperature < 1` 直接 400 | 不改每个请求都失败 | `ModelQuirks.OmitTemperature` 命中即删该字段 |
| Claude 系缺 `max_tokens` 会被拒 | 不改就 400 | `ModelQuirks.NeedsMaxTokens` 命中即补 8192 |
| 不返回 thinking、不支持 prompt caching | 省钱与推理可见性打折 | 走查记录里的缓存命中显示 0，属正常 |

海外直连在部分网络下不可达。`/connect claude` 会实测并如实报错，不会假装接上了。

## 3. 自定义端点 / 中转站

目录之外的任何 OpenAI 兼容端点都走这条路（国内聚合网关、自建 One-API / New-API、OpenRouter、本机 llama.cpp / Ollama 都算）：

```bash
/connect relay https://你的中转站地址/v1 sk-xxxx [模型名]
```

- 地址填到 `/v1` 为止，绿虎会幂等拼接（已有 `/v1` 不会重复）。
- 模型名可省略：拉 `/v1/models` 后自动挑一个。
- 探测顺序：`/v1/models` → `/models` → `/api/tags`（自建网关常见形状）。
- 探测失败**不改任何配置**，不会把能用的旧配置弄丢；回执里不打印 Key。

**本机模型**：llama.cpp 必须带 `--jinja`，否则工具调用退化成纯文本；Ollama 用 `/v1` 端点。AutoPool 不主动探测本机端点（服务不一定在跑，白探一轮就是每次任务多等一次超时）。

**挑中转站的三条硬指标**（别只看单价）：① 是否真支持 `tools` 字段与 SSE 流式；② 是否有 cache 计价与用量明细；③ 是否支持对公 / 发票。绿虎只做连通性验证，不替你判断商家信誉。

## 4. 接入后怎么确认真的能用

```bash
/connect           # 看当前大脑与各条路径的引导
/model             # 列出端点返回的真实模型列表，点哪个换哪个
```

再跑一个最小任务（例如「列出当前目录前 5 个文件」），然后在 `/walkthrough` 里看这次调用的时间线与 token：有工具调用、有 token 计量，才算真接上了。

## 5. 代码落点

| 文件 | 职责 |
|---|---|
| `src/Connect.cs` | `/connect` 解析、端点归一化、模型探测与挑选、接入回执；`ModelQuirks` 收敛模型差异 |
| `src/Providers.cs` | 内置目录（5 家官方服务商）、`IsTrialTier`、`IsLocalEndpoint` |
| `src/Brain.cs` | `ApplyModelQuirks`：按模型补 `max_tokens` / 删 `temperature`，其它模型零影响 |
| `src/ContextPolicy.cs` | 按模型窗口推导上下文预算：Fable / Mythos 按 1M，其余 Claude 200K |
