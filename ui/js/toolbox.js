/* 绿虎工具箱：命令舞台式图形操作台（面向普通用户，不聊天也能用）。
 * 视觉：玻璃舞台 + aura 辉光 + 渐变图标砖（style.css .tb-* 皮肤，全走 theme 令牌）。
 * 契约：全部走 /api/toolbox/* 结构化 JSON（WebServer.HandleToolbox → LocalOps）；
 * 危险操作（卸载 / 粉碎 / 清理）先弹二次确认层，用户按下确认后才带 confirm:true 提交；
 * reveal（资源管理器定位）无破坏性，直接调用。
 * 独立实现 fetch 而不复用 app.js 的 api()：后者固定 20s 超时，撑不住全盘扫描与粉碎。
 * 命名约束：顶层名一律 tb 前缀或明确不与 app.js 撞（check:ui 第 7 条把非 app.js
 * 模块的顶层名视为全局名，通用词如 open/busy/el/view 会误报未导入）。 */
import { $, $$, icon, esc, fmtNum, fmtSize, hydrateIcons } from "./util.js";

var tbView = null;
var tbOpen = false;
var activeKey = null;
var activeSheet = null;
var lastFocus = null;

/* ---------- 请求 ---------- */
function tbApi(op, body, timeoutMs){
  timeoutMs = timeoutMs || 30000;
  return new Promise(function(resolve, reject){
    var ctrl = (typeof AbortController !== "undefined") ? new AbortController() : null;
    var timer = ctrl ? setTimeout(function(){ try { ctrl.abort(); } catch(e){} }, timeoutMs) : null;
    fetch("/api/toolbox/" + op, {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body || {}),
      signal: ctrl ? ctrl.signal : undefined
    }).then(function(res){
      if (timer) clearTimeout(timer);
      var ct = res.headers.get("content-type") || "";
      if (ct.indexOf("application/json") >= 0) return res.json();
      return res.text().then(function(t){ return { ok:false, error:t }; });
    }).then(function(data){
      if (!data || data.ok !== true) throw new Error((data && data.error) || "操作失败");
      resolve(data);
    }).catch(function(e){
      if (timer) clearTimeout(timer);
      reject(new Error(e && e.name === "AbortError" ? "操作超时，请稍后重试" : (e && e.message) || "网络错误"));
    });
  });
}

/* 工具箱自己的 toast：落点与 app.js 相同（#toasts），样式类名完全复用。 */
function tbToast(message, type, timeout){
  var wrap = $("#toasts");
  if (!wrap) return;
  while (wrap.children.length >= 4) wrap.removeChild(wrap.firstChild);
  var node = document.createElement("div");
  node.className = "toast " + (type || "info");
  if (type === "error") node.setAttribute("role", "alert");
  var ico = type === "ok" ? "checkCircle" : type === "error" ? "xCircle" : type === "warn" ? "alert" : "info";
  node.innerHTML = '<span class="toast-ico">' + icon(ico) + '</span><div class="toast-text">' + esc(message) + '</div>';
  wrap.appendChild(node);
  setTimeout(function(){ if (node.parentNode) node.parentNode.removeChild(node); }, timeout || 4000);
}

/* ---------- 本地缓存：打开面板先看见上次的结果，而不是一片空白 ---------- */
var TB_CACHE_NS = "greentiger.toolbox.";
function tbCacheGet(key){
  try { var raw = localStorage.getItem(TB_CACHE_NS + key); return raw ? JSON.parse(raw) : null; } catch(e){ return null; }
}
function tbCacheSet(key, val){
  try { localStorage.setItem(TB_CACHE_NS + key, JSON.stringify(val)); } catch(e){}
}
function tbAgo(ms){
  var d = Date.now() - Number(ms || 0);
  if (d < 60000) return "刚刚";
  if (d < 3600000) return Math.floor(d / 60000) + " 分钟前";
  if (d < 86400000) return Math.floor(d / 3600000) + " 小时前";
  return Math.floor(d / 86400000) + " 天前";
}
/* 体检结果里的 paths 可能上千条，缓存前截到前 20 条，避免撞 localStorage 5MB 配额 */
function tbScanCacheShape(data){
  var items = (data && data.items) || [];
  var out = { total: data && data.total, disks: (data && data.disks) || [], items: [] };
  for (var i = 0; i < items.length; i++){
    var it = items[i];
    out.items.push({
      id: it.id, title: it.title, category: it.category, bytes: it.bytes, human: it.human,
      files: it.files, risk: it.risk, explain: it.explain, can_clean: it.can_clean,
      paths: (it.paths || []).slice(0, 20)
    });
  }
  return out;
}

