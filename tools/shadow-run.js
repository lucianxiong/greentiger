#!/usr/bin/env node
/**
 * 影子运行（阶段7）：把真实任务丢给绿虎，但强制只读，看它「本来会做什么」而不真的动手。
 *
 * 两种用法：
 *   1) 自检（不需要任何大脑）：node tools/shadow-run.js --mock
 *      起一个本地假大脑，让它连续要求 write_file / shell；断言这些调用全部被只读模式挡下，
 *      文件没有被创建。这是对「只读模式真的挡得住副作用」的可执行证明。
 *
 *   2) 真实影子运行：node tools/shadow-run.js --tasks=shadow/tasks.json [--data-dir=...] [--api-key=...]
 *      tasks.json 形如 [{ "id": "t1", "prompt": "把 README 的错别字列出来" }, ...]
 *      强制 readOnly=true、highRiskPolicy=deny、autoRun=true：普通工具照常跑（只读类），
 *      任何写操作都会被策略拒绝并记进报告。跑完输出 shadow/report-<时间>.json。
 *
 * 安全断言（两种模式都做）：报告里不允许出现「已执行（done）的副作用调用」。
 * 出现就说明只读模式漏了，进程以非零码退出。
 */
const fs = require("fs");
const os = require("os");
const path = require("path");
const http = require("http");
const { spawn, spawnSync } = require("child_process");

const root = path.resolve(__dirname, "..");
const args = process.argv.slice(2);
const flag = (n) => args.find((a) => a === "--" + n || a.startsWith("--" + n + "="));
const value = (n, d) => {
  const f = flag(n);
  if (!f) return d;
  const i = f.indexOf("=");
  return i < 0 ? true : f.slice(i + 1);
};

const mockMode = !!flag("mock");
const exe = path.resolve(root, String(value("exe", path.join("dist", "GreenTiger.exe"))));
const port = Number(value("port", mockMode ? 17996 : 17997));
const mockPort = Number(value("mock-port", 17998));
const timeoutSec = Number(value("timeout", 240));
const stamp = new Date().toISOString().replace(/[-:T]/g, "").slice(0, 14);
const dataDir = value("data-dir", path.join(os.tmpdir(), "GreenTiger", "shadow-" + stamp));

const SIDE_EFFECT_TOOLS = ["write_file", "shell", "mouse", "keyboard", "upload_file", "clipboard", "task"];
let failed = 0;
let appPid = 0;
const check = (name, ok, detail) => {
  console.log((ok ? "PASS " : "FAIL ") + name + (detail ? " :: " + detail : ""));
  if (!ok) failed++;
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------- 假大脑（仅 --mock）：连续要求三个副作用动作 ----------
const mockReplies = [
  "我先写个文件。\n\n```greentiger\n" + JSON.stringify({ tool: "write_file", args: { path: "shadow-should-not-exist.txt", content: "x" } }) + "\n```",
  "再跑一条命令。\n\n```greentiger\n" + JSON.stringify({ tool: "shell", args: { command: "Write-Output shadow", timeout: 30 } }) + "\n```",
  "看起来都被挡住了。\n\n```greentiger\n" + JSON.stringify({ tool: "finish", args: { summary: "只读模式演练结束" } }) + "\n```",
];

function startMock() {
  let turn = 0;
  const server = http.createServer((req, res) => {
    if (req.url.indexOf("/v1/models") === 0) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ data: [{ id: "mock-shadow" }] }));
      return;
    }
    if (req.url.indexOf("/v1/chat/completions") === 0) {
      let body = "";
      req.on("data", (c) => (body += c));
      req.on("end", () => {
        const content = mockReplies[Math.min(turn, mockReplies.length - 1)];
        turn++;
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({
          choices: [{ index: 0, message: { role: "assistant", content }, finish_reason: "stop" }],
          usage: { prompt_tokens: 900, completion_tokens: 50 },
        }));
      });
      return;
    }
    res.writeHead(404);
    res.end();
  });
  return new Promise((resolve) => server.listen(mockPort, "127.0.0.1", () => resolve(server)));
}

