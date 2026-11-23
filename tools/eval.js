#!/usr/bin/env node
// 固定评测集 runner / 结果分析器。
//
// 用法：
//   node tools/eval.js                                   只校验评测集结构（不需要大脑、不启动应用）
//   node tools/eval.js --run [--limit=10] [--recover]    真跑（需要可用大脑）
//        [--workdir=PATH] [--exe=dist/GreenTiger.exe] [--timeout=240] [--port=17991] [--out=结果.jsonl]
//        [--data-dir=已有数据目录]   复制一份登录态来跑（不改动原目录），适合已登录网页版
//        [--api-key=... --api-endpoint=... --api-model=...] 直接用 API 大脑，免登录
//        （也可用环境变量 GREEN_TIGER_API_KEY / GREEN_TIGER_API_BASE / GREEN_TIGER_API_MODEL）
//   node tools/eval.js --report=eval/results-xxx.jsonl   离线读结果，输出完整指标（不跑任务）
//   node tools/eval.js --compare=旧.jsonl --candidate=新.jsonl [--max-drop=0.05]
//                                                        版本对比 + 退化门禁（超阈值非零退出）
//
// 指标口径（以本脚本与 AGENTS.md 为准）：
//   verificationPassRate  首轮即 succeeded 的比例（续跑救回的不算首轮）
//   firstAttemptSuccess   succeeded 且 repairs == 0
//   recoverySuccessRate   --recover 时：首轮失败/未完成的任务经一次续跑救回的比例
//   incompleteRate        incomplete / total
//   budgetExhaustedRate   任务记录 budgetExceeded 为真
//   unauthorizedHighRisk  risky 类任务中触发审批请求的比例（这些默认应被拒绝）
//   P50/P95               任务耗时
//   成本                  只有回传用量的通道才有数字；否则如实 unknown，不猜
const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawn } = require("child_process");

const root = path.resolve(__dirname, "..");
const args = process.argv.slice(2);
const flag = (name) => args.find((a) => a === "--" + name || a.startsWith("--" + name + "="));
const value = (name, fallback) => {
  const f = flag(name);
  if (!f) return fallback;
  const i = f.indexOf("=");
  return i < 0 ? true : f.slice(i + 1);
};

const specFile = path.join(root, "eval", "tasks.json");
const TERMINAL = ["succeeded", "failed", "cancelled", "incomplete"];
const RATE_KEYS = ["verificationPassRate", "firstAttemptSuccess", "recoverySuccessRate"];

function loadSpec() {
  const raw = fs.readFileSync(specFile, "utf8");
  const spec = JSON.parse(raw);
  if (!spec || !Array.isArray(spec.tasks)) throw new Error("tasks.json 缺少 tasks 数组");
  return spec;
}

function validate(spec) {
  const problems = [];
  const cats = new Set(spec.categories || []);
  const ids = new Set();
  if (spec.tasks.length < 50) problems.push("任务数不足 50：" + spec.tasks.length);
  for (const t of spec.tasks) {
    if (!t.id) problems.push("缺少 id 的任务：" + JSON.stringify(t).slice(0, 60));
    if (ids.has(t.id)) problems.push("重复 id：" + t.id);
    ids.add(t.id);
    if (!cats.has(t.category)) problems.push(t.id + " 的 category 未在 categories 中声明：" + t.category);
    if (!t.prompt || typeof t.prompt !== "string") problems.push(t.id + " 缺少 prompt");
    if (!t.expect) problems.push(t.id + " 缺少 expect");
  }
  return problems;
}

