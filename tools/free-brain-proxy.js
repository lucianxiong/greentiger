#!/usr/bin/env node
/**
 * 免 Key 大脑代理（评测/演练用，不参与生产）。
 *
 * 背景：Pollinations 的公共端点 OpenAI 兼容、会回传 usage，但匿名限速「每 30 秒 1 次」。
 * 绿虎的 Agent 循环一步一请求，直接打会连环 402。这个代理做两件事：
 *   1) 按最小间隔排队（默认 31s），保证不触发限速；
 *   2) 遇到 402 自动等待重试（最多 3 次）。
 *
 * 只在回环地址监听，转发目标是固定上游；不保存请求内容（只打印时间与状态）。
 *
 * 用法：
 *   node tools/free-brain-proxy.js [--port=17999] [--upstream=https://text.pollinations.ai] [--model=openai-fast] [--min-interval=31000]
 *   # 然后让绿虎/评测指向它：
 *   node tools/eval.js --run --local-endpoint=http://127.0.0.1:17999 --local-model=openai-fast --limit=2
 */
const http = require("http");
const { URL } = require("url");

const args = process.argv.slice(2);
const value = (n, d) => {
  const f = args.find((a) => a.startsWith("--" + n + "="));
  return f ? f.slice(n.length + 3) : d;
};

const port = Number(value("port", 17999));
const upstream = String(value("upstream", "https://text.pollinations.ai")).replace(/\/+$/, "");
const model = String(value("model", "openai-fast"));
const minInterval = Number(value("min-interval", 31000));

let lastAt = 0;
let queue = Promise.resolve();

// 串行化 + 最小间隔：所有请求排一条队，保证任意两次之间 ≥ minInterval
function schedule(fn) {
  const run = queue.then(async () => {
    const wait = lastAt + minInterval - Date.now();
    if (wait > 0) await new Promise((r) => setTimeout(r, wait));
    try {
      return await fn();
    } finally {
      lastAt = Date.now();
    }
  });
  queue = run.catch(() => {});
  return run;
}

async function forward(body) {
  let payload = body;
  try {
    const obj = JSON.parse(body);
    obj.model = model;
    obj.stream = false;
    payload = JSON.stringify(obj);
  } catch (e) {}

  for (let attempt = 1; attempt <= 3; attempt++) {
    const res = await fetch(upstream + "/v1/chat/completions", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: payload,
    });
    const text = await res.text();
    if (res.status === 402 && attempt < 3) {
      console.log("[proxy] 402 限速，等待 6 秒重试 (" + attempt + "/3)");
      await new Promise((r) => setTimeout(r, 6000));
      continue;
    }
    console.log("[proxy] " + res.status + " " + new Date().toISOString().slice(11, 19) + " " + text.length + "B");
    return { status: res.status, text };
  }
  return { status: 500, text: JSON.stringify({ error: "proxy retries exhausted" }) };
}

const server = http.createServer((req, res) => {
  if (req.method === "GET" && req.url.startsWith("/v1/models")) {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ object: "list", data: [{ id: model, object: "model" }] }));
    return;
  }
  if (req.method === "POST" && req.url.startsWith("/v1/chat/completions")) {
    let body = "";
    req.on("data", (c) => (body += c));
    req.on("end", () => {
      schedule(() => forward(body))
        .then((r) => {
          res.writeHead(r.status, { "Content-Type": "application/json" });
          res.end(r.text);
        })
        .catch((e) => {
          res.writeHead(502, { "Content-Type": "application/json" });
          res.end(JSON.stringify({ error: String(e && e.message ? e.message : e) }));
        });
    });
    return;
  }
  res.writeHead(404, { "Content-Type": "application/json" });
  res.end(JSON.stringify({ error: "not found" }));
});

server.listen(port, "127.0.0.1", () => {
  console.log("免 Key 代理已启动: http://127.0.0.1:" + port + " → " + upstream + " (model=" + model + ", 间隔 " + Math.round(minInterval / 1000) + "s)");
});
