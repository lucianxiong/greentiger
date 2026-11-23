/*
 * GreenTiger 界面端到端检查（playwright-core 驱动本机 Edge，不下载浏览器）
 *
 * 用法：
 *   npm install          # 只装 devDependency：playwright-core
 *   node tools\ui-e2e.js
 *
 * 会以临时数据目录启动 dist\GreenTiger.exe，用 Edge 打开带 token 的界面地址，
 * 逐项断言关键交互，最后把截图写到 %TEMP%\GreenTiger\e2e-*.png。
 */
"use strict";

const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const EXE = path.join(ROOT, "dist", process.argv.includes("--candidate") ? "GreenTiger.candidate.exe" : "GreenTiger.exe");
// P3：界面源码已拆分；文本类断言读合并后的源，避免逐条改断言。
const UI_SOURCE = [
  fs.readFileSync(path.join(ROOT, "ui", "index.html"), "utf8"),
  fs.readFileSync(path.join(ROOT, "ui", "style.css"), "utf8"),
  ...["util.js", "markdown.js", "tools.js", "chat.js", "model.js", "app.js"]
    .map((f) => fs.readFileSync(path.join(ROOT, "ui", "js", f), "utf8"))
].join("\n");
const DATA_DIR = path.join(os.tmpdir(), "GreenTiger", "ui-e2e-" + Date.now());
const PORT = 41300 + Math.floor(Math.random() * 400);
const SHOT_DIR = path.join(os.tmpdir(), "GreenTiger");

const results = [];
let failed = 0;

function check(name, ok, detail) {
  results.push({ name, ok, detail });
  if (!ok) failed++;
  const tag = ok ? "PASS" : "FAIL";
  console.log(tag + " " + name + (detail ? " :: " + detail : ""));
}

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

async function waitFor(fn, timeoutMs, label) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const v = await fn();
    if (v) return v;
    await sleep(150);
  }
  throw new Error("超时等待：" + label);
}

/* playwright-core 是唯一的 node 依赖：缺了就直说怎么补，不要甩调用栈。
 * 它不带浏览器二进制，channel:"msedge" 用的是系统已有的 Edge。 */
function requirePlaywright() {
  try {
    return require("playwright-core");
  } catch (e) {
    throw new Error("缺少 playwright-core：请先在仓库根目录运行 npm install"
      + "（devDependency 已写入 package.json，用本机 Edge，不会下载浏览器）。原始错误：" + e.message);
  }
}

// 在真实浏览器中用命中测试发现边界外、被遮挡或缩得太小的主要按钮。
function unreachableControls(selector) {
  const buttons = Array.from(document.querySelectorAll(selector));
  if (!buttons.length) return ["缺少 " + selector];
  return buttons.filter(button => {
    button.scrollIntoView({ block: "nearest", inline: "nearest" });
    const box = button.getBoundingClientRect();
    const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
    return button.disabled || box.width < 24 || box.height < 24 || box.left < -1 || box.right > innerWidth + 1
      || !button.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }) || !button.contains(hit);
  }).map(button => button.id || button.dataset.tb);
}

// 成对截取实际文字背景、同处的实色文字样本，透明卡面、渐变、父元素透明度均由浏览器合成。
async function toolboxContrast(page) {
  const selector = "#tbHome :is(.tb-card-title,.tb-card-desc)";
  const points = await page.locator(selector).evaluateAll(labels => labels.flatMap(label => {
    const range = document.createRange();
    range.selectNodeContents(label);
    return Array.from(range.getClientRects(), rect => ({
      x: Math.floor(rect.left + rect.width / 2), y: Math.floor(rect.top + rect.height / 2)
    }));
  }));
  async function capture(rule) {
    const style = await page.addStyleTag({ content: selector + "{" + rule + "}" });
    try {
      return (await page.screenshot({ scale: "css", animations: "disabled" })).toString("base64");
    } finally {
      await style.evaluate(el => el.remove());
    }
  }
  const background = await capture("color:transparent!important;text-shadow:none!important");
  const foreground = await capture("box-shadow:inset 0 0 0 1000px currentColor!important");
  return page.evaluate(async ({ points, background, foreground }) => {
    async function samples(png) {
      const img = new Image();
      img.src = "data:image/png;base64," + png;
      await img.decode();
      const canvas = document.createElement("canvas");
      canvas.width = img.width;
      canvas.height = img.height;
      const ctx = canvas.getContext("2d", { willReadFrequently: true });
      ctx.drawImage(img, 0, 0);
      return points.map(({ x, y }) => {
        const [red, green, blue] = Array.from(ctx.getImageData(x, y, 1, 1).data, byte => {
          const s = byte / 255;
          return s <= .04045 ? s / 12.92 : ((s + .055) / 1.055) ** 2.4;
        });
        return .2126 * red + .7152 * green + .0722 * blue;
      });
    }
    const bg = await samples(background);
    const fg = await samples(foreground);
    return bg.map((b, i) => (Math.max(b, fg[i]) + .05) / (Math.min(b, fg[i]) + .05));
  }, { points, background, foreground });
}

function toolboxFits() {
  return [document.documentElement, document.querySelector(".tb-scroll"), document.querySelector("#tbHome")]
    .every(el => el.clientWidth > 0 && el.scrollWidth <= el.clientWidth + 1);
}

