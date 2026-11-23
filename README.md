# GreenTiger（绿虎）

Windows 桌面 Agent。模型负责规划，工具在本机执行，闭环是「规划 → 执行 → 观察 → 再规划」。

只有一个模型通道：任意 OpenAI 兼容端点，不绑定厂商。产物是单个 exe：用 Windows 自带的编译器构建，零 NuGet，界面作为内嵌资源打进程序集，不需要装运行时、不需要前端构建链。

## 快速开始

1. 运行 `GreenTiger.exe`，从托盘打开控制台
2. 选工作目录——Agent 只能在这里读写文件、执行命令
3. 选服务商、填 API Key（目录里没有的端点用「自定义」填到 `/v1`）
4. 描述任务。每次工具调用默认弹确认框，可以「允许一次」或「本次始终允许」

## 模型

内置 5 家官方服务商，每家只留当前旗舰模型：

| 服务商 | 模型 |
|---|---|
| DeepSeek | `deepseek-v4-pro` |
| 通义千问（百炼） | `qwen3.8-max` |
| 智谱 GLM | `glm-5.3` |
| Moonshot Kimi | `kimi-k3.1-pro` |
| Claude（官方兼容层） | `claude-fable-5-2` / `claude-opus-4.6` |

flash / plus 之类的小档位不进目录——它们能验证连通性，撑不起 Agent 任务。需要时用「自定义端点」自己填，本机 llama.cpp / Ollama 也走这条路（llama.cpp 必须带 `--jinja`）。

- `/connect claude <Key>` 接 Claude 官方兼容层；`/connect relay <地址> <Key>` 接中转站或自建网关。
- 默认走原生 function calling（工具 Schema 随请求下发、SSE 流式、精确计 token）；端点不支持 `tools` 时自动降级为文本围栏协议，并按端点记住。
- 切换服务商或模型会清空该大脑的历史，旧模型的上下文不污染新模型。

## 工具（36 个）

文件读写与检索（`read_file` / `write_file` / `edit` / `multiedit` / `glob` / `grep` / `list_dir` / `repo_map`）、执行（`shell` 含后台运行与进程树终止、`shell_output`、`shell_kill`）、网络（`web_fetch` 正文提取 + 15 分钟缓存、`http_get`、`web_search`）、桌面（`screenshot` / `mouse` / `keyboard` / `focus_window`）、协作（`ask_user` / `plan` / `todo` / `task` 只读子代理 / `save_memory`）、本机运维（搜索、体检、磁盘概览、启动项、卸载、清缓存、粉碎），以及 `mcp__*` 派发的 MCP 工具。

- 多个只读调用并发执行，含写调用的批次保持顺序。
- 每次编辑前留快照；未沙箱的 shell 执行前对整个工作目录做增量 checkpoint，`/undo` 能回到命令执行前。

## 安全

三层：**硬拦**（格式化、删盘根、注册表高危项，不可豁免）→ **软分级**（路径越界、复合命令逐段判定、不可信内容流入 shell 时升 danger）→ **人工确认**（默认逐条，shell 与 danger 无永久档）。

本地接口用进程级随机令牌（重启即失效）+ Origin 白名单 + HttpOnly Cookie，默认只监听 `127.0.0.1`；网页 / 搜索结果 / MCP 返回统一包 `<untrusted_content>`，出现在命令参数里时确认框会写明来源。

**边界如实说**：路径比较是词法的（不解析 junction / 符号链接）、高危命令是黑名单（有绕过形态）、死循环检测是启发式。完整清单见 [docs/security.md](docs/security.md)。

## 每个任务留一份可审阅的记录

任务结束写一份 Markdown 到 `工作目录\.greentiger\walkthroughs\`：目标、token 与缓存命中、**证据强度**、时间线、改动与备份。`/walkthrough` 查看。

证据强度只有四档：`已客观验证` / `验收未通过` / `仅模型自述` / `未完成`。判定只看验证器记录与任务终态，不看模型措辞。**「模型说做完了」不等于做完了**，这是这个项目最在意的一条。

## 本地经验蒸馏（`/evolve`）

每个终态任务写一条证据（结果、归一化失败签名、元规则、步数与 token）。同类元规则在 ≥3 个任务、≥2 天重复才蒸馏成候选；候选过确定性门禁并由你 `/evolve accept` 才启用；启用后成功率相对基线跌 ≥20 点自动回滚。

全流程不调模型、不发网络请求，只蒸馏「怎么做事」的元规则，不蒸馏领域技巧。设计见 [docs/self-evolution.md](docs/self-evolution.md)。

## 构建与测试

```bash
npm run check:version     # 版本 / 内嵌资源清单一致性
powershell -ExecutionPolicy Bypass -File build.ps1   # → dist\GreenTiger.exe
dist\GreenTiger.exe --self-test
npm run check:ui          # 界面脚本静态检查
npm test                  # smoke + recovery + regression
npm run e2e               # 真机点一遍（需要 Node 20 + 本机 Edge）
npm run verify:all        # 发布前全量
```

只要 Windows + .NET Framework 4.x + Node 20（仅 e2e 需要）。版本唯一事实源是 `src/Config.cs` 的 `AppInfo.Version`，升版本用 `npm run bump:version`。

## 现状与已知缺口

- **旗舰模型只验证到「协议通」**：工具协议、流式解析、审批链路有自检与 e2e 覆盖，但目录里的模型名来自各家公开文档，没有逐个用真实 Key 跑过长任务。真实模型下的翻车样本是最缺的。
- **只支持 Windows**，界面只有中文。
- **安全是审批式，不是沙箱级**：见上文「边界如实说」。
- **经验蒸馏只覆盖元规则**，证据不足时宁可不产出。

## 许可

GPL-3.0，见 [LICENSE](LICENSE)。

[架构](docs/architecture.md) · [安全模型](docs/security.md) · [长上下文](docs/long-context.md) · [模型接入](docs/model-access.md) · [参与开发](CONTRIBUTING.md) · [变更记录](CHANGELOG.md)