/* ---------- DOM 小助手 ---------- */
function tbEl(tag, cls, html){
  var n = document.createElement(tag);
  if (cls) n.className = cls;
  if (html != null) n.innerHTML = html;
  return n;
}
function tbBusy(btn, on, busyText){
  if (!btn) return;
  if (on){
    /* 幂等：连点两次不会把 spinner 当成「原标签」存进去，否则按钮会永久转圈 */
    if (btn.dataset.busy === "1") return;
    btn.dataset.busy = "1";
    btn.dataset.label = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = '<span class="tb-spin" aria-hidden="true"></span>' + esc(busyText || "处理中…");
  } else {
    delete btn.dataset.busy;
    if (btn.dataset.label){ btn.innerHTML = btn.dataset.label; delete btn.dataset.label; }
    btn.disabled = false;
  }
}
function tbErr(msg){ return tbEl("div", "tb-error", icon("alert") + "<span>" + esc(msg) + "</span>"); }
function tbNote(msg){ return tbEl("div", "tb-note", icon("info") + "<span>" + msg + "</span>"); }
function tbEmpty(ico, text){
  return tbEl("div", "tb-empty", icon(ico) + "<span>" + text + "</span>");
}
/* 分段控件：options=[[value,label],...]，onPick(value)。返回容器。 */
function tbSeg(options, current, onPick){
  var seg = tbEl("div", "tb-seg");
  seg.setAttribute("role", "tablist");
  options.forEach(function(opt){
    var b = tbEl("button", opt[0] === current ? "on" : "", esc(opt[1]));
    b.type = "button";
    b.setAttribute("role", "tab");
    b.addEventListener("click", function(){
      if (b.className === "on") return;
      $$(".tb-seg button", seg).forEach(function(x){ x.className = ""; });
      b.className = "on";
      onPick(opt[0]);
    });
    seg.appendChild(b);
  });
  return seg;
}

/* ---------- 二次确认（危险操作） ---------- */
function confirmSheet(opts){
  return new Promise(function(resolve){
    var sheet = tbEl("div", "tb-sheet");
    var card = tbEl("div", "tb-sheet-card" + (opts.danger ? " danger" : ""));
    card.setAttribute("role", "alertdialog");
    card.setAttribute("aria-modal", "true");
    card.appendChild(tbEl("div", "tb-sheet-ico", icon(opts.danger ? "alert" : "info")));
    card.appendChild(tbEl("h4", null, esc(opts.title)));
    var lines = opts.lines || [];
    for (var i = 0; i < lines.length; i++) card.appendChild(tbEl("p", "tb-sheet-line", esc(lines[i])));
    var btns = tbEl("div", "tb-sheet-btns");
    var cancel = tbEl("button", "btn ghost", "取消");
    cancel.type = "button";
    var okBtn = tbEl("button", "btn " + (opts.danger ? "danger-solid" : ""), esc(opts.okText || "确认"));
    okBtn.type = "button";
    btns.appendChild(cancel);
    btns.appendChild(okBtn);
    card.appendChild(btns);
    sheet.appendChild(card);
    tbView.appendChild(sheet);
    activeSheet = sheet;
    okBtn.focus();
    function done(v){
      if (activeSheet !== sheet) return;
      activeSheet = null;
      sheet.parentNode.removeChild(sheet);
      document.removeEventListener("keydown", onKey, true);
      resolve(v);
    }
    function onKey(e){
      if (e.key === "Escape"){ e.stopPropagation(); done(false); }
      if (e.key === "Enter" && e.target !== cancel){ e.preventDefault(); done(true); }
    }
    cancel.addEventListener("click", function(){ done(false); });
    okBtn.addEventListener("click", function(){ done(true); });
    sheet.addEventListener("click", function(e){ if (e.target === sheet) done(false); });
    document.addEventListener("keydown", onKey, true);
  });
}

/* ---------- 面板注册表 ---------- */
var PANELS = {
  search: { title: "极速搜索", render: renderSearch },
  scan: { title: "本机体检", render: renderScan },
  uninstall: { title: "卸载软件", render: renderUninstall },
  shred: { title: "文件粉碎", render: renderShred }
};

function showPanel(key){
  var def = PANELS[key];
  if (!def) return;
  activeKey = key;
  $("#tbHome", tbView).hidden = true;
  /* 卡片页的快捷键提示不属于面板，进面板时要收起（否则会被挤在面板标题上方） */
  var foot = $(".tb-foot", tbView);
  if (foot) foot.hidden = true;
  var panel = $("#tbPanel", tbView);
  panel.hidden = false;
  panel.style.animation = "none";
  /* 重置入场动画（面板内容每次进入重新 stagger） */
  void panel.offsetWidth;
  panel.style.animation = "";
  $("#tbPanelTitle", tbView).textContent = def.title;
  var body = $("#tbPanelBody", tbView);
  body.innerHTML = "";
  def.render(body);
  hydrateIcons(body);
  var back = $("#tbBackBtn", tbView);
  if (back) back.focus();
}
function panelBack(){
  if (!activeKey) return;
  activeKey = null;
  $("#tbPanel", tbView).hidden = true;
  $("#tbPanelBody", tbView).innerHTML = "";
  var home = $("#tbHome", tbView);
  home.hidden = false;
  var foot = $(".tb-foot", tbView);
  if (foot) foot.hidden = false;
  /* 卡片重新入场（重新触发 stagger 动画） */
  $$(".tb-card", home).forEach(function(c){
    c.style.animation = "none";
    void c.offsetWidth;
    c.style.animation = "";
  });
  var first = $(".tb-card", home);
  if (first) first.focus();
}

