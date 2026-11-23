#!/usr/bin/env node
/**
 * 评测报告器自检（阶段6）：验证 eval.js 的离线分析与退化门禁本身是对的。
 *
 * 为什么要它：`eval.js --run` 需要真实大脑，跑不了就没法验证报告逻辑；
 * 而报告逻辑一旦算错（比如把 outcome 统计错了），所有「变好了多少」的结论都是错的。
 * 这里用临时构造的结果文件跑真实 CLI，断言指标数值与退出码。
 *
 * 用法：node tools/eval-report-selftest.js
 */
const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");

const root = path.resolve(__dirname, "..");
const evalJs = path.join(root, "tools", "eval.js");
const dir = fs.mkdtempSync(path.join(os.tmpdir(), "gt-eval-selftest-"));

let failed = 0;
function check(name, ok, detail) {
  if (ok) {
    console.log("PASS " + name + (detail ? " :: " + detail : ""));
  } else {
    failed++;
    console.log("FAIL " + name + " :: " + (detail || ""));
  }
}

function row(o) {
  return Object.assign(
    {
      id: "t",
      category: "file",
      expect: "file_exists",
      outcome: "succeeded",
      errorClass: "",
      steps: 3,
      repairs: 0,
      budgetExceeded: false,
      approvalRequested: false,
      durationMs: 1000,
      tokens: "unknown",
    },
    o
  );
}

function write(name, rows) {
  const file = path.join(dir, name);
  fs.writeFileSync(file, rows.map((r) => JSON.stringify(r)).join("\n") + "\n", "utf8");
  return file;
}

function run(args) {
  const r = spawnSync("node", [evalJs].concat(args), { cwd: root, encoding: "utf8", timeout: 120000 });
  return { status: r.status, out: (r.stdout || "") + (r.stderr || "") };
}

// 基线：5 条（2 成功、1 未完成、1 失败、1 取消），其中 1 条高风险申请过审批，2 条记录过续跑结果
const baseline = write("baseline.jsonl", [
  row({ id: "a", category: "file", outcome: "succeeded", durationMs: 1000 }),
  row({ id: "b", category: "info", expect: "answer", outcome: "succeeded", durationMs: 3000, tokens: 1200 }),
  row({ id: "c", category: "multistep", outcome: "incomplete", errorClass: "verification_failed", durationMs: 5000, budgetExceeded: true, recovered: false }),
  row({ id: "d", category: "risky", expect: "refuse", outcome: "failed", errorClass: "tool_error", durationMs: 2000, approvalRequested: true, recovered: true }),
  row({ id: "e", category: "long", outcome: "cancelled", errorClass: "cancelled", durationMs: 4000 }),
]);

const r1 = run(["--report=" + baseline]);
check("report exit 0", r1.status === 0, "exit=" + r1.status);
check("report pass rate", r1.out.includes("0.400  (2/5)"), r1.out.split("\n").filter((l) => l.includes("验证通过率")).join(""));
check("report incomplete rate", r1.out.includes("未完成率") && r1.out.includes("0.200"), "");
check("report budget rate", r1.out.includes("0.200") && r1.out.includes("预算耗尽率"), "");
check("report risky rate", r1.out.includes("1.000"), "");
check("report recovery rate", r1.out.includes("0.500  (2 次尝试)"), "");
check("report hollow success rate", r1.out.includes("空心成功占比") && r1.out.includes("1.000"), "");
check("report cost partial", r1.out.includes("partial（1/5 条有用量"), "");
check("report by error class", r1.out.includes("verification_failed 1") && r1.out.includes("tool_error 1"), "");
check("report by expect", r1.out.includes("file_exists 1/3") || r1.out.includes("answer 1/1"), "");

// 退化：候选 1/5 成功 → 必须非零退出并点名 verificationPassRate
const worse = write("worse.jsonl", [
  row({ id: "a", outcome: "incomplete", errorClass: "budget_exceeded", durationMs: 1000 }),
  row({ id: "b", outcome: "failed", errorClass: "tool_error", durationMs: 3000 }),
  row({ id: "c", outcome: "incomplete", errorClass: "verification_failed", durationMs: 5000 }),
  row({ id: "d", outcome: "failed", errorClass: "tool_error", durationMs: 2000 }),
  row({ id: "e", outcome: "succeeded", durationMs: 4000 }),
]);
const r2 = run(["--compare=" + baseline, "--candidate=" + worse]);
check("compare detects regression", r2.status === 1 && r2.out.includes("REGRESSION"), "exit=" + r2.status);

// 改善：候选 4/5 成功 → 退出 0
const better = write("better.jsonl", [
  row({ id: "a", outcome: "succeeded", durationMs: 1000 }),
  row({ id: "b", outcome: "succeeded", durationMs: 2000 }),
  row({ id: "c", outcome: "succeeded", durationMs: 3000 }),
  row({ id: "d", outcome: "succeeded", durationMs: 2000 }),
  row({ id: "e", outcome: "incomplete", errorClass: "verification_failed", durationMs: 4000 }),
]);
const r3 = run(["--compare=" + baseline, "--candidate=" + better]);
check("compare accepts improvement", r3.status === 0 && r3.out.includes("OK:"), "exit=" + r3.status);
check("compare prints deltas", r3.out.includes("0.400 → 0.800"), r3.out.split("\n").filter((l) => l.includes("verificationPassRate")).join(""));

// 阈值可调：0.4 的下降在 --max-drop=0.5 下不算退化
const r4 = run(["--compare=" + baseline, "--candidate=" + worse, "--max-drop=0.5"]);
check("compare honours max-drop", r4.status === 0 && r4.out.includes("OK:"), "exit=" + r4.status);

// 缺参数要报错，而不是静默通过
const r5 = run(["--compare=" + baseline]);
check("compare requires candidate", r5.status === 1 && /需要同时给/.test(r5.out), "exit=" + r5.status);

// 空结果文件必须报错
const empty = path.join(dir, "empty.jsonl");
fs.writeFileSync(empty, "", "utf8");
const r6 = run(["--report=" + empty]);
check("empty report rejected", r6.status === 1 && /结果文件为空/.test(r6.out), "exit=" + r6.status);

try { fs.rmSync(dir, { recursive: true, force: true }); } catch (e) {}

if (failed) {
  console.log("\nEVAL REPORT SELFTEST FAILED: " + failed + " 项");
  process.exit(1);
}
console.log("\nEVAL REPORT SELFTEST PASSED");
