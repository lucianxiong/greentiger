// 中断恢复的进程级冒烟：构造一个「执行中被打断」的会话，启动 GreenTiger，检查是否被如实恢复。
// 用法：node tools/recovery-smoke.js [--exe=dist/GreenTiger.exe]
const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawn } = require("child_process");

const root = path.resolve(__dirname, "..");
const argExe = process.argv.find((a) => a.startsWith("--exe="));
const exe = argExe ? path.resolve(root, argExe.slice(6)) : path.join(root, "dist", "GreenTiger.exe");
const port = Number(process.env.GREENTIGER_RECOVERY_PORT || 17992);
const dataDir = path.join(os.tmpdir(), "GreenTiger", "recovery-" + Date.now());
const base = "http://127.0.0.1:" + port;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function writeSession(session) {
  fs.mkdirSync(path.join(dataDir, "sessions"), { recursive: true });
  fs.writeFileSync(path.join(dataDir, "sessions", session.Id + ".json"), JSON.stringify(session), "utf8");
}

// 会话2：卡在等待审批，且用户当时已经点了「允许」（票据已批准但尚未执行）
function seedApprovalSession() {
  const now = Date.now();
  writeSession({
    Id: "s_approval_test",
    Title: "审批等待测试",
    CreatedAt: now,
    UpdatedAt: now,
    WorkDir: process.cwd(),
    ToolCount: 1,
    TurnCount: 1,
    ContextChars: 10,
    SchemaVersion: 2,
    Task: {
      TaskId: "task_approval_test",
      SessionId: "s_approval_test",
      UserText: "跑一条命令",
      Outcome: "running",
      ErrorClass: "",
      ErrorDetail: "",
      Steps: 1,
      Repairs: 0,
      Retries: 0,
      StartedAt: now - 5000,
      EndedAt: 0,
      DurationMs: 0,
      Verification: "",
      BudgetExceeded: false,
      RunOwner: "dead-worker:1:abc",
      LeaseUntil: now - 1000,
      Version: 2,
    },
    Approvals: [
      {
        Id: "ap_test_1",
        TaskId: "task_approval_test",
        SessionId: "s_approval_test",
        Tool: "shell",
        ArgsHash: "h00000000",
        Risk: "danger",
        Status: "granted",
        Decision: "allow",
        CreatedAt: now - 4000,
        DecidedAt: now - 3000,
        ExpiresAt: now + 300000,
        Note: "用户允许一次",
      },
    ],
    Messages: [
      { Id: "m_1", Role: "user", Text: "跑一条命令", Time: now - 5000, Status: "done", Brain: "", ToolCalls: [] },
      {
        Id: "m_2",
        Role: "assistant",
        Text: "我执行一下。",
        Time: now - 4000,
        Status: "streaming",
        Brain: "provider",
        ToolCalls: [
          {
            Id: "t_ap",
            Name: "shell",
            ArgsJson: "{\"command\":\"Write-Output hi\"}",
            Command: "",
            Risk: "danger",
            RiskReason: "",
            Status: "awaiting",
            Source: "",
            Output: "",
            ExitCode: 0,
            StartedAt: now - 3000,
            FinishedAt: 0,
            DurationMs: 0,
            RequiresApproval: true,
            ApprovalStatus: "",
            Artifacts: [],
            ErrorCode: "",
            ErrorClass: "",
            IdempotencyKey: "",
            Attempts: 0,
          },
        ],
      },
    ],
  });
}

// 会话3：已取消的任务——不允许借「继续任务」复活
function seedCancelledSession() {
  const now = Date.now();
  writeSession({
    Id: "s_cancelled_test",
    Title: "已取消任务",
    CreatedAt: now,
    UpdatedAt: now,
    WorkDir: process.cwd(),
    ToolCount: 0,
    TurnCount: 1,
    ContextChars: 5,
    SchemaVersion: 2,
    Task: {
      TaskId: "task_cancelled_test",
      SessionId: "s_cancelled_test",
      UserText: "写一个文件",
      Outcome: "cancelled",
      ErrorClass: "cancelled",
      ErrorDetail: "用户停止任务",
      Steps: 2,
      Repairs: 0,
      Retries: 0,
      StartedAt: now - 9000,
      EndedAt: now - 8000,
      DurationMs: 1000,
      Verification: "",
      BudgetExceeded: false,
      RunOwner: "",
      LeaseUntil: 0,
      Version: 4,
    },
    Approvals: [],
    Messages: [{ Id: "m_1", Role: "user", Text: "写一个文件", Time: now - 9000, Status: "done", Brain: "", ToolCalls: [] }],
  });
}

