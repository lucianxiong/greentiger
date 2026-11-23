#!/usr/bin/env node
"use strict";
/**
 * 版本与资源清单一致性门禁。被 build.ps1、npm scripts、CI、一键验证共同调用。
 * 任一处不一致即非零退出，并打印精确到文件/行号的修复提示。
 * 用法：node tools/check-version.js
 */
const manifest = require("./project-manifest");

const { version, errors } = manifest.verify();
const head = "GreenTiger 一致性检查（事实源 src/Config.cs）";
console.log(head);
console.log("-".repeat(74));

if (!version) {
  console.log("[FAIL] 无法读取版本号");
  for (const e of errors) console.log("  - " + e);
  process.exit(1);
}

if (errors.length === 0) {
  console.log("[ OK ] 版本 " + version + "：程序集 / npm / Win32 manifest 全部一致");
  console.log("[ OK ] UI 无硬编码版本号，缓存戳由服务端注入 " + manifest.PLACEHOLDER);
  console.log("[ OK ] UI_ASSETS " + manifest.UI_ASSETS.length + " 项：build.ps1 / App.cs / WebServer.cs / ui\\ 四处一致");
  console.log("[ OK ] build.ps1 已挂载一致性门禁");
  const warnings = manifest.sourceSizeWarnings();
  if (warnings.length) {
    console.log("[WARN] 单体文件规模（持续拆分提示，不阻断构建）：");
    for (const w of warnings) console.log("       - " + w);
  }
  console.log("VERSION/ASSET CHECK PASSED");
  process.exit(0);
}

console.log("[FAIL] 发现 " + errors.length + " 处不一致：");
for (const e of errors) console.log("  - " + e);
console.log("");
console.log("修复：升版本用 npm run bump:version <x.y.z>；只改资源清单请同步");
console.log("      tools/project-manifest.js 的 UI_ASSETS 与 build.ps1 / App.cs / WebServer.cs。");
process.exit(1);
