#!/usr/bin/env node
"use strict";
/**
 * 升版本号的唯一入口：一次改平事实源与全部派生落点，改完立刻自检。
 * 用法：npm run bump:version 3.10.4
 * 注意：改 UI 必须升版本（AGENTS.md），因为界面是程序集内嵌资源，
 *       App.ExportEmbeddedUi 按版本戳覆盖数据目录 ui\。
 */
const manifest = require("./project-manifest");

const next = process.argv[2];
let result;
try {
  result = manifest.sync(next);
} catch (e) {
  console.error("[FAIL] " + e.message);
  process.exit(1);
}

console.log("版本 " + result.from + " → " + result.to + "，已同步 " + result.changed.length + " 处：");
for (const item of result.changed) console.log("  - " + item);
console.log("");

const { errors } = manifest.verify();
if (errors.length) {
  console.error("[FAIL] 同步后仍有不一致，请手工处理：");
  for (const e of errors) console.error("  - " + e);
  process.exit(1);
}
console.log("VERSION BUMP OK · " + result.to);
