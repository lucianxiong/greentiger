/*
 * GreenTiger 界面截图工具（开发用，不做断言）
 *
 * 用法：node tools\ui-shot.js
 *
 * 以临时数据目录启动 dist\GreenTiger.exe，先写好一份「真实形状」的会话
 * （用户消息 + 助手解释 + 已完成的工具行 + 差异 + 运行中的工具），
 * 再用 Edge 打开并截图到 %TEMP%\GreenTiger\shot-*.png。
 *
 * 为什么要写会话文件而不是在页面里调渲染函数：整段前端脚本被包在一个 IIFE 里，
 * 外部拿不到 messageHtml；从磁盘喂数据反而更接近真实加载路径。
 */
"use strict";

const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const EXE = path.join(ROOT, "dist", "GreenTiger.exe");
const DATA_DIR = path.join(os.tmpdir(), "GreenTiger", "shot-" + Date.now());
const PORT = 42100 + Math.floor(Math.random() * 300);
const SHOT_DIR = path.join(os.tmpdir(), "GreenTiger");

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitFor(fn, timeoutMs, label) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const v = await fn();
    if (v) return v;
    await sleep(150);
  }
  throw new Error("超时等待：" + label);
}

const now = Date.now();
const SESSION = {
  Id: "s_shot_demo",
  Title: "修告警缓存",
  CreatedAt: now - 120000,
  UpdatedAt: now,
  WorkDir: "",
  ToolCount: 4,
  TurnCount: 2,
  ContextChars: 12400,
  Messages: [
    {
      Id: "m1", Role: "user", Time: now - 96000, Status: "done", Brain: "",
      Text: "把告警缓存和任务统计缓存拆开，现在告警从来没生效过。改完跑一遍烟测。",
      ToolCalls: []
    },
    {
      Id: "m2", Role: "assistant", Time: now - 94000, Status: "done", Brain: "provider",
      Text: "找到了：`Alerts()` 和 `TaskStatsDto()` 共用同一个 `_taskStatsCachedAt`，而广播顺序是先统计后告警，所以告警的刷新判断恒为 false。我把时间戳拆成两份：",
      ToolCalls: [
        {
          Id: "t1", Name: "read_file", Status: "done", Risk: "safe", DurationMs: 104,
          ArgsJson: JSON.stringify({ args: { path: "src/App.cs" } }),
          Output: "585  private TaskStatsSummary _taskStatsCache;\n586  private long _taskStatsCachedAt;\n587  private List<AlertItem> _alertsCache;\n588  private string _lastAlertFingerprint = \"<none>\";",
          Artifacts: []
        },
        {
          Id: "t2", Name: "edit", Status: "done", Risk: "normal", DurationMs: 61,
          BackupPath: "C:/Users/me/AppData/GreenTiger/backups/App.cs.20261030.bak",
          ArgsJson: JSON.stringify({ args: { path: "src/App.cs" } }),
          Diff: "@@ -590,4 +590,9 @@\n-            if (_taskStatsCache == null || now - _taskStatsCachedAt > 10000)\n+            if (_alertsCache == null || now - _alertCachedAt > 10000)\n+            {\n+                _alertsCache = AlertEngine.Evaluate(TaskStore.Load(AlertEngine.Window(Config)), Config);\n+                _alertCachedAt = now;\n+            }",
          Output: "已写入 src/App.cs（+5 −1）",
          Artifacts: []
        },
        {
          Id: "t3", Name: "shell", Status: "done", Risk: "normal", DurationMs: 4210,
          ArgsJson: JSON.stringify({ command: "node tools/smoke.js", timeout: 120 }),
          Command: "node tools/smoke.js",
          Output: "PASS health\nPASS api rejects missing token\nPASS state\nPASS config replyTimeout\nALL SMOKE CHECKS PASSED",
          Artifacts: []
        }
      ]
    },
    {
      Id: "m3", Role: "assistant", Time: now - 30000, Status: "done", Brain: "provider",
      Text: "烟测全绿。接下来我把设置面板里的预算字段也一并接上：\n\n```ts\nexport async function saveBudget(patch: BudgetPatch) {\n  const next = await api(\"/api/config\", patch);\n  if (next.version) applyState(next);\n  return next.config.maxTaskTokens > 0;\n}\n```\n\n| 字段 | 默认值 | 说明 |\n| --- | --- | --- |\n| maxTaskTokens | 0 | 0 表示不限 |\n| maxRepairs | 2 | 验收失败后的修复轮次 |",
      ToolCalls: [
        {
          Id: "t4", Name: "grep", Status: "running", Risk: "safe", DurationMs: 0,
          ArgsJson: JSON.stringify({ args: { pattern: "_alertCachedAt" } }),
          Output: "", Artifacts: []
        }
      ]
    },
    {
      // 本地小模型把工具调用写成裸 JSON 的真实形态：用来校验「识别成调用」与「整段按代码块渲染、
      // 下划线不被当成强调吃掉」这两件事的观感
      Id: "m4", Role: "assistant", Time: now - 12000, Status: "done", Brain: "provider",
      Text: "本地模型有时会把调用直接写成裸 JSON，绿虎现在也认得：\n\n{\"name\": \"mcp__context7__query-docs\", \"arguments\": {\"query\": \"测试\", \"language\": \"python\"}}",
      ToolCalls: []
    }
  ]
};