function seedSession() {
  fs.mkdirSync(path.join(dataDir, "sessions"), { recursive: true });
  const now = Date.now();
  const session = {
    Id: "s_recovery_test",
    Title: "中断恢复测试",
    CreatedAt: now,
    UpdatedAt: now,
    WorkDir: process.cwd(),
    ToolCount: 1,
    TurnCount: 1,
    ContextChars: 10,
    SchemaVersion: 2,
    Task: {
      TaskId: "task_recovery_test",
      SessionId: "s_recovery_test",
      UserText: "写一个文件",
      Outcome: "running",
      ErrorClass: "",
      ErrorDetail: "",
      Steps: 1,
      Repairs: 0,
      Retries: 0,
      StartedAt: now - 5000,
      EndedAt: 0,
      DurationMs: 0,
      Verification: "",
      BudgetExceeded: false,
      RunOwner: "dead-worker:1:abc",
      LeaseUntil: now - 1000,
      Version: 3,
    },
    Messages: [
      { Id: "m_1", Role: "user", Text: "写一个文件", Time: now - 5000, Status: "done", Brain: "", ToolCalls: [] },
      {
        Id: "m_2",
        Role: "assistant",
        Text: "我来写。",
        Time: now - 4000,
        Status: "streaming",
        Brain: "provider",
        ToolCalls: [
          {
            Id: "t_1",
            Name: "write_file",
            ArgsJson: "{\"path\":\"a.txt\",\"content\":\"x\"}",
            Command: "",
            Risk: "safe",
            RiskReason: "",
            Status: "running",
            Source: "",
            Output: "",
            ExitCode: 0,
            StartedAt: now - 3000,
            FinishedAt: 0,
            DurationMs: 0,
            RequiresApproval: false,
            ApprovalStatus: "auto",
            Artifacts: [],
            ErrorCode: "",
            ErrorClass: "",
            IdempotencyKey: "",
            Attempts: 0,
          },
        ],
      },
    ],
  };
  fs.writeFileSync(path.join(dataDir, "sessions", session.Id + ".json"), JSON.stringify(session), "utf8");
}

async function waitForUrl() {
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

(async () => {
  if (!fs.existsSync(exe)) throw new Error("找不到可执行文件：" + exe);
  seedSession();
  seedApprovalSession();
  seedCancelledSession();
  const child = spawn(exe, ["--no-browser", "--port=" + port, "--data-dir=" + dataDir], { stdio: "ignore" });
  const fails = [];
  const ok = (name, cond, detail) => {
    console.log((cond ? "PASS " : "FAIL ") + name + (detail ? " :: " + detail : ""));
    if (!cond) fails.push(name);
  };
  try {
    const uiUrl = await waitForUrl();
    const token = new URL(uiUrl).searchParams.get("token");
    const state = await (await fetch(base + "/api/state", { headers: { "X-GreenTiger-Token": token } })).json();
    const task = state.task || {};
    ok("recovered task not running", task.outcome !== "running", "outcome=" + task.outcome);
    ok("recovered outcome incomplete", task.outcome === "incomplete", task.outcome);
    ok("recovered error class", task.errorClass === "tool_error", task.errorClass);

    const msgs = (state.active && state.active.messages) || [];
    const note = msgs.find((m) => m.role === "system" && String(m.text || "").indexOf("中断恢复") >= 0);
    ok("recovery note added", !!note, note ? note.text.split("\n")[0] : "missing");

    let call = null;
    for (const m of msgs) for (const c of m.toolCalls || []) if (c.id === "t_1") call = c;
    ok("dangling call marked interrupted", !!call && call.status === "interrupted", call ? call.status : "missing");
    ok("dangling call has error code", !!call && call.errorCode === "interrupted", call ? call.errorCode : "");

    const saved = JSON.parse(fs.readFileSync(path.join(dataDir, "sessions", "s_recovery_test.json"), "utf8"));
    ok("recovery persisted", saved.Task && saved.Task.Outcome === "incomplete", saved.Task ? saved.Task.Outcome : "missing");

    // —— 阶段4：等待审批的任务在重启后仍可续跑，且票据状态如实暴露 ——
    const headers = { "X-GreenTiger-Token": token, "Content-Type": "application/json" };
    const api = async (p, opts) => (await fetch(base + p, Object.assign({ headers: headers }, opts || {}))).json();

    const apState = await api("/api/session/select", { method: "POST", body: JSON.stringify({ id: "s_approval_test" }) });
    const apTask = apState.task || {};
    ok("approval task waiting", apTask.outcome === "waiting_approval", apTask.outcome);
    ok("approval task resumable", apTask.resumable === true, String(apTask.resumable));
    const tickets = apTask.approvals || [];
    ok("approval ticket survived restart", tickets.length === 1 && tickets[0].status === "granted",
      tickets.length ? tickets[0].status : "no ticket");

    // —— 已取消的任务不能借「继续任务」复活 ——
    const cState = await api("/api/session/select", { method: "POST", body: JSON.stringify({ id: "s_cancelled_test" }) });
    const cTask = cState.task || {};
    ok("cancelled task not resumable", cTask.resumable !== true, String(cTask.resumable));
    const resumeResult = await api("/api/task/resume", { method: "POST", body: "{}" });
    ok("resume refused for cancelled", resumeResult.ok === false && !!resumeResult.error, JSON.stringify(resumeResult));
    const afterResume = await api("/api/state");
    const refuseNote = ((afterResume.active && afterResume.active.messages) || [])
      .find((m) => m.role === "system" && String(m.text || "").indexOf("无法继续") >= 0);
    ok("resume refusal explained", !!refuseNote, refuseNote ? refuseNote.text.slice(0, 40) : "missing");
  } finally {
    try { child.kill(); } catch (e) {}
  }
  console.log(fails.length === 0 ? "ALL RECOVERY CHECKS PASSED" : fails.length + " CHECK(S) FAILED");
  process.exit(fails.length === 0 ? 0 : 1);
})().catch((e) => {
  console.error("恢复冒烟失败：" + e.message);
  process.exit(1);
});
