# GreenTiger（绿虎）

Windows 桌面 Agent。模型负责规划，工具在本机执行，闭环是「规划 → 执行 → 观察 → 再规划」。

只有一个模型通道：**任意 OpenAI 兼容端点**。不绑定厂商，不要求登录特定客户端。

产物是单个 exe：C# 编译用 Windows 自带的 `csc.exe`，零 NuGet，界面作为内嵌资源打进程序集，不需要装运行时、不需要前端构建链。

## 快速开始

1. 运行 `GreenTiger.exe`，从托盘打开控制台
2. 选一个**工作目录**——Agent 只能在这里读写文件、执行命令
3. 接模型：内置 5 家官方服务商，选一家填 API Key；其他端点用「自定义」填到 `/v1` 为止
4. 描述任务。每次工具调用默认弹确认框，可以「允许一次」或「本次始终允许」

## 模型

**内置目录只收官方旗舰，每家只留真正能扛 Agent 任务的模型**：

| 服务商 | 模型 |
|---|---|
| DeepSeek 开放平台 | `deepseek-v4-pro` |
| 阿里通义千问（百炼） | `qwen3.8-max` |
| 智谱 GLM | `glm-5.3` |
| Moonshot Kimi | `kimi-k3.1-pro` |
| Claude 官方（OpenAI 兼容层） | `claude-fable-5-2` / `claude-opus-4.6` |

各家 flash / plus / 免 Key 公益池都**不进目录**：那些档位只能验证连通性，拿来跑正经任务是在给弱模型的畸形输出打补丁。需要它们（或本机 llama.cpp / Ollama、自建网关、中转站）时，用「自定义端点」自己填——目录不替你做这个决定。

- **接 Claude**：`/connect claude <Key>` 走官方兼容层；直连不通就走 `/connect relay <中转站地址> <Key> [模型]`。
- **本机离线**：llama.cpp 必须带 `--jinja` 才有工具调用，缺了只会输出纯文本。
- **换模型即换上下文**：切换服务商或模型会清空该大脑的历史并让探测缓存失效。
- **附件**：图片（png/jpg/webp/gif，≤4 张、单张 ≤6MB）以多模态 `image_url` 下发；文本类附件内联正文（各截断 6000 字）。走文本围栏协议时图片会明确告知未送达，不假装看过。
- **双协议**：默认原生 function calling（工具 Schema 随请求下发、SSE 流式、精确计 token）；端点不支持 `tools` 字段时自动降级为文本围栏协议，并按端点记住，不会反复重试。

## 工具（37 个）

`shell`（支持 `background` 后台执行）`shell_output` `shell_kill` `read_file` `write_file` `edit` `multiedit`（批量替换，全部成功才写盘）`glob` `grep` `list_dir` `repo_map`（目录树 + 符号索引）`screenshot` `mouse` `keyboard` `focus_window` `upload_file` `web_fetch`（抓页面→可读正文，15 分钟缓存）`http_get` `web_search` `clipboard` `wait` `ask_user`（选项弹窗提问）`finish` `plan` `todo` `task`（只读子代理）`save_memory`，本机运维一组 `everything_search` / `pc_scan` / `pc_disk_summary` / `pc_startup_list` / `pc_uninstall_list` / `pc_uninstall_launch` / `pc_clean_cache` / `pc_shred` / `pc_shred_preview` / `pc_launch_app`，以及 `mcp__*` 派发的 MCP 工具。

- **并行只读**：模型一次发起的多个只读调用并发执行，结果按序回灌；含写调用的批次保持顺序。
- **后台命令**：`background:true` 起 dev server / watch / 长构建，立刻返回 id；`shell_output` 轮询（512KB 滑动窗口），`shell_kill` 终止整棵进程树。
- **改动可回滚**：每次 `write_file` / `edit` 前留原文快照；未沙箱的 shell 在执行前对整个工作目录做增量 checkpoint，`/undo` 能回到命令执行前。
- **MCP**：stdio 与 Streamable HTTP 两种传输；每次连接按工具清单（全名 + 描述 + Schema）做 SHA256 比对，描述被改会黄标提示复核。

## 安全

三层判定：

1. **硬拦**——格式化、删盘根、关服务、注册表高危项，直接拒绝、不可豁免。
2. **软分级**——路径越界、复合命令按引号感知拆分后逐段判定、不可信来源的内容流进 shell 时升 `danger`。
3. **人工确认**——默认逐条；`AutoRun` / `acceptEdits` 只影响普通动作，`danger` 与 shell 仍需确认。

另外：本地接口用**进程级随机令牌**（重启即失效）+ Origin 白名单 + HttpOnly Cookie，默认只监听 `127.0.0.1`；不可信来源（网页、搜索结果、MCP 返回）统一包 `<untrusted_content>` 标签，出现在命令参数里时确认框会写明来源；可选 Windows 低完整性沙箱。

