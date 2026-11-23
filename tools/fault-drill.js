#!/usr/bin/env node
/**
 * 真实故障演练（阶段7）：任务执行到一半时强杀进程，再重启，检查恢复行为。
 *
 * 为什么需要它：`recovery-smoke.js` 是「构造一份中断状态」的单元级验证，
 * 和「进程真的在跑工具时被 kill」不是一回事——后者才会暴露半写文件、悬置调用、
 * 租约未释放、重启后读到脏状态这类问题。
 *
 * 怎么在没有真实大脑的情况下造出「执行中」的任务：
 * 起一个本地 OpenAI 兼容的假大脑（/v1/models + /v1/chat/completions），
 * 让应用把它当成 API 服务商。它第一轮返回一条要跑 30 秒的命令，
 * 我们等工具真的进入 running 再 taskkill /F /T（连子进程一起杀）。
 *
 * 断言：
 *   1) 杀之前：工具调用确实在 running
 *   2) 重启后：任务不是 running，标记 interrupted + resumable
 *   3) 重启后：那条调用变成 interrupted（不是谎称成功）
 *   4) 续跑：能接着跑完并进入 succeeded
 *
 * 用法：node tools/fault-drill.js [--exe=dist/GreenTiger.exe] [--port=17994] [--mock-port=17995] [--keep]
 */
const fs = require("fs");
const os = require("os");
const path = require("path");
const http = require("http");
const { spawn, spawnSync } = require("child_process");
const net = require("net");

const root = path.resolve(__dirname, "..");
const args = process.argv.slice(2);
const flag = (n) => args.find((a) => a === "--" + n || a.startsWith("--" + n + "="));
const value = (n, d) => {
  const f = flag(n);
  if (!f) return d;
  const i = f.indexOf("=");
  return i < 0 ? true : f.slice(i + 1);
};

const exe = path.resolve(root, String(value("exe", path.join("dist", "GreenTiger.exe"))));
// 端口按运行随机：固定端口在连续跑（回归里紧跟着上一步）时会撞上 TIME_WAIT / 残留监听，
// 表现成「应用没起来 → 后面 8 项全挂」。需要固定就显式 --port=。
const port = Number(value("port", 17900 + Math.floor(Math.random() * 80)));
const mockPort = Number(value("mock-port", port + 100));
const keep = !!flag("keep");
const stamp = new Date().toISOString().replace(/[-:T]/g, "").slice(0, 14);
const dataDir = path.join(os.tmpdir(), "GreenTiger", "drill-" + stamp);
const workDir = path.join(dataDir, "workspace");

let failed = 0;
let appPid = 0;
function check(name, ok, detail) {
  console.log((ok ? "PASS " : "FAIL ") + name + (detail ? " :: " + detail : ""));
  if (!ok) failed++;
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------- 假大脑：OpenAI 兼容，第一轮让应用去跑长命令 ----------
const shellCmd = "Start-Sleep -Seconds 30; Write-Output drill-done";
const firstReply =
  "我先跑一条会持续 30 秒的命令。\n\n```greentiger\n" +
  JSON.stringify({ tool: "shell", args: { command: shellCmd, timeout: 120 } }) +
  "\n```";
const verifyReply = "先核验一下当前状态，不再重复执行那条长命令。\n\n```greentiger\n" +
  JSON.stringify({ tool: "shell", args: { command: "Write-Output drill-state-checked", timeout: 60 } }) +
  "\n```";
const resumeReply = "核验过了，任务收尾。\n\n```greentiger\n" +
  JSON.stringify({ tool: "finish", args: { summary: "演练任务完成（未重复执行副作用）" } }) +
  "\n```";

let turn = 0;
function startMock() {
  const server = http.createServer((req, res) => {
    if (req.url.indexOf("/v1/models") === 0) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ data: [{ id: "mock-brain" }] }));
      return;
    }
    if (req.url.indexOf("/v1/chat/completions") === 0) {
      let body = "";
      req.on("data", (c) => (body += c));
      req.on("end", () => {
        turn++;
        const content = turn === 1 ? firstReply : turn === 2 ? verifyReply : resumeReply;
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({
          id: "mock-" + turn,
          choices: [{ index: 0, message: { role: "assistant", content }, finish_reason: "stop" }],
          usage: { prompt_tokens: 1500, completion_tokens: 60 },
        }));
      });
      return;
    }
    res.writeHead(404);
    res.end();
  });
  return new Promise((resolve) => server.listen(mockPort, "127.0.0.1", () => resolve(server)));
}

