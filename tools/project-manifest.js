#!/usr/bin/env node
"use strict";
/**
 * GreenTiger 项目清单（单一事实源）。
 *
 * 事实源：
 *   - 产品版本：src/Config.cs 的 AppInfo.Version
 *   - UI 内嵌资源清单：本文件的 UI_ASSETS
 *
 * 校验范围（verify）：
 *   1. 所有派生版本落点与事实源一致（程序集 / npm / Win32 manifest）。
 *   2. UI 源码里不允许再出现硬编码的产品版本号：缓存戳用 __GT_VERSION__ 占位，
 *      由 WebServer.ServeUi 在下发 HTML 时替换；显示版本一律来自 /api/state。
 *   3. build.ps1 / App.ExportEmbeddedUi / WebServer 三份资源清单与 UI_ASSETS 完全一致，
 *      且 ui/ 下没有未登记的孤儿文件。
 *   4. 构建门禁自锁：build.ps1 必须调用 check-version.js，防止有人把门禁删掉。
 *
 * 同步（sync）：bump-version.js 用它一次性改写全部落点，避免手改漏项。
 * 全部只用 Node 内置模块，不引入任何依赖；写回时保留原文件 BOM。
 */

const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const VERSION_SOURCE = "src/Config.cs";
const VERSION_RE = /\bpublic\s+const\s+string\s+Version\s*=\s*"(\d+\.\d+\.\d+)"/;
const SEMVER_RE = /^\d+\.\d+\.\d+$/;
const PLACEHOLDER = "__GT_VERSION__";

/** ui/ 下全部内嵌资源，路径相对 ui/。新增资源必须同时改这里与三处消费点。 */
const UI_ASSETS = [
  "index.html",
  "style.css",
  "theme.css",
  "js/util.js",
  "js/markdown.js",
  "js/tools.js",
  "js/chat.js",
  "js/model.js",
  "js/app.js",
  "js/toolbox.js",

  "shell.js",
  "garden.css",
  "garden.js",
  "manifest.webmanifest",
  "icon-192.png",
  "icon-512.png",
  "swirl.png",
];

function abs(rel) { return path.join(ROOT, rel); }
function read(rel) { return fs.readFileSync(abs(rel), "utf8"); }
function write(rel, text) { fs.writeFileSync(abs(rel), text, "utf8"); }
function stripBom(text) { return text.charCodeAt(0) === 0xfeff ? text.slice(1) : text; }
function capture(text, re) { const m = re.exec(text); return m ? m[1] : null; }

