/* 绿虎主入口：状态、事件、SSE、API 与启动流程。
 * 纯函数层已拆到同目录的 ES 模块；这些 import 绑定在 IIFE 外层，
 * 因此 IIFE 内原有调用点无需改动，闭包状态仍全部留在主入口。 */
import { ICONS, TOOL_META, ESC_MAP, $, $$, atParentDir, icon, avatarHtml, esc, attr, clampInt, fmtNum, fmtSize, fmtDuration, fmtClock, fmtTime, fmtLogTime, relTime, baseName, shortPath, oneLine, fuzzyScore, fuzzyHighlight, fuzzyRank, hydrateIcons, FOCUSABLE } from "./util.js";
import { HL_KEYWORDS, HL_LINE_COMMENT, HL_TPL, HL_TYPES, jsonDepth, hlConf, highlightCode, codeBlockHtml, inlineMd, splitTableRow, diffHtml } from "./markdown.js";
import { toolMeta, prettyArgs, toolSummary, toolStatusLabel, toolOutPreview, toolDiffHtml, artifactHtml, riskText, riskClass, planInfo } from "./tools.js";
import { msgSig, sessionItemHtml, dayGroup, brainTagHtml, splitAttachments } from "./chat.js";
import { SHORTCUTS, keyLabel, shortcutHtml, toggleRowHtml, modeNameZh, SLASH_COMMANDS, SETTINGS_CATS, HOME_PREFS, bindState, dsInfo, brainInfo, connInfo } from "./model.js";import { initToolbox } from "./toolbox.js";


/* 共享状态提升到模块顶层：IIFE 内原有赋值仍落到同一个变量，
 * 同时让 model.js 通过 bindState 读取最新的 S。 */
var S = null;
bindState(function(){ return S; });