**边界如实说**：路径比较是词法的（不解析 junction / 符号链接）、高危命令是黑名单（有绕过形态）、死循环检测是启发式。完整清单见 [docs/security.md](docs/security.md) 的「已知局限」——那里没有把没修的写成修好的。

## 每个任务留一份可审阅的记录

任务结束会在 `工作目录\.greentiger\walkthroughs\` 写一份 Markdown：目标、token 与缓存命中、**证据强度**、执行时间线、改动与备份、未完成与风险。`/walkthrough` 看最近 10 条。

证据强度只有四档：`已客观验证` / `验收未通过` / `仅模型自述` / `未完成`。判定只看验证器记录与任务终态，不看模型措辞；成功但无法客观验证的会额外写一条 warn。**「模型说做完了」不等于做完了**，这是这个项目最在意的一条。

## 本地经验蒸馏（`/evolve`）

每个终态任务写一条证据到 JSONL：结果、归一化失败签名（路径 / 文件名 / 数字都折叠，所以「找不到 C:\a\x.txt」和「找不到 D:\b\y.txt」是同一个桶）、元规则、步数与 token。

同类元规则在 **≥3 个任务、≥2 天** 重复出现才蒸馏成候选；候选要过确定性门禁（格式 / 篇幅 / 安全措辞 / 证据天数 / 不覆盖手写技能）并由你 `/evolve accept` 才启用；启用后 6 个任务内成功率相对基线跌 ≥20 点，自动回滚并留档。

**全流程不调模型、不发网络请求**，纯本地 JSONL 判定。只蒸馏六类元规则（先验收、先计划、失败两次换写法、写前先读、先探环境、卡住就问），不蒸馏领域技巧——后者跨模型迁移会负迁移。

## 界面

三栏：左侧会话与记忆，中间对话与输入，右侧工作台（产物 / 任务 / 日志）。配色固定绿 / 黑 / 白，深浅两套主题共用一套设计令牌。

- 工具调用是行不是卡：`⏺ 工具名 参数`，折叠时用 `⎿` 带一行结果摘要。
- 对话按消息 key 做增量渲染，长会话流式输出不会全量重算语法高亮。
- 命令面板（`Ctrl+K`）、斜杠命令、`@` 文件三处共用一套模糊打分。
- 动效服务反馈：按压回弹、开关变形、触点水波、成功描画；设置里可整体关闭，也尊重 `prefers-reduced-motion`。

## 构建与测试

```bash
npm run check:version      # 版本 / 内嵌资源清单四处一致性（build.ps1 会自动调用）
npm run check:deps         # Node / playwright-core / Edge / 编译器 / WebView2
powershell -ExecutionPolicy Bypass -File build.ps1   # → dist\GreenTiger.exe
dist\GreenTiger.exe --self-test                      # 单元 + 循环级断言
npm run check:ui           # 内联脚本：语法 + 死 id 引用 + 图标名
npm test                   # smoke + recovery + regression（含 build + 自检 + 评测）
npm run e2e                # 真机点一遍（playwright-core 驱动本机 Edge，不下载浏览器）
npm run verify:all         # 发布前一键全量
```

只要 Windows + .NET Framework 4.x（系统自带 `csc.exe`）+ Node 20（只有 e2e 需要）。不需要 Visual Studio、NuGet、前端打包器。

版本唯一事实源是 `src/Config.cs` 的 `AppInfo.Version`；升版本只用 `npm run bump:version <x.y.z>`。改 UI 必须升版本——界面是内嵌资源，按版本戳覆盖数据目录里的 `ui/`。

## 现状与已知缺口

功能面比验证面宽，这些是实话：

- **旗舰模型只验证到「协议通」**：自检和 e2e 覆盖工具协议、流式解析、审批链路，但目录里的旗舰模型名来自各家公开文档，没有逐个用真实 Key 跑过长任务。真实模型下的翻车样本是目前最缺的东西。
- **只支持 Windows**：构建依赖系统 `csc.exe`，浏览器层依赖 WebView2，沙箱走 Win32 低完整性标签，没有跨平台计划。
- **界面只有中文**，没有 i18n 层。
- **安全是审批式，不是沙箱级**：见上文的「边界如实说」。
- **经验蒸馏只覆盖六类元规则**，证据不足时宁可不产出。

## 许可

GPL-3.0，见 [LICENSE](LICENSE)。基于 YaoGaoChaoLe/TigerOS 重写，命名与标识完全独立；上游署名按 GPL 保留在 `LICENSE` 与 `src/AssemblyInfo.cs`。

工程文档：[架构](docs/architecture.md) · [安全模型](docs/security.md) · [长上下文](docs/long-context.md) · [模型接入](docs/model-access.md) · [经验蒸馏设计](docs/self-evolution.md) · [参与开发](CONTRIBUTING.md) · [变更记录](CHANGELOG.md)
