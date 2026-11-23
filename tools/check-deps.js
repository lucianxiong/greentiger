/*
 * GreenTiger 依赖自检
 *
 * 用法：npm run check:deps   （或 node tools\check-deps.js）
 *
 * 这个项目刻意保持「零 NuGet、零浏览器下载、零构建系统」：
 *   · .NET 侧只用系统自带的 .NET Framework 编译器；
 *   · 前端只有一个 node 依赖 playwright-core，且复用本机 Edge，不拉 Chromium。
 * 因此依赖一缺就是「跑不起来」，而不是「降级运行」。本脚本把所有前置条件
 * 一次性列清楚：缺什么、为什么需要、怎么补。
 */
"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");

const ROOT = path.resolve(__dirname, "..");
const rows = [];
let missing = 0;
let warned = 0;

function add(level, name, detail, remedy) {
  rows.push({ level, name, detail, remedy });
  if (level === "MISS") missing++;
  if (level === "WARN") warned++;
}

function exists(p) {
  try { return fs.existsSync(p); } catch (e) { return false; }
}

/* ---------- 1. Node ---------- */
const nodeMajor = Number(process.versions.node.split(".")[0]);
if (nodeMajor >= 20) {
  add(" OK ", "Node.js", "v" + process.versions.node, "");
} else {
  add("MISS", "Node.js", "v" + process.versions.node + "（需要 >= 20）", "安装 Node 20 LTS 或更高版本（playwright-core 1.64 要求 Node 20）");
}

/* ---------- 2. playwright-core（唯一的 node 依赖） ---------- */
let pwVersion = "";
try {
  pwVersion = require("playwright-core/package.json").version;
  add(" OK ", "playwright-core", pwVersion, "");
} catch (e) {
  add("MISS", "playwright-core", "未安装", "在仓库根目录运行 npm install");
}

/* ---------- 3. 本机浏览器（playwright-core 不带浏览器） ---------- */
const EDGE_CANDIDATES = [
  path.join(process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)", "Microsoft", "Edge", "Application", "msedge.exe"),
  path.join(process.env.ProgramFiles || "C:\\Program Files", "Microsoft", "Edge", "Application", "msedge.exe")
];
const CHROME_CANDIDATES = [
  path.join(process.env.ProgramFiles || "C:\\Program Files", "Google", "Chrome", "Application", "chrome.exe"),
  path.join(process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)", "Google", "Chrome", "Application", "chrome.exe")
];
const edge = EDGE_CANDIDATES.find(exists);
const chrome = CHROME_CANDIDATES.find(exists);
if (process.platform !== "win32") {
  add("WARN", "浏览器", "非 Windows 平台：界面端到端检查不可用", "ui-e2e.js 依赖本机 Edge，仅 Windows");
} else if (edge) {
  add(" OK ", "Microsoft Edge", edge, "");
} else if (chrome) {
  add("WARN", "Microsoft Edge", "未找到，但检测到 Chrome", "ui-e2e.js 的 channel 需要改成 chrome");
} else {
  add("MISS", "Microsoft Edge", "未找到", "安装 Edge（系统自带），或改 ui-e2e.js 的 channel 为 chrome");
}

/* ---------- 4. .NET Framework 编译器 ---------- */
const FRAMEWORK_DIRS = [
  path.join(process.env.WINDIR || "C:\\Windows", "Microsoft.NET", "Framework64", "v4.0.30319"),
  path.join(process.env.WINDIR || "C:\\Windows", "Microsoft.NET", "Framework", "v4.0.30319")
];
const compiler = FRAMEWORK_DIRS
  .map((d) => path.join(d, "csc.exe"))
  .find(exists);
if (process.platform !== "win32") {
  add("WARN", "C# 编译器", "非 Windows 平台：无法构建桌面程序", "构建需要 Windows + .NET Framework 4.x");
} else if (compiler) {
  add(" OK ", "C# 编译器", compiler, "");
} else {
  add("MISS", "C# 编译器", ".NET Framework 4.x 未找到", "启用 Windows 功能「.NET Framework 4.x」后重试");
}

/* ---------- 5. WebView2 托管程序集（绿虎浏览器窗口） ---------- */
const WV_DIR = path.join(ROOT, "lib", "WebView2");
const wvFiles = ["Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll"];
const wvMissing = wvFiles.filter((f) => !exists(path.join(WV_DIR, f)));
if (wvMissing.length === 0) {
  add(" OK ", "WebView2 程序集", "lib/WebView2（3/3）", "");
} else if (wvMissing.length === wvFiles.length) {
  add("WARN", "WebView2 程序集", "未提供：构建会跳过「绿虎浏览器」窗口", "放置 Core/WinForms/Loader 三个 DLL 到 lib/WebView2");
} else {
  add("MISS", "WebView2 程序集", "不完整，缺：" + wvMissing.join("、"), "补齐 lib/WebView2 下的三个 DLL，否则构建会直接失败");
}

/* ---------- 6. 构建产物 ---------- */
const EXE = path.join(ROOT, "dist", "GreenTiger.exe");
if (exists(EXE)) {
  const mb = (fs.statSync(EXE).size / 1024).toFixed(1);
  add(" OK ", "dist/GreenTiger.exe", mb + " KB", "");
} else {
  add("WARN", "dist/GreenTiger.exe", "尚未构建", "运行 npm run build");
}

/* ---------- 输出 ---------- */
const pad = (s, n) => (s + " ".repeat(Math.max(0, n - [...s].reduce((w, c) => w + (c.charCodeAt(0) > 255 ? 2 : 1), 0))));
console.log("");
console.log("GreenTiger 依赖自检 · " + (process.platform === "win32" ? "Windows" : process.platform));
console.log("-".repeat(78));
for (const r of rows) {
  console.log("[" + r.level + "] " + pad(r.name, 22) + pad(r.detail, 34));
  if (r.remedy) console.log("        → " + r.remedy);
}
console.log("-".repeat(78));
if (missing === 0 && warned === 0) {
  console.log("全部就绪。可以 npm run build / npm test / npm run e2e。");
} else if (missing === 0) {
  console.log("可用，但有 " + warned + " 项可选项缺失（见 WARN）。");
} else {
  console.log("有 " + missing + " 项必需依赖缺失，请按上面的提示补齐。");
}
process.exit(missing === 0 ? 0 : 1);