/* ---------- 极速搜索 ---------- */
var SEARCH_SORTS = [ ["", "综合"], ["size-descending", "大小"], ["date-modified-descending", "时间"] ];
function renderSearch(body){
  var wrap = tbEl("div", "tb-block");
  wrap.appendChild(tbEl("p", "tb-lead", "输入文件名的一部分，立刻在全盘找到它（本机索引实时）。也支持 ext:pdf、size:>100mb 这类语法；结果可「定位」到文件夹，双击某行同样直接定位。"));
  var row = tbEl("div", "tb-cmd");
  var input = tbEl("input", "tb-input");
  input.type = "text";
  input.placeholder = "例如：报告.docx、setup、照片";
  input.setAttribute("aria-label", "搜索词");
  var btn = tbEl("button", "tb-go", "搜索全盘");
  btn.type = "button";
  row.appendChild(input);
  row.appendChild(btn);
  wrap.appendChild(row);
  /* 最近搜过的词：再点一次就重搜，不用重新敲 */
  var recentBox = tbEl("div", "tb-recent");
  wrap.appendChild(recentBox);
  function paintRecent(){
    recentBox.innerHTML = "";
    var list = tbCacheGet("recent") || [];
    if (!list.length){ recentBox.hidden = true; return; }
    recentBox.hidden = false;
    recentBox.appendChild(tbEl("span", "tb-count", "最近"));
    for (var i = 0; i < list.length; i++){
      (function(q){
        var chip = tbEl("button", "tb-recent-chip", esc(q));
        chip.type = "button";
        chip.title = "重新搜「" + q + "」";
        chip.addEventListener("click", function(){ input.value = q; run(); });
        recentBox.appendChild(chip);
      })(list[i]);
    }
  }
  function remember(q){
    var list = tbCacheGet("recent") || [];
    var next = [q];
    for (var i = 0; i < list.length; i++){ if (list[i] !== q && next.length < 6) next.push(list[i]); }
    tbCacheSet("recent", next);
    paintRecent();
  }
  var sortBox = tbEl("div", "tb-cmd");
  var sortSeg = null;
  var sortVal = "";
  sortSeg = tbSeg(SEARCH_SORTS, sortVal, function(v){ sortVal = v; if (input.value.trim()) run(); });
  var sortLabel = tbEl("span", "tb-count", "排序：");
  sortBox.appendChild(sortLabel);
  sortBox.appendChild(sortSeg);
  sortBox.hidden = true;
  wrap.appendChild(sortBox);
  var status = tbEl("div", "tb-status");
  wrap.appendChild(status);
  body.appendChild(wrap);
  input.focus();
  paintRecent();

  function run(){
    var q = input.value.trim();
    if (!q){ status.innerHTML = ""; status.appendChild(tbErr("请先输入搜索词")); return; }
    status.innerHTML = "";
    status.appendChild(tbEl("div", "tb-loading", '<span class="tb-spin"></span>正在搜索「' + esc(q) + "」…"));
    tbBusy(btn, true, "搜索中…");
    var t0 = Date.now();
    tbApi("search", { q: q, limit: 100, sort: sortVal }, 30000).then(function(data){
      status.innerHTML = "";
      sortBox.hidden = false;
      remember(q);
      var items = data.items || [];
      var ms = Date.now() - t0;
      status.appendChild(tbEl("div", "tb-count", "找到 <b>" + fmtNum(items.length) + "</b> 个文件" +
        (items.length >= 100 ? "（已达上限 100，可换更具体的词）" : "") +
        ' <span class="tb-dim">· ' + (ms / 1000).toFixed(1) + "s</span>"));
      if (!items.length){
        status.appendChild(tbEmpty("search", "没有找到匹配的文件。<br>索引可能还在建立，稍等几秒再试；或者换个更短的关键词。"));
        return;
      }
      for (var i = 0; i < items.length; i++) status.appendChild(searchRow(items[i]));
      hydrateIcons(status);
    }).catch(function(e){
      status.innerHTML = "";
      status.appendChild(tbErr(e.message));
    }).then(function(){ tbBusy(btn, false); });
  }
  btn.addEventListener("click", run);
  input.addEventListener("keydown", function(e){ if (e.key === "Enter") run(); });
}
function searchRow(item){
  var row = tbEl("div", "tb-row");
  row.title = "双击在资源管理器中定位";
  var main = tbEl("div", "tb-row-main");
  main.appendChild(tbEl("div", "tb-row-title", tbFileIcon(item.name) + esc(item.name)));
  var meta = (item.size ? fmtSize(item.size) : "") + (item.mtime ? " · " + esc(item.mtime) : "");
  if (meta) main.appendChild(tbEl("div", "tb-row-meta", meta));
  main.appendChild(tbEl("div", "tb-row-path", esc(item.path)));
  var side = tbEl("div", "tb-row-side");
  var reveal = tbEl("button", "tb-ghost tb-sm", icon("folder") + "定位");
  reveal.type = "button";
  reveal.title = "在资源管理器中显示该文件";
  var opened = false;
  function doReveal(){
    if (opened) return;
    opened = true;
    tbBusy(reveal, true, "打开…");
    tbApi("reveal", { path: item.path }, 15000).then(function(){
      tbToast("已在资源管理器中定位", "ok", 2400);
    }).catch(function(e){ tbToast("定位失败：" + e.message, "error", 4200); })
      .then(function(){ tbBusy(reveal, false); opened = false; });
  }
  reveal.addEventListener("click", doReveal);
  row.addEventListener("dblclick", doReveal);
  var copy = tbEl("button", "tb-ghost tb-sm", "复制路径");
  copy.type = "button";
  copy.addEventListener("click", function(){
    try {
      navigator.clipboard.writeText(item.path).then(function(){ tbToast("路径已复制", "ok", 2400); }, function(){ tbToast("复制失败", "error"); });
    } catch(e){ tbToast("复制失败", "error"); }
  });
  side.appendChild(reveal);
  side.appendChild(copy);
  row.appendChild(main);
  row.appendChild(side);
  return row;
}

