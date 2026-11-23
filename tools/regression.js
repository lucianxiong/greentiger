#!/usr/bin/env node
/**
 * 一键回归（阶段6/7）：每次改动 Runtime/Prompt/工具后跑这一条命令。
 * 串联：构建 → 自检（进程内 300+ 断言）→ HTTP 冒烟 → 中断恢复冒烟 → 评测集结构校验 → 评测报告器自检。
 * 任一步失败即非零退出并停在失败步骤；--skip-build 可跳过构建（仅跑测试）。
 */
const { spawnSync } = require("child_process");
const path = require("path");

const root = path.resolve(__dirname, "..");
const skipBuild = process.argv.includes("--skip-build");

const steps = [];
// 版本/资源清单一致性先跑：构建脚本本身也会校验，这里再单独留一步证据。
steps.push({
  name: "check-version",
  cmd: "node",
  args: [path.join(root, "tools", "check-version.js")],
  expect: /VERSION\/ASSET CHECK PASSED/,
});
if (!skipBuild) {
  steps.push({
    name: "build",
    cmd: "powershell",
    args: ["-ExecutionPolicy", "Bypass", "-File", path.join(root, "build.ps1")],
    quiet: true,
  });
}
steps.push(
  {
    name: "self-test",
    cmd: path.join(root, "dist", "GreenTiger.exe"),
    args: ["--self-test"],
    expect: /ALL TESTS PASSED/,
    failOn: /^FAIL/,
  },
  {
    name: "smoke",
    cmd: "node",
    args: [path.join(root, "tools", "smoke.js")],
    expect: /ALL SMOKE CHECKS PASSED/,
  },
  {
    name: "recovery-smoke",
    cmd: "node",
    args: [path.join(root, "tools", "recovery-smoke.js")],
    expect: /ALL RECOVERY CHECKS PASSED/,
  },
  {
    name: "eval-structure",
    cmd: "node",
    args: [path.join(root, "tools", "eval.js")],
    expect: /结构校验通过/,
  },
  {
    // 报告器自检：用临时构造的结果文件验证指标口径与退化门禁（不需要大脑）
    name: "eval-report",
    cmd: "node",
    args: [path.join(root, "tools", "eval-report-selftest.js")],
    expect: /EVAL REPORT SELFTEST PASSED/,
  },
  {
    // 影子运行自检：起真实进程 + 假大脑，验证只读模式确实挡住了写文件与 shell
    name: "shadow-selftest",
    cmd: "node",
    args: [path.join(root, "tools", "shadow-run.js"), "--mock"],
    expect: /SHADOW RUN SELFTEST PASSED/,
  },
  // 真实故障演练（会启动并强杀自己启动的进程，约 40 秒）：默认不跑，--with-drill 打开。
  // 它验证的是「执行中杀进程 → 恢复 → 先核验再收尾」这条链路，属于发布前检查而非每次改动检查。
  ...(process.argv.includes("--with-drill")
    ? [{
        name: "fault-drill",
        cmd: "node",
        args: [path.join(root, "tools", "fault-drill.js")],
        expect: /FAULT DRILL PASSED/,
      }]
    : [])
);

let failed = false;
const results = [];
for (const step of steps) {
  process.stdout.write("[" + step.name + "] ... ");
  const r = spawnSync(step.cmd, step.args, { cwd: root, encoding: "utf8", timeout: 600000 });
  const out = (r.stdout || "") + (r.stderr || "");
  const ok =
    r.status === 0 &&
    (!step.expect || step.expect.test(out)) &&
    (!step.failOn || !step.failOn.test(out));
  results.push({ name: step.name, ok, status: r.status });
  if (ok) {
    console.log("PASS");
  } else {
    failed = true;
    console.log("FAIL (exit=" + r.status + ")");
    const tail = out.trim().split(/\r?\n/).slice(-25).join("\n");
    console.log("---- 输出末尾 ----\n" + tail + "\n------------------");
    break; // 停在第一个失败步骤，修完再跑
  }
}

console.log("");
if (failed) {
  const done = results.filter((r) => r.ok).length;
  console.log("REGRESSION FAILED: " + done + "/" + steps.length + " 步通过，停在 " + results[results.length - 1].name);
  process.exit(1);
}
console.log("REGRESSION OK: " + steps.length + "/" + steps.length + " 步全部通过");
