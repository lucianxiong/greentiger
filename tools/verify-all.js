#!/usr/bin/env node
"use strict";
/**
 * 一键全量验证（发布前唯一入口）。按 AGENTS.md 的顺序串起所有检查：
 *   check:version → check:deps → check:ui → npm test（含构建 + --self-test + 冒烟 + 恢复 + 评测 + 影子）
 *   → e2e（真机 Edge）→ shot（截图）
 * 任一步失败即停，并把逐步证据写到 %TEMP%\GreenTiger\verify-all-report.txt。
 * 用法：npm run verify:all
 */
const { spawnSync } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const root = path.resolve(__dirname, "..");
const reportDir = path.join(os.tmpdir(), "GreenTiger");
const reportPath = path.join(reportDir, "verify-all-report.txt");

// 直接用当前 Node 解释器调用工具脚本：Windows 下不经 cmd.exe / npm.cmd，
// 避免 shell 差异，也不依赖 PATH 里能否解析 npm。每一步仍标注等价的 npm 命令。
const NODE_SCRIPTS = {
  "check:version": "tools/check-version.js",
  "check:deps": "tools/check-deps.js",
  "check:ui": "tools/_check_inline_ui.js",
  regression: "tools/regression.js",
  e2e: "tools/ui-e2e.js",
  shot: "tools/ui-shot.js",
};

function npmRun(script) {
  return { cmd: process.execPath, args: [NODE_SCRIPTS[script]] };
}
function npmTest() {
  return { cmd: process.execPath, args: [NODE_SCRIPTS.regression] };
}

const steps = [
  {
    name: "版本与资源清单一致性", script: "npm run check:version", ...npmRun("check:version"),
    expect: /VERSION\/ASSET CHECK PASSED/,
  },
  {
    name: "依赖自检", script: "npm run check:deps", ...npmRun("check:deps"),
    expect: /全部就绪/,
  },
  {
    name: "界面静态检查", script: "npm run check:ui", ...npmRun("check:ui"),
    expect: /inline UI OK/,
  },
  {
    name: "完整回归（build + --self-test + smoke + recovery + eval + shadow）",
    script: "npm test（等价：node tools/regression.js）", ...npmTest(), expect: /REGRESSION OK/,
  },
  {
    name: "界面端到端（真机 Edge）", script: "npm run e2e", ...npmRun("e2e"),
    validate: out => /\d+\/\d+ 通过/.test(out) && !/失败项：|E2E 失败/.test(out),
  },
  {
    name: "界面截图", script: "npm run shot", ...npmRun("shot"),
  },
];

const lines = [];
const record = s => { console.log(s); lines.push(s); };
const started = Date.now();
record("GreenTiger 全量验证 · " + new Date().toISOString());
record("=".repeat(78));

let failedAt = null;
const results = [];
for (let i = 0; i < steps.length; i++) {
  const step = steps[i];
  const t0 = Date.now();
  record("[" + (i + 1) + "/" + steps.length + "] " + step.name + "  ·  " + step.script);
  const r = spawnSync(step.cmd, step.args, { cwd: root, encoding: "utf8", timeout: 900000 });
  const out = (r.stdout || "") + (r.stderr || "");
  const seconds = ((Date.now() - t0) / 1000).toFixed(1);
  const ok = r.status === 0 &&
    (!step.expect || step.expect.test(out)) &&
    (!step.validate || step.validate(out));
  results.push({ name: step.name, ok, seconds, status: r.status });
  record("    " + (ok ? "PASS" : "FAIL") + "  exit=" + r.status + "  " + seconds + "s");
  if (!ok) {
    failedAt = step;
    record("---- 输出末尾 ----");
    record(out.trim().split(/\r?\n/).slice(-30).join("\n"));
    record("------------------");
    break;
  }
}

record("");
record("步骤汇总：");
for (const r of results) record("  " + (r.ok ? "PASS" : "FAIL") + "  " + r.name + "  (" + r.seconds + "s)");
record("总耗时 " + ((Date.now() - started) / 1000).toFixed(1) + "s");
record(failedAt ? "VERIFY FAILED · 停在 " + failedAt.name : "VERIFY OK · " + results.length + "/" + steps.length + " 步全部通过");
record("证据文件：" + reportPath);

try {
  fs.mkdirSync(reportDir, { recursive: true });
  fs.writeFileSync(reportPath, lines.join("\n") + "\n", "utf8");
} catch (e) {
  console.error("（证据文件写入失败：" + e.message + "）");
}

process.exit(failedAt ? 1 : 0);
