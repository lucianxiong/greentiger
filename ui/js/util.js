/* 基础工具层：图标表、转义、格式化、模糊匹配。无状态、无 DOM 副作用，可被任意模块复用。 */

var ICONS = {
  plus:'<path d="M12 5v14M5 12h14"/>',
  send:'<path d="M12 19V5M5 12l7-7 7 7"/>',
  stop:'<rect x="7" y="7" width="10" height="10" rx="2"/>',
  pause:'<rect x="8" y="6" width="3" height="12" rx="1"/><rect x="13" y="6" width="3" height="12" rx="1"/>',
  folder:'<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  sun:'<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.42 1.42m11.3 11.3 1.42 1.42M2 12h2m16 0h2M4.93 19.07l1.42-1.42m11.3-11.3 1.42-1.42"/>',
  moon:'<path d="M20.5 14.2A8.5 8.5 0 0 1 9.8 3.5 8.5 8.5 0 1 0 20.5 14.2z"/>',
  wind:'<path d="M3 8h11a3 3 0 1 0-3-3"/><path d="M3 12h15a3 3 0 1 1-3 3"/><path d="M3 16h7"/>',
  sound:'<path d="M11 5L6 9H3v6h3l5 4V5z"/><path d="M16.5 9a4.5 4.5 0 010 6"/><path d="M19.2 6.5a8 8 0 010 11"/>',
  spark:'<path d="M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z"/>',
  mic:'<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5 11a7 7 0 0 0 14 0M12 18v3M8 21h8"/>',
  logs:'<path d="M4 6h16M4 12h16M4 18h10"/>',
  sliders:'<path d="M4 6h9M18 6h2M4 12h2M11 12h9M4 18h9M18 18h2"/><circle cx="15" cy="6" r="2"/><circle cx="8" cy="12" r="2"/><circle cx="15" cy="18" r="2"/>',
  command:'<path d="M8 9l-4 3 4 3M16 9l4 3-4 3M13 5l-2 14"/>',
  panel:'<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M9 4v16"/>',
  panelRight:'<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M15 4v16"/>',
  more:'<circle cx="5" cy="12" r="1.3" fill="currentColor" stroke="none"/><circle cx="12" cy="12" r="1.3" fill="currentColor" stroke="none"/><circle cx="19" cy="12" r="1.3" fill="currentColor" stroke="none"/>',  toolbox:'<rect x="3" y="8" width="18" height="12" rx="2"/><path d="M9 8V6a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v2M12 12v3"/>',

  x:'<path d="M6 6l12 12M18 6L6 18"/>',
  check:'<path d="M5 12l4 4L19 6"/>',
  checkCircle:'<circle cx="12" cy="12" r="9"/><path d="M8 12l3 3 5-6"/>',
  xCircle:'<circle cx="12" cy="12" r="9"/><path d="M9 9l6 6M15 9l-6 6"/>',
  alert:'<path d="M12 3l9 17H3z"/><path d="M12 9v5M12 17h.01"/>',
  info:'<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/>',
  shield:'<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/><path d="M9 12l2 2 4-4"/>',
  paperclip:'<path d="M21.4 11.1l-8.8 8.8a6 6 0 0 1-8.5-8.5l8.8-8.8a4 4 0 0 1 5.7 5.7l-8.8 8.8a2 2 0 0 1-2.8-2.8l8.1-8.1"/>',
  trash:'<path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/>',
  pin:'<path d="M9 3h6l-1 6 3 3v2H7v-2l3-3-1-6zM12 14v7"/>',
  archive:'<rect x="3" y="4" width="18" height="4" rx="1"/><path d="M5 8v11a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8M10 12h4"/>',
  chevronDown:'<path d="M6 9l6 6 6-6"/>',
  arrowDown:'<path d="M12 5v14M5 12l7 7 7-7"/>',
  arrowLeft:'<path d="M19 12H5M12 5l-7 7 7 7"/>',
  arrowRight:'<path d="M5 12h14M12 5l7 7-7 7"/>',
  terminal:'<rect x="4" y="5" width="16" height="14" rx="2"/><path d="M8 9l3 3-3 3M13 15h4"/>',
  file:'<path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z"/><path d="M14 3v6h6"/>',
  edit:'<path d="M4 20h4L20 8l-4-4L4 16z"/>',
  undo:'<path d="M9 14L4 9l5-5"/><path d="M4 9h10a6 6 0 0 1 0 12h-3"/>',
  list:'<path d="M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01"/>',
  camera:'<path d="M4 8h3l2-3h6l2 3h3v11H4z"/><circle cx="12" cy="13" r="3"/>',
  cursor:'<path d="M5 3l14 8-6 1.5L10 19z"/>',
  keyboard:'<rect x="3" y="6" width="18" height="12" rx="2"/><path d="M7 10h.01M11 10h.01M15 10h.01M19 10h.01M8 14h8"/>',
  window:'<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 8h18"/>',
  upload:'<path d="M12 16V4M7 9l5-5 5 5M4 20h16"/>',
  clipboard:'<rect x="6" y="4" width="12" height="17" rx="2"/><path d="M9 4V2h6v2"/>',
  clock:'<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  refresh:'<path d="M20 11a8 8 0 1 0-2.3 6.7M20 5v6h-6"/>',
  power:'<path d="M12 3v9M6.4 6.4a8 8 0 1 0 11.2 0"/>',
  copy:'<rect x="8" y="8" width="11" height="12" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h2"/>',
  download:'<path d="M12 4v12M7 11l5 5 5-5M4 20h16"/>',
  search:'<circle cx="11" cy="11" r="7"/><path d="M20 20l-4.3-4.3"/>',
  brain:'<path d="M9 4a3 3 0 0 0-3 3 3 3 0 0 0-1 5.8A3 3 0 0 0 7 18a3 3 0 0 0 5 1.7V4.6A3 3 0 0 0 9 4z"/><path d="M15 4a3 3 0 0 1 3 3 3 3 0 0 1 1 5.8A3 3 0 0 1 17 18a3 3 0 0 1-5 1.7"/>',
  theme:'<circle cx="12" cy="12" r="9"/><path d="M12 3v18a9 9 0 0 0 0-18z" fill="currentColor"/>',
  puzzle:'<path d="M4 6a2 2 0 0 1 2-2h3v1.5a2.5 2.5 0 0 0 5 0V4h3a2 2 0 0 1 2 2v3h-1.5a2.5 2.5 0 0 0 0 5H19v3a2 2 0 0 1-2 2h-3v-1.5a2.5 2.5 0 0 0-5 0V19H6a2 2 0 0 1-2-2v-3h1.5a2.5 2.5 0 0 0 0-5H4z"/>',
};