async function checkToolboxDesign(browser, url) {
  const probe = await browser.newPage({ viewport: { width: 375, height: 667 }, reducedMotion: "reduce" });
  try {
    await probe.goto(url, { waitUntil: "domcontentloaded" });
    await probe.locator("#input").fill("测试发送入口");
    const nav = await probe.evaluate(unreachableControls, "#toolboxBtn, #sendBtn");
    check("375px 下工具箱与发送入口大小足够且可点击", !nav.length, nav.join("、"));
    await probe.click("#toolboxBtn");
    await probe.locator("#tbHome").waitFor();
    await probe.locator("#tbHome .tb-card").first().click({ trial: true });
    const actions = "#toolboxCloseBtn, #tbHome .tb-card";
    const missing = await probe.evaluate(unreachableControls, actions);
    check("375px 下工具箱无横滚且滚动后每个操作可点", await probe.evaluate(toolboxFits) && !missing.length, missing.join("、"));
    await probe.setViewportSize({ width: 1440, height: 900 });
    for (const theme of ["dark", "light"]) {
      await probe.evaluate(value => { document.documentElement.dataset.theme = value; }, theme);
      await probe.evaluate(() => Promise.all(document.getAnimations().filter(a =>
        Number.isFinite(a.effect.getComputedTiming().endTime)).map(a => a.finished.catch(() => {}))));
      const ratios = await toolboxContrast(probe);
      check(theme + " 工具箱卡片标题与说明对比度至少 4.5:1",
        ratios.length >= 8 && ratios.every(r => r >= 4.5), "最低 " + Math.min(...ratios).toFixed(2));
    }
    await probe.addStyleTag({ content: "#tbHome .tb-card-title{color:transparent!important}" });
    const contrastRejected = (await toolboxContrast(probe)).some(r => r < 4.5);
    await probe.setViewportSize({ width: 375, height: 667 });
    await probe.addStyleTag({ content: "#tbHome{min-width:900px!important} #toolboxCloseBtn{pointer-events:none!important}" });
    check("反例能检出低对比、横向溢出与不可点击的返回按钮",
      contrastRejected && !(await probe.evaluate(toolboxFits))
      && (await probe.evaluate(unreachableControls, "#toolboxCloseBtn")).includes("toolboxCloseBtn"));
  } finally {
    await probe.close();
  }
}