/* 按扩展名给结果配图标：搜索结果一眼能分出「文档 / 图片 / 压缩包 / 程序」 */
var TB_EXT_ICONS = {
  doc:"edit", docx:"edit", txt:"edit", md:"edit", rtf:"edit", pdf:"file",
  xls:"list", xlsx:"list", csv:"list",
  ppt:"list", pptx:"list",
  jpg:"camera", jpeg:"camera", png:"camera", gif:"camera", bmp:"camera", webp:"camera", heic:"camera",
  zip:"archive", rar:"archive", "7z":"archive", tar:"archive", gz:"archive", iso:"archive",
  exe:"terminal", msi:"terminal", bat:"terminal", ps1:"terminal", cmd:"terminal",
  mp4:"camera", mov:"camera", mkv:"camera", avi:"camera", mp3:"sound", wav:"sound"
};
function tbFileIcon(name){
  var n = String(name || "");
  var dot = n.lastIndexOf(".");
  var ext = dot >= 0 ? n.slice(dot + 1).toLowerCase() : "";
  return icon(TB_EXT_ICONS[ext] || "file");
}

/* ---------- 本机体检 ---------- */
function renderScan(body){
  var wrap = tbEl("div", "tb-block");
  wrap.appendChild(tbEl("p", "tb-lead", "一次看清：每块磁盘还剩多少、缓存占了多少、哪些大文件在吃盘、重复文件和开机自启项。先给结论，再给明细；动手前可以点「显示路径」自己核对。"));
  /* 开屏先交代「要查什么、改不改东西」，比一个孤零零的按钮更像个完整功能 */
  var checks = tbEl("div", "tb-checks");
  var CHECK_ITEMS = ["磁盘剩余空间", "缓存与垃圾目录", "大文件与重复文件", "开机自启项"];
  for (var c = 0; c < CHECK_ITEMS.length; c++){
    checks.appendChild(tbEl("div", "tb-check", icon("checkCircle") + "<span>" + CHECK_ITEMS[c] + "</span>"));
  }
  wrap.appendChild(checks);
  wrap.appendChild(tbEl("div", "tb-note", icon("info") + "<span>体检只读：只统计不删除。真正清理前会再问你一次。</span>"));
  var btnRow = tbEl("div", "tb-cmd");
  var btn = tbEl("button", "tb-go", icon("shield") + "开始体检");
  btn.type = "button";
  btnRow.appendChild(btn);
  var ageEl = tbEl("span", "tb-count", "");
  btnRow.appendChild(ageEl);
  wrap.appendChild(btnRow);
  var status = tbEl("div", "tb-status");
  wrap.appendChild(status);
  body.appendChild(wrap);
  btn.focus();

  /* 见过一次就记住：再次打开面板直接显示上次结果，按钮变成「重新体检」 */
  var cached = tbCacheGet("scan");
  if (cached && cached.data){
    btn.innerHTML = icon("refresh") + "重新体检";
    ageEl.textContent = "上次体检 " + tbAgo(cached.at);
    paint(cached.data);
  }

  function paint(data){
    status.innerHTML = "";
    var disks = data.disks || [];
    if (disks.length){
      status.appendChild(tbEl("div", "tb-count", "磁盘空间"));
      for (var d = 0; d < disks.length; d++) status.appendChild(diskRow(disks[d]));
    }
    var items = data.items || [];
    if (data.total){
      status.appendChild(tbEl("div", "tb-count", "体检发现 <b>" + fmtNum(items.length) + "</b> 项，合计涉及 <b>" + esc(data.total) + "</b> 空间"));
    }
    if (!items.length){
      status.appendChild(tbEmpty("checkCircle", "本次没有发现值得处理的项目，电脑很干净。"));
      hydrateIcons(status);
      return;
    }
    var highCount = 0, cleanCount = 0;
    for (var k = 0; k < items.length; k++){
      if (Number(items[k].risk || 0) >= 60) highCount++;
      if (items[k].can_clean) cleanCount++;
    }
    var brief = tbEl("div", "tb-brief");
    brief.appendChild(tbEl("div", "tb-brief-ico", icon(highCount ? "alert" : "checkCircle")));
    brief.appendChild(tbEl("div", "tb-brief-text",
      "<b>" + (highCount ? highCount + " 项建议尽快处理" : "没有高风险项") + "</b>" +
      "<span>可一键清理 " + fmtNum(cleanCount) + " 项；其余项点「显示路径」自己确认后再动手。</span>"));
    status.appendChild(brief);
    if (cleanCount){
      var grantBar = tbEl("div", "tb-grant");
      var grantText = tbEl("div", "tb-grant-text",
        "<b>让绿虎接手清理</b><span>授权后，绿虎可在对话里直接清理体检列出的白名单缓存目录；粉碎、卸载仍需逐次确认。</span>");
      var grantBtn = tbEl("button", "tb-go tb-sm", "授权绿虎清理");
      grantBtn.type = "button";
      grantBtn.addEventListener("click", function(){
        confirmSheet({
          title: "授权绿虎清理缓存",
          lines: [
            "只授权 pc_clean_cache：绿虎仍只能清理体检扫描列出的白名单缓存目录，不能删除任意路径。",
            "粉碎文件、卸载软件、启动第三方程序仍需每次单独确认。",
            "授权会写入权限规则并落盘，之后可在 设置 → 权限规则 撤销。"
          ],
          okText: "确认授权"
        }).then(function(yes){
          if (!yes) return;
          tbBusy(grantBtn, true, "授权中…");
          tbApi("grant_clean", { confirm: true }, 15000).then(function(data){
            tbToast(data.message || "已授权绿虎清理白名单缓存", "ok", 4200);
            grantBtn.dataset.label = "已授权 · 可撤销";
            grantBtn.disabled = true;
            grantText.innerHTML = "<b>已授权绿虎清理</b><span>绿虎可在对话里清理白名单缓存；粉碎与卸载仍需逐次确认。撤销入口在 设置 → 权限规则。</span>";
          }).catch(function(e){
            tbToast("授权失败：" + e.message, "error", 4200);
          }).then(function(){
            tbBusy(grantBtn, false);
          });
        });
      });
      grantBar.appendChild(grantText);
      grantBar.appendChild(grantBtn);
      status.appendChild(grantBar);
    }
    for (var i = 0; i < items.length; i++) status.appendChild(scanRow(items[i]));
    hydrateIcons(status);
  }

  btn.addEventListener("click", function(){
    status.innerHTML = "";
    status.appendChild(tbEl("div", "tb-loading", '<span class="tb-spin"></span>正在体检（扫描缓存、大文件、重复文件与自启项，约 10~40 秒）…'));
    tbBusy(btn, true, "体检中…");
    tbApi("scan", {}, 120000).then(function(data){
      tbCacheSet("scan", { at: Date.now(), data: tbScanCacheShape(data) });
      ageEl.textContent = "刚刚完成";
      paint(data);
    }).catch(function(e){
      status.innerHTML = "";
      status.appendChild(tbErr("体检失败：" + e.message));
    }).then(function(){
      tbBusy(btn, false);
      btn.innerHTML = icon("refresh") + "重新体检";
    });
  });
}
function diskRow(disk){
  var box = tbEl("div", "tb-disk");
  var head = tbEl("div", "tb-disk-head");
  head.appendChild(tbEl("span", "tb-disk-name", esc(disk.label) + "（" + esc(disk.name) + "）"));
  head.appendChild(tbEl("span", "tb-disk-meta", "剩余 " + esc(disk.free_human) + " / 共 " + esc(disk.total_human)));
  box.appendChild(head);
  var total = Number(disk.total || 0), free = Number(disk.free || 0);
  var usedPct = total > 0 ? Math.round((total - free) * 100 / total) : 0;
  var bar = tbEl("div", "tb-bar");
  var fill = document.createElement("i");
  fill.style.width = Math.max(2, usedPct) + "%";
  if (usedPct >= 95) fill.className = "full";
  else if (usedPct >= 88) fill.className = "hot";
  bar.appendChild(fill);
  box.appendChild(bar);
  box.appendChild(tbEl("span", "tb-row-meta", "已用 " + usedPct + "%" + (usedPct >= 88 ? " · 空间紧张，建议清理" : "")));
  return box;
}
/* 体检路径行：文本 + hover 浮现的「定位」按钮（文件选中高亮 / 目录直开） */
function pathRow(p){
  var row = tbEl("div", "tb-path-row");
  row.appendChild(tbEl("div", "tb-row-path", esc(p)));
  var loc = tbEl("button", "tb-path-locate", icon("folder"));
  loc.type = "button";
  loc.title = "在资源管理器中定位";
  loc.setAttribute("aria-label", "定位 " + p);
  loc.addEventListener("click", function(){
    tbApi("reveal", { path: p }, 15000).then(function(){
      tbToast("已定位", "ok", 2000);
    }).catch(function(e){ tbToast("定位失败：" + e.message, "error", 3600); });
  });
  row.appendChild(loc);
  return row;
}
function riskBadge(risk){
  var cls = risk >= 60 ? "high" : risk >= 40 ? "mid" : "low";
  var label = risk >= 60 ? "风险高" : risk >= 40 ? "风险中" : "风险低";
  return '<span class="tb-badge ' + cls + '"><i class="tb-dot"></i>' + label + "</span>";
}
function scanRow(item){
  var row = tbEl("div", "tb-row tb-finding");
  var main = tbEl("div", "tb-row-main");
  var head = tbEl("div", "tb-row-title");
  head.innerHTML = riskBadge(item.risk) + "<b>" + esc(item.title) + "</b>";
  main.appendChild(head);
  var meta = item.human || "";
  if (item.files) meta += (meta ? " · " : "") + fmtNum(item.files) + " 项";
  if (meta) main.appendChild(tbEl("div", "tb-row-meta", esc(meta)));
  main.appendChild(tbEl("div", "tb-row-desc", esc(item.explain || "")));
  var paths = item.paths || [];
  if (paths.length){
    var toggle = tbEl("button", "tb-ghost tb-sm tb-toggle", "显示路径");
    toggle.type = "button";
    var list = tbEl("div", "tb-paths");
    list.hidden = true;
    for (var i = 0; i < paths.length && i < 60; i++) list.appendChild(pathRow(paths[i]));
    if (paths.length > 60) list.appendChild(tbEl("div", "tb-row-path", "… 其余 " + fmtNum(paths.length - 60) + " 条已省略"));
    toggle.addEventListener("click", function(){
      list.hidden = !list.hidden;
      toggle.textContent = list.hidden ? "显示路径" : "收起路径";
    });
    main.appendChild(toggle);
    main.appendChild(list);
  }
  var side = tbEl("div", "tb-row-side");
  if (item.can_clean){
    var clean = tbEl("button", "btn danger-solid tb-sm", "立即清理");
    clean.type = "button";
    clean.addEventListener("click", function(){
      confirmSheet({
        danger: true,
        title: "清理「" + item.title + "」",
        lines: [
          "将永久删除该分类下的缓存文件（约 " + (item.human || "未知大小") + "）。",
          "正在使用的文件会自动跳过，浏览器等软件建议先关闭。",
          "删除后无法恢复。"
        ],
        okText: "确认清理"
      }).then(function(yes){
        if (!yes) return;
        tbBusy(clean, true, "清理中…");
        tbApi("clean", { finding_id: item.id, confirm: true }, 120000).then(function(data){
          tbToast(data.message || "清理完成", "ok", 5200);
          row.classList.add("done");
          clean.dataset.label = "已清理";
        }).catch(function(e){ tbToast("清理失败：" + e.message, "error", 5200); })
          .then(function(){ tbBusy(clean, false); if (row.classList.contains("done")) clean.disabled = true; });
      });
    });
    side.appendChild(clean);
  }
  row.appendChild(main);
  row.appendChild(side);
  return row;
}