var TOOL_META = {
  shell:{label:"PowerShell",icon:"terminal"},
  read_file:{label:"读取文件",icon:"file"},
  write_file:{label:"写入文件",icon:"edit"},
  edit:{label:"精确修改",icon:"edit"},
  glob:{label:"查找文件",icon:"search"},
  grep:{label:"检索内容",icon:"search"},
  list_dir:{label:"列出目录",icon:"list"},
  screenshot:{label:"屏幕截图",icon:"camera"},
  mouse:{label:"鼠标",icon:"cursor"},
  keyboard:{label:"键盘",icon:"keyboard"},
  focus_window:{label:"聚焦窗口",icon:"window"},
  upload_file:{label:"上传附件",icon:"upload"},
  http_get:{label:"网络请求",icon:"globe"},
  clipboard:{label:"剪贴板",icon:"clipboard"},
  wait:{label:"等待",icon:"clock"},
  finish:{label:"完成任务",icon:"checkCircle"},
  plan:{label:"计划",icon:"list"},
  todo:{label:"进度",icon:"checkCircle"},
  web_search:{label:"网页搜索",icon:"search"},
  task:{label:"子任务",icon:"spark"},
  everything_search:{label:"全盘搜索",icon:"search"},
  pc_scan:{label:"本机体检",icon:"shield"},
  pc_uninstall_list:{label:"已装程序",icon:"list"},
  pc_uninstall_launch:{label:"启动卸载",icon:"trash"},
  pc_clean_cache:{label:"清理缓存",icon:"refresh"},
  pc_shred:{label:"文件粉碎",icon:"archive"},
  pc_launch_app:{label:"软件直达",icon:"power"}
};

var ESC_MAP = {"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"};
/* 取目录的父目录（用于 @ 引用面板的「上一级」）。纯路径字符串操作，无 DOM 依赖。 */
function atParentDir(dir){
  if (!dir || dir === ".") return "";
  var i = dir.lastIndexOf("/");
  return i <= 0 ? "" : dir.slice(0, i);
}