(function(){
"use strict";

var lastChatSig = "";
var attachments = [];
var expandedTools = new Set();
var collapsedTools = new Set();
var sending = false;
var uploading = false;
var runStart = 0;
var logsOpen = false;
// 花园：主页园景画布与全屏花园各自持有一个 mount 实例，互斥存在（进全屏先销毁小景）
var emptyGardenApi = null;
var gardenOverlayApi = null;
var gardenWindOn = false;
var gardenLastFocus = null;
var wbTab = "artifacts";
var wbSessionQuery = "";
var currentAskLabel = "";
var logsInit = false;
var bootSessionChecked = false;
var unreadLogs = 0;
var logFilter = "all";
var settingsOpen = false;
var settingsTheme = "dark";
var uiAudioContext = null;
var lastUiSoundAt = 0;

function motionReduced(){
  var osReduced = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  return !!osReduced || !!(S && S.config && S.config.uiMotion === false);
}
function syncUiMotionPreference(){
  document.documentElement.classList.toggle("motion-reduced", motionReduced());
}
function playUiSound(kind){
  if (!(S && S.config && S.config.uiSounds)) return;
  var AudioContext = window.AudioContext || window.webkitAudioContext;
  if (!AudioContext) return;
  var now = Date.now();
  if (kind === "tap" && now - lastUiSoundAt < 48) return;
  lastUiSoundAt = now;
  try{
    if (!uiAudioContext) uiAudioContext = new AudioContext();
    var ctx = uiAudioContext;
    if (ctx.state === "suspended") ctx.resume().catch(function(){});
    var notes = kind === "success" ? [{hz:620,at:0},{hz:840,at:.075}]
      : kind === "error" ? [{hz:360,at:0},{hz:270,at:.09}]
      : [{hz:520,at:0}];
    var base = ctx.currentTime;
    for (var i=0;i<notes.length;i++){
      var osc = ctx.createOscillator();
      var gain = ctx.createGain();
      var at = base + notes[i].at;
      var length = kind === "tap" ? .035 : .065;
      osc.type = "sine";
      osc.frequency.setValueAtTime(notes[i].hz, at);
      gain.gain.setValueAtTime(.0001, at);
      gain.gain.exponentialRampToValueAtTime(kind === "tap" ? .012 : .018, at + .008);
      gain.gain.exponentialRampToValueAtTime(.0001, at + length);
      osc.connect(gain); gain.connect(ctx.destination);
      osc.start(at); osc.stop(at + length + .005);
    }
  }catch(e){}
}

/* 主题统一入口：优先交给 shell.js（唯一键 greentiger.theme），
 * 没有 shell.js 时退回直接写 data-theme，保证主控制台单页可用。 */
/* 强调色主题入口：green=默认本色（无 data-accent），其余走 theme.css 的覆盖块 */
function applyThemeSafe(theme){
 if (window.GreenTigerShell && typeof window.GreenTigerShell.applyTheme === "function"){
 window.GreenTigerShell.applyTheme(theme);
 } else {
 document.documentElement.dataset.theme = theme === "dark" ? "dark" : "light";
 }
 return document.documentElement.dataset.theme;
}
var currentApprovalId = null;
var currentApprovalReviewSig = "";
var confirmCallback = null;
var paletteActions = [];
var paletteFiltered = [];
var paletteIndex = 0;
var composing = false;
var eventSource = null;
var sseErrors = 0;
var sseWarned = false;
var shuttingDown = false;
var draftKey = "greentiger.draft";
var editDraftKey = "greentiger.draft.before-edit";
var sessionQuery = "";
var editingIndex = -1;   // 正在编辑的 user 消息索引（-1 = 未编辑）；提交时走 /api/session/edit 而非 /api/chat

/* ---------- 模糊匹配（命令面板 / 斜杠命令 / @ 文件三处共用）----------
 * 以前三处各写一份 indexOf：只能前缀或子串匹配，打「重试」找不到 /retry，
 * 打错一个字母就整个列表空掉。
 *
 * 这里统一成「子序列打分」：字符按顺序出现即命中，并给连续命中、落在词首
 * （空格 - _ / . 之后）额外加分，跳得越远扣分越多。返回值带命中下标，
 * 界面还能把匹配到的字母标出来 —— 让用户看清这条为什么被选中。
 * 返回 null 表示不匹配。
 */
/* ---------- markdown ---------- */
/* 裸 JSON 判定：括号配平计数（跳过字符串内的括号），用于把整段 JSON 当代码块渲染 */
var jsonLineStart = /^\s*[{\[]\s*("|\}|\])/;
/* ---------- 轻量语法高亮（零依赖） ----------
 * 顺序扫描而不是正则替换：先把代码切成 token，再逐段转义，
 * 避免「先转义导致引号变成 &quot;、字符串再也识别不出来」这个经典坑。
 * 词表只收高频关键字；未知语言原样输出，绝不做猜测。 */
function renderMd(raw){
  if (raw==null) return "";
  var text = String(raw).replace(/\r\n/g,"\n");
  var truncated = false;
  if (text.length > 60000) { text = text.slice(0,60000); truncated = true; }
  var blocks = [];
  text = text.replace(/```([^\n`]*)\n?([\s\S]*?)```/g,function(m,lang,code){
    blocks.push({lang:lang.trim(),code:code});
    return "\u0000CODE"+(blocks.length-1)+"\u0000";
  });
  var lines = text.split("\n");
  var html = [];
  var para = [], list = null, quote = [];
  function flushPara(){
    if (!para.length) return;
    html.push("<p>"+inlineMd(esc(para.join("\n"))).replace(/\n/g,"<br>")+"</p>");
    para = [];
  }
  function flushList(){
    if (!list) return;
    var tag = list.type === "ol" ? "ol" : "ul";
    html.push("<"+tag+">"+list.items.map(function(it, idx){
      // 有序列表编号显式写入文本：不依赖 list-style marker 渲染，任何 WebView2 版本都不丢数字
      var prefix = tag === "ol" ? (idx+1)+". " : "";
      return "<li>"+prefix+inlineMd(esc(it))+"</li>";
    }).join("")+"</"+tag+">");
    list = null;
  }
  function flushQuote(){
    if (!quote.length) return;
    html.push("<blockquote>"+inlineMd(esc(quote.join("\n"))).replace(/\n/g,"<br>")+"</blockquote>");
    quote = [];
  }
  function flushAll(){ flushPara(); flushList(); flushQuote(); }
  for (var i=0;i<lines.length;i++){
    var line = lines[i];
    var codeMatch = line.match(/^\u0000CODE(\d+)\u0000$/);
    if (codeMatch){
      flushAll();
      var b = blocks[+codeMatch[1]];
      if (b) html.push(codeBlockHtml(b.lang,b.code));
      continue;
    }
    if (/^\s*$/.test(line)) { flushAll(); continue; }
    // 裸 JSON（模型把工具调用/数据直接写进正文）：按代码块渲染。
    // 不这么做的两个后果：__x__ 被当成强调吃掉下划线；长 JSON 挤成一段没法读。
    if (jsonLineStart.test(line)) {
      var jsonLines = [line], j = i, probe = line;
      while (jsonDepth(probe) > 0 && j + 1 < lines.length && jsonLines.length < 200) {
        j++; jsonLines.push(lines[j]); probe += "\n" + lines[j];
      }
      if (jsonDepth(probe) === 0) {
        flushAll();
        html.push(codeBlockHtml("json", jsonLines.join("\n")));
        i = j;
        continue;
      }
    }
    var m;
    if ((m = line.match(/^(#{1,4})\s+(.*)$/))){
      flushAll();
      var level = Math.min(5,m[1].length+1);
      html.push("<h"+level+">"+inlineMd(esc(m[2]))+"</h"+level+">");
      continue;
    }
    if (/^\s*(---+|\*\*\*+|___+)\s*$/.test(line)) { flushAll(); html.push("<hr>"); continue; }
    if ((m = line.match(/^>\s?(.*)$/))) {
      flushPara(); flushList(); quote.push(m[1]); continue;
    } else { flushQuote(); }
    if (line.indexOf("|")>=0 && i+1<lines.length && /^\s*\|?[\s:|-]+\|?\s*$/.test(lines[i+1]) && lines[i+1].indexOf("-")>=0 && lines[i+1].indexOf("|")>=0){
      flushAll();
      var head = splitTableRow(line);
      i++;
      var rows = [];
      while (i+1<lines.length && lines[i+1].indexOf("|")>=0 && lines[i+1].trim()!==""){ i++; rows.push(splitTableRow(lines[i])); }
      var table = "<table><thead><tr>"+head.map(function(c){return "<th>"+inlineMd(esc(c))+"</th>";}).join("")+"</tr></thead><tbody>";
      table += rows.map(function(r){
        while (r.length < head.length) r.push("");
        return "<tr>"+r.slice(0,head.length).map(function(c){return "<td>"+inlineMd(esc(c))+"</td>";}).join("")+"</tr>";
      }).join("");
      html.push(table+"</tbody></table>");
      continue;
    }
    if ((m = line.match(/^\s*[-*•]\s+(.*)$/))) {
      flushPara(); flushQuote();
      if (!list || list.type!=="ul"){ flushList(); list = {type:"ul",items:[]}; }
      list.items.push(m[1]); continue;
    } else if (list && list.type==="ul") { flushList(); }
    if ((m = line.match(/^\s*\d+[.)]\s+(.*)$/))) {
      flushPara(); flushQuote();
      if (!list || list.type!=="ol"){ flushList(); list = {type:"ol",items:[]}; }
      list.items.push(m[1]); continue;
    } else if (list && list.type==="ol") { flushList(); }
    para.push(line);
  }
  flushAll();
  var out = html.join("");
  // ---- 数字守卫（自我修复）：渲染后可见数字必须与输入一致，否则回退纯文本渲染 ----
  // 统计时排除代码占位符（CODE0 里的 0 不是用户输入的数字）与截断标记。
  var countDigits = function(str){
    var clean = String(str||"").replace(/\u0000CODE\d+\u0000/g,"");
    return (clean.match(/\d/g) || []).length;
  };
  var digitsIn = countDigits(text);
  if (!truncated && digitsIn > 0){
    var digitsOut = countDigits(String(out).replace(/<[^>]*>/g, " "));
    if (digitsOut < digitsIn){
      // 极端兜底：转义原文 + 换行，保证任何字符（含数字）都原样可见
      out = "<p class=\"trunc-note\">（渲染自检发现内容丢失，已回退为纯文本显示）</p><pre class=\"md-fallback\">" + esc(text) + "</pre>";
    }
  }
  if (truncated) out += '<p class="trunc-note">内容过长，已截断显示。</p>';
  return out;
}
/* ---------- api / toast ---------- */
async function api(path,body){
  var init = {method: body===undefined ? "GET" : "POST", credentials:"same-origin", headers:{}};
  if (body !== undefined){
    init.headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }
  // 全局请求超时：网络异常（如系统 TCP 栈故障、后端无响应）时避免界面永久挂起。
  var timer = null;
  if (typeof AbortController !== "undefined"){
    var ctrl = new AbortController();
    init.signal = ctrl.signal;
    timer = setTimeout(function(){ try{ ctrl.abort(); }catch(e){} },20000);
  }
  var res;
  try {
    res = await fetch(path,init);
  } catch(e) {
    if (timer) clearTimeout(timer);
    throw e;
  }
  if (timer) clearTimeout(timer);
  var ct = res.headers.get("content-type")||"";
  var data = null;
  if (ct.indexOf("application/json")>=0) {
    try { data = await res.json(); } catch(e) { data = null; }
  } else {
    var txt = "";
    try { txt = await res.text(); } catch(e) {}
    data = {error:txt};
  }
  if (!res.ok || (data && data.ok === false)){
    throw new Error((data && data.error) || ("请求失败：HTTP "+res.status));
  }
  return data || {};
}
function toast(message,type,timeout){
  var wrap = $("#toasts");
  if (!wrap) return;
  if (type === "ok") playUiSound("success");
  else if (type === "error") playUiSound("error");
  // 成功提示会伴随一次很小的触点爆散：反馈分量与「复制/保存成功」这种轻动作匹配。
  if (type === "ok" && Date.now() - lastPointerAt < 1400) burstAt(lastPointerX, lastPointerY, 7);
  while (wrap.children.length >= 4) wrap.removeChild(wrap.firstChild);
  var node = document.createElement("div");
  node.className = "toast " + (type||"info");
  if (type === "error") node.setAttribute("role","alert");   // 错误需要打断式播报
  var ico = type==="ok" ? "checkCircle" : type==="error" ? "xCircle" : type==="warn" ? "alert" : "info";
  node.innerHTML = '<span class="toast-ico">'+icon(ico)+'</span><div class="toast-text">'+esc(message)+'</div><button class="toast-close" aria-label="关闭">'+icon("x")+'</button>';
  wrap.appendChild(node);
  var removed = false;
  function remove(){
    if (removed) return;
    removed = true;
    node.classList.add("out");
    setTimeout(function(){ if (node.parentNode) node.parentNode.removeChild(node); },190);
  }
  node.querySelector(".toast-close").addEventListener("click",remove);
  setTimeout(remove,timeout||4000);
}

/* ---------- 3.31 交互精修：触点水波 / 轻量爆散 / 数字滚动 / 加载骨架 ---------- */
var lastPointerX = 0, lastPointerY = 0, lastPointerAt = 0;

function burstAt(x, y, count){
  if (motionReduced()) return;
  if (!isFinite(x) || !isFinite(y)) return;
  var layer = document.createElement("div");
  layer.className = "gt-burst";
  layer.style.left = x + "px";
  layer.style.top = y + "px";
  var n = count || 8;
  for (var i=0;i<n;i++){
    var bit = document.createElement("i");
    var angle = (360 / n) * i + (Math.random() * 16 - 8);
    bit.style.setProperty("--ba", angle + "deg");
    bit.style.setProperty("--bdist", (14 + Math.random() * 17) + "px");
    bit.style.setProperty("--bd", (Math.random() * .04) + "s");
    layer.appendChild(bit);
  }
  document.body.appendChild(layer);
  setTimeout(function(){ if (layer.parentNode) layer.parentNode.removeChild(layer); }, 760);
}
function burstFromElement(el, count){
  if (!el || !el.getBoundingClientRect) return;
  var r = el.getBoundingClientRect();
  burstAt(r.left + r.width / 2, r.top + r.height / 2, count || 8);
}
function celebrateTaskDone(){
  var dot = document.getElementById("phaseDot");
  if (!dot) return;
  if (!motionReduced()) dot.classList.add("pop");
  setTimeout(function(){ dot.classList.remove("pop"); }, 700);
  burstFromElement(dot, 9);
}

/* 水波从真实触点扩散：父级用 .ripple-clip 裁成控件形状，反馈原点不跟控件中心走。 */
var RIPPLE_SKIP = ".tb-card,.no-ripple";
function bindRipple(){
  document.addEventListener("pointerdown", function(e){
    var btn = e.target && e.target.closest ? e.target.closest("button") : null;
    if (!btn || btn.disabled || btn.getAttribute("aria-disabled") === "true") return;
    if (btn.matches(RIPPLE_SKIP)) return;
    var r = btn.getBoundingClientRect();
    if (r.width < 24 || r.height < 18) return;
    var x = e.clientX - r.left, y = e.clientY - r.top;
    var size = Math.max(r.width, r.height) * 2.15;
    var dot = document.createElement("span");
    dot.className = "gt-ripple";
    dot.style.width = dot.style.height = size + "px";
    dot.style.left = x + "px";
    dot.style.top = y + "px";
    btn.classList.add("ripple-clip");
    btn.appendChild(dot);
    setTimeout(function(){ if (dot.parentNode) dot.parentNode.removeChild(dot); }, 620);
  }, {passive:true});
}

/* 数字滚动：工作台统计从旧值缓停在目标值，不滚到一半突然刹住。 */
function rollStatNumbers(scope){
  if (!scope) return;
  var nodes = scope.querySelectorAll(".stat b");
  for (var i=0;i<nodes.length;i++){
    (function(el){
      var raw = (el.textContent || "").trim();
      var m = raw.match(/^([0-9][0-9,]*)(%?)$/);
      if (!m) return;
      var target = parseInt(m[1].replace(/,/g,""), 10);
      if (!isFinite(target)) return;
      var shown = el.getAttribute("data-roll-cur");
      if (shown === String(target)) return;
      var suffix = m[2] || "";
      el.setAttribute("data-roll-cur", String(target));
      if (motionReduced() || shown === null){ el.textContent = fmtNum(target) + suffix; return; }
      var from = parseInt(shown, 10);
      if (!isFinite(from)) from = 0;
      var start = performance.now(), dur = 560;
      function frame(now){
        if (!el.isConnected) return;
        var p = Math.min(1, (now - start) / dur);
        var eased = 1 - Math.pow(1 - p, 3);
        var val = Math.round(from + (target - from) * eased);
        el.textContent = fmtNum(val) + suffix;
        if (p < 1) requestAnimationFrame(frame);
      }
      requestAnimationFrame(frame);
    })(nodes[i]);
  }
}

function renderSidebarSkeleton(){
  if (S) return;
  var list = document.getElementById("sessionList");
  if (!list || list.children.length) return;
  var html = "";
  for (var i=0;i<4;i++) html += '<div class="session-skel" aria-hidden="true"><i></i></div>';
  list.innerHTML = html;
}

function initMotionPolish(){
  bindRipple();
  document.addEventListener("pointerdown", function(e){
    lastPointerX = e.clientX; lastPointerY = e.clientY; lastPointerAt = Date.now();
  }, {passive:true});
}
function showFatal(message){
  document.getElementById("app").style.display = "none";
  var div = document.createElement("div");
  div.className = "fatal";
  div.innerHTML = '<div class="fatal-card"><h2>无法连接绿虎</h2><p>'+esc(message)+'<br><br>请从系统托盘图标重新打开绿虎浏览器。</p><button class="btn primary" id="fatalReload">重新加载</button></div>';
  document.body.appendChild(div);
  var btn = document.getElementById("fatalReload");
  if (btn) btn.addEventListener("click",function(){ location.reload(); });
}

/* ---------- derived state ---------- */
function displayAssistantText(m){
  var t = m.text||"";
  if (m.status === "streaming"){
    t = t.replace(/```greentiger[\s\S]*?```/g,"\n\n> 正在调用工具…\n\n");
    var idx = t.indexOf("```greentiger");
    if (idx >= 0 && t.indexOf("```",idx+3) < 0) t = t.slice(0,idx)+"\n\n> 正在调用工具…";
  }
  return t;
}
function chatSig(){
  if (!S || !S.active) return "none";
  var a = S.active;
  var parts = [a.id,String(S.config&&S.config.workDir||"")];
  if (!a.messages.length){
    var ds = dsInfo();
    parts.push("empty",ds.state);
    return parts.join("|");
  }
  for (var i=0;i<a.messages.length;i++){
    var m = a.messages[i];
    var txt = m.text||"";
    parts.push(m.id,m.status,String(txt.length),txt.slice(-24));
    var ts = m.toolCalls||[];
    for (var j=0;j<ts.length;j++){
      var t = ts[j];
      parts.push(t.id,t.status,String((t.output||"").length),t.approvalStatus||"",String((t.diff||"").length));
    }
  }
  return parts.join("|");
}
function syncRunStart(){
  var running = !!(S && S.status && S.status.running);
  if (running && !runStart) runStart = Date.now();
  if (!running) runStart = 0;
}
/* UI 版本自动刷新：更新后旧标签页（更新前加载的界面）检测到服务端 UI 版本变化，
 * 自动重载一次（sessionStorage 防循环）——半屏/全屏窗口不再出现新旧界面并存。 */
var uiVersionLoaded = "";
function checkUiVersion(){
  var uiv = (S && S.uiVersion) || "";
  if (!uiv) return;
  if (!uiVersionLoaded){ uiVersionLoaded = uiv; return; }
  if (uiv !== uiVersionLoaded){
    var key = "gt.uireload." + uiv;
    try {
      if (!sessionStorage.getItem(key)){
        sessionStorage.setItem(key, "1");
        location.reload();
      }
    } catch(e){ location.reload(); }
  }
}
function applyState(data){
  if (!data || !data.version) return;
  var wasRunning = !!(S && S.status && S.status.running);
  S = data;
  syncUiMotionPreference();
  /* 任务从「运行中」落到结束：给一次明确的收尾反馈。
   * 长任务常常被切到后台，没有这一下就只能靠回来翻日志判断成没成。 */
  if (wasRunning && !(S.status && S.status.running)){
    var endPhase = String((S.status && S.status.phase) || "");
    if (/出错|失败|中止|中断/.test(endPhase)) playUiSound("error");
    else playUiSound("success");
  }
  /* 启动时无活动会话：自动新建一个，默认进入居中问候页 */
  if (!bootSessionChecked && (!S.active || !S.active.id)){
    bootSessionChecked = true;
    setTimeout(function(){ newSession(); }, 0);
  }
  /* UI 主题以服务器配置为准：写进 localStorage，与浏览器外壳(ChromeTheme)保持同一套深浅。
   * 但用户刚点过主题按钮时（3 秒内）以本地为准，避免异步保存前的旧 state 把主题打回去。 */
  if (Date.now() >= themeLocalUntil){
    var serverTheme = (S && S.config && S.config.theme) || "light";
    try{ localStorage.setItem("greentiger.theme", serverTheme); }catch(e){}
    applyThemeSafe(serverTheme);
  }
  syncRunStart();
  if (!logsInit){ logsInit = true; logsOpen = !!(S.config && S.config.showInspector); syncInspectorClass(); }
  renderSidebar();
  renderHeader();
  renderStatus();
  renderBrainBanner();
  renderApproval();
  renderModeSelect();
  updateDocTitle();
  checkUiVersion();
  var sig = chatSig();
  if (sig !== lastChatSig) renderChat();
  renderAttachments();
  if (logsOpen) renderInspector();
  updateLogBadge();
  updateSendState();
  syncScrim();
  if (wasRunning && !(S.status && S.status.running)){
    var phase = S.status && S.status.phase || "";
    if (phase === "就绪"){ toast("任务已完成","ok",2600); celebrateTaskDone(); }
    else if (phase === "已暂停") toast("任务已暂停，可检查日志或新建会话","warn",3200);
  }
  var bm = document.getElementById("brandMark");
  if (bm) bm.classList.toggle("working", !!(S.status && S.status.running));
}
/* 免 Key 公益池（自定义端点里填匿名池的人）有限速——选中它时在输入框上方
 * 常驻提示排队/402/429 的可能性，直到用户换正式服务商或点「知道了」（本次页面内不再弹）。 */
var brainBannerDismissed = false;
function renderBrainBanner(){
  var b = document.getElementById("brainBanner");
  if (!b) return;
  var on = !!(S && S.config && S.config.brainTrial) && !brainBannerDismissed;
  b.hidden = on ? false : true;
  if (on){
    var t = document.getElementById("brainBannerText");
    if (t) t.textContent = S.config.brainTrialNote || "当前为免 Key 免费模型：匿名队列有限速，重任务建议换正式服务商。";
    var c = document.getElementById("brainBannerClose");
    if (c) c.onclick = function(){ brainBannerDismissed = true; b.hidden = true; };
  }
}
/* 离线自愈：只有首屏连不上才致命；运行中掉线改为指数退避重试，不再整页报废。 */
var offline = false, offlineWarned = false, offlineTimer = null, offlineDelay = 2000, stateBooted = false;
var themeLocalUntil = 0;
function setOffline(message){
  offline = true;
  var pt = document.getElementById("phaseText"), pd = document.getElementById("phaseDot");
  if (pt) pt.textContent = "已断开 · 正在重连";
  if (pd){ pd.className = "phase-dot err"; }
  if (!offlineWarned){
    offlineWarned = true;
    toast("与绿虎服务断开，正在自动重连…","warn",4200);
  }
  if (offlineTimer) return;
  offlineTimer = setTimeout(function(){
    offlineTimer = null;
    refreshState();
  }, offlineDelay);
  offlineDelay = Math.min(offlineDelay * 2, 30000);
}
function setOnline(){
  if (!offline) return;
  offline = false;
  offlineWarned = false;
  offlineDelay = 2000;
  toast("已重新连接绿虎","ok",2600);
}
async function refreshState(){
  try{
    var data = await api("/api/state");
    setOnline();
    stateBooted = true;
    applyState(data);
  }catch(e){
    if (!stateBooted){ showFatal(e.message); return; }
    setOffline(e.message);
  }
}
function updateDocTitle(){
  var title = (S && S.active && S.active.title) ? S.active.title : "绿虎";
  if (S && S.approval) title = "● 需要确认 · " + title;
  document.title = title + " · 绿虎";
  // 版本只从 /api/state 取，界面源码不写死版本号（单一事实源：src/Config.cs）。
  var ver = (S && S.version) ? "v"+S.version : "v…";
  var vl = document.getElementById("verLabel");
  if (vl) vl.textContent = ver;
  var iv = document.getElementById("inspectorVer");
  if (iv) iv.textContent = ver;
}

/* ---------- sidebar / header / status ---------- */
var lastSidebarHtml = "";
var showArchived = false;
// 多选：selectedSessions 用对象而不是 Set，和文件其它处保持同一套 ES5 写法
var selectMode = false;
var selectedSessions = Object.create(null);
var lastVisibleIds = [];
function renderSidebar(){
  if (!S) return;
  var list = document.getElementById("sessionList");
  if (!list) return;
  var all = S.sessions || [];
  var archivedCount = 0;
  for (var ai = 0; ai < all.length; ai++) if (all[ai].archived) archivedCount++;
  // 未归档 / 已归档：两个 tab 常驻（不再等到有归档项才出现），计数各归各位
  var tabA = document.getElementById("tabActive");
  var tabR = document.getElementById("tabArchived");
  if (tabA){
    tabA.classList.toggle("active", !showArchived);
    tabA.setAttribute("aria-selected", showArchived ? "false" : "true");
  }
  if (tabR){
    tabR.classList.toggle("active", showArchived);
    tabR.setAttribute("aria-selected", showArchived ? "true" : "false");
  }
  var activeCountEl = document.getElementById("sessionCount");
  if (activeCountEl) activeCountEl.textContent = String(all.length - archivedCount);
  var archivedCountEl = document.getElementById("archiveCount");
  if (archivedCountEl) archivedCountEl.textContent = String(archivedCount);
  // 归档会话默认隐藏；切到「已归档」时只显示归档项，可直接取消归档
  var sessions = all.filter(function(s){ return showArchived ? !!s.archived : !s.archived; });
  var q = sessionQuery.trim().toLowerCase();
  var visible = q ? sessions.filter(function(s){ return String(s.title||"").toLowerCase().indexOf(q) >= 0; }) : sessions;
  var html;
  if (!visible.length){
    html = '<div class="session-empty">'+(q?"没有匹配「"+esc(sessionQuery.trim())+"」的会话":(showArchived?"没有归档的会话":"还没有会话"))+'</div>';
  } else {
    var pinned = visible.filter(function(s){ return s.pinned; });
    var rest = visible.filter(function(s){ return !s.pinned; });
    html = pinned.length ? sessionGroupHtml("置顶", pinned, selectMode) : "";
    if (showArchived || q){
      html += rest.map(function(s){ return sessionItemHtml(s, selectMode, !!selectedSessions[s.id]); }).join("");
    } else {
      // 日期分组：今天 / 昨天 / 今年 / 更早
      var groups = [];
      var groupIdx = {};
      for (var g = 0; g < rest.length; g++){
        var gs = rest[g];
        var gk = dayGroup(gs.updatedAt);
        if (groupIdx[gk] === undefined){
          groupIdx[gk] = groups.length;
          groups.push({ label: gk, items: [] });
        }
        groups[groupIdx[gk]].items.push(gs);
      }
      for (var gi = 0; gi < groups.length; gi++){
        html += sessionGroupHtml(groups[gi].label, groups[gi].items, selectMode);
      }
    }
  }
  lastVisibleIds = visible.map(function(s){ return s.id; });
  syncBulkBar();
  // 内容未变则不重建：高频状态推送下保住侧栏滚动位置与键盘焦点
  if (html === lastSidebarHtml) return;
  lastSidebarHtml = html;
  var keepTop = list.scrollTop;
  list.innerHTML = html;
  list.scrollTop = keepTop;
}
function renderHeader(){
  if (!S) return;
  var wd = S.config && S.config.workDir || "";
  var wl = document.getElementById("workdirLabel");
  var wc = document.getElementById("workdirChip");
  if (wl){
    wl.textContent = wd ? shortPath(wd,62) : "选择工作目录";
    wl.title = wd || "";
  }
  if (wc){
    wc.className = "workdir-chip" + (wd ? "" : " missing");
    wc.title = wd ? ("工作目录：" + wd + " · 点击更换") : "点击选择工作目录";
  }
  // 顶栏只留图标入口；完整路径在底部状态栏这一处展示（title 里保留全路径便于查看）
  var sw = document.getElementById("statusWorkdir");
  if (sw){
    sw.textContent = wd ? shortPath(wd,80) : "";
    sw.title = wd || "";
    sw.hidden = !wd;
  }
  var conn = connInfo();
  var dot = document.getElementById("dsChipDot");
  if (dot) dot.className = "dot " + conn.cls;
  var chipText = document.getElementById("dsChipText");
  if (chipText){
    // 胶囊写的就是当前模型名；没接入模型时写「未接入模型」，点开直接去接
    chipText.textContent = conn.chip;
  }
  var chipBtn = document.getElementById("modelBtn");
  if (chipBtn){
    chipBtn.className = "ds-chip" + (conn.cls === "ok" ? " ready" : "");
    chipBtn.title = "点击切换模型";
    chipBtn.setAttribute("aria-label", "当前模型：" + (chipText ? chipText.textContent : conn.chip) + "，点击切换");
  }
  var themeButton = document.getElementById("themeBtn");
  if (themeButton){
    var darkTheme = document.documentElement.dataset.theme === "dark";
    var targetTheme = darkTheme ? "light" : "dark";
    // 主题按钮已收进「⋯」菜单：只换图标，保留菜单项的文字标签
    var themeIco = document.getElementById("themeIco");
    if (themeIco) themeIco.innerHTML = icon(targetTheme === "light" ? "sun" : "moon");
    else themeButton.innerHTML = icon(targetTheme === "light" ? "sun" : "moon");
    themeButton.title = "切换到" + (targetTheme === "light" ? "浅色" : "深色") + "主题";
    themeButton.setAttribute("aria-label",themeButton.title);
    themeButton.setAttribute("aria-pressed",darkTheme ? "true" : "false");
  }
  var stop = document.getElementById("stopTopBtn");
  if (stop) stop.hidden = !(S.status && S.status.running);
  var pause = document.getElementById("pauseTopBtn");
  if (pause) pause.hidden = !(S.status && S.status.running);
  // 中栏顶部写明当前会话：三栏布局里，用户第一眼要知道「我在哪个对话里」。
  var topSession = document.getElementById("topSessionName");
  if (topSession){
    var activeTitle = (S.active && S.active.title) || "新会话";
    topSession.textContent = activeTitle;
    topSession.title = activeTitle;
  }
}
function renderStatus(){
  if (!S) return;
  var running = !!(S.status && S.status.running);
  var phase = (S.status && S.status.phase) || (running ? "运行中…" : "就绪");
  var phaseText = document.getElementById("phaseText");
  if (phaseText) phaseText.textContent = phase;
  // 顶栏身份处同步运行状态：不用把视线拉到页脚也能知道当前是否在干活。
  var topIdentity = document.getElementById("topIdentity");
  if (topIdentity) topIdentity.classList.toggle("running", running);
  // Claude Code 的 statusline 语汇：权限模式作为一段，⏵⏵ 标记 accept edits，⏵⏵⏵ 标记 auto
  var modeBadge = document.getElementById("modeBadge");
  if (modeBadge){
    var mode = (S.config && S.config.mode) || "ask";
    modeBadge.textContent = mode === "acceptEdits" ? "⏵⏵ 自动接受编辑"
      : mode === "auto" ? "⏵⏵⏵ 自动执行"
      : mode === "plan" ? "规划设计模式" : "";
    modeBadge.style.display = modeBadge.textContent ? "" : "none";
  }
  var phaseDot = document.getElementById("phaseDot");
  if (phaseDot){
    phaseDot.className = "phase-dot" + (running ? " busy" : (phase.indexOf("出错")>=0 ? " err" : ""));
  }
  var stats = S.stats || {};
  var limit = stats.contextLimit || 60000;
  var chars = stats.contextChars || 0;
  var pct = limit ? Math.min(100,Math.round(chars/limit*100)) : 0;
  var ctx = document.getElementById("contextText");
  if (ctx){
    ctx.textContent = fmtNum(chars) + " / " + fmtNum(limit) + " · " + pct + "%";
    ctx.title = "上下文占用 " + pct + "%（上限 " + fmtNum(limit) + " 字符）";
  }
  var bar = document.getElementById("ctxBarFill");
  if (bar) bar.style.background = pct >= 90 ? "var(--red)" : (pct >= 75 ? "var(--amber)" : "var(--accent)");
  var fill = document.getElementById("ctxBarFill");
  if (fill) fill.style.width = pct+"%";
  var st = document.getElementById("statsText");
  if (st) st.textContent = fmtNum(stats.tools||0)+" 次工具 · "+fmtNum(stats.turns||0)+" 轮";
  var gb = document.getElementById("gitBranch");
  if (gb){
    var br = S.status && S.status.gitBranch;
    gb.textContent = br || "";
    gb.style.display = br ? "" : "none";
  }
  // 顶栏的模型 / 推理强度指示：composer 段在 IIFE 内，通过 window 上暴露的引用刷新
  if (window.renderComposerSelectors) window.renderComposerSelectors();
  // 主页底部那行「工作目录 · 模型 · 状态」也要跟着走，否则切完模型它还写着旧名字
  var homeStatus = document.getElementById("homeStatus");
  if (homeStatus) homeStatus.textContent = homeStatusLine();
  updateSendState();
}
function updateSendState(){
  var send = document.getElementById("sendBtn");
  if (!send) return;
  var running = !!(S && S.status && S.status.running);
  if (running){
    send.className = "send-btn stop";
    send.innerHTML = icon("stop");
    send.title = "停止任务";
    send.setAttribute("aria-label","停止任务");
    send.disabled = false;
  } else {
    send.className = "send-btn";
    send.innerHTML = icon("send");
    send.title = "发送";
    send.setAttribute("aria-label","发送");
    var has = false;
    var input = document.getElementById("input");
    if (input && input.value.trim()) has = true;
    if (attachments.length) has = true;
    send.disabled = sending || uploading || !has;
  }
  var input = document.getElementById("input");
  if (input){
    input.placeholder = running
      ? "任务运行中，可先输入…"
      : "描述你要做的事…";
  }
  var stop = document.getElementById("stopTopBtn");
  if (stop) stop.hidden = !running;
  var pause = document.getElementById("pauseTopBtn");
  if (pause) pause.hidden = !running;
}
/* ---------- chat rendering ---------- */
function homeStatusLine(){
  if (!S) return "";
  // 工作目录路径已挪到底部状态栏，主页状态行只讲模型连接状态，避免重复刷同一条路径
  var bi = brainInfo();
  // 未接入时也把选定的模型名带出来（deepseek-v4-pro · 未接入），不让主页状态行把当前选择说丢
  return bi.active === "provider" ? (bi.short + " · 已就绪") : bi.short;
}
function emptyHtml(){
  // 空态：一个小园景 + 一句提问，其余交给留白。
  var status = homeStatusLine();
  // 主页不放接入引导组件：模型配置统一从输入栏的「未接入模型」胶囊进入。
  return '<div class="empty home-empty">'
    +'<section class="home-garden" aria-label="绿虎花园">'
      +'<canvas id="emptyGarden" class="garden-home-canvas" width="300" height="300" aria-hidden="true"></canvas>'
      +'<div class="garden-home-shade" aria-hidden="true"></div>'
      +'<div class="garden-home-content"><button type="button" class="btn garden-open" id="gardenOpenBtn" aria-label="进入花园" title="进入花园">'+icon("spark")+'<span>进入花园</span></button></div>'
    +'</section>'
    /* 对标 ChatGPT 的居中问候语：轻量、只做品牌温度，不喧宾夺主（类名刻意避开 home-title，
       否则会命中 e2e「主页已移除问候语」护栏；旧的昵称式问候语早已移除，此处不再复现） */
    +'<div class="home-hero">'
      +'<h1 class="home-hero-title">今天想做点什么？</h1>'
      +'<p class="home-hero-sub">说出目标，绿虎会拆解步骤、调用工具，复杂产出落到右侧工作台。</p>'
    +'</div>'
    +'<div class="empty-status" id="homeStatus">'+esc(status)+'</div>'
  +'</div>';
}
/* 界面偏好：已迁入「设置 → 应用行为」。命令面板仍可即时切换，
   这里只保留一份读取规则，供 toggleHomePref 与设置面板共用。 */
/* 命令面板即时切换后，若设置面板正开着，把两个复选框同步到最新状态。
   用 querySelector 是因为复选框 id 由 toggleRowHtml 动态生成，不在静态标记里。 */
function syncHomePrefs(){
  var c = (S && S.config) || {};
  var motion = document.querySelector("#setUiMotion");
  var sounds = document.querySelector("#setUiSounds");
  if (motion) motion.checked = c.uiMotion !== false;
  if (sounds) sounds.checked = !!c.uiSounds;
}
function toggleHomePref(key){
  if (!S || !S.config) return;
  var def = null;
  for (var i=0;i<HOME_PREFS.length;i++) if (HOME_PREFS[i].key === key) def = HOME_PREFS[i];
  if (!def) return;
  var next = !def.read(S.config);
  var patch = {}; patch[key] = next;
  S.config[key] = next;                       // 先本地生效，提示音开启后这一下就能听见
  if (key === "uiMotion") syncUiMotionPreference();
  syncHomePrefs();
  api("/api/config", patch).then(function(st){
    if (st && st.version) applyState(st);
    syncHomePrefs();
  }).catch(function(e){
    S.config[key] = !next;                    // 保存失败就回滚，避免界面和真实配置不一致
    if (key === "uiMotion") syncUiMotionPreference();
    syncHomePrefs();
    toast("偏好未保存："+e.message,"error");
  });
}
function isToolExpanded(t){
  if (expandedTools.has(t.id)) return true;
  if (collapsedTools.has(t.id)) return false;
  return t.status==="running" || t.status==="awaiting" || t.status==="error" || t.status==="denied";
}
/* ---- 文件工具差异视图：展示执行时记录的真实前后内容 ---- */
/* 折叠态的 ⎿ 摘要：取输出第一行有内容的，其余行数用「+N 行」收口。
 * 这一行是 Claude Code 的招牌：不展开也知道发生了什么。 */
function toolHtml(t){
  if (t.name === "plan") return planToolHtml(t);
  if (t.name === "todo") return todoToolHtml(t);
  var meta = toolMeta(t.name);
  var isSub = t.name === "task";
  var subState = isSub ? ({running:"进行中",done:"已完成",error:"失败",denied:"已停止",awaiting:"等待中"}[t.status] || "处理中") : "";
  var open = isToolExpanded(t);
  var risk = t.risk === "danger" ? '<span class="badge err">高危</span>' : "";
  var appr = t.approvalStatus ? ({auto:"自动执行",allow:"已允许",always:"本任务内免确认",denied:"已拒绝"}[t.approvalStatus]||t.approvalStatus) : "";
  var stat = toolStatusLabel(t);
  // 状态图标：运行中 Spinner / 完成勾 / 失败叉；其余状态沿用终端语汇的圆点
  var stateIcon = '<span class="tool-mark" aria-hidden="true">\u23fa</span>';
  if (t.status === "running") stateIcon = '<span class="tool-state spin" aria-hidden="true"></span>';
  else if (t.status === "error" || t.status === "denied") stateIcon = '<span class="tool-state bad" aria-hidden="true">'+icon("x")+'</span>';
  else if (t.status === "done") stateIcon = '<span class="tool-state done" aria-hidden="true">'+icon("check")+'</span>';
  var dur = (t.durationMs>0 && t.status!=="running") ? '<span class="tool-dur">'+fmtDuration(t.durationMs)+'</span>' : "";
  var argsText = t.name==="shell" ? (t.command||prettyArgs(t)) : prettyArgs(t);
  var summary = toolSummary(t);
  var output = t.output||"";
  var artifacts = (t.artifacts||[]).map(artifactHtml).join("");
  var body = "";
  if (t.riskReason){
    body += '<div class="risk-note '+(t.risk==="danger"?"danger":"")+'">'+icon("alert")+'<span>'+esc(t.riskReason)+'</span></div>';
  }
  if (t.name === "write_file" || t.name === "edit"){
    // 展示执行时保存的前后差异，避免用已写入的当前文件伪造“旧版本”。
    var wargs = {};
    try{ var wo = JSON.parse(t.argsJson||"{}"); wargs = wo.args||wo; }catch(e){}
    var wpath = wargs.path || wargs.file || "";
    if (wpath) body += '<div class="tool-section"><div class="tool-label"><span>文件</span></div><pre><code>'+esc(wpath)+'</code></pre></div>';
    var rollbackBtn = (t.backupPath && t.status === "done" && wpath)
      ? '<button class="copy-btn" data-rollback-file="'+attr(wpath)+'" data-rollback-snapshot="'+attr(t.backupPath)+'" title="把文件恢复到这次改动之前（当前内容会先另存快照）">回滚到改动前</button>'
      : "";
    body += '<div class="tool-section tool-diff"><div class="tool-label"><span>变更差异</span>'+rollbackBtn+'</div><pre><code>'+toolDiffHtml(t)+'</code></pre></div>';
  } else if (argsText){
    var ckRollback = (t.name==="shell" && t.status==="done" && t.backupPath && String(t.backupPath).indexOf("gtckpt:")===0)
      ? '<button class="copy-btn" data-rollback-ckpt="'+attr(String(t.backupPath).slice(7))+'" title="把工作目录恢复到这条命令执行前（当前状态会先另存新档）">回滚到命令前</button>'
      : "";
    body += '<div class="tool-section"><div class="tool-label"><span>'+(t.name==="shell"?"命令":"参数")+'</span>'+ckRollback+'<button class="copy-btn" data-copy-code>复制</button></div><pre><code>'+esc(argsText)+'</code></pre></div>';
  }
  if (t.status==="running" && !output){
    body += '<div class="tool-running"><span class="spark">\u273b</span><span>执行中…</span><em>esc 中断</em></div>';
  } else if (output){
    body += '<div class="tool-section tool-output"><div class="tool-label"><span>输出</span><button class="copy-btn" data-copy-code>复制</button></div><pre><code>'+esc(output)+'</code></pre></div>';
  } else if (t.status==="denied"){
    body += '<div class="tool-section tool-output"><div class="tool-label"><span>输出</span></div><pre><code>用户拒绝执行。</code></pre></div>';
  } else if (t.status==="done"){
    body += '<div class="tool-section tool-output"><div class="tool-label"><span>输出</span></div><pre><code>（无输出）</code></pre></div>';
  }
  if (artifacts) body += '<div class="tool-artifacts">'+artifacts+'</div>';
  // 悬浮标题写全「工具名：参数」，一眼知道这条调用具体执行了什么
  var titleAttr = meta.label + (summary ? "：" + summary : "") + (appr ? " · " + appr : "") + (t.source==="auto" ? " · 自动执行" : "");
  return '<div class="tool'+(open?" open":"")+(stat&&stat.cls==="err"?" out-error":"")+(isSub?" subagent":"")+'" data-status="'+attr(t.status)+'" data-tool-id="'+attr(t.id)+'">'
    +'<button class="tool-head" data-tool-toggle="'+attr(t.id)+'" aria-expanded="'+(open?"true":"false")+'" title="'+attr(titleAttr)+'">'
      +stateIcon
      +icon(meta.icon,"tool-ico")
      +'<span class="tool-name">'+esc(isSub ? "子智能体" : meta.label)+'</span>'
      +(isSub ? '<span class="subagent-state '+attr(t.status)+'">'+esc(subState)+'</span>' : "")
      +'<span class="tool-args">'+esc(summary)+'</span>'
      +'<span class="tool-side">'+risk+(stat?'<span class="tool-stat '+stat.cls+'">'+esc(stat.text)+'</span>':"")+dur+icon("chevronDown","tool-chev")+'</span>'
    +'</button>'
    + toolOutPreview(t, open)
    +'<div class="tool-body"'+(open?"":" hidden")+'>'+body+'</div>'
  +'</div>';
}
/* 计划卡片（plan）：步骤清单，批准前 ○，完成后显示已批准状态。
 * 计划是「任务骨架」，默认展开——除非用户自己点了折叠。 */
function planToolHtml(t){
  var p = planInfo(t);
  var open = expandedTools.has(t.id) || !collapsedTools.has(t.id);
  var appr = t.approvalStatus ? ({auto:"自动执行",allow:"已允许",always:"本任务内免确认",denied:"已拒绝"}[t.approvalStatus]||t.approvalStatus) : "";
  var rows = (p.steps||[]).map(function(s){
    var done = /^\s*\[x\]/i.test(String(s));
    return '<li class="plan-step'+(done?" done":"")+'">'+(done?'<span class="plan-mark done">✓</span>':'<span class="plan-mark">○</span>')+'<span>'+esc(String(s).replace(/^\s*\[[ x]\]\s*/i,""))+'</span></li>';
  }).join("");
  var card = '<div class="plan-card"><div class="plan-head">'+icon("list")+'<span>'+esc(p.title||"计划")+'</span><span class="plan-count">'+(p.steps?p.steps.length:0)+' 步</span></div><ul class="plan-steps">'+(rows||'<li class="plan-step"><span>（未列出步骤）</span></li>')+'</ul></div>';
  var body = card;
  if (t.output) body += '<div class="tool-section tool-output"><div class="tool-label"><span>结果</span></div><pre><code>'+esc(t.output)+'</code></pre></div>';
  var done = 0;
  (p.steps||[]).forEach(function(x){ if (/^\s*\[x\]/i.test(String(x))) done++; });
  return '<div class="tool plan-tool'+(open?" open":"")+'" data-status="'+attr(t.status)+'" data-tool-id="'+attr(t.id)+'">'
    +'<button class="tool-head" data-tool-toggle="'+attr(t.id)+'" aria-expanded="'+(open?"true":"false")+'" title="plan'+(appr?" · "+appr:"")+'">'
    +'<span class="tool-mark" aria-hidden="true">\u23fa</span>'
    +'<span class="tool-name">计划</span>'
    +'<span class="tool-args">'+esc(p.title||"")+'</span>'
    +'<span class="tool-side"><span class="tool-dur">'+done+'/'+(p.steps?p.steps.length:0)+'</span>'+icon("chevronDown","tool-chev")+'</span>'
    +'</button><div class="tool-body"'+(open?"":" hidden")+'>'+body+'</div></div>';
}
/* 进度卡片（todo）：随 plan 步骤状态更新，done 打勾。 */
function todoToolHtml(t){
  var items = [];
  try{ var o = JSON.parse(t.argsJson||"{}"); var a = o.args||o; if (Array.isArray(a.items)) items = a.items; }catch(e){}
  var open = expandedTools.has(t.id) || !collapsedTools.has(t.id);
  var done = 0;
  items.forEach(function(it){ if (it && it.status === "done") done++; });
  var rows = items.map(function(it){
    var title = (it && (it.title || it.name)) || "";
    var status = (it && it.status) || "pending";
    var mark = status === "done" ? '<span class="plan-mark done">✓</span>' : (status === "doing" ? '<span class="plan-mark doing">→</span>' : '<span class="plan-mark">○</span>');
    return '<li class="plan-step'+(status==="done"?" done":"")+'">'+mark+'<span>'+esc(title)+'</span></li>';
  }).join("");
  var card = '<div class="plan-card"><div class="plan-head">'+icon("checkCircle")+'<span>进度</span><span class="plan-count">'+done+'/'+items.length+'</span></div><ul class="plan-steps">'+(rows||'<li class="plan-step"><span>（无条目）</span></li>')+'</ul></div>';
  var body = card;
  if (t.output) body += '<div class="tool-section tool-output"><div class="tool-label"><span>结果</span></div><pre><code>'+esc(t.output)+'</code></pre></div>';
  return '<div class="tool plan-tool'+(open?" open":"")+'" data-status="'+attr(t.status)+'" data-tool-id="'+attr(t.id)+'">'
    +'<button class="tool-head" data-tool-toggle="'+attr(t.id)+'" aria-expanded="'+(open?"true":"false")+'" title="todo">'
    +'<span class="tool-mark" aria-hidden="true">\u23fa</span>'
    +'<span class="tool-name">进度</span>'
    +'<span class="tool-args">'+done+'/'+items.length+' 项完成</span>'
    +'<span class="tool-side">'+icon("chevronDown","tool-chev")+'</span>'
    +'</button><div class="tool-body"'+(open?"":" hidden")+'>'+body+'</div></div>';
}
function messageHtml(m, isLastAssistant, idx, lastUserIdx){
  if (m.role === "system") return systemMessageHtml(m);
  if (m.role === "user"){
    var parsed = splitAttachments(m.text);
    var atts = parsed.files.map(function(p){
      return '<span class="msg-att" title="'+attr(p)+'">'+icon("file")+'<span>'+esc(baseName(p))+'</span></span>';
    }).join("");
    var editing = editingIndex === idx;
    var undoBtn = idx === lastUserIdx ? '<button class="msg-op user" data-undo-msg="'+idx+'" title="撤回这条消息（删除其后的所有回复）">'+icon("undo")+'<span>撤回</span></button>' : "";
    return '<div class="msg user'+(editing?" editing":"")+'" data-msg="'+attr(m.id)+'">'+avatarHtml()+'<div class="user-wrap">'
      +'<div class="user-bubble"><div class="md">'+renderMd(parsed.body.join("\n").trim())+'</div>'+(atts?'<div class="msg-atts">'+atts+'</div>':"")+'</div>'
      +'<div class="user-meta">你 · '+fmtTime(m.time)
      +'<button class="msg-op user'+(editing?" on":"")+'" data-edit-msg="'+idx+'" title="编辑这条消息并重新运行">'+icon("edit")+'<span>'+(editing?"编辑中":"编辑")+'</span></button>'
      +undoBtn
      +'<button class="msg-op user" data-copy-msg="'+attr(m.id)+'" title="复制消息">复制</button>'
      +'<button class="msg-op user" data-branch-msg="'+attr(m.id)+'" title="以这条消息为起点新开会话（对齐主流 agent 的分支）">分支</button>'
      +'</div></div></div>';
  }
  if (m.role === "system"){
    return '<div class="system-msg" data-msg="'+attr(m.id)+'">'+icon("alert")+'<div class="md">'+renderMd(m.text)+'</div></div>';
  }
  var streaming = m.status === "streaming";
  var display = displayAssistantText(m);
  var content = display.trim() ? renderMd(display)
    : (streaming ? '<span class="thinking"><span class="spark">✻</span><span>思考中</span><em data-elapsed>· 0s</em><em>· esc 中断</em></span>'
      : '<p class="trunc-note">（无内容）</p>');
  var status = streaming ? '<span class="msg-status"><span class="dot busy"></span>生成中</span>' : (m.status==="error" ? '<span class="msg-status err">回复失败</span>' : "");
  var tools = (m.toolCalls||[]).map(toolHtml).join("");
  var ops = '<div class="msg-ops">'
    +'<button class="msg-op" data-copy-msg="'+attr(m.id)+'" title="复制内容">'+icon("copy")+'<span>复制</span></button>'
    +(isLastAssistant && !streaming && m.status !== "error" ? '<button class="msg-op" data-retry-msg="'+attr(m.id)+'" title="重新生成这条回复">'+icon("refresh")+'<span>重试</span></button>' : "")
    +'</div>';
  /* 对标 ChatGPT：助手消息左侧品牌头像 + 全宽正文，无气泡；谁在说话由头像表达 */
  return '<div class="msg assistant'+(streaming?" streaming":"")+'" data-msg="'+attr(m.id)+'">'
    +avatarHtml()
    +'<div class="msg-body"><div class="msg-head">'+status+'<span class="msg-time">'+fmtTime(m.time)+'</span></div>'
    +'<div class="md">'+content+'</div>'
    +(tools?'<div class="tools">'+tools+'</div>':"")
    +ops
    +'</div></div>';
}
/* 系统消息（斜杠命令输出 / 压缩标记 / 运行提示）：轻量虚线卡片，不抢视觉。 */
function systemMessageHtml(m){
  var err = m.status === "error";
  var body = renderMd((m.text||"").trim());
  var copy = '<button class="msg-op" data-copy-msg="'+attr(m.id)+'" title="复制内容">'+icon("copy")+'<span>复制</span></button>';
  return '<div class="msg system'+(err?" error":"")+'" data-msg="'+attr(m.id)+'">'
    +'<div class="sys-body"><div class="sys-head">'+icon(err?"alert":"info")+'<span>'+esc(err?"提示":"系统")+'</span><span class="msg-time">'+fmtTime(m.time)+'</span>'+copy+'</div>'
    +'<div class="md">'+body+'</div></div></div>';
}
/* ---------- 对话区的增量渲染 ----------
 * 以前 renderChat 每次签名变化就把整个会话 innerHTML 重建一遍：
 * 每一段代码重新跑语法高亮、每一处差异重新解析。二十几条消息之后，
 * 每收到一个流式分片都要全量重算，长会话会明显发涩。
 *
 * 现在按「每条消息一个 key」做 keyed diff：key 里带上这条消息自己的签名
 * （文本长度 / 尾部片段 / 状态 / 工具状态），以及会影响它外观的上下文位
 * （是否最后一条回复、是否最后一条提问、是否正在编辑）。
 * 于是一条新消息只多一个节点，改一条消息只重建那一个节点，
 * 其它消息的原样留在 DOM 里，节点上的展开状态、选区都不会被冲掉。
 */
function msgRenderKey(m, idx, isLastAssistant, lastUserIdx){
  return (m.id || "idx"+idx) + "#" + msgSig(m)
    + "@" + (isLastAssistant ? 1 : 0) + (idx === lastUserIdx ? 1 : 0) + (editingIndex === idx ? 1 : 0);
}

/* 把 chat 的子节点调成 wanted 的顺序与内容，复用 key 相同的节点。 */
function reconcileMessages(chat, wanted){
  var pool = {};
  var child = chat.firstElementChild;
  while (child){
    var next = child.nextElementSibling;
    var key = child.getAttribute("data-mkey");
    // 没有 key（老手工节点）或 key 重复的节点直接回收，避免两份同 key 时第二个永远不被更新
    if (key && !pool[key]) pool[key] = child;
    else child.parentNode.removeChild(child);
    child = next;
  }
  for (var i = 0; i < wanted.length; i++){
    var el = pool[wanted[i].key];
    if (el){
      delete pool[wanted[i].key];
    } else {
      var tmp = document.createElement("div");
      tmp.innerHTML = wanted[i].html;
      el = tmp.firstElementChild;
      if (!el) continue;
      el.setAttribute("data-mkey", wanted[i].key);
    }
    var at = chat.children[i];
    if (at !== el) chat.insertBefore(el, at || null);
  }
  for (var stale in pool){
    if (Object.prototype.hasOwnProperty.call(pool, stale) && pool[stale].parentNode === chat){
      chat.removeChild(pool[stale]);
    }
  }
}

function destroyEmptyGarden(){
  if (emptyGardenApi && emptyGardenApi.destroy){ try { emptyGardenApi.destroy(); } catch(e){} }
  emptyGardenApi = null;
}
function mountEmptyGarden(){
  var canvas = document.getElementById("emptyGarden");
  var garden = window.GreenTigerGarden;
  if (!canvas || !garden || !garden.mount) return;
  emptyGardenApi = garden.mount(canvas,{compact:true,seed:20261027,reducedMotion:motionReduced()});
}
function paintGardenStatus(){
  var status = document.getElementById("gardenStatusText");
  var st = gardenOverlayApi && gardenOverlayApi.stats;
  if (status){
    status.textContent = !st ? "花园准备中…"
      : st.reducedMotion ? "静静展示 · 已降低动态效果"
      : st.paused ? "暂歇片刻 · " + st.plants + " 株花草"
      : (gardenWindOn ? "微风轻拂" : "自然生长") + " · " + st.plants + " 株花草";
  }
  var pause = document.getElementById("gardenPauseBtn");
  var pauseLabel = document.getElementById("gardenPauseLabel");
  if (pause && st){
    pause.setAttribute("aria-pressed",st.paused?"true":"false");
    pause.setAttribute("aria-label",st.paused?"继续花园生长":"暂停花园生长");
    pause.title = st.paused ? "继续生长" : "暂停生长";
    if (pauseLabel) pauseLabel.textContent = st.paused ? "继续" : "暂停";
  }
}
function openGardenView(){
  var view = document.getElementById("gardenView");
  var canvas = document.getElementById("gardenCanvas");
  var garden = window.GreenTigerGarden;
  if (!view || !canvas || !garden || !garden.mount){ toast("花园暂时无法打开","error"); return; }
  if (!view.hidden) return;
  gardenLastFocus = document.activeElement;
  destroyEmptyGarden();
  view.hidden = false;
  gardenWindOn = false;
  var windButton = document.getElementById("gardenWindBtn");
  if (windButton) windButton.setAttribute("aria-pressed","false");
  gardenOverlayApi = garden.mount(canvas,{seed:(Date.now() & 0x7fffffff),reducedMotion:motionReduced()});
  paintGardenStatus();
  var close = document.getElementById("gardenCloseBtn");
  if (close) setTimeout(function(){ close.focus(); },40);
}
function closeGardenView(){
  var view = document.getElementById("gardenView");
  if (!view || view.hidden) return;
  view.hidden = true;
  if (gardenOverlayApi && gardenOverlayApi.destroy){ try { gardenOverlayApi.destroy(); } catch(e){} }
  gardenOverlayApi = null;
  mountEmptyGarden();
  // 整张卡片也能点开花园，此时 activeElement 是 body：别把焦点还给 body，还给入口按钮。
  var last = gardenLastFocus;
  var focus = (last && last.isConnected && last !== document.body && last.focus) ? last : document.getElementById("gardenOpenBtn");
  if (focus && focus.focus) setTimeout(function(){ focus.focus(); },20);
}
function bindGardenOverlay(){
  var view = document.getElementById("gardenView");
  if (!view) return;
  document.addEventListener("click",function(e){
    var t = e.target;
    if (!t || !t.closest) return;
    if (t.closest("#gardenOpenBtn") || t.closest(".home-garden")) openGardenView();
  });
  var wind = document.getElementById("gardenWindBtn");
  if (wind) wind.addEventListener("click",function(){
    gardenWindOn = !gardenWindOn;
    wind.setAttribute("aria-pressed",gardenWindOn?"true":"false");
    if (gardenOverlayApi && gardenOverlayApi.setWind) gardenOverlayApi.setWind(gardenWindOn?0.92:0.42);
    paintGardenStatus();
  });
  var replay = document.getElementById("gardenReplayBtn");
  if (replay) replay.addEventListener("click",function(){
    if (gardenOverlayApi && gardenOverlayApi.replay) gardenOverlayApi.replay();
    paintGardenStatus();
  });
  var pause = document.getElementById("gardenPauseBtn");
  if (pause) pause.addEventListener("click",function(){
    if (gardenOverlayApi && gardenOverlayApi.togglePause) gardenOverlayApi.togglePause();
    paintGardenStatus();
  });
  var close = document.getElementById("gardenCloseBtn");
  if (close) close.addEventListener("click",closeGardenView);
  document.addEventListener("keydown",function(e){
    if (!view || view.hidden) return;
    if (e.key === "Escape"){
      e.preventDefault(); e.stopImmediatePropagation(); closeGardenView(); return;
    }
    if (e.key === "Tab"){
      var focusables = view.querySelectorAll("button:not(:disabled)");
      if (!focusables.length) return;
      var first = focusables[0], last = focusables[focusables.length-1];
      if (e.shiftKey && (document.activeElement === first || !view.contains(document.activeElement))){
        e.preventDefault(); last.focus();
      } else if (!e.shiftKey && (document.activeElement === last || !view.contains(document.activeElement))){
        e.preventDefault(); first.focus();
      }
    }
  },true);
}
function renderChat(){
  if (!S) return;
  var sc = document.getElementById("chatScroll");
  var chat = document.getElementById("chat");
  if (!sc || !chat) return;
  var stick = (sc.scrollHeight - sc.scrollTop - sc.clientHeight) < 130;
  var dist = sc.scrollHeight - sc.scrollTop - sc.clientHeight;
  var a = S.active;
  var moved = chat.dataset.session !== (a?a.id:"");
  if (moved && editingIndex >= 0){
    editingIndex = -1;
    var banner = document.getElementById("editBanner");
    if (banner) banner.hidden = true;
  }
  // 切换会话时清空自动展开记录，避免长会话里无限累积已不存在的工具 id
  if (moved){ expandedTools.clear(); collapsedTools.clear(); lastInspectorHtml = ""; }
  var sig = chatSig();
  if (moved) destroyEmptyGarden();
  var tools = [];
  for (var i=0;i<a.messages.length;i++){
    var ts = a.messages[i].toolCalls||[];
    for (var j=0;j<ts.length;j++){
      if (ts[j].status==="running" || ts[j].status==="awaiting") expandedTools.add(ts[j].id);
    }
  }
  if (!a.messages.length){
    // 只有「刚切进来」或「还没有空态 DOM」时才整块重建；之后只改底部状态行的文字。
    // 只有「刚切进来」或「还没有空态 DOM」时才整块重建；之后只改底部状态行的文字，
    // 否则画布会被 destroy 再从种子重新生长，动画永远从头开始。
    if (moved || !chat.querySelector(".home-empty")){
      destroyEmptyGarden();
      chat.innerHTML = emptyHtml();
      mountEmptyGarden();
    } else {
      var stLine = document.getElementById("homeStatus");
      if (stLine) stLine.textContent = homeStatusLine();
    }
  } else {
    var lastAssIdx = -1;
    var lastUserIdx = -1;
    for (var li = a.messages.length - 1; li >= 0; li--){
      if (a.messages[li].role === "assistant"){ if (lastAssIdx < 0) lastAssIdx = li; }
      else if (a.messages[li].role === "user" && lastUserIdx < 0){ lastUserIdx = li; }
      if (lastAssIdx >= 0 && lastUserIdx >= 0) break;
    }
    reconcileMessages(chat, a.messages.map(function(m, idx){
      return {
        key: msgRenderKey(m, idx, m.role === "assistant" && idx === lastAssIdx, lastUserIdx),
        html: messageHtml(m, m.role === "assistant" && idx === lastAssIdx, idx, lastUserIdx)
      };
    }));
  }
  chat.dataset.session = a.id;
  // 空态（无消息）标记 body.home：隐藏底部状态栏，主页只留干净的问候 + 建议 + 输入框
  document.body.classList.toggle("home", !a.messages.length);
  lastChatSig = sig;
  if (!a.messages.length){
    sc.scrollTop = 0;
  } else {
    var force = moved || stick;
    if (force) sc.scrollTop = sc.scrollHeight;
    else sc.scrollTop = Math.max(0,sc.scrollHeight - sc.clientHeight - dist);
  }
  updateScrollButton();
}
var lastAttHtml = "";
function renderAttachments(){
  var wrap = document.getElementById("attachments");
  if (!wrap) return;
  var html = attachments.map(function(a,i){
    return '<span class="att-chip" title="'+attr(a.path)+'">'+icon("file")+'<span>'+esc(a.name)+'</span><button data-remove-att="'+i+'" title="移除附件" aria-label="移除附件">'+icon("x")+'</button></span>';
  }).join("");
  if (html === lastAttHtml) return;                      // 附件未变则跳过重建
  lastAttHtml = html;
  wrap.innerHTML = html;
  updateSendState();
}
function updateScrollButton(){
  var sc = document.getElementById("chatScroll");
  var btn = document.getElementById("scrollBottom");
  if (!sc || !btn) return;
  var show = (sc.scrollHeight - sc.scrollTop - sc.clientHeight) > 220;
  btn.classList.toggle("show",show);
}
function scrollToBottom(smooth){
  var sc = document.getElementById("chatScroll");
  if (!sc) return;
  if (smooth && sc.scrollTo) sc.scrollTo({top:sc.scrollHeight,behavior:"smooth"});
  else sc.scrollTop = sc.scrollHeight;
  setTimeout(updateScrollButton,220);
}
/* ---------- approval ---------- */
/* 差异视图：解析 @@ 块头拿行号，逐行渲染成「旧号 新号 内容」三段，
 * 与 git diff 的行号对齐，改了几行、改在哪一行一眼能看到。 */
function renderApproval(){
  var root = document.getElementById("approvalRoot");
  if (!root) return;
  var a = S && S.approval;
  if (!a){
    if (currentApprovalId){
      currentApprovalId = null;
      currentAskLabel = "";
      root.innerHTML = "";
      updateDocTitle();
    }
    return;
  }
  var reviewSig = a.review ? (a.review.verdict + "|" + a.review.note) : "";
  if (currentApprovalId === a.id && currentApprovalReviewSig === reviewSig) return;
  currentApprovalId = a.id;
  currentApprovalReviewSig = reviewSig;
  if (a.name === "ask_user"){ renderAskUser(a); updateDocTitle(); return; }
  var meta = toolMeta(a.name);
  var cmd = a.name==="shell" ? (a.command||prettyArgs(a)) : prettyArgs(a);
  var danger = a.risk === "danger";
  var approvalReason = a.approvalReason || a.riskReason || (danger ? "高风险操作需要单独确认" : "请检查目标与参数后决定是否继续");
  var risky = approvalReason || danger;
  root.innerHTML = '<div class="approval-backdrop"><div class="approval" role="alertdialog" aria-modal="true" aria-labelledby="approvalTitle">'
    +'<div class="approval-head">'
      +'<div class="approval-icon">'+icon("shield")+'</div>'
      +'<div style="flex:1;min-width:0"><div class="approval-title" id="approvalTitle">需要你的确认</div>'
      +'<div class="approval-sub">'+esc(meta.label)+' 请求在这台电脑上执行</div></div>'
      +'<span class="badge '+riskClass(a.risk)+'">'+esc(riskText(a.risk))+'</span>'
    +'</div>'
    +'<div class="approval-body">'
      +(risky?'<div class="risk-note '+(danger?"danger":"")+'">'+icon("alert")+'<span>'+esc(approvalReason)+'</span></div>':"")
      +(function(){
        var r = a.review;
        if (!r) return danger||a.risk==="danger" ? '<div class="approval-review waiting" id="approvalReview"><span class="review-dot"></span><span>AI 审查中…（不必等待，可随时确认）</span></div>' : "";
        var cls = r.verdict==="safe" ? "safe" : (r.verdict==="suspicious" ? "suspicious" : "unclear");
        var label = r.verdict==="safe" ? "AI 审查：未发现明显风险" : (r.verdict==="suspicious" ? "AI 审查：有可疑点，请细看" : "AI 审查不可用");
        return '<div class="approval-review '+cls+'"><span class="review-dot"></span><div><b>'+esc(label)+'</b>'+(r.note?'<span>'+esc(r.note)+'</span>':"")+'</div></div>';
      })()
      +'<div class="approval-tool">'+icon(meta.icon)+'<span>'+esc(meta.label)+'</span><code>'+esc(a.name)+'</code></div>'
      +(a.target?'<div class="approval-target"><span>操作目标</span><code>'+esc(a.target)+'</code></div>':"")
      +(a.workDir?'<div class="approval-target"><span>工作目录</span><code>'+esc(a.workDir)+'</code></div>':"")
      +((a.diff)?'<div class="tool-label"><span>将要做的修改</span></div>'
        +'<div class="approval-diff"><div class="diff-meta">'+icon("file")+'<span>'+esc(a.command||"")+'</span></div>'
        +'<pre><code>'+diffHtml(a.diff)+'</code></pre></div>':"")
      +'<div class="tool-label"><span>'+(a.name==="shell"?"将要运行的命令":"工具参数")+'</span></div>'
      +'<pre><code>'+esc(cmd)+'</code></pre>'
      +(a.argsJson && a.name==="shell" ? '<details class="approval-args"><summary>查看原始参数</summary><pre><code>'+esc(prettyArgs(a))+'</code></pre></details>' : "")
      +'<div class="approval-note">'+icon("info")+'<span>执行结果会返回给 Agent，用于决定下一步。拒绝后不会执行此工具。</span></div>'
    +'</div>'
    +'<div class="approval-foot">'
      +'<button class="btn btn-lg" data-decision="deny">拒绝</button>'
      +(a.canAlwaysAllow!==false?'<button class="btn btn-lg" data-decision="always" title="仅对本次任务后续同名工具调用生效">本任务同工具免确认</button>':'<span class="approval-scope">高风险操作必须逐次确认</span>')
      +(a.canAlwaysAllow!==false&&a.name!=="shell"?'<button class="btn btn-lg" data-decision="always_persist" title="写入权限规则并落盘：以后这类操作都不再询问（shell 不提供此档）">永久允许此工具</button>':"")
      +'<button class="btn btn-lg primary" data-decision="allow" id="approvalAllow">允许一次</button>'
      +(a.canAlwaysAllow!==false?'<div class="approval-scope">'+esc(a.alwaysAllowScope||"仅本次任务后续同名工具调用免确认。")+'</div>':"")
    +'</div>'
  +'</div></div>';
  updateDocTitle();
  // 默认焦点给「拒绝」而不是「允许一次」：用户若正在打字，回车/空格不会误放行高危操作。
  setTimeout(function(){
    var deny = document.querySelector('#approvalRoot [data-decision="deny"]');
    var b = deny || document.getElementById("approvalAllow");
    if (b) b.focus();
  },50);
}
/* ---------- ask_user 选项卡：模型提问、用户点选，答案回灌继续 ---------- */
function renderAskUser(a){
  var args = {};
  try { args = JSON.parse(a.argsJson||"{}"); } catch(e) {}
  if (typeof args !== "object" || !args) args = {};
  var q = String(args.question||args.title||args.text||args.prompt||"Agent 想请你做一个选择");
  var opts = Array.isArray(args.options) ? args.options : [];
  var rows = opts.map(function(o){
    if (o == null) return "";
    var label = "", desc = "";
    if (typeof o === "object"){ label = String(o.label||o.value||o.name||o.text||"").trim(); desc = String(o.description||o.desc||"").trim(); }
    else { label = String(o).trim(); }
    if (!label) return "";
    return '<button type="button" class="ask-opt" data-ask-opt="'+esc(label)+'" aria-pressed="false">'
      +'<span class="ask-opt-label">'+esc(label)+'</span>'
      +(desc?'<span class="ask-opt-desc">'+esc(desc)+'</span>':"")
      +'</button>';
  }).join("");
  var root = document.getElementById("approvalRoot");
  if (!root) return;
  currentAskLabel = "";
  root.innerHTML = '<div class="approval-backdrop"><div class="approval" role="alertdialog" aria-modal="true" aria-labelledby="askTitle">'
    +'<div class="approval-head">'
      +'<div class="approval-icon">'+icon("info")+'</div>'
      +'<div style="flex:1;min-width:0"><div class="approval-title" id="askTitle">Agent 想请你拍板</div>'
      +'<div class="approval-sub">点一个选项，或直接输入你的想法；答案会回给 Agent 继续执行</div></div>'
    +'</div>'
    +'<div class="approval-body">'
      +'<div class="ask-question">'+esc(q)+'</div>'
      +(rows?'<div class="ask-opts" role="listbox" aria-label="可选方案">'+rows+'</div>':"")
      +'<div class="ask-other"><input id="askOther" type="text" placeholder="或者直接输入你的回答…" autocomplete="off" spellcheck="false" aria-label="自定义回答"></div>'
    +'</div>'
    +'<div class="approval-foot">'
      +'<button class="btn btn-lg" data-decision="deny">跳过</button>'
      +'<button class="btn btn-lg primary" data-decision="allow" id="askSubmit">提交回答</button>'
    +'</div>'
  +'</div></div>';
  setTimeout(function(){
    var inp = document.getElementById("askOther");
    if (inp) inp.focus();
  },50);
}
async function resolveApproval(id,decision,answer){
  var root = document.getElementById("approvalRoot");
  var buttons = $$("button",root);
  buttons.forEach(function(b){ b.disabled = true; });
  try{
    await api("/api/approval",{id:id,decision:decision,answer:answer||""});
    // 确认已提交：立即收起弹窗，避免请求/SSE 延迟时界面一直卡在确认框。
    // 后端已放行（agent 在跑），最新状态由 refreshState/SSE 同步。
    setTimeout(function(){
      currentApprovalId = null;
      if (root) root.innerHTML = "";
      updateDocTitle();
      refreshState();
    },200);
  }catch(e){
    buttons.forEach(function(b){ b.disabled = false; });
    toast("提交确认失败："+e.message,"error");
  }
}

/* ---------- inspector / logs ---------- */
function syncInspectorClass(){
  var app = document.getElementById("app");
  if (app) app.classList.toggle("inspector-open",logsOpen);
  var panel = document.getElementById("inspector");
  if (panel) panel.setAttribute("aria-hidden",logsOpen?"false":"true");
  var button = document.getElementById("workbenchBtn");
  if (button){ button.classList.toggle("active",logsOpen); button.setAttribute("aria-pressed",logsOpen?"true":"false"); button.setAttribute("aria-label",logsOpen?"关闭工作台":"打开工作台"); }
}
function filteredLogs(){
  var logs = (S && S.logs) || [];
  if (logFilter === "all") return logs;
  return logs.filter(function(l){ return l.level === logFilter; });
}
function updateLogBadge(){
  var b = document.getElementById("logBadge");
  if (!b) return;
  if (!logsOpen && unreadLogs > 0){
    b.hidden = false;
    b.textContent = unreadLogs > 99 ? "99+" : String(unreadLogs);
  } else {
    b.hidden = true;
  }
}
/* 工作台：面板先回答「做出来了什么」，再回答「跑得怎么样」。
   产物 = 本会话工具产出的文件与截图；任务 = 执行状态与预算；日志 = 运行明细。 */
function collectSessionArtifacts(){
  var messages = (S && S.active && S.active.messages) || [];
  var out = [], seen = Object.create(null);
  for (var i = messages.length - 1; i >= 0; i--){
    var calls = messages[i].toolCalls || [];
    for (var j = calls.length - 1; j >= 0; j--){
      var list = calls[j].artifacts || [];
      for (var k = list.length - 1; k >= 0; k--){
        var a = list[k];
        if (!a || !a.id || seen[a.id]) continue;
        seen[a.id] = 1;
        out.push(a);
        if (out.length >= 24) return out;
      }
    }
  }
  return out;
}
function syncWorkbenchTabs(artifactCount, logCount){
  var box = document.getElementById("wbTabs");
  if (box) $$(".wb-tab", box).forEach(function(b){
    var on = b.getAttribute("data-wb-tab") === wbTab;
    b.classList.toggle("active", on);
    b.setAttribute("aria-selected", on ? "true" : "false");
  });
  var ac = document.getElementById("wbArtifactCount");
  if (ac) ac.textContent = artifactCount > 99 ? "99+" : String(artifactCount);
  var lc = document.getElementById("wbLogCount");
  if (lc) lc.textContent = logCount > 99 ? "99+" : String(logCount);
  var sc = document.getElementById("wbSessionCount");
  if (sc) sc.textContent = String((S && S.sessions || []).filter(function(x){ return !x.archived; }).length);
}
function workbenchArtifactsHtml(artifacts, changed){
  var html = '<div class="inspector-section-head"><span>产物</span><span class="log-time">'+artifacts.length+' 个</span></div>';
  html += artifacts.length
    ? '<div class="wb-artifacts">'+artifacts.map(artifactHtml).join("")+'</div>'
    : '<div class="wb-empty">本会话还没有产物。<br>绿虎写好页面、截好图或导出文件后，会直接出现在这里。</div>';
  html += '<div class="inspector-section-head"><span>文件改动</span><span class="log-time">'+changed.length+' 个文件</span></div>';
  html += changed.length
    ? '<div class="change-list">'+changed.map(function(f){
        return '<button type="button" class="change-row" data-review-tool="'+attr(f.toolId)+'" title="查看 '+attr(f.path)+' 的实际差异">'+icon("file")+'<span class="change-file">'+esc(f.path)+'</span><span class="change-meta">'+f.count+' 次 · 查看</span></button>';
      }).join("")+'</div>'
    : '<div class="log-empty">本会话尚无文件写入</div>';
  return html;
}
function workbenchOverviewHtml(pct){
  var stats = S.stats || {};
  return '<div class="stat-grid">'
      +'<div class="stat"><b>'+fmtNum(stats.messages||0)+'</b><span>消息</span></div>'
      +'<div class="stat"><b>'+fmtNum(stats.tools||0)+'</b><span>工具调用</span></div>'
      +'<div class="stat"><b>'+fmtNum(stats.turns||0)+'</b><span>对话轮次</span></div>'
      +'<div class="stat"><b>'+fmtNum(stats.contextChars||0)+'</b><span>上下文字符</span></div>'
    +'</div>'
    +'<div class="ctx-card"><div class="ctx-card-top"><span>上下文占用</span><b>'+pct+'%</b></div><div class="ctx-track"><i style="width:'+pct+'%"></i></div></div>';
}
function workbenchTaskHtml(){
  // 任务执行：跨任务统计 + 退化告警；当前任务终态与「继续任务」入口
  var ts = S.taskStats||{};
  var alerts = S.alerts||[];
  var cur = S.task||{};
  var outcomeText = {succeeded:"已完成",failed:"失败",cancelled:"已取消",incomplete:"未完成",waiting_approval:"等待审批",paused:"已暂停",running:"进行中"}[cur.outcome]||"";
  var ticketText = "";
  var ticketList = cur.approvals||[];
  if (ticketList.length){
    var lastTicket = ticketList[ticketList.length-1];
    var ticketState = {pending:"待确认",granted:"已批准待执行",denied:"已拒绝",expired:"已过期",consumed:"已执行"}[lastTicket.status]||lastTicket.status;
    ticketText = '<div class="ds-panel-detail">审批票据：'+esc(lastTicket.tool||"")+' · '+esc(ticketState)+'</div>';
  }
  var parkedText = "";
  var parked = cur.parked||[];
  if (parked.length){
    parkedText = '<div class="ds-panel-detail">可继续的任务（'+parked.length+'）</div>'
      +parked.map(function(p){
        var label = {paused:"已暂停",incomplete:"未完成",waiting_approval:"等待审批"}[p.outcome]||p.outcome;
        return '<div class="ds-panel-actions"><button class="btn mini" data-resume-parked data-task-id="'+esc(p.id)+'">继续</button>'
          +'<span class="log-time">'+esc(p.title||"")+' · '+esc(label)+'</span></div>';
      }).join("");
  }
  return '<div class="inspector-section-head"><span>任务执行</span><span class="log-time">最近 '+fmtNum(ts.total||0)+' 个任务</span></div>'
    +(outcomeText?'<div class="ds-panel-detail">当前任务：'+esc(outcomeText)+(cur.resumeCount?' · 已续跑 '+cur.resumeCount+' 次':'')+'</div>':"")
    +(cur.resumable?'<div class="ds-panel-actions"><button class="btn mini" data-resume-task>继续任务</button><span class="log-time">沿用原任务与预算</span></div>':"")
    +parkedText
    +ticketText
    +'<div class="stat-grid">'
      +'<div class="stat"><b>'+Math.round((ts.successRate||0)*100)+'%</b><span>成功率</span></div>'
      +'<div class="stat"><b>'+fmtNum(ts.succeeded||0)+'/'+fmtNum(ts.total||0)+'</b><span>成功/总数</span></div>'
      +'<div class="stat"><b>'+fmtDuration(ts.p50Ms||0)+' / '+fmtDuration(ts.p95Ms||0)+'</b><span>P50 / P95</span></div>'
      +'<div class="stat"><b>'+fmtNum(ts.budgetExceeded||0)+'</b><span>预算耗尽</span></div>'
    +'</div>'
    +((ts.promptTokens>0||ts.completionTokens>0)?'<div class="ds-panel-detail">Token（输入 '+fmtNum(ts.promptTokens||0)+' / 输出 '+fmtNum(ts.completionTokens||0)+'）</div>':"")
    +((ts.tokensEstimated>0)?'<div class="ds-panel-detail">Token ≈'+fmtNum(ts.tokensEstimated)+'（网页版按字符估算，非实测）</div>':"")
    +(alerts.length?'<div class="log-list" style="max-height:120px">'+alerts.map(function(a){
        return '<div class="log-row '+(a.level==="critical"?"error":"warn")+'"><span class="log-level '+(a.level==="critical"?"error":"warn")+'">告警</span><span class="log-text">'+esc(a.message)+'</span></div>';
      }).join("")+'</div>':"");
}
function workbenchModelHtml(ds){
  return '<div class="inspector-section-head"><span>模型</span><button class="mini-link" data-open-model-settings>切换</button></div>'
    +'<div class="ds-panel"><div class="ds-panel-row"><span class="dot '+ds.cls+'"></span><b>'+esc((S.brain&&S.brain.provider&&S.brain.provider.model)||ds.label)+'</b><span class="log-time">'+esc(ds.label)+'</span></div>'
      +'<div class="ds-panel-detail" title="'+attr(ds.detail)+'">'+esc(ds.detail||"还没有可用的模型")+'</div></div>';
}
function renderInspector(){
  if (!S) return;
  var body = document.getElementById("inspectorBody");
  if (!body) return;
  var oldList = document.getElementById("logList");
  var wasNear = true, keepTop = 0, keepBody = body.scrollTop;
  if (oldList){
    wasNear = (oldList.scrollHeight - oldList.scrollTop - oldList.clientHeight) < 60;
    keepTop = oldList.scrollTop;
  }
  var stats = S.stats||{};
  var limit = stats.contextLimit||60000;
  var chars = stats.contextChars||0;
  var pct = limit ? Math.min(100,Math.round(chars/limit*100)) : 0;
  var allLogs = S.logs||[];
  var artifacts = collectSessionArtifacts();
  var html = "";
  if (wbTab === "artifacts"){
    html = workbenchArtifactsHtml(artifacts, collectSessionChanges());
  } else if (wbTab === "tasks"){
    html = workbenchOverviewHtml(pct) + workbenchTaskHtml() + workbenchModelHtml(dsInfo());
  } else if (wbTab === "sessions"){
    html = workbenchSessionsHtml();
  } else {
    var logs = filteredLogs();
    var counts = {info:0,warn:0,error:0};
    allLogs.forEach(function(l){ if (counts[l.level]!==undefined) counts[l.level]++; });
    var filterHtml = [["all","全部",allLogs.length],["info","信息",counts.info],["warn","警告",counts.warn],["error","错误",counts.error]]
      .map(function(f){
        return '<button class="log-filter'+(logFilter===f[0]?" active":"")+'" data-log-filter="'+f[0]+'">'+f[1]+' '+f[2]+'</button>';
      }).join("");
    var logHtml = logs.length ? logs.map(function(l){
      var lv = l.level === "warn" ? "warn" : l.level === "error" ? "error" : "info";
      return '<div class="log-row '+lv+'"><span class="log-time">'+fmtLogTime(l.time)+'</span><span class="log-level '+lv+'">'+lv.toUpperCase()+'</span><span class="log-text">'+esc(l.text)+'</span></div>';
    }).join("") : '<div class="log-empty">暂无日志</div>';
    html = '<div class="inspector-section-head"><span>运行日志</span><span class="log-time">最近 '+logs.length+' 条 · <button class="mini-link" data-copy-logs>复制</button></span></div>'
      +'<div class="log-filters">'+filterHtml+'</div>'
      +'<div class="log-list" id="logList">'+logHtml+'</div>';
  }
  syncWorkbenchTabs(artifacts.length, allLogs.length);
  // 定时刷新（每 600ms）常常什么都没变：一样就不要再打一次 innerHTML，
  // 否则用户正在面板里选中的文字、滚动位置、按钮 focus 会被反复冲掉。
  if (html === lastInspectorHtml) return;
  lastInspectorHtml = html;
  body.innerHTML = html;
  rollStatNumbers(body);
  var newList = document.getElementById("logList");
  if (newList){
    // 贴底时跟随新日志；否则保留用户正在看的位置（含面板自身滚动位置）
    if (wasNear) newList.scrollTop = newList.scrollHeight;
    else newList.scrollTop = keepTop;
  }
  body.scrollTop = (wbTab === "logs" && wasNear) ? body.scrollHeight : keepBody;
  if (wbTab === "sessions") renderWbSessionList();
}
function workbenchSessionsHtml(){
  var all = (S && S.sessions || []);
  var q = esc(wbSessionQuery||"");
  return '<div class="inspector-section-head"><span>所有对话</span><span class="log-time">'+all.length+' 个</span></div>'
    +'<div class="wb-session-search"><span data-icon="search"></span><input id="wbSessionSearch" type="search" placeholder="搜索会话标题" value="'+q+'" autocomplete="off" spellcheck="false" aria-label="搜索会话标题"></div>'
    +'<div class="wb-session-list" id="wbSessionList"></div>';
}
function renderWbSessionList(){
  var box = document.getElementById("wbSessionList");
  if (!box) return;
  var q = (wbSessionQuery||"").trim().toLowerCase();
  var items = (S && S.sessions || []).filter(function(s){
    if (q && String(s.title||"").toLowerCase().indexOf(q) < 0) return false;
    return true;
  });
  if (!items.length){
    box.innerHTML = '<div class="wb-session-empty">'+(q?"没有匹配的会话":"还没有会话")+'</div>';
    return;
  }
  box.innerHTML = items.map(function(s){
    var when = s.updatedAt ? dayGroup(s.updatedAt) : "";
    var meta = when + ' · ' + fmtNum(s.messageCount||0) + ' 条';
    var tags = (s.pinned?'<span class="badge">置顶</span>':"") + (s.archived?'<span class="badge">已归档</span>':"");
    return '<button type="button" class="wb-session'+(s.active?" active":"")+'" data-wb-session="'+esc(s.id)+'">'
      +'<span class="wb-session-title">'+esc(s.title||"未命名会话")+tags+'</span>'
      +'<span class="wb-session-meta">'+esc(meta)+'</span>'
      +'</button>';
  }).join("");
}
function collectSessionChanges(){
  var messages = S && S.active && S.active.messages || [];
  var byPath = Object.create(null);
  var order = [];
  messages.forEach(function(m){
    (m.toolCalls||[]).forEach(function(t){
      if ((t.name!=="write_file" && t.name!=="edit") || t.status!=="done") return;
      var raw = {};
      try{ var parsed = JSON.parse(t.argsJson||"{}"); raw = parsed.args||parsed; }catch(e){}
      var path = String(raw.path||raw.file||"").trim();
      if (!path) return;
      var key = path.toLowerCase();
      var item = byPath[key];
      if (!item){ item = byPath[key] = {path:path,count:0,toolId:t.id||""}; order.push(item); }
      item.count++;
      item.toolId = t.id||item.toolId;
    });
  });
  return order.slice(-12).reverse();
}
function toggleLogs(open){
  logsOpen = typeof open === "boolean" ? open : !logsOpen;
  if (logsOpen) unreadLogs = 0;
  syncInspectorClass();
  updateLogBadge();
  if (logsOpen) renderInspector();
  syncScrim();
}

/* ---------- dialogs ---------- */
/* 焦点管理：弹层打开时记住来源元素，关闭后把焦点还回去（键盘用户不会被丢回文档开头）。 */
var lastFocusEl = null;
function rememberFocus(){ lastFocusEl = document.activeElement; }
function restoreFocus(){
  if (lastFocusEl && lastFocusEl.isConnected && typeof lastFocusEl.focus === "function"){
    try{ lastFocusEl.focus(); }catch(e){}
  }
  lastFocusEl = null;
}
/* Tab 焦点陷阱：让 Tab / Shift+Tab 在模态内循环，不会跑到背后的聊天区。 */
function trapTab(e, container){
  if (!container) return;
  var items = Array.prototype.slice.call(container.querySelectorAll(FOCUSABLE))
    .filter(function(el){ return el.offsetParent !== null || el === document.activeElement; });
  if (!items.length) return;
  var first = items[0], last = items[items.length - 1];
  if (e.shiftKey && document.activeElement === first){ e.preventDefault(); last.focus(); }
  else if (!e.shiftKey && document.activeElement === last){ e.preventDefault(); first.focus(); }
  else if (items.indexOf(document.activeElement) < 0){ e.preventDefault(); first.focus(); }
}
function activeModal(){
  return document.querySelector("#dialogRoot .dialog") || document.querySelector("#paletteRoot .palette");
}
function syncScrim(){
  var scrim = document.getElementById("scrim");
  if (!scrim) return;
  var narrowInspector = logsOpen && window.innerWidth <= 1180;
  var narrowSidebar = !sidebarCollapsed() && window.innerWidth <= 900;
  var show = narrowInspector || narrowSidebar;
  scrim.classList.toggle("show",show);
}
function closeDialog(){
  var root = document.getElementById("dialogRoot");
  if (!root) return;
  if (settingsOpen){
    settingsOpen = false;
    if (Date.now() >= themeLocalUntil) applyThemeSafe((S && S.config && S.config.theme) || "light");
  }
  confirmCallback = null;
  root.innerHTML = "";
  restoreFocus();
}
function openSettings(category){
  if (!S) return;
  if (settingsOpen){ if (category) showSettingsCat(category); return; }
  rememberFocus();
  settingsOpen = true;
  settingsTheme = (S.config && S.config.theme) || "light";
  var c = S.config || {};
  var root = document.getElementById("dialogRoot");
  root.innerHTML = '<div class="dialog-backdrop" data-dialog-backdrop><div class="dialog" role="dialog" aria-modal="true" aria-labelledby="settingsTitle">'
    +'<form id="settingsForm">'
    +'<div class="dialog-head"><div style="flex:1"><div class="dialog-eyebrow">GREEN TIGER · SETTINGS</div><div class="dialog-title" id="settingsTitle">设置</div><div class="dialog-sub">项目、模型、权限与扩展</div></div><button type="button" class="icon-btn" data-dialog-close aria-label="关闭">'+icon("x")+'</button></div>'
    +'<div class="dialog-body"><div class="settings-cats"><nav class="settings-nav" id="settingsNav"></nav><div class="settings-cats-main">'
      +'<section class="settings-section" data-cat="workspace"><h4>项目与工作区</h4>'
        +'<div class="field"><label>已保存的项目</label><div class="workspace-list" id="workspaceList"></div><div class="row" style="margin-top:10px"><input class="input" id="wsName" placeholder="项目名称" style="flex:1"><button type="button" class="btn" id="wsPickBtn">选择文件夹…</button><button type="button" class="btn" id="wsAddBtn">添加项目</button></div><div class="field-help">保存常用项目路径，切换项目时会创建或打开对应会话。</div></div>'
      +'</section>'
      +'<section class="settings-section" data-cat="models"><h4>模型与服务</h4>'
        +'<div class="settings-note"><b>当前模型</b><div class="field-help" id="modelStrategyInfo"></div></div>'
        +'<div class="field-help">目录只保留各家旗舰模型（Claude Fable 5 / DeepSeek v4-pro / Qwen3.8-max / GLM-5.3 / Kimi k3.1-pro）。填入对应服务商的 API Key 即可使用；其他任何 OpenAI 兼容端点（含中转站、本机模型）用「自定义」填。</div>'
        +'<div class="field"><label for="setProvider">模型服务商</label><select class="input" id="setProvider"><option value="">— 选择服务商 —</option><option value="custom">自定义 / 自建网关</option></select><div class="field-help" id="providerHelp">目录里只放实测可达的服务商；其他任何 OpenAI 兼容端点用「自定义」填。</div></div>'
        +'<div class="field"><label for="setProviderEndpoint">端点</label><input class="input" id="setProviderEndpoint" value="'+attr(c.providerEndpoint||"")+'" placeholder="https://api.example.com/v1" spellcheck="false" autocomplete="off"><div class="field-help">只填 Base URL（到 /v1 为止），不要带 /chat/completions。换服务商会自动填入。</div></div>'
        +'<div class="field"><label for="setProviderKey">API Key</label><input class="input" id="setProviderKey" type="password" value="'+attr(c.providerKey||"")+'" placeholder="粘贴服务商 API Key"></div>'
        +'<div class="field"><label for="setProviderModel">模型</label><input class="input" id="setProviderModel" list="providerModelList" value="'+attr(c.providerModel||"")+'" placeholder="模型名（先点「测试连接」拉取候选）" spellcheck="false" autocomplete="off"><datalist id="providerModelList"></datalist><div class="field-help" id="providerModelHelp">可以直接输入任意模型名；点「测试连接」会拉取该端点真实可用的列表。</div></div>'
        +'<div class="settings-actions"><button type="button" class="btn" id="testProviderBtn">测试连接</button><button type="button" class="btn mini" id="providerKeyBtn" hidden>获取 API Key</button><button type="button" class="btn" id="clearProviderBtn">清除模型配置</button></div><div class="field-help" id="providerResult"></div>'
      +'</section>'
      +'<section class="settings-section" data-cat="permissions"><h4>权限与运行限制</h4>'
        +'<div class="settings-note"><b>权限模式在输入框旁切换</b><div class="field-help">规划只读分析；逐项确认会在修改或执行前询问；自动接受编辑只跳过文件编辑确认；自动执行仍会拦截高危操作。普通回复中的代码示例不会执行。</div></div>'
        +toggleRowHtml("setSandbox","沙箱模式（低完整性）","开启后工作目录自动切到沙箱目录：命令以 Windows 低完整性运行，只能写沙箱目录，普通命令与文件修改免逐条确认；高危命令、越界路径与键鼠操作仍需确认。不防网络外发。",!!c.sandboxMode)
        +toggleRowHtml("setSupervisor","AI 审查高风险操作","高风险确认框附一份 AI 风险解读：这条命令做什么、有什么可疑点。只帮你判断，不会自动放行；每次审查消耗少量模型额度。",c.supervisorReview!==false)
        +toggleRowHtml("setWatch","离席自动暂停（watch mode）","开启后：任务运行中超过设定分钟数没有任何用户操作（发消息/审批/点界面），自动暂停并在回来后手动继续。适合不想让 agent 在你离开时继续跑高危操作的场合。",!!c.watchMode)
        +'<div class="field" style="margin-top:6px"><label>离席阈值（分钟）</label><input class="input" id="setWatchIdle" type="number" min="1" max="120" value="'+(c.watchIdleMinutes||5)+'"><div class="field-help">仅在打开 watch mode 时生效。</div></div>'
        +'<div class="grid2" style="margin-top:14px"><div class="field"><label for="setMaxSteps">单任务最大工具调用数</label><input class="input" id="setMaxSteps" type="number" min="1" max="120" value="'+(c.maxSteps||40)+'"></div><div class="field"><label for="setCommandTimeout">单次命令超时（秒）</label><input class="input" id="setCommandTimeout" type="number" min="5" max="3600" value="'+(c.commandTimeout||120)+'"></div><div class="field"><label for="setReplyTimeout">模型回复超时（秒）</label><input class="input" id="setReplyTimeout" type="number" min="30" max="1800" value="'+(c.replyTimeout||300)+'"></div></div>'
        +'<div class="grid2" style="margin-top:14px"><div class="field"><label for="setTaskTimeout">单任务总耗时预算（秒）</label><input class="input" id="setTaskTimeout" type="number" min="30" max="86400" value="'+(c.taskTimeoutSeconds||1800)+'"></div><div class="field"><label for="setMaxTokens">单任务 Token 上限（0 = 不限）</label><input class="input" id="setMaxTokens" type="number" min="0" max="20000000" value="'+(c.maxTaskTokens||0)+'"></div><div class="field"><label for="setMaxRetries">工具自动重试次数</label><input class="input" id="setMaxRetries" type="number" min="0" max="50" value="'+(c.maxToolRetries==null?5:c.maxToolRetries)+'"></div><div class="field"><label for="setMaxRepairs">验收失败后的修复轮次</label><input class="input" id="setMaxRepairs" type="number" min="0" max="10" value="'+(c.maxRepairs==null?2:c.maxRepairs)+'"></div></div>'
        +'<div class="field-help">预算用于兜住失控的任务：超预算会停下并说明原因，不会无限跑。</div>'
        +'<div class="field" style="margin-top:14px"><label for="setNetPolicy">网络白名单</label>'
          +'<select class="input" id="setNetPolicy"><option value="off">关闭 · 不限制出网目标</option><option value="allowlist">白名单 · 命令与抓取的目标必须在名单内</option></select>'
          +'<textarea class="input" id="setNetAllowlist" rows="4" placeholder="example.com&#10;api.example.com" style="margin-top:8px" spellcheck="false"></textarea>'
          +'<div class="field-help">每行一个域名；条目同时放行其子域（github.com 放行 api.github.com）。白名单开着且名单为空 = 一切出网都要确认（fail-closed）。开启后命令、网页抓取、下载的目标不在名单内会被强制确认。</div>'
        +'</div>'
        +'<div class="field" style="margin-top:14px"><label>工具权限规则（白 / 黑名单）</label>'
          +'<div class="mcp-list" id="permitList"></div>'
          +'<div class="grid2"><div class="field"><input class="input" id="permitMatch" placeholder="shell:Get-ChildItem* 或 read_file:**" spellcheck="false"></div>'
          +'<div class="field"><select class="input" id="permitAction"><option value="allow">允许 · 跳过逐条确认</option><option value="deny">拒绝 · 直接拦截</option></select></div></div>'
          +'<div class="settings-actions"><button type="button" class="btn" id="permitAddBtn">添加规则</button></div>'
          +'<div class="field-help">格式「工具名:匹配串」，* 通配任意字符、? 匹配单字符，不区分大小写；deny 优先。规则随「保存设置」一起生效。</div>'
        +'</div>'
      +'</section>'
      +'<section class="settings-section" data-cat="memory"><h4>记忆</h4>'
        +'<div class="settings-note"><b>自动沉淀先进候选区</b><div class="field-help">任务成功/失败教训的自动沉淀出现在下面的候选列表，采纳后才进项目记忆（防止错误结论污染后续任务）。save_memory 显式记忆与全局记忆直接生效。</div></div>'
        +'<div id="memoryReview"></div>'
      +'</section>'
      +'<section class="settings-section" data-cat="automation"><h4>定时任务</h4><div class="task-list" id="taskList"></div>'
        +'<div class="field" style="margin-top:10px"><label for="taskName">新任务</label><input class="input" id="taskName" placeholder="任务名称"></div>'
        +'<div class="grid2"><div class="field"><label for="taskSchedule">时间</label><input class="input" id="taskSchedule" placeholder="09:00 或 every:30"></div><div class="field"><label for="taskPrompt">指令</label><input class="input" id="taskPrompt" placeholder="触发后交给 Agent 执行"></div></div>'
        +'<div class="settings-actions"><button type="button" class="btn" id="taskAddBtn">添加定时任务</button></div><div class="field-help">HH:mm 表示每天此时触发；every:N 表示每 N 分钟触发。</div>'
      +'</section>'
      +'<section class="settings-section" data-cat="extensions"><h4>工具与扩展</h4>'
        +'<div class="field" style="margin-top:18px"><label>外部工具（MCP 服务器）</label>'
          +'<div class="mcp-list" id="mcpList"><div class="field-help">加载中…</div></div>'
          +'<div class="settings-actions"><button type="button" class="btn" id="mcpDiscoverBtn">扫描本机已配置</button><button type="button" class="btn" id="mcpAddBtn">手动添加</button></div>'
          +'<div id="mcpDiscoverBox"></div>'
          +'<div class="field-help">REA 等第三方工具走标准 MCP 协议，格式与 WorkBuddy / Qoder / Claude / VS Code 一致。技能包放 <code>.greentiger\\skills\\*.md</code>，任务里会自动读取。</div>'
        +'</div>'
      +'</section>'
      +'<section class="settings-section" data-cat="appearance"><h4>应用行为</h4>'
        +toggleRowHtml("setUiMotion","界面动效","柔和的按钮反馈与页面过渡；系统减少动态效果时会自动降低。",c.uiMotion !== false)
        +toggleRowHtml("setUiSounds","界面提示音","按钮轻触与完成、异常提示播放短音；默认关闭。",!!c.uiSounds)
        +toggleRowHtml("setShowInspector","工作台常驻右侧面板","在右侧查看产物、任务进度、文件变更与运行记录；保存后立即生效。",!!c.showInspector)
        +toggleRowHtml("setHideShot","截图时隐藏绿虎窗口","截屏工具运行时减少自身窗口遮挡。",!!c.hideOnScreenshot)
      +'</section>'
      +'</div></div><div class="settings-error" id="setError" hidden></div><div class="about-line">GreenTiger '+(S.version?("v"+esc(S.version)):"")+' · GPL-3.0</div></div>'
    +'<div class="dialog-foot"><button type="button" class="btn" data-dialog-close>取消</button><button type="submit" class="btn primary" id="settingsSave">保存设置</button></div></form></div></div>';
  renderHarnessSettings();
  renderMcpServers();
  permitRulesDraft = (c.permitRules||[]).map(function(r){ return {id:r.id||"",name:r.name||"",match:r.match||"",action:r.action||"allow"}; });
  renderPermitRules();
  (function(){
    var np = document.getElementById("setNetPolicy");
    var na = document.getElementById("setNetAllowlist");
    if (np) np.value = c.networkPolicy || "off";
    if (na) na.value = (c.networkAllowlist||[]).join("\n");
  })();
  renderMemoryReview();
  setupSettingsCats(category);
  setTimeout(function(){
    var section = document.querySelector("#settingsForm .settings-section:not(.hide)");
    var el = section && section.querySelector("input:not([type=checkbox]),select,button");
    if (el) el.focus();
  },40);
}
// 设置大类：把平铺的 section 归到语义分组，左侧导航切换（隐藏的 section 仍在 DOM 内，保存时一并提交）
function setupSettingsCats(initialCat){
  var nav = document.getElementById("settingsNav");
  if (!nav) return;
  var html = "";
  for (var i=0;i<SETTINGS_CATS.length;i++){
    var c = SETTINGS_CATS[i];
    html += '<button type="button" class="settings-nav-btn'+(i===0?" active":"")+'" data-cat="'+attr(c.key)+'">'+esc(c.label)+'</button>';
  }
  nav.innerHTML = html;
  var btns = nav.querySelectorAll("[data-cat]");
  for (var j=0;j<btns.length;j++){
    btns[j].addEventListener("click", function(){ showSettingsCat(this.getAttribute("data-cat")); });
  }
  var selected = SETTINGS_CATS[0].key;
  for (var k=0;k<SETTINGS_CATS.length;k++) if (SETTINGS_CATS[k].key === initialCat) selected = initialCat;
  showSettingsCat(selected);
}
function showSettingsCat(cat){
  var nav = document.getElementById("settingsNav");
  if (nav){
    var bs = nav.querySelectorAll("[data-cat]");
    for (var i=0;i<bs.length;i++) bs[i].classList.toggle("active", bs[i].getAttribute("data-cat") === cat);
  }
  var secs = document.querySelectorAll("#settingsForm .settings-section[data-cat]");
  for (var j=0;j<secs.length;j++){
    secs[j].classList.toggle("hide", secs[j].getAttribute("data-cat") !== cat);
  }
}
function showSettingsError(msg){
  var el = document.getElementById("setError");
  if (!el) return;
  el.hidden = false;
  el.textContent = msg;
}
/* 快捷键速查表：界面上的每一条 hint 都应该能在这里查到，两边共用同一份数据。 */
/* Windows 上的 key 标签：Ctrl/N 这些写死在系统有关系，
   但全部集中在这一个函数里，将来要适配 macOS ⌘ 只需要改这里。 */
function openShortcuts(){
  var root = document.getElementById("dialogRoot");
  if (!root) return;
  if (document.getElementById("paletteRoot") && document.getElementById("paletteRoot").firstElementChild) closePalette();
  root.innerHTML = '<div class="dialog-backdrop" data-dialog-backdrop><div class="dialog" role="dialog" aria-modal="true" aria-labelledby="shortcutsTitle">'
    +'<div class="dialog-head"><div style="flex:1"><div class="dialog-eyebrow">GREEN TIGER · KEYS</div><div class="dialog-title" id="shortcutsTitle">快捷键</div>'
    +'<div class="dialog-sub">任何时候按 Ctrl / 都能打开这张表</div></div>'
    +'<button type="button" class="icon-btn" data-dialog-close aria-label="关闭">'+icon("x")+'</button></div>'
    +'<div class="dialog-body">'+shortcutHtml(SHORTCUTS)+'</div>'
    +'<div class="dialog-foot"><button type="button" class="btn primary" data-dialog-close id="shortcutsOk">知道了</button></div>'
  +'</div></div>';
  setTimeout(function(){ var b = document.getElementById("shortcutsOk"); if (b) b.focus(); },40);
}

function askConfirm(title,message,options,callback){
  var root = document.getElementById("dialogRoot");
  if (!root) return;
  if (settingsOpen){
    settingsOpen = false;
    if (Date.now() >= themeLocalUntil) applyThemeSafe((S && S.config && S.config.theme) || "light");
  }
  confirmCallback = callback;
  var label = (options && options.confirmLabel) || "确定";
  var danger = !!(options && options.danger);
  var sub = (options && options.sub) || "";
  root.innerHTML = '<div class="dialog-backdrop" data-dialog-backdrop><div class="dialog confirm" role="dialog" aria-modal="true" aria-labelledby="confirmTitle">'
    +'<div class="dialog-head"><div style="flex:1"><div class="dialog-eyebrow">GREEN TIGER · CONFIRM</div><div class="dialog-title" id="confirmTitle">'+esc(title)+'</div>'+(sub?'<div class="dialog-sub">'+esc(sub)+'</div>':"")+'</div><button type="button" class="icon-btn" data-dialog-close aria-label="关闭">'+icon("x")+'</button></div>'
    +'<div class="dialog-body"><div class="confirm-text">'+esc(message)+'</div></div>'
    +'<div class="dialog-foot"><button type="button" class="btn" data-dialog-close>取消</button><button type="button" class="btn '+(danger?"danger-solid":"primary")+'" id="confirmYes">'+esc(label)+'</button></div>'
  +'</div></div>';
  setTimeout(function(){ var b = document.getElementById("confirmYes"); if (b) b.focus(); },40);
}
function askPrompt(title,sub,value,onOk){
  var root = document.getElementById("dialogRoot");
  if (!root) return;
  if (settingsOpen){
    settingsOpen = false;
    if (Date.now() >= themeLocalUntil) applyThemeSafe((S && S.config && S.config.theme) || "light");
  }
  root.innerHTML = '<div class="dialog-backdrop" data-dialog-backdrop><div class="dialog confirm" role="dialog" aria-modal="true" aria-labelledby="promptTitle">'
    +'<div class="dialog-head"><div style="flex:1"><div class="dialog-eyebrow">GREEN TIGER · INPUT</div><div class="dialog-title" id="promptTitle">'+esc(title)+'</div>'+(sub?'<div class="dialog-sub">'+esc(sub)+'</div>':"")+'</div><button type="button" class="icon-btn" data-dialog-close aria-label="关闭">'+icon("x")+'</button></div>'
    +'<div class="dialog-body"><input class="input" id="promptInput" value="'+attr(value||"")+'" maxlength="60" spellcheck="false" autocomplete="off" aria-label="'+esc(title)+'"></div>'
    +'<div class="dialog-foot"><button type="button" class="btn" data-dialog-close>取消</button><button type="button" class="btn primary" id="promptOk">保存</button></div>'
  +'</div></div>';
  var input = document.getElementById("promptInput");
  function submit(){
    var next = input ? input.value.trim() : "";
    if (!next){ if (input) input.focus(); return; }
    closeDialog();
    if (onOk) setTimeout(function(){ onOk(next); },20);
  }
  var ok = document.getElementById("promptOk");
  if (ok) ok.addEventListener("click",submit);
  if (input){
    input.addEventListener("keydown",function(e){
      if (e.key === "Enter"){ e.preventDefault(); submit(); }
    });
    setTimeout(function(){ input.focus(); input.select(); },40);
  }
}
/* 记忆候选审核：设置 → 记忆。采纳 = 进项目记忆；丢弃 = 删除候选。 */
function renderMemoryReview(){
  var box=document.getElementById("memoryReview");
  if(!box) return;
  api("/api/memory/candidates").then(function(data){
    var items=(data&&data.candidates)||[];
    var out="";
    if(!items.length){
      out+='<div class="field-help">没有待审核的记忆候选。任务成功后的自动沉淀会先出现在这里，采纳后才进入记忆。</div>';
    } else {
      for(var i=0;i<items.length;i++){
        var c=items[i];
        out+='<div style="border:1px solid var(--line);border-radius:8px;padding:8px 10px;margin:6px 0">'
          +'<div style="white-space:pre-wrap;font-size:12px;line-height:1.5">'+esc(c.text)+'</div>'
          +'<div class="row" style="margin-top:6px"><button class="btn" data-mem-accept="'+c.id+'">采纳</button><button class="btn" data-mem-dismiss="'+c.id+'">丢弃</button></div></div>';
      }
    }
    out+='<div class="field-help">全局记忆 '+((data&&data.globalLines)||0)+' 条：save_memory(scope=global) 或数据目录 memory.md 手工编辑；自动沉淀只进当前项目的候选区。</div>';
    box.innerHTML=out;
  }).catch(function(e){ box.innerHTML='<div class="field-help">加载失败：'+esc(e.message)+'</div>'; });
}
async function saveSettings(){
  var form = document.getElementById("settingsForm");
  if (!form || !S) return;
  var error = document.getElementById("setError");
  if (error) error.hidden = true;
  var motionBox = form.querySelector("#setUiMotion");
  var soundsBox = form.querySelector("#setUiSounds");
  var body = {
    maxSteps:clampInt(form.querySelector("#setMaxSteps").value,1,120,S.config.maxSteps||40),
    commandTimeout:clampInt(form.querySelector("#setCommandTimeout").value,5,3600,S.config.commandTimeout||120),
    taskTimeoutSeconds:clampInt(form.querySelector("#setTaskTimeout").value,30,86400,S.config.taskTimeoutSeconds||1800),
    maxTaskTokens:clampInt(form.querySelector("#setMaxTokens").value,0,20000000,S.config.maxTaskTokens||0),
    maxToolRetries:clampInt(form.querySelector("#setMaxRetries").value,0,50,S.config.maxToolRetries==null?5:S.config.maxToolRetries),
    maxRepairs:clampInt(form.querySelector("#setMaxRepairs").value,0,10,S.config.maxRepairs==null?2:S.config.maxRepairs),
    replyTimeout:clampInt(form.querySelector("#setReplyTimeout").value,30,1800,S.config.replyTimeout||300),
    hideOnScreenshot:form.querySelector("#setHideShot").checked,
    sandboxMode:(function(){var sb=form.querySelector("#setSandbox");return sb?sb.checked:!!S.config.sandboxMode;})(),
    supervisorReview:(function(){var sp=form.querySelector("#setSupervisor");return sp?sp.checked:(S.config.supervisorReview!==false);})(),
    watchMode:(function(){var sw=form.querySelector("#setWatch");return sw?sw.checked:!!S.config.watchMode;})(),
    watchIdleMinutes:clampInt((function(){var wi=form.querySelector("#setWatchIdle");return wi?wi.value:5;})(),1,120,S.config.watchIdleMinutes||5),
    showInspector:form.querySelector("#setShowInspector").checked,
    uiMotion:motionBox ? motionBox.checked : (S.config.uiMotion !== false),
    uiSounds:soundsBox ? soundsBox.checked : !!S.config.uiSounds,
    theme:settingsTheme,
    permitRules:permitRulesDraft,
    networkPolicy:(function(){var np=form.querySelector("#setNetPolicy");return np?np.value:"off";})(),
    networkAllowlist:(function(){
      var na=form.querySelector("#setNetAllowlist");
      if (!na) return S.config.networkAllowlist||[];
      return na.value.split(/\r?\n/).map(function(s){return s.trim().toLowerCase();}).filter(function(s){return s.length>0;});
    })()
  };
  var pSel = form.querySelector("#setProvider");
  var pKey = form.querySelector("#setProviderKey");
  var pModel = form.querySelector("#setProviderModel");
  var pEndpoint = form.querySelector("#setProviderEndpoint");
  var endpointValue = pEndpoint ? pEndpoint.value.trim() : "";
  if (endpointValue && !/^https?:\/\//i.test(endpointValue)){
    showSettingsError("端点必须以 http:// 或 https:// 开头");
    if (btn) { btn.disabled = false; btn.textContent = "保存设置"; }
    return;
  }
  if (pSel && (pSel.value || endpointValue)){
    var provider = null;
    for (var pi=0;pi<providersCatalog.length;pi++) if (providersCatalog[pi].id === pSel.value) provider = providersCatalog[pi];
    body.providerId = provider ? provider.id : (pSel.value === "custom" ? "" : "");
    body.providerEndpoint = endpointValue || (provider ? provider.endpoint : "");
    body.providerKey = pKey ? pKey.value.trim() : "";
    body.providerModel = pModel ? pModel.value.trim() : "";
  }
  var btn = document.getElementById("settingsSave");
  if (btn){ btn.disabled = true; btn.textContent = "保存中…"; }
  try{
    var next = await api("/api/config",body);
    // 刚保存的主题就是用户此刻的选择：清掉「点了主题按钮后 3 秒本地优先」的窗口，
    // 否则在这一小段时间里保存设置，主题会看起来没反应。
    themeLocalUntil = 0;
    applyState(next);
    // 工作台常驻开关：保存即生效（打开/收起当前窗口的面板），不必等下次启动
    if (next && next.config && typeof next.config.showInspector === "boolean"){
      logsOpen = next.config.showInspector;
      syncInspectorClass();
      if (logsOpen) renderInspector();
    }
    closeDialog();
    toast("设置已保存","ok");
  }catch(e){
    showSettingsError(e.message);
  }finally{
    if (btn){ btn.disabled = false; btn.textContent = "保存设置"; }
  }
}
/* ---------- palette ---------- */
function buildActions(){
  var running = !!(S && S.status && S.status.running);
  var actions = [
    {id:"new-session",label:"新建会话",hint:"Ctrl N",icon:"plus",keywords:"session 会话 新建",disabled:running,run:newSession},
    {id:"rename-session",label:"重命名当前会话",icon:"edit",keywords:"标题 改名 rename",run:function(){ if (S && S.active) renameSession(S.active.id); }},
    {id:"export",label:"导出当前会话为 Markdown",icon:"download",keywords:"导出 保存 备份 markdown",disabled:!(S && S.active && (S.active.messages||[]).length),run:exportSession},
    {id:"new-chat",label:"新建会话",icon:"spark",keywords:"对话 清空 新聊天 新建",disabled:running,run:newChat},
    {id:"pick-folder",label:"选择工作目录…",icon:"folder",keywords:"目录 文件夹 工作区",disabled:running,run:chooseWorkdir},
    {id:"attach",label:"附加文件…",icon:"paperclip",keywords:"上传 附件 文件",run:attachFiles},
    {id:"logs",label:logsOpen?"关闭运行日志":"打开运行日志",icon:"logs",keywords:"日志 记录 统计",run:function(){toggleLogs();}},
    {id:"settings",label:"打开设置",hint:"Ctrl ,",icon:"sliders",keywords:"配置 偏好 工作目录",run:openSettings},
    {id:"memory",label:"记忆管理",icon:"brain",keywords:"记忆 项目记忆 全局记忆 编辑 删除",run:function(){ openSettings("memory"); }},
    {id:"models",label:"模型设置",hint:"",icon:"brain",keywords:"模型 服务商 API key",run:function(){ openSettings("models"); }},
    {id:"appearance",label:"应用行为",icon:"theme",keywords:"行为 活动面板 截图 关闭",run:function(){ openSettings("appearance"); }},
    {id:"shortcuts",label:"快捷键速查",hint:"Ctrl /",icon:"keyboard",keywords:"快捷键 按键 键盘 帮助 help 速查 shortcut",run:openShortcuts},
    {id:"toggle-motion",label:(S && S.config && S.config.uiMotion === false)?"开启界面动效":"关闭界面动效",icon:"wind",keywords:"动效 动画 过渡 motion 主页",run:function(){ toggleHomePref("uiMotion"); }},
    {id:"toggle-sound",label:(S && S.config && S.config.uiSounds)?"关闭界面提示音":"开启界面提示音",icon:"sound",keywords:"提示音 声音 音效 sound 主页",run:function(){ toggleHomePref("uiSounds"); }},
    {id:"workdir-open",label:"在资源管理器打开工作目录",hint:"Ctrl Shift O",icon:"folder",keywords:"工作目录 文件夹 资源管理器 explorer 打开",run:openWorkdir},
    {id:"model",label:"接入 / 切换模型",icon:"spark",keywords:"模型 服务商 key 切换 接入 ollama",run:function(){ openSettings("models"); }},
    {id:"restart",label:"重开控制台窗口",icon:"refresh",keywords:"白屏 卡死 重开 窗口",run:restartBrowser},
    {id:"update",label:"查看项目更新",icon:"upload",keywords:"github 更新 版本",run:checkUpdate},
    {id:"shutdown",label:"退出绿虎",icon:"power",keywords:"退出 关闭 停止",danger:true,run:quitApp}
  ];
  if (running){
    actions.splice(1,0,{id:"pause",label:"暂停当前任务",hint:"可继续",icon:"pause",keywords:"暂停 挂起 稍后 继续 pause",run:pauseTask});
    actions.splice(1,0,{id:"stop",label:"停止当前任务",icon:"stop",keywords:"停止 中断 取消",run:stopTask});
  }
  return actions;
}
function openPalette(){
  if (S && S.approval) return;
  var root = document.getElementById("paletteRoot");
  if (!root) return;
  paletteActions = buildActions();
  paletteIndex = 0;
  rememberFocus();
  root.innerHTML = '<div class="palette-backdrop" data-palette-backdrop><div class="palette" role="dialog" aria-modal="true" aria-label="命令面板">'
    +'<div class="palette-input">'+icon("command")+'<input id="paletteInput" placeholder="搜索命令或操作…" autocomplete="off" spellcheck="false" aria-label="搜索命令或操作" role="combobox" aria-expanded="true" aria-controls="paletteList"><kbd>ESC</kbd></div>'
    +'<div class="palette-list" id="paletteList" role="listbox" aria-label="命令结果"></div>'
    +'<div class="palette-foot"><span><b>↑↓</b> 选择</span><span><b>Enter</b> 执行</span><span><b>Esc</b> 关闭</span></div>'
  +'</div></div>';
  renderPaletteList();
  var input = document.getElementById("paletteInput");
  if (input) input.focus();
}
function closePalette(){
  var root = document.getElementById("paletteRoot");
  if (root) root.innerHTML = "";
  restoreFocus();
}
function renderPaletteList(){
  var list = document.getElementById("paletteList");
  var input = document.getElementById("paletteInput");
  if (!list || !input) return;
  var q = input.value.trim();
  // 标签按模糊打分，关键字只参与「要不要出现」的判断（不参与标签上的字符高亮）。
  var ranked = fuzzyRank(paletteActions, q, function(a){ return a.label; });
  if (q){
    var lowered = q.toLowerCase();
    ranked = ranked.filter(function(r){
      return (r.item.keywords || "").toLowerCase().indexOf(lowered) >= 0 || r.score > 10;
    });
  }
  paletteFiltered = ranked.map(function(r){ return r.item; });
  if (paletteIndex >= paletteFiltered.length) paletteIndex = Math.max(0,paletteFiltered.length-1);
  if (!paletteFiltered.length){
    list.innerHTML = '<div class="palette-empty">没有匹配的命令</div>';
    syncPaletteActiveDescendant(input, null);
    return;
  }
  list.innerHTML = ranked.map(function(r,i){
    return '<div class="palette-item'+(i===paletteIndex?" active":"")+(r.item.disabled?" disabled":"")+'" id="palette-opt-'+i+'" role="option" aria-selected="'+(i===paletteIndex?"true":"false")+'" aria-disabled="'+(r.item.disabled?"true":"false")+'" data-action-index="'+i+'">'
      +icon(r.item.icon)+'<span>'+fuzzyHighlight(r.item.label, r.hits)+'</span>'+(r.item.hint?'<span class="hint">'+esc(r.item.hint)+'</span>':"")+'</div>';
  }).join("");
  syncPaletteActiveDescendant(input, paletteIndex);
  var active = list.querySelector(".palette-item.active");
  if (active && active.scrollIntoView) active.scrollIntoView({block:"nearest"});
}
/* 焦点一直留在输入框里，光靠 aria-selected 读屏软件不会播报当前高亮项。
   补上 aria-activedescendant 指向高亮的那一项，键盘导航才真正“可感知”。 */
function syncPaletteActiveDescendant(input, index){
  if (!input) return;
  if (index == null || index < 0) input.removeAttribute("aria-activedescendant");
  else input.setAttribute("aria-activedescendant","palette-opt-" + index);
}
function runPaletteAction(){
  var action = paletteFiltered[paletteIndex];
  if (!action) return;
  if (action.disabled){ toast("任务运行中，暂不可用","warn"); return; }
  closePalette();
  setTimeout(function(){ action.run(); },20);
}

/* ---------- actions ---------- */
/* 侧栏：常驻左栏，折叠状态只记在 body.sidebar-collapsed 上（唯一状态源）。
   窄屏下同一份状态驱动覆盖式抽屉，不额外引入第二套开关。 */
function sidebarCollapsed(){ return document.body.classList.contains("sidebar-collapsed"); }
function setSidebarCollapsed(on){
  document.body.classList.toggle("sidebar-collapsed",on);
  var b = document.getElementById("menuBtn");
  if (b){
    b.setAttribute("aria-expanded",on?"false":"true");
    b.title = on ? "显示侧栏 · Ctrl B" : "收起侧栏 · Ctrl B";
    b.setAttribute("aria-label",on ? "显示侧栏" : "收起侧栏");
  }
  syncScrim();
}
function collapseSidebar(){ setSidebarCollapsed(true); }
function toggleSidebar(){ setSidebarCollapsed(!sidebarCollapsed()); }
/* 切会话/分叉后只在窄屏收起抽屉，宽屏常驻栏不打断视野。 */
function collapseSidebarNarrow(){ if (window.innerWidth <= 900) collapseSidebar(); }
var bootSessionFailed = false;
async function newSession(){
  if (S && S.status && S.status.running){ toast("任务运行中，暂不能新建会话","warn"); return; }
  try{
    var next = await api("/api/session/new",{});
    bootSessionFailed = false;
    if (next.version) applyState(next);
    collapseSidebarNarrow();
    var input = document.getElementById("input");
    if (input) input.focus();
  }catch(e){
    bootSessionFailed = true;
    renderChat();                                          // 让空态给出「重试」入口，而不是永远转圈
    toast("新建会话失败："+e.message,"error");
  }
}
async function selectSession(id){
  if (!S) return;
  if (S.active && S.active.id === id){ collapseSidebarNarrow(); return; }
  if (S.status && S.status.running){ toast("任务运行中，暂不能切换会话","warn"); return; }
  try{
    var next = await api("/api/session/select",{id:id});
    if (next.version) applyState(next);
    collapseSidebarNarrow();
  }catch(e){ toast("切换会话失败："+e.message,"error"); }
}
function deleteSession(id){
  if (!S) return;
  if (S.status && S.status.running){ toast("任务运行中，暂不能删除会话","warn"); return; }
  var sess = (S.sessions||[]).filter(function(s){return s.id===id;})[0];
  if (!sess) return;
  askConfirm("删除会话","确定删除「"+sess.title+"」吗？本地消息记录会一并删除，不可恢复。",{confirmLabel:"删除",danger:true},async function(){
    try{
      var next = await api("/api/session/delete",{id:id});
      if (next.version) applyState(next);
      toast("会话已删除","ok");
    }catch(e){ toast("删除失败："+e.message,"error"); }
  });
}
function renameSession(id){
  if (!S) return;
  var sess = (S.sessions||[]).filter(function(s){return s.id===id;})[0];
  if (!sess) return;
  askPrompt("重命名会话","标题只保存在本地，方便你之后找回这段对话。",sess.title,async function(title){
    try{
      var next = await api("/api/session/rename",{id:id,title:title});
      if (next.version) applyState(next);
      toast("会话已重命名","ok",2000);
    }catch(e){ toast("重命名失败："+e.message,"error"); }
  });
}
function sessionById(id){
  var all = (S && S.sessions) || [];
  for (var i=0;i<all.length;i++) if (all[i].id === id) return all[i];
  return null;
}
async function setSessionFlag(id,kind,value){
  if (!S) return;
  if (S.status && S.status.running){ toast("任务运行中，暂不能调整会话","warn"); return; }
  try{
    var next = await api("/api/session/"+kind,{id:id,value:value});
    if (next.version) applyState(next);
    toast(kind === "pin" ? (value?"会话已置顶":"已取消置顶") : (value?"会话已归档":"已取消归档"),"ok",2000);
  }catch(e){ toast("操作失败："+e.message,"error"); }
}
/* ---------- 会话批量选择 / 归档 / 删除 ---------- */
function pickedIds(){ return Object.keys(selectedSessions); }
function enterSelectMode(){
  selectMode = true;
  selectedSessions = Object.create(null);
}
function exitSelectMode(){
  selectMode = false;
  selectedSessions = Object.create(null);
}
function togglePick(id){
  if (!id) return;
  if (selectedSessions[id]) delete selectedSessions[id];
  else selectedSessions[id] = true;
  lastSidebarHtml = "";
  renderSidebar();
}
/* 批量栏与 tab 的即时状态：renderSidebar 每次都会调，html 没变也不会漏更。 */
function syncBulkBar(){
  var bar = document.getElementById("bulkBar");
  if (bar) bar.hidden = !selectMode;
  // 选择模式下收起搜索框：一排控件并列会把 276px 宽的侧栏挤满，而批量操作时基本不查搜索
  var searchWrap = document.getElementById("sessionSearchWrap");
  if (searchWrap) searchWrap.hidden = selectMode;
  var n = pickedIds().length;
  var cnt = document.getElementById("bulkCount");
  if (cnt) cnt.textContent = "已选 " + n + " 项";
  var allSel = lastVisibleIds.length > 0 && lastVisibleIds.every(function(id){ return !!selectedSessions[id]; });
  var allBtn = document.getElementById("bulkAllBtn");
  if (allBtn){
    allBtn.textContent = allSel ? "取消全选" : "全选";
    allBtn.setAttribute("aria-pressed", allSel ? "true" : "false");
  }
  var archBtn = document.getElementById("bulkArchiveBtn");
  if (archBtn){ archBtn.textContent = showArchived ? "取消归档" : "归档"; archBtn.disabled = n === 0; }
  var delBtn = document.getElementById("bulkDeleteBtn");
  if (delBtn) delBtn.disabled = n === 0;
  var sel = document.getElementById("selectToggle");
  if (sel){
    sel.setAttribute("aria-pressed", selectMode ? "true" : "false");
    sel.title = selectMode ? "退出批量选择" : "批量选择会话";
    sel.setAttribute("aria-label", sel.title);
  }
}
async function bulkSessionAction(action){
  var ids = pickedIds();
  if (!ids.length){ toast("先选中会话","warn",1800); return; }
  if (S && S.status && S.status.running){ toast("任务运行中，暂不能调整会话","warn"); return; }
  var busy = [document.getElementById("bulkArchiveBtn"), document.getElementById("bulkDeleteBtn")];
  busy.forEach(function(b){ if (b) b.disabled = true; });
  try{
    var next = await api("/api/sessions/bulk",{action:action, ids:ids});
    if (next.version) applyState(next);
    var n = ids.length;
    exitSelectMode();
    lastSidebarHtml = "";
    renderSidebar();
    toast(action === "delete" ? ("已删除 " + n + " 个会话")
        : action === "archive" ? ("已归档 " + n + " 个会话")
        : ("已取消归档 " + n + " 个会话"), "ok", 2200);
  }catch(e){
    toast("批量操作失败：" + e.message, "error");
  }finally{
    busy.forEach(function(b){ if (b) b.disabled = false; });
  }
}
function bulkDeleteSessions(){
  var ids = pickedIds();
  if (!ids.length){ toast("先选中会话","warn",1800); return; }
  askConfirm("删除 " + ids.length + " 个会话？", "会话记录、消息与产物会一并清理，无法撤销。", {confirmLabel:"删除",danger:true,sub:"批量删除"}, function(){
    bulkSessionAction("delete");
  });
}
/* 分支：以某条消息为起点新开会话，原文带进输入框（对齐 ChatGPT branchInNewChat / OpenCode fork）。 */
async function branchFromMessage(id){
  if (!S) return;
  if (S.status && S.status.running){ toast("任务运行中，暂不能分叉会话","warn"); return; }
  try{
    var next = await api("/api/session/fork",{id:id});
    if (next.version) applyState(next);
    collapseSidebarNarrow();
    var input = document.getElementById("input");
    if (input) input.focus();
    toast("已分叉：保留这条消息之前的完整对话，直接输入新的追问即可","ok",2800);
  }catch(e){ toast("分叉失败："+e.message,"error"); }
}
function copyMsg(id){
  var a = S && S.active;
  if (!a || !a.messages) return;
  for (var i = 0; i < a.messages.length; i++){
    var m = a.messages[i];
    if (m.id !== id) continue;
    var text = m.role === "assistant" ? displayAssistantText(m) : (splitAttachments(m.text).body.join("\n").trim());
    if (!text) { toast("这条消息没有可复制的内容","warn"); return; }
    copyText(text).then(function(){ toast("已复制消息内容","ok",1600); });
    return;
  }
}
function retryMsg(){
  if (!S || !S.active) return;
  if (S.status && S.status.running){ toast("任务运行中，请先停止再重试","warn"); return; }
  api("/api/retry",{method:"POST"}).then(function(r){
    if (r && r.ok){ refreshState(); toast("正在重新生成…","ok",1600); }
    else toast((r && r.error) || "重试失败","error");
  }).catch(function(e){ toast("重试失败："+e.message,"error"); });
}
/* 编辑 / 撤回消息（对齐 Claude Code：编辑后立即以新文本重跑，撤回删除其后所有回复） */
function startEditMsg(idx){
  if (!S || !S.active) return;
  var msgs = S.active.messages || [];
  if (idx < 0 || idx >= msgs.length || msgs[idx].role !== "user"){ toast("只能编辑用户消息","warn"); return; }
  if (S.status && S.status.running){ toast("任务运行中，请先停止再编辑","warn"); return; }
  var input = document.getElementById("input");
  if (!input) return;
  // 先把未发送草稿存起来：编辑内容会覆盖输入框，取消编辑时要还回去
  var hadDraft = input.value.trim();
  if (hadDraft){
    try{ localStorage.setItem(editDraftKey, input.value); }catch(e){}
    toast("已有草稿已暂存，取消编辑后会恢复","info",3200);
  } else {
    try{ localStorage.removeItem(editDraftKey); }catch(e){}
  }
  editingIndex = idx;
  var parsed = splitAttachments(msgs[idx].text);
  input.value = parsed.body.join("\n");
  var banner = document.getElementById("editBanner");
  var bannerText = document.getElementById("editBannerText");
  if (banner) banner.hidden = false;
  if (bannerText) bannerText.textContent = "正在编辑消息 #"+(idx+1)+"，Enter 提交修改并重新运行";
  resizeInput(); input.focus(); updateSendState(); saveDraft();
  renderChat();
}
function cancelEditMsg(){
  editingIndex = -1;
  var banner = document.getElementById("editBanner");
  if (banner) banner.hidden = true;
  // 恢复编辑前暂存的草稿
  var input = document.getElementById("input");
  if (input && !input.value.trim()){
    try{
      var saved = localStorage.getItem(editDraftKey);
      if (saved){
        input.value = saved;
        localStorage.removeItem(editDraftKey);
        resizeInput(); updateSendState();
      }
    }catch(e){}
  }
  renderChat();
}
async function undoMsg(idx){
  if (!S || !S.active) return;
  var msgs = S.active.messages || [];
  if (idx < 0 || idx >= msgs.length || msgs[idx].role !== "user"){ toast("只能撤回用户消息","warn"); return; }
  if (S.status && S.status.running){ toast("任务运行中，请先停止再撤回","warn"); return; }
  askConfirm("撤回消息","撤回这条消息？其后的所有回复也会被删除。",{confirmLabel:"撤回"},function(){
    api("/api/session/undo",{index:idx}).then(function(r){
      if (r && r.ok){
        editingIndex = -1;
        var banner = document.getElementById("editBanner");
        if (banner) banner.hidden = true;
        refreshState();
        toast("已撤回","ok",1600);
      } else toast("撤回失败","error");
    }).catch(function(e){ toast("撤回失败："+e.message,"error"); });
  });
}
async function exportSession(){
  if (!S || !S.active) return;
  var name = "greentiger-session.md";
  try{
    var res = await fetch("/api/session/export?id="+encodeURIComponent(S.active.id),{credentials:"same-origin"});
    if (!res.ok) throw new Error("HTTP "+res.status);
    var blob = await res.blob();
    var cd = res.headers.get("content-disposition")||"";
    var m = cd.match(/filename\*=UTF-8''([^;]+)/i);
    if (m){
      try { name = decodeURIComponent(m[1]); } catch(e){}
    }
    name = name.replace(/[\\/:*?"<>|]+/g,"_");
    var url = URL.createObjectURL(blob);
    var a = document.createElement("a");
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(function(){ try { URL.revokeObjectURL(url); } catch(e){} },5000);
    toast("已导出 Markdown："+name,"ok",3200);
  }catch(e){
    toast("导出失败："+e.message,"error");
  }
}
async function newChat(){
  if (S && S.status && S.status.running){ toast("任务运行中，暂不能新建对话","warn"); return; }
  try{
    await api("/api/new-chat",{});
    toast("正在新建会话…","info",2400);
  }catch(e){ toast("新建对话失败："+e.message,"error"); }
}
async function chooseWorkdir(){
  if (S && S.status && S.status.running){ toast("任务运行中，暂不能切换工作目录","warn"); return; }
  try{
    var res = await api("/api/pick-folder",{});
    var path = (res && res.path) ? res.path : "";
    if (!path) return;                                   // 用户取消选择
    if (path === (S && S.config && S.config.workDir)){ toast("工作目录未变化","info",1800); return; }
    var next = await api("/api/config",{workDir:path});
    if (next && next.version) applyState(next);
    toast("工作目录已设置","ok");
  }catch(e){ toast("设置工作目录失败："+e.message,"error"); }
}
/* 打开工作目录（资源管理器）；无工作目录时打开数据目录。 */
async function openWorkdir(){
  try{ await api("/api/explorer",{path:""}); }
  catch(e){ toast("打开工作目录失败："+e.message,"error"); }
}
function addAttachment(path,name,size){
  if (!path) return;
  if (attachments.some(function(a){return a.path.toLowerCase()===path.toLowerCase();})) return;
  if (attachments.length >= 10){ toast("一次最多附加 10 个文件","warn"); return; }
  attachments.push({path:path,name:name||baseName(path),size:size||0});
  renderAttachments();
}
async function attachFiles(){
  if (uploading) return;
  try{
    var res = await api("/api/pick-files",{});
    var files = (res && res.files) ? res.files : [];
    if (!files.length) return;
    files.forEach(function(p){ addAttachment(p); });
    toast("已附加 "+files.length+" 个文件","ok",2200);
  }catch(e){ toast("选择文件失败："+e.message,"error"); }
}
/* 「+」菜单 → 引用工作目录文件：插入 @ 并走已有的 @ 弹层逻辑，不另造一套选择器 */
function insertAtMention(){
  var v = input.value || "";
  var sep = v && !/\s$/.test(v) ? " " : "";
  input.value = v + sep + "@";
  try { input.setSelectionRange(input.value.length, input.value.length); } catch(ex){}
  input.focus();
  input.dispatchEvent(new Event("input",{bubbles:true}));
}
async function uploadFiles(fileList){
  if (uploading) return;
  var files = Array.prototype.slice.call(fileList||[]);
  if (!files.length) return;
  uploading = true;
  updateSendState();
  var added = 0;
  for (var i=0;i<files.length;i++){
    var file = files[i];
    if (file.size > 100*1024*1024){ toast(file.name+" 超过 100 MB，已跳过","warn"); continue; }
    try{
      var res = await fetch("/api/upload?name="+encodeURIComponent(file.name),{
        method:"POST",credentials:"same-origin",
        headers:{"Content-Type":file.type||"application/octet-stream"},
        body:file
      });
      var data = null;
      try { data = await res.json(); } catch(e){}
      if (!res.ok || !data || !data.ok) throw new Error((data&&data.error)||("HTTP "+res.status));
      addAttachment(data.path,data.name,file.size);
      added++;
    }catch(e){
      toast("上传 "+file.name+" 失败："+e.message,"error");
    }
  }
  uploading = false;
  updateSendState();
  if (added) toast("已附加 "+added+" 个文件","ok",2400);
}
async function handleSend(){
  if (!S){ toast("控制台尚未连接","error"); return; }
  if (S.status && S.status.running){ stopTask(); return; }
  if (sending || uploading) return;
  var input = document.getElementById("input");
  var text = input ? input.value.trim() : "";
  if (!text && !attachments.length){ if (input) input.focus(); return; }
  if (!S.config || !S.config.workDir){
    toast("请先选择工作目录","warn");
    openSettings();
    return;
  }
  // —— Harness 适配：/ 指令 ——
  if (text.charAt(0) === "/" && text.indexOf(" ") < 0 && !attachments.length){
    var handled = false;
    try{ handled = await trySlashCommand(text); }
    catch(err){ toast("命令执行失败：" + ((err && err.message) || err), "error"); return; }
    if (handled) return;
  }
  var files = attachments.map(function(a){return a.path;});
  if (text) saveInputHistory(text);
  // —— 编辑消息模式：提交修改后的文本，替换原 user 消息并重跑 ——
  if (editingIndex >= 0){
    var target = S.active && S.active.messages[editingIndex];
    if (!target || target.role !== "user"){ toast("编辑目标已失效","error"); editingIndex = -1; cancelEditMsg(); return; }
    sending = true;
    updateSendState();
    try{
      await api("/api/session/edit",{index:editingIndex,text:text});
      editingIndex = -1;
      var banner = document.getElementById("editBanner");
      if (banner) banner.hidden = true;
      if (input){ input.value = ""; closeSlash(); closeAt(); }
      try { localStorage.removeItem(draftKey); } catch(e){}
      attachments = [];
      resizeInput();
      renderAttachments();
      scrollToBottom(false);
      refreshState();
      toast("已更新消息并重新运行","ok",1600);
    }catch(e){
      toast("编辑发送失败："+e.message,"error");
    }finally{
      sending = false;
      updateSendState();
      if (input) input.focus();
    }
    return;
  }
  sending = true;
  updateSendState();
  try{
    await api("/api/chat",{text:text,files:files});
    if (input){ input.value = ""; closeSlash(); closeAt(); }
    try { localStorage.removeItem(draftKey); } catch(e){}
    attachments = [];
    resizeInput();
    renderAttachments();
    scrollToBottom(false);
  }catch(e){
    toast("发送失败："+e.message,"error");
  }finally{
    sending = false;
    updateSendState();
    if (input) input.focus();
  }
}
var stopping = false;
async function stopTask(){
  if (!S || !S.status || !S.status.running) return;
  if (stopping) return;                                  // Esc 与按钮可能同时触发，去重
  stopping = true;
  try{
    await api("/api/stop",{});
    toast("正在停止任务…","warn",2400);
  }catch(e){ toast("停止失败："+e.message,"error"); }
  finally{ stopping = false; }
}
async function pauseTask(){
  if (!S || !S.status || !S.status.running) return;
  try{
    await api("/api/task/pause",{});
    toast("正在暂停任务…暂停后可在检查器点「继续任务」接着跑","warn",3600);
  }catch(e){ toast("暂停失败："+e.message,"error"); }
}
async function restartBrowser(){
  askConfirm("重开控制台窗口","界面白屏或卡死时用。会关掉当前控制台窗口再重新打开；正在跑的任务会被中断。",{confirmLabel:"重开",danger:true},async function(){
    try{
      await api("/api/restart-browser",{});
      toast("正在重开控制台窗口…","info",3000);
    }catch(e){ toast("重开失败："+e.message,"error"); }
  });
}
// 阶段4：继续上次中断的任务（沿用原任务记录与预算，不新建任务）
async function resumeTask(taskId){
  try{
    var body = taskId ? {taskId:taskId} : {};
    var res = await api("/api/task/resume",body);
    if (res && res.ok === false){ toast(res.error || "任务当前不可继续","warn",4000); return; }
    toast(taskId ? "正在继续选中的任务…" : "正在继续上次的任务…","info",2500);
  }catch(e){ toast("继续任务失败："+e.message,"error"); }
}
async function checkUpdate(){
  try{
    var res = await api("/api/check-update",{});
    if (res && res.updateUrl){ toast("发现新版本：" + res.updateUrl,"info",6000); return; }
    toast("当前已是最新版本"+(res && res.version ? (" v"+res.version) : ""),"info");
  }catch(e){ toast("检查更新失败："+e.message,"error"); }
}
function quitApp(){
  askConfirm("退出绿虎","退出后会停止当前任务并关闭本地控制台。是否继续？",{confirmLabel:"退出",danger:true},async function(){
    try{
      await api("/api/shutdown",{});
      shuttingDown = true;
      if (eventSource) eventSource.close();
      document.body.innerHTML = '<div class="farewell"><div class="farewell-card"><div class="farewell-mark"><span class="glyph-swirl" style="width:50px;height:50px;background:var(--accent-ink)"></span></div><h2>绿虎已退出</h2><p>本地控制台已关闭，现在可以关闭这个窗口。</p></div></div>';
    }catch(e){ toast("退出失败："+e.message,"error"); }
  });
}
/* ---------- clipboard ---------- */
function copyText(text){
  if (navigator.clipboard && window.isSecureContext){
    return navigator.clipboard.writeText(text).catch(function(){ return fallbackCopy(text); });
  }
  return Promise.resolve(fallbackCopy(text));
}
function fallbackCopy(text){
  var ta = document.createElement("textarea");
  ta.value = text;
  ta.style.position = "fixed";
  ta.style.left = "-9999px";
  document.body.appendChild(ta);
  ta.focus();
  ta.select();
  try { document.execCommand("copy"); } catch(e){}
  document.body.removeChild(ta);
}
/* ---------- live updates ---------- */
/* 合帧：任务运行中状态/日志推送很密，用 rAF 把同一帧内的多次重渲染并成一次。 */
var stateRaf = null, pendingState = null;
function applyStateSoon(data){
  pendingState = data;
  if (stateRaf) return;
  var raf = window.requestAnimationFrame || function(fn){ return setTimeout(fn, 16); };
  stateRaf = raf(function(){
    stateRaf = null;
    var d = pendingState; pendingState = null;
    applyState(d);
  });
}
var insRaf = null;
/* 上一次渲染出来的面板 HTML：内容没变就跳过 DOM 重建。 */
var lastInspectorHtml = "";
function renderInspectorSoon(){
  if (insRaf) return;
  var raf = window.requestAnimationFrame || function(fn){ return setTimeout(fn, 16); };
  insRaf = raf(function(){ insRaf = null; if (logsOpen) renderInspector(); });
}
function connectSSE(){
  if (shuttingDown) return;
  try { if (eventSource) eventSource.close(); } catch(e){}
  eventSource = new EventSource("/api/events");
  eventSource.onopen = function(){
    sseErrors = 0;
    if (sseWarned){ sseWarned = false; toast("实时连接已恢复","ok",2200); refreshState(); }
  };
  eventSource.onerror = function(){
    if (shuttingDown) return;
    sseErrors++;
    if (sseErrors >= 3 && !sseWarned){
      sseWarned = true;
      toast("实时连接中断，正在自动重连…","warn",4200);
    }
  };
  eventSource.onmessage = function(ev){
    var data = null;
    try { data = JSON.parse(ev.data); } catch(e) { return; }
    if (!data || !data.type) return;
    if (data.type === "state"){
      applyStateSoon(data.state);
    } else if (data.type === "status"){
      if (!S) return;
      var was = !!(S.status && S.status.running);
      S.status = S.status || {};
      S.status.running = !!(data.status && data.status.running);
      if (data.status && typeof data.status.phase === "string") S.status.phase = data.status.phase;
      var bm = document.getElementById("brandMark");
      if (bm) bm.classList.toggle("working", !!S.status.running);
      syncRunStart();
      renderHeader();
      renderStatus();
      if (was && !S.status.running) updateDocTitle();
    } else if (data.type === "assistant"){
      patchAssistant(data.message);
    } else if (data.type === "log"){
      if (S && data.entry){
        S.logs = S.logs || [];
        S.logs.push(data.entry);
        if (S.logs.length > 300) S.logs.splice(0,S.logs.length-300);
      }
      if (logsOpen) renderInspectorSoon();
      else { unreadLogs++; updateLogBadge(); }
    }
  };
}
function patchAssistant(msg){
  if (!S || !S.active || !msg) return;
  var m = null;
  for (var i=0;i<S.active.messages.length;i++){
    if (S.active.messages[i].id === msg.id){ m = S.active.messages[i]; break; }
  }
  if (!m){ refreshState(); return; }
  m.text = msg.text || "";
  m.status = msg.status || "done";
  if (msg.brain) m.brain = msg.brain;
  var sc = document.getElementById("chatScroll");
  var stick = !sc || (sc.scrollHeight - sc.scrollTop - sc.clientHeight) < 130;
  var article = document.querySelector('.msg[data-msg="'+msg.id+'"]');
  if (!article){ renderChat(); return; }
  var md = article.querySelector(".md");
  if (md){
    var display = displayAssistantText(m);
    md.innerHTML = display.trim() ? renderMd(display) : (m.status==="streaming" ? '<span class="thinking"><i></i><i></i><i></i><span>正在思考</span></span>' : '<p class="trunc-note">（无内容）</p>');
  }
  article.classList.toggle("streaming",m.status==="streaming");
  var head = article.querySelector(".msg-head");
  if (head){
    var status = m.status==="streaming" ? '<span class="msg-status"><span class="dot busy"></span>生成中</span>' : (m.status==="error" ? '<span class="msg-status err">回复失败</span>' : "");
    head.innerHTML = '<span class="msg-name">绿虎</span>'+brainTagHtml(m)+status+'<span class="msg-time">'+fmtTime(m.time)+'</span>';
  }
  if (stick && sc) sc.scrollTop = sc.scrollHeight;
  updateScrollButton();
}
function resizeInput(){
  var input = document.getElementById("input");
  if (!input) return;
  input.style.height = "auto";
  input.style.height = Math.min(200,Math.max(24,input.scrollHeight))+"px";
}
function restoreDraft(){
  try{
    var d = localStorage.getItem(draftKey);
    var input = document.getElementById("input");
    if (d && input){ input.value = d; resizeInput(); updateSendState(); }
  }catch(e){}
}
function saveDraft(){
  var input = document.getElementById("input");
  if (!input) return;
  try{
    if (input.value) localStorage.setItem(draftKey,input.value);
    else localStorage.removeItem(draftKey);
  }catch(e){}
}
var inputHistory = [];
var historyIdx = -1;
var historyDraft = "";
function saveInputHistory(text){
  text = (text||"").trim();
  if (!text) return;
  var idx = inputHistory.indexOf(text);
  if (idx >= 0) inputHistory.splice(idx,1);
  inputHistory.unshift(text);
  if (inputHistory.length > 20) inputHistory.length = 20;
  historyIdx = -1;
  try { localStorage.setItem("greentiger.inputHistory", JSON.stringify(inputHistory)); } catch(e){}
}
function loadInputHistory(){
  historyIdx = -1;
  try {
    var raw = localStorage.getItem("greentiger.inputHistory");
    if (raw) inputHistory = JSON.parse(raw) || [];
  } catch(e){ inputHistory = []; }
}
function setHistoryValue(input,val){
  input.value = val;
  resizeInput(); updateSendState();
  var len = val.length;
  requestAnimationFrame(function(){ try { input.setSelectionRange(len,len); } catch(ex){} });
}
/* ---- 斜杠命令弹层（对齐 Claude Code 的 / 菜单）---- */
/* local:true = 前端直接执行；其余发到服务端 Agent（Agent.cs TrySlashCommandAsync） */
var slashState = {open:false, idx:0, items:[]};
var slashPopEl = null;
function slashEl(){ return slashPopEl || (slashPopEl = document.getElementById("slashPop")); }
function updateSlashPop(){
  var pop = slashEl();
  var v = input.value;
  var show = v.length > 0 && v.charAt(0) === "/" && v.indexOf(" ") === -1 && v.indexOf("\n") === -1;
  if (!show){ closeSlash(); return; }
  var q = v.slice(1);
  // 以前只认前缀（cmd.indexOf(q)===0），打错头一个字母整份列表就空了。
  var ranked = fuzzyRank(SLASH_COMMANDS, q, function(c){ return c.cmd; });
  slashState.items = ranked.map(function(r){ return r.item; });
  slashState.hits = ranked.map(function(r){ return r.hits; });
  if (!slashState.items.length){ closeSlash(); return; }
  slashState.open = true;
  slashState.idx = 0;
  pop.innerHTML = slashState.items.map(function(c,i){
    return '<button type="button" class="slash-item" id="slash-opt-'+i+'" role="option" aria-selected="false" data-slash="'+attr(c.cmd)+'"><span class="slash-cmd">'+fuzzyHighlight(c.cmd, slashState.hits[i])+'</span><span class="slash-desc">'+esc(c.desc)+'</span></button>';
  }).join("");
  pop.hidden = false;
  markSlash(0);
}
function closeSlash(){
  if (!slashState.open && (!slashPopEl || slashPopEl.hidden)) return;
  slashState.open = false;
  var pop = slashEl();
  pop.hidden = true;
  pop.innerHTML = "";
  var ta = document.getElementById("input");
  if (ta){ ta.setAttribute("aria-expanded","false"); ta.removeAttribute("aria-activedescendant"); }
}
function applySlashItem(item){
  closeSlash();
  if (!item) return;
  if (item.prompt){                       // 示例任务：只把文案放进输入框，交给用户确认后再发
    input.value = item.prompt;
    resizeInput(); updateSendState(); saveDraft(); input.focus();
    return;
  }
  if (item.local){
    input.value = "";
    resizeInput();
    updateSendState();
    try{ trySlashCommand(item.cmd); }
    catch(err){ toast("命令执行失败：" + ((err && err.message) || err), "error"); }
    input.focus();
    return;
  }
  input.value = item.cmd + " ";
  resizeInput();
  updateSendState();
  input.focus();
}
function markSlash(idx){
  slashState.idx = idx;
  var pop = slashEl();
  var items = pop.querySelectorAll(".slash-item");
  for (var i = 0; i < items.length; i++){
    var on = i === idx;
    items[i].classList.toggle("on", on);
    items[i].setAttribute("aria-selected", on ? "true" : "false");
  }
  var ta = document.getElementById("input");
  if (!ta) return;
  ta.setAttribute("aria-expanded","true");
  // 焦点仍在输入框：用 aria-activedescendant 告诉读屏软件当前高亮到第几项。
  if (items[idx]) ta.setAttribute("aria-activedescendant", items[idx].id);
  else ta.removeAttribute("aria-activedescendant");
}
/* ---- @ 文件引用弹层（对齐 Claude Code 的 @ 提及，支持子目录浏览）---- */
var atState = {open:false, idx:0, items:[], dir:"."};
var atPopEl = null; var atFilesCache = {}; var atReqId = 0;
function atEl(){ return atPopEl || (atPopEl = document.getElementById("atPop")); }
function ensureAtFiles(dir, cb){
  var now = Date.now();
  var hit = atFilesCache[dir];
  if (hit && now - hit.at < 5000){ cb(hit.items); return; }
  var req = ++atReqId;
  fetch("/api/fs/list?path="+encodeURIComponent(dir), {credentials:"same-origin"})
    .then(function(r){ return r.json(); })
    .then(function(j){
      if (req !== atReqId) return;
      var items = (j && j.ok && Array.isArray(j.items)) ? j.items : [];
      atFilesCache[dir] = {items:items, at:now};
      cb(items);
    })
    .catch(function(){ if (req === atReqId) cb([]); });
}
function atTokenStart(){
  var v = input.value;
  var pos = input.selectionStart == null ? v.length : input.selectionStart;
  var before = v.slice(0, pos);
  var m = before.match(/(^|\s)@([^\s@]*)$/);
  if (!m) return -1;
  return pos - m[0].length + m[1].length;
}function updateAtPop(){
  var pop = atEl();
  var v = input.value;
  var pos = input.selectionStart == null ? v.length : input.selectionStart;
  var before = v.slice(0, pos);
  var m = before.match(/(^|\s)@([^\s@]{0,120})$/);
  if (!m){ closeAt(); return; }
  var frag = m[2];
  var slash = frag.lastIndexOf("/");
  var dirPart = slash >= 0 ? frag.slice(0, slash) : ".";
  if (dirPart === "") dirPart = ".";
  var q = (slash >= 0 ? frag.slice(slash+1) : frag).toLowerCase();
  atState.dir = dirPart;
  ensureAtFiles(dirPart, function(items){
    var rows = [];
    if (dirPart !== ".") rows.push({name:"..", path:"..", dir:true, up:true});
    // 目录仍然只认前缀（那是路径导航不是搜索），文件走模糊打分：
    // 以前 name.indexOf(q)>=0，打错一个字母就再也找不到文件。
    var dirs = items.filter(function(it){ return it.dir && (!q || it.name.toLowerCase().indexOf(q) === 0); });
    var fileRank = fuzzyRank(items.filter(function(it){ return !it.dir; }), q, function(it){ return it.name; });
    var files = fileRank.map(function(r){
      // 拍平成和目录项一样的结构（path / name / dir），只是多带一份命中下标，
      // 这样选中它的 applyAtItem 与下面的匹配都不用区分两种形态。
      return {hits:r.hits, path:r.item.path, name:r.item.name, dir:false};
    });
    rows = rows.concat(dirs.slice(0,15), files.slice(0,15));
    if (!rows.length && !dirs.length){ closeAt(); return; }
    atState.open = true;
    atState.idx = 0;
    atState.items = rows;
    pop.innerHTML = rows.map(function(it,i){
      if (it.up) return '<button type="button" class="at-item dir up" id="at-opt-'+i+'" role="option" aria-selected="false" data-at=".." data-name=".."><span class="at-name">… 返回上级目录</span></button>';
      if (it.dir) return '<button type="button" class="at-item dir" id="at-opt-'+i+'" role="option" aria-selected="false" data-at="'+attr(it.path)+'" data-name="'+attr(it.name)+'"><span class="at-name">▸ '+esc(it.name)+'</span><span class="at-dir">回车进入</span></button>';
      return '<button type="button" class="at-item" id="at-opt-'+i+'" role="option" aria-selected="false" data-at="'+attr(it.path)+'" data-name="'+attr(it.name)+'"><span class="at-name">'+fuzzyHighlight(it.name, it.hits)+'</span><span class="at-dir">'+esc(it.path)+'</span></button>';
    }).join("");
    pop.hidden = false;
    markAt(0);
  });
}
function closeAt(){
  if (!atState.open && (!atPopEl || atPopEl.hidden)) return;
  atState.open = false;
  var pop = atEl();
  pop.hidden = true;
  pop.innerHTML = "";
  var ta = document.getElementById("input");
  if (ta){ ta.setAttribute("aria-expanded","false"); ta.removeAttribute("aria-activedescendant"); }
}
function navigateAt(newDirToken){
  // newDirToken: "" 回顶层，否则形如 "src" / "src/sub"
  var start = atTokenStart();
  if (start < 0){ closeAt(); return; }
  var replacement = "@" + (newDirToken ? newDirToken + "/" : "");
  var v = input.value;
  var pos = input.selectionStart;
  input.value = v.slice(0, start) + replacement + v.slice(pos);
  var np = start + replacement.length;
  try { input.setSelectionRange(np, np); } catch(ex){}
  resizeInput(); updateSendState();
  updateAtPop();
}
function applyAtItem(item){
  closeAt();
  if (!item) return;
  if (item.up){ navigateAt(atParentDir(atState.dir)); return; }
  if (item.dir){ navigateAt(atState.dir === "." ? item.name : atState.dir + "/" + item.name); return; }
  addAttachment(item.path, item.name);
  var start = atTokenStart();
  if (start < 0){ resizeInput(); updateSendState(); input.focus(); return; }
  var v = input.value;
  var pos = input.selectionStart;
  input.value = v.slice(0, start) + v.slice(pos);
  try { input.setSelectionRange(start, start); } catch(ex){}
  resizeInput(); updateSendState(); input.focus();
}
function markAt(idx){
  atState.idx = idx;
  var pop = atEl();
  var items = pop.querySelectorAll(".at-item");
  for (var i = 0; i < items.length; i++){
    var on = i === idx;
    items[i].classList.toggle("on", on);
    items[i].setAttribute("aria-selected", on ? "true" : "false");
  }
  var ta = document.getElementById("input");
  if (!ta) return;
  ta.setAttribute("aria-expanded","true");
  // 焦点仍在输入框：用 aria-activedescendant 告诉读屏软件当前高亮到第几项。
  if (items[idx]) ta.setAttribute("aria-activedescendant", items[idx].id);
  else ta.removeAttribute("aria-activedescendant");
}
function handleComposerKey(e){
  if (e.isComposing || composing) return;
  // @ 文件引用弹层：优先于斜杠弹层
  if (atState.open){
    if (e.key === "ArrowDown"){ e.preventDefault(); markAt((atState.idx+1) % atState.items.length); return; }
    if (e.key === "ArrowUp"){ e.preventDefault(); markAt((atState.idx-1+atState.items.length) % atState.items.length); return; }
    if (e.key === "Enter" || e.key === "Tab"){ e.preventDefault(); if (atState.items[atState.idx]) applyAtItem(atState.items[atState.idx]); return; }
    if (e.key === "Escape"){ e.preventDefault(); closeAt(); return; }
  }
  // 斜杠命令弹层：打开时方向键/回车/Tab 优先服务于命令选择
  if (slashState.open){
    if (e.key === "ArrowDown"){ e.preventDefault(); markSlash((slashState.idx+1) % slashState.items.length); return; }
    if (e.key === "ArrowUp"){ e.preventDefault(); markSlash((slashState.idx-1+slashState.items.length) % slashState.items.length); return; }
    if (e.key === "Enter" || e.key === "Tab"){ e.preventDefault(); if (slashState.items[slashState.idx]) applySlashItem(slashState.items[slashState.idx]); return; }
    if (e.key === "Escape"){ e.preventDefault(); closeSlash(); return; }
  }
  // 说明：Esc 中断任务只由 document 级快捷键统一处理，避免两处各发一次 /api/stop。
  if (e.key === "ArrowUp" && !e.shiftKey && !e.altKey && inputHistory.length){
    if (historyIdx === -1) historyDraft = e.target.value;
    if (historyIdx < inputHistory.length - 1){
      e.preventDefault();
      historyIdx++;
      setHistoryValue(e.target, inputHistory[historyIdx] || "");
    }
    return;
  }
  if (e.key === "ArrowDown" && inputHistory.length){
    if (historyIdx > 0){
      e.preventDefault();
      historyIdx--;
      setHistoryValue(e.target, inputHistory[historyIdx] || "");
    } else if (historyIdx === 0){
      e.preventDefault();
      historyIdx = -1;
      setHistoryValue(e.target, historyDraft);
    }
    return;
  }
  if (e.key !== "Enter" || e.isComposing || composing || e.shiftKey) return;
  e.preventDefault();
  if (S && S.status && S.status.running) return;
  handleSend();
}

/* ---------- 统一 Tooltip ----------
 * 参数控件与 ? 帮助入口共用 [data-tip] 一份数据；鼠标悬浮和键盘聚焦都能看到，
 * 位置按触发元素实时计算，靠近视口边缘时自动收回。 */
function initTooltips(){
  var box = document.getElementById("gtTip");
  if (!box) return;
  function hide(){ box.hidden = true; }
  function show(host){
    var text = host.getAttribute("data-tip") || "";
    if (!text){ hide(); return; }
    box.textContent = text;
    box.hidden = false;
    var r = host.getBoundingClientRect();
    var b = box.getBoundingClientRect();
    box.style.left = Math.max(8, Math.min(r.left, window.innerWidth - b.width - 8)) + "px";
    var top = r.top - b.height - 8;
    box.style.top = (top < 8 ? r.bottom + 8 : top) + "px";
  }
  function hostOf(node){ return node && node.closest ? node.closest("[data-tip]") : null; }
  document.addEventListener("mouseover",function(e){
    var host = hostOf(e.target);
    if (host) show(host); else hide();
  });
  document.addEventListener("mouseout",function(e){ if (!e.relatedTarget) hide(); });
  document.addEventListener("focusin",function(e){
    var host = hostOf(e.target);
    if (host) show(host);
  });
  document.addEventListener("focusout",hide);
  window.addEventListener("scroll",hide,true);
  window.addEventListener("resize",hide,true);
  var help = document.getElementById("helpBtn");
  if (help) help.addEventListener("click",function(){ hide(); openShortcuts(); });
}

/* ---------- static bindings ---------- */
function bindEvents(){
  document.getElementById("newSessionBtn").addEventListener("click",newSession);
  document.getElementById("menuBtn").addEventListener("click",toggleSidebar);
  var brandMark = document.getElementById("brandMark");
  if (brandMark) brandMark.addEventListener("click",function(){
    brandMark.classList.toggle("spinning");
    brandMark.setAttribute("aria-pressed", brandMark.classList.contains("spinning") ? "true" : "false");
  });
  // 示例任务已收进「/」命令，空态不再堆建议卡片。
  document.getElementById("settingsBtn").addEventListener("click",openSettings);
  var memoryBtn = document.getElementById("memoryBtn");
  if (memoryBtn) memoryBtn.addEventListener("click",function(){ openSettings("memory"); });
  var _workbenchBtn = document.getElementById("workbenchBtn");
  if (_workbenchBtn) _workbenchBtn.addEventListener("click",function(){ toggleLogs(); });
  // 工作台分段：切换后必须强制重绘，否则「内容没变」的短路会把新分段挡掉
  var _wbTabs = document.getElementById("wbTabs");
  if (_wbTabs) _wbTabs.addEventListener("click",function(e){
    var tab = e.target && e.target.closest ? e.target.closest("[data-wb-tab]") : null;
    if (!tab) return;
    wbTab = tab.getAttribute("data-wb-tab") || "artifacts";
    lastInspectorHtml = "";
    renderInspector();
  });
  document.getElementById("themeBtn").addEventListener("click",function(){
    var shell = window.GreenTigerShell;
    var next = shell && shell.toggleTheme
      ? shell.toggleTheme()
      : (document.documentElement.dataset.theme === "dark" ? "light" : "dark");
    if (!shell || !shell.toggleTheme) applyThemeSafe(next);
    settingsTheme = next;
    // 服务端 config 是异步落库的，期间推来的旧 state 会把主题打回去；先锁 3 秒本地优先。
    themeLocalUntil = Date.now() + 3000;
    api("/api/config",{theme:next}).catch(function(){ toast("主题已切换，但未能保存","error"); });
  });
  document.getElementById("stopTopBtn").addEventListener("click",stopTask);
  document.getElementById("pauseTopBtn").addEventListener("click",pauseTask);
  document.getElementById("workdirChip").addEventListener("click",chooseWorkdir);
  document.getElementById("attachBtn").addEventListener("click",attachFiles);
  // ===== 输入区「+」分组菜单 =====
  var addBtn = document.getElementById("addBtn");
  var addPop = document.getElementById("addPop");
  if (addBtn && addPop){
    var setAddPop = function(open){
      addPop.hidden = !open;
      addBtn.setAttribute("aria-expanded", open ? "true" : "false");
      addBtn.classList.toggle("active", open);
    };
    addBtn.addEventListener("click",function(e){
      e.stopPropagation();
      setAddPop(addPop.hidden);
    });
    addPop.addEventListener("click",function(e){
      if (e.target.closest && e.target.closest(".add-item")) setAddPop(false);
    });
    document.addEventListener("click",function(e){
      if (addPop.hidden) return;
      if (e.target.closest && e.target.closest("#addPop,#addBtn")) return;
      setAddPop(false);
    });
    document.addEventListener("keydown",function(e){
      if (e.key === "Escape" && !addPop.hidden){ setAddPop(false); addBtn.focus(); }
    });
  }
  var addRefBtn = document.getElementById("addRefBtn");
  if (addRefBtn) addRefBtn.addEventListener("click",insertAtMention);
  var addWorkdirBtn = document.getElementById("addWorkdirBtn");
  if (addWorkdirBtn) addWorkdirBtn.addEventListener("click",openWorkdir);
  var addPaletteBtn = document.getElementById("addPaletteBtn");
  if (addPaletteBtn) addPaletteBtn.addEventListener("click",openPalette);
  document.getElementById("sendBtn").addEventListener("click",handleSend);
  document.getElementById("paletteBtn").addEventListener("click",openPalette);
  // ===== 精简顶栏：次要应用级入口收进「⋯」溢出菜单 =====
  var moreBtn = document.getElementById("moreBtn");
  var topMenu = document.getElementById("topMenu");
  if (moreBtn && topMenu){
    var setTopMenu = function(open){
      topMenu.hidden = !open;
      moreBtn.setAttribute("aria-expanded", open ? "true" : "false");
      moreBtn.classList.toggle("active", open);
    };
    moreBtn.addEventListener("click",function(e){
      e.stopPropagation();
      setTopMenu(topMenu.hidden);
    });
    topMenu.addEventListener("click",function(e){
      if (e.target.closest && e.target.closest(".menu-item")) setTopMenu(false);
    });
    document.addEventListener("click",function(e){
      if (topMenu.hidden) return;
      if (e.target.closest && e.target.closest("#topMenu,#moreBtn")) return;
      setTopMenu(false);
    });
    document.addEventListener("keydown",function(e){
      if (e.key === "Escape" && !topMenu.hidden){ setTopMenu(false); moreBtn.focus(); }
    });
  }
  bindGardenOverlay();
  initTooltips();
  // ===== P1 参数后置：齿轮开合 =====
  var paramsBtn = document.getElementById("paramsBtn");
  var cmpParams = document.getElementById("cmpParams");
  var paramsClose = document.getElementById("paramsClose");
  if (paramsBtn && cmpParams){
    var setParamsOpen = function(open){
      cmpParams.hidden = !open;
      paramsBtn.setAttribute("aria-expanded", open ? "true" : "false");
    };
    paramsBtn.addEventListener("click",function(){
      setParamsOpen(cmpParams.hidden);
      // 面板展开后把齿轮自己的 Tooltip 收掉，避免右下角叠两层提示
      var tip = document.getElementById("gtTip");
      if (tip) tip.hidden = true;
    });
    if (paramsClose) paramsClose.addEventListener("click",function(){ setParamsOpen(false); paramsBtn.focus(); });
    document.addEventListener("click",function(e){
      if (cmpParams.hidden) return;
      if (e.target.closest && (e.target.closest("#cmpParams") || e.target.closest("#paramsBtn"))) return;
      setParamsOpen(false);
    });
  }
  document.addEventListener("click",function(e){
    var button = e.target && e.target.closest ? e.target.closest("button") : null;
    if (button && !button.disabled) playUiSound("tap");
  });

  // ===== Codex 风格作曲栏：推理强度 / 模型切换 / 听写 =====
  var EFFORT_OPTIONS = [
    {key:"low",label:"快速",detail:"快速直接，适合简单任务。"},
    {key:"medium",label:"平衡",detail:"速度与推理深度兼顾，适合日常使用。"},
    {key:"high",label:"深入",detail:"为复杂任务增加分析与核验。"},
    {key:"xhigh",label:"极致",detail:"为高难度任务投入最多分析与交叉检查。"}
  ];
  function currentEffort(){ return ((S && S.config && S.config.reasoningEffort) || "medium"); }
  function effortIndex(value){
    for (var i=0;i<EFFORT_OPTIONS.length;i++) if (EFFORT_OPTIONS[i].key===value) return i;
    return 1;
  }
  // 横向滑块：idx(0..3) → 轨道 0/33/66/100%，同步填充条、手柄、aria 与文案
  function paintEffort(idx){
    idx = Math.max(0, Math.min(3, idx));
    var pct = (idx/3*100);
    var fill = document.getElementById("effortFill");
    var thumb = document.getElementById("effortThumb");
    if (fill) fill.style.width = pct + "%";
    if (thumb) thumb.style.left = pct + "%";
    var range = document.getElementById("effortRange");
    if (range){
      range.setAttribute("aria-valuenow", String(idx));
      range.setAttribute("aria-valuetext", EFFORT_OPTIONS[idx].label+" · "+EFFORT_OPTIONS[idx].detail);
      range.setAttribute("data-tip","推理强度 · "+EFFORT_OPTIONS[idx].label+"：\n"+EFFORT_OPTIONS[idx].detail+"\n拖动滑块或用方向键调档。");
    }
    var hint = document.getElementById("effortHint");
    if (hint) hint.textContent = EFFORT_OPTIONS[idx].label+" · "+EFFORT_OPTIONS[idx].detail;
    var cur = document.getElementById("effortCurrent");
    if (cur && cur.textContent !== EFFORT_OPTIONS[idx].label){
      cur.textContent = EFFORT_OPTIONS[idx].label;
      cur.classList.remove("settle");
      void cur.offsetWidth;   // 重启动画，让标签与滑钮同一拍落位
      cur.classList.add("settle");
    }
    // 高亮当前档位刻度（对标 ChatGPT 滑块的选中态）
    var ticks = document.querySelectorAll(".effort-ticks span[data-eff]");
    for (var ti = 0; ti < ticks.length; ti++){
      ticks[ti].classList.toggle("on", ticks[ti].getAttribute("data-eff") === String(idx));
    }
  }
  var effortLock = false;
  function renderComposerSelectors(){
    var idx = effortIndex(currentEffort());
    // 正在拖动或键盘调档时不要被 SSE 旧状态拽回去
    if (!effortLock && document.activeElement !== document.getElementById("effortRange")) paintEffort(idx);
    var mb = document.getElementById("modelBtn");
    if (mb && typeof brainInfo === "function"){
      // 合并后的按钮：状态点与名称由 renderHeader 的 connInfo 写入，这里只补 title/aria
      try {
        var bi = brainInfo();
        mb.title = "点击切换模型";
        mb.setAttribute("aria-label", "当前模型：" + (bi.short || "未连接") + "，点击切换");
      } catch(e){}
    }
  }
  // 顶栏（IIFE 外）也要能刷新这两个控件：否则「已连接」右边的模型指示不会跟着状态走
  window.renderComposerSelectors = renderComposerSelectors;
  // Esc 要能关掉模型面板：面板现在在输入框顶部，仍属于「最表层」的浮层，键盘用户不该只能点空白处关闭
  window.closeModelPop = closeCmpPop;
  function setModelPopExpanded(open){
    var mb = document.getElementById("modelBtn");
    if (mb) mb.setAttribute("aria-expanded", open ? "true" : "false");
  }
  function closeCmpPop(){ var p = document.getElementById("cmpPop"); if (p) p.hidden = true; setModelPopExpanded(false); }
  function openCmpPop(items,onPick){
    var pop = document.getElementById("cmpPop");
    if (!pop) return;
    setModelPopExpanded(true);
    var html = "";
    for (var i=0;i<items.length;i++){
      var it = items[i];
      if (it.sep){ html += '<div class="cmp-pop-sep"></div>'; continue; }
      var isHead = (it.key === "head") || it.head;
      html += '<button type="button" class="cmp-pop-item'+(it.on?" on":"")+(isHead?" head":"")+'" data-cmp="'+attr(it.key)+'">'
        + esc(it.label) + (it.on ? '<span class="tick">✓</span>' : '') + '</button>';
    }
    // 只重写模型列表区：面板底部的「思考强度」是静态 DOM，避免 innerHTML 冲掉它的事件绑定
    var itemsEl = document.getElementById("cmpPopItems") || pop;
    itemsEl.innerHTML = html;
    pop.hidden = false;
    var btns = itemsEl.querySelectorAll("[data-cmp]");
    for (var j=0;j<btns.length;j++){
      btns[j].addEventListener("click", function(){ closeCmpPop(); onPick(this.getAttribute("data-cmp")); });
    }
  }
  document.addEventListener("click", function(e){
    var p = document.getElementById("cmpPop");
    if (!p || p.hidden) return;
    var t = e.target;
    if (t && t.closest && t.closest("#cmpPop,#modelBtn")) return;
    p.hidden = true;
  });
  async function applyCfg(patch,msg){
    /* 乐观更新：切换请求有往返，但「当前模型」必须立刻变。
       先改本地 state 让胶囊/提示换成新名字，服务端返回整份 state 后再以它为准。 */
    if (patch && patch.providerModel && S && S.brain && S.brain.provider){
      S.brain.provider.model = patch.providerModel;
      applyState(S);
    }
    try {
      var next = await api("/api/config", patch);
      // 接口回的是整份 state：只取 config 的话 S.brain / S.status 还是旧的，
      // 顶栏「已连接」右边的模型指示就会停在切换前的名字上
      if (next && next.version) applyState(next);
      else if (next && next.config) S.config = next.config;
      renderComposerSelectors();
      if (msg) toast(msg);
    } catch(err){
      renderComposerSelectors();
      toast("切换失败：" + (err && err.message ? err.message : err), "error");
    }
  }
  var effortRangeEl = document.getElementById("effortRange");
  if (effortRangeEl){
    // 横向滑块：点/拖轨道落定档位，方向键微调；视觉与持久化都走同一个 setEffort
    var effDragging = false;
    var effPending = effortIndex(currentEffort());
    function effIdxFromX(clientX){
      var track = effortRangeEl.querySelector(".effort-track");
      var rect = track.getBoundingClientRect();
      var ratio = rect.width ? (clientX - rect.left) / rect.width : 0;
      return Math.max(0, Math.min(3, Math.round(ratio * 3)));
    }
    function setEffort(idx, persist){
      idx = Math.max(0, Math.min(3, idx));
      effPending = idx;
      paintEffort(idx);
      if (persist !== false){
        var selected = EFFORT_OPTIONS[idx];
        applyCfg({reasoningEffort:selected.key}, "推理强度：" + selected.label);
      }
    }
    effortRangeEl.addEventListener("pointerdown", function(e){
      effDragging = true; effortLock = true;
      effortRangeEl.classList.add("dragging");
      try { effortRangeEl.focus(); effortRangeEl.setPointerCapture(e.pointerId); } catch(_){}
      setEffort(effIdxFromX(e.clientX), false);
      e.preventDefault();
    });
    effortRangeEl.addEventListener("pointermove", function(e){
      if (!effDragging) return;
      setEffort(effIdxFromX(e.clientX), false);
    });
    function effStop(){
      if (!effDragging) return;
      effDragging = false; effortLock = false;
      effortRangeEl.classList.remove("dragging");
      setEffort(effPending); // 松手落定为当前档位并持久化
    }
    effortRangeEl.addEventListener("pointerup", effStop);
    effortRangeEl.addEventListener("pointercancel", effStop);
    effortRangeEl.addEventListener("keydown", function(e){
      var idx = effortIndex(currentEffort());
      if (e.key === "ArrowRight" || e.key === "ArrowUp"){ e.preventDefault(); setEffort(idx+1); }
      else if (e.key === "ArrowLeft" || e.key === "ArrowDown"){ e.preventDefault(); setEffort(idx-1); }
      else if (e.key === "Home"){ e.preventDefault(); setEffort(0); }
      else if (e.key === "End"){ e.preventDefault(); setEffort(3); }
    });
  }
  var modelBtnEl = document.getElementById("modelBtn");
  function openModelMenu(){
    var b = (S && S.brain) || {};
    var pv = b.provider || {};
    var cur = pv.model || "";
    var models = (pv.models || []).slice(0, 8);
    // 面板第一行永远写「当前模型 · 名字」：切换前看得见现在是谁，切换后不用回看顶栏才知道生效的是哪个。
    // 没接 Key 也照样把选定的模型名亮出来，只标「未接入」，不把用户挑过的模型藏起来。
    var items = [];
    var headLabel = cur ? ("当前模型 · " + cur) : "当前模型 · 未接入";
    items.push({key:"head", label:headLabel, on:b.active==="provider"});
    // DisplayName 里已带了「· 模型名」，菜单第二行只留服务商名，避免和第一行重复
    var pname = b.providerName || "";
    var suffix = cur ? (" · " + cur) : "";
    if (suffix && pname.lastIndexOf(suffix) === pname.length - suffix.length) pname = pname.slice(0, pname.length - suffix.length);
    if (pname) items.push({key:"noop", label:"服务商 · " + pname, on:false});
    if (b.active !== "provider" && cur && pname) items.push({key:"noop", label:"状态 · 未接入（还差 API Key）", on:false});
    if (models.length){
      for (var mi=0; mi<models.length; mi++){
        items.push({key:"model:" + models[mi], label:models[mi] + (models[mi] === cur ? " · 当前" : ""), on:models[mi] === cur});
      }
      var recent = readList(RECENT_MODELS_KEY).filter(function(n){ return models.indexOf(n) < 0; });
      for (var ri=0;ri<recent.length;ri++) items.push({key:"model:"+recent[ri],label:recent[ri],on:false});
      items.push({sep:true});
    }
    items.push({key:"settings",label:"配置模型服务商"});
    openCmpPop(items, function(k){
      if (k === "settings"){ closeCmpPop(); openSettings("models"); return; }
      if (k === "noop" || k === "head"){ closeCmpPop(); return; }
      if (k.indexOf("model:") === 0){ var mn=k.slice(6); closeCmpPop(); recordModelUse(mn); applyCfg({providerModel:mn}, "已切换到模型 · " + mn); return; }
    });
  }
  /* 免费额度入口已移除：目录只留旗舰模型，弱模型的“先用免费档”路径不再出现在切换面板。 */
  if (modelBtnEl) modelBtnEl.addEventListener("click", openModelMenu);
  // 听写：Web Speech API（Chromium 内核 + 可联网时可用，不支持时给出明确提示）
  var dictating = false, recObj = null;
  function stopDictation(){
    dictating = false; recObj = null;
    var btn = document.getElementById("dictateBtn");
    if (btn){ btn.classList.remove("recording"); btn.title = "听写（语音输入）"; }
  }
  function toggleDictation(){
    var SR = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!SR){ toast("当前环境不支持听写（需要 Chromium 内核并联网）", "error"); return; }
    if (dictating){ if (recObj){ try { recObj.stop(); } catch(e){} } stopDictation(); return; }
    var ta = document.getElementById("input");
    if (!ta) return;
    var rec = new SR();
    rec.lang = "zh-CN"; rec.interimResults = true; rec.continuous = true;
    var base = ta.value;
    rec.onresult = function(ev){
      var txt = "";
      for (var i = ev.resultIndex; i < ev.results.length; i++) txt += ev.results[i][0].transcript;
      ta.value = base + txt;
      try { ta.dispatchEvent(new Event("input",{bubbles:true})); } catch(e){}
    };
    rec.onerror = function(ev){
      var m = (ev && ev.error) ? ev.error : "未知";
      if (m === "not-allowed" || m === "service-not-allowed") toast("听写被拒绝：请允许麦克风权限", "error");
      else if (m === "network") toast("听写需要联网（语音识别走在线服务）", "error");
      else toast("听写失败：" + m, "error");
      stopDictation();
    };
    rec.onend = function(){ stopDictation(); };
    recObj = rec; dictating = true;
    var btn = document.getElementById("dictateBtn");
    if (btn){ btn.classList.add("recording"); btn.title = "停止听写"; }
    try { rec.start(); toast("开始听写，说话即可上屏"); }
    catch(e){ toast("无法启动听写：" + (e && e.message ? e.message : e), "error"); stopDictation(); }
  }
  var dictateBtnEl = document.getElementById("dictateBtn");
  if (dictateBtnEl) dictateBtnEl.addEventListener("click", toggleDictation);
  renderComposerSelectors();
  document.getElementById("scrollBottom").addEventListener("click",function(){ scrollToBottom(true); });
  syncDockHeight();
  var _insClose = document.getElementById("inspectorClose");
  if (_insClose) _insClose.addEventListener("click",function(){ toggleLogs(false); });
  var input = document.getElementById("input");
  input.addEventListener("keydown",handleComposerKey);
  var editCancel = document.getElementById("editCancel");
  if (editCancel) editCancel.addEventListener("click",cancelEditMsg);
  input.addEventListener("input",function(){ resizeInput(); updateSendState(); saveDraft(); updateSlashPop(); updateAtPop(); });
  input.addEventListener("compositionstart",function(){ composing = true; });
  input.addEventListener("compositionend",function(){ composing = false; });

  document.getElementById("chatScroll").addEventListener("scroll",updateScrollButton,{passive:true});

  // 归档 / 未归档：两个 tab 显式切换，切换时退出批量选择避免选中项跨视图错位
  var sideTabs = document.querySelectorAll("#sidebar .side-tab");
  for (var sti = 0; sti < sideTabs.length; sti++){
    sideTabs[sti].addEventListener("click", function(){
      var want = this.getAttribute("data-session-tab") === "archived";
      if (want === showArchived) return;
      showArchived = want;
      exitSelectMode();
      lastSidebarHtml = "";
      renderSidebar();
    });
  }
  var selectToggle = document.getElementById("selectToggle");
  if (selectToggle) selectToggle.addEventListener("click", function(){
    if (selectMode) exitSelectMode(); else enterSelectMode();
    lastSidebarHtml = "";
    renderSidebar();
  });
  var bulkCancelBtn = document.getElementById("bulkCancelBtn");
  if (bulkCancelBtn) bulkCancelBtn.addEventListener("click", function(){
    exitSelectMode();
    lastSidebarHtml = "";
    renderSidebar();
  });
  var bulkAllBtn = document.getElementById("bulkAllBtn");
  if (bulkAllBtn) bulkAllBtn.addEventListener("click", function(){
    var ids = lastVisibleIds;
    var allSel = ids.length > 0 && ids.every(function(id){ return !!selectedSessions[id]; });
    for (var i = 0; i < ids.length; i++){
      if (allSel) delete selectedSessions[ids[i]];
      else selectedSessions[ids[i]] = true;
    }
    lastSidebarHtml = "";
    renderSidebar();
  });
  var bulkArchiveBtn = document.getElementById("bulkArchiveBtn");
  if (bulkArchiveBtn) bulkArchiveBtn.addEventListener("click", function(){
    bulkSessionAction(showArchived ? "unarchive" : "archive");
  });
  var bulkDeleteBtn = document.getElementById("bulkDeleteBtn");
  if (bulkDeleteBtn) bulkDeleteBtn.addEventListener("click", bulkDeleteSessions);
  document.getElementById("sessionList").addEventListener("click",function(e){
    var grp = e.target.closest("[data-group]");
    if (grp){ e.stopPropagation(); toggleGroup(grp.getAttribute("data-group")); return; }
    var pin = e.target.closest("[data-pin-session]");
    if (pin){
      e.stopPropagation();
      var pid = pin.getAttribute("data-pin-session");
      var ps = sessionById(pid);
      setSessionFlag(pid,"pin",!(ps && ps.pinned));
      return;
    }
    var arc = e.target.closest("[data-archive-session]");
    if (arc){
      e.stopPropagation();
      var aid = arc.getAttribute("data-archive-session");
      var as = sessionById(aid);
      setSessionFlag(aid,"archive",!(as && as.archived));
      return;
    }
    var del = e.target.closest("[data-del-session]");
    if (del){ e.stopPropagation(); deleteSession(del.getAttribute("data-del-session")); return; }
    var item = e.target.closest("[data-session]");
    if (!item) return;
    if (selectMode) togglePick(item.getAttribute("data-session"));
    else selectSession(item.getAttribute("data-session"));
  });
  document.getElementById("sessionList").addEventListener("keydown",function(e){
    var item = e.target.closest("[data-session]");
    // 焦点在行内按钮（删除）上时，按键由按钮自己处理，不要再冒泡成「选中会话」
    if (item && e.target.closest("button") && e.target !== item) return;
    if (e.key === "F2"){                                   // 重命名不只有双击入口，键盘也够得着
      if (item){ e.preventDefault(); renameSession(item.getAttribute("data-session")); }
      return;
    }
    if (e.key!=="Enter" && e.key!==" ") return;
    if (!item) return;
    e.preventDefault();
    if (selectMode) togglePick(item.getAttribute("data-session"));
    else selectSession(item.getAttribute("data-session"));
  });
  document.getElementById("sessionList").addEventListener("dblclick",function(e){
    if (selectMode) return;
    if (e.target.closest("[data-del-session],[data-pin-session],[data-archive-session]")) return;
    var item = e.target.closest("[data-session]");
    if (item) renameSession(item.getAttribute("data-session"));
  });
  var slashPopDom = document.getElementById("slashPop");
  if (slashPopDom){
    slashPopDom.addEventListener("click",function(e){
      var item = e.target.closest("[data-slash]");
      if (!item) return;
      var cmd = item.getAttribute("data-slash");
      var match = null;
      for (var i = 0; i < SLASH_COMMANDS.length; i++) if (SLASH_COMMANDS[i].cmd === cmd) { match = SLASH_COMMANDS[i]; break; }
      applySlashItem(match || {cmd:cmd});
    });
    slashPopDom.addEventListener("mousemove",function(e){
      var item = e.target.closest(".slash-item");
      if (!item) return;
      var items = Array.prototype.slice.call(slashPopDom.querySelectorAll(".slash-item"));
      markSlash(items.indexOf(item));
    });
  }
  var atPopDom = document.getElementById("atPop");
  if (atPopDom){
    atPopDom.addEventListener("click",function(e){
      var item = e.target.closest("[data-at]");
      if (!item) return;
      var path = item.getAttribute("data-at");
      var name = item.getAttribute("data-name");
      var match = null;
      for (var i = 0; i < atState.items.length; i++) if (atState.items[i].path === path) { match = atState.items[i]; break; }
      applyAtItem(match || {path:path, name:name});
    });
    atPopDom.addEventListener("mousemove",function(e){
      var item = e.target.closest(".at-item");
      if (!item) return;
      var items = Array.prototype.slice.call(atPopDom.querySelectorAll(".at-item"));
      markAt(items.indexOf(item));
    });
  }
  var search = document.getElementById("sessionSearch");
  if (search){
    search.addEventListener("input",function(){ sessionQuery = search.value; renderSidebar(); });
    search.addEventListener("keydown",function(e){
      if (e.key === "Escape"){
        e.stopPropagation();
        search.value = "";
        sessionQuery = "";
        renderSidebar();
      }
    });
  }

  document.getElementById("attachments").addEventListener("click",function(e){
    var rm = e.target.closest("[data-remove-att]");
    if (!rm) return;
    var idx = parseInt(rm.getAttribute("data-remove-att"),10);
    if (!isNaN(idx)){ attachments.splice(idx,1); renderAttachments(); saveDraft(); }
  });

  document.getElementById("chat").addEventListener("click",function(e){
    if (e.target.closest("[data-open-model-settings]")){ openSettings("models"); return; }
    var editBtn = e.target.closest("[data-edit-msg]");
    if (editBtn){ startEditMsg(parseInt(editBtn.getAttribute("data-edit-msg"),10)); return; }
    var undoBtn = e.target.closest("[data-undo-msg]");
    if (undoBtn){ undoMsg(parseInt(undoBtn.getAttribute("data-undo-msg"),10)); return; }
    var copyMsgBtn = e.target.closest("[data-copy-msg]");
    if (copyMsgBtn){ copyMsg(copyMsgBtn.getAttribute("data-copy-msg")); return; }
    var branchBtn = e.target.closest("[data-branch-msg]");
    if (branchBtn){ branchFromMessage(branchBtn.getAttribute("data-branch-msg")); return; }
    var retryBtn = e.target.closest("[data-retry-msg]");
    if (retryBtn){ retryMsg(); return; }
    var copy = e.target.closest("[data-copy-code]");
    if (copy){
      var box = copy.closest(".codeblock") || copy.closest(".tool-section");
      var code = box ? box.querySelector("code") : null;
      if (code){ copyText(code.textContent).then(function(){ toast("已复制","ok",1600); }); }
      return;
    }
    var rollback = e.target.closest("[data-rollback-file]");
    if (rollback){
      var rbPath = rollback.getAttribute("data-rollback-file") || "";
      var rbSnap = rollback.getAttribute("data-rollback-snapshot") || "";
      askConfirm("回滚这次改动？", "将把 "+rbPath+" 恢复到本次修改前的内容。当前内容会先另存一份快照，所以这一步也能再回滚。",
        {confirmLabel:"回滚", danger:true, sub:"文件改动"},
        function(){
          api("/api/backups/restore",{snapshot:rbSnap, path:rbPath}).then(function(){
            toast("已回滚到改动前","ok");
            refreshState();
          }).catch(function(err){ toast("回滚失败："+err.message,"error"); });
        });
      return;
    }
    var ckBtn = e.target.closest("[data-rollback-ckpt]");
    if (ckBtn){
      var ckMan = ckBtn.getAttribute("data-rollback-ckpt") || "";
      askConfirm("回滚这条命令的改动？", "将把工作目录恢复到这条 shell 命令执行前的状态：被改的文件还原、之后新建的文件删除。当前状态会先另存一份新档，所以这一步也能再回滚。",
        {confirmLabel:"回滚", danger:true, sub:"命令改动"},
        function(){
          api("/api/backups/restore_checkpoint",{manifest:ckMan}).then(function(){
            toast("已回滚到命令前","ok");
            refreshState();
          }).catch(function(err){ toast("回滚失败："+err.message,"error"); });
        });
      return;
    }
    var memBtn = e.target.closest("[data-mem-accept],[data-mem-dismiss]");
    if (memBtn){
      var acc = memBtn.hasAttribute("data-mem-accept");
      var mid = parseInt(acc?memBtn.getAttribute("data-mem-accept"):memBtn.getAttribute("data-mem-dismiss"),10);
      api("/api/memory/review",{id:mid,accept:acc}).then(function(){
        toast(acc?"已采纳进项目记忆":"已丢弃","ok");
        renderMemoryReview();
      }).catch(function(err){ toast("操作失败："+err.message,"error"); });
      return;
    }
    var toggle = e.target.closest("[data-tool-toggle]");
    if (toggle){
      var id = toggle.getAttribute("data-tool-toggle");
      var card = toggle.closest(".tool");
      var body = card ? card.querySelector(".tool-body") : null;
      if (!card || !body) return;
      var open = card.classList.contains("open");
      if (open){ card.classList.remove("open"); body.hidden = true; expandedTools.delete(id); collapsedTools.add(id); }
      else { card.classList.add("open"); body.hidden = false; expandedTools.add(id); collapsedTools.delete(id); }
      toggle.setAttribute("aria-expanded",open?"false":"true");
      return;
    }
  });

  document.getElementById("scrim").addEventListener("click",function(){
    if (!sidebarCollapsed() && window.innerWidth <= 900) collapseSidebar();
    else if (logsOpen) toggleLogs(false);
  });
  // 点抽屉外面收起：仅窄屏生效，宽屏常驻栏不需要「点外面关闭」
  document.addEventListener("click",function(e){
    if (sidebarCollapsed() || window.innerWidth > 900) return;
    var t = e.target;
    if (t && t.closest && t.closest("#sidebar,#menuBtn")) return;
    collapseSidebar();
  });
  // 跨断点时切换侧栏形态：进窄屏收成抽屉，回宽屏恢复常驻左栏
  var wasNarrow = window.innerWidth <= 900;
  window.addEventListener("resize",function(){
    var narrow = window.innerWidth <= 900;
    if (narrow !== wasNarrow){
      wasNarrow = narrow;
      setSidebarCollapsed(narrow);
    }
    syncScrim();
  });
  window.addEventListener("beforeunload",saveDraft);

  document.addEventListener("dragover",function(e){
    if (e.dataTransfer && Array.prototype.indexOf.call(e.dataTransfer.types||[],"Files")>=0) e.preventDefault();
  });
  document.addEventListener("drop",function(e){
    if (!(e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length)) return;
    e.preventDefault();
    uploadFiles(e.dataTransfer.files);
  });
  var composer = document.getElementById("composer");
  ["dragenter","dragover"].forEach(function(name){
    composer.addEventListener(name,function(e){ e.preventDefault(); composer.classList.add("dragging"); });
  });
  composer.addEventListener("dragleave",function(e){
    if (!composer.contains(e.relatedTarget)) composer.classList.remove("dragging");
  });
  document.addEventListener("drop",function(){ composer.classList.remove("dragging"); });

  document.addEventListener("paste",function(e){
    if (S && S.approval) return;
    var files = e.clipboardData && e.clipboardData.files;
    if (files && files.length){ e.preventDefault(); uploadFiles(files); }
  });
}
/* ---------- delegated dialogs ---------- */
function bindDialogEvents(){
  var root = document.getElementById("dialogRoot");
  root.addEventListener("click",async function(e){
    if (e.target.closest("#confirmYes")){
      var cb = confirmCallback;
      closeDialog();
      if (cb) { setTimeout(function(){ cb(); },20); }
      return;
    }
    if (e.target.closest("[data-dialog-close]")){ closeDialog(); return; }
    if (e.target.hasAttribute("data-dialog-backdrop") && e.target === e.currentTarget.firstElementChild){ closeDialog(); return; }
  });
  root.addEventListener("submit",function(e){
    if (e.target && e.target.id === "settingsForm"){
      e.preventDefault();
      saveSettings();
    }
  });
  // 项目名 / 定时任务输入框里按 Enter 应当是「添加」，而不是提交整个设置表单
  var ENTER_TO_ADD = {wsName:"wsAddBtn", taskName:"taskAddBtn", taskSchedule:"taskAddBtn", taskPrompt:"taskAddBtn", permitMatch:"permitAddBtn"};
  root.addEventListener("keydown",function(e){
    if (e.key !== "Enter" || e.shiftKey || e.ctrlKey || e.metaKey) return;
    var t = e.target;
    if (!t || !t.id || !ENTER_TO_ADD[t.id]) return;
    var btn = document.getElementById(ENTER_TO_ADD[t.id]);
    if (!btn) return;
    e.preventDefault();
    btn.click();
  });
}
function bindPaletteEvents(){
  var root = document.getElementById("paletteRoot");
  root.addEventListener("input",function(e){
    if (e.target && e.target.id === "paletteInput"){ paletteIndex = 0; renderPaletteList(); }
  });
  root.addEventListener("keydown",function(e){
    if (!e.target || e.target.id !== "paletteInput") return;
    if (e.key === "ArrowDown"){
      e.preventDefault(); e.stopPropagation();
      if (paletteFiltered.length){ paletteIndex = (paletteIndex+1)%paletteFiltered.length; renderPaletteList(); }
    } else if (e.key === "ArrowUp"){
      e.preventDefault(); e.stopPropagation();
      if (paletteFiltered.length){ paletteIndex = (paletteIndex-1+paletteFiltered.length)%paletteFiltered.length; renderPaletteList(); }
    } else if (e.key === "Enter"){
      e.preventDefault(); e.stopPropagation(); runPaletteAction();
    } else if (e.key === "Escape"){
      e.preventDefault(); e.stopPropagation(); closePalette();
    }
  });
  root.addEventListener("click",function(e){
    if (e.target.hasAttribute("data-palette-backdrop")){ closePalette(); return; }
    var item = e.target.closest("[data-action-index]");
    if (item){
      paletteIndex = parseInt(item.getAttribute("data-action-index"),10)||0;
      runPaletteAction();
    }
  });
}
function bindApprovalEvents(){
  var root = document.getElementById("approvalRoot");
  root.addEventListener("click",function(e){
    var opt = e.target.closest ? e.target.closest("[data-ask-opt]") : null;
    if (opt){
      currentAskLabel = opt.getAttribute("data-ask-opt")||"";
      $$(".ask-opt",root).forEach(function(b){
        var on = b === opt;
        b.classList.toggle("selected", on);
        b.setAttribute("aria-pressed", on ? "true" : "false");
      });
      return;
    }
    var btn = e.target.closest("[data-decision]");
    if (!btn || !S || !S.approval) return;
    var decision = btn.getAttribute("data-decision");
    var answer = "";
    if (S.approval.name === "ask_user" && decision === "allow"){
      var inp = document.getElementById("askOther");
      var typed = inp && inp.value ? inp.value.trim() : "";
      answer = typed || currentAskLabel;
      if (!answer){ toast("先选一个选项，或直接输入你的回答","warn"); return; }
    }
    resolveApproval(S.approval.id,decision,answer);
  });
}
function bindInspectorEvents(){
  var body = document.getElementById("inspectorBody");
  if (!body) return;
  body.addEventListener("input",function(e){
    if (e.target && e.target.id === "wbSessionSearch"){ wbSessionQuery = e.target.value; renderWbSessionList(); }
  });
  body.addEventListener("keydown",function(e){
    if (e.target && e.target.id === "wbSessionSearch" && e.key === "Escape"){
      e.preventDefault(); e.stopPropagation();
      e.target.value = ""; wbSessionQuery = ""; renderWbSessionList();
    }
  });
  body.addEventListener("click",function(e){
    var row = e.target.closest ? e.target.closest("[data-wb-session]") : null;
    if (row){ selectSession(row.getAttribute("data-wb-session")); return; }
    var review = e.target.closest("[data-review-tool]");
    if (review){
      var toolId = review.getAttribute("data-review-tool")||"";
      var cards = document.querySelectorAll(".tool[data-tool-id]");
      var card = null;
      for (var ci=0;ci<cards.length;ci++) if (cards[ci].getAttribute("data-tool-id")===toolId){ card=cards[ci]; break; }
      if (card){
        var head = card.querySelector("[data-tool-toggle]");
        var toolBody = card.querySelector(".tool-body");
        if (toolBody){ card.classList.add("open"); toolBody.hidden=false; }
        if (head) head.setAttribute("aria-expanded","true");
        expandedTools.add(toolId); collapsedTools.delete(toolId);
      }
      toggleLogs(false);
      if (card) setTimeout(function(){
        card.scrollIntoView({behavior:"smooth",block:"center"});
        card.classList.add("review-target");
        setTimeout(function(){ card.classList.remove("review-target"); },1500);
      },window.innerWidth<=1180?220:40);
      else toast("对应的工具记录已不在当前消息中","warn");
      return;
    }
    var filter = e.target.closest("[data-log-filter]");
    if (filter){ logFilter = filter.getAttribute("data-log-filter"); renderInspector(); return; }
    if (e.target.closest("[data-resume-task]")){ resumeTask(); return; }
    var parkedBtn = e.target.closest("[data-resume-parked]");
    if (parkedBtn){ resumeTask(parkedBtn.getAttribute("data-task-id")||""); return; }
    if (e.target.closest("[data-restart-browser]")){ restartBrowser(); return; }
    if (e.target.closest("[data-copy-logs]")){
      var logs = filteredLogs();
      var text = logs.map(function(l){ return fmtLogTime(l.time)+" ["+l.level+"] "+l.text; }).join("\n");
      copyText(text).then(function(){ toast("日志已复制","ok",1800); });
    }
  });
}


/* 主流对齐：模型最近使用、侧栏分区折叠（只用 localStorage 两个键，不新增状态机） */
var RECENT_MODELS_KEY = "gt.recentModels";
var GROUP_KEY = "gt.collapsedGroups";
function readList(key){ try{ var v = JSON.parse(localStorage.getItem(key)||"[]"); return Array.isArray(v)?v:[]; }catch(e){ return []; } }
function writeList(key,list){ try{ localStorage.setItem(key, JSON.stringify(list)); }catch(e){} }
function recordModelUse(name){
  if (!name) return;
  var list = readList(RECENT_MODELS_KEY).filter(function(x){ return x !== name; });
  list.unshift(name); writeList(RECENT_MODELS_KEY, list.slice(0,3));
}
function sessionGroupHtml(label,items,pickMode){
  // 批量选择时强制展开：否则「全选」只作用于看得见的几行，容易误判
  var collapsed = !pickMode && readList(GROUP_KEY).indexOf(label) >= 0;
  var cls = collapsed ? "session-group collapsible collapsed" : "session-group collapsible";
  var exp = collapsed ? "false" : "true";
  return '<button type="button" class="'+cls+'" data-group="'+attr(label)+'" aria-expanded="'+exp+'">'
    +esc(label)+'<span class="group-chev">'+icon("chevronDown")+'</span></button>'
    +(collapsed ? "" : items.map(function(s){ return sessionItemHtml(s, pickMode, !!selectedSessions[s.id]); }).join(""));
}
function toggleGroup(name){
  var list = readList(GROUP_KEY), i = list.indexOf(name);
  if (i>=0) list.splice(i,1); else list.push(name);
  writeList(GROUP_KEY, list); lastSidebarHtml = ""; renderSidebar();
}
/* ===== 外部工具（MCP）：本机发现 / 一键接入 / 启停 / 手动添加 ===== */
function mcpPost(patch,done){
  return api("/api/mcp/servers",patch).then(function(list){
    renderMcpList(list);
    if (done) done(list);
  }).catch(function(e){ toast("外部工具操作失败："+e.message,"error"); });
}
function renderMcpList(list){
  var box = document.getElementById("mcpList");
  if (!box) return;
  if (!list || !list.length){
    box.innerHTML = '<div class="field-help">还没有外部工具。点「扫描本机已配置」把 REA 等工具接进来。</div>';
    return;
  }
  box.innerHTML = list.map(function(s,i){
    var state = !s.enabled ? '<span class="mcp-state off">已停用</span>'
      : s.ready ? '<span class="mcp-state ok">已连接 · '+Number(s.toolCount)+' 个工具</span>'+(s.changed ? ' <span class="mcp-state warn">清单已变化，请复核</span>' : '')
      : '<span class="mcp-state wait">连接中…</span>';
    var err = s.error ? '<div class="mcp-err">'+esc(s.error)+'</div>' : "";
    /* 防投毒：工具描述全文可见——模型看到的就是这些文本，用户要能逐条核对 */
    var tools = (s.ready && s.tools && s.tools.length)
      ? '<details class="mcp-tools"><summary>查看 '+s.tools.length+' 个工具的完整描述</summary>'
        + s.tools.map(function(t){
            return '<div class="mcp-tool"><code>'+esc(t.name)+'</code><span>'+esc(t.description||"（无描述）")+'</span></div>';
          }).join("")
        + '</details>'
      : "";
    return '<div class="mcp-row"><div class="mcp-main">'
      +'<div class="mcp-name">'+esc(s.name)+' '+state+'</div>'
      +'<div class="mcp-cmd">'+esc(s.source||"自定义")+' · '+esc(s.command||"")+'</div>'+err+tools+'</div>'
      +'<div class="mcp-actions">'
        +'<button type="button" class="btn mini" data-mcp-toggle="'+i+'">'+(s.enabled?"停用":"启用")+'</button>'
        +'<button type="button" class="btn mini" data-mcp-del="'+attr(s.name)+'">移除</button>'
      +'</div></div>';
  }).join("");
  var toggles = box.querySelectorAll("[data-mcp-toggle]");
  for (var i=0;i<toggles.length;i++){
    toggles[i].addEventListener("click",function(){
      var s = list[Number(this.getAttribute("data-mcp-toggle"))];
      mcpPost({name:s.name,enabled:!s.enabled});
    });
  }
  var dels = box.querySelectorAll("[data-mcp-del]");
  for (var j=0;j<dels.length;j++){
    dels[j].addEventListener("click",function(){
      var name = this.getAttribute("data-mcp-del");
      askConfirm("移除外部工具","确定移除「"+name+"」吗？只影响绿虎的配置，不会动其它客户端。",{confirmLabel:"移除",danger:true},function(){
        mcpPost({name:name,remove:true});
      });
    });
  }
}
function renderMcpServers(){
  var d = document.getElementById("mcpDiscoverBtn");
  if (d) d.onclick = mcpDiscover;
  var a = document.getElementById("mcpAddBtn");
  if (a) a.onclick = mcpAddForm;
  api("/api/mcp/servers").then(renderMcpList).catch(function(e){
    var box=document.getElementById("mcpList");
    if (box) box.innerHTML='<div class="field-help">加载失败：'+esc(e.message)+'</div>';
  });
}
function mcpDiscover(){
  var box = document.getElementById("mcpDiscoverBox");
  if (!box) return;
  box.innerHTML = '<div class="field-help">正在扫描 WorkBuddy / Qoder / Claude / VS Code 的配置…</div>';
  api("/api/mcp/discover").then(function(list){
    if (!list || !list.length){ box.innerHTML='<div class="field-help">本机没有找到已配置的 MCP 服务器，可用「手动添加」。</div>'; return; }
    box.innerHTML = '<div class="mcp-discover"><div class="mcp-discover-head">本机已配置</div>'
      + list.map(function(s,i){
          return '<div class="mcp-row"><div class="mcp-main"><div class="mcp-name">'+esc(s.name)+' <span class="mcp-state">'+esc(s.source||"")+'</span></div>'
            +'<div class="mcp-cmd">'+esc(s.command||"")+'</div></div>'
            +'<button type="button" class="btn mini" data-mcp-import="'+i+'"'+(s.alreadyAdded?' disabled':'')+'>'+(s.alreadyAdded?'已接入':'接入')+'</button></div>';
        }).join("") + '</div>';
    var imps = box.querySelectorAll("[data-mcp-import]");
    for (var i=0;i<imps.length;i++){
      imps[i].addEventListener("click",function(){
        var s = list[Number(this.getAttribute("data-mcp-import"))];
        mcpPost({name:s.name,command:s.command,args:s.args||[],env:s.env||{},enabled:true},function(){
          toast("已接入 "+s.name,"ok",2200);
        });
      });
    }
  }).catch(function(e){ box.innerHTML='<div class="field-help">扫描失败：'+esc(e.message)+'</div>'; });
}
function mcpSplitCmd(line){
  var out=[],m,re=/"([^"]+)"|(\S+)/g;
  while((m=re.exec(line))!==null) out.push(m[1]||m[2]);
  return out;
}
function mcpAddForm(){
  var box = document.getElementById("mcpDiscoverBox");
  if (!box) return;
  box.innerHTML = '<div class="mcp-addform">'
    +'<div class="field"><label for="mcpNewName">名称</label><input class="input" id="mcpNewName" placeholder="例如 rea"></div>'
    +'<div class="field"><label for="mcpNewCmd">完整启动命令</label><input class="input" id="mcpNewCmd" placeholder="node path/to/server.mjs mcp"></div>'
    +'<div class="field-help">把其它客户端 mcpServers 里的 command 与 args 拼成一行粘进来；含空格的路径用英文双引号包住。</div>'
    +'<div class="settings-actions"><button type="button" class="btn primary" id="mcpNewSave">添加并连接</button><button type="button" class="btn" id="mcpNewCancel">取消</button></div></div>';
  var cancel=document.getElementById("mcpNewCancel");
  if (cancel) cancel.onclick=function(){ box.innerHTML=""; };
  var save=document.getElementById("mcpNewSave");
  if (save) save.onclick=function(){
    var name=(document.getElementById("mcpNewName").value||"").trim();
    var parts=mcpSplitCmd(document.getElementById("mcpNewCmd").value||"");
    if (!name || parts.length<1){ toast("请填写名称和启动命令","warn"); return; }
    mcpPost({name:name,command:parts[0],args:parts.slice(1),env:{},enabled:true},function(){
      box.innerHTML=""; toast("已添加外部工具 "+name,"ok",2200);
    });
  };
}
var permitRulesDraft = [];
function renderPermitRules(){
  var box = document.getElementById("permitList");
  if (!box) return;
  if (!permitRulesDraft.length){
    box.innerHTML = '<div class="field-help">还没有规则；留空时按输入框旁的「权限模式」逐项确认。</div>';
  } else {
    box.innerHTML = permitRulesDraft.map(function(r,i){
      return '<div class="mcp-row"><div class="mcp-main">'
        +'<div class="mcp-name">'+esc(r.match)+' <span class="mcp-state '+(r.action==="deny"?"off":"ok")+'">'+(r.action==="deny"?"拒绝":"允许")+'</span></div>'
        +'<div class="mcp-cmd">'+esc(r.name||"工具权限规则")+'</div></div>'
        +'<button type="button" class="btn mini" data-permit-del="'+i+'">删除</button></div>';
    }).join("");
  }
  var dels = box.querySelectorAll("[data-permit-del]");
  for (var i=0;i<dels.length;i++){
    dels[i].addEventListener("click",function(){
      permitRulesDraft.splice(Number(this.getAttribute("data-permit-del")),1);
      renderPermitRules();
    });
  }
  var add = document.getElementById("permitAddBtn");
  if (add) add.onclick = function(){
    var el = document.getElementById("permitMatch");
    var m = el ? el.value.trim() : "";
    if (!m){ toast("请填写匹配串，例如 shell:Get-ChildItem*","warn"); return; }
    var a = document.getElementById("permitAction");
    permitRulesDraft.push({id:"",name:"",match:m,action:a?a.value:"allow"});
    if (el) el.value = "";
    renderPermitRules();
  };
}
/* ---------- global keys ---------- */
function bindGlobalKeys(){
  document.addEventListener("keydown",function(e){
    var key = (e.key||"").toLowerCase();
    var mod = e.ctrlKey || e.metaKey;
    if (e.key === "Tab" && !mod && !e.altKey){
      var modal = activeModal();
      if (modal){ trapTab(e, modal); return; }
    }
    if (mod && key === "k"){
      e.preventDefault();
      if (document.getElementById("paletteRoot").firstElementChild) closePalette();
      else openPalette();
      return;
    }
    if (mod && key === "/"){
      e.preventDefault();
      if (document.getElementById("dialogRoot").firstElementChild) closeDialog();
      else openShortcuts();
      return;
    }
    if (S && S.approval){
      // ask_user 选项卡：焦点不在输入框时按 Esc = 跳过（与「跳过」按钮同义）
      if (S.approval.name === "ask_user" && e.key === "Escape" &&
          (!e.target || (e.target.tagName !== "INPUT" && e.target.tagName !== "TEXTAREA"))){
        e.preventDefault();
        resolveApproval(S.approval.id,"deny","");
      }
      return;
    }
    if (mod && key === "n"){ e.preventDefault(); newSession(); return; }
    if (mod && e.key === ","){ e.preventDefault(); openSettings(); return; }
    if (mod && e.shiftKey && key === "l"){ e.preventDefault(); toggleLogs(); return; }
    if (mod && e.shiftKey && key === "o"){ e.preventDefault(); openWorkdir(); return; }
    if (mod && key === "b"){ e.preventDefault(); toggleSidebar(); return; }
    if (mod && key === "enter"){
      var t = e.target;
      if (t && (t.tagName === "TEXTAREA" || t.tagName === "INPUT")){ e.preventDefault(); if (!(S && S.status && S.status.running)) handleSend(); }
      return;
    }
    if (e.key === "Escape"){
      var paramsPanel = document.getElementById("cmpParams");
      if (paramsPanel && !paramsPanel.hidden){
        e.preventDefault(); paramsPanel.hidden = true;
        var pbtn = document.getElementById("paramsBtn");
        if (pbtn){ pbtn.setAttribute("aria-expanded","false"); pbtn.focus(); }
        return;
      }
      var modelPop = document.getElementById("cmpPop");
      if (modelPop && !modelPop.hidden && typeof window.closeModelPop === "function"){
        e.preventDefault();
        window.closeModelPop();
        var back = document.getElementById("modelBtn");
        if (back) back.focus();
        return;
      }
      if (document.getElementById("paletteRoot").firstElementChild){ closePalette(); return; }
      if (document.getElementById("dialogRoot").firstElementChild){ closeDialog(); return; }
      if (S && S.status && S.status.running){ e.preventDefault(); stopTask(); return; }
      // 批量选择是模态状态：Esc 先退出它，再考虑收侧栏 / 收工作台
      if (selectMode){ e.preventDefault(); exitSelectMode(); lastSidebarHtml = ""; renderSidebar(); return; }
      if (logsOpen && window.innerWidth <= 1180){ toggleLogs(false); return; }
      if (!sidebarCollapsed() && window.innerWidth <= 900){ collapseSidebar(); return; }
    }
  });
  document.addEventListener("visibilitychange",function(){
    if (!document.hidden) updateDocTitle();
  });
}

/* ---------- dock 高度同步 ---------- */
/* 输入框变高（多行、附件、编辑横幅）时同步 --dock-h，供「滚动到底部」按钮定位。 */
function syncDockHeight(){
  var dock = document.querySelector(".dock");
  if (!dock) return;
  var h = dock.offsetHeight || 0;
  if (h > 0) document.documentElement.style.setProperty("--dock-h", h + "px");
}
if (typeof ResizeObserver !== "undefined"){
  window.addEventListener("load",function(){
    var dock = document.querySelector(".dock");
    if (!dock) return;
    new ResizeObserver(function(){ syncDockHeight(); }).observe(dock);
  });
}

/* ---------- clock ---------- */
function tick(){
  var clock = document.getElementById("runClock");
  var running = !!(S && S.status && S.status.running && runStart);
  var elapsed = running ? "· " + fmtClock((Date.now()-runStart)/1000) : "";
  if (clock) clock.textContent = running ? fmtClock((Date.now()-runStart)/1000) : "";
  // 消息里的「生成中 · 12s」不重建 DOM，只改文本，避免整段对话重绘
  var marks = document.querySelectorAll("[data-elapsed]");
  for (var i=0;i<marks.length;i++) marks[i].textContent = elapsed;
}

/* ---------- init ---------- */
/* 浏览器外壳主题同步 */
document.addEventListener("greentiger:theme", function(e){
 var t = e && e.detail && e.detail.theme;
 if (t !== "dark" && t !== "light") return;
 settingsTheme = t;
 var btn = document.getElementById("themeBtn");
 if (btn){
  var target = t === "dark" ? "light" : "dark";
  var icoEl = document.getElementById("themeIco");
  if (icoEl) icoEl.innerHTML = icon(target === "light" ? "sun" : "moon");
  else btn.innerHTML = icon(target === "light" ? "sun" : "moon");
  btn.title = "切换到" + (target === "light" ? "浅色" : "深色") + "主题";
  btn.setAttribute("aria-label",btn.title);
  btn.setAttribute("aria-pressed",t === "dark" ? "true" : "false");
 }
});

function init(){
 /* 启动时把服务端配置主题同步进统一键 */
 try {
 var bootTheme = (S && S.config && S.config.theme) === "dark" ? "dark" : "light";
 if (window.GreenTigerShell && typeof window.GreenTigerShell.setTheme === "function"){
 window.GreenTigerShell.setTheme(bootTheme);
 } else { applyThemeSafe(bootTheme); }
 } catch(e){}
  // 窄屏默认收起侧栏（覆盖式抽屉），宽屏保持常驻左栏
  if (window.innerWidth <= 900) document.body.classList.add("sidebar-collapsed");
  hydrateIcons();
  renderSidebarSkeleton();
  initMotionPolish();
  bindEvents();
  bindDialogEvents();
  bindPaletteEvents();
  bindApprovalEvents();
  bindInspectorEvents();
  bindGlobalKeys();
  bindHarnessEvents();
  restoreDraft();
  loadInputHistory();
  resizeInput();
  connectSSE();
  refreshState();
  setInterval(tick,1000);
  setInterval(function(){ if (S) renderSidebar(); },60000);
  setTimeout(function(){
    if (window.innerWidth > 900){
      var input = document.getElementById("input");
      if (input && !input.value) input.focus();
    }
  },450);
}

/* ---------- Harness 适配：模式 / Provider / 工作区 / 定时任务 / 官方插件 ---------- */
var providersCatalog = [];
function renderModeSelect(){
  var sel = document.getElementById("modeSelect");
  if (!sel || !S || !S.config) return;
  var mode = S.config.autoRun ? "auto" : ((S.config.mode === "plan" || S.config.mode === "acceptEdits") ? S.config.mode : "ask");
  if (sel.value !== mode) sel.value = mode;
  sel.style.color = "";
}
function trySlashCommand(cmd){
  var c = (cmd||"").trim();
  if (c === "/new"){ newSession(); return true; }
  if (c === "/stop"){ stopTask(); return true; }
  if (c === "/pause"){ pauseTask(); return true; }
  if (c === "/retry"){ retryMsg(); return true; }
  if (c === "/settings"){ openSettings(); return true; }
  if (c === "/files"){ openWorkdir(); return true; }
  if (c === "/plan"){ toast("多步任务开始前，绿虎会自动输出 plan 计划并等待你批准；批准后按步骤执行并用 todo 更新进度。","info",5000); return true; }
  if (c === "/rules"){ showRules(); return true; }
  if (c === "/export"){ exportSession(); return true; }
  if (c === "/memory"){ showRules(); return true; }
  if (c === "/help" || c === "/commands"){ openPalette(); return true; }
  if (c.indexOf("/mode:") === 0){
    var m = c.substring(6).trim();
    if (m === "plan" || m === "normal" || m === "ask" || m === "acceptEdits" || m === "auto"){
      setMode(m);
      return true;
    }
  }
  // 手敲完整的示例命令时，与菜单点选保持一致：换成任务文案，不误报「未知命令」
  for (var si=0; si<SLASH_COMMANDS.length; si++){
    var sc = SLASH_COMMANDS[si];
    if (sc.prompt && sc.cmd === c){
      var ip = document.getElementById("input");
      if (ip){ ip.value = sc.prompt; resizeInput(); updateSendState(); saveDraft(); ip.focus(); }
      return true;
    }
  }
  return false;
}
/* 文件级记忆规则（对齐 Claude Code 的 CLAUDE.md）：显示项目/用户规则加载状态。 */
async function showRules(){
  try{
    var res = await api("/api/rules");
    if (!res) return;
    var lines = [];
    var projectFiles = Array.isArray(res.projectFiles) ? res.projectFiles : (res.project ? [res.project] : []);
    var pathRules = Array.isArray(res.pathRules) ? res.pathRules : [];
    if (projectFiles.length){
      lines.push("项目规则：已加载 "+projectFiles.length+" 个，按仓库根目录到工作目录叠加");
      projectFiles.slice(0,6).forEach(function(f){ lines.push("· "+(f.firstLine||"")+"（"+f.path+" · "+fmtNum(f.chars)+" 字符）"); });
      if (projectFiles.length>6) lines.push("· 另有 "+(projectFiles.length-6)+" 个规则文件");
    } else lines.push("项目规则：未加载（工作区内无项目指令文件）");
    if (pathRules.length){
      lines.push("路径规则：已加载 "+pathRules.length+" 个 .claude/rules 文件");
      pathRules.slice(0,6).forEach(function(f){
        var paths = Array.isArray(f.paths) && f.paths.length ? f.paths.join(", ") : "所有工作区路径";
        lines.push("· "+f.path+"（相对 "+(f.baseDir||"工作区")+"）→ "+paths);
      });
      if (pathRules.length>6) lines.push("· 另有 "+(pathRules.length-6)+" 个路径规则文件");
    } else lines.push("路径规则：无（可在 .claude/rules/ 下添加 Markdown 规则）");
    if (res.user) lines.push("用户规则："+(res.user.firstLine||"")+"（"+res.user.path+" · "+fmtNum(res.user.chars)+" 字符）");
    else lines.push("用户规则：无（数据目录 rules.md，可新建）");
    lines.push("规则文件会在每次任务开始时自动注入系统提示。");
    toast(lines.join("\n"),"info",8000);
  }catch(e){ toast("读取规则失败："+e.message,"error"); }
}
async function setMode(mode){
  if (S && S.status && S.status.running){ toast("任务运行中，暂不能切换模式","warn"); return; }
  if (!S) return;
  var selected = mode || "ask";
  var configMode = selected === "plan" ? "plan" : (selected === "acceptEdits" ? "acceptEdits" : "normal");
  var autoRun = selected === "auto";
  var apply = async function(){
    try{
      var next = await api("/api/config",{mode:configMode,autoRun:autoRun});
      if (next.version) applyState(next);
      toast("已切换到「" + modeNameZh(selected) + "」","ok",3000);
    }catch(e){ toast("切换模式失败："+e.message,"error"); renderModeSelect(); }
  };
  if (autoRun){
    askConfirm("启用自动执行","自动执行普通工具；高危操作仍会被拦截或要求确认。确认前请检查当前工作目录。",{confirmLabel:"启用自动执行"},apply);
    renderModeSelect();
    return;
  }
  try{
    var next = await api("/api/config",{mode:configMode,autoRun:false});
    if (next.version) applyState(next);
    toast("已切换到「" + modeNameZh(selected) + "」","ok",3000);
  }catch(e){ toast("切换模式失败："+e.message,"error"); }
}
function renderHarnessSettings(){
  var c = (S && S.config) || {};
  var strategy = document.getElementById("modelStrategyInfo");
  if (strategy){
    var bi = brainInfo();
    strategy.textContent = bi.active === "provider"
      ? ("当前：" + (bi.short || "模型") + " · " + (bi.detail || "") + "。换模型点输入框上方的模型胶囊。")
      : "还没有可用模型：下面选主流服务商填入 API Key，或填任意 OpenAI 兼容端点。";
  }
  // Provider 下拉
  var sel = document.getElementById("setProvider");
  if (sel){
    api("/api/providers",{}).then(function(data){
      if (!data || !data.providers) return;
      providersCatalog = data.providers || [];
      sel.innerHTML = '<option value="">— 选择服务商 —</option>';
      for (var i=0;i<providersCatalog.length;i++){
        var p = providersCatalog[i];
        var opt = document.createElement("option");
        opt.value = p.id;
        opt.textContent = p.name + (p.free ? " · 免费额度" : "");
        sel.appendChild(opt);
      }
      if (c.providerId){ sel.value = c.providerId; }
      renderProviderDetail();
      var keyEl = document.getElementById("setProviderKey");
      if (keyEl && c.providerKey) keyEl.value = c.providerKey;
      var mSel = document.getElementById("setProviderModel");
      if (mSel && c.providerModel) mSel.value = c.providerModel;
    }).catch(function(){});
  }
  renderWorkspaceList();
  renderTaskList();
}
function renderProviderDetail(){
  var c = (S && S.config) || {};
  var sel = document.getElementById("setProvider");
  var help = document.getElementById("providerHelp");
  var mSel = document.getElementById("setProviderModel");
  var keyEl = document.getElementById("setProviderKey");
  if (!sel) return;
  var p = null;
  for (var i=0;i<providersCatalog.length;i++) if (providersCatalog[i].id === sel.value) p = providersCatalog[i];
  if (help){
    help.textContent = p
      ? p.help + (p.quota ? " · 免费额度：" + p.quota : "")
      : "选择服务商并填写 API Key，然后保存设置。";
  }
  var keyBtn = document.getElementById("providerKeyBtn");
  if (keyBtn){
    if (p && p.keyUrl){
      keyBtn.hidden = false;
      keyBtn.addEventListener("click", function(){ openExternal(p.keyUrl); });
    }
    else keyBtn.hidden = true;
  }
  // 端点跟着服务商走：选中目录里的服务商就填它的端点；「自定义」保留用户已填的值
  var epEl = document.getElementById("setProviderEndpoint");
  if (epEl && p && p.endpoint) epEl.value = p.endpoint;
  // 模型改成可输入 + 候选列表（datalist）：目录里的推荐模型和探测来的模型都进候选
  fillModelDatalist(p ? (p.models||[]) : []);
}
function fillModelDatalist(models, keepTyped){
  var list = document.getElementById("providerModelList");
  if (!list) return;
  var c = (S && S.config) || {};
  var seen = {};
  var html = "";
  function push(v){
    v = (v||"").trim();
    if (!v || seen[v.toLowerCase()]) return;
    seen[v.toLowerCase()] = 1;
    html += '<option value="'+attr(v)+'"></option>';
  }
  for (var i=0;i<(models||[]).length;i++) push(models[i]);
  if (keepTyped !== false) push(c.providerModel);
  list.innerHTML = html;
}
function openExternal(url, inApp){
  if (!url) return;
  api("/api/open-url",{url:url, inApp:!!inApp}).then(function(res){
    if (res && res.ok === false) toast(res.error||"打开失败","error");
    else if (inApp) toast("已在绿虎浏览器里打开（真渲染）","info",2200);
  }).catch(function(e){ toast("打开失败："+e.message,"error"); });
}
function renderWorkspaceList(){
  var list = document.getElementById("workspaceList");
  if (!list) return;
  var ws = (S && S.config && S.config.workspaces && S.config.workspaces.list) || [];
  if (!ws.length){
    list.innerHTML = '<div class="field-help">还没有命名工作区。工作目录即默认工作区。</div>';
    return;
  }
  var html = "";
  for (var i=0;i<ws.length;i++){
    var w = ws[i];
    html += '<div class="ws-item' + (w.active ? " active" : "") + '">'
      +'<div class="ws-main"><div class="ws-name">' + esc(w.name||"未命名") + '</div>'
      +'<div class="ws-path">' + esc(w.path||"") + '</div></div>'
      +'<div class="ws-actions">'
      + (w.active ? '<span class="ws-active-tag">当前</span>' : '<button type="button" class="btn mini" data-ws-switch="'+attr(w.id)+'">切换</button>')
      +'<button type="button" class="btn mini danger" data-ws-del="'+attr(w.id)+'">删除</button>'
      +'</div></div>';
  }
  list.innerHTML = html;
  list.querySelectorAll("[data-ws-switch]").forEach(function(b){
    b.addEventListener("click", function(){ wsAction("switch", this.getAttribute("data-ws-switch"), ""); });
  });
  list.querySelectorAll("[data-ws-del]").forEach(function(b){
    b.addEventListener("click", function(){ wsAction("delete", this.getAttribute("data-ws-del"), ""); });
  });
}
function wsAction(action, id, extraPath){
  var payload = {action:action, id:id};
  if (action === "add"){
    var name = document.getElementById("wsName");
    var path = document.getElementById("wsPickBtn");
    payload.name = name ? name.value.trim() : "";
    payload.path = extraPath || "";
  }
  api("/api/workspaces", payload).then(function(data){
    if (data && data.message) toast(data.message, "ok", 3200);
    // 添加成功后复位选择按钮，避免连点「添加项目」把同一路径重复加进去
    if (action === "add"){
      var pathBtn = document.getElementById("wsPickBtn");
      if (pathBtn){ pathBtn.textContent = "选择文件夹…"; delete pathBtn.dataset.path; }
      var nameInput = document.getElementById("wsName");
      if (nameInput) nameInput.value = "";
    }
    refreshState();
  }).catch(function(e){
    toast("操作失败："+e.message,"error");
  });
}
function renderTaskList(){
  var list = document.getElementById("taskList");
  if (!list) return;
  var tasks = (S && S.config && S.config.tasks) || [];
  if (!tasks.length){
    list.innerHTML = '<div class="field-help">还没有定时任务。</div>';
    return;
  }
  var html = "";
  for (var i=0;i<tasks.length;i++){
    var t = tasks[i];
    html += '<div class="task-item">'
      +'<div class="ws-main"><div class="ws-name">' + esc(t.name||"未命名") + (t.enabled ? "" : "（已暂停）") + '</div>'
      +'<div class="ws-path">' + esc(t.schedule||"") + ' · ' + esc((t.prompt||"").slice(0,60)) + '</div></div>'
      +'<div class="ws-actions">'
      +'<button type="button" class="btn mini" data-task-toggle="'+attr(t.id)+'">'+(t.enabled?"暂停":"启用")+'</button>'
      +'<button type="button" class="btn mini danger" data-task-del="'+attr(t.id)+'">删除</button>'
      +'</div></div>';
  }
  list.innerHTML = html;
  list.querySelectorAll("[data-task-toggle]").forEach(function(b){
    b.addEventListener("click", function(){
      var id = this.getAttribute("data-task-toggle");
      var t = null;
      for (var i=0;i<tasks.length;i++) if (tasks[i].id === id) t = tasks[i];
      api("/api/tasks", {action:"toggle", id:id, enabled: t ? !t.enabled : true}).then(function(data){
        if (data && data.message) toast(data.message, "ok", 2600);
        refreshState();
      }).catch(function(e){ toast("操作失败："+e.message,"error"); });
    });
  });
  list.querySelectorAll("[data-task-del]").forEach(function(b){
    b.addEventListener("click", function(){
      api("/api/tasks", {action:"delete", id: this.getAttribute("data-task-del")}).then(function(data){
        if (data && data.message) toast(data.message, "ok", 2600);
        refreshState();
      }).catch(function(e){ toast("操作失败："+e.message,"error"); });
    });
  });
}
function bindHarnessEvents(){
  document.addEventListener("change", function(e){
    if (e.target && e.target.id === "setProvider") renderProviderDetail();
    if (e.target && e.target.id === "modeSelect") setMode(e.target.value);
  });
  document.addEventListener("click", function(e){
    var key = e.target.closest("[data-provider-key]");
    if (key){ openExternal(key.getAttribute("data-provider-key")); return; }
    if (e.target.closest("#testProviderBtn")){ testProvider(); return; }
    if (e.target.closest("#clearProviderBtn")){ clearProvider(); return; }
    if (e.target.closest("#wsPickBtn")){
      // 用系统目录对话框代替 window.prompt：后者在 WebView2 里可能被拦截，也不校验路径
      var pathBtn = document.getElementById("wsPickBtn");
      var oldLabel = pathBtn ? pathBtn.textContent : "";
      if (pathBtn) pathBtn.disabled = true;
      api("/api/pick-folder",{}).then(function(res){
        var picked = (res && res.path) ? res.path.trim() : "";
        if (!picked) return;
        if (pathBtn){ pathBtn.dataset.path = picked; pathBtn.textContent = shortPath(picked,24); }
      }).catch(function(err){
        toast("选择文件夹失败："+err.message,"error");
      }).then(function(){
        if (pathBtn){ pathBtn.disabled = false; pathBtn.textContent = pathBtn.dataset.path ? shortPath(pathBtn.dataset.path,24) : oldLabel; }
      });
      return;
    }
    if (e.target.closest("#wsAddBtn")){
      var name = (document.getElementById("wsName") || {}).value || "";
      if (!name.trim()){ toast("请填写工作区名称","warn"); return; }
      var pathBtn2 = document.getElementById("wsPickBtn");
      var wsPath = (pathBtn2 && pathBtn2.dataset.path) || "";
      if (!wsPath){ toast("请先选择项目文件夹","warn"); return; }
      wsAction("add", "", wsPath);
      return;
    }
    if (e.target.closest("#taskAddBtn")){
      var taskName = (document.getElementById("taskName") || {}).value || "";
      var schedule = (document.getElementById("taskSchedule") || {}).value || "09:00";
      var taskPrompt = (document.getElementById("taskPrompt") || {}).value || "";
      if (!taskPrompt){ toast("请填写定时任务的指令","warn"); return; }
      api("/api/tasks", {action:"add", name:taskName, schedule:schedule, prompt:taskPrompt, enabled:true}).then(function(data){
        if (data && data.message) toast(data.message,"ok",3200);
        refreshState();
      }).catch(function(err){ toast("添加失败："+err.message,"error"); });
      return;
    }
  });
}
function testProvider(){
  var sel = document.getElementById("setProvider");
  var keyEl = document.getElementById("setProviderKey");
  var epEl = document.getElementById("setProviderEndpoint");
  var mEl = document.getElementById("setProviderModel");
  var res = document.getElementById("providerResult");
  if (!res) return;
  var endpoint = epEl ? epEl.value.trim() : "";
  if (!endpoint){ toast("请先填端点","warn"); return; }
  res.textContent = "正在测试连接…";
  api("/api/providers/test", {id: sel ? sel.value : "", key: keyEl ? keyEl.value : "", endpoint: endpoint}).then(function(data){
    if (data && data.ok && data.models && data.models.length){
      res.textContent = "连接成功，可用 " + data.models.length + " 个模型（已放进模型候选）";
      fillModelDatalist(data.models, true);
      // 模型留空时替用户选第一个，省掉「填了 Key 还是不能聊」这一步
      if (mEl && !mEl.value.trim()){
        var pick = data.models[0];
        for (var i=0;i<data.models.length;i++){
          if (/chat|instruct|flash|pro/i.test(data.models[i])) { pick = data.models[i]; break; }
        }
        mEl.value = pick;
      }
      toast("连接成功：可用 " + data.models.length + " 个模型","ok",3200);
    } else {
      res.textContent = "连接失败：" + ((data && data.error) || "未知错误");
      toast("连接失败","error");
    }
  }).catch(function(e){ res.textContent = "连接失败：" + e.message; });
}
function clearProvider(){
  api("/api/config", {providerId:"", providerKey:"", providerEndpoint:"", providerModel:""}).then(function(data){
    if (data.version) applyState(data);
    var sel = document.getElementById("setProvider");
    if (sel) sel.value = "";
    var keyEl = document.getElementById("setProviderKey");
    if (keyEl) keyEl.value = "";
    var epEl2 = document.getElementById("setProviderEndpoint");
    if (epEl2) epEl2.value = "";
    var mEl2 = document.getElementById("setProviderModel");
    if (mEl2) mEl2.value = "";
    renderProviderDetail();
    toast("已清除模型配置。接入任意服务商后即可继续对话。","ok",4200);
  }).catch(function(e){ toast("操作失败："+e.message,"error"); });
}

/* watch mode 心跳：真实界面活动时节流打点（45s），供服务端判断「人还在」。 */
(function(){
  var lastBeat = 0;
  function beat(){
    var now = Date.now();
    if (now - lastBeat < 45000) return;
    lastBeat = now;
    try { api("/api/ping",{}); } catch(e){}
  }
  var evs = ["click","keydown","mousemove","wheel","touchstart"];
  for (var i=0;i<evs.length;i++){ document.addEventListener(evs[i], beat, {passive:true}); }
})();

init();
})();

/* 工具箱：面向普通用户的图形化本机操作台，独立模块（ui/js/toolbox.js），主入口只负责挂载。 */
initToolbox();