/* ---------- 卸载软件 ---------- */
var uninstallCache = [];
function renderUninstall(body){
  var wrap = tbEl("div", "tb-block");
  wrap.appendChild(tbEl("p", "tb-lead", "和「设置 → 应用」同一个来源（Windows 标准卸载注册表）。点「卸载」会打开该软件自己的卸载程序，按提示走完即可。"));
  var row = tbEl("div", "tb-cmd");
  var input = tbEl("input", "tb-input");
  input.type = "text";
  input.placeholder = "输入软件名筛选，例如：绿虎、Office";
  input.setAttribute("aria-label", "筛选软件名");
  row.appendChild(input);
  wrap.appendChild(row);
  var toolRow = tbEl("div", "tb-cmd");
  var sortVal = "name";
  var sortSeg = tbSeg([["name", "按名称"], ["size", "按大小"]], sortVal, function(v){
    sortVal = v;
    drawList(filtered());
  });
  var refresh = tbEl("button", "tb-ghost tb-sm", icon("refresh") + "刷新列表");
  refresh.type = "button";
  refresh.title = "卸载完成后重新读取列表";
  var countEl = tbEl("span", "tb-count", "");
  toolRow.appendChild(sortSeg);
  toolRow.appendChild(refresh);
  toolRow.appendChild(countEl);
  wrap.appendChild(toolRow);
  var status = tbEl("div", "tb-status");
  wrap.appendChild(status);
  body.appendChild(wrap);

  function filtered(){
    var q = input.value.trim().toLowerCase();
    var src = uninstallCache;
    var hit = [];
    for (var i = 0; i < src.length; i++){
      if (!q || src[i].name.toLowerCase().indexOf(q) >= 0) hit.push(src[i]);
    }
    /* 「按大小」以前只切了按钮样式、列表纹丝不动——排序放在这里做，两条路径共用同一份结果 */
    hit.sort(sortVal === "size"
      ? function(a, b){ return Number(b.size_bytes || 0) - Number(a.size_bytes || 0) || a.name.localeCompare(b.name, "zh-CN"); }
      : function(a, b){ return a.name.localeCompare(b.name, "zh-CN"); });
    return hit;
  }
  function drawList(items){
    status.innerHTML = "";
    countEl.textContent = items.length === uninstallCache.length
      ? "共 " + fmtNum(items.length) + " 个软件"
      : "匹配 " + fmtNum(items.length) + " / " + fmtNum(uninstallCache.length) + " 个";
    if (!items.length){
      status.appendChild(tbEmpty("trash", "没有匹配的软件，换个关键词试试。"));
      return;
    }
    for (var i = 0; i < items.length; i++) status.appendChild(uninstallRow(items[i]));
    hydrateIcons(status);
  }
  function load(soft){
    if (!soft) status.innerHTML = "";
    status.appendChild(tbEl("div", "tb-loading", '<span class="tb-spin"></span>正在读取已安装软件列表…'));
    tbApi("uninstall_list", {}, 30000).then(function(data){
      uninstallCache = data.items || [];
      drawList(filtered());
    }).catch(function(e){
      status.innerHTML = "";
      status.appendChild(tbErr("读取失败：" + e.message));
    });
  }
  input.addEventListener("input", function(){ drawList(filtered()); });
  refresh.addEventListener("click", function(){ load(true); });
  if (uninstallCache.length) drawList(filtered());
  else load(false);
  input.focus();
}
function uninstallRow(item){
  var row = tbEl("div", "tb-row");
  var main = tbEl("div", "tb-row-main");
  main.appendChild(tbEl("div", "tb-row-title", icon("archive") + esc(item.name) + (item.version ? ' <span class="tb-ver">v' + esc(item.version) + "</span>" : "")));
  var meta = [];
  if (item.publisher) meta.push(esc(item.publisher));
  var sizeText = item.size || (Number(item.size_bytes || 0) > 0 ? fmtSize(item.size_bytes) : "");
  if (sizeText) meta.push(esc(sizeText));
  if (meta.length) main.appendChild(tbEl("div", "tb-row-meta", meta.join(" · ")));
  var side = tbEl("div", "tb-row-side");
  var btn = tbEl("button", "btn danger ghost tb-sm", "卸载");
  btn.type = "button";
  btn.addEventListener("click", function(){
    confirmSheet({
      title: "卸载「" + item.name + "」",
      lines: [
        "将打开它的标准卸载程序（和控制面板里点「卸载」一样）。",
        "后续步骤请在弹出的卸载窗口里按提示完成。"
      ],
      okText: "打开卸载程序"
    }).then(function(yes){
      if (!yes) return;
      tbBusy(btn, true, "启动中…");
      tbApi("uninstall", { name: item.name, confirm: true }, 20000).then(function(data){
        tbToast("卸载程序已打开。完成卸载后回来点「刷新列表」。", "ok", 5200);
      }).catch(function(e){ tbToast("启动失败：" + e.message, "error", 5200); })
        .then(function(){ tbBusy(btn, false); });
    });
  });
  side.appendChild(btn);
  row.appendChild(main);
  row.appendChild(side);
  return row;
}