function $(sel,root){ return (root||document).querySelector(sel); }
function $$(sel,root){ return Array.prototype.slice.call((root||document).querySelectorAll(sel)); }
function icon(name,cls){
  var p = ICONS[name] || ICONS.spark;
  return '<svg class="ico '+(cls||'')+'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'+p+'</svg>';
}
function avatarHtml(){ return '<div class="avatar"><img src="/icons/icon-192.png" alt="" draggable="false"></div>'; }
function esc(v){ return String(v==null?"":v).replace(/[&<>"']/g,function(c){return ESC_MAP[c];}); }
function attr(v){ return esc(v).replace(/\n/g,"&#10;").replace(/\r/g,"&#13;"); }
function clampInt(v,min,max,fallback){
  var n = parseInt(v,10);
  if (isNaN(n)) n = fallback;
  return Math.max(min,Math.min(max,n));
}
function fmtNum(n){ try{ return Number(n||0).toLocaleString("zh-CN"); }catch(e){ return String(n||0); } }
function fmtSize(b){
  b = Number(b||0);
  if (b < 1024) return b+" B";
  if (b < 1024*1024) return (b/1024).toFixed(1)+" KB";
  if (b < 1024*1024*1024) return (b/1024/1024).toFixed(1)+" MB";
  return (b/1024/1024/1024).toFixed(2)+" GB";
}
function fmtDuration(ms){
  ms = Number(ms||0);
  if (ms < 1000) return ms+"ms";
  if (ms < 60000) return (ms/1000).toFixed(1)+"s";
  return Math.floor(ms/60000)+"m"+Math.floor((ms%60000)/1000)+"s";
}
function fmtClock(sec){
  sec = Math.max(0,Math.floor(sec));
  var m = Math.floor(sec/60), s = sec%60;
  return "运行 "+(m>0?m+":"+String(s).padStart(2,"0"):sec+"s");
}
function fmtTime(ms){
  try{ return new Date(ms).toLocaleTimeString("zh-CN",{hour:"2-digit",minute:"2-digit"}); }catch(e){ return ""; }
}
function fmtLogTime(ms){
  try{ return new Date(ms).toLocaleTimeString("zh-CN",{hour12:false,hour:"2-digit",minute:"2-digit",second:"2-digit"}); }catch(e){ return ""; }
}
function relTime(ms){
  var d = Date.now()-Number(ms||0);
  if (d < 45000) return "刚刚";
  if (d < 3600000) return Math.floor(d/60000)+" 分钟前";
  if (d < 86400000) return Math.floor(d/3600000)+" 小时前";
  if (d < 604800000) return Math.floor(d/86400000)+" 天前";
  try{ return new Date(ms).toLocaleDateString("zh-CN",{month:"numeric",day:"numeric"}); }catch(e){ return ""; }
}
function baseName(p){
  var s = String(p==null?"":p);
  var i = Math.max(s.lastIndexOf("\\"),s.lastIndexOf("/"));
  return i>=0 ? s.slice(i+1) : s;
}
function shortPath(p,max){
  p = String(p||"");
  max = max||62;
  if (!p) return "";
  if (p.length <= max) return p;
  var head = Math.max(12,Math.floor(max*.38));
  return p.slice(0,head)+"…"+p.slice(p.length-(max-head-1));
}
function oneLine(s,max){
  var t = String(s==null?"":s).replace(/\s+/g," ").trim();
  if (t.length > (max||120)) t = t.slice(0,max||120)+"…";
  return t;
}

function fuzzyScore(text, query){
  var t = String(text == null ? "" : text).toLowerCase();
  var q = String(query == null ? "" : query).toLowerCase().replace(/\s+/g,"");
  if (!q) return {score:1, hits:[]};
  if (t === q) return {score:1000, hits:[0]};
  if (t.indexOf(q) === 0) {
    var head = [];
    for (var k = 0; k < q.length; k++) head.push(k);
    return {score:600, hits:head};
  }
  var hits = [], ti = 0, score = 0, streak = 0;
  for (var qi = 0; qi < q.length; qi++){
    var ch = q.charAt(qi);
    var found = -1;
    for (var i = ti; i < t.length; i++){ if (t.charAt(i) === ch){ found = i; break; } }
    if (found < 0) return null;
    var prev = found > 0 ? t.charAt(found-1) : "";
    var boundary = found === 0 || " -_/.".indexOf(prev) >= 0;
    score += 10;
    if (found === ti && ti > 0){ streak++; score += 6 * streak; }
    else streak = 0;
    if (boundary) score += 8;
    score -= Math.min(found - ti, 4);
    hits.push(found);
    ti = found + 1;
  }
  return {score:score, hits:hits};
}

function fuzzyHighlight(text, hits){
  var t = String(text == null ? "" : text);
  if (!hits || !hits.length) return esc(t);
  var out = "", hi = 0;
  for (var i = 0; i < t.length; i++){
    var hit = hits[hi] === i;
    if (hit) hi++;
    var piece = esc(t.charAt(i));
    out += hit ? '<b class="fz">'+piece+'</b>' : piece;
  }
  return out;
}

function fuzzyRank(items, query, keyFn){
  var q = String(query == null ? "" : query).trim();
  var out = [];
  for (var i = 0; i < items.length; i++){
    var text = keyFn ? keyFn(items[i]) : items[i];
    var hit = fuzzyScore(text, q);
    if (hit) out.push({item:items[i], score:hit.score, hits:hit.hits, index:i});
  }
  if (q){
    out.sort(function(a,b){
      if (b.score !== a.score) return b.score - a.score;
      return a.index - b.index;
    });
  }
  return out;
}

/* P3 第二轮：从主入口迁入的静态配置与纯函数 */
function hydrateIcons(root){
  Array.prototype.slice.call((root||document).querySelectorAll("[data-icon]")).forEach(function(node){
    var name = node.getAttribute("data-icon");
    // 保留宿主 span 上的业务类（如 .chip-chev）：直接 outerHTML 替换会把类丢掉
    var keep = node.className && node.className !== "ico" ? " " + node.className : "";
    node.outerHTML = icon(name, keep.trim());
  });
}

var FOCUSABLE = 'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])';

export { ICONS, TOOL_META, ESC_MAP, $, $$, atParentDir, icon, avatarHtml, esc, attr, clampInt, fmtNum, fmtSize, fmtDuration, fmtClock, fmtTime, fmtLogTime, relTime, baseName, shortPath, oneLine, fuzzyScore, fuzzyHighlight, fuzzyRank, hydrateIcons, FOCUSABLE };