function startApp() {
  fs.mkdirSync(dataDir, { recursive: true });
  const child = spawn(exe, ["--no-browser", "--port=" + port, "--data-dir=" + dataDir], { stdio: "ignore" });
  appPid = child.pid || 0;
  return child;
}

async function waitReady(sec) {
  const file = path.join(dataDir, "ui.url");
  const start = Date.now();
  while (Date.now() - start < sec * 1000) {
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
  throw new Error("应用未在 " + sec + " 秒内就绪");
}

function api(token) {
  const headers = { "X-GreenTiger-Token": token, "Content-Type": "application/json" };
  return {
    state: () => fetch("http://127.0.0.1:" + port + "/api/state", { headers }).then((r) => r.json()),
    post: (p, body) =>
      fetch("http://127.0.0.1:" + port + p, { method: "POST", headers, body: JSON.stringify(body || {}) }).then((r) => r.json()),
  };
}

function collectCalls(st) {
  const calls = [];
  for (const m of (st && st.active && st.active.messages) || []) {
    for (const c of m.toolCalls || []) {
      calls.push({
        name: c.name,
        status: c.status,
        errorCode: c.errorCode || "",
        risk: c.risk || "",
        args: String(c.argsJson || "").slice(0, 240),
      });
    }
  }
  return calls;
}

async function runTask(client, task) {
  const started = Date.now();
  await client.post("/api/chat", { text: task.prompt });
  let outcome = "";
  let lastTask = null;
  while (Date.now() - started < timeoutSec * 1000) {
    await sleep(800);
    let st;
    try { st = await client.state(); } catch (e) { continue; }
    if (st.approval && st.approval.id) await client.post("/api/approval", { id: st.approval.id, decision: "deny" });
    const t = st.task || {};
    if (t.id) lastTask = t;
    if (["succeeded", "failed", "cancelled", "incomplete"].includes(t.outcome)) { outcome = t.outcome; break; }
  }
  const st = await client.state();
  return {
    id: task.id || "",
    prompt: task.prompt,
    outcome: outcome || "timeout",
    steps: lastTask ? lastTask.steps : 0,
    durationMs: Date.now() - started,
    calls: collectCalls(st),
  };
}

function safetyViolations(tasks) {
  const bad = [];
  for (const t of tasks) {
    for (const c of t.calls) {
      if (c.status !== "done") continue;
      if (SIDE_EFFECT_TOOLS.includes(c.name) || c.name.startsWith("mcp__")) bad.push(t.id + ":" + c.name);
    }
  }
  return bad;
}

(async () => {
  if (!fs.existsSync(exe)) throw new Error("找不到可执行文件：" + exe + "（先运行 build.ps1）");
  const workDir = path.join(dataDir, "workspace");
  fs.mkdirSync(workDir, { recursive: true });

  let mock = null;
  let app = null;
  try {
    if (mockMode) mock = await startMock();
    app = startApp();
    const ready = await waitReady(40);
    const client = api(ready.token);

    const cfg = {
      workDir,
      autoRun: true,
      readOnly: true,          // 影子运行的核心：任何副作用工具都会被拒绝
      highRiskPolicy: "deny",  // danger 级直接拒绝，不打扰人
      estimateWebTokens: true,
    };
    if (mockMode) {
      cfg.providerEndpoint = "http://127.0.0.1:" + mockPort;
      cfg.providerKey = "mock-key";
      cfg.providerModel = "mock-shadow";
    } else {
      const apiKey = String(value("api-key", process.env.GREEN_TIGER_API_KEY || "") || "");
      const apiBase = String(value("api-endpoint", process.env.GREEN_TIGER_API_BASE || "") || "");
      const apiModel = String(value("api-model", process.env.GREEN_TIGER_API_MODEL || "") || "");
      const localBase = String(value("local-endpoint", process.env.GREEN_TIGER_LOCAL_BASE || "") || "");
      const localModel = String(value("local-model", process.env.GREEN_TIGER_LOCAL_MODEL || "") || "");
      if (apiKey && apiBase) {
        cfg.providerEndpoint = apiBase;
        cfg.providerKey = apiKey;
        cfg.providerModel = apiModel || "deepseek-chat";
      }
      if (localBase) {
        cfg.localEndpoint = localBase;
        if (localModel) cfg.localModel = localModel;
      }
    }
    await client.post("/api/config", cfg);

    let tasks;
    if (mockMode) {
      tasks = [{ id: "mock-shadow", prompt: "随便动一下文件" }];
    } else {
      const tasksFile = value("tasks", null);
      if (!tasksFile) throw new Error("真实影子运行需要 --tasks=<json>；或加 --mock 跑安全自检");
      const spec = JSON.parse(fs.readFileSync(path.resolve(root, String(tasksFile)), "utf8"));
      tasks = Array.isArray(spec) ? spec : spec.tasks || [];
      const limit = Number(value("limit", tasks.length));
      tasks = tasks.slice(0, limit);
      if (!tasks.length) throw new Error("任务列表为空");
    }

    const results = [];
    for (const t of tasks) {
      const r = await runTask(client, t);
      results.push(r);
      const blocked = r.calls.filter((c) => c.status === "error" && c.errorCode === "permission_denied").length;
      console.log((r.outcome === "succeeded" ? "DONE " : "MISS ") + (r.id || "?") +
        " outcome=" + r.outcome + " steps=" + r.steps + " calls=" + r.calls.length + " blocked=" + blocked);
    }

    const bad = safetyViolations(results);
    const blockedCalls = results.reduce(
      (acc, r) => acc + r.calls.filter((c) => c.status === "error" && c.errorCode === "permission_denied").length, 0);
    check("no side effect executed in shadow mode", bad.length === 0, bad.join(", "));

    if (mockMode) {
      const ghost = path.join(workDir, "shadow-should-not-exist.txt");
      check("write_file did not create file", !fs.existsSync(ghost), ghost);
      check("write_file blocked by policy", results[0].calls.some((c) => c.name === "write_file" && c.errorCode === "permission_denied"),
        results[0].calls.map((c) => c.name + ":" + c.status + ":" + c.errorCode).join(" | "));
      check("shell blocked by policy", results[0].calls.some((c) => c.name === "shell" && c.errorCode === "permission_denied"),
        results[0].calls.map((c) => c.name + ":" + c.errorCode).join(" | "));
    } else {
      const outFile = value("out", path.join(root, "shadow", "report-" + stamp + ".json"));
      fs.mkdirSync(path.dirname(outFile), { recursive: true });
      const summary = {
        total: results.length,
        succeeded: results.filter((r) => r.outcome === "succeeded").length,
        incomplete: results.filter((r) => r.outcome === "incomplete").length,
        failed: results.filter((r) => r.outcome === "failed").length,
        timeout: results.filter((r) => r.outcome === "timeout").length,
        blockedCalls,
      };
      fs.writeFileSync(outFile, JSON.stringify({ generatedAt: new Date().toISOString(), readOnly: true, summary, results }, null, 2), "utf8");
      console.log("\n影子运行完成：" + JSON.stringify(summary));
      console.log("报告：" + outFile);
      console.log("被策略挡下的副作用调用：" + blockedCalls + " 次（这就是「本来会做什么」的证据）");
    }
  } finally {
    try { if (app) app.kill(); } catch (e) {}
    if (appPid) {
      spawnSync("taskkill", ["/F", "/T", "/PID", String(appPid)], { encoding: "utf8" });
      appPid = 0;
    }
    try { if (mock) mock.close(); } catch (e) {}
    if (!value("keep", false)) {
      try { fs.rmSync(dataDir, { recursive: true, force: true }); } catch (e) {}
    }
  }

  console.log("");
  if (failed) {
    console.log("SHADOW RUN FAILED: " + failed + " 项");
    process.exit(1);
  }
  console.log(mockMode ? "SHADOW RUN SELFTEST PASSED" : "SHADOW RUN OK");
})().catch((e) => {
  console.error("SHADOW RUN ERROR: " + e.message);
  process.exit(1);
});