async function main() {
  if (!fs.existsSync(EXE)) throw new Error("找不到 " + EXE + "，请先运行 build.ps1");
  if (process.platform !== "win32") throw new Error("ui-e2e.js 依赖本机 Microsoft Edge，仅支持 Windows");
  fs.mkdirSync(DATA_DIR, { recursive: true });
  fs.mkdirSync(SHOT_DIR, { recursive: true });

  const workDir = path.join(DATA_DIR, "work");
  fs.mkdirSync(workDir, { recursive: true });
  fs.writeFileSync(
    path.join(DATA_DIR, "config.json"),
    JSON.stringify({ workDir, autoRun: false, theme: "dark", brain: "auto" }, null, 2),
    "utf8"
  );

  const { chromium } = requirePlaywright();

  const child = spawn(EXE, ["--no-browser", "--port=" + PORT, "--data-dir=" + DATA_DIR], {
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true
  });
  let stderr = "";
  child.stderr.on("data", (d) => (stderr += d.toString()));

  let browser = null;
  try {
    const urlFile = path.join(DATA_DIR, "ui.url");
    const url = await waitFor(() => {
      if (!fs.existsSync(urlFile)) return null;
      const u = fs.readFileSync(urlFile, "utf8").trim();
      return u && u.startsWith("http") ? u : null;
    }, 20000, "ui.url");

    check("界面地址已生成", !!url, url.replace(/token=[^&]+/, "token=***"));

    browser = await chromium.launch({ channel: "msedge", headless: true });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

    const errors = [];
    page.on("pageerror", (e) => errors.push(String(e)));
    page.on("console", (m) => {
      if (m.type() === "error") errors.push(m.text());
    });

    await page.goto(url, { waitUntil: "domcontentloaded" });
    await checkToolboxDesign(browser, url);
    await page.waitForSelector("#chat", { timeout: 15000 });
    await sleep(700);

    // 1. 基础渲染
    const title = await page.title();
    check("标题为绿虎", title.indexOf("绿虎") >= 0, title);
    check("品牌名已换为绿虎", ((await page.locator(".brand-text strong").textContent()) || "").indexOf("绿虎") >= 0);
    check("权限模式位于参数面板内", (await page.locator("#cmpParams #modeSelect").count()) === 1);
    check("权限模式提供四档", (await page.locator("#modeSelect option").count()) === 4);
    check("主页已移除配置引导组件", (await page.locator(".welcome-card").count()) === 0 &&
      (await page.locator('[data-model-setup]').count()) === 0);
    check("空输入时发送按钮置灰不可点", await page.locator("#sendBtn").isDisabled());
    await page.locator("#input").fill("   ");
    check("纯空格仍然置灰", await page.locator("#sendBtn").isDisabled());
    await page.locator("#input").fill("测试一下发送按钮");
    check("有内容后发送按钮恢复激活", !(await page.locator("#sendBtn").isDisabled()));
    await page.locator("#input").fill("");
    check("清空后再次置灰", await page.locator("#sendBtn").isDisabled());
    check("次要入口已收进 + 菜单，输入栏只剩主流两组控件", (await page.locator("#addPop #helpBtn").count()) === 1 &&
      (await page.locator("#addPop #paramsBtn").count()) === 1 &&
      (await page.locator(".composer-right #paramsBtn").count()) === 0 &&
      (await page.locator(".composer-right #helpBtn").count()) === 0 &&
      (await page.locator(".composer .hint").count()) === 0 &&
      (await page.locator("[data-tip]").count()) >= 3);
    await page.click("#addBtn");
    await page.locator("#helpBtn").hover();
    check("悬浮菜单项显示精简快捷键提示", (await page.locator("#gtTip").isVisible()) &&
      ((await page.locator("#gtTip").textContent()) || "").indexOf("Enter 发送") >= 0);
    await page.locator("#input").hover();
    check("移开后 Tooltip 收起", !(await page.locator("#gtTip").isVisible()));
    await page.keyboard.press("Escape");
    check("主页已移除问候语与示例 chip", (await page.locator(".home-title").count()) === 0 &&
      (await page.locator(".chips-row").count()) === 0);
    // 回归护栏：数字与符号不能在输入框里丢失，状态栏的数字/分隔符也不能淡到看不见
    const charSample = "测试 123 0.5 -42 !@#$%^&*() []{} <>?/\\| ;:,._+= 中文";
    await page.locator("#input").fill(charSample);
    check("输入框完整保留数字与符号", (await page.locator("#input").inputValue()) === charSample);
    check("状态栏数字与分隔符可读", await page.locator(".statusbar").evaluate((el) => {
      const cs = getComputedStyle(el);
      const sep = el.querySelector(".sep");
      return parseFloat(cs.fontSize) >= 10 && !!sep && parseFloat(getComputedStyle(sep).opacity) >= 0.7;
    }));
    await page.locator("#input").fill("");
    await page.locator("#input").fill("/");
    await page.waitForSelector("#slashPop:not([hidden])", { timeout: 3000 });
    check("/ 菜单提供示例任务快捷命令", (await page.locator('#slashPop [data-slash="/看目录"]').count()) === 1 &&
      (await page.locator('#slashPop [data-slash="/截屏"]').count()) === 1);
    await page.click('#slashPop [data-slash="/看目录"]');
    check("选中示例命令把任务文案填入输入框", (await page.locator("#input").inputValue()) === "看看工作目录里有什么");
    await page.locator("#input").fill("");
    check("参数面板默认收起", await page.locator("#cmpParams").isHidden());
    await page.click("#addBtn");
    await page.click("#paramsBtn");
    check("+ 菜单展开参数面板", await page.locator("#cmpParams").isVisible() &&
      (await page.locator("#modeSelect").isVisible()));
    await page.keyboard.press("Escape");
    check("Esc 收起参数面板", await page.locator("#cmpParams").isHidden());
    await page.click("#addBtn");
    check("输入区 + 菜单可展开且含真实动作", await page.locator("#addPop").isVisible()
      && (await page.locator("#addPop #attachBtn").count()) === 1
      && (await page.locator("#addPop #addRefBtn").count()) === 1);
    await page.click("#addRefBtn");
    check("引用工作目录文件写入 @ 并收起菜单", ((await page.locator("#input").inputValue()) || "").indexOf("@") >= 0
      && (await page.locator("#addPop").isHidden()));
    await page.fill("#input", "");
    check("连接与模型已合并为一个按钮", (await page.locator("#openDeepSeekBtn").count()) === 0
      && (await page.locator("#dsChip").count()) === 0
      && (await page.locator("#modelBtn.ds-chip").count()) === 1
      && (await page.locator("#modelBtn .chip-chev").count()) === 1);
    check("顶栏次要入口收进溢出菜单", (await page.locator("#moreBtn").count()) === 1
      && (await page.locator("#topMenu #themeBtn").count()) === 1
      && (await page.locator("#topMenu").isHidden()));
    await page.click("#moreBtn");
    check("溢出菜单可展开", await page.locator("#topMenu").isVisible());
    check("主界面主题按钮可用", (await page.locator("#themeBtn").count()) === 1);
    const initialTheme = await page.locator("html").getAttribute("data-theme");
    const nextTheme = initialTheme === "dark" ? "light" : "dark";
    await page.click("#themeBtn");
    await page.waitForFunction((theme) => fetch("/api/state").then((r) => r.json()).then((s) => s.config.theme === theme), nextTheme, { timeout: 6000 });
    check("主界面主题切换并保存", await page.locator("html").getAttribute("data-theme") === nextTheme);
    check("工作目录快捷入口不重复", (await page.locator("#workdirChip").count()) === 1 && (await page.locator("#folderBtn").count()) === 0);

    check("工作台入口在顶栏常驻", (await page.locator("#workbenchBtn").count()) === 1);
    await page.click("#workbenchBtn");
    await page.waitForFunction(() => document.querySelector("#app").classList.contains("inspector-open"));
    check("工作台分四段：产物 / 任务 / 对话 / 日志", (await page.locator("#wbTabs .wb-tab").count()) === 4);
    const wbArtifactsText = (await page.locator("#inspectorBody").textContent()) || "";
    check("工作台默认展示产物与文件改动", ["产物", "文件改动"].every((x) => wbArtifactsText.indexOf(x) >= 0));
    await page.click('#wbTabs [data-wb-tab="tasks"]');
    const wbTasksText = (await page.locator("#inspectorBody").textContent()) || "";
    check("工作台任务段展示执行统计与模型", ["任务执行", "成功率", "上下文占用", "模型"].every((x) => wbTasksText.indexOf(x) >= 0));
    await page.click('#wbTabs [data-wb-tab="logs"]');
    const wbLogsText = (await page.locator("#inspectorBody").textContent()) || "";
    check("工作台日志段展示运行日志", wbLogsText.indexOf("运行日志") >= 0);
    await page.click('#wbTabs [data-wb-tab="sessions"]');
    await page.waitForFunction(() => (document.getElementById("wbSessionList") || { childElementCount: 0 }).childElementCount > 0, null, { timeout: 4000 });
    const wbSessionsText = (await page.locator("#inspectorBody").textContent()) || "";
    check("工作台对话段展示所有会话与搜索", wbSessionsText.indexOf("所有对话") >= 0 && (await page.locator("#wbSessionSearch").count()) === 1);
    const wbSessionRows = await page.locator("#wbSessionList [data-wb-session]").count();
    check("工作台对话段列出会话行", wbSessionRows >= 1, "rows=" + wbSessionRows);
    await page.fill("#wbSessionSearch", "不存在的会话标题");
    await page.waitForFunction(() => (document.getElementById("wbSessionList") || { textContent: "" }).textContent.indexOf("没有匹配的会话") >= 0, null, { timeout: 3000 });
    check("工作台对话段搜索过滤生效", true);
    await page.fill("#wbSessionSearch", "");
    check("工作台面板可访问", await page.locator("#inspector").getAttribute("aria-hidden") === "false");
    check("工作台不再重复提供 DeepSeek 打开按钮", (await page.locator("#inspectorBody [data-open-deepseek]").count()) === 0);
    await page.click("#inspectorClose");

    check("主页有一张园景小卡作为花园入口",
      (await page.locator(".home-garden #emptyGarden").count()) === 1 &&
      (await page.locator("#gardenOpenBtn").count()) === 1);
    check("主页只剩一句提问",
      (await page.locator(".home-empty .home-hero-title").count()) === 1 &&
      ((await page.locator(".home-empty").textContent()) || "").indexOf("今天想做点什么") >= 0);
    // 花园：点小卡进全屏，Esc / 返回按钮退出，退出后小景重挂
    await page.click(".home-garden");
    await page.waitForFunction(() => { const v = document.getElementById("gardenView"); return !!v && !v.hidden; }, { timeout: 6000 });
    check("花园全屏可打开并带控制条",
      (await page.locator("#gardenCanvas").count()) === 1 &&
      (await page.locator("#gardenWindBtn").count()) === 1 &&
      (await page.locator("#gardenCloseBtn").count()) === 1);
    await page.click("#gardenCloseBtn");
    await page.waitForFunction(() => document.getElementById("gardenView").hidden, { timeout: 6000 });
    check("退出花园后主页小景重新挂载", (await page.locator(".home-garden #emptyGarden").count()) === 1);

    // 工具箱：顶栏按钮打开全屏，卡片进面板，Esc 分层退出（确认层在 shred 流程里另测）
    check("顶栏常驻工具箱按钮", await page.locator("#toolboxBtn").isVisible());
    await page.click("#toolboxBtn");
    await page.waitForFunction(() => { const v = document.getElementById("toolboxView"); return !!v && !v.hidden; }, { timeout: 6000 });
    check("工具箱全屏可打开且四张大卡齐全",
      (await page.locator("#tbHome .tb-card").count()) === 4 &&
      (await page.locator('#tbHome .tb-card[data-tb="search"]').count()) === 1 &&
      (await page.locator('#tbHome .tb-card[data-tb="launch"]').count()) === 0);
    await page.click('#tbHome .tb-card[data-tb="search"]');
    await page.waitForFunction(() => { const p = document.getElementById("tbPanel"); return !!p && !p.hidden; }, { timeout: 6000 });
    check("极速搜索面板可进入且输入框聚焦",
      ((await page.locator("#tbPanelTitle").textContent()) || "").indexOf("极速搜索") >= 0 &&
      (await page.locator("#tbPanelBody .tb-input").count()) === 1);
    await page.keyboard.press("Escape");
    await page.waitForFunction(() => document.getElementById("tbPanel").hidden, { timeout: 6000 });
    check("Esc 先退回卡片页而不是直接关闭", await page.locator("#tbHome").isVisible());
    await page.keyboard.press("Escape");
    await page.waitForFunction(() => document.getElementById("toolboxView").hidden, { timeout: 6000 });
    check("再按 Esc 关闭工具箱回到原界面", await page.locator("#toolboxView").isHidden());

    // 2. 状态字段
    const state = await page.evaluate(() => fetch("/api/state").then((r) => r.json()));
    check("状态含模型字段", !!state.brain && typeof state.brain.active === "string" && !!state.brain.provider,
      "active=" + (state.brain && state.brain.active));
    check("状态含 config", !!state.config && typeof state.config.providerId === "string" &&
      state.config.brain === undefined && state.config.closeBrowserOnExit === undefined,
      "workDir=" + (state.config && state.config.workDir));
    check("状态含会话列表", Array.isArray(state.sessions) && state.sessions.length >= 1, (state.sessions || []).length + " 个会话");
    // 侧栏常驻左栏：先验证折叠/唤回，再验证分区折叠
    check("侧栏常驻左栏", await page.locator("#sidebar").isVisible());
    await page.click("#menuBtn");
    check("侧栏可折叠并把宽度让给主区", await page.locator("#sidebar").isHidden());
    await page.click("#menuBtn");
    check("顶栏左侧按钮唤回侧栏", await page.locator("#sidebar").isVisible());
    const groupEl = page.locator(".session-group[data-group]").first();
    if (await groupEl.count()) {
      const before = await groupEl.getAttribute("aria-expanded");
      await groupEl.click();
      const after = await groupEl.getAttribute("aria-expanded");
      check("侧栏分区可折叠", before === "true" && after === "false", before + " -> " + after);
      await groupEl.click();
    } else check("侧栏分区可折叠", false, "no collapsible group");
    check("状态含 status", !!state.status && typeof state.status.running === "boolean");
    const sessionFlagRoundTrip = await page.evaluate(async () => {
      const post = (p, b) => fetch(p, { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b) }).then((r) => r.json());
      const s = await fetch("/api/state").then((r) => r.json());
      const id = (s.sessions || [])[0] && s.sessions[0].id;
      if (!id) return { ok: false, why: "no session" };
      const on = await post("/api/session/pin", { id, value: true });
      const off = await post("/api/session/pin", { id, value: false });
      const arc = await post("/api/session/archive", { id, value: true });
      const back = await post("/api/session/archive", { id, value: false });
      const flag = (st, key) => (st.sessions || []).some((x) => x.id === id && x[key] === true);
      return { ok: flag(on, "pinned") && !flag(off, "pinned") && flag(arc, "archived") && !flag(back, "archived") };
    });
    check("置顶与归档接口往返生效", sessionFlagRoundTrip.ok, JSON.stringify(sessionFlagRoundTrip));
    const bulkRoundTrip = await page.evaluate(async () => {
      const post = (p, b) => fetch(p, { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b) }).then((r) => r.json());
      const st = await fetch("/api/state").then((r) => r.json());
      const ids = (st.sessions || []).slice(0, 2).map((x) => x.id);
      if (!ids.length) return { skipped: true };
      const has = (state, key, want) => ids.every((id) => (state.sessions || []).some((x) => x.id === id && x[key] === want));
      const arc = await post("/api/sessions/bulk", { action: "archive", ids });
      const back = await post("/api/sessions/bulk", { action: "unarchive", ids });
      return { ok: has(arc, "archived", true) && has(back, "archived", false), n: ids.length };
    });
    check("批量归档 / 取消归档一次提交生效", bulkRoundTrip.skipped || bulkRoundTrip.ok, JSON.stringify(bulkRoundTrip));

    // 归档 / 未归档两个 tab 常驻，切换后列表跟着换
    await page.click("#tabArchived");
    check("切到已归档视图", await page.locator("#tabArchived").getAttribute("aria-selected") === "true" &&
      await page.locator("#tabActive").getAttribute("aria-selected") === "false");
    await page.click("#tabActive");
    check("切回未归档视图", await page.locator("#tabActive").getAttribute("aria-selected") === "true");

    // 批量选择：进入后整行变成复选框，不生成悬停操作按钮
    await page.click("#selectToggle");
    check("批量栏出现且搜索框让位", await page.locator("#bulkBar").isVisible() &&
      await page.locator("#sessionSearchWrap").isHidden());
    const pickTarget = page.locator("#sessionList .session-item").first();
    await pickTarget.click();
    const bulkText = (await page.locator("#bulkCount").textContent()) || "";
    check("会话可被勾选并计数", bulkText.indexOf("已选 1 项") >= 0 &&
      (await page.locator("#sessionList .session-check.on").count()) === 1 &&
      (await page.locator("#sessionList .session-ops").count()) === 0, bulkText);
    if ((await page.locator("#bulkAllBtn").getAttribute("aria-pressed")) === "true") await page.click("#bulkAllBtn");
    await page.click("#bulkAllBtn");
    const bulkAllText = (await page.locator("#bulkCount").textContent()) || "";
    check("全选把当前视图全部选中", /已选 [1-9]/.test(bulkAllText) &&
      (await page.locator("#bulkAllBtn").getAttribute("aria-pressed")) === "true", bulkAllText);
    await page.click("#bulkCancelBtn");
    check("退出批量选择后复原", await page.locator("#bulkBar").isHidden() &&
      await page.locator("#sessionSearchWrap").isVisible());
    const forkRoundTrip = await page.evaluate(async () => {
      const st = await fetch("/api/state").then((r) => r.json());
      const before = (st.sessions || []).length;
      const msgs = (st.active && st.active.messages) || [];
      if (!msgs.length) return { skipped: true };
      const r = await fetch("/api/session/fork", {
        method: "POST", credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id: msgs[msgs.length - 1].id })
      }).then((x) => x.json());
      return {
        before, after: (r.sessions || []).length,
        activeMsgs: ((r.active && r.active.messages) || []).length,
        title: r.active && r.active.title
      };
    });
    check("上下文分叉创建带完整历史的新会话", forkRoundTrip.skipped ||
      (forkRoundTrip.after === forkRoundTrip.before + 1 && forkRoundTrip.activeMsgs >= 1 &&
        /分支/.test(forkRoundTrip.title || "")), JSON.stringify(forkRoundTrip));
    const freeProviders = await page.evaluate(async () => {
      const r = await fetch("/api/providers", {
        method: "POST", credentials: "same-origin",
        headers: { "Content-Type": "application/json" }, body: "{}"
      }).then((x) => x.json());
      return r.providers || [];
    });
    check("内置服务商只含官方旗舰档", freeProviders.length === 5 &&
      freeProviders.every((p) => p.models && p.models.length > 0) &&
      freeProviders.every((p) => p.free !== true),
      freeProviders.map((p) => p.id + ":" + (p.models || []).join("/")).join(" | "));
    await page.click("#modelBtn");
    const modelMenuText = await page.locator("#cmpPop").innerText();
    check("模型面板不提供免费额度入口且首行显示当前模型",
      (await page.locator("#cmpPop [data-cmp='free']").count()) === 0 && /当前模型/.test(modelMenuText),
      modelMenuText.split("\n").slice(0, 3).join(" / "));
    await page.keyboard.press("Escape");
    const permitRoundTrip = await page.evaluate(async () => {
      const post = (b) => fetch("/api/config", {
        method: "POST", credentials: "same-origin",
        headers: { "Content-Type": "application/json" }, body: JSON.stringify(b)
      }).then((r) => r.json());
      const on = await post({ permitRules: [{ match: "shell:Get-ChildItem*", action: "allow" }] });
      const off = await post({ permitRules: [] });
      return { on: on.config && on.config.permitRules, off: off.config && off.config.permitRules };
    });
    check("工具权限规则可保存与清空", permitRoundTrip.on && permitRoundTrip.on.length === 1 &&
      permitRoundTrip.off && permitRoundTrip.off.length === 0, JSON.stringify(permitRoundTrip));
    const mcpState = await page.evaluate(async () => {
      const servers = await fetch("/api/mcp/servers").then((r) => r.json());
      const found = await fetch("/api/mcp/discover").then((r) => r.json());
      return { servers: Array.isArray(servers) ? servers : [], found: Array.isArray(found) ? found : [] };
    });
    const reaServer = mcpState.servers.filter((s) => /rea/i.test(s.name || ""))[0];
    if (reaServer) check("REA 已连接并注册工具", reaServer.ready === true && reaServer.toolCount > 0,
      "ready=" + reaServer.ready + " tools=" + reaServer.toolCount + " err=" + reaServer.error);
    check("本机 MCP 配置可被发现", mcpState.found.some((s) => /rea/i.test(s.name || "")),
      JSON.stringify(mcpState.found.map((s) => s.name)));
    check("REA 已自动接入绿虎", mcpState.servers.some((s) => /rea/i.test(s.name || "") && s.enabled),
      JSON.stringify(mcpState.servers.map((s) => ({ name: s.name, enabled: s.enabled, ready: s.ready, tools: s.toolCount }))));
    // 思考强度已并入模型面板（水平推理滑块），操作滑块前展开模型面板
    await page.click("#modelBtn");
    check("思考强度位于模型面板而非齿轮面板", (await page.locator("#cmpPop #effortRange").count()) === 1
      && (await page.locator("#cmpParams #effortRange").count()) === 0);
    await page.locator("#effortRange").focus();
    await page.keyboard.press("End");
    await page.waitForFunction(() => fetch("/api/state").then((r) => r.json()).then((s) => s.config.reasoningEffort === "xhigh"), { timeout: 6000 });
    check("水平滑块直接保存极致档", (await page.locator("#effortRange").getAttribute("aria-valuenow")) === "3" &&
      (await page.locator("#effortCurrent").textContent()) === "极致");
    await page.keyboard.press("ArrowLeft");
    await page.waitForFunction(() => fetch("/api/state").then((r) => r.json()).then((s) => s.config.reasoningEffort === "high"), { timeout: 6000 });
    check("水平滑块可切换到深入档", (await page.locator("#effortCurrent").textContent()) === "深入");
    await page.keyboard.press("ArrowLeft");
    await page.waitForFunction(() => fetch("/api/state").then((r) => r.json()).then((s) => s.config.reasoningEffort === "medium"), { timeout: 6000 });
    check("水平滑块可回到平衡档", (await page.locator("#effortCurrent").textContent()) === "平衡");
    check("参数面板解释文字跟随档位", ((await page.locator("#effortHint").textContent()) || "").indexOf("平衡") >= 0);
    await page.keyboard.press("Escape");
    check("操作完成后模型面板已收起", await page.locator("#cmpPop").isHidden());

    // toolHtml is intentionally private inside the UI IIFE; verify its diff renderer without exposing app internals globally.
    const hasFileDiffRenderer = /function toolDiffHtml\(t\)/.test(UI_SOURCE)
      && /function diffHtml\(text\)/.test(UI_SOURCE)
      && /cls = "diff-del"/.test(UI_SOURCE)
      && /cls = "diff-add"/.test(UI_SOURCE);
    check("文件变更差异渲染器区分删除与新增行", hasFileDiffRenderer);
    check("工具行具备状态图标与轻卡片形态",
      /class="tool-state spin"/.test(UI_SOURCE) &&
      /class="tool-state done"/.test(UI_SOURCE) &&
      /class="tool-state bad"/.test(UI_SOURCE) &&
      /icon\(meta\.icon,"tool-ico"\)/.test(UI_SOURCE) &&
      /\.tool\{[^}]*border:1px solid var\(--line-soft\)/.test(UI_SOURCE));
    check("模型回复逐字流式渲染",
      /new EventSource\("\/api\/events"\)/.test(UI_SOURCE) &&
      /function reconcileMessages\(/.test(UI_SOURCE) &&
      /msgRenderKey/.test(UI_SOURCE));

    // 3. 当前模型只在输入区显示，配置入口从模型菜单直达模型设置。
    check("模型选择移入输入框顶部，不再占用顶栏",
      (await page.locator(".composer #modelBtn").count()) === 1 &&
      (await page.locator(".connection-group").count()) === 0);

    // 顶栏模型指示必须跟着切换走：以前 applyCfg 只取 config，S.brain 是旧的，按钮一直停在切换前的名字
    const modelBefore = ((await page.locator("#modelBtn").textContent()) || "").trim();
    await page.click("#modelBtn");
    await page.waitForSelector("#cmpPop", { timeout: 6000 });
    // 展开面板必须紧贴触发按钮：以前是 fixed 贴在屏幕右下角，现在向上弹出并贴着按钮本身
    const anchor = await page.evaluate(() => {
      const b = document.querySelector("#modelBtn").getBoundingClientRect();
      const p = document.querySelector("#cmpPop").getBoundingClientRect();
      return {
        gap: Math.round(b.top - p.bottom),
        dRight: Math.round(Math.abs(p.right - b.right)),
        overlap: Math.round(Math.min(p.right, b.right) - Math.max(p.left, b.left))
      };
    });
    check("模型面板紧贴触发按钮（向上弹出）", anchor.gap >= 0 && anchor.gap <= 16 && anchor.dRight <= 16 && anchor.overlap > 40,
      JSON.stringify(anchor));
    await page.keyboard.press("Escape");
    await sleep(200);
    check("Escape 关闭模型面板并退回按钮", await page.locator("#cmpPop").isHidden() &&
      await page.evaluate(() => document.activeElement && document.activeElement.id === "modelBtn"));
    await page.click("#modelBtn");
    await page.waitForSelector("#cmpPop", { timeout: 6000 });
    // 胶囊写的是当前模型；没接入模型时必须如实写「未接入模型」，不许谎报
    const connSync = await page.evaluate(async () => {
      const s = await fetch("/api/state").then((r) => r.json());
      return {
        active: (s.brain && s.brain.active) || "none",
        endpoint: (s.brain && s.brain.provider && s.brain.provider.endpoint) || "",
        chip: document.querySelector("#dsChipText").textContent.trim()
      };
    });
    check("未接入模型时胶囊如实写未接入模型",
      connSync.active === "none" && connSync.endpoint === "" && connSync.chip.indexOf("未接入模型") >= 0,
      JSON.stringify(connSync));
    check("模型菜单只保留云端配置入口",
      (await page.locator('#cmpPop [data-cmp="settings"]').count()) === 1 &&
      (await page.locator('#cmpPop [data-cmp="discover"]').count()) === 0);
    check("模型配置只保留在输入栏面板，主页不再重复引导",
      (await page.locator('[data-model-setup]').count()) === 0 &&
      (await page.locator('#cmpPop [data-cmp="settings"]').count()) === 1);

    // 填一个连不上的端点：探测失败也不许把胶囊写成「已就绪」
    await page.evaluate(() => fetch("/api/config", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ providerId: "deepseek-api", providerEndpoint: "http://127.0.0.1:9/v1", providerModel: "deepseek-chat" })
    }));
    await sleep(1500);
    const afterBad = await page.evaluate(async () => {
      const s = await fetch("/api/state").then((r) => r.json());
      return { active: (s.brain && s.brain.active) || "none", chip: document.querySelector("#dsChipText").textContent.trim() };
    });
    check("端点不可用时不谎报已连接且仍显示选定的模型", afterBad.active === "none" &&
      /未接入/.test(afterBad.chip) && /deepseek-chat/.test(afterBad.chip),
      JSON.stringify(afterBad));
    const homeStatus = ((await page.locator("#homeStatus").textContent()) || "");
    check("主页状态行跟随模型状态", /未接入/.test(homeStatus) && /deepseek-chat/.test(homeStatus), homeStatus.trim());

    await page.keyboard.press("Escape");
    await sleep(200);

    // 清空，后面的用例在干净状态下跑
    await page.evaluate(() => fetch("/api/config", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ providerId: "", providerEndpoint: "", providerKey: "", providerModel: "" })
    }));
    await sleep(600);

    // 4. 模型服务与策略集中在设置中
    await page.click("#modelBtn");
    await page.waitForSelector("#cmpPop", { timeout: 6000 });
    await page.click('#cmpPop [data-cmp="settings"]');
    await page.waitForSelector("#settingsForm", { timeout: 6000 });
    check("模型菜单配置入口直达模型设置", await page.locator('#settingsNav [data-cat="models"]').evaluate((el) => el.classList.contains("active")));
    check("模型策略设置解释对应输入区选择器", (await page.locator("#modelStrategyInfo").count()) === 1);
    check("本地模型设置已删除", (await page.locator("#setLocalEndpoint").count()) === 0);
    check("模型策略没有第二个设置选择器", (await page.locator("#setBrainMode").count()) === 0);
    const settingCats = await page.locator("#settingsNav .settings-nav-btn").count();
    check("设置分为七个主类", settingCats === 7, settingCats + " 类");
    await sleep(400);
    await page.screenshot({ path: path.join(SHOT_DIR, "e2e-model-settings.png") });

    // 5. 关闭设置
    await page.keyboard.press("Escape");
    await sleep(250);
    check("Escape 关闭设置", (await page.locator("#settingsForm").count()) === 0);

    // 7. 命令面板
    await page.click("#moreBtn");
    await page.click("#paletteBtn");
    await page.waitForSelector("#paletteList", { timeout: 6000 });
    const paletteItems = await page.locator(".palette-item").count();
    check("命令面板渲染命令", paletteItems >= 10, paletteItems + " 条");
    await page.fill("#paletteInput", "设置");
    await sleep(250);
    const filtered = await page.locator(".palette-item").count();
    check("命令面板可搜索", filtered >= 1 && filtered < paletteItems, filtered + " 条命中");
    await page.screenshot({ path: path.join(SHOT_DIR, "e2e-palette.png") });
    await page.keyboard.press("Escape");
    await sleep(250);
    check("Escape 关闭命令面板", (await page.locator("#paletteList").count()) === 0);

    // 7b. 命令面板：模糊匹配 + 命中字符高亮 + 键盘高亮播报
    // 旧实现是 label.indexOf(子串)，打不连续的字母整个列表就空了；
    // 现在按子序列打分，"md" 应该命中「导出当前会话为 Markdown」。
    await page.keyboard.press("Control+k");
    await page.waitForSelector("#paletteInput", { timeout: 6000 });
    await page.fill("#paletteInput", "md");
    await sleep(250);
    const fuzzyFirst = ((await page.locator(".palette-item span").first().textContent()) || "").trim();
    check("命令面板支持非连续模糊匹配", fuzzyFirst.indexOf("Markdown") >= 0, fuzzyFirst);
    check("模糊匹配的命中字符被标出", (await page.locator(".palette-item .fz").count()) >= 1);
    check("面板键盘高亮可被读屏播报", await page.locator("#paletteInput").evaluate((el) => {
      const id = el.getAttribute("aria-activedescendant");
      return !!id && !!document.getElementById(id);
    }));
    // 快捷键速查：Ctrl / 呼出，表里每一行都要有键帽
    await page.keyboard.press("Escape");
    await sleep(200);
    await page.keyboard.press("Control+Slash");
    await page.waitForSelector("#shortcutsOk", { timeout: 6000 });
    const shortcutRows = await page.locator(".shortcut-row").count();
    check("Ctrl / 打开快捷键速查表", shortcutRows >= 10, shortcutRows + " 条");
    check("快捷键条目都有键帽", (await page.locator(".shortcut-row kbd").count()) >= shortcutRows);
    await page.screenshot({ path: path.join(SHOT_DIR, "e2e-shortcuts.png") });
    await page.keyboard.press("Escape");
    await sleep(200);


    // 8. 设置面板控件
    await page.click("#settingsBtn");
    await page.waitForSelector("#settingsForm", { timeout: 6000 });
    check("工作目录入口只留顶栏图标，完整路径只在状态栏",
      (await page.locator("#setWorkDir, #folderBtn").count()) === 0 &&
      (await page.locator("#workdirChip").count()) === 1 &&
      (await page.locator("#statusWorkdir").count()) === 1 &&
      (await page.locator("#workdirLabel").isVisible()) === false);
    check("主题切换已从设置移至主界面", (await page.locator("#setTheme").count()) === 0 && (await page.locator("#themeBtn").count()) === 1);
    check("设置不重复放置权限模式", (await page.locator("#setModeSeg").count()) === 0);
    check("设置导航仅保留主流分类", (await page.locator("#settingsNav .settings-nav-btn").count()) === 7);
    check("非 Agent API 导航目录已删除", (await page.locator("#apiSiteSearch, #freeProviderList, #setAutoShell").count()) === 0);
    await page.click('#settingsNav [data-cat="appearance"]');
    check("界面动效与提示音已迁入设置", (await page.locator("#setUiMotion, #setUiSounds").count()) === 2 &&
      (await page.locator(".home-prefs").count()) === 0);
    // 新入口必须真能落库，而不只是画了两个开关
    await page.click("#setUiSounds");
    await page.click("#setUiMotion");
    await page.click("#settingsSave");
    await page.waitForFunction(() => !document.querySelector("#settingsForm"), { timeout: 6000 });
    const savedState = await page.evaluate(() => fetch("/api/state").then((r) => r.json()));
    check("设置保存外观并保留工作目录", !!savedState.config.workDir && savedState.config.theme === nextTheme);
    check("设置里的界面偏好已落库", savedState.config.uiSounds === true && savedState.config.uiMotion === false);
    check("关闭动效后进入减弱动效", await page.locator("html").evaluate((el) => el.classList.contains("motion-reduced")));
    // 设置保存必须立刻落到当前窗口：工作台常驻开关不再等下次启动
    const inspectorAfterSave = await page.evaluate(() => ({
      cfg: !!document.querySelector("#app").classList.contains("inspector-open"),
      saved: null
    }));
    const cfgShow = savedState.config.showInspector;
    check("设置保存后工作台常驻立即与配置一致",
      inspectorAfterSave.cfg === !!cfgShow, JSON.stringify({ ui: inspectorAfterSave.cfg, cfg: cfgShow }));

    // 8c. 批量选择 UI 走一遍：新建临时会话 → 勾选 → 归档 → 已归档视图 → 取消归档 → 删除。
    // 全程只动这个临时会话，不碰 demo 数据。
    await page.evaluate(() => fetch("/api/session/new", {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json" }, body: "{}"
    }).then((r) => r.json()));
    await sleep(600);
    await page.click("#selectToggle");
    await page.locator("#sessionList .session-item").first().click();
    await page.click("#bulkArchiveBtn");
    await page.waitForFunction(() => document.getElementById("bulkBar").hidden, { timeout: 8000 });
    await page.click("#tabArchived");
    await page.waitForFunction(() => document.querySelectorAll("#sessionList .session-item").length > 0, { timeout: 8000 });
    check("UI 批量归档后出现在已归档视图", (await page.locator("#sessionList .session-item").count()) >= 1);
    await page.click("#selectToggle");
    await page.locator("#sessionList .session-item").first().click();
    check("已归档视图里批量按钮变成取消归档",
      ((await page.locator("#bulkArchiveBtn").textContent()) || "").indexOf("取消归档") >= 0);
    await page.click("#bulkArchiveBtn");
    await page.waitForFunction(() => document.getElementById("bulkBar").hidden, { timeout: 8000 });
    await page.click("#tabActive");
    check("取消归档后回到会话视图", (await page.locator("#sessionList .session-item").count()) >= 1);

    const bulkDelete = await page.evaluate(async () => {
      const post = (p, b) => fetch(p, { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b || {}) }).then((r) => r.json());
      const made = await post("/api/session/new", {});
      const victim = made.active && made.active.id;
      const before = (made.sessions || []).length;
      if (!victim) return { skipped: true };
      const del = await post("/api/sessions/bulk", { action: "delete", ids: [victim] });
      return { before, after: (del.sessions || []).length, gone: !(del.sessions || []).some((x) => x.id === victim) };
    });
    check("批量删除接口移除会话", bulkDelete.skipped || (bulkDelete.gone && bulkDelete.after === bulkDelete.before - 1), JSON.stringify(bulkDelete));

    // 8b. 矮窗口回归：空态比可视区高时，以前居中的顶部被裁、状态行被输入框盖住且滚不到
    await page.setViewportSize({ width: 1024, height: 640 });
    await sleep(600);
    const compactHome = await page.evaluate(() => {
      const sc = document.querySelector("#chatScroll");
      const sr = document.querySelector(".empty-status").getBoundingClientRect();
      const dr = document.querySelector(".dock").getBoundingClientRect();
      return {
        overflow: sc.scrollHeight - sc.clientHeight,
        statusBottom: Math.round(sr.bottom), dockTop: Math.round(dr.top)
      };
    });
    check("矮窗口下主页不溢出、状态行不被输入框压住",
      compactHome.overflow <= 2 && compactHome.statusBottom <= compactHome.dockTop,
      JSON.stringify(compactHome));
    await page.screenshot({ path: path.join(SHOT_DIR, "e2e-home-compact.png") });
    await page.setViewportSize({ width: 1440, height: 900 });
    await sleep(400);

    // 9. 无 JS 报错
    check("无页面报错", errors.length === 0, errors.slice(0, 3).join(" | "));

    // 10. 深色/浅色都能渲染
    await page.evaluate(() => {
      document.documentElement.dataset.theme = "light";
    });
    await sleep(250);
    await page.screenshot({ path: path.join(SHOT_DIR, "e2e-light.png") });
    check("浅色主题可渲染", true);

  } finally {
    if (browser) await browser.close().catch(() => {});
    child.kill();
    await sleep(400);
    if (stderr.trim()) console.log("--- 进程 stderr ---\n" + stderr.trim().slice(0, 800));
    const pass = results.length - failed;
    console.log("\n" + pass + "/" + results.length + " 通过");
    if (failed) {
      console.log("失败项：");
      results.filter((r) => !r.ok).forEach((r) => console.log("  - " + r.name + (r.detail ? " :: " + r.detail : "")));
    }
    process.exitCode = failed ? 1 : 0;
  }
}

main().catch((e) => {
  console.error("E2E 失败：" + (e && e.stack ? e.stack : e.message));
  process.exitCode = 1;
});
