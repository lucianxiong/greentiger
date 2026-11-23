# 变更记录

版本唯一事实源是 `src/Config.cs` 的 `AppInfo.Version`，升版本用 `npm run bump:version <x.y.z>`。改 UI 必须升版本（界面是内嵌资源，按版本戳覆盖数据目录 `ui/`）。

## 3.32.0

工具箱修复、Agent 接管，以及发布前的目录收敛：内置服务商精简到 5 家官方旗舰。

- **修复「立即清理」必失败**（`src/LocalOps.cs`）：扫描输出的是 `cache-<hash>`，清理白名单只比对裸 `<hash>`，两者永远不相等，所以每次点「立即清理」都报「finding_id 不在白名单内」。`ResolveCleanTarget` 现在同时接受 `cache-<hash>` 与旧裸 hash（大小写不敏感），并仍只允许 `CacheTargets` 里的 13 个目录；任意路径/未知 hash 继续拒绝。新增 7 条自检断言覆盖这两种 id、大小写与拒绝路径。
- **授权绿虎清理白名单缓存**（`src/WebServer.cs` / `ui/js/toolbox.js` / `ui/style.css`）：体检结果新增「授权绿虎清理」条。确认后只写入 `pc_clean_cache:*` 权限规则并落盘，可从 设置 → 权限规则 撤销；粉碎、卸载、启动第三方程序仍逐次确认，不放开任意路径删除。
- **Agent 工具箱补全与免打扰读操作**（`src/LocalOps.cs` / `src/ToolRegistry.cs` / `src/Tools.cs` / `src/ToolSchemas.cs` / `src/Agent.cs` / `src/SubAgent.cs`）：新增 `pc_disk_summary`、`pc_startup_list`、`pc_shred_preview` 三个只读工具，与现有 `file_search` / `pc_scan` / `pc_uninstall_list` / `pc_clean_cache` / `pc_shred` / `pc_uninstall_launch` 组成完整工具箱闭环。工具箱只读动作加入无审批白名单（AutoRun=false 时不再逐次弹确认）；危险动作保持 danger + 审批，规划模式与只读子代理同步纳入这三个新工具。
- **目录只留 5 家官方服务商**（`src/Providers.cs` / `src/SelfTestRuntime.cs` / `tools/ui-e2e.js`）：删掉免 Key 公益池与第三方本机桥两个条目。前者是弱模型且对话经第三方中转，后者是非官方线路、额度与稳定性都由上游决定——两者都不该作为「内置」摆在目录里替用户选。需要时用「自定义端点」自己填。目录 5 条、free 条目归零，自检与 e2e 断言同步改写。
- **验证**：编译通过（`dist\GreenTiger.exe`，SHA256 `C49298C7…697E4`）；`--self-test` **ALL TESTS PASSED**（目录 5 条、免 Key 档与桥条目已移除、工具箱 id 解析与工具注册、安全只读免审批）；`npm run smoke` 通过；`npm run e2e` **124/124**；`check:ui` 通过；`check:version` 通过。
- **发布整理**：移除所有第三方非模型产品名与来源表述（含已删功能的历史文案），MCP 本机发现改为通用隐藏目录扫描、不绑定任何客户端品牌；默认配置不再指向已删除的免费档。二进制内扫描零残留。
- **未验证**：没有实际删除 279.4 MB 用户临时目录来做破坏性验收；修复用单元解析断言 + 真实扫描 id 校验覆盖。`C:\Windows\Temp` / `SoftwareDistribution\Download` 等系统缓存仍受当前进程管理员权限限制，非管理员时部分文件会按既有逻辑跳过，不会强行提权。

## 3.31.0

界面一轮：重做主题、顶栏与交互反馈，只取对桌面 Agent 真正有用的部分，配色仍严格限定绿 / 黑 / 白，花园 `ui/garden.css` 未改。

- **主题不再只是「灰底加一点绿」**（`ui/theme.css`）：浅色改成纸白微绿的「纸白园林」，深色改成近黑的「夜林墨黑」；中性墨、边框、悬停层与代码底色都带极轻草木色相，强调绿只负责 Logo / 发送键 / 选中态 / 动效。新增 `--ease-spring` / `--ease-out` / `--dur-*` / `--shadow-*` / `--bg-glass` 等动效与深度令牌，全站只引用不硬写。
- **顶栏有会话身份与运行状态**（`ui/index.html` / `ui/js/app.js`）：中栏顶部显示当前会话标题（三栏布局里不再不知道「我在哪个对话里」）；任务运行时 `GREEN TIGER` 眉标后出现绿色「· 运行中」，页脚状态之外再给一处高可见状态。
- **12 条交互落到具体控件**：
  - 按压回弹：全站按钮 hover 轻抬，按下 `translateY(1px) scale(.965)`，松手用 `--ease-spring` 回位；发送键额外带绿色投影。
  - 弹性开关：设置里的开关在切换时旋钮先横向拉长再压回圆，轨道同时变绿（`width` 动画，不是 `scale`）。
  - 水波按钮：新增统一 `bindRipple()`，从真实 pointer 坐标扩散，父级 `ripple-clip` 裁形；跳过工具箱卡片等需要保留堆叠阴影的控件。
  - 点击爆散：成功 toast 在触点撒 7 颗绿白粒子；任务完成时在阶段点撒 9 颗，`0.58s` 收尾，分量只匹配「轻成功」不抢主线。
  - 成功勾选：toast 成功图标与工具卡完成态用 `stroke-dashoffset` 一笔描画；计划步骤完成用 `markPop` 回弹。
  - 液态滑杆：模型面板的思考强度轨道、填充、滑钮、当前档位标签改用同一 `.3s` 弹簧曲线；拖动时滑钮 `scale(1.16,.92)`，标签同步落位。
  - 数字滚动：右侧工作台统计卡用 `requestAnimationFrame + easeOutCubic` 从旧值缓停到新值（`rollStatNumbers`）。
  - 骨架同高：侧栏首屏加载用 `44px` 骨架行 + 绿色扫光，高度对齐真实会话行，内容到达不跳动。
  - 卡片堆叠：工具箱卡片 hover 时在背后错开 `5px / 1.2°` 露出第二层，配合 `translateY(-2px) rotate(-.35°)` 形成纸叠感。
  - 底部抽屉：≤640px 时设置 / 审批 / 工具箱改为贴底抽屉，关键帧在 54%–72% 停一段再落位；桌面端仍是居中弹窗。
  - 结果与工具透明：工具卡左侧状态细轨（运行绿 / 完成浅绿 / 错误墨白）、计划卡绿色左标、工作台统计与上下文条统一绿色数据语义。
  - **没有硬凑全部 12 条**：卡片翻面与汉堡变叉更适合移动导航 / 可翻动卡片；本轮把「层次」交给工具箱纸叠与抽屉，不为每个控件都加一场翻面秀——视频最后一句「高级感不是堆特效」是这一轮的分寸线。
- **空态与提示不堆信息**：主页标题下补一行「说出目标，绿虎会拆解步骤、调用工具，复杂产出落到右侧工作台」；toast 改为抬到输入区上方，不再压住发送键。
- **记忆入口前置**（`ui/index.html` / `ui/js/app.js`）：侧栏底部新增「记忆」按钮，直达 `设置 → 记忆`；命令面板新增「记忆管理」搜索项。全局 / 项目记忆的编辑与删除仍在原设置页，不新增假入口。
- **响应式修整**：≤640px 设置页从「148px 左导航 + 右内容」改为顶部横向分类条 + 单列字段，430px 下不再把表单挤成两半；工作台、工具箱、审批同步走底部抽屉。
- **验证**：`build.ps1` 通过（`dist\GreenTiger.exe` 1424.5 KB，SHA256 `6D4E3CFD…52B1CB`）；`--self-test` **ALL TESTS PASSED**；`check-version` 通过；`_check_inline_ui.js` 通过（7 模块 / 150 id / 21 图标）；`ui-e2e.js` **124/124**；`ui-shot.js` 全部截图人工核对（浅 / 深主页、对话、工作台、模型面板、窄屏、抽屉），另用 430×900 截图核对设置底部抽屉与横向分类。
- **未验证**：尚未用旗舰模型 Key 实测真实对话、SSE 流式回复与长文任务；本轮动效均在本地内嵌 UI 上跑通，未在真实触屏设备上验证手感。

## 3.30.0

长文本 / 长上下文一轮，参考 Claude 的四件事：**长文放前面 + 先引用再回答**、**文档索引后分段读**、**context editing 批量清旧工具结果**、**compaction 结构化交接**。当前仍是 OpenAI 兼容单通道（Anthropic 兼容层不支持 `cache_control`），所以缓存侧做的是「稳定前缀」：让 OpenAI / DeepSeek 的自动前缀缓存能命中，而不是每轮改写历史把缓存打掉。

- **预算跟模型窗口走（`src/ContextPolicy.cs`）**：不再所有模型统一 24k 输入 / 6k+2k 工具结果 / 40 条历史。
  - 单条输入：窗口 40%，1M 封顶 300k 字（32k 窗口约 15.7k，128k 约 62.9k）；超预算时保留头 70% + 尾 30%，不再把写在长文末尾的问题砍掉。
  - 工具结果：32k → 6k+2k；128k → 10k+3k；200k → 16k+5k；1M → 24k+8k。`read_file` 单次上限同步到 10k/15k/23k/34k 字，避免「读完再被二次截断」。
  - 压缩水位：1M 窗口从封顶 400k 提到 **600k 字**；原生历史 64/96/120 条；文本围栏协议单独收紧到 32k 输入 / 24 条。
  - `TruncateHeadTail` 的「完整结果见附件」改为「完整输出保留在本机工具卡」——之前那句是假的，模型看不到附件。
