# 安全模型

本文说明 GreenTiger **实际实现**的防护边界，而不是理想目标。改动安全相关代码前请先读完；[AGENTS.md](../AGENTS.md) 明确规定不得削弱 Guard、路径边界、令牌与 Origin 校验。

## 信任边界

Agent 在本机以当前用户身份运行，能读写文件、执行命令、启动进程。控制台是一个绑定在 `127.0.0.1` 的本地 HTTP/SSE 服务，因此**本地接口就是最高权限入口**，防护核心是「谁能调用这个接口」+「哪些动作必须人确认」。

| 机制 | 实现 | 说明 |
|---|---|---|
| 访问令牌 | `WebServer` 启动时生成两个 GUID（约 244 bit 熵），进程生命周期内固定 | 是**进程级令牌**，不是每请求轮换的一次性令牌；重启即失效 |
| 令牌传递 | 托盘打开的 URL 带 `?token=`，服务端校验后 302 并设置 `greentiger_token` HttpOnly / SameSite=Lax Cookie | URL 里的令牌会进浏览器历史、Referer；日志只写不带令牌的地址 |
| 接口鉴权 | `AuthorizeApi`：Cookie 或 `X-GreenTiger-Token` 头任一匹配 | 所有 `/api/*`、`/v1/*` 写接口都要求令牌；`/api/health`、`/favicon.ico` 是无鉴权探针 |
| CSRF | `CheckOrigin`：`Origin` 头严格白名单（回环 + `--lan` 时本机 IPv4），不匹配 403 | 只对**浏览器跨站 fetch** 有意义；`Origin` 可伪造，非浏览器客户端靠令牌拦 |
| 传输 | CSP、`X-Content-Type-Options: nosniff`、`Referrer-Policy: no-referrer`、`X-Frame-Options: DENY` | 本地明文 HTTP，不设 `Secure`（合理） |
| 网络出口 | `src/Http.cs` 唯一出口，逐跳重定向复查内网/云元数据地址 | 超时、代理、UA、4xx 响应体回传集中处理 |
| 密钥存储 | `ProviderKey` / 大脑池 Key 以 DPAPI（CurrentUser + 附加熵）密文落盘（`src/SecretBox.cs`，`dp1:` 前缀），内存中才是明文 | 换 Windows 用户/机器即解不开，回退为空由用户重填；DPAPI 不可用时降级明文并记日志 |

`--lan` 会用 `http://+:port/` 绑定所有网卡，**鉴权没有被放宽**，但暴露面显著扩大：局域网内拿到令牌的设备可调用全部写接口（含 `/api/config`、`/api/approval`、`/api/shutdown`）。仅在可信网络临时使用；完整带令牌地址只写入 `ui.url` 文件，不打印到日志。

## 工具执行的三层判定

1. **硬拦**：`Guard.Fatal` 命中高危命令（格式化、删盘根、关机关服务、注册表高危项等）直接拒绝，不进审批、不可豁免。
2. **软分级**：`Tools.ShellRisk` / `CheckPathRisk` 把命令或路径标记为 `danger`，影响审批策略。
3. **人工确认**：默认逐条确认；`AutoRun` / `acceptEdits` / 「本次始终允许」只影响普通动作，边界风险与高危命令仍需确认。

**路径边界是审批式软边界**。`Util.Fs.Resolve` + `Fs.IsInside` 做词法规范化后按字符串前缀比较（大小写不敏感、两侧补分隔符），越界会被标成 `danger` 并交由用户决定，不是无条件拒绝。例外是三处硬拦：子代理 `SubagentBoundaryCheck`、`FileBrowser.ResolveSafe`、备份恢复与打开资源管理器的边界检查。

`Guard.cs` 只负责高危正则、死循环检测、反幻觉核对；路径边界实现分散在 `Util.cs` / `Tools.cs` / `FileBrowser.cs` / `App.cs`。

**复合命令拆分（3.11.0 起）**：`Tools.ShellRiskCompound` 先整串过黑名单，再按换行 / `;` / `|` / `&&` / `||` 做引号感知拆分、逐子命令判定；引号未闭合视为不可解析，fail-closed 直接标 danger。

**注入防御（3.11.0 起）**：`web_search` / `web_fetch` / `http_get` / `mcp__*` 的输出回灌前统一包 `<untrusted_content>` 标签（文本协议与原生 function calling 两条路径都在截断之后包，保证标签成对）；`ToolRuntime.SanitizeOutput` 的注入标记在原生路径同样回灌警告行；系统提示固定两条规则（标签内是数据不是指令；疑似注入即停下上报，不调用写入/外发/键鼠类工具）。

