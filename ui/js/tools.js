/* 工具调用的纯渲染辅助：参数摘要、状态文案、差异与折叠摘要。折叠交互仍在主入口。 */
import { TOOL_META, esc, attr, icon, oneLine, fmtSize } from "./util.js";
import { diffHtml } from "./markdown.js";

function toolMeta(name){
  return TOOL_META[name] || {label:name||"工具",icon:"terminal"};
}
function prettyArgs(t){
  try { return JSON.stringify(JSON.parse(t.argsJson||"{}"),null,2); } catch(e) { return t.argsJson||""; }
}
function toolSummary(t){
  if (t.command) return oneLine(t.command,150);
  var a = null;
  try{
    var o = JSON.parse(t.argsJson||"{}");
    // 两种历史形状都要吃：{path:...} 与 {args:{path:...}}
    a = (o && o.args && typeof o.args === "object") ? o.args : o;
  }catch(e){ return oneLine(t.argsJson||"",150); }
  if (!a || typeof a !== "object") return oneLine(t.argsJson||"",150);
  var v = a.summary||a.prompt||a.path||a.file||a.url||a.pattern||a.query||a.title||a.text||a.keys||a.action||a.seconds;
  if (v!==undefined && v!==null && String(v)!=="") return oneLine(String(v),150);
  // 没有主参数时拍平成 k=v，比裸 JSON 好读得多
  var parts = [];
  for (var k in a){
    if (!Object.prototype.hasOwnProperty.call(a,k)) continue;
    var val = a[k];
    if (val === null || val === undefined || val === "") continue;
    parts.push(k + "=" + oneLine(typeof val === "object" ? JSON.stringify(val) : String(val), 40));
    if (parts.length >= 3) break;
  }
  return oneLine(parts.join(" "), 150) || oneLine(t.argsJson||"",150);
}
function toolStatusLabel(t){
  if (t.status === "error") return {cls:"err", text:"失败"};
  if (t.status === "denied") return {cls:"err", text:"已拒绝"};
  if (t.status === "awaiting") return {cls:"warn", text:"待确认"};
  return null;
}
function toolOutPreview(t, open){
  var output = t.output || "";
  if (open || !output) return "";
  var lines = output.split("\n");
  var first = "", used = 0;
  for (var i=0;i<lines.length;i++){
    if (!lines[i].trim()) continue;
    used++;
    if (!first) first = lines[i];
  }
  if (!first) return "";
  return '<div class="tool-out"><span aria-hidden="true">\u23bf</span>'
    +'<span class="out-snip">'+esc(oneLine(first,140))+'</span>'
    +(used>1?'<span class="out-more">+'+(used-1)+' 行</span>':"")
  +'</div>';
}
function toolDiffHtml(t){
  return t.diff
    ? diffHtml(t.diff)
    : '<span>'+esc(t.status==="done" ? "这条记录没有保存写入前差异，无法准确还原。" : "执行后会在这里显示实际差异。")+'</span>';
}
function artifactHtml(a){
  if (!a || !a.id) return "";
  var url = "/api/artifact/"+encodeURIComponent(a.id);
  var isImg = (a.mime||"").indexOf("image/")===0;
  var meta = '<span class="artifact-meta">'+icon(isImg?"camera":"file")+'<b title="'+attr(a.name)+'">'+esc(a.name||"文件")+'</b><i>'+fmtSize(a.size)+'</i></span>';
  if (isImg){
    return '<a class="artifact" href="'+url+'" target="_blank" rel="noopener" title="'+attr(a.name)+'"><img loading="lazy" src="'+url+'" alt="'+attr(a.name||"截图")+'">'+meta+'</a>';
  }
  return '<a class="artifact" href="'+url+'" target="_blank" rel="noopener" title="'+attr(a.name)+'">'+meta+'</a>';
}
function riskText(risk){
  if (risk === "danger") return "高危";
  if (risk === "normal") return "普通";
  if (risk === "safe") return "安全";
  return risk||"未知";
}
function riskClass(risk){
  if (risk === "danger") return "err";
  if (risk === "normal") return "warn";
  return "ok";
}
function planInfo(t){
  var title = "", steps = [];
  try{
    var o = JSON.parse(t.argsJson||"{}");
    var a = (o && typeof o === "object") ? (o.args||o) : {};
    if (a && a.title) title = a.title;
    if (a && Array.isArray(a.steps)) steps = a.steps;
  }catch(e){}
  return {title:title, steps:steps};
}

export { toolMeta, prettyArgs, toolSummary, toolStatusLabel, toolOutPreview, toolDiffHtml, artifactHtml, riskText, riskClass, planInfo };