- **`read_file` 新增 `outline:true` 文档索引（`src/FileOps.cs` / `Tools.cs` / `ToolSchemas.cs`）**：只回章节/标题/代码符号与行号（Markdown `#`、中文 `第一章`/`一、`/`1.1`、代码 `class/def/function/public`），最多 240 条，不回正文。长文档先拿地图，再按 `offset/limit` 读需要的段；两份 Schema（plan / 普通）与文本协议说明同步。
- **长文任务简报（`src/Agent.cs`）**：用户单条 ≥12,000 字，或附件文档 ≥48KB 时，在首条消息注入 `[长文本任务 · 长上下文工作法]`：建索引 → 先引原句（文件:行号）→ 分块记「事实/数字/引文/位置」→ 压缩后靠索引+已读段+笔记续接；短输入不注入。系统提示常驻一小节「长文本 / 长文档」，覆盖通过工具读大文件的情况。
- **Context editing 改成批量（`src/Brain.cs`）**：`AppendToolResults` 不再每轮 `AgeToolMessages`。历史总字符超过 `ApiHistoryChars`（压缩水位 80%）才批量折叠到 70%，保留最近 12 条工具结果；两次编辑之间请求前缀不变，自动 prompt cache 可命中。`TrimHistory` 删除「每条 user/assistant 正文压到 6k/12k」——长文档就在首条 user 里，每轮重写既毁缓存又毁内容。
- **Compaction 结构化（`src/Agent.cs`）**：摘要固定小节「任务目标与验收 / 已完成（带证据）/ 关键决定与约束 / 未完成与下一步 / 文件与位置（已读未读段）/ 错误与死路 / 用户原话要点」；最早用户目标置顶（头 1200+尾 1000 字），最近 6 条每条约 4000 字，更早 1200-1600 字，工具输出每条 500 字；总输入按窗口 16k/39k/180k 字，摘要目标 1200-3200 字。
- **文档**：新增 `docs/long-context.md`（机制、预算表、注入时机、明确没做的：原生 `cache_control` / PDF OCR / >8MB 文件 / 真实旗舰长文实测）。
- **验证**：`build.ps1` 通过（`dist\GreenTiger.exe` 1402.5 KB，SHA256 `C50C47B8…76F8AB7`）；`--self-test` **ALL TESTS PASSED**（新增 15 条：预算随窗口放大、1M 水位 600k、文本协议 32k、批量折叠保留最近结果、outline 识别中英文/代码、长文简报只在长输入注入等）；`check-version` 通过；`_check_inline_ui.js` 通过（7 模块 / 147 id / 21 图标）；`ui-e2e.js` **124/124**（本轮未改界面，回归确认内嵌 UI 与版本戳正常）。**未验证**：没有旗舰 Key，真实 API 的长文请求、prompt cache 命中率、PDF OCR 均未实测。

## 3.29.0

本版聚焦四件事：换强模型、删杂模型、切换必须看见当前模型、界面收成绿黑白并统一外壳。目录与界面统一按旗舰口径重写，预置 `deepseek-api / deepseek-v4-pro` 且 **API Key 留空**——没填 Key 之前界面如实显示「deepseek-v4-pro · 未接入」，不会假装能用。

- **模型目录裁剪（`src/Providers.cs`）**：从 9 条压到 6 条——`anthropic`（Claude 官方兼容层：`claude-fable-5-2` / `claude-opus-4.6`）、`deepseek-api`（`deepseek-v4-pro`）、`qwen`（`qwen3.8-max`）、`zhipu`（`glm-5.3`）、`kimi`（`kimi-k3.1-pro`）、本机免费桥条目一并移除。删除免 Key 弱档、本机 llama.cpp / Ollama 占位端点、各家 flash / plus / 旧代模型（deepseek-chat、kimi-k2、glm-4 等）。离线 / 自建 / 中转统一走「自定义端点」，不再往目录里塞只能验证连通性的条目。
- **上下文窗口同步旗舰（`src/ContextPolicy.cs`）**：`deepseek-v4` 与 `kimi-k3` 按 1M 窗口计，其余 DeepSeek 128K、Kimi 200K 不变，避免 1M 模型被 32K 的保守阈值提前压缩。
- **切换模型必须显示当前模型（`ui/js/app.js` / `ui/js/model.js` / `src/App.cs`）**：
  - 模型面板首行永远写「当前模型 · 名称」，第二行服务商、第三行状态（未接入时写「还差 API Key」），当前项标 `· 当前`；删掉免费额度入口与 `showFreeMenu()` 孤儿函数。
  - 切换时先乐观更新 `S.brain.provider.model`，胶囊立刻变，不等服务端整份 state 回来。
  - 没接 Key 也显示选定模型：胶囊与主页状态行写「deepseek-v4-pro · 未接入」，不再笼统说「未接入模型」。
  - 窄屏不再 `display:none` 隐藏模型名，改为 `max-width:32vw` 截断显示。
  - `BrainDto()` 在探测未成功时回填配置里的 endpoint / model / 目录模型列表，界面因此始终知道用户选的是哪个模型；此前没 Key 时 `probe.Model` 为空，界面只能写「未接入模型」。
- **绿 / 黑 / 白统一配色（`ui/theme.css`）**：浅色 `--red`→墨黑 `#0d0d0d`、`--amber`→品牌绿 `#3c8a19`、`--blue`→灰 `#5d5d5d`，新增 `--red-ink` 供反相底色上的文字；深色 `--red`→墨白 `#ededed`、`--amber`→亮绿 `#8de85c`、`--blue`→浅灰 `#b4b4b4`。语法高亮 `--hl-*` 全部收进绿阶 + 灰阶（去掉橙 / 紫 / 蓝）；`ui/style.css` 里的 `#fff` 文字（danger 按钮 / 日志徽标 / 停止键）改 `var(--red-ink)`，深色 diff 的 `#3fb950/#f85149` 改令牌，`rgba(207,34,46)`、`rgba(46,160,67)` 同步改 `--red-soft/--green-soft`。脚本按 HSL 扫描三个 CSS 文件，非绿相有色值清零（花园 `garden.css` 不动）。
- **统一架构（dialog / palette / toolbox / inspector / approval，`ui/style.css`）**：新增 `--surface-pad-x:24px`、`--surface-pad-y:20px`、`--surface-gap:14px`；设置 / 快捷键 / 确认 / 输入四类弹窗统一 `--r-xl` 圆角、24/20 内边距、`rgba(0,0,0,.34)+blur(10px)` 遮罩，头部统一加 `GREEN TIGER · SETTINGS/KEYS/CONFIRM/INPUT` 等宽眉标；命令面板从 13px 圆角 / .52 遮罩 / 12-13 内边距对齐到同一套；工具箱的 hero/panel 内边距改用同一对令牌；右侧工作台头高 52→56、内边距 13→24/20，统计卡圆角归 `--r-md`；工具审批弹窗从 .62 遮罩 / blur 7 / 9px 圆角 / 18-19 内边距对齐到同一套（标题 18px、底色 `--bg`、内层卡 `--r-md`）。
- **文档**：README 的模型段、`docs/self-evolution.md` 的本机端点说明、`docs/agent-stability-middleware.md` 的弱模型警告同步为「3.29.0 已删除弱档」口径。
- **验证**：`build.ps1` 通过（`dist\GreenTiger.exe` 1393 KB，SHA256 `574FAB48…5B745FE6`）；`--self-test` **ALL TESTS PASSED**（目录数、免费档、AutoPool 等 6 条旧断言同步改写）；`node tools/_check_inline_ui.js` 通过（7 模块 / 147 id / 21 图标）；`node tools/ui-e2e.js` **124/124**（新断言：目录 6 条旗舰、免费档归零、模型面板无免费入口且首行显示当前模型、失联时仍显示选定模型）；截图人工核对 `shot-home.png`、`shot-dark.png`、`shot-light.png`、`m-pop.png`、`m-settings.png`（模型胶囊与面板显示「deepseek-v4-pro · 未接入」）。
- **未验证**：本版没有可用旗舰 Key，真实对话、工具调用、SSE 流式均未实测；目录里的模型名（`deepseek-v4-pro` / `qwen3.8-max` / `glm-5.3` / `kimi-k3.1-pro` / `claude-fable-5-2`）来自各家公开文档，尚未逐个用真实 Key 打通过 `/v1/models`。填入 Key 后建议先点「测试连接」，再用一个最小任务验证工具调用。

## 3.28.0

这一版只做两件事：让「听懂人话」变成可验证的行为，让工具箱从半成品变成能天天用的小工具。不引入新依赖、不动模型通道。