/* ---------- 文件粉碎 ---------- */
function renderShred(body){
  var wrap = tbEl("div", "tb-block");
  wrap.appendChild(tbEl("p", "tb-lead", "把敏感文件彻底抹掉：覆写 3 趟后再删除，常规手段无法恢复。只针对单个文件；系统目录会被自动拒绝。支持一次粘贴多行路径批量处理，加错了可以逐条移除或清空列表。"));
  var row = tbEl("div", "tb-cmd");
  var area = tbEl("textarea", "tb-area");
  area.placeholder = "每行一个文件完整路径，例如：\nD:\\照片\\身份证.jpg\nE:\\合同扫描件.pdf";
  area.setAttribute("aria-label", "文件路径（每行一个）");
  var add = tbEl("button", "tb-ghost", "添加到列表");
  add.type = "button";
  row.appendChild(area);
  row.appendChild(add);
  wrap.appendChild(row);
  var list = tbEl("div", "tb-paths tb-shred-list");
  wrap.appendChild(list);
  var cmd2 = tbEl("div", "tb-cmd");
  var check = tbEl("button", "tb-go", "检查这些文件");
  check.type = "button";
  check.disabled = true;
  var shredBtn = tbEl("button", "btn danger-solid", "确认粉碎");
  shredBtn.type = "button";
  shredBtn.disabled = true;
  var clearAll = tbEl("button", "tb-ghost tb-sm", "清空列表");
  clearAll.type = "button";
  clearAll.disabled = true;
  clearAll.addEventListener("click", function(){
    paths = [];
    status.innerHTML = "";
    redraw();
    tbToast("列表已清空", "info", 2000);
  });
  cmd2.appendChild(check);
  cmd2.appendChild(shredBtn);
  cmd2.appendChild(clearAll);
  wrap.appendChild(cmd2);
  var status = tbEl("div", "tb-status");
  wrap.appendChild(status);
  body.appendChild(wrap);
  var paths = [];
  var autoTimer = null;

  function redraw(){
    list.innerHTML = "";
    check.disabled = paths.length === 0;
    clearAll.disabled = paths.length === 0;
    shredBtn.disabled = true;
    if (!paths.length){
      list.appendChild(tbNote("还没有添加文件。添加后先「检查」，确认无误再粉碎。"));
      hydrateIcons(list);
      return;
    }
    for (var i = 0; i < paths.length; i++){
      (function(p, idx){
        var chip = tbEl("div", "tb-chip");
        chip.appendChild(tbEl("span", "tb-chip-text", esc(p)));
        var x = tbEl("button", "tb-chip-x", icon("x"));
        x.type = "button";
        x.setAttribute("aria-label", "移除 " + p);
        x.addEventListener("click", function(){ paths.splice(idx, 1); redraw(); });
        chip.appendChild(x);
        list.appendChild(chip);
      })(paths[i], i);
    }
    hydrateIcons(list);
  }
  function addPaths(){
    var raw = area.value.split(/\r?\n/);
    var added = 0;
    for (var i = 0; i < raw.length; i++){
      var v = raw[i].trim().replace(/^"+|"+$/g, "");
      if (!v) continue;
      if (paths.indexOf(v) >= 0) continue;
      paths.push(v);
      added++;
    }
    if (!added){ tbToast("没有新路径可添加", "warn", 2600); return; }
    area.value = "";
    status.innerHTML = "";
    redraw();
    tbToast("已添加 " + added + " 个路径，正在检查…", "ok", 2200);
    area.focus();
    /* 智能省步：添加后自动预览（只读检查，无任何删除） */
    if (autoTimer) clearTimeout(autoTimer);
    autoTimer = setTimeout(function(){ if (!check.disabled) check.click(); }, 650);
  }
  add.addEventListener("click", addPaths);

  check.addEventListener("click", function(){
    status.innerHTML = "";
    status.appendChild(tbEl("div", "tb-loading", '<span class="tb-spin"></span>正在检查路径…'));
    tbBusy(check, true, "检查中…");
    tbApi("shred_preview", { paths: paths }, 20000).then(function(data){
      status.innerHTML = "";
      var items = data.items || [];
      var okCount = 0, okSize = 0;
      for (var i = 0; i < items.length; i++){
        var it = items[i];
        if (it.ok){ okCount++; okSize += Number(it.bytes || 0); }
        status.appendChild(shredPreviewRow(it));
      }
      shredBtn.disabled = okCount === 0;
      status.appendChild(tbEl("div", "tb-count", "可粉碎 <b>" + fmtNum(okCount) + "</b> / " + fmtNum(items.length) + " 个文件，共 " + fmtSize(okSize)));
      if (okCount < items.length) status.appendChild(tbNote("标 ✗ 的路径无法粉碎（原因见说明），可移除后再继续。"));
    }).catch(function(e){
      status.innerHTML = "";
      status.appendChild(tbErr("检查失败：" + e.message));
    }).then(function(){ tbBusy(check, false); });
  });

  shredBtn.addEventListener("click", function(){
    confirmSheet({
      danger: true,
      title: "粉碎 " + fmtNum(paths.length) + " 个文件",
      lines: [
        "以下文件将被覆写 3 趟后彻底删除，任何软件都无法恢复：",
        paths.join("\n")
      ],
      okText: "永久粉碎，不可恢复"
    }).then(function(yes){
      if (!yes) return;
      tbBusy(shredBtn, true, "粉碎中…");
      tbApi("shred", { paths: paths, confirm: true }, 180000).then(function(data){
        status.innerHTML = "";
        paths = [];
        redraw();
        tbToast("粉碎完成", "ok", 4200);
        status.appendChild(tbEl("div", "tb-count", esc((data.message || "已完成").split("\n")[0])));
        status.appendChild(tbNote("粉碎是直接删除，不经过回收站。"));
      }).catch(function(e){
        tbToast("粉碎失败：" + e.message, "error", 6000);
      }).then(function(){
        tbBusy(shredBtn, false);
        /* 列表已清空：redraw 里的 disabled=true 会被 tbBusy 的收尾覆盖，这里补回来 */
        shredBtn.disabled = true;
      });
    });
  });
  redraw();
  area.focus();
}
function shredPreviewRow(it){
  var row = tbEl("div", "tb-row");
  var main = tbEl("div", "tb-row-main");
  main.appendChild(tbEl("div", "tb-row-title", icon(it.ok ? "checkCircle" : "xCircle") + esc(it.path)));
  main.appendChild(tbEl("div", "tb-row-meta", it.ok ? fmtSize(Number(it.bytes || 0)) + " · 可以粉碎" : esc(it.reason || "无法粉碎")));
  row.appendChild(main);
  return row;
}

