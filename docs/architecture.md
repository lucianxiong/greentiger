# 架构说明

GreenTiger 是一个 Windows 桌面 Agent：模型负责规划，工具在本机执行，形成「规划 → 执行 → 观察 → 再规划」闭环。本文描述的是**工程结构**，产品能力见 [README.md](../README.md)，协作约定见 [AGENTS.md](../AGENTS.md)。

## 技术边界

| 项 | 选择 | 原因 |
|---|---|---|
| 运行时 | .NET Framework 4.x（系统自带 `csc.exe`） | 拷贝 exe 即可运行，不需要安装运行时或 Visual Studio |
| 依赖 | 零 NuGet；唯一 Node 开发依赖是 `playwright-core` | 保持构建链可复现、离线可编译 |
| 前端 | 原生 ES modules + CSS 变量，无打包器 | UI 是程序集内嵌资源，运行时直接下发 |
| 模型通道 | 唯一：OpenAI 兼容端点 | 不绑定厂商，自建网关也能接 |
| HTTP 出口 | `src/Http.cs` 是唯一网络出口 | 超时 / 代理 / UA / SSRF 复查集中在一处 |

## 分层

```
入口        Program.cs ─ 参数解析 / 单实例 / 程序集解析 / --self-test
宿主        App.cs ─ 应用状态、会话、托盘（内嵌 UI 资产拆到 AppUiAssets.cs）
服务        WebServer.cs ─ 本地 HTTP + SSE 控制台（唯一对外接口）
循环        Agent.cs ─ 任务循环、工具协议、系统提示词
能力        ToolRegistry.cs / ToolSchemas.cs / Tools.cs ─ 注册、Schema、实现
            Brain.cs / Providers.cs ─ 模型路由与 API 客户端
判定        Runtime.cs / Verifier.cs / Guard.cs / Approval.cs / Recovery.cs
            ContextPolicy.cs / TaskPolicy.cs / Scheduler.cs / Trace.cs
外设        BrowserWindow.cs（WebView2）/ FileOps.cs / FileBrowser.cs / Mcp.cs
界面        ui/index.html + ui/js/*.js + ui/*.css（17 个内嵌资源）
```

数据流：用户在控制台发消息 → `WebServer` 鉴权后交给 `App` → `Agent` 组装提示词并调用 `Brain` → 模型返回 `tool_calls`（或文本围栏降级）→ `ToolRegistry` 校验参数 → `Guard` / `Approval` 决定是否放行 → `Tools` 执行 → 结果回灌模型 → `Verifier` 验收 → `Trace` 记录。状态通过 SSE 推给前端，前端按消息 key 做增量渲染。

## 构建与内嵌 UI

`build.ps1` 用 `csc.exe` 把 `src/*.cs` 全量编入一个 `winexe`，把 `ui/` 下 17 个文件作为 `/resource:` 嵌入程序集。**新增 C# 文件不需要改构建脚本**（`Get-ChildItem src -Filter *.cs` 自动收集），但**新增 UI 资源必须同步四处**：

1. `tools/project-manifest.js` 的 `UI_ASSETS`（事实源）
2. `build.ps1` 的 `$resources`
3. `src/App.cs` 的 `ExportEmbeddedUi` → `names[]`
4. `src/WebServer.cs` 的静态资源路由 / `UiModules` 白名单

`tools/check-version.js` 会在构建前比对这四份清单，不一致直接拒绝编译。运行时 `App.ExportEmbeddedUi` 按 `AppInfo.Version` 版本戳把内嵌 UI 覆盖到数据目录 `ui/`，所以**改 UI 必须升版本**，否则用户拿到的是旧界面。

## 版本机制

唯一事实源是 `src/Config.cs` 的 `AppInfo.Version`。派生落点（程序集三个版本属性、`package.json`、`package-lock.json`、`app.manifest`）由 `npm run bump:version <x.y.z>` 一次性同步，并由 `check-version.js` 校验。

界面源码**不允许出现硬编码版本号**：缓存戳写成 `?v=__GT_VERSION__`，由 `WebServer.ServeUi` 在下发 HTML 时替换为 `AppInfo.Version`；显示版本（`#verLabel` / `#inspectorVer`）一律取自 `/api/state` 的 `version` 字段。这样升版本只改一处，不会再出现 `3.9.1` 这类漂移。

## 结构拆分现状与路线

拆分遵循「零行为变更优先」。`src/App.cs` 里内嵌 UI 资产与版本戳相关的代码已用 `partial class` 拆到 `src/AppUiAssets.cs`。因为 `build.ps1` 自动收集 `src/*.cs`，新增 C# 文件不需要改构建脚本；`check-version.js` 会自动在所有 `App*.cs` 里定位 `names[]` 清单，不会因为拆分而漏检。

其余三个千行级文件（`Agent.cs`、`Tools.cs`、`BrowserWindow.cs`）与 `ui/js/app.js` 暂不一刀切：

- `Agent.cs` 的提示词构造与任务循环共享大量实例状态，硬拆容易改变模型行为；
- `ui/js/app.js` 的模块边界与增量渲染 key、三处补全共用的打分器耦合，拆错会导致界面不刷新；
- 项目没有前端打包器，也没有覆盖 UI 的单元测试框架，一次性大拆的验证成本高于收益。

`check-version.js` 会持续打印超过阈值（`src/*.cs` 2500 行、`ui/js/*.js` 3000 行）的文件作为趋势提示，但不阻断构建。后续拆分顺序建议：WebServer 的静态资源路由收敛为单一数组 → `Tools.cs` 按工具族拆 partial class → `app.js` 按设置/会话/工作台拆 ES module；每一步都必须跑 `npm run verify:all`。

## 验证层次

| 层 | 命令 | 覆盖 |
|---|---|---|
| 静态 | `npm run check:version` | 版本 / 资源清单 / UI 占位 / 门禁自锁 |
| 静态 | `npm run check:ui` | 内联脚本语法、死 id 引用、重复 id、图标名、import 闭合 |
| 环境 | `npm run check:deps` | Node / playwright-core / Edge / csc.exe / WebView2 |
| 进程内 | `dist\GreenTiger.exe --self-test` | 300+ 单元与循环级断言 |
| 进程级 | `npm test` | smoke + recovery + regression（构建、自检、评测、影子运行） |
| 真机 | `npm run e2e` / `npm run shot` | Edge 点一遍 / 截图人眼校对 |
| 全量 | `npm run verify:all` | 以上全部串成一条，逐步输出证据 |

CI（`.github/workflows/ci.yml`）跑静态 + 构建 + 回归；真机层需要图形会话，由发布前本地 `verify:all` 完成。