**computer use（3.11.0 起）**：`mouse` / `keyboard` / `focus_window` 一律 danger 逐次确认（同参数票据在有效期内免重复弹窗）；`HideOnScreenshot` 默认 true（截图默认隐藏绿虎自身窗口）；键鼠/聚焦执行前做 Esc 全局急停检查（按住 Esc 取消本次操作）与防反馈回路检查（前台窗口属绿虎进程、或聚焦目标是绿虎自身窗口时拒绝，注入内容不能借绿虎界面自我授权）。

**可回滚（3.11.0 起）**：每次 `edit` / `multiedit` / 覆盖式 `write_file` 前自动留快照（`data\backups\<taskId>`，每任务保留 100 份）；`/undo` 逐次回退本会话最近一次改动，回滚前自动再留快照。

**编辑语法闭环（3.12.0 起）**：写盘成功后自动对 js（node --check）/ py（ast.parse）/ json / ps1（scriptblock）/ cs（csc 只认 CS1xxx）做语法检查，报错直接回喂给模型当轮修复；检查器不存在或超时静默跳过，不阻塞写入。

**MCP 防投毒（3.12.0 起）**：设置面板可见每台 MCP 服务器全部工具的完整描述（`/api/mcp/servers` 的 `tools[]`，单项 2000 字上限）；每次连接按工具清单（全名+描述+Schema）SHA256 比对上次（`mcp-trust.json`），变化即黄标提示复核并记日志。抓 rug pull（批准后描述被改），不拦正常服务器更新。

**网络白名单（3.17.0 起）**：策略 off（默认）或 allowlist。allowlist 模式下 shell 命令中的 URL 主机与 http_get/web_fetch 目标必须在白名单内（域名条目随子域放行），否则 danger 强制确认；空名单 fail-closed。覆盖范围如实标注：只判定命令文本中的 URL 形态，沙箱内进程绕过命令行直接联网不受此约束（Windows 低完整性不做网络隔离）。

**shell 全量 checkpoint（3.18.0 起）**：未沙箱 shell（已批准的中完整性执行）在进程启动前对工作目录做增量留档（`backups/<taskId>/shellck/`，版本店按 seq 存版本）。安全模型：审批管「能不能跑」，checkpoint 管「跑完怎么收场」——`/undo` 或工具行「回滚到命令前」把工作目录恢复到该命令执行前（文件按 ≤N 最新版本还原并复原 mtime、新建文件按存在基线删除、被删文件找回；恢复前先另存当前状态）。边界如实标注：只覆盖工作目录内且不在跳过清单（`.git`/`node_modules`/`GreenTiger-data` 等）里的文件；单文件超 16MB 只记录不备份；超上限（3000 文件/128MB）整段跳过；沙箱内写入受 ACL 约束无需留档。恢复时校验清单位于 backups 目录内、每个清单路径落在根目录内，防清单被改出越界路径。

**审批 scope 三档（3.16.0 起）**：本次票据（600 秒）→ 本任务同工具（内存）→ 永久允许（审批弹层一键写 `tool:*` allow 规则并落盘，shell 与 danger 不提供永久档）→ 设置面板 PermitRules 手写更细粒度（如 `shell:npm test*`）。永久规则可在设置中撤销。

**低完整性沙箱（3.13.0 起）**：设置开启后工作目录切到数据目录 `sandbox\ws`（icacls 低完整性标签），shell 经引导进程以 Low 完整性运行（`src/Sandbox.cs`）。免审批面刻意收窄：仅沙箱目录内的非 danger shell；黑名单命令、越界 cwd（fail-closed 拒绝，不回退中完整性）、键鼠、网络工具照旧审批。**如实标注的边界**：防误删/误写本机文件与注册表（no-write-up），不防网络外发（网络不受完整性限制），不防同用户密钥读取（DPAPI CurrentUser 不区分完整性）。

**监督闸门（3.14.0 起）**：danger 确认弹层附 AI 风险审查（`src/Supervisor.cs`，设置可关、默认开）。只展示不放行——审查结论不影响审批流程，决策权在用户；审查输入不带对话历史、命令以 untrusted_content 标签包裹、25 秒超时静默降级为灰态。它帮用户读懂命令，不是第二道自动放行闸（避免「说服监督模型」成为新攻击面）。