/* ---------- 开关与事件 ---------- */
function openToolbox(){
  if (tbOpen) return;
  tbOpen = true;
  lastFocus = document.activeElement;
  tbView.hidden = false;
  /* 每次打开都从顶部开始，不要沿用上次的面板内滚动位置 */
  var scroller = $(".tb-scroll", tbView);
  if (scroller) scroller.scrollTop = 0;
  var first = $(".tb-card", tbView);
  if (first) first.focus();
}
function closeToolbox(){
  if (!tbOpen) return;
  tbOpen = false;
  if (activeSheet && activeSheet.parentNode) activeSheet.parentNode.removeChild(activeSheet);
  activeSheet = null;
  activeKey = null;
  $("#tbPanel", tbView).hidden = true;
  $("#tbPanelBody", tbView).innerHTML = "";
  $("#tbHome", tbView).hidden = false;
  var foot = $(".tb-foot", tbView);
  if (foot) foot.hidden = false;
  tbView.hidden = true;
  if (lastFocus && lastFocus.focus) { try { lastFocus.focus(); } catch(e){} }
}
function onKey(e){
  if (!tbOpen) return;
  if (e.key === "Escape"){
    if (activeSheet) return;               /* 确认层自己处理 Esc */
    e.preventDefault();
    if (activeKey) panelBack();
    else closeToolbox();
    return;
  }
  /* 数字键 1-4 快速进入面板（仅在卡片页、且焦点不在输入框时） */
  if (e.key >= "1" && e.key <= "4" && !activeKey && !activeSheet){
    var tag = (document.activeElement && document.activeElement.tagName) || "";
    if (tag !== "INPUT" && tag !== "TEXTAREA"){
      var keys = ["search", "scan", "uninstall", "shred"];
      showPanel(keys[Number(e.key) - 1]);
      return;
    }
  }
  if (e.key === "Tab"){
    /* 焦点圈定在工具箱内，避免 Tab 跑到被遮住的聊天输入框 */
    var focusables = $$("button,input,textarea,[href],[tabindex]:not([tabindex='-1'])", tbView).filter(function(n){
      return !n.disabled && n.offsetParent !== null;
    });
    if (!focusables.length) return;
    var first = focusables[0], last = focusables[focusables.length - 1];
    if (e.shiftKey && document.activeElement === first){ e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last){ e.preventDefault(); first.focus(); }
  }
}

var wired = false;
function initToolbox(){
  tbView = $("#toolboxView");
  if (!tbView || wired) return;
  wired = true;
  var openBtn = $("#toolboxBtn");
  if (openBtn) openBtn.addEventListener("click", openToolbox);
  var menuItem = $("#toolboxItem");
  if (menuItem) menuItem.addEventListener("click", openToolbox);
  var closeBtn = $("#toolboxCloseBtn");
  if (closeBtn) closeBtn.addEventListener("click", closeToolbox);
  var back = $("#tbBackBtn");
  if (back) back.addEventListener("click", panelBack);
  $$(".tb-card", tbView).forEach(function(card){
    card.addEventListener("click", function(){
      var key = card.getAttribute("data-tb");
      if (key) showPanel(key);
    });
  });
  document.addEventListener("keydown", onKey);
  hydrateIcons(tbView);
}

export { initToolbox };