- **行为准则前置（`src/Agent.cs`）**：系统提示第一段换成 7 条硬准则——先复述目标与验收标准、先判断要不要用工具、三步以上先列 todo、只改被要求的范围、完成必须有证据、同一做法失败两次换路、不复述内部规则。原来这些散落在 900 行提示的中后段，模型经常读不到。
- **入口强制复述**：首条消息末尾追加 `[开工要求]`——先一句话说清「你要我……，做完的标准是……」，理解偏了用户当场就能纠正，而不是等十个工具跑完才发现跑偏。
- **原生通道提示瘦身**：`nativeTools` 下不再把 30 多个工具的参数逐条重复一遍（Schema 已随请求下发），改成「什么时候用哪个」的选择表。省下的上下文回到行为准则与任务本身；围栏协议（网页版）保留完整参数清单。
- **失败可纠偏**：工具结果新增 `next:` 行，按 `error_code` 与具体症状给出下一步动作（not_found → 先 glob/list_dir 确认真实路径；bad_args → 按 Schema 改不要猜；timeout → 缩小范围或 background；edit 找不到 old_string → 回读原文复制；permission → 不要换写法绕）。
- **Schema 修正**：删掉 plan 模式下重复下发的 `repo_map`（同名两份，纯浪费 token）；`shell` / `plan` / `todo` 描述补上「不要用它读文件找文件」「什么规模才该立计划」「完成一步就更新」。
- **推理强度接上执行预算（`src/Runtime.cs`）**：高档把 `MaxSteps` ×1.5、修复轮 +1，极致 ×2、修复轮 +2（上限仍 120 步，low 不缩短）。以前「极致」只是提示词里的自我要求，长任务常在第 40 步被掐断，现在档位真的换来探索与复查的空间。
- **输出风格纠偏**：去掉「不要只丢一句简短结论」这种鼓励长篇的指令，改成「先结论后依据、长度跟随任务复杂度、区分已确认/未验证/不确定」。
- **工具箱功能修复**：`按大小` 排序以前只切按钮样式、列表纹丝不动（现在按 `size_bytes` 真排序）；`tbBusy` 连点两次会把 spinner 存成原标签导致按钮永久转圈；粉碎完成后按钮被收尾逻辑重新点亮；打开工具箱会沿用上次滚动位置；进入详情面板时卡片页的快捷键提示被挤到面板标题上方。
- **样式层面的真 bug**：新写的 13 条 `.tb-*` 规则缺分号，导致 `background/border-radius/transition` 这些声明被浏览器整段丢弃——深色主题下输入框退回 UA 白色、圆角失效。补分号后输入框与按钮样式才真正生效。
- **工具箱补齐**：搜索结果按扩展名配图标、显示耗时、双击整行即定位、单行按钮防重入；体检先出结论条（几项建议处理 / 可清理几项）再列明细，并把上次结果存本地，再进面板就能看到「上次体检 · x 小时前」；搜索记住最近 6 个词，点一下重搜；体检面板开场交代「查什么、只读不改」；卸载列表补 `size_bytes` 兜底显示与中文名排序；粉碎加「清空列表」，完成后不再可点。
- **工具箱视觉重做**：栅格从「4 列窄卡 + 辉光 aura + 18px 圆角玻璃舞台」改为两列工具卡 + 发丝边框 + `--r-md/--r-lg/--r-xl` 同一套几何刻度，绿色只留在图标砖与焦点环；卡片图标修回「砖内 18px 字形」（此前 `data-icon` 直接挂在砖上，被 hydrate 成 40px 大图标）；窄屏（≤680px）单列、结果行上下排，不再横向挤压。
- **验证**：`build.ps1` 通过（`dist\GreenTiger.exe` 1394 KB，SHA256 `B83E9631…CB28FD`）；`--self-test` **ALL TESTS PASSED**（先清掉 `%TEMP%\GreenTiger\selftest-data`，否则上一轮留下的任务记录会让 `task store cost unknown` 这条断言误报）；`node tools/_check_inline_ui.js` 通过（7 模块 / 147 个 id / 21 图标）；`node tools/ui-e2e.js` **124/124**；四张截图人工核对（工具箱浅色主页、深色主页、深色搜索面板、浅色体检面板）。

## 3.27.0

把 Claude 官方兼容层与任意中转站两条外部通道接进绿虎，探测失败不改配置。详见 `docs/model-access.md`。

- **接入向导 `/connect`（`src/Connect.cs`）**：`/connect` 看引导；`/connect bridge` 探本机桥的 `/v1/models` 与 `/health` 并写入端点与优先模型；`/connect claude <Key>` 走 Anthropic 官方 OpenAI 兼容层；`/connect relay <地址> <Key> [模型]` 接任意中转站。端点归一化只认 http/https 绝对地址、幂等去尾斜杠；探测顺序 `/v1/models` → `/models` → `/api/tags`；探测失败**不改任何配置**，不会把能用的旧大脑弄丢；回执里不打印 Key。
- **服务商目录 +1**：`anthropic`（Claude 官方 OpenAI 兼容层，`claude-fable-5-2` / `claude-opus-4.6` / `claude-sonnet-4.6`），写清按 token 计费与直连不通时改走中转站。
- **模型差异收口（`ModelQuirks` + `ApiBrain.ApplyModelQuirks`）**：Claude 系请求补 `max_tokens=8192`；Claude 4.7+ / Fable / Mythos 从请求体删掉 `temperature`（官方兼容层对 `temperature < 1` 直接 400）。只在该命中时动字段，其它模型请求体一字不改。
- **上下文窗口**：`ContextPolicy.ModelWindowTokens` 把 Fable / Mythos 按 1M 计（官方 2026-11 口径），其余 Claude 仍按 200K 保守估，避免过早压缩。
- **自检**：新增 17 条断言——`/connect` 参数解析 3、端点归一化 3、模型挑选 3、`ModelQuirks` 2、本机 mock 桥探测 2（模型列表 + `/health` + relay 解析落地）、目录条目 2（本机桥 回环免费且含 Claude 模型、`anthropic` 含 `claude-fable-5-2` 且 Key 页为 https）；目录总数断言 7 → 9。
- **验证**：build ✓（SHA 1E10C4D7）；`--self-test` ALL TESTS PASSED ✓；`npm run test` 回归 8/8 ✓；UI 未改，e2e 未跑。
- **未验证**：当时环境没有可用的 Claude API Key，所以「真实连上上游」这一步没跑；探测链路的协议形状只用本机 HttpListener mock 验证。真正接上需要 `/connect claude <Key>` 或 `/connect relay <地址> <Key>`。

## 3.26.0

只取能落在「零依赖、可验证、不改权重」范围内的部分（取舍见 `docs/self-evolution.md`）。

- **任务走查记录（`src/Walkthrough.cs`）**：每个终态任务在 `工作目录\.greentiger\walkthroughs\` 生成一份 Markdown——目标 / 成本 / **证据强度** / 结论 / 执行时间线 / 改动与备份 / 未完成与风险。人看产物，不必读工具日志。时间线只留参数摘要（`command`/`path`/`url`/`pattern`…）与结果首行，不复制工具全文；最多保留 60 份，超出删最旧。`/walkthrough` 列最近 10 条，`/walkthrough N` 读全文；文件名走白名单正则，路径穿越直接拒绝。
- **证据强度先说（长跑时自己盯着结果能不能复现）**：走查记录首屏固定一行 `已客观验证 / 仅模型自述（未客观验证）/ 验收未通过 / 未完成`，判定只依据验证器记录与任务终态，不看模型措辞；成功但不可客观验证的任务额外写一条 warn 日志，不再让「模型说做完了」冒充证据。
- **知识库检索侧**：3.25.0 只做了「贡献」（失败→规则），3.26.0 补「检索」——`Evolution.Relevant` 按当前目标从本工作目录的失败记录里找相似项（中文 2-gram + 英文词，签名去重），回灌「上次栽在哪、这次先做什么」，预算 600 字；不相似就一分上下文都不占。
- **子代理经验分发（大量子代理共享约束）**：`SubAgentRunner.SubSystemPrompt(workDir, task)` 现在带上主循环已晋升的规则（≤900 字）与相似历史失败（≤400 字），避免同一次任务里主 agent 刚学会「先读再改」、子代理还在重犯；提示词总长有断言兜底。
- **成本可见（token 效率与缓存计价）**：走查记录写总 token 与缓存命中占比，回答「这次任务贵在哪」。
- **自检**：新增 13 条断言——走查写入/首屏证据强度/时间线含失败调用/改动与备份/风险段/未验证标注/列表倒序/按名读取/拒绝路径穿越（9）；相似失败命中与无关目标不命中（2）；子代理继承已晋升规则与提示长度上限（2）。
- **验证**：build ✓（SHA F9182FB9）；`--self-test` ALL TESTS PASSED ✓；`npm run test` 回归 8/8 ✓；UI 未改，e2e 未跑。

## 3.25.0

- **自进化闭环 v1（`src/Evolution.cs`）**：把「任务结束」变成一次可审计的学习。四环全部零模型调用、零网络请求，断网或只有本地小模型时照常工作：
  ① **记账**——每个终态任务写一条证据到 `工作目录\.greentiger\evolution\ledger.jsonl`（outcome / errorClass / **归一化失败签名**（类别 + 主工具 + 细节，路径、文件名、数字、引号内容都折叠成占位符，所以「找不到 C:\a\x.txt」和「找不到 D:\b\y.txt」是同一个桶）/ 元规则键 / steps / token），上限 600 条；
  ② **蒸馏**——同一元规则在 **≥3 个任务且 ≥2 个不同日期** 重复出现，才在 `evolution\candidates\` 生成 `ev-*.md` 候选（不启用）。只蒸馏六类元规则：`verify_before_finish` / `plan_then_act` / `variant_after_two_failures` / `read_before_edit` / `probe_environment` / `ask_when_blocked`——2026-08 的跨模型迁移实验里，只有「检查约束 / 验证结果 / 回滚」这类元规则稳定正向迁移，领域技巧会负迁移（Gemini 把「语义模拟」用成了缓存 SHA-256 结果作弊）；
  ③ **门禁**（`Evolution.Gate`，纯函数）：名称格式、frontmatter 完整、正文 ≥80 且 ≤2000 字、安全措辞黑名单（跳过确认 / bypass approval / rm -rf / 等）、证据条数与**天数从候选自带的日期行重算**（不信 frontmatter 计数）、不覆盖手写同名技能、内容必须有变化；
  ④ **棘轮**——晋升记录基线成功率，6 个任务后比对：跌 ≥20 个点自动把技能移入 `evolution\archive\` 并标 `demoted`，否则 `kept`。只进不退 + 可回滚，照的是 autoresearch 的骨架，而不是「让模型给自己打分」。
- **规则真正生效的路径**：已启用的 `ev-*` 技能被 `Evolution.PromptSection` 注入系统提示（只取标题 + 「做法」三行，总预算 1400 字，无规则零开销）。这是闭环唯一改变行为的地方——晋升过的规则，下一次任务就会看到。
- **命令**：`/evolve` 看状态（账本容量 / 最近 20 任务成功率 / 反复失败 top6 / 已蒸馏规则 / 候选 / 棘轮进度 / 下一步），`/evolve propose|accept <名>|reject <名>|revert <名>|ratchet` 全部本地执行。
- **本机离线端点回到目录**：`local-llamacpp`（`http://127.0.0.1:8080`，提示必须 `--jinja`，否则工具调用退化成纯文本）与 `local-ollama`（`http://127.0.0.1:11434`），免 Key、声明零成本。配套 `Providers.IsLocalEndpoint`：**AutoPool 不自动探测本机端点**（服务不一定在跑，白探一轮就是每次任务多等一次超时），只有用户在设置里选中时才走主配置。
- **文档**：新增 `docs/self-evolution.md`——外部证据表（公开的 agent 评测、经验迁移与上下文压缩研究）、差距地图、P1/P2 路线与明确不做项。
- **自检**：新增 12 条断言（签名归一化 / 验收失败推导元规则 / 账本落盘 / 候选需跨天证据 / 门禁放行 / 门禁拒绝「跳过确认」 / 晋升写技能 / 提示词注入 / 棘轮回滚 / 回滚后提示词不再含该规则 / 本机端点识别 / 池内不探本机）；服务商目录断言同步为 7 家（4 云端 + 1 免 Key + 2 本机），原「本机服务商已移除」断言改写为「本机离线端点必须在目录且是回环地址」。
- **验证**：build ✓（SHA 444FE33F）；`--self-test` ALL TESTS PASSED ✓；smoke 全绿 ✓；e2e 未跑（本轮无 UI 改动）。

