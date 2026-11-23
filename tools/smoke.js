// GreenTiger 端到端冒烟测试（零依赖，Node 18+ 自带 fetch）。
// 用法：node tools/smoke.js [--exe=dist/GreenTiger.exe]
// 会用一个临时数据目录启动 GreenTiger，验证鉴权、状态、重命名、导出和界面资源，然后退出进程。
const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawn } = require("child_process");

const root = path.resolve(__dirname, "..");
const argExe = process.argv.find((a) => a.startsWith("--exe="));
const exe = argExe ? path.resolve(root, argExe.slice(6)) : path.join(root, "dist", "GreenTiger.exe");
const port = Number(process.env.GREENTIGER_SMOKE_PORT || 17990);
const dataDir = path.join(os.tmpdir(), "GreenTiger", "smoke-" + Date.now());
const base = "http://127.0.0.1:" + port;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitForUrl() {
  const file = path.join(dataDir, "ui.url");
  for (let i = 0; i < 80; i++) {
    try {
      const url = fs.readFileSync(file, "utf8").trim();
      if (url) return url;
    } catch (e) {}
    await sleep(250);
  }
  throw new Error("ui.url 未生成，GreenTiger 可能启动失败");
}

(async () => {
  if (!fs.existsSync(exe)) {
    console.error("找不到可执行文件: " + exe + "，请先运行 build.ps1");
    process.exit(2);
  }
  // 版本清理回归：全新临时数据目录（没有版本戳）启动不应清掉 %TEMP%\GreenTiger 下已有产物。
  // 修复前这个哨兵会被 CleanupStaleTempOnUpdate 删掉，shot-* 截图正是这样丢的。
  const tempRoot = path.join(os.tmpdir(), "GreenTiger");
  fs.mkdirSync(tempRoot, { recursive: true });
  const sentinel = path.join(tempRoot, "shot-sentinel-" + Date.now() + ".png");
  fs.writeFileSync(sentinel, "sentinel", "utf8");

  const child = spawn(exe, ["--no-browser", "--port=" + port, "--data-dir=" + dataDir], { stdio: "ignore" });
  const fails = [];
  const ok = (name, cond, detail) => {
    console.log((cond ? "PASS " : "FAIL ") + name + (detail ? " :: " + detail : ""));
    if (!cond) fails.push(name);
  };
  try {
    const uiUrl = await waitForUrl();
    const token = new URL(uiUrl).searchParams.get("token");
    const auth = { "X-GreenTiger-Token": token };

    const health = await (await fetch(base + "/api/health")).json();
    ok("health", health.ok === true, JSON.stringify(health));

    ok("first run does not purge temp artifacts", fs.existsSync(sentinel), path.basename(sentinel));
    try { fs.unlinkSync(sentinel); } catch (e) {}

    const unauthorized = await fetch(base + "/api/state");
    ok("api rejects missing token", unauthorized.status === 401, "status=" + unauthorized.status);

    const state = await (await fetch(base + "/api/state", { headers: auth })).json();
    ok("state", !!state.active && typeof state.config === "object", "sessions=" + state.sessions.length);
    ok("config replyTimeout", typeof state.config.replyTimeout === "number", String(state.config.replyTimeout));
    ok("config exposes motion and sound preferences", typeof state.config.uiMotion === "boolean" && typeof state.config.uiSounds === "boolean",
      "motion=" + state.config.uiMotion + " sounds=" + state.config.uiSounds);
    ok("reply shell block execution removed", !Object.prototype.hasOwnProperty.call(state.config, "autoRunShellBlocks"), "field absent");

    await fetch(base + "/api/config", {
      method: "POST",
      headers: Object.assign({ "Content-Type": "application/json" }, auth),
      body: JSON.stringify({ uiMotion: false, uiSounds: true }),
    });
    const preferenceState = await (await fetch(base + "/api/state", { headers: auth })).json();
    ok("motion and sound preferences persist", preferenceState.config.uiMotion === false && preferenceState.config.uiSounds === true,
      "motion=" + preferenceState.config.uiMotion + " sounds=" + preferenceState.config.uiSounds);
    await fetch(base + "/api/config", {
      method: "POST",
      headers: Object.assign({ "Content-Type": "application/json" }, auth),
      body: JSON.stringify({ reasoningEffort: "xhigh" }),
    });
    const effortState = await (await fetch(base + "/api/state", { headers: auth })).json();
    ok("fourth reasoning effort persists", effortState.config.reasoningEffort === "xhigh", effortState.config.reasoningEffort);

    const rules = await (await fetch(base + "/api/rules", { headers: auth })).json();
    ok("rules expose layered and path-scoped files", Array.isArray(rules.projectFiles) && Array.isArray(rules.pathRules) && Object.prototype.hasOwnProperty.call(rules, "user"),
      "projectFiles=" + (rules.projectFiles || []).length + " pathRules=" + (rules.pathRules || []).length);

    const rulesRoot = path.join(dataDir, "rules-workspace");
    fs.mkdirSync(path.join(rulesRoot, ".git"), { recursive: true });
    fs.mkdirSync(path.join(rulesRoot, ".claude", "rules"), { recursive: true });
    fs.writeFileSync(path.join(rulesRoot, "AGENTS.md"), "workspace rule", "utf8");
    fs.writeFileSync(path.join(rulesRoot, ".claude", "rules", "csharp.md"),
      "---\npaths:\n  - \"src/**/*.cs\"\n---\nUse the fixture C# style.", "utf8");
    await fetch(base + "/api/config", {
      method: "POST",
      headers: Object.assign({ "Content-Type": "application/json" }, auth),
      body: JSON.stringify({ workDir: rulesRoot }),
    });
    const scopedRules = await (await fetch(base + "/api/rules", { headers: auth })).json();
    ok("rules API lists matching path selectors", scopedRules.projectFiles.length === 1 && scopedRules.pathRules.length === 1 &&
      scopedRules.pathRules[0].paths[0] === "src/**/*.cs" && scopedRules.pathRules[0].baseDir === rulesRoot,
      JSON.stringify(scopedRules.pathRules));

    const sid = state.active.id;
    const renamed = await (await fetch(base + "/api/session/rename", {
      method: "POST",
      headers: Object.assign({ "Content-Type": "application/json" }, auth),
      body: JSON.stringify({ id: sid, title: "冒烟 测试 会话" })
    })).json();
    ok("rename session", renamed.active.title === "冒烟 测试 会话", renamed.active.title);

    const blank = await (await fetch(base + "/api/session/rename", {
      method: "POST",
      headers: Object.assign({ "Content-Type": "application/json" }, auth),
      body: JSON.stringify({ id: sid, title: "  " })
    })).json();
    ok("rename rejects blank title", blank.active.title === "冒烟 测试 会话", blank.active.title);

    const mdRes = await fetch(base + "/api/session/export?id=" + encodeURIComponent(sid), { headers: auth });
    const md = await mdRes.text();
    ok("export markdown", mdRes.status === 200 && md.indexOf("# 冒烟 测试 会话") === 0, md.split("\n")[0]);

    const gtCookie = { Cookie: "greentiger_token=" + token };
    const page = await (await fetch(base + "/", { headers: gtCookie })).text();
    // P3：index.html 已是干净结构，模板里的 id 移到 js/app.js，样式移到 style.css。
    // 这里同时验证三个新静态资源都能带 token 正常取回，防止内嵌资源清单漏登记。
    const servedApp = await (await fetch(base + "/js/app.js", { headers: gtCookie })).text();
    const servedCss = await (await fetch(base + "/style.css", { headers: gtCookie })).text();
    ok("ui served", page.indexOf("sessionSearch") > 0 && page.indexOf("settingsBtn") > 0 &&
      page.indexOf('type="module" src="/js/app.js"') > 0 && page.indexOf('href="/style.css"') > 0,
      page.length + " bytes");
    ok("split style and ES module are served", servedCss.indexOf(".composer{") >= 0 &&
      servedApp.indexOf("import {") >= 0 && servedApp.indexOf("init();") >= 0, "");
    ok("home is one question line plus the garden card and the direct reasoning slider", page.indexOf('id="effortRange"') >= 0 &&
      servedApp.indexOf("今天想做点什么") >= 0 && servedApp.indexOf('id="emptyGarden"') >= 0 &&
      page.indexOf('id="effortBtn"') < 0 && servedApp.indexOf('id="effortBtn"') < 0, "");

    const providers = await (await fetch(base + "/api/providers", {
      method: "POST",
      headers: Object.assign({ "Content-Type": "application/json" }, auth),
      body: "{}",
    })).json();
    const freeOnes = (providers.providers || []).filter((p) => p.free);
    ok("providers are official flagship only", freeOnes.length === 0 &&
      (providers.providers || []).length === 5 &&
      (providers.providers || []).every((p) => p.models && p.models.length > 0),
      "free=" + freeOnes.length + "/" + (providers.providers || []).length);

    await fetch(base + "/api/config", {
      method: "POST",
      headers: Object.assign({ "Content-Type": "application/json" }, auth),
      body: JSON.stringify({
        providerId: "groq",
        providerKey: "",
        providerEndpoint: "https://api.groq.com/openai/v1",
        providerModel: "llama-3.3-70b-versatile",
      }),
    });
    const st2 = await (await fetch(base + "/api/state", { headers: auth })).json();
    ok("provider config persists", st2.config.providerId === "groq" &&
      String(st2.config.providerEndpoint).indexOf("groq.com") > 0,
      st2.config.providerId + "@" + st2.config.providerEndpoint);

    const removedCatalog = await fetch(base + "/api/api-sites", { method: "POST", headers: auth, body: "{}" });
    ok("non-agent API directory removed", removedCatalog.status === 404, "status=" + removedCatalog.status);

    const badUrl = await fetch(base + "/api/open-url", {
      method: "POST",
      headers: Object.assign({ "Content-Type": "application/json" }, auth),
      body: JSON.stringify({ url: "file:///C:/Windows/System32/calc.exe" }),
    });
    ok("open-url rejects non-http", badUrl.status === 400, "status=" + badUrl.status);

    const redirect = await fetch(uiUrl, { redirect: "manual" });
    ok("token sets cookie", redirect.status === 302, "status=" + redirect.status);

    const blocked = await fetch(base + "/");
    ok("ui requires token", blocked.status === 403, "status=" + blocked.status);

    // ---- 鉴权 / CSRF 行为回归 ----
    // 这些能力以前只写在文档里，没有任何请求级断言；削弱护栏不会再被静默放过。
    ok("health does not leak version", !Object.prototype.hasOwnProperty.call(health, "version"), JSON.stringify(health));

    const badToken = await fetch(base + "/api/state", { headers: { "X-GreenTiger-Token": token + "x" } });
    ok("api rejects wrong token", badToken.status === 401, "status=" + badToken.status);

    const badCookie = await fetch(base + "/api/state", { headers: { Cookie: "greentiger_token=" + token.slice(0, -1) } });
    ok("api rejects wrong cookie value", badCookie.status === 401, "status=" + badCookie.status);

    const wrongName = await fetch(base + "/api/state", { headers: { Cookie: "other_token=" + token } });
    ok("api rejects wrong cookie name", wrongName.status === 401, "status=" + wrongName.status);

    const badOrigin = await fetch(base + "/api/state", { headers: Object.assign({ Origin: "http://evil.example" }, auth) });
    ok("api rejects bad origin", badOrigin.status === 403, "status=" + badOrigin.status);

    const goodOrigin = await fetch(base + "/api/state", { headers: Object.assign({ Origin: base }, auth) });
    ok("api accepts loopback origin", goodOrigin.status === 200, "status=" + goodOrigin.status);

    const anonEvents = await fetch(base + "/api/events");
    ok("sse requires token", anonEvents.status === 401, "status=" + anonEvents.status);

    const anonAsset = await fetch(base + "/js/app.js");
    ok("static asset requires cookie", anonAsset.status === 401, "status=" + anonAsset.status);

    // 开关控件只能有一套实现：旧版 <span class="knob"> 会和新版 ::after 圆点叠成两个圆
    // P3：界面已拆成 index.html + style.css + js/*.js。形态断言统一读合并后的源，
    // 这样既有几十条字符串断言不用逐条重写，仍然覆盖拆分后的全部代码。
    const uiDir = path.join(root, "ui");
    const uiModules = ["util.js", "markdown.js", "tools.js", "chat.js", "model.js", "app.js"];
    const uiHtml = [
      fs.readFileSync(path.join(uiDir, "index.html"), "utf8"),
      fs.readFileSync(path.join(uiDir, "style.css"), "utf8"),
      ...uiModules.map((f) => fs.readFileSync(path.join(uiDir, "js", f), "utf8"))
    ].join("\n");
    ok("approval explains target and task scope",
      uiHtml.indexOf("操作目标") >= 0 && uiHtml.indexOf("本任务同工具免确认") >= 0 && uiHtml.indexOf("高风险操作必须逐次确认") >= 0, "");
    ok("rules panel exposes scoped selectors", uiHtml.indexOf("路径规则：已加载") >= 0 && uiHtml.indexOf(".claude/rules/") >= 0, "");
    ok("switch has no legacy knob markup", uiHtml.indexOf('class="knob"') < 0 && uiHtml.indexOf(".switch .knob") < 0, "");
    // 官方插件开关（pluginFlags）已整体删除：界面、API、配置三处都不再有残留
    ok("dead plugin switches removed",
      uiHtml.indexOf('input type="checkbox" data-flag=') < 0 &&
      uiHtml.indexOf("pluginFlagsList") < 0 &&
      uiHtml.indexOf("renderPluginFlags") < 0, "");
    // 几何校验（与 ui/index.html 中的开关实现保持一致）：
    // 旋钮为正方形；OFF 时 left+size <= 轨道宽；ON 时用 left:calc(100% - Npx) 保证右端留白 >=0（绝不溢出）。
    const knob = uiHtml.match(/\.switch span::after\{[^}]*width:(\d+)px;height:(\d+)px;left:(\d+)px;top:50%/);
    const track = uiHtml.match(/\.switch\{[^}]*width:(\d+)px;height:(\d+)px/);
    const onShift = uiHtml.match(/\.switch input:checked\+span::after\{left:calc\(100% - (\d+)px\)/);
    const fits = !!(knob && track && onShift) &&
      Number(knob[1]) === Number(knob[2]) &&                       // 旋钮是正方形
      Number(knob[3]) + Number(knob[1]) <= Number(track[1]) &&     // OFF：左内边距+旋钮宽 <= 轨道宽
      Number(onShift[1]) >= Number(knob[1]) &&                    // ON：右内边距(=onShift-size) >= 0
      Number(track[2]) >= Number(knob[2]);                         // 轨道高 >= 旋钮高
    ok("switch knob stays inside track", fits,
      knob && track && onShift ? "knob=" + knob[1] + "x" + knob[2] + " offLeft=" + knob[3] + " track=" + track[1] + "x" + track[2] + " onRightInset=" + onShift[1] : "样式未找到");

    // 控件统一性护栏：同类控件只允许一套实现。历史别名已从 DOM 与 CSS 中彻底清除，不保留第二套写法。
    ok("mini button has single implementation",
      /^\s*\.btn\.mini\{/m.test(uiHtml) && uiHtml.indexOf(".mini-btn") < 0, "");
    ok("toggle label has single implementation",
      /^\s*\.toggle-title\{/m.test(uiHtml) && uiHtml.indexOf(".toggle-label") < 0, "");
    ok("topbar pill keeps its own geometry, composer controls share one badge",
      /^\s*\.ds-chip\{/m.test(uiHtml) && uiHtml.indexOf(".ds-chip,.mode-select{") < 0 &&
      /^\s*\.cmp-badge\{/m.test(uiHtml) && uiHtml.indexOf('class="cmp-badge mode-select"') >= 0, "");
    ok("no fourth button size variant", !/^\s*\.settings-actions \.btn\{/m.test(uiHtml), "");
    const deepSeekButtonCount = (uiHtml.match(/id="openDeepSeekBtn"/g) || []).length;
    // 连接与模型合并为一个按钮：状态点 + 当前模型名 + ⌄，点开就是切换器
    ok("connection and model switch are one merged control",
      deepSeekButtonCount === 0 &&
      /<button type="button" class="ds-chip" id="modelBtn"/.test(uiHtml) &&
      uiHtml.indexOf('id="dsChipDot"') >= 0 && uiHtml.indexOf('id="dsChipText"') >= 0 &&
      uiHtml.indexOf("chip-chev") >= 0 &&
      uiHtml.indexOf('getElementById("dsChip")') < 0,
      "separate buttons=" + deepSeekButtonCount);
    // 界面不绑定任何一家厂商：网页版入口与协议残留全部删除，接入语义统一为「模型」。
    // 设置页可以列出服务商名字，但不得再出现网页版 DeepSeek 专用入口/接口。
    ok("ui has no vendor-specific entry",
      uiHtml.indexOf("/api/open-deepseek") < 0 && uiHtml.indexOf("openDeepSeek") < 0 &&
      uiHtml.indexOf("openConnected") < 0, "");
    ok("connection pill reflects the actually active model",
      uiHtml.indexOf("function connInfo()") >= 0 && uiHtml.indexOf("function brainInfo()") >= 0 &&
      uiHtml.indexOf('"未接入模型"') >= 0 && uiHtml.indexOf('bi.active === "provider"') >= 0,
      "");
    ok("local model option removed; home setup card gone, config lives in the composer",
      uiHtml.indexOf('data-model-setup') < 0 &&
      uiHtml.indexOf("/api/brain/discover") < 0 &&
      uiHtml.indexOf("discoverLocalModel") < 0 &&
      uiHtml.indexOf("Ollama") < 0 && uiHtml.indexOf("LM Studio") < 0, "");
    ok("model switch panel closes on Escape",
      uiHtml.indexOf("window.closeModelPop = closeCmpPop") >= 0 &&
      uiHtml.indexOf('if (modelPop && !modelPop.hidden && typeof window.closeModelPop === "function")') >= 0, "");
    // 展开面板必须挂在触发按钮所在的容器里（输入框顶部的 .model-wrap），不能再用 fixed 贴到屏幕另一角
    ok("model panel is anchored to its trigger (top of composer) and owns the thinking-effort slider",
      /<div class="cmp-pop" id="cmpPop" role="group"[^>]*>/.test(uiHtml) &&
      uiHtml.indexOf('class="model-wrap"') < uiHtml.indexOf('id="cmpPop"') &&            // 面板在模型胶囊容器内（输入框顶部）
      uiHtml.indexOf('id="cmpPop"') < uiHtml.indexOf('class="composer-bar"') &&          // 模型胶囊在输入框顶部，早于底部工具行
      uiHtml.indexOf('id="cmpPopItems"') > uiHtml.indexOf('id="cmpPop"') &&              // 只重写列表区，强度控件静态保留
      uiHtml.indexOf('id="effortRange"') > uiHtml.indexOf('id="cmpPop"') &&
      uiHtml.indexOf('id="effortRange"') < uiHtml.indexOf('id="cmpParams"') &&           // 强度已不在齿轮面板里
      !/position:fixed;right:18px;top:68px/.test(uiHtml) &&
      /\.cmp-pop\{\s*\n\s*position:absolute;right:0;bottom:calc\(100% \+ 8px\)/.test(uiHtml),
      "");
    ok("sidebar supports pin / archive with a dedicated archived view",
      uiHtml.indexOf('data-pin-session') >= 0 && uiHtml.indexOf('data-archive-session') >= 0 &&
      uiHtml.indexOf('data-session-tab="archived"') >= 0 && uiHtml.indexOf('id="bulkBar"') >= 0 &&
      uiHtml.indexOf('bulkSessionAction') >= 0 &&
      uiHtml.indexOf('setSessionFlag') >= 0 && uiHtml.indexOf('showArchived') >= 0, "");
    ok("mainstream alignment: subagent rows, recent models, collapsible groups",
      uiHtml.indexOf('subagent-state') >= 0 && uiHtml.indexOf('recordModelUse') >= 0 &&
      uiHtml.indexOf('sessionGroupHtml') >= 0 && uiHtml.indexOf('data-group=') >= 0, "");
    ok("composer keeps + on the left, send on the right, model pill on top",
      uiHtml.indexOf('id="addPop"') < uiHtml.indexOf('id="paramsBtn"') &&
      uiHtml.indexOf('id="addPop"') < uiHtml.indexOf('id="helpBtn"') &&
      uiHtml.indexOf('class="model-wrap"') < uiHtml.indexOf('class="composer-bar"') &&   // 模型胶囊在输入框顶部
      uiHtml.indexOf('class="model-wrap"') < uiHtml.indexOf('id="sendBtn"') &&
      uiHtml.indexOf(".help-btn{") < 0, "");
    ok("dead icons and dead subtle-button styles are gone",
      uiHtml.indexOf("prompt:'<path") < 0 && uiHtml.indexOf("zap:'<path") < 0 &&
      uiHtml.indexOf(".btn.subtle") < 0, "");
    ok("permission rules have a minimal settings UI",
      uiHtml.indexOf('id="permitList"') >= 0 && uiHtml.indexOf('id="permitAddBtn"') >= 0 &&
      uiHtml.indexOf('permitRulesDraft') >= 0 && uiHtml.indexOf('permitRules:permitRulesDraft') >= 0, "");
    ok("free-tier providers live in settings and message branching stays exposed",
      uiHtml.indexOf('showFreeMenu') < 0 && uiHtml.indexOf('/api/providers') >= 0 &&
      uiHtml.indexOf('data-branch-msg') >= 0 && uiHtml.indexOf('branchFromMessage') >= 0 &&
      uiHtml.indexOf('模型在顶栏切换') < 0 && uiHtml.indexOf('模型胶囊已上移到输入框顶部') >= 0, "");
    ok("external MCP tools have a discoverable settings entry",
      uiHtml.indexOf('id="mcpList"') >= 0 && uiHtml.indexOf('id="mcpDiscoverBtn"') >= 0 &&
      uiHtml.indexOf('id="mcpAddBtn"') >= 0 && uiHtml.indexOf('/api/mcp/discover') >= 0 &&
      uiHtml.indexOf('renderMcpServers') >= 0, "");
    ok("composer + menu groups real attachment actions",
      uiHtml.indexOf('id="addBtn"') >= 0 && uiHtml.indexOf('id="addPop"') >= 0 &&
      uiHtml.indexOf('id="addRefBtn"') >= 0 && uiHtml.indexOf('insertAtMention') >= 0 &&
      uiHtml.indexOf('id="attachBtn"') > uiHtml.indexOf('id="addPop"'), "");
    ok("workdir picker has one top-level shortcut", uiHtml.indexOf('id="workdirChip"') >= 0 && uiHtml.indexOf('id="folderBtn"') < 0 && uiHtml.indexOf('id="setWorkDir"') < 0, "");
    ok("topbar is minimal: secondary entries live in one overflow menu",
      uiHtml.indexOf('id="moreBtn"') >= 0 && uiHtml.indexOf('id="topMenu"') >= 0 &&
      uiHtml.indexOf('class="top-sep"') < 0 &&
      uiHtml.indexOf('id="topMenu"') < uiHtml.indexOf('id="themeBtn"') &&
      uiHtml.indexOf('id="topMenu"') < uiHtml.indexOf('id="paletteBtn"') &&
      uiHtml.indexOf('id="setTheme"') < 0, "");
    ok("model selector is at the top of the composer, not the topbar",
      uiHtml.indexOf('id="modelBtn"') >= 0 &&
      uiHtml.indexOf('class="connection-group"') < 0 &&               // 旧顶栏连接组已移除
      uiHtml.indexOf('class="model-wrap"') < uiHtml.indexOf('class="composer-bar"') &&  // 模型按钮在输入框顶部容器内
      uiHtml.indexOf('id="brainStatusBtn"') < 0 &&
      uiHtml.indexOf('id="modelBtn" title="切换模型">模型</button>') < 0, "");

    // 作曲栏控件 + 设置分组：推理是水平滑块（轨道 + 拖动手柄），不是竖条
    ok("composer has a horizontal effort slider and dictation",
      /id="effortRange" role="slider" tabindex="0"/.test(uiHtml) &&
      uiHtml.indexOf('class="effort-slider"') >= 0 &&
      uiHtml.indexOf('class="effort-track"') >= 0 &&
      uiHtml.indexOf('id="effortThumb"') >= 0 &&
      uiHtml.indexOf('id="modelBtn"') >= 0 && uiHtml.indexOf('id="effortBtn"') < 0 && uiHtml.indexOf('id="dictateBtn"') >= 0 &&
      uiHtml.indexOf('key:"xhigh",label:"极致"') >= 0, "");
    // 主页空态：一句提问 + 一张园景小卡；旧的昵称问候与示例 chip 仍然不再出现
    ok("home keeps the single question line and the garden card",
      uiHtml.indexOf('class="home-hero-title"') >= 0 &&
      uiHtml.indexOf("今天想做点什么") >= 0 &&
      uiHtml.indexOf('id="gardenView"') >= 0 && uiHtml.indexOf("/garden.js") >= 0 &&
      uiHtml.indexOf('class="home-title"') < 0 &&
      uiHtml.indexOf("chips-row") < 0 && uiHtml.indexOf('class="chip"') < 0, "");
    ok("home dropped logo, kicker and small print",
      uiHtml.indexOf("GREEN TIGER · PERSONAL AGENT") < 0 &&
      uiHtml.indexOf("home-kicker") < 0 && uiHtml.indexOf("empty-mark") < 0 &&
      uiHtml.indexOf("empty-sub") < 0 && uiHtml.indexOf("说说你想完成的事") < 0 &&
      uiHtml.indexOf("给思绪留一片柔软的地方") < 0, "");
    // 空态以前是 flex:1，矮窗口里被压缩后居中内容顶部被裁、底部状态行被输入框盖住且滚不到
    ok("home empty state cannot be squeezed below its content",
      /\.empty\{[^}]*flex:1 0 auto/.test(uiHtml), "");
    ok("motion and sound controls moved into settings, off the home page",
      uiHtml.indexOf('toggleRowHtml("setUiMotion"') >= 0 &&
      uiHtml.indexOf('toggleRowHtml("setUiSounds"') >= 0 &&
      uiHtml.indexOf('key:"uiMotion"') >= 0 && uiHtml.indexOf('key:"uiSounds"') >= 0 &&
      uiHtml.indexOf('class="home-prefs"') < 0 && uiHtml.indexOf("function homePrefsHtml") < 0 &&
      uiHtml.indexOf("function toggleHomePref") >= 0 &&
      uiHtml.indexOf("function syncHomePrefs") >= 0, "");
    ok("model switch keeps the top bar indicator in sync",
      uiHtml.indexOf("window.renderComposerSelectors = renderComposerSelectors") >= 0 &&
      uiHtml.indexOf("if (window.renderComposerSelectors) window.renderComposerSelectors();") >= 0 &&
      uiHtml.indexOf("if (next && next.version) applyState(next);") >= 0 &&
      uiHtml.indexOf('id="modelBtn"') >= 0, "");
    // 裸 JSON 的两处渲染修复：整段按代码块渲染 + 下划线强调不越过词边界
    ok("bare json renders as code and keeps underscores",
      uiHtml.indexOf("function jsonDepth(") >= 0 &&
      uiHtml.indexOf("jsonLineStart") >= 0 &&
      uiHtml.indexOf("jsonDepth(probe) === 0") >= 0 &&
      uiHtml.indexOf("下划线强调必须落在词边界上") >= 0, "");
    ok("home status line follows model state changes",
      uiHtml.indexOf("function homeStatusLine()") >= 0 && uiHtml.indexOf('id="homeStatus"') >= 0 &&
      uiHtml.indexOf("homeStatus.textContent = homeStatusLine()") >= 0, "");
    ok("settings form persists both interface preferences",
      uiHtml.indexOf("uiMotion:motionBox ? motionBox.checked") >= 0 &&
      uiHtml.indexOf("uiSounds:soundsBox ? soundsBox.checked") >= 0 &&
      uiHtml.indexOf('api("/api/config", patch)') >= 0 &&
      !/class="home-prefs" role="group"/.test(uiHtml), "");
    ok("button feedback respects reduced motion", uiHtml.indexOf("button:active:not(:disabled)") >= 0 &&
      uiHtml.indexOf("html.motion-reduced *") >= 0 && uiHtml.indexOf("prefers-reduced-motion:reduce") >= 0, "");
    const catCount = (uiHtml.match(/<section class="settings-section" data-cat="/g) || []).length;
    ok("settings sections follow seven main categories", catCount === 7, "data-cat 段数=" + catCount);
    ok("permission mode is not duplicated in settings", uiHtml.indexOf('id="setModeSeg"') < 0 && uiHtml.indexOf('id="modeSelect"') >= 0, "mode selector in composer only");

    // 本轮修复的回归护栏：这些曾是真死链 / 真 bug，断言防止回退
    ok("workdir picker is wired to the backend",
      uiHtml.indexOf('/api/pick-folder') >= 0 && uiHtml.indexOf("/api/pick-files") >= 0 && uiHtml.indexOf("GreenTigerWorkspace") < 0, "");
    ok("slash commands call defined functions",
      uiHtml.indexOf('if (c === "/retry"){ retryMsg(); return true; }') >= 0 && uiHtml.indexOf("retryLast") < 0 &&
      uiHtml.indexOf('if (c === "/memory")') >= 0, "");
    ok("explorer requests send path at top level",
      uiHtml.indexOf('{ method:"POST", body:{ path:') < 0 && uiHtml.indexOf('api("/api/explorer",{path:') >= 0, "");
    ok("state refresh recovers instead of going fatal",
      uiHtml.indexOf("function setOffline") >= 0 && uiHtml.indexOf("if (!stateBooted){ showFatal(e.message); return; }") >= 0, "");
    ok("composer anchors slash and at popovers", /\.composer\{[^}]*position:relative/.test(uiHtml), "");
    ok("stop is not fired twice on Escape",
      uiHtml.indexOf("Esc 中断任务只由 document 级快捷键统一处理") >= 0 && uiHtml.indexOf("if (stopping) return;") >= 0, "");
    ok("switches expose accessible names",
      uiHtml.indexOf('aria-label="\'+esc(title)+\'"><span></span></label>') >= 0, "");
    ok("toast region is announced", /id="toasts" role="status" aria-live="polite"/.test(uiHtml) &&
      uiHtml.indexOf("white-space:pre-line") >= 0, "");
    ok("approval outranks toasts in stacking order",
      /\.toasts\{[^}]*z-index:90/.test(uiHtml) && /\.approval-backdrop\{[^}]*z-index:110/.test(uiHtml), "");
    ok("ask_user option dialog + wb sessions tab wired",
      uiHtml.indexOf("data-ask-opt") >= 0 && uiHtml.indexOf('data-wb-tab="sessions"') >= 0, "");
    const themeCss = fs.readFileSync(path.join(root, "ui", "theme.css"), "utf8");
    const shellJsText = fs.readFileSync(path.join(root, "ui", "shell.js"), "utf8");
    /* P0 视觉重构护栏：炭灰底 / 微色差 / 留白与排版 / 系统主题色同步 */
    /* 3.31 视觉方向：夜林墨黑（不是旧版炭灰 #212121），分隔线低对比，圆角继续收敛 */
    ok("dark theme uses night-forest ink with quiet dividers",
      themeCss.indexOf("--bg:#0b0d09") >= 0 &&
      themeCss.indexOf("--bg-side:#070806") >= 0 &&
      themeCss.indexOf("--bg-elev:#141710") >= 0 &&
      themeCss.indexOf("--bg:#000000") < 0 &&
      themeCss.indexOf("--line:#262c1f") >= 0 &&
      themeCss.indexOf("--text-faint:#818a74") >= 0 &&
      themeCss.indexOf("--r-xl:12px") >= 0, "");
    ok("system theme color follows the charcoal palette",
      uiHtml.indexOf('content="#f7f9f4"') >= 0 &&
      shellJsText.indexOf('next === "dark" ? "#1e1e1e" : "#f7f9f4"') >= 0 &&
      shellJsText.indexOf("#000000") < 0, "");
    ok("typography and spacing breathe without breaking compact windows",
      /body\{[^}]*line-height:1\.65/.test(uiHtml) &&
      /body\{[^}]*letter-spacing:\.01em/.test(uiHtml) &&
      uiHtml.indexOf("padding:34px 26px 60px") >= 0 &&
      uiHtml.indexOf("padding:34px 0 48px") >= 0 &&
      uiHtml.indexOf("margin-bottom:28px") >= 0 &&
      uiHtml.indexOf("border-radius:var(--r-xl)") >= 0 &&
      /@media \(max-height:650px\)\{[\s\S]*?\.msg\{margin-bottom:14px\}/.test(uiHtml), "");
    /* P1 形态护栏：示例命令 / 参数后置 / 极简输入框 / 状态栏数字可读 */
    ok("P1 home chips became slash quick commands",
      uiHtml.indexOf('cmd:"/看目录"') >= 0 && uiHtml.indexOf('cmd:"/截屏"') >= 0 &&
      uiHtml.indexOf('prompt:"看看工作目录里有什么"') >= 0 &&
      uiHtml.indexOf("if (item.prompt)") >= 0 &&
      uiHtml.indexOf('class="chips-row"') < 0, "");
    ok("gear keeps permission mode and dictation; effort lives in the model panel",
      uiHtml.indexOf('id="paramsBtn"') >= 0 && uiHtml.indexOf('id="cmpParams"') >= 0 &&
      uiHtml.indexOf('id="effortHint"') >= 0 && uiHtml.indexOf("setParamsOpen") >= 0 &&
      uiHtml.indexOf('id="modeSelect"') >= 0 && uiHtml.indexOf('id="dictateBtn"') >= 0 &&
      uiHtml.indexOf('id="effortRange"') < uiHtml.indexOf('id="cmpParams"'), "");
    ok("composer is a clean rounded box with no decorative prompt",
      uiHtml.indexOf(".composer::before") < 0 &&
      uiHtml.indexOf('placeholder="描述你要做的事…"') >= 0 &&
      uiHtml.indexOf("border-radius:var(--r-xl)") >= 0, "");
    ok("status bar numerals and separators are legible",
      /\.statusbar\{[^}]*font-size:10\.6px/.test(uiHtml) &&
      uiHtml.indexOf(".sep{opacity:.72}") >= 0 &&
      uiHtml.indexOf("font-variant-numeric:tabular-nums") >= 0, "");
    /* 上一轮主页改版的形态护栏：欢迎卡 / 置灰发送 / ? 提示 / 参数 Badge / 路径去重 */
    ok("neutral tokens exist in both themes with readable dark-mode contrast",
      (themeCss.match(/--neutral-ink:/g) || []).length === 2 &&
      (themeCss.match(/--neutral-rgb:/g) || []).length === 2 &&
      themeCss.indexOf("--neutral-ink:#e8ecdf") >= 0 &&
      themeCss.indexOf("--neutral-rgb:232,236,223") >= 0, "");
    ok("home setup card removed; model config stays in the composer panel only",
      uiHtml.indexOf('class="welcome-card"') < 0 &&
      uiHtml.indexOf("请先为我配置一个大脑") < 0 &&
      uiHtml.indexOf('data-model-setup') < 0 &&
      uiHtml.indexOf('key:"settings"') >= 0 &&
      uiHtml.indexOf("🐯") < 0 &&
      uiHtml.indexOf('class="model-setup"') < 0 && uiHtml.indexOf("var(--surface-2)") < 0, "");
    ok("accent green now only marks logo, active send button and tiny status dots",
      uiHtml.indexOf(".glyph-swirl{display:block;background:var(--accent)") >= 0 &&
      uiHtml.indexOf("background:var(--accent);color:var(--accent-ink);border:1px solid transparent") >= 0 &&
      uiHtml.indexOf(".send-btn:not(:disabled):hover{background:var(--accent-strong)") >= 0 &&
      uiHtml.indexOf("border-top-color:var(--accent)") >= 0 &&
      uiHtml.indexOf(".side-link.active{background:var(--neutral-fill-strong)") >= 0 &&
      uiHtml.indexOf(".btn.primary{background:var(--text)") >= 0 &&
      uiHtml.indexOf("width:3px;height:16px;border-radius:3px;background:var(--neutral-ink)") >= 0, "");
    ok("send button greys out when the composer is empty",
      /\.send-btn:disabled\{[^}]*var\(--neutral-fill-strong\)/.test(uiHtml) &&
      uiHtml.indexOf(".send-btn:not(:disabled):hover") >= 0 &&
      uiHtml.indexOf("input.value.trim()") >= 0, "");
    ok("dense shortcut hint collapsed into one help button with tooltips",
      uiHtml.indexOf('id="helpBtn"') >= 0 && uiHtml.indexOf('id="gtTip"') >= 0 &&
      uiHtml.indexOf("function initTooltips()") >= 0 &&
      uiHtml.indexOf("Enter 发送 · Shift+Enter 换行 · / 命令 · ! 直执") < 0 &&
      (uiHtml.match(/data-tip=/g) || []).length >= 4, "");
    ok("parameter controls share one badge component",
      (uiHtml.match(/class="cmp-badge/g) || []).length >= 2 &&
      uiHtml.indexOf('class="cmp-badge mode-select"') >= 0 &&
      uiHtml.indexOf('class="cmp-badge icon-only"') >= 0 &&
      uiHtml.indexOf('id="effortThumb"') >= 0 &&
      /id="effortRange" role="slider" tabindex="0"/.test(uiHtml), "");
    ok("workdir path appears once, in the status bar",
      uiHtml.indexOf('id="statusWorkdir"') >= 0 &&
      uiHtml.indexOf(".workdir-chip #workdirLabel{display:none}") >= 0 &&
      uiHtml.indexOf("shortPath(wd,30)") < 0 && uiHtml.indexOf("shortPath(wd,80)") >= 0, "");
    ok("settings expose budget controls",
      uiHtml.indexOf('id="setTaskTimeout"') >= 0 && uiHtml.indexOf('id="setMaxTokens"') >= 0 &&
      uiHtml.indexOf('id="setMaxRetries"') >= 0 && uiHtml.indexOf('id="setMaxRepairs"') >= 0 &&
      uiHtml.indexOf("taskTimeoutSeconds:clampInt") >= 0, "");
    ok("no id shadows a built-in global", uiHtml.indexOf('id="setTimeout"') < 0, "");
    ok("file changes can be rolled back from the tool card",
      uiHtml.indexOf("data-rollback-file") >= 0 && uiHtml.indexOf("/api/backups/restore") >= 0 &&
      uiHtml.indexOf("askConfirm(\"回滚这次改动？\"") >= 0, "");

    /* —— 终端语汇 ——
     * 这些断言锁住的是「形态」而不是像素：工具是行不是卡、结果有 ⎿ 摘要、
     * 代码块有语法高亮、生成中有秒数、状态栏有上下文占比。 */
    ok("tool calls stay terminal rows with a light card treatment",
      uiHtml.indexOf('class="tool-mark"') >= 0 && uiHtml.indexOf('aria-hidden="true">\\u23fa') >= 0 &&
      uiHtml.indexOf('class="tool-name"') >= 0 && uiHtml.indexOf('class="tool-args"') >= 0 &&
      uiHtml.indexOf("function toolOutPreview(") >= 0 &&
      /\.tool\{[^}]*border:1px solid var\(--line-soft\)/.test(uiHtml), "");
    ok("tool rows show running spinner, done check and failure icons",
      uiHtml.indexOf('class="tool-state spin"') >= 0 &&
      uiHtml.indexOf('class="tool-state done"') >= 0 &&
      uiHtml.indexOf('class="tool-state bad"') >= 0 &&
      uiHtml.indexOf('icon(meta.icon,"tool-ico")') >= 0 &&
      uiHtml.indexOf("gtSpin .8s linear infinite") >= 0, "");
    ok("streaming replies use SSE with incremental reconciliation",
      uiHtml.indexOf('new EventSource("/api/events")') >= 0 &&
      uiHtml.indexOf("function reconcileMessages(") >= 0 &&
      uiHtml.indexOf("msgRenderKey") >= 0 && uiHtml.indexOf(".msg.streaming") >= 0, "");
    ok("collapsed tool row shows a result preview line",
      uiHtml.indexOf("function toolOutPreview(") >= 0 && uiHtml.indexOf('class="tool-out"') >= 0 &&
      uiHtml.indexOf("out-more") >= 0, "");
    ok("tool summary unwraps nested args instead of dumping json",
      uiHtml.indexOf("o.args && typeof o.args === \"object\"") >= 0 &&
      uiHtml.indexOf("parts.push(k + \"=\"") >= 0, "");
    ok("code blocks are syntax highlighted without a dependency",
      uiHtml.indexOf("function highlightCode(") >= 0 && uiHtml.indexOf("function hlConf(") >= 0 &&
      uiHtml.indexOf("highlightCode(body,lang)") >= 0 && /\.hl-k\{color:var\(--hl-key\)\}/.test(uiHtml), "");
    ok("highlight colors come from theme tokens in both themes",
      /--hl-key:/.test(themeCss) && (themeCss.match(/--hl-key:/g) || []).length >= 2, "");
    ok("diff view has gutters and line numbers",
      uiHtml.indexOf('class="dl-no"') >= 0 && uiHtml.indexOf("diff-ctx") >= 0, "");
    ok("streaming shows one elapsed timer and no duplicate working line",
      uiHtml.indexOf('data-elapsed') >= 0 && uiHtml.indexOf('class="msg-working"') < 0 &&
      (uiHtml.match(/<em data-elapsed>/g) || []).length === 1 &&
      uiHtml.indexOf('querySelectorAll("[data-elapsed]")') >= 0, "");
    ok("status bar shows context usage percentage",
      uiHtml.indexOf('+ " · " + pct + "%"') >= 0, "");
    ok("user messages use a right-aligned bubble, not a transcript prompt",
      /\.msg\.user\{justify-content:flex-end/.test(uiHtml) &&
      /\.user-bubble\{\s*position:relative;border:1px solid var\(--line-soft\);border-radius:18px/.test(uiHtml) &&
      !/\.user-bubble\{\s*position:relative;border:0;border-radius:0/.test(uiHtml), "");
    ok("dependency self-check script exists and is wired to npm",
      fs.existsSync(path.join(root, "tools", "check-deps.js")) &&
      JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8")).scripts["check:deps"] === "node tools/check-deps.js", "");
    // 对话区必须是增量渲染：全量 innerHTML 会在长会话里反复重算语法高亮与差异。
    ok("chat renders incrementally instead of rebuilding the whole transcript",
      uiHtml.indexOf("function reconcileMessages(") >= 0 &&
      uiHtml.indexOf("data-mkey") >= 0 &&
      uiHtml.indexOf("reconcileMessages(chat, a.messages.map(") >= 0 &&
      uiHtml.indexOf("chat.innerHTML = a.messages.map(") < 0, "");
    // 三处补全共用同一套模糊匹配，并且都要能让读屏软件播报当前高亮项。
    ok("palette, slash, and file mention share one fuzzy matcher",
      uiHtml.indexOf("function fuzzyScore(") >= 0 &&
      uiHtml.indexOf("function fuzzyRank(") >= 0 &&
      (uiHtml.match(/fuzzyRank\(/g) || []).length >= 3 &&
      (uiHtml.match(/aria-activedescendant/g) || []).length >= 3, "");
    // 风险徽章以前没有任何样式，等于危险等级在界面上没有视觉提示。
    ok("risk badges have real styles",
      /\.badge\{[^}]*border-radius:var\(--r-xs\)/.test(uiHtml) && /\.badge\.err\{/.test(uiHtml), "");
    // 死文件清理的护栏：shell.css 无人引用，不该再被任何地方加载或打包。
    ok("dead shell stylesheet is gone from ui and the build",
      !fs.existsSync(path.join(root, "ui", "shell.css")) &&
      fs.readFileSync(path.join(root, "build.ps1"), "utf8").indexOf("shell.css\",") < 0, "");
    ok("shortcut cheat sheet is reachable from the keyboard",
      uiHtml.indexOf("function openShortcuts(") >= 0 && uiHtml.indexOf('key === "/"') >= 0 &&
      uiHtml.indexOf('id:"shortcuts"') >= 0, "");

    const dark = themeCss.slice(themeCss.indexOf('html[data-theme="dark"]'));
    const cssToken = (n) => { const m = dark.match(new RegExp("--" + n + ":\\s*([^;]+);")); return m ? m[1].trim() : ""; };
    /* 绿黑白约束下，错误用近白墨色、成功用绿色：两者必须仍可区分；警告与成功共用绿色是有意选择。 */
    ok("dark theme keeps error distinguishable from success", 
      cssToken("red") && cssToken("green") && cssToken("red") !== cssToken("green"),
      "red=" + cssToken("red") + " green=" + cssToken("green") + " amber=" + cssToken("amber"));
  } catch (e) {
    ok("smoke run", false, e.message);
  } finally {
    child.kill();
    await sleep(500);
    try { child.kill("SIGKILL"); } catch (e) {}
    try { fs.rmSync(dataDir, { recursive: true, force: true }); } catch (e) {}
    console.log(fails.length ? fails.length + " CHECK(S) FAILED" : "ALL SMOKE CHECKS PASSED");
    process.exitCode = fails.length ? 1 : 0;
  }
})();