// ---------- 应用进程 ----------
function startApp() {
  const child = spawn(exe, ["--no-browser", "--port=" + port, "--data-dir=" + dataDir], { stdio: "ignore" });
  appPid = child.pid || 0;
  return child;
}

async function waitReady(timeoutSec) {
  const file = path.join(dataDir, "ui.url");
  const start = Date.now();
  while (Date.now() - start < timeoutSec * 1000) {
    try {
      const url = fs.readFileSync(file, "utf8").trim();
      if (url) {
        const token = new URL(url).searchParams.get("token");
        const st = await fetch("http://127.0.0.1:" + port + "/api/state", {
          headers: { "X-GreenTiger-Token": token },
        }).then((r) => r.json());
        if (st) return { token, state: st };
      }
    } catch (e) {}
    await sleep(400);
  }
  throw new Error("应用未在 " + timeoutSec + " 秒内就绪");
}

function api(token) {
  const headers = { "X-GreenTiger-Token": token, "Content-Type": "application/json" };
  return {
    state: () => fetch("http://127.0.0.1:" + port + "/api/state", { headers }).then((r) => r.json()),
    post: (p, body) =>
      fetch("http://127.0.0.1:" + port + p, { method: "POST", headers, body: JSON.stringify(body || {}) }).then((r) => r.json()),
  };
}

function portOpen(p) {
  return new Promise((resolve) => {
    const socket = net.connect({ host: "127.0.0.1", port: p });
    const done = (v) => { try { socket.destroy(); } catch (e) {} resolve(v); };
    socket.once("connect", () => done(true));
    socket.once("error", () => done(false));
    socket.setTimeout(600, () => done(false));
  });
}

async function waitPortFree(p, tries) {
  for (let i = 0; i < tries; i++) {
    if (!(await portOpen(p))) return true;
    await sleep(300);
  }
  return false;
}

function findCall(st, status) {
  const msgs = (st && st.active && st.active.messages) || [];
  for (const m of msgs) {
    for (const c of m.toolCalls || []) {
      if (c && c.status === status) return c;
    }
  }
  return null;
}