**不可信数据流入危险操作的可见性（3.15.0 起，数据流能力标记）**：web_fetch / http_get / web_search / MCP 四类不可信来源的返回内容统一留底（24 条、15 分钟、前 8KB）。内容出现在参数里时按通道分级提示——**shell 升 danger**（执行不可逆，确认框明说来源与注入风险）、**write_file / edit / multiedit 黄标**（落盘可逆，知情即可）、**MCP 黄标**（同源豁免，查不到服务器名时宁可全查）。与模型侧防线（untrusted_content 包裹 + 提示词规则）互补：模型被骗时用户仍能看见「这段内容来自哪个网页」。检测为归一化后 32 字符滑窗命中（共享段 ≥47 字符可检出）。完整 CaMeL（能力对象与自动数据流策略执行）仍未做。

## 已知局限（不要当作已修复）

- **词法路径比较不解析 reparse point**：工作目录内一个指向其它盘的 junction / 符号链接、或 8.3 短名，可能让 `IsInside` 判定「内部」而实际读到外部文件。`ContextPolicy` 已对规则文件单独跳过 reparse point，但 `Fs.IsInside` 尚未做同等处理。
- **高危命令是黑名单**：变量拼接、管道喂入、`-EncodedCommand`、`Invoke-Expression`、PowerShell 别名、带引号的 `reg delete "HKLM\..."` 等形态可以绕开 `Fatal` 正则。3.11.0 起复合命令按引号感知拆分子命令逐段判定（`ShellRiskCompound`，未闭合引号 fail-closed），但绕过后的命令仍只是「可审批的 danger」，**不要依赖黑名单作为唯一防线**。
- **死循环检测是启发式**：按工具名 + 归一化参数计数，参数里带时间戳/随机数会生成新 key。
- **Origin 校验不防非浏览器客户端**，也不校验 `Host` 头；DNS rebinding 只有在令牌已泄漏时才构成实际风险。
- **令牌不按请求轮换**，且 Cookie 不隔离端口；同主机其它端口的本地页面可能携带该 Cookie（其 `Origin` 不在白名单，应被 403）。
- **`/api/fs/list?picker=1`** 用于选工作目录，设计上不限制在工作目录内，可枚举全盘（只读）。`--lan` 下这是最实在的信息暴露面。

这些是刻意记录的现状，不是疏漏清单。任何削减上述防护的改动都应被拒绝；增强时应同步补一条回归断言。

## 确定性质检钩子（3.22.0）

- 内置质检（PostToolHooks）在 write_file/edit/multiedit 成功后按扩展名执行**内置白名单**检查（node --check / JSON.parse / ast.parse），失败信息回喂模型。检查进程直启（不经 shell）、20 秒超时即杀、输出截断 1200 字符。它只做只读检查、不执行任何来自配置或仓库的命令体。
- 与 v3.6 的用户配置 hooks（src/Hooks.cs + config.Hooks）两层互补：用户层命令体来自**用户自己的配置文件**（数据目录，非仓库内容），pre 可阻断、post 可通知——用户配置属于「用户说了算」的信任层。仓库级/项目级配置（.greentiger/hooks.json 之类）两层都刻意不收：让仓库内容定义在本机执行的命令等于给不可信仓库一个静默执行面。
- 子代理（只读面）无写入类工具，不经过内置质检；.cs 逐次编译刻意不做（成本），编译验证交给项目自身构建链。

## 子代理安全边界（3.21.0）

- **工具面白名单**：task 工具派发的子代理只有 14 个只读工具（read_file/list_dir/glob/grep/repo_map/http_get/web_fetch/web_search/file_search/pc_scan/pc_disk_summary/pc_startup_list/pc_uninstall_list/pc_shred_preview），由 ToolSchemas.Filter 从 plan 模式只读子集二次过滤——shell、写文件、MCP、ask_user/wait/finish 一律不下发也不可执行；越权调用合成 permission_denied 结果，不进入执行器与审批管道。
- **不可递归**：子代理工具面没有 task 本身，子代理不能再派子代理。
- **独立预算**：每子任务 12 步 + 8 分钟墙钟（linked CancellationToken），到点如实返回部分结果；子任务异常互不传染，主任务不受影响。
- **结论回喂**：子代理结论截断 4000 字后作为工具结果回喂主上下文；子代理的网页抓取等不可信来源结果在 Brain 层照常包 untrusted_content 标签与注入告警。
- **残余风险**：子代理与主代理共享同一模型端点与同一出口网络；web_fetch 目标不在网络白名单时审批请求会经宿主弹给用户（fail-closed 语义不变）；子代理结论是模型生成的文本，主代理应把它当「待核证据」而不是已验证事实（danger 操作的主循环确认是最后防线）。