(async () => {
  if (!fs.existsSync(EXE)) throw new Error("找不到 " + EXE + "，请先运行 build.ps1");
  const workDir = path.join(DATA_DIR, "work");
  fs.mkdirSync(path.join(DATA_DIR, "sessions"), { recursive: true });
  fs.mkdirSync(workDir, { recursive: true });
  fs.mkdirSync(SHOT_DIR, { recursive: true });
  fs.writeFileSync(path.join(DATA_DIR, "config.json"),
    JSON.stringify({ workDir, autoRun: false, theme: "dark", brain: "auto" }, null, 2), "utf8");
  SESSION.WorkDir = workDir;
  fs.writeFileSync(path.join(DATA_DIR, "sessions", SESSION.Id + ".json"),
    JSON.stringify(SESSION, null, 2), "utf8");

  const { chromium } = require("playwright-core");
  const child = spawn(EXE, ["--no-browser", "--port=" + PORT, "--data-dir=" + DATA_DIR],
    { stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
  let browser = null;
  try {
    const urlFile = path.join(DATA_DIR, "ui.url");
    const url = await waitFor(() => {
      if (!fs.existsSync(urlFile)) return null;
      const u = fs.readFileSync(urlFile, "utf8").trim();
      return u && u.startsWith("http") ? u : null;
    }, 20000, "ui.url");

    browser = await chromium.launch({ channel: "msedge", headless: true });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
    page.on("pageerror", (e) => console.error("pageerror:", String(e)));
    await page.goto(url, { waitUntil: "domcontentloaded" });
    await page.waitForSelector("#chat", { timeout: 15000 });
    await sleep(1200);

    const shots = [
      ["shot-home.png", null],
      ["shot-dark.png", "dark"],
      ["shot-light.png", "light"]
    ];
    // 先截空态主页（新建一个空会话，不动 demo 会话）
    await page.click("#newSessionBtn");
    await sleep(900);
    await page.screenshot({ path: path.join(SHOT_DIR, "shot-home.png") });
    console.log("shot-home.png");
    // 浅色主页：园景小卡在浅色地面上的观感
    await page.evaluate(() => { document.documentElement.dataset.theme = "light"; });
    await sleep(400);
    await page.screenshot({ path: path.join(SHOT_DIR, "shot-home-light.png") });
    console.log("shot-home-light.png");
    await page.evaluate(() => { document.documentElement.dataset.theme = "dark"; });
    await sleep(300);
    // 全屏花园：点主页园景卡片进入
    await page.click(".home-garden");
    await sleep(1400);
    await page.screenshot({ path: path.join(SHOT_DIR, "shot-garden.png") });
    console.log("shot-garden.png");
    await page.click("#gardenCloseBtn");
    await sleep(500);
    // 批量选择态：侧栏变成复选框列表 + 批量操作栏
    await page.click("#selectToggle");
    await sleep(320);
    await page.screenshot({ path: path.join(SHOT_DIR, "shot-bulk.png") });
    console.log("shot-bulk.png");
    await page.click("#bulkCancelBtn");
    await sleep(260);
    await page.click('[data-session="s_shot_demo"]');
    await sleep(900);
    for (const [name, theme] of shots.slice(1)) {
      await page.evaluate((t) => { document.documentElement.dataset.theme = t; }, theme);
      await sleep(350);
      await page.screenshot({ path: path.join(SHOT_DIR, name) });
      console.log(name);
    }
    // 展开一个工具行，校对展开态
    await page.evaluate(() => { document.documentElement.dataset.theme = "dark"; });
    await sleep(200);
    await page.click('[data-tool-toggle="t2"]');
    await sleep(350);
    await page.screenshot({ path: path.join(SHOT_DIR, "shot-expanded.png") });
    console.log("shot-expanded.png");

    // 工作台（右侧面板）：产物 / 任务 / 日志
    await page.click("#workbenchBtn");
    await sleep(500);
    await page.screenshot({ path: path.join(SHOT_DIR, "shot-workbench.png") });
    console.log("shot-workbench.png");
    await page.click('#wbTabs [data-wb-tab="tasks"]');
    await sleep(300);
    await page.screenshot({ path: path.join(SHOT_DIR, "shot-workbench-tasks.png") });
    console.log("shot-workbench-tasks.png");
    await page.click("#inspectorClose");
    await sleep(250);

    // 顶部：看用户消息与首条助手消息的排版
    await page.evaluate(() => { const el = document.getElementById("chatScroll"); if (el) el.scrollTop = 0; });
    await sleep(300);
    await page.screenshot({ path: path.join(SHOT_DIR, "shot-top.png") });
    console.log("shot-top.png");

    // 工具行局部放大：校对 ⏺ / ⎿ 的细节
    const tools = page.locator(".tools").first();
    if (await tools.count()) {
      await tools.screenshot({ path: path.join(SHOT_DIR, "shot-tools.png") });
      console.log("shot-tools.png");
    }

    // 模型切换面板：确认它是贴着顶栏按钮落下来的，而不是飞到屏幕另一角
    await page.setViewportSize({ width: 1440, height: 900 });
    await sleep(250);
    await page.evaluate(() => { document.documentElement.dataset.theme = "dark"; });
    await page.click("#modelBtn");
    await sleep(320);
    await page.screenshot({ path: path.join(SHOT_DIR, "shot-model-pop.png") });
    console.log("shot-model-pop.png");
    await page.keyboard.press("Escape");
    await sleep(200);

    // 窄屏：侧栏自动收成抽屉 + 顶栏收窄，最容易在改版后错位
    await page.setViewportSize({ width: 820, height: 900 });
    await sleep(500);
    await page.screenshot({ path: path.join(SHOT_DIR, "shot-narrow.png") });
    console.log("shot-narrow.png");
    await page.click("#menuBtn");
    await sleep(400);
    await page.screenshot({ path: path.join(SHOT_DIR, "shot-narrow-drawer.png") });
    console.log("shot-narrow-drawer.png");
  } finally {
    try { if (browser) await browser.close(); } catch (e) {}
    try { child.kill(); } catch (e) {}
  }
})().catch((e) => { console.error(e.message); process.exit(1); });