## 3.23.0

- **MCP Streamable HTTP 传输（远程 MCP 通道）**：`mcpServers` 配置新增 `url` 型条目（与 Claude / VS Code 的 mcpServers 格式一致），与 `command` 型 stdio 子进程并存。实现：McpSession 双传输（`StartHttpAsync` + `SendHttpRequestAsync`），全部 JSON-RPC 走 POST 单请求-响应，响应为 JSON 或 SSE 文本（POST 场景响应在方法返回时已完整，从 `data:` 行取 id 匹配的最后一条消息，返回形态与 stdio 路径一致）；`Mcp-Session-Id` 响应头跨请求延续；`notifications/initialized` 等 notify 同样 POST（202/200）。协议裁剪（刻意）：GET 长连接流（服务器主动推送）与 DELETE 终结不做——工具调用场景用不到主动推送，会话随进程生命周期由服务端超时回收。http 传输的工具照常进 `mcp__<server>__<tool>` 注册、防投毒指纹（McpTrust.Observe）与 danger 逐次审批，与 stdio 同轨。配套：HttpResponse 新增 `Headers`/`ContentType` 与大小写不敏感 `Header(name)`；HttpOptions 新增 `WithHeaders`/`WithAccept`（原有的 `WithHeader` 单头方法保留）。配置解析：`McpDiscovery.ParseDef` 识别 `url` + `headers`（Authorization 等用户凭据透传），`command`/`url` 双空才拒绝；设置面板 DTO 增 `transport`/`url` 字段。
- **评测接线（差距地图 v2 第⑤项修正）**：复核 `tools/eval.js` 发现其早已具备完整能力——评测集结构校验（eval/tasks.json）、`--run` 真跑（spawn exe/独立数据目录/jsonl 输出）、`--report` 离线指标（verificationPassRate / firstAttemptSuccess / recoverySuccessRate / incompleteRate / budgetExhaustedRate / unauthorizedHighRisk / P50 / P95）、`--compare=旧 --candidate=新 --max-drop` 退化门禁。此前地图判「缺失」过时，本轮只做接线不造轮子：package.json 新增 `eval` / `eval:run` / `eval:report` 别名，AGENTS.md 测试段同步说明（回归链里 eval-structure / eval-report 两步此前已在跑）。
- **自检**：新增 RunMcpHttpChecks 6 条断言——HttpListener 起 127.0.0.1 随机端口 mock MCP server，直调 `StartHttpAsync`：url 条目解析（含 headers 透传）、initialize+tools/list（SSE 提取路径）后 Ready、工具 FullName、tools/call 回环、Mcp-Session-Id 跨请求延续（tools/list 与 tools/call 两次 POST 带同一会话头）、initialized notify 送达。
- **验证**：build ✓（SHA E06B6E76）；`--self-test` ALL PASSED（新增 6 条 mcp-http）✓；回归 8/8 ✓；e2e 119/119 ✓；smoke 全绿 ✓。

## 3.22.0

- **修正 v3.21.0 的同名回归**：仓库里此前已有一个旧版 `task` 工具（prompt/mode 单任务式，Tools.TaskSubagentAsync）；v3.21.0 引入的并行 task 与之同名冲突——分发行旧实现在前命中，且模型按新 schema（tasks 数组）调用会打到旧实现的 prompt 校验上必然失败。本轮统一：旧实现整体退役（TaskSubagentAsync 197 行、旧 schema/spec、围栏 17 号描述、write 模式审批特判全删），`task` 唯一指向并行只读子代理；write 模式随旧实现退役（子代理统一只读，写文件由主循环执行——与安全边界一致）。ToolSchemas.Filter 顺带去重（修 Build(true) 里 repo_map 重复下发）。
- **内置零配置质检（PostToolHooks，3.22.0）**：write_file/edit/multiedit 成功落盘后按扩展名跑内置白名单检查——.js/.mjs/.cjs → `node --check`、.json → `JSON.parse`、.py → `ast.parse`（不落 __pycache__）；失败信息（exit 码 + 截断报错）就地追加进工具结果回喂给模型，把「改完的代码能否解析」从提示词自觉升级为确定性机器检查。它与 v3.6 的 `src/Hooks.cs`（config.Hooks 用户自配 pre 阻断 / post 通知命令）是两层互补：内置层零配置自动生效、只做只读检查；用户层命令体来自用户自己的配置，继续有效。仓库级/项目级配置（.greentiger/hooks.json 之类）两层都刻意不收——仓库内容可驱动的执行面是注入向量。.cs 刻意不做逐次编译（太贵）；子代理只读面不经过此路径。
- **cached_tokens 计量**：ParseUsage 解析 prompt_tokens_details.cached_tokens（含网关平铺兜底），全链贯通 BrainReply → TaskRecord → TaskStatsSummary → 任务 DTO（cachedTokens）→ CostNote（「缓存命中 N」）。
- **验证**：build ✅；`--self-test` ALL PASSED（新增 12 条：usage 缓存解析 3、钩子行为 6——好文件静默/坏 js/json 确定性报错/不适用扩展/非写入工具/task 统一 3——schema 唯一/注册参数 tasks/只读 spec）✅；回归 8/8 ✅；e2e 119/119 ✅；smoke 全绿 ✅。

## 3.21.0

- **只读分析子代理（task 工具）**：新增并行子代理能力——一次调用可派 1-4 个独立迷你 agent 循环，各自带 12 步步数预算 + 8 分钟墙钟上限，结论截断 4000 字回喂主上下文。安全边界：工具面 = plan 模式只读子集按白名单再过滤（read_file/list_dir/glob/grep/repo_map/http_get/web_fetch/web_search/file_search/pc_scan/pc_uninstall_list），无 shell、无写文件、无 MCP、无 ask_user/wait/finish；子代理工具面里没有 task 本身（不可递归派生）；越权调用不进入执行器，直接合成 permission_denied 结果回喂。子代理用独立 ApiBrain 实例（选举三元组已回写 Config），与主会话历史互不污染；端点降级围栏协议时如实报错不硬跑。用途：多方案对比、全仓审查、批量资料搜集这类可拆分的只读调查；写操作与命令仍由主循环亲自执行。
- **API 层工具结果老化**：会话层 MicroCompact 老化的是 session 拷贝，native 协议真正发给 API 的 Brain 内部 role:"tool" 历史此前从不收缩——微压缩省的是计数不是 token。现在每次 AppendToolResults 后把最近 12 条之外的旧工具结果就地占位化（保留头部与 tool_call_id，协议结构不动），长结果 >800 字符才折叠，幂等。/stats 新增累计节省字符。
- 技能清单注入上限 20 个的静默截断改为显式标注（/技能名 调用不受限）。
- **验证**：build ✅（SHA 9A0C9E78）；`--self-test` ALL PASSED（新增 17 条断言：老化 7 条——节省字符/折叠标记/call id 保留/最近完整/幂等/system 不动；task 注册+strict 3 条；工具面安全 4 条——不可递归/只读面/schema 过滤；循环级 4 条——脚本工具大脑驱动真执行器并行跑两子任务、越权 shell 拦截、read_file 真执行、空 tasks 拒绝）✅；回归 8/8 ✅；e2e 119/119 ✅；smoke 全绿 ✅。

## 3.20.0