(async () => {
  if (!fs.existsSync(exe)) throw new Error("找不到可执行文件：" + exe + "（先运行 build.ps1）");
  fs.mkdirSync(workDir, { recursive: true });
  const mock = await startMock();
  let app = null;
  try {
    app = startApp();
    let ready = await waitReady(40);
    let token = ready.token;
    const client = api(token);
    await client.post("/api/config", {
      workDir,
      autoRun: true,
      providerEndpoint: "http://127.0.0.1:" + mockPort,
      providerKey: "mock-key",
      providerModel: "mock-brain",
      // 演练不模拟人工点击：danger 工具直接放行（只读演示环境）
      highRiskPolicy: "allow",
    });
    // 保存配置后探测是异步的（主线程不阻塞返回），这里等它就绪而不是立刻断言
    let st0 = null;
    for (let i = 0; i < 40; i++) {
      st0 = await client.state();
      if (st0 && st0.brain && st0.brain.active === "provider") break;
      await sleep(250);
    }
    check("mock brain ready", !!(st0 && st0.brain && st0.brain.active === "provider"),
      JSON.stringify(st0 && st0.brain ? { active: st0.brain.active, detail: st0.brain.provider && st0.brain.provider.detail } : null));

    await client.post("/api/chat", { text: "跑一条长命令试试" });

    // 等工具真的进入 running：这才是「执行中被杀」
    let running = null;
    let sawTask = false;
    for (let i = 0; i < 60; i++) {
      await sleep(500);
      const st = await client.state();
      if (st && st.task && st.task.outcome === "running") sawTask = true;
      running = findCall(st, "running");
      if (running) break;
    }
    check("task entered running", sawTask, "");
    check("tool reached running before kill", !!running, running ? running.name : "没见过 running 的工具调用");
    if (!running) throw new Error("没等到工具进入 running，演练无法继续（可能假大脑没被用上）");

    // 真杀：连子进程一起杀。taskkill 的退出码受本地化/竞态影响，
    // 判定用「PID 是否还在」而不是退出码。
    const pid = app.pid;
    spawnSync("taskkill", ["/F", "/T", "/PID", String(pid)], { encoding: "utf8" });
    app = null;
    await sleep(1200);
    const alive = spawnSync("tasklist", ["/FI", "PID eq " + pid, "/NH"], { encoding: "utf8" });
    const stillAlive = (alive.stdout || "").indexOf(String(pid)) >= 0;
    check("process killed", !stillAlive, stillAlive ? "PID " + pid + " 仍在运行" : "pid=" + pid);
    // 等端口真正释放再重启：被杀进程的监听可能还挂在 TIME_WAIT/CLOSE_WAIT
    const freed = await waitPortFree(port, 40);
    check("port released before restart", freed, "port=" + port);

    // 重启：同一个 data-dir，恢复逻辑应当介入
    app = startApp();
    ready = await waitReady(40);
    token = ready.token;
    const client2 = api(token);

    let st2 = null;
    let recovered = false;
    for (let i = 0; i < 40; i++) {
      await sleep(500);
      st2 = await client2.state();
      const task = (st2 && st2.task) || {};
      if (task.outcome && task.outcome !== "running" && task.outcome !== "") { recovered = true; break; }
    }
    const task2 = (st2 && st2.task) || {};
    check("task left running after restart", recovered, task2.outcome || "(空)");
    check("task marked interrupted", task2.interrupted === true, String(task2.interrupted));
    check("task marked resumable", task2.resumable === true, String(task2.resumable));
    const interruptedCall = findCall(st2, "interrupted");
    check("dangling call marked interrupted", !!interruptedCall, interruptedCall ? interruptedCall.name : "没找到 interrupted 调用");

    // 续跑：同一任务记录，假大脑直接 finish
    const resumeRes = await client2.post("/api/task/resume", {});
    check("resume accepted", !!(resumeRes && resumeRes.ok), JSON.stringify(resumeRes || {}));
    let finalOutcome = "";
    for (let i = 0; i < 60; i++) {
      await sleep(500);
      const st = await client2.state();
      const t = (st && st.task) || {};
      if (["succeeded", "failed", "cancelled", "incomplete"].includes(t.outcome)) { finalOutcome = t.outcome; break; }
    }
    check("resumed task reaches terminal", finalOutcome.length > 0, finalOutcome || "timeout");
    // 恢复后「先核验再收尾」应当能进入 succeeded：失败恢复不是死路
    check("resumed task can still succeed after verify", finalOutcome === "succeeded", finalOutcome);

    // 副作用没有被重复执行：被中断的那条长命令不应在续跑里再跑一次
    const st3 = await client2.state();
    const calls = [];
    for (const m of (st3.active && st3.active.messages) || []) for (const c of m.toolCalls || []) calls.push(c);
    const longCalls = calls.filter((c) => c.name === "shell" && (c.argsJson || "").indexOf("Start-Sleep") >= 0);
    check("interrupted long command not replayed", longCalls.every((c) => c.status !== "done"),
      "done long calls=" + longCalls.filter((c) => c.status === "done").length);
    const verifyCalls = calls.filter((c) => c.name === "shell" && (c.argsJson || "").indexOf("drill-state-checked") >= 0);
    check("verification action recorded", verifyCalls.some((c) => c.status === "done"),
      "verify calls=" + verifyCalls.length);
  } finally {
    // GUI 进程 child.kill() 不保证退出：按 pid 强杀，避免残留实例占住 exe 让下一次构建失败
    try { if (app) app.kill(); } catch (e) {}
    if (appPid) {
      spawnSync("taskkill", ["/F", "/T", "/PID", String(appPid)], { encoding: "utf8" });
      appPid = 0;
    }
    try { mock.close(); } catch (e) {}
    if (!keep) {
      try { fs.rmSync(dataDir, { recursive: true, force: true }); } catch (e) {}
    } else {
      console.log("数据目录保留在 " + dataDir);
    }
  }

  console.log("");
  if (failed) {
    console.log("FAULT DRILL FAILED: " + failed + " 项未通过");
    process.exit(1);
  }
  console.log("FAULT DRILL PASSED");
})().catch((e) => {
  console.error("FAULT DRILL ERROR: " + e.message);
  process.exit(1);
});
