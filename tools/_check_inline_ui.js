/*
 * 界面静态检查（改 ui/ 后必跑）—— P3 起支持拆分后的多文件结构
 *
 * 1) 语法：每个 js/*.js 按 ES module 用 `node --check` 校验；
 * 2) 结构：index.html 必须是干净结构，只留一个 type="module" 主入口；
 * 3) 死引用：getElementById("x") 里的 x 必须真实存在（HTML 或模板字符串里）；
 * 4) 重复 id：静态标记里同一个 id 出现两次必是笔误；
 * 5) 图标：icon("x") 用到的名字必须在 ICONS 表里；
 * 6) 模块闭合：每条 import 的名字必须在目标模块的 export 列表里。
 *
 * 第 6 条是拆分后新增的：模块被搬走但漏了导出，浏览器里会直接报
 * "does not provide an export named ..."，整页白屏。
 */
"use strict";

const fs = require("fs");
const path = require("path");
const os = require("os");
const { execFileSync } = require("child_process");

const root = path.resolve(__dirname, "..");
const uiDir = path.join(root, "ui");
const MODULES = ["util.js", "markdown.js", "tools.js", "chat.js", "model.js", "app.js", "toolbox.js"];

const html = fs.readFileSync(path.join(uiDir, "index.html"), "utf8");
const css = fs.readFileSync(path.join(uiDir, "style.css"), "utf8");
const jsFiles = {};
for (const m of MODULES) jsFiles[m] = fs.readFileSync(path.join(uiDir, "js", m), "utf8");
const jsAll = Object.values(jsFiles).join("\n");
const all = [html, css, jsAll].join("\n");

const problems = [];

/* ---------- 1. ES 模块语法 ---------- */
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "gt-ui-check-"));
try {
  for (const m of MODULES) {
    const dst = path.join(tmp, m.replace(/\.js$/, ".mjs"));
    fs.writeFileSync(dst, jsFiles[m], "utf8");
    try {
      execFileSync(process.execPath, ["--check", dst], { stdio: "pipe" });
    } catch (e) {
      problems.push("ES 模块语法错误 " + m + ": " + String(e.stderr || e.message).slice(0, 300));
    }
  }
} finally {
  fs.rmSync(tmp, { recursive: true, force: true });
}

/* ---------- 2. 干净结构 ---------- */
if (!/<script type="module" src="\/js\/app\.js"><\/script>/.test(html))
  problems.push("index.html 缺少 type=module 主入口");
if (/<script>/.test(html) || /<style>/.test(html))
  problems.push("index.html 仍有内联 script / style");
if (html.indexOf('href="/style.css"') < 0) problems.push("index.html 缺少 style.css 链接");

/* ---------- 3. 收集 id，检查死引用 ---------- */
const markupIds = Array.from(html.matchAll(/\bid="([^"]+)"/g)).map((m) => m[1]);
const templateIds = Array.from(all.matchAll(/\bid="([A-Za-z0-9_$-]+)"/g)).map((m) => m[1]);
const known = new Set([...markupIds, ...templateIds]);
const refs = Array.from(new Set(Array.from(all.matchAll(/getElementById\("([^"]+)"\)/g)).map((m) => m[1])));
const dangling = refs.filter((id) => !known.has(id));
if (dangling.length) problems.push("getElementById 引用了不存在的 id: " + dangling.join(", "));

/* ---------- 4. 静态标记里的重复 id ---------- */
const seen = new Map();
for (const id of markupIds) seen.set(id, (seen.get(id) || 0) + 1);
const duplicated = Array.from(seen.entries()).filter(([, n]) => n > 1).map(([id]) => id);
if (duplicated.length) problems.push("静态标记里 id 重复: " + duplicated.join(", "));

/* ---------- 5. 图标名 ---------- */
const iconsStart = jsAll.indexOf("var ICONS");
const iconsEnd = iconsStart < 0 ? -1 : jsAll.indexOf("\n};", iconsStart);
const iconNames = new Set();
if (iconsStart >= 0 && iconsEnd > iconsStart) {
  for (const m of jsAll.slice(iconsStart, iconsEnd).matchAll(/^\s{2}([A-Za-z0-9_]+):/gm)) iconNames.add(m[1]);
}
const usedIcons = Array.from(new Set(Array.from(all.matchAll(/\bicon\("([A-Za-z0-9_]+)"/g)).map((m) => m[1])));
const unknownIcons = iconNames.size ? usedIcons.filter((n) => !iconNames.has(n)) : [];
if (unknownIcons.length) problems.push("icon() 使用了未定义的图标: " + unknownIcons.join(", "));

/* ---------- 6. import / export 闭合 ---------- */
for (const [file, src] of Object.entries(jsFiles)) {
  for (const im of src.matchAll(/import\s*\{([^}]+)\}\s*from\s*"\.\/([A-Za-z0-9_.-]+)";/g)) {
    const names = im[1].split(",").map((x) => x.trim()).filter(Boolean);
    const target = jsFiles[im[2]];
    if (!target) { problems.push(file + " 引用了不存在的模块 " + im[2]); continue; }
    const exported = new Set(
      Array.from(target.matchAll(/export\s*\{([^}]+)\}/g))
        .flatMap((x) => x[1].split(",").map((y) => y.trim()))
    );
    for (const n of names) {
      if (!exported.has(n)) problems.push(file + " 从 " + im[2] + " 导入了未导出的 " + n);
    }
  }
}

/* ---------- 7. 跨模块标识符引用闭合 ----------
 * 只查「被抽到模块里的顶层名字」：函数之外还有 ICONS / TOOL_META / HL_* 这类常量，
 * 漏了 import 时浏览器会抛 ReferenceError，且只有走到对应渲染分支才暴露。 */
const moduleDefined = {};
const moduleImported = {};
for (const [file, srcText] of Object.entries(jsFiles)) {
  moduleDefined[file] = new Set(
    Array.from(srcText.matchAll(/^(?:function|var|const|let)\s+([A-Za-z_$][\w$]*)/gm)).map((m) => m[1])
  );
  moduleImported[file] = new Set(
    Array.from(srcText.matchAll(/import\s*\{([^}]+)\}/g))
      .flatMap((m) => m[1].split(",").map((x) => x.trim()))
  );
}
const movedNames = new Set(
  Object.entries(moduleDefined).filter(([f]) => f !== "app.js")
    .flatMap(([, set]) => Array.from(set))
);
for (const [file, srcText] of Object.entries(jsFiles)) {
  const body = srcText.split("\n").filter((l) => !/^\s*(import|export)\s/.test(l)).join("\n");
  const missing = new Set();
  for (const m of body.matchAll(/\b([A-Za-z_$][\w$]*)\b/g)) {
    const n = m[1];
    if (!movedNames.has(n)) continue;
    if (moduleDefined[file].has(n) || moduleImported[file].has(n)) continue;
    missing.add(n);
  }
  if (missing.size) problems.push(file + " 引用了未 import 的跨模块标识符: " + Array.from(missing).join(", "));
}

if (problems.length) {
  console.error("inline UI check failed:");
  for (const p of problems) console.error("  - " + p);
  process.exit(1);
}
console.log("inline UI OK · " + MODULES.length + " 个 ES 模块 / " + refs.length +
  " 个 id 引用 / " + usedIcons.length + " 个图标 / 语法与 import 闭合通过");