- **记忆候选审核（记忆 inbox）**：任务自动沉淀（成功任务/失败教训）不再直写记忆文件，改写入候选区 `.greentiger\memory-candidates.md`（封顶 60 条，FIFO）；「设置 → 记忆」新增候选列表，逐条「采纳 / 丢弃」，采纳才进项目记忆——错误结论不再污染后续任务。新增 `save_memory` 工具（normal 风险）：用户明确说「记住 X」时直接写入，scope=project（默认，当前工作目录）或 global（跨项目，`AppPaths.Root\memory.md`）；strict 校验生效（content 必填、scope 枚举越界拒绝）。
- **全局记忆层**：系统提示词的记忆段改为「全局记忆在前 + 项目记忆在后」；全局记忆 = 数据目录 `memory.md`，候选审核与 save_memory 均可触达。
- **敏感输入截图守卫**：绿虎浏览器窗口注入页面脚本，密码/支付输入框（type=password、autocomplete cc-/current-password/new-password、name/id 含 pass|pwd|card|cvv|cvc）聚焦时上报宿主；screenshot 工具命中即拒绝执行并说明原因（键盘输入不拦）。防用户正在输入的凭据经截图进入模型上下文。
- **watch mode（离席自动暂停）**：设置 → 权限新增开关（默认关）与离席阈值（1-120 分钟，默认 5）。开启后任务运行中超过阈值无用户活动（发消息/审批/界面操作/心跳）→ 自动暂停，回来点「继续」续跑。服务端以非轮询 API 请求 + 界面节流心跳（45s）做活动打点。
- **截图降采样**：长边超 1568px 等比 HighQualityBicubic 重编码回同路径（失败保留原图），输出行标注降采样状态——模型吃图按长边计 token，全屏截图普遍 2000-3000px，此项直接省一半以上图像开销。
- **反死循环纪律**：系统提示词新增任务纪律——同一错误连续 2 次换方法或停下说明，连续 3 次同类失败必须停下汇报进展与卡点。
- smoke/e2e 断言随新增「记忆」设置分类由六类更新为七类。
- **验证**：build ✅（SHA 19D740DF）；`--self-test` ALL PASSED（新增记忆候选断言：候选封顶 60/采纳进记忆/记忆封顶 120 丢最旧/显式 project+global/PromptSection 含全局层/dismiss/save_memory 注册+strict 校验含 scope 枚举拒绝）✅；回归 8/8 ✅；e2e 119/119 ✅（含「无页面报错」）；smoke 全绿 ✅；check:ui ✅（7 模块/147 id/21 图标）。

## 3.19.0

- **strict 工具调用本地校验（外部研究报告确认的第②位缺口）**：两条协议（原生 function calling / 文本围栏降级）共用的唯一校验入口 `ToolRegistry.Validate` 升级——按 ToolSchemas 同源 Schema 硬校验：未知参数名拒绝（additionalProperties:false）、类型不符拒绝、枚举越界拒绝、数组/对象递归进 items/properties（含嵌套 required）。失败结构化回喂：错误附「正确输入示例」（必填参数按类型占位 + 可选参数列表），不执行、直接教模型怎么改。围栏降级路径从此不再是「无约束解码」。
- **修复潜伏 Schema bug**：`prop()` 的第 4 参数（数组形状）一直被忽略——multiedit.edits / todo.items / plan.steps / pc_shred.paths 的数组项 Schema 从未下发给 API、也没进本地校验。现在正确并入：API 侧拿到真实 items 约束，本地递归校验生效。
- mouse 补声明 `button` 参数（handler 一直在读、Schema 漏了——严格校验下会误拒右键）。
- **验证**：build ✓（SHA 015EC533）；`--self-test` ALL PASSED（新增 8 条 strict 断言：类型错/未知键/枚举越界/嵌套必填/合法放行/可选 null/mouse button/示例提示）✓；回归 8/8 ✓；本轮无 UI 改动，e2e/smoke 不受影响。

## 3.18.0

- **shell 全量 checkpoint（外部研究报告交叉确认的最高价值缺口）**：未沙箱 shell 命令执行前，增量留档工作目录到 `backups/<taskId>/shellck/`——版本店按 seq 存档、只拷相对上一档有变化（size/mtime）的文件；`.git`/`node_modules` 等跳过；3000 文件 / 128MB / 单文件 16MB 上限，超限整段跳过、绝不阻断执行。`/undo` 与工具行新增「回滚到命令前」按钮可把工作目录恢复到该命令执行前：被改文件按 ≤N 最新版本还原（mtime 复原）、之后新建的文件/空目录按存在基线删除（无基线宁可漏删不误删）、N 之后被删的文件找回；恢复前当前状态自动另存新档（可再回滚）。沙箱内写入受 ACL 约束，不留档。命令输出尾行标注留档状态。
- **验证**：build ✓（SHA 8F17A742）；`--self-test` ALL PASSED（新增 16 条 checkpoint 断言：增量留档 / 修改保留 / 新建删除 / 删后找回 / 超大文件不动 / 默认跳过目录不动 / 回滚链两步 / 存在基线 / 上限跳过）✓；回归 8/8 ✓；e2e 119/119 ✓；smoke 全绿 ✓；check:ui ✓；截图人眼校对 ✓。

## 3.17.1

- **MCP shadowing 检测**（三份外部研究报告交叉确认的缺口）：工具描述里引用其他 MCP 工具全名（`mcp__…__…`）即视为疑似 shadowing 引导——系统提示该工具行加 ⚠ 前缀并记日志。恶意服务器在描述里写「使用 X 工具前必须先调用我」劫持调用流的形态从此可见。附注：MCP 工具在绿虎一律 danger 逐次确认（无自动豁免路径），描述被省略的尾部工具因此天然不参与任何豁免。
- **验证**：build ✓（SHA 33FFF4D2）；`--self-test` ALL PASSED（新增 3 条：引用其他工具黄标/普通描述不误报/空描述不误报）✓。

## 3.17.0

- **网络白名单（执行安全三件套的最后一件）**：设置 → 权限与运行限制新增「网络白名单」——策略 off（默认，不限制出网）或 allowlist（命令与网页抓取/下载的目标必须在名单内，否则强制确认）。域名条目同时放行子域（github.com 放行 api.github.com）；白名单开着且名单为空 = 一切出网都要确认（fail-closed）。判定覆盖 shell 命令里的 URL 与 http_get/web_fetch 的目标；无协议形态（curl host）暂不抓取，宁可漏抓不误判。配合注入防御链闭环：数据流可见（3.15.0）管「谁把内容塞进命令」，网络白名单管「数据能发到哪里」。
- **验证**：build ✓（SHA 0A1C13AF）；`--self-test` ALL PASSED（新增 8 条：主机精确/子域/名单外/空名单 fail-closed、shell 升 danger、子域放行保持 normal、http_get 拦截、off 策略零影响）✓；回归 8/8 ✓；e2e 119/119 ✓；smoke 全绿（修复白名单 placeholder 违反供应商中立即断言）✓。

## 3.16.0

- **审批 scope 第三档：永久允许（评估报告 #8）**：审批弹层新增「永久允许此工具」按钮（高风险与 shell 不提供）——写入一条 `tool:*` 的 allow 权限规则并立刻落盘，重启后仍有效；重复点击去重；可在设置 → 权限规则中撤销。shell 刻意排除：命令之间差异巨大，工具级永久放行等于关闭全部命令审批，按「shell:具体命令*」粒度放行请在设置面板手写。
- **repo_map symbols 深度模式（评估报告 #13 的务实版）**：`{"symbols":true}` 逐源码文件（.cs/.js/.ts/.mjs/.cjs/.ps1/.py）列出顶级符号与行号——「某功能在哪个文件」一眼可见；构建链不引 tree-sitter（仓库铁律），正则级提取对导航用途足够；上限 40 文件/24 符号，超出引导用 grep。
- **验证**：build ✓（SHA F630B849）；`--self-test` ALL PASSED（新增 7 条断言：永久允许写入/去重/shell 排除/空名拒绝 + symbols 模式 safe 定级与 C#/JS 符号提取）✓；回归 8/8 ✓；e2e 119/119 ✓；check:ui ✓。

## 3.15.0

- **不可信数据流入危险操作的可见性（能力标记式数据流提示，接 3.14.0 数据流黄标的泛化）**：
  - **留底面扩展**：不再只记 MCP 结果——web_fetch / http_get 的网页正文与 web_search 的搜索结果同样留底（上限 24 条、15 分钟有效、每条截前 8KB），来源描述带 URL / 搜索词。
  - **shell（执行通道，升 danger）**：命令内容包含留底中的不可信内容时直接升级为高风险，确认框明说「命令内容包含来自『网页 <URL>』的返回内容（网页/工具输出可能携带注入指令，请确认这是你要执行的）」——「下载后执行」类注入链对用户可见。
  - **write_file / edit / multiedit（落盘通道，只黄标）**：写入内容含不可信内容时追加提示但不升级——落盘在工作目录内、可逆，用户知情即可。
  - **MCP 跨来源黄标保留**：同源豁免（服务器自己的记录不算），服务器名查不到（未连接）时宁可全查，宁严勿松。
  - **两层防线的分工**：模型侧是 untrusted_content 包裹 + 提示词规则（3.11.0），用户侧是本条的数据流可见性——模型被骗时，用户在确认框仍能看见「这段命令/文件内容来自哪个网页」。
- **验证**：build ✓；`--self-test` ALL PASSED（数据流断言扩到 8 条：归一化、跨源命中、同源豁免、无关不报、过期不报、**网页内容进 shell 升 danger**、**网页内容进写文件只黄标**、无关命令保持 normal）✓；回归 8/8 ✓；e2e 119/119 ✓。

