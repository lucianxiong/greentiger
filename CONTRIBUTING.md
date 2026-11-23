# 参与开发

先读 [AGENTS.md](AGENTS.md)（命名与结构约定）和 [docs/architecture.md](docs/architecture.md)（分层与构建链），再动手。安全相关改动还要读 [docs/security.md](docs/security.md)。

## 环境

Windows + 系统自带 .NET Framework 4.x（提供 `csc.exe`）+ Node 20 以上（`playwright-core` 1.64 要求 Node 20）。不需要 Visual Studio、NuGet 或前端构建工具。

```bash
npm ci                 # 只装 playwright-core，用本机 Edge，不下载浏览器
npm run check:deps     # 确认 Node / Edge / csc.exe / WebView2 三件套齐全
```

## 按改动类型选择验证

| 改了什么 | 必须跑 |
|---|---|
| `ui/index.html`（含内联脚本）、`ui/*.css`、`ui/js/*.js` | `npm run check:ui` → `npm run e2e` → `npm run build` → `npm run shot`；**并升版本号** |
| `src/*.cs`（Runtime / 工具 / 提示词） | `npm run build` → `dist\GreenTiger.exe --self-test` → `npm test` |
| 版本号或 UI 资源清单 | `npm run check:version` → `npm run build` |
| 文档 / CI / 工具脚本 | `node --check <脚本>` + 受影响的检查 |
| 发布前 | `npm run verify:all`（全链路逐步输出证据） |

`npm run test` 里的 `regression.js` 会自己编译并跑 `--self-test`、冒烟、中断恢复、评测结构、报告器、影子运行；`verify:all` 再补上真机 e2e 和截图。

**改 UI 一定要重新 `build.ps1`**：界面是程序集内嵌资源，`App.ExportEmbeddedUi` 按 `AppInfo.Version` 版本戳覆盖数据目录 `ui/`；不重编译就会把旧界面发给用户。

## 升版本

只有一个入口，不要手改散落的版本字符串：

```bash
npm run bump:version 3.10.5
```

它会同步 `src/Config.cs`（事实源）、`AssemblyInfo.cs` 三个属性、`package.json`、`package-lock.json`、`app.manifest`，然后立刻自检。界面源码里**不允许**写死版本号：缓存戳用 `?v=__GT_VERSION__`（服务端注入），显示版本读 `/api/state`。

## 新增 UI 资源

同步四处，缺一处 `check-version.js` 就会拒绝构建：

1. `tools/project-manifest.js` 的 `UI_ASSETS`（事实源）
2. `build.ps1` 的 `$resources`
3. `src/App.cs` 的 `ExportEmbeddedUi` → `names[]`
4. `src/WebServer.cs` 的静态资源路由 / `UiModules` 白名单

新增 **C# 文件**不需要改构建脚本，`build.ps1` 自动收集 `src/*.cs`；用 `partial class` 拆大文件时记得给原类声明加 `partial`。

## 红线

- 不削弱 Guard、路径边界、令牌 / Origin 校验；新增 HTTP 路由必须有真实 UI / 脚本调用点，不留孤儿接口。
- 对话区是**增量渲染**（`reconcileMessages` + `msgRenderKey`），不要退回全量 `innerHTML` 重绘；三处补全共用 `fuzzyScore` / `fuzzyRank`。
- UI 视觉只用 `ui/theme.css` 的设计令牌，不写死色值。
- 命名只有 GreenTiger / 绿虎两种，标识统一为 `GreenTiger` 命名空间、`GreenTiger.ui.*` 资源、`greentiger.*` 存储键。

## 提交

提交信息写清楚「改了什么 + 为什么」，并附验证证据（命令与结果）。没验证就明确写「未验证」，不要写「应该没问题」。安全修复要同时补一条回归断言，防止以后被无声削弱。