/** 派生版本落点。inspect 读当前值，expected 给期望值，fix 写回新版本。 */
const VERSION_SLOTS = [
  {
    file: "src/AssemblyInfo.cs", label: "AssemblyVersion",
    inspect: t => capture(t, /\[assembly:\s*AssemblyVersion\("([^"]+)"\)\]/),
    expected: v => v + ".0",
    fix: (t, v) => t.replace(/(\[assembly:\s*AssemblyVersion\(")[^"]+("\)\])/, "$1" + v + ".0$2"),
  },
  {
    file: "src/AssemblyInfo.cs", label: "AssemblyFileVersion",
    inspect: t => capture(t, /\[assembly:\s*AssemblyFileVersion\("([^"]+)"\)\]/),
    expected: v => v + ".0",
    fix: (t, v) => t.replace(/(\[assembly:\s*AssemblyFileVersion\(")[^"]+("\)\])/, "$1" + v + ".0$2"),
  },
  {
    file: "src/AssemblyInfo.cs", label: "AssemblyInformationalVersion",
    inspect: t => capture(t, /\[assembly:\s*AssemblyInformationalVersion\("([^"]+)"\)\]/),
    expected: v => v,
    fix: (t, v) => t.replace(/(\[assembly:\s*AssemblyInformationalVersion\(")[^"]+("\)\])/, "$1" + v + "$2"),
  },
  {
    file: "package.json", label: "package.json version",
    inspect: t => capture(stripBom(t), /"version"\s*:\s*"([^"]+)"/),
    expected: v => v,
    fix: (t, v) => t.replace(/("version"\s*:\s*")[^"]+(")/, "$1" + v + "$2"),
  },
  {
    file: "package-lock.json", label: "package-lock root + packages[\"\"] version",
    inspect: t => {
      try {
        const o = JSON.parse(stripBom(t));
        return o.version + "/" + (o.packages && o.packages[""] ? o.packages[""].version : "<missing>");
      } catch (e) { return null; }
    },
    expected: v => v + "/" + v,
    fix: (t, v) => {
      const bom = t.charCodeAt(0) === 0xfeff ? "\ufeff" : "";
      const o = JSON.parse(stripBom(t));
      o.version = v;
      if (o.packages && o.packages[""]) o.packages[""].version = v;
      return bom + JSON.stringify(o, null, 2) + "\n";
    },
  },
  {
    file: "app.manifest", label: "Win32 assemblyIdentity version",
    inspect: t => capture(t, /<assemblyIdentity\s+version="([^"]+)"/),
    expected: v => v + ".0",
    fix: (t, v) => t.replace(/(<assemblyIdentity\s+version=")[^"]+(")/, "$1" + v + ".0$2"),
  },
];

/** 读事实源版本；读不到或格式不对直接抛错。 */
function readVersion() {
  const text = read(VERSION_SOURCE);
  const v = capture(text, VERSION_RE);
  if (!v) throw new Error("无法从 " + VERSION_SOURCE + " 读取 AppInfo.Version");
  if (!SEMVER_RE.test(v)) throw new Error("版本号必须是 x.y.z，实际为 " + v);
  return v;
}

/** UI 源码里禁止再写死产品版本号（garden.js 的模块自身版本 1.0.0 不在扫描范围）。 */
function verifyNoHardcodedVersion(errors) {
  const scanned = ["ui/index.html"].concat(
    fs.readdirSync(abs("ui/js")).filter(f => f.endsWith(".js")).map(f => "ui/js/" + f)
  );
  const re = /\bv?(\d+\.\d+\.\d+)\b/g;
  for (const rel of scanned) {
    const text = read(rel);
    let m;
    while ((m = re.exec(text)) !== null) {
      const line = text.slice(0, m.index).split(/\r?\n/).length;
      errors.push(rel + ":" + line + " 出现硬编码版本号 \"" + m[1] + "\"；缓存戳请用 " +
        PLACEHOLDER + " 占位，显示版本请用 /api/state 的 version 字段");
    }
  }
  const html = read("ui/index.html");
  if (html.indexOf("?v=" + PLACEHOLDER) < 0)
    errors.push("ui/index.html 缺少 ?v=" + PLACEHOLDER + " 缓存戳占位，升级版本后浏览器会继续用旧 CSS");
  const server = read("src/WebServer.cs");
  if (server.indexOf('Replace("' + PLACEHOLDER + '"') < 0)
    errors.push("src/WebServer.cs 的 ServeUi 未把 " + PLACEHOLDER + " 替换为 AppInfo.Version，缓存戳会原样下发");
}

function collectUiFiles() {
  const out = [];
  const walk = (dir, prefix) => {
    for (const entry of fs.readdirSync(abs(dir), { withFileTypes: true })) {
      const rel = prefix ? prefix + "/" + entry.name : entry.name;
      if (entry.isDirectory()) walk(path.join(dir, entry.name), rel);
      else if (entry.isFile()) out.push(rel);
    }
  };
  walk("ui", "");
  return out;
}

function sameSet(a, b) {
  const x = a.slice().sort();
  const y = b.slice().sort();
  return x.length === y.length && x.every((v, i) => v === y[i]);
}

function quotedArray(text, startRe) {
  const m = startRe.exec(text);
  if (!m) return null;
  const block = text.slice(m.index, m.index + 1600);
  const end = block.indexOf("};");
  const body = end < 0 ? block : block.slice(0, end);
  return (body.match(/"([^"]+)"/g) || []).map(s => s.slice(1, -1));
}

/** 三份资源清单 + 磁盘文件 + 构建门禁，必须与事实源一致。 */
function verifyUiAssets(errors) {
  const canonical = UI_ASSETS.slice();

  // 1) build.ps1 的 /resource: 清单
  const build = read("build.ps1");
  const embedded = (build.match(/"GreenTiger\.ui\.([^"]+)"/g) || [])
    .map(s => s.replace(/^"GreenTiger\.ui\./, "").replace(/"$/, ""));
  if (!sameSet(embedded, canonical))
    errors.push("build.ps1 的 $resources 与 UI_ASSETS 不一致：\n    构建=" + embedded.slice().sort().join(", ") +
      "\n    事实源=" + canonical.slice().sort().join(", "));

  // 2) App.ExportEmbeddedUi 的 names[]（App 已按 partial class 拆分，自动在所有 App*.cs 里找）
  let exported = null;
  for (const f of fs.readdirSync(abs("src")).filter(n => /^App.*\.cs$/.test(n))) {
    const found = quotedArray(read("src/" + f), /string\[\]\s+names\s*=\s*new\s+string\[\]\s*\{/);
    if (found !== null) { exported = found; break; }
  }
  if (exported === null) errors.push("App*.cs 找不到 ExportEmbeddedUi 的 names[] 清单");
  else if (!sameSet(exported, canonical))
    errors.push("src/App.cs 的 names[] 与 UI_ASSETS 不一致：\n    App=" + exported.slice().sort().join(", ") +
      "\n    事实源=" + canonical.slice().sort().join(", "));

  // 3) WebServer：顶层路由字面量 + ES 模块白名单
  const server = read("src/WebServer.cs");
  const topLevel = {
    "index.html": '"/index.html"',
    "theme.css": '"/theme.css"',
    "shell.js": '"/shell.js"',
    "garden.css": '"/garden.css"',
    "garden.js": '"/garden.js"',
    "style.css": '"/style.css"',
    "manifest.webmanifest": '"/manifest.webmanifest"',
    "icon-192.png": '"/icons/icon-192.png"',
    "icon-512.png": '"/icons/icon-512.png"',
    "swirl.png": '"/icons/swirl.png"',
  };
  for (const name of Object.keys(topLevel)) {
    if (server.indexOf(topLevel[name]) < 0)
      errors.push("src/WebServer.cs 缺少静态资源路由 " + topLevel[name] + "（" + name + "）");
  }
  const modules = quotedArray(server, /private\s+static\s+readonly\s+string\[\]\s+UiModules\s*=\s*\{/);
  if (modules === null) errors.push("src/WebServer.cs 找不到 UiModules 白名单");
  else {
    const canonicalModules = canonical.filter(n => n.startsWith("js/")).map(n => n.slice(3)).sort();
    if (!sameSet(modules, canonicalModules))
      errors.push("src/WebServer.cs 的 UiModules 与 UI_ASSETS 的 js/ 模块不一致：\n    白名单=" + modules.slice().sort().join(", ") +
        "\n    事实源=" + canonicalModules.join(", "));
  }

  // 4) 磁盘文件：缺一不可，也不允许孤儿
  const files = collectUiFiles().sort();
  if (!sameSet(files, canonical.slice().sort()))
    errors.push("ui/ 目录文件与 UI_ASSETS 不一致：\n    磁盘=" + files.join(", ") +
      "\n    事实源=" + canonical.slice().sort().join(", "));

  // 5) 门禁自锁：构建前必须跑 check-version
  if (build.indexOf("tools\\check-version.js") < 0 && build.indexOf("tools/check-version.js") < 0)
    errors.push("build.ps1 未调用 tools/check-version.js；版本一致性门禁被绕过");
}

/** 结构复杂度趋势告警：只提示、不阻断构建，避免为了过门禁而仓促拆分。 */
function sourceSizeWarnings() {
  const warnings = [];
  const dirs = [["src", ".cs", 2500], ["ui/js", ".js", 3000]];
  for (const [dir, ext, limit] of dirs) {
    for (const f of fs.readdirSync(abs(dir)).filter(n => n.endsWith(ext))) {
      const rel = dir + "/" + f;
      const count = read(rel).split(/\r?\n/).length;
      if (count > limit)
        warnings.push(rel + " " + count + " 行（阈值 " + limit + "），建议按 partial class / ES module 继续拆");
    }
  }
  return warnings;
}

function verify() {
  const errors = [];
  let version;
  try { version = readVersion(); }
  catch (e) { return { version: null, errors: [e.message] }; }

  for (const slot of VERSION_SLOTS) {
    let current;
    try { current = slot.inspect(read(slot.file)); }
    catch (e) { errors.push(slot.file + " 读取/解析失败：" + e.message); continue; }
    const want = slot.expected(version);
    if (current !== want)
      errors.push(slot.file + " · " + slot.label + " = " + JSON.stringify(current) +
        "，期望 " + JSON.stringify(want) + "（事实源 " + VERSION_SOURCE + " = " + version + "）");
  }

  try { verifyNoHardcodedVersion(errors); }
  catch (e) { errors.push("UI 版本占位检查失败：" + e.message); }
  try { verifyUiAssets(errors); }
  catch (e) { errors.push("UI 资源清单检查失败：" + e.message); }

  return { version, errors };
}

/** 把事实源与全部派生落点一次性改到新版本。 */
function sync(next) {
  if (!SEMVER_RE.test(next || ""))
    throw new Error("用法：node tools/bump-version.js <x.y.z>（例如 3.10.4）");
  const current = readVersion();
  const changed = [];
  const src = read(VERSION_SOURCE);
  const newSrc = src.replace(VERSION_RE, m => m.replace(current, next));
  if (newSrc !== src) { write(VERSION_SOURCE, newSrc); changed.push(VERSION_SOURCE); }

  for (const slot of VERSION_SLOTS) {
    const text = read(slot.file);
    const updated = slot.fix(text, next);
    if (updated !== text) { write(slot.file, updated); changed.push(slot.file + " · " + slot.label); }
  }
  return { from: current, to: next, changed };
}

module.exports = { ROOT, VERSION_SOURCE, UI_ASSETS, PLACEHOLDER, read, write, readVersion, sourceSizeWarnings, verify, sync };