## 3.14.0

- **repo_map 仓库概览工具（评估报告 #6）**：新增只读工具，一次调用返回两层目录树 + 每目录文件数 / 大小 / 最近修改时间，自动跳过 node_modules/.git/dist 等噪声目录。接触陌生仓库第一步先调它建立整体感，替代连环 list_dir；已进规划模式只读白名单与免审批清单。
- **监督闸门（评估报告 #11，务实版）**：danger 确认弹层自动附一份 AI 风险审查——后台用当前大脑快速评估「这条命令做什么、有什么可疑点」，绿灯（未发现明显风险）/ 黄灯（有可疑点，请细看）/ 灰（审查中或不可用）三态展示在弹层里。刻意只展示不放行：审查说 safe 也不会自动批准，决策权始终在用户。审查输入只有工具名/命令/风险理由（不带对话历史，减小注入面），命令内容用 untrusted_content 标签包裹，25 秒超时不阻塞手动确认。设置 → 权限与运行限制 可关（默认开，每次审查消耗少量模型额度）。
- **MCP 跨服务器数据流黄标（评估报告 MCP 第三防的务实子集）**：最近 24 条 MCP 工具结果留底（15 分钟有效）；当新的 MCP 调用参数里出现「另一台服务器」返回的内容（归一化后 32 字符滑窗探针命中）时，确认框风险理由明说「参数包含来自外部工具 X 的返回内容（跨服务器数据流）」——恶意服务器诱导数据从 A 流向 B 时用户看得见。MCP 工具本身维持 danger 逐次确认不变。
- **验证**：check:ui ✓；build ✓（最终 SHA 78CA3CAD，verify:all 复编译后）；`--self-test` ALL PASSED（新增 15 条断言：Supervisor 提示构造/五种解析容错、repo_map 输出与 safe 定级、数据流归一化/跨服务器命中/同源不报/无关不报/过期不报）✓；回归 8/8 ✓；e2e 119/119 ✓；监督闸门 harness 9/9（默认开/关持久化/重开/设置面板开关渲染）✓。审批弹层 AI 审查块的三态真机渲染需大脑驱动，留待实测。

## 3.13.0

- **Windows 低完整性沙箱（评估报告第三阶段核心项：沙箱内免审批，解审批疲劳）**：
  - **架构**：新增 `src/Sandbox.cs`——ToolExecutor 启动本 exe 的引导进程（`--sandbox-exec`），引导进程把自身 token 完整性降到 Low（S-1-16-4096，降级不需特权）再启动 powershell；输出经显式泵转发回外层管道，超时 / kill 树 / 编码解码全部复用原 shell 链路。实测：写沙箱目录 OK、写中完整性目录被系统拒绝（no-write-up）、读中完整性文件照常。
  - **沙箱目录**：数据目录 `sandbox\ws`（icacls 低完整性标签 + 标记文件，幂等准备，普通用户无需管理员）。开启沙箱模式时工作目录自动切到沙箱目录。
  - **免审批边界（刻意收窄）**：仅「沙箱目录内的、非 danger 的 shell」免审批（`Sandbox.CoversCall`）；黑名单命令、越界 cwd、键鼠、网络工具、写模式子代理一律照旧走审批。沙箱模式下 cwd 越出沙箱 = fail-closed 直接拒绝执行，绝不静默回退到中完整性。shell 输出带「【沙箱·低完整性】」前缀，系统提示告知模型沙箱语义。
  - **如实标注的局限**：低完整性防「误删 / 误写本机文件与注册表」，不防网络外发（网络不受完整性限制）、不防同用户密钥读取（DPAPI 不区分完整性）。文档同步写入 docs/security.md。
  - **入口**：设置 → 权限与运行限制 → 「沙箱模式（低完整性）」开关；关闭时不动工作目录。
- **验证**：check:ui ✓；build ✓（SHA FB8026DB）；`--self-test` ALL PASSED（新增 11 条沙箱断言：目录准备、豁免判定六形态、越界 fail-closed、子进程端到端低完整性隔离、经引导进程真实执行 + 输出前缀）✓；回归 8/8 ✓；e2e 119/119 ✓；HTTP harness 9/9（开关持久化 / 工作目录自动切换 / 关闭保持 / UI 开关渲染）✓。

## 3.12.0

- **专业地基轮（评估报告 P1 三项落地：截断三件套 / 编辑语法闭环 / MCP 防投毒）**：
  - **工具结果截断三件套**（Anthropic writing-tools-for-agents 口径）：`ContextPolicy.TruncateToolResult` 替换模型回灌路径（文本协议 + 原生 function calling 双侧）的粗截断——标记如实写 ① 原文完整大小（字符数+行数）② 截断点吸附行边界（head 吸附行尾、tail 吸附行首，400 字窗口，无换行可吸附则硬切并补分隔符）③ 收窄重查提示（read_file offset/limit、grep include/max_results、web_fetch max_chars）。短文本原样返回。
  - **编辑语法验证闭环**：edit / multiedit / write_file 成功写盘后自动跑语法检查，结果直接附在工具输出回喂——模型当轮看到错误当轮修，不用等用户跑构建。检查器：.js/.mjs/.cjs → `node --check`；.py → `ast.parse`（零副作用，不落 __pycache__）；.json → 进程内解析；.ps1 → `[scriptblock]::Create`；.cs → csc 单文件编译只认 CS1xxx 语法类错误（缺引用的 CS0xxx 忽略）。工具不存在/超时/append 中间态（分段写入合法不完整）静默跳过；检查结果不改变写入的 Ok。
  - **MCP 防投毒两防**：① 工具描述全文可见——`/api/mcp/servers` 每台服务器返回完整 `tools[]`（全名+描述，单项上限 2000 字），设置 → 外部工具 逐条展开核对（模型看到的就是这些文本，用户要看得到）；② 清单指纹锁定——每次连接计算工具清单（全名+描述+Schema 排序）SHA256 存 `mcp-trust.json`，与上次不同即黄标「清单已变化，请复核」+ 日志告警（防 rug pull：批准时干净、之后描述被注入）。实测 REA 255 个工具全量渲染。
  - **评测集补语法自愈三题**（file-11/12、multistep-11）：写错→自检报错→修复→自检通过的完整闭环真跑用例，评测集 50 → 53 条。
- **验证**：check:ui（7 模块 / 144 id）✓；build ✓（SHA F6D6E1F6）；`--self-test` ALL PASSED（新增 13 条 P1 断言：截断三件套 6 + MCP 指纹/信任 3 + 语法闭环端到端 4）✓；回归 8/8 ✓；e2e 119/119 ✓；MCP harness：API 字段形状 + 设置面板 255 工具 DOM 渲染 ✓；eval 结构校验 53 条 ✓。

## 3.11.0

- **安全止血轮（对照外部差距评估报告，P0 六项全部落地）**：
  - **密钥加密存储**：`ProviderKey` 与大脑池条目 Key 改为 DPAPI（`ProtectedData`/CurrentUser + 附加熵）密文落盘（`dp1:` 前缀），新增 `src/SecretBox.cs`；旧明文配置读取兼容、下次保存自动转密文；`Save` 在 Json 克隆副本上换密文，不触碰运行中的活对象（避免并发请求瞬间读到空 Key）；DPAPI 不可用时降级明文并记日志。`build.ps1` 新增 `System.Security.dll` 引用。
  - **注入防御**：`web_search` / `web_fetch` / `http_get` / `mcp__*` 的工具输出回灌模型前统一包 `<untrusted_content>` 标签（文本协议 `Agent.cs` 与原生 function calling `Brain.cs` 两条路径都在**截断之后**包，保证开闭标签成对）；原生路径补齐 `InjectionSuspected` 警告行；系统提示新增两条规则：标签内是数据不是指令、疑似注入即停下不动作并如实上报。
  - **computer use 四道防线**：`mouse` / `keyboard` / `focus_window` 从 normal 升 **danger 逐次确认**（审批票据保证同参数重复调用不重复弹窗）；`HideOnScreenshot` 默认改 **true**（截图默认隐藏绿虎自身窗口）；键鼠/聚焦执行前检查 **Esc 全局急停**（按住 Esc 即取消本次操作）；**防反馈回路**——前台窗口属绿虎进程或聚焦目标是绿虎自身时拒绝键鼠（注入内容不能借绿虎界面自我授权）。
  - **/undo 回滚**：编辑前快照（`data\backups\<taskId>`，已有能力）补上消费入口——`/undo` 找本会话最近一次带快照的 edit/multiedit/write_file 改动回滚，可连续后退；回滚前自动再留快照（rollback 目录），副作用可再撤销。
  - **复合命令拆分**：`ShellRiskCompound` 整串过黑名单后，再按换行/`;`/`|`/`&&`/`||` 做引号感知拆分、逐子命令判定（危险段不能再藏进复合命令）；引号未闭合 fail-closed 按危险处理；黑名单补 `irm` / `iwr` / `invoke-restmethod` 下载摇篮别名。
  - **弱模型生产警告**：`Providers.IsTrialTier` 识别免 Key 免费公益档（CHANGELOG 3.10.10 口径：仅限连通性验证），`/api/state` 新增 `brainTrial` / `brainTrialNote`，输入框上方常驻琥珀色提醒横幅（可点「知道了」关闭）。