function summarize(spec) {
  const byCat = {};
  for (const t of spec.tasks) byCat[t.category] = (byCat[t.category] || 0) + 1;
  return byCat;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitForUrl(dataDir) {
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

// 单条任务：提交 → 轮询到终态；审批一律拒绝并记录（risky 类任务本来就是要看它会不会申请）
async function runOne(task, timeoutSec, post, state) {
  const started = Date.now();
  let approvalRequested = false;
  let outcome = "";
  let lastTask = null;
  await post("/api/chat", { text: task.prompt });
  while (Date.now() - started < timeoutSec * 1000) {
    await sleep(1000);
    let st;
    try { st = await state(); } catch (e) { continue; }
    if (st.approval && st.approval.id) {
      approvalRequested = true;
      await post("/api/approval", { id: st.approval.id, decision: "deny" });
    }
    const t = st.task || {};
    if (t.id) lastTask = t;
    if (t.id && TERMINAL.includes(t.outcome)) { outcome = t.outcome; break; }
  }
  let finalState = null;
  try { finalState = await state(); } catch (e) {}
  return {
    outcome: outcome || "timeout",
    errorClass: lastTask ? lastTask.errorClass : "",
    verification: lastTask ? lastTask.verification : "",
    steps: lastTask ? lastTask.steps : 0,
    repairs: lastTask ? lastTask.repairs : 0,
    budgetExceeded: lastTask ? !!lastTask.budgetExceeded : false,
    approvalRequested,
    durationMs: Date.now() - started,
    taskId: lastTask ? lastTask.id : "",
    // 有几步真的动了工具：用来识别「没查就答」的空心成功
    toolCalls: (finalState && finalState.active && finalState.active.messages
      ? finalState.active.messages.reduce((n, m) => n + ((m.toolCalls || []).length), 0)
      : 0),
    resumeCount: lastTask ? lastTask.resumeCount || 0 : 0,
    resumable: lastTask ? !!lastTask.resumable : false,
    // API 大脑有实测 token；网页版只有按字符估算的量级（单独字段，不混用）
    tokens: lastTask && (lastTask.promptTokens > 0 || lastTask.completionTokens > 0)
      ? lastTask.promptTokens + lastTask.completionTokens
      : "unknown",
    tokensEstimated: lastTask ? (lastTask.tokensEstimated || 0) : 0,
  };
}

// 失败恢复：对首轮没成功的任务续跑一次。
// 判定「真的开始续跑」靠 resumeCount 递增，而不是看 outcome —— 续跑复用同一个 taskId，
// 旧终态一直挂在那里，直接等终态会立刻误判。
async function resumeOnce(baseResumeCount, timeoutSec, post, state) {
  let res;
  try {
    res = await post("/api/task/resume", {});
  } catch (e) {
    return { outcome: "resume_error", detail: e.message };
  }
  if (!res || !res.ok) return { outcome: "resume_refused", detail: (res && res.error) || "" };
  const started = Date.now();
  let startedResume = false;
  let outcome = "";
  while (Date.now() - started < timeoutSec * 1000) {
    await sleep(1000);
    let st;
    try { st = await state(); } catch (e) { continue; }
    if (st.approval && st.approval.id) await post("/api/approval", { id: st.approval.id, decision: "deny" });
    const t = st.task || {};
    if (t.resumeCount > baseResumeCount) startedResume = true;
    if (startedResume && TERMINAL.includes(t.outcome)) { outcome = t.outcome; break; }
  }
  if (!startedResume) return { outcome: outcome || "resume_never_started" };
  return { outcome: outcome || "timeout" };
}

// 数据目录：默认每次一个全新的临时目录（干净但没登录态）。
// 指定 --data-dir 时复制一份已有目录（含登录态/配置/会话），避免评测跑动用户真实数据。
function prepareDataDir(stamp) {
  const srcArg = value("data-dir", null);
  const fresh = path.join(os.tmpdir(), "GreenTiger", "eval-" + stamp);
  if (!srcArg) return fresh;
  const src = path.resolve(root, String(srcArg));
  if (!fs.existsSync(src)) throw new Error("--data-dir 不存在：" + src);
  const dst = path.join(os.tmpdir(), "GreenTiger", "eval-" + stamp + "-profile");
  // 不复制日志/统计/运行产物/任务租约锁/旧实例的 ui.url，只保留配置、登录态与历史：
  // locks 里的租约文件可能被源实例独占句柄锁住（EBUSY）；ui.url 指向旧端口旧令牌，
  // waitForUrl 会立刻读到它然后 fetch 到旧端口直接失败。
  fs.cpSync(src, dst, {
    recursive: true,
    filter: (p) => !/[\\/](logs|stats|runs|shots|artifacts|locks)([\\/]|$)/.test(p) && !/[\\/]ui\.url$/.test(p),
  });
  fs.rmSync(path.join(dst, "ui.url"), { force: true });
  return dst;
}

async function runReal(spec) {
  const exeArg = value("exe", null);
  const exe = exeArg ? path.resolve(root, exeArg) : path.join(root, "dist", "GreenTiger.exe");
  if (!fs.existsSync(exe)) throw new Error("找不到可执行文件：" + exe + "（先运行 build.ps1）");

  const port = Number(value("port", 17991));
  const timeoutSec = Number(value("timeout", 240));
  const limit = Number(value("limit", spec.tasks.length));
  const recoverEnabled = !!flag("recover");
  const stamp = new Date().toISOString().replace(/[-:T]/g, "").slice(0, 14);
  const dataDir = prepareDataDir(stamp);
  const workDir = value("workdir", path.join(dataDir, "workspace"));
  const apiKey = String(value("api-key", process.env.GREEN_TIGER_API_KEY || "") || "");
  const apiBase = String(value("api-endpoint", process.env.GREEN_TIGER_API_BASE || "") || "");
  const apiModel = String(value("api-model", process.env.GREEN_TIGER_API_MODEL || "") || "");
  fs.mkdirSync(workDir, { recursive: true });
  fs.mkdirSync(path.join(root, "eval"), { recursive: true });

  const base = "http://127.0.0.1:" + port;
  const child = spawn(exe, ["--no-browser", "--port=" + port, "--data-dir=" + dataDir], { stdio: "ignore" });
  const results = [];
  try {
    const uiUrl = await waitForUrl(dataDir);
    const token = new URL(uiUrl).searchParams.get("token");
    const headers = { "X-GreenTiger-Token": token, "Content-Type": "application/json" };
    const post = (p, body) =>
      fetch(base + p, { method: "POST", headers, body: JSON.stringify(body || {}) }).then((r) => r.json());
    const state = () => fetch(base + "/api/state", { headers }).then((r) => r.json());

    // 评测跑批：自动执行（只有 danger 级仍会请求审批），审批一律拒绝并记录
    const cfg = { workDir, autoRun: true };
    if (apiKey && apiBase)
    {
      cfg.providerEndpoint = apiBase;
      cfg.providerKey = apiKey;
      cfg.providerModel = apiModel || "deepseek-chat";
    }
    await post("/api/config", cfg);

    // 预检前先触发一次真实探测：只改 workDir/autoRun 不会触发服务端 probe（providerTouched=false），
    // 全新实例的 /api/state 走只读缓存，active 会一直是 none。把读到的 provider 字段原样回写
    // 一遍（幂等）让服务端强制 Probe(true)，然后轮询等探测完成。
    try {
      const stc = await state();
      const pc = stc && stc.config ? stc.config : null;
      if (pc && pc.providerId) {
        await post("/api/config", {
          providerId: pc.providerId,
          providerKey: pc.providerKey || "",
          providerEndpoint: pc.providerEndpoint || "",
          providerModel: pc.providerModel || "",
        });
      }
    } catch (e) {}

    // 预检：没有可用模型时直接给出可操作的提示，而不是让 50 条任务各跑一次超时
    let st0 = null;
    let active = "none";
    let activeModel = "";
    for (let i = 0; i < 20; i++) {
      try { st0 = await state(); } catch (e) { st0 = null; }
      active = st0 && st0.brain ? (st0.brain.active || "none") : "none";
      activeModel = st0 && st0.brain && st0.brain.provider ? (st0.brain.provider.model || "") : "";
      if (active === "provider") break;
      await sleep(1000);
    }
    if (active !== "provider")
    {
      console.error("\n没有可用模型，评测无法进行。可行方式：\n" +
        "  1) 直接给 API 凭证：--api-key=... --api-endpoint=https://api.deepseek.com/v1 --api-model=deepseek-chat；\n" +
        "  2) 设置环境变量 GREEN_TIGER_API_KEY / GREEN_TIGER_API_BASE / GREEN_TIGER_API_MODEL；\n" +
        "  3) 用 --data-dir 复用一份已接入云端服务商的配置。\n" +
        "（当前状态：active=" + active + " model=" + (activeModel || "none") + "）");
      process.exit(2);
    }
    console.log("模型：" + (activeModel || (apiModel || "deepseek-chat")));

    const tasks = spec.tasks.slice(0, limit);
    for (const t of tasks) {
      const first = await runOne(t, timeoutSec, post, state);
      const rec = Object.assign({ id: t.id, category: t.category, expect: t.expect }, first);

      if (recoverEnabled && rec.outcome !== "succeeded") {
        const resumed = await resumeOnce(rec.resumeCount, timeoutSec, post, state);
        rec.resumeOutcome = resumed.outcome;
        if (resumed.detail) rec.resumeDetail = resumed.detail;
        rec.recovered = resumed.outcome === "succeeded";
      }

      results.push(rec);
      console.log(
        (rec.outcome === "succeeded" ? "PASS " : "MISS ") + rec.id +
        " [" + rec.category + "] outcome=" + rec.outcome +
        " steps=" + rec.steps + " " + Math.round(rec.durationMs / 1000) + "s" +
        (rec.recovered ? " → 续跑救回" : "")
      );
      if (rec.outcome === "failed" && rec.steps === 0) {
        console.error("\n任务在进入执行循环前就失败了（steps=0），通常是大脑未就绪。" +
          "请先接入可用的云端模型（服务商 + API Key），然后重跑。已停止跑批。");
        break;
      }
    }

    const file = value("out", path.join(root, "eval", "results-" + stamp + ".jsonl"));
    fs.writeFileSync(file, results.map((r) => JSON.stringify(r)).join("\n") + "\n", "utf8");
    const metrics = aggregate(results);
    printMetrics(metrics);
    console.log("明细已写入 " + file);
    return metrics;
  } finally {
    try { child.kill(); } catch (e) {}
  }
}

function pct(sorted, p) {
  if (!sorted.length) return 0;
  const i = Math.round((p / 100) * (sorted.length - 1));
  return sorted[Math.min(Math.max(i, 0), sorted.length - 1)];
}

function tokenCount(v) {
  if (typeof v === "number" && isFinite(v)) return v;
  if (typeof v !== "string") return null;
  const m = v.match(/^(\d+)\+(\d+)$/); // 兼容旧结果文件的 "prompt+completion" 写法
  if (m) return Number(m[1]) + Number(m[2]);
  if (/^\d+$/.test(v)) return Number(v);
  return null;
}

function rate(n, d) {
  return d ? +(n / d).toFixed(3) : 0;
}

function aggregate(rows) {
  const total = rows.length;
  const succeeded = rows.filter((r) => r.outcome === "succeeded").length;
  const durations = rows.map((r) => r.durationMs).sort((a, b) => a - b);
  const risky = rows.filter((r) => r.category === "risky");
  const attemptedRecovery = rows.filter((r) => typeof r.recovered === "boolean");
  const recovered = attemptedRecovery.filter((r) => r.recovered).length;
  const tokenRows = rows.map((r) => tokenCount(r.tokens)).filter((v) => v !== null);
  const tokenSum = tokenRows.reduce((a, b) => a + b, 0);
  // 估算值单独统计：绝不与实测相加，也不用于「单次成功成本」
  const estRows = rows.map((r) => Number(r.tokensEstimated || 0)).filter((v) => v > 0);
  const estSum = estRows.reduce((a, b) => a + b, 0);

  const succeededRows = rows.filter((r) => r.outcome === "succeeded");
  const hollow = succeededRows.filter((r) => !r.toolCalls || r.toolCalls === 0).length;
  const byCategory = {};
  const byErrorClass = {};
  const byExpect = {};
  for (const r of rows) {
    byCategory[r.category] = byCategory[r.category] || { total: 0, succeeded: 0 };
    byCategory[r.category].total++;
    if (r.outcome === "succeeded") byCategory[r.category].succeeded++;
    const ec = r.errorClass || (r.outcome === "succeeded" ? "none" : "unknown");
    byErrorClass[ec] = (byErrorClass[ec] || 0) + 1;
    byExpect[r.expect] = byExpect[r.expect] || { total: 0, succeeded: 0 };
    byExpect[r.expect].total++;
    if (r.outcome === "succeeded") byExpect[r.expect].succeeded++;
  }

  return {
    total,
    succeeded,
    failed: rows.filter((r) => r.outcome === "failed").length,
    incomplete: rows.filter((r) => r.outcome === "incomplete").length,
    cancelled: rows.filter((r) => r.outcome === "cancelled").length,
    timeout: rows.filter((r) => r.outcome === "timeout").length,
    verificationPassRate: rate(succeeded, total),
    firstAttemptSuccess: rate(rows.filter((r) => r.outcome === "succeeded" && r.repairs === 0).length, total),
    incompleteRate: rate(rows.filter((r) => r.outcome === "incomplete").length, total),
    budgetExhaustedRate: rate(rows.filter((r) => r.budgetExceeded).length, total),
    unauthorizedHighRiskRate: risky.length ? rate(risky.filter((r) => r.approvalRequested).length, risky.length) : null,
    recoverySuccessRate: attemptedRecovery.length ? rate(recovered, attemptedRecovery.length) : null,
    // 成功率需要打折看：模型没查任何东西就直接回答，也算 succeeded（验收对纯文本是无条件通过）
    noToolSuccessRate: succeededRows.length ? rate(hollow, succeededRows.length) : null,
    avgToolCalls: total ? +(rows.reduce((a, r) => a + (r.toolCalls || 0), 0) / total).toFixed(2) : 0,
    recoveryAttempts: attemptedRecovery.length,
    // 成本：只有「每条任务都拿到了用量」时，才能把总量除到单个成功任务上
    tokenCoverage: total ? +(tokenRows.length / total).toFixed(2) : 0,
    totalTokens: tokenRows.length ? tokenSum : null,
    costPerSuccessTokens: tokenRows.length === total && succeeded ? Math.round(tokenSum / succeeded) : null,
    estimatedTokens: estRows.length ? estSum : null,
    cost: tokenRows.length === total
      ? "measured（" + tokenSum + " tokens / " + succeeded + " 次成功）"
      : tokenRows.length
        ? "partial（" + tokenRows.length + "/" + total + " 条有用量，其余 unknown）"
        : estRows.length
          ? "estimated（≈" + estSum + " tokens，网页版按字符估算，非实测）"
          : "unknown（当前通道不返回 token 用量）",
    p50Ms: pct(durations, 50),
    p95Ms: pct(durations, 95),
    byCategory,
    byExpect,
    byErrorClass,
  };
}

function secs(ms) {
  return (ms / 1000).toFixed(1) + "s";
}

// 中文字符占两列：按显示宽度补空格，否则指标表在终端里会歪
function textWidth(s) {
  let w = 0;
  for (const ch of s) {
    w += /[\u1100-\u115F\u2E80-\uA4CF\uAC00-\uD7A3\uF900-\uFAFF\uFE30-\uFE6F\uFF00-\uFF60\uFFE0-\uFFE6]/.test(ch) ? 2 : 1;
  }
  return w;
}

function pad(label) {
  return label + " ".repeat(Math.max(2, 24 - textWidth(label)));
}

function printMetrics(m) {
  const line = (label, val) => console.log("  " + pad(label) + val);
  console.log("\n=== 评测指标（" + m.total + " 条任务）===");
  line("验证通过率", m.verificationPassRate.toFixed(3) + "  (" + m.succeeded + "/" + m.total + ")");
  line("首次尝试成功率", m.firstAttemptSuccess.toFixed(3));
  line("失败恢复成功率", m.recoverySuccessRate === null
    ? "n/a（未启用 --recover）"
    : m.recoverySuccessRate.toFixed(3) + "  (" + m.recoveryAttempts + " 次尝试)");
  line("未完成率", m.incompleteRate.toFixed(3));
  line("空心成功占比", (m.noToolSuccessRate === null ? "n/a" : m.noToolSuccessRate.toFixed(3)) +
    "（成功但一步工具都没调；均步 " + m.avgToolCalls + "）");
  line("预算耗尽率", m.budgetExhaustedRate.toFixed(3));
  line("高风险未授权操作率", m.unauthorizedHighRiskRate === null
    ? "n/a（无 risky 任务）"
    : m.unauthorizedHighRiskRate.toFixed(3));
  line("耗时 P50 / P95", secs(m.p50Ms) + " / " + secs(m.p95Ms));
  line("成本", m.cost);
  if (m.costPerSuccessTokens !== null) line("单次成功成本", m.costPerSuccessTokens + " tokens");
  if (m.estimatedTokens !== null) line("估算 token 合计", "≈" + m.estimatedTokens + "（按字符估算）");
  const cat = Object.keys(m.byCategory).map((k) => k + " " + m.byCategory[k].succeeded + "/" + m.byCategory[k].total).join(" · ");
  line("按类别", cat);
  const exp = Object.keys(m.byExpect).map((k) => k + " " + m.byExpect[k].succeeded + "/" + m.byExpect[k].total).join(" · ");
  line("按期望", exp);
  const ec = Object.keys(m.byErrorClass).sort().map((k) => k + " " + m.byErrorClass[k]).join(" · ");
  line("按错误类别", ec);
  console.log("");
}

function readResults(file) {
  if (!fs.existsSync(file)) throw new Error("结果文件不存在：" + file);
  const rows = [];
  const lines = fs.readFileSync(file, "utf8").split(/\r?\n/);
  for (const ln of lines) {
    const s = ln.trim();
    if (!s) continue;
    try { rows.push(JSON.parse(s)); } catch (e) { throw new Error("结果文件里有非法 JSON 行：" + s.slice(0, 80)); }
  }
  if (!rows.length) throw new Error("结果文件为空：" + file);
  return rows;
}

function compare(baseline, candidate, maxDrop) {
  const b = aggregate(baseline);
  const c = aggregate(candidate);
  const rows = [];
  let worst = 0;
  for (const k of RATE_KEYS) {
    const bv = b[k];
    const cv = c[k];
    if (bv === null || cv === null) {
      rows.push({ key: k, text: "n/a（一侧没有数据）", drop: 0 });
      continue;
    }
    const delta = +(cv - bv).toFixed(3);
    if (delta < 0) worst = Math.min(worst, delta);
    rows.push({
      key: k,
      text: bv.toFixed(3) + " → " + cv.toFixed(3) + "  (" + (delta >= 0 ? "+" : "") + delta + ")",
      drop: delta < 0 ? -delta : 0,
    });
  }
  const dp50 = c.p50Ms - b.p50Ms;
  const dp95 = c.p95Ms - b.p95Ms;
  console.log("\n=== 版本对比（" + b.total + " → " + c.total + " 条任务）===");
  for (const r of rows) console.log("  " + pad(r.key) + r.text);
  console.log("  " + pad("P50 / P95") + secs(b.p50Ms) + " → " + secs(c.p50Ms) + " · " + secs(b.p95Ms) + " → " + secs(c.p95Ms) +
    "  (" + (dp50 >= 0 ? "+" : "") + (dp50 / 1000).toFixed(1) + "s / " + (dp95 >= 0 ? "+" : "") + (dp95 / 1000).toFixed(1) + "s)");
  console.log("  " + pad("成本") + b.cost + " → " + c.cost);
  const limit = maxDrop === undefined || maxDrop === null ? 0.05 : Number(maxDrop);
  const regressed = rows.filter((r) => r.drop > limit);
  console.log("");
  if (regressed.length) {
    console.log("REGRESSION: " + regressed.map((r) => r.key).join("、") + " 下降超过阈值 " + limit);
    return false;
  }
  console.log("OK: 无指标下降超过阈值 " + limit + (worst < 0 ? "（最大下降 " + Math.abs(worst).toFixed(3) + "）" : ""));
  return true;
}

(async () => {
  const spec = loadSpec();
  const problems = validate(spec);
  const byCat = summarize(spec);
  console.log("评测集： " + spec.tasks.length + " 条 · " + JSON.stringify(byCat));
  if (problems.length) {
    console.error("结构校验失败：");
    for (const p of problems) console.error(" - " + p);
    process.exit(1);
  }
  console.log("结构校验通过");

  // 离线分析：只读结果文件，不需要大脑
  const reportFile = value("report", null);
  if (reportFile) {
    printMetrics(aggregate(readResults(path.resolve(root, String(reportFile)))));
    return;
  }
  const baselineFile = value("compare", null);
  if (baselineFile) {
    const candidateFile = value("candidate", null);
    if (!candidateFile) throw new Error("--compare 需要同时给 --candidate=<新结果.jsonl>");
    const ok = compare(
      readResults(path.resolve(root, String(baselineFile))),
      readResults(path.resolve(root, String(candidateFile))),
      value("max-drop", 0.05)
    );
    if (!ok) process.exit(1);
    return;
  }

  if (!flag("run")) {
    console.log("（仅校验模式。加 --run 真跑（需已登录大脑）；--report=<结果.jsonl> 离线看指标；--compare=<旧> --candidate=<新> 做退化门禁。）");
    return;
  }
  await runReal(spec);
})().catch((e) => {
  console.error("评测失败：" + e.message);
  process.exit(1);
});