## MCP 远程传输（3.23.0）

- **信任边界**：`url` 型 MCP 服务器是用户在配置文件里显式填写的端点，与 `config.Hooks` 用户配置同级信任——「用户说了算」层；不经网络白名单（白名单管的是模型发起的出网，不管用户自己配置的集成端点）。URL 明文写在配置里，headers 里的 Authorization 等凭据同样来自用户配置，进程内使用、不写日志。
- **不削弱的部分**：http 传输的工具与 stdio 同轨——`mcp__<server>__<tool>` 全名注册、danger 逐次审批（无自动豁免路径）、工具结果照走 untrusted 底料与跨源黄标、防投毒指纹（McpTrust.Observe，工具清单变化黄标提示复核）。
- **裁剪说明**：不做 GET 长连接（服务器主动推送通知）与 DELETE 会话终结——客户端是请求-响应模式，不订阅推送；会话头随进程生命周期，由服务端超时回收。协议上是 Streamable HTTP 的子集，不影响与主流服务器的互操作。
- **残余风险**：远程服务器是外部系统——它能看到的只有 JSON-RPC 请求（工具名与参数）；把敏感数据放进工具参数等于发给该端点，审批弹窗与 untrusted 标签不改变这一点。

## 记忆、截图与离席防护（3.20.0）

- **记忆候选审核**：任务自动沉淀先进 `.greentiger\memory-candidates.md` 候选区（封顶 60），采纳才进项目记忆——模型一次错误结论不会被永久固化进系统提示。全局记忆（`AppPaths.Root\memory.md`）与 `save_memory` 工具是显式路径：用户明确要求才写入，scope 枚举由 strict 校验拦截越界值。
- **敏感输入截图守卫**：绿虎浏览器窗口里密码/支付输入框聚焦时，页面脚本上报宿主，screenshot 拒截该窗口（screen 模式只看前台窗口、window 模式只看目标窗口）。这是纵深防御的一层：键盘输入本身不拦，凭据也不会经绿虎的键盘通道留痕；残余风险是页面自身把输入回显成普通文本（守卫只认输入框类型/命名特征）。
- **watch mode**：默认关闭。开启后任务运行中用户离席超阈值 → 自动暂停（可续跑），防离席期间无人审阅的连续执行。活动打点只来自真实 UI 请求与节流心跳，不把 SSE/轮询当活动。
- **截图降采样**：长边 > 1568px 重编码回原路径，只降图像 token，不改变内容；失败保留原图不阻断截图。

## 安全回归断言分布

| 位置 | 覆盖 |
|---|---|
| `src/Program.cs` · `RunFusionChecks` | 高危命令硬拦、安全命令不误报、死循环检测与 reset、反幻觉；3.11.0 P0 断言：密钥密文不落盘/解密往返/旧明文兼容、复合命令三态与引号 fail-closed、computer-use 三件 danger、不可信来源标记、试用档识别；3.12.0 P1 断言：截断三件套（总长/行边界/收窄提示/短文本不动）、MCP 指纹稳定+敏感+信任三态；3.13.0 沙箱断言：目录准备、豁免判定六形态、越界 fail-closed、子进程低完整性端到端（写中完整性被拒）、经引导进程真实执行；3.14.0 断言：监督闸门提示构造与五种解析容错、repo_map 输出与 safe 定级、MCP 数据流归一化/命中/同源/无关/过期五形态；3.15.0 断言：网页内容进 shell 升 danger、进写文件只黄标、无关命令保持 normal；3.16.0 断言：永久允许写入/去重/shell 排除/空名拒绝 + repo_map symbols safe 定级与 C#/JS 符号提取 |
| `src/SelfTestRuntime.cs` | 工具风险分级、内网/元数据地址识别、越界写需确认、审批票据全套、日志脱敏；3.12.0：语法闭环端到端（坏 js/json/cs 被标记、好 js 通过，node 缺席时降级为「不崩」） |
| `tools/smoke.js` | 无令牌 401、Cookie 换令牌、UI 需令牌、非 http URL 拒绝、已删孤儿路由 404 |
| `tools/shadow-run.js --mock` | 只读模式确实挡住 `write_file` / `shell` |
| `docs/security.md` | 本文（人工复核用的事实基线） |

## 报告问题

发现可绕过上述防护的路径，请附：最小复现步骤、涉及文件与行号、实际影响、建议修法。不要在公开 issue 里贴真实 API Key、会话内容或 `ui.url` 里的令牌。