- **文档**：docs/security.md 同步注入防御 / computer use / 回滚 / 密钥存储的新防线与回归断言分布。
- **验证**：check:ui（7 模块 / 144 id / 21 图标）✓；build ✓（最终 SHA 4D30D5B2）；`--self-test` ALL PASSED（新增 12 条 P0 断言：明文不落盘 / 加密往返 / 池条目解密 / 旧明文兼容 / 复合命令三态 / 引号 fail-closed / 键鼠三件 danger / 不可信来源标记 / 试用档识别）✓；回归 8/8 ✓；e2e 119/119 ✓。

## 3.10.14

- **删除「软件直达」卡片**（用户指令）：五卡收为四卡一排（grid minmax 212px，940px 舞台下 4×213px 对称排布）；卡片文案收紧为两行内；卡片页底部新增快捷键提示行（二次确认说明 + Esc + 数字键 1-4）。顶栏按钮 title 同步去掉软件直达；`/api/toolbox/launch` 后端路由保留（Agent 工具 pc_launch_app 与搜索自愈仍在用），仅移除 UI 入口。
- **功能细节完善（智能化）**：
  - 极速搜索：索引服务不可用时的报错写清「确认索引服务正在运行」，不再代拉起第三方程序，也不静默降级成全盘遍历；
  - 本机体检：大文件 / 重复文件 / 安装包的路径列表每行新增 hover 浮现的「定位」按钮（资源管理器选中高亮，复用 /api/toolbox/reveal 只读路由）；
  - 文件粉碎：粘贴路径后 **650ms 自动预览**（去抖，只读检查零删除），省掉手动「检查」一步；toast 文案同步「正在检查…」；
  - 卸载软件：卸载程序启动后的 toast 引导「完成后回来点刷新列表」；
  - 数字键 1-4 在卡片页快速进入对应面板（焦点在输入框时不抢键）。
- **视觉打磨**：右上/左下 aura 辉光外推至舞台角落（right:-215/top:-235 与 left:-195/bottom:-200），并显式层序（aura z0 / scroll z1 / hero z2）——修复辉光从 hero 透明区与卡片缝隙透出、在内容上方形成"脏斑"的问题（像素级复测：卡片缝隙绿度 0.00）；e2e 断言同步五卡→四卡。
- 验证：check:ui ✓；build ✓；e2e 119/119 ✓；harness 21/21 ✓；亮暗双主题截图 + 像素级绿度检测 ✓。

## 3.10.13

- **工具箱视觉重做（命令舞台）**：穿模修复——顶栏按钮的「工具箱」文字溢出根因是 `.workdir-chip #workdirLabel` 只按 id 隐藏，顶栏改为纯图标按钮（icon-btn，与「⋯」一致），文字入口只留「⋯」菜单；界面按 2026 前沿审美重做：全屏重模糊玻璃底（blur 18px + saturate）上浮圆角 18px 舞台卡、顶部渐变高光线、角落 aura 辉光、渐变图标砖、卡片 stagger 入场 + hover 抬升 + 辉光环 + 箭头滑入、分段控件、focus ring、微缩按压反馈；卡片网格 3+2 对称布局；颜色全走 theme 令牌（含 --accent-rgb），亮暗主题自动适配（已双主题截图校验）。
- **功能补强**：
  - 极速搜索：结果行新增「定位」（资源管理器中选中高亮该文件，新路由 `/api/toolbox/reveal`，只读无 confirm；文件 /select、目录直开）与「复制路径」；新增排序分段控件（综合 / 大小 / 时间，透传 es.exe `-sort`，白名单校验不变）；空结果改空状态插画文案；
  - 本机体检：结果顶部新增磁盘空间概览（每块固定盘一行：卷标 + 剩余/总量 + 用量渐变进度条，≥88% 变琥珀、≥95% 变红，`scan` 响应新增 `disks` 字段，DriveInfo 即时读取零扫描成本）；
  - 卸载软件：新增排序（名称 / 大小，`uninstall_list` 响应新增 `size_bytes` 数值字段）与「刷新列表」按钮（卸载完成后重读注册表），计数行显示筛选/总量；
  - 文件粉碎：单行输入升级为多行文本域，一次粘贴多行路径批量添加（去重、去引号），chip 列表逐个可移除。
- **代码健康**：WebServer.cs 全文件统一 CRLF（首轮 PowerShell here-string 写入曾引入 161 处 lone LF）；修复 scan case 一处重复拼接行。
- 验证：check:ui（7 模块 / 21 图标）✓；build ✓；e2e 119/119（工具箱 5 条专项不变全过，id 骨架兼容零改动）✓；工具箱 harness 21/21（新增磁盘概览 / size_bytes / reveal 不存在路径与相对路径双拒）✓；亮暗双主题 + 粉碎面板截图人眼校验 ✓。

## 3.10.12

- **新界面：工具箱（图形化本机操作台）**——上一轮植入的七件 Agent 工具只藏在对话里，不熟悉命令行的人用不上；本轮把能力搬到大按钮上，不聊天也能用：
  - 入口：顶栏常驻「工具箱」按钮（accent 强调）+「⋯」溢出菜单项；全屏弹层五大卡片（极速搜索 / 本机体检 / 卸载软件 / 文件粉碎 / 软件直达），全程中文、一句话说明、大按钮；
  - 后端新增 `/api/toolbox/*` 结构化 JSON 路由（search / scan / uninstall_list 只读；uninstall / shred_preview / shred / launch / clean 带语义化 confirm 门），能力全部复用 `LocalOps`（es.exe 查询消毒、粉碎硬护栏、缓存白名单原样生效，无第二套逻辑）；
  - 危险操作（卸载 / 粉碎 / 清理）前端弹二次确认层，确认后才带 `confirm:true` 提交；粉碎必须先「检查」预览（逐文件大小 + 可否粉碎 + 拒绝原因）再执行；
  - 新增 `ui/js/toolbox.js` 模块：独立 fetch（20s→60s+ 超时撑住全盘扫描与粉碎）、焦点圈定、Esc 分层关闭（确认层→面板→工具箱）、toast 复用 #toasts；四处资源清单同步（project-manifest / build.ps1 / AppUiAssets / WebServer UiModules）；
  - `TOOL_META` 补齐七件新工具中文名（file_search 全盘搜索 / pc_scan 本机体检 / pc_uninstall_list 已装程序 / pc_uninstall_launch 启动卸载 / pc_clean_cache 清理缓存 / pc_shred 文件粉碎 / pc_launch_app 软件直达），对话区工具卡同步受益。

## 3.10.11

- **新能力：本机运维工具七件**（把「三软件融合」的能力以绿虎工具形式实现，`src/LocalOps.cs`）：
  - `file_search`：全盘文件名搜索（es.exe `-json` 调用面；bin\es.exe 探测顺序：数据目录 → exe 旁 → PATH）；
  - `pc_scan`：本地可解释风险扫描（缓存与 AI 模型占用 / 大文件 / 下载目录安装包 / 重复文件 MD5 查重 / 开机自启），启发式评分非杀毒结论；
  - `pc_uninstall_list` / `pc_uninstall_launch`：标准卸载注册表三处清单（HKLM×2+HKCU）与标准卸载器启动（走与控制面板一致的卸载路径）；
  - `pc_clean_cache`：白名单缓存清理（仅限 pc_scan 输出里的 finding_id，占用文件自动跳过）；
  - `pc_shred`：文件粉碎（3 趟覆写 0x00→0xFF→随机 + 改名 + 删除；Windows/Program Files/ProgramData/盘根/数据目录硬拒绝）；
  - `pc_launch_app`：按可执行文件名或绝对路径启动本机程序（App Paths / PATH / Program Files 定位，不写死安装位置）。
- **风险分级**：只读三件（search/scan/uninstall_list）= safe 且进规划模式白名单（ToolSchemas plan 分支 + Agent.ModeAllows + 系统提示同步）；直达 = normal；卸载启动/缓存清理/粉碎 = danger 逐次确认（Analyze + ToolRegistry 双侧标注）。
- 接线：ToolSchemas（API 通道 7 件 + plan 3 件）、Agent 围栏工具清单 25-31、ToolRegistry 规格、ToolExecutor Analyze/ExecuteAsync 分发。

## 3.10.10

- **修复：弱模型裸文本工具调用解析洞**（用户实测 openai-fast 输出 `ask_user {"question":...}`——工具名+空格+JSON，既不是围栏也不是 OpenAI 形状）。解析器新增兕底 3：`工具名 {JSON}` 单行形状提取，注册表白名单门控（未注册名/非 JSON 一律留在正文，防举例文本误执行）。
- **修复：ask_user 失败兜底语义**。参数非法（问题缺失/为空）【绝不静默兜底】也【绝不报「用户跳过」】：归一化剥掉 question 键让校验报缺参，审批前预检直接拦下，强制回灌 `[System Error] ask_user 调用失败，请重新生成合法的 JSON 参数，或者直接停止任务，禁止跳过确认流程直接执行高危命令！`；「用户跳过」仅保留给真实拒绝/超时。
- **文档**：docs/agent-stability-middleware.md 新增「模型要求（架构师忠告）」——接入模型必须支持原生 Function Calling，严禁生产使用 openai-fast 等免费公益池（仅限连通性验证），推荐 DeepSeek-V3 / GLM-4-Flash / GPT-4o-mini；README 模型节同步。
- 自检新增 6 条：宽松解析 3（命中/未注册门控/非 JSON）+ 裸文本 ask_user 端到端 + 非法参数 [System Error] 回灌 + 无「用户跳过」。

## 3.10.9

