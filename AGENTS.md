# AGENTS.md

仓库协作约定。产品说明见 [README.md](README.md)。

## 命名

只有两个名字：英文 **GreenTiger**，中文 **绿虎**，不带后缀。代码标识固定一套：命名空间 `GreenTiger`、资源 `GreenTiger.ui.*`、存储键 `greentiger.*`、Cookie `greentiger_token`、请求头 `X-GreenTiger-Token`、协议围栏 ```` ```greentiger ````、数据目录 `GreenTiger-data`、可执行文件 `GreenTiger.exe`。不做旧名兼容。

## 结构

| 文件 | 职责 |
|---|---|
| `src/Agent.cs` | 任务循环、工具协议、系统提示词 |
| `src/Runtime.cs` | 任务终态、错误分类、预算、任务记录 |
| `src/ToolRegistry.cs` / `src/ToolSchemas.cs` | 工具注册、参数校验、Schema 目录 |
| `src/Tools.cs` / `src/Guard.cs` | 工具实现 / 安全护栏 |
| `src/LocalOps.cs` | 本机运维工具（搜索、体检、卸载、清缓存、粉碎） |
| `src/Brain.cs` / `src/Providers.cs` | 模型客户端（原生 function calling + 文本围栏降级）/ 服务商目录 |
| `src/Connect.cs` | `/connect` 接入向导：Claude 官方兼容层 / 中转站 |
| `src/ContextPolicy.cs` | 上下文预算与截断策略 |
| `src/Verifier.cs` / `src/Walkthrough.cs` | 独立验收 / 任务走查记录（证据强度四档） |
| `src/Evolution.cs` / `src/AgentMemory.cs` | 本地经验蒸馏 / 项目记忆 |
| `src/Recovery.cs` / `src/Trace.cs` / `src/Hooks.cs` | 中断恢复 / 结构化轨迹 / tool-use hooks |
| `src/Http.cs` | 全项目唯一 HTTP 出口（超时 / 代理 / UA / 4xx 回传） |
| `src/WebServer.cs` / `src/App.cs` | HTTP + SSE 控制台 / 应用状态与托盘 |
| `src/Sandbox.cs` / `src/Supervisor.cs` | 低完整性沙箱 / danger 审批的风险说明 |
| `src/SelfTestRuntime.cs` | 自检用例（单元 + 循环级） |
| `ui/index.html` `ui/style.css` `ui/theme.css` `ui/js/*.js` | 控制台界面（17 个内嵌资源） |
| `tools/project-manifest.js` | UI 内嵌资源清单的事实源 |
| `tools/check-version.js` / `tools/bump-version.js` | 构建期一致性门禁 / 升版本唯一入口 |
| `tools/verify-all.js` | 发布前一键全量验证 |
| `docs/` | 架构、安全、模型接入、长上下文、经验蒸馏 |

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1   # → dist\GreenTiger.exe
```

- 系统自带编译器，不引 NuGet / 前端构建链；新增 C# 文件会被 `src/*.cs` 自动收集。
- 构建前强制跑 `tools/check-version.js`：版本落点、UI 资源清单、UI 占位符任一不一致即拒绝编译。
- 版本唯一事实源是 `src/Config.cs` 的 `AppInfo.Version`。升版本只用 `npm run bump:version <x.y.z>`；界面源码禁止硬编码版本号，缓存戳用 `?v=__GT_VERSION__`。
- 新增 UI 资源必须同步四处：`tools/project-manifest.js` 的 `UI_ASSETS`、`build.ps1` 的 `$resources`、`App*.cs` 的 `names[]`、`WebServer.cs` 的静态路由。

## 测试

```powershell
npm run check:version   # 版本 / 资源清单 / 门禁自锁
npm run check:deps      # Node / playwright-core / Edge / 编译器 / WebView2
dist\GreenTiger.exe --self-test
npm run check:ui        # 内联脚本：语法 + 死 id 引用 + 图标名
npm test                # smoke + recovery + regression
npm run e2e             # 界面端到端（Node 20 + 本机 Edge）
npm run verify:all      # 发布前一键全量
```

改 JS 先 `node --check`；改 C# 必须重编译并跑 `--self-test`；改 UI 后必须重新 `build.ps1`（界面是内嵌资源，不重编译会把旧界面发给用户）。

## 约束

- 不削弱 Guard、路径边界、令牌 / Origin 校验。
- 不留孤儿接口：HTTP 路由必须对应真实调用点；不留孤儿资源：UI 资源四处清单由 `check-version.js` 强制比对。
- 数据目录只有 `GreenTiger-data`（便携）与 `%APPDATA%\GreenTiger`（系统）两种。
- 交付要给验证证据：命令 + 结果；没验证就写「未验证」。