- **稳定性中间件五模块落地**（对照外部规格盘点：三模块已是既有能力，本轮补真实缺口，方案与 System Prompt 模板见 docs/agent-stability-middleware.md）：
  - **模块一 Sanitizer**：新增 `src/Sanitizer.cs`——剥离 `<|channel|>` 等 harmony/ChatML 控制标记与 `<thinking>` 思考块（含未闭合尾段），工具名清洗（`shell<|channel|>commentary` → `shell`）；应用在 Brain 三条响应路径 + Agent 执行前兜底。实测缺陷（工具名泄漏成 unknown_tool）自此闭环，新增标记泄漏回归断言。
  - **模块二补强**：`bad_args` 反馈附带 `legal_tools` 全量清单（unknown_tool 原本就带；连败 3 次熔断/自修复已有）。
  - **模块三补强**：文本协议回灌点（`BuildToolResult`）加单条观测掩码——超 6000 字保留头 6000/尾 2000 + 诚实省略标记（native 路径 `AppendToolResults` 已有同款）；只影响回灌，UI 工具卡展开仍完整。
  - **模块四补强**：系统提示新增「高危命令必须先 ask_user 确认并列出具体命令」「内部推理保持精简」；语言改为跟随用户输入（默认中文）。
  - **模块五补强**：ApiBrain 两条请求路径对 429/5xx 做 1.5s/3s 指数退避（最多重试 2 次，尊重取消）；大脑池自动切换时在会话流写入「【大脑池】模型已自动切换 → …」留痕。
  - 自检新增 7 条：清洗 5 + 标记泄漏闭环 + 长输出掩码闭环。

## 3.10.8

- **修复：ask_user 选项弹窗看不到选项**。小模型常把 options 填成字符串数组、把问题写在 title/text 里甚至留空——进入审批前新增 `NormalizeAskUserArgs` 归一化（字符串→{label}、{value|name|text}→label、title/text/prompt→question、空问题兜底「请做出你的选择」），选项卡、轨迹、回放看到统一形状；解析失败原样交 Validate 报 bad_args。
- ask_user 的 Schema 给 options 补上 items 结构（{label 必填, description 可选}），从源头减少模型乱填。
- UI 解析同步兼容字符串选项与别名字段（纵深防御）。自检新增 3 条归一化断言；真机验证：真实模型调 ask_user，弹窗正确渲染问题与两个选项。

## 3.10.7

- **ask_user 选项弹窗**：新增 `ask_user` 工具——模型需要用户拍板时弹出选项卡（问题 + 2-4 个互斥选项 + 自定义回答输入），点选后答案作为工具结果回灌继续；跳过或超时（10 分钟）时模型收到「基于已有信息继续」的明确反馈。提问在任何权限模式/白名单下都不能自动放行；原生 function calling 与文本围栏双协议均可用（Schema 随请求下发）。
- **工作台新增「对话」分段**：右侧面板变为产物 / 任务 / 对话 / 日志四段——「对话」列出所有会话，支持标题搜索过滤、置顶/归档标记与当前会话高亮，点击直接切换。
- **任务系统通知（托盘气泡）**：任务完成/失败/未完成（超过 8 秒的长任务）与「等待你的确认」时弹托盘通知，`config.Notify`（默认开）可关——人不在屏幕前不再错过审批。
- 自检新增：ask_user 注册 / 双模式 Schema / 选项回灌 / 任务成功；smoke 新增选项卡与对话分段接线断言；e2e 更新为工作台四段并覆盖对话分段的列表渲染与搜索过滤。
- 顺带：修掉 ToolSchemas 规划分支里 web_fetch Schema 重复注册的笔误。

## 3.10.6

- **大脑池（AutoPool）：多免费大脑自动轮流择优**。config.json 开 `AutoPool` 后，每次任务开始并行探测池内候选并择优：设置里的当前服务商 > `ProviderPool` 显式条目（自带 Key，仅存本机）> 目录免 Key 免费档（自动入池）。刚失败的候选按失败类型冷却（鉴权失败 5 分钟 / 请求失败 1 分钟），下个任务自动轮换到下一个可用候选；探测与选举规则为纯函数，新增 13 条自检断言。
- 选举结果回写 `Config`（服务商三元组 + Key），界面胶囊、`/api/state`、子代理读到的永远是当前真正生效的大脑；手工改设置立即作废粘滞（尊重显式切换）。
- `ApiBrain` 增加传输/鉴权失败记录；`/api/config` 接受 `autoPool` / `providerPool` 字段（与设置页未来的池管理共用一条通道）。
- 顺带：`AskTextProtocol` 从 `PostJsonText` 改走 `PostJson`，能拿到 HTTP 状态码用于失败分类（此前 429/5xx 只能看到错误文本）。

## 3.10.5

- **内置免费免 Key 服务商**：目录新增免 Key 免费档（匿名模型，支持原生 function calling 与 SSE 流式）——免注册、免 API Key，选中即聊，「先跑起来」不再依赖申请 Key。匿名档限速，隐私上内容经第三方中转，目录条目里已注明。
- **模型列表解析兼容**：`ParseOpenAiModels` 除 OpenAI 标准 `{"data":[...]}` 外，兼容 `{"models":[...]}` 与裸数组形状（字符串或 `{id|name|model}` 对象）；非 JSON 文本返回空列表，不再把闲聊应答误判成模型。
- **探测兜底 + 如实报错**：`/v1/models` 拿不到列表时自动补一发 `{endpoint}/models`（探测与「测试连接」同口径）；鉴权失败（401/403）的提示明确写「API Key 未填或无效」，不再笼统显示「端点无响应」。
- 自检同步：目录规模 4→5、免 Key 条目存在性、三种形状解析与非 JSON 输入断言；README 目录口径修正（硅基流动 / OpenRouter 本就不在目录，走「自定义」）。
- 评测工具修复：`eval.js --data-dir` 复制数据目录时排除任务租约锁（源实例独占句柄会导致 EBUSY）与旧实例残留的 `ui.url`（指向旧端口旧令牌，waitForUrl 会立刻读到然后 fetch 失败）；预检前回写 provider 字段触发服务端强制探测并轮询等待（全新实例的 `/api/state` 走只读缓存，`active` 不会自己变 `provider`）。

## 3.10.4

工程基线（一次性补齐，无功能行为变更）：

- **版本单一事实源**：修平 `AssemblyInformationalVersion` 3.9.1、`package-lock.json` 3.9.15、`app.manifest` 3.1.0.0 三处陈年漂移；新增 `tools/project-manifest.js`（事实源 + 校验/同步）、`tools/check-version.js`（门禁）、`tools/bump-version.js`（唯一升版本入口）。
- **界面不再写死版本号**：`?v=__GT_VERSION__` 由 `WebServer.ServeUi` 注入 `AppInfo.Version`，显示版本来自 `/api/state`；校验器会拒绝任何重新硬编码版本号的行为。
- **资源清单门禁**：`build.ps1` 编译前强制比对 `UI_ASSETS` / `build.ps1` / `App*.cs` / `WebServer.cs` / `ui/` 五处，并做门禁自锁（防止有人删掉校验）。
- **一键验证与 CI**：新增 `npm run verify:all`（6 步全链路 + 逐步证据文件）；`regression.js` 增加 `check-version` 步骤；新增 `.github/workflows/ci.yml`（Windows runner：静态 → 构建 → 回归）。
- **工程文档**：`docs/architecture.md`、`docs/security.md`、`CONTRIBUTING.md`、本文件、`.editorconfig`；`.gitignore` 补 `nul` 与 `**/GreenTiger-data/`，并清理了误写产生的 `nul` 文件。

安全与可靠性：

- `--lan` 日志不再明文打印访问令牌；`/api/health` 未鉴权探针不再回版本号。
- `Guard.Fatal` 补强：编码命令（`-EncodedCommand`）、Base64 解码后执行、带引号的 `reg delete "HKLM\..."`、盘符根通配符删除（`del /f /q C:\*.*`）纳入硬拦；新增 `-Encoding` 不误报断言。
- 10 处关键链路的空 `catch` 改为留痕：HTTP 4xx 响应体读取失败、规则目录加载失败（护栏曾被静默降级）、Origin 白名单枚举失败、SSE 断连、后台进程等待失败、进程树终止失败等。
- `Http.cs` 两处 `HttpWebResponse` 改为 `using` 释放；`WebServer.LoadAsset` 资源流在异常路径也释放；修正 `ServeEvents` 上错放的「路径越界 403」注释。
- 新增回归断言：`Program.cs` 的 Guard 绕过变体与 `Fs.IsInside` 边界形态（同前缀目录、父目录、大小写、绝对路径逃逸）；`smoke.js` 的错误令牌 / 伪造 Cookie / 伪造 Origin / SSE 与静态资源鉴权。
- 修复版本清理误删测试产物：`CleanupStaleTempOnUpdate` 在首次运行（无版本戳）时只写戳、不清理。此前 smoke/e2e/shot 的全新临时数据目录会触发清理，把 `ui-shot` 刚生成的截图删掉，并行测试时还会互删恢复夹具；`smoke.js` 新增哨兵断言锁住这个行为。

结构：

- `src/App.cs` 用 `partial class` 把内嵌 UI 资产与版本戳逻辑拆到 `src/AppUiAssets.cs`（2451 → 2308 行），修掉一处悬空注释；`check-version.js` 自动在所有 `App*.cs` 里定位资源清单。
- 新增单体文件规模趋势提示（只告警、不阻断构建）；其余大文件的拆分路线与理由见 `docs/architecture.md`。

文档口径修正：README 的「一次性令牌」改为「进程级随机令牌」，并如实说明路径边界是审批式软边界、黑名单可绕过形态等已知局限。
