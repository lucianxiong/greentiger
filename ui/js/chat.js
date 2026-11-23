/* 消息渲染的纯辅助：渲染签名、会话条目、日期分组、模型标签。keyed diff 仍在主入口。 */
import { esc, attr, icon, relTime } from "./util.js";

function msgSig(m){
  var t = m.toolCalls||[];
  var parts = [m.status||"", String((m.text||"").length), (m.text||"").slice(-24)];
  for (var j=0;j<t.length;j++){
    parts.push(t[j].id, t[j].status, String((t[j].output||"").length), t[j].approvalStatus||"", String((t[j].diff||"").length));
  }
  return parts.join(",");
}

/* 会话项：pickMode 下整行退化成复选框（不生成悬停操作按钮，避免多选时误触归档/删除）。 */
function sessionItemHtml(s, pickMode, picked){
  var tools = s.toolCount ? '<span>'+s.toolCount+' 工具</span>' : "";
  var pinned = s.pinned ? '<span>置顶</span>' : "";
  var ops = pickMode ? "" : ('<div class="session-ops">'
    +'<button class="session-op" data-pin-session="'+attr(s.id)+'" title="'+(s.pinned?"取消置顶":"置顶会话")+'" aria-label="'+(s.pinned?"取消置顶":"置顶会话")+'">'+icon("pin")+'</button>'
    +'<button class="session-op" data-archive-session="'+attr(s.id)+'" title="'+(s.archived?"取消归档":"归档会话")+'" aria-label="'+(s.archived?"取消归档":"归档会话")+'">'+icon("archive")+'</button>'
    +'<button class="session-op session-del" data-del-session="'+attr(s.id)+'" title="删除会话" aria-label="删除会话">'+icon("trash")+'</button>'
    +'</div>');
  var check = pickMode
    ? '<span class="session-check'+(picked?" on":"")+'" aria-hidden="true">'+(picked?icon("check"):"")+'</span>'
    : "";
  var roleAttr = pickMode ? 'role="checkbox" aria-checked="'+(picked?"true":"false")+'"' : 'role="button" aria-current="'+(s.active?"true":"false")+'"';
  var titleAttr = pickMode ? "点击选中 / 取消选中" : (s.title+'（双击或 F2 重命名）');
  return '<div class="session-item'+(s.active && !pickMode ? " active":"")+(s.archived?" archived":"")+(pickMode?" picking":"")+(picked?" selected":"")+'" data-session="'+attr(s.id)+'" '+roleAttr+' tabindex="0" title="'+esc(titleAttr)+'">'
    +check
    +'<div class="session-main"><div class="session-title" title="'+attr(s.title)+'">'+esc(s.title)+'</div>'
    +'<div class="session-meta"><span>'+s.messageCount+' 条</span>'+tools+pinned+'<span>·</span><span>'+esc(relTime(s.updatedAt))+'</span></div></div>'
    +ops+'</div>';
}
function dayGroup(ts){
  if (!ts) return "更早";
  var now = new Date();
  var startToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  if (ts >= startToday) return "今天";
  if (ts >= startToday - 86400000) return "昨天";
  var startYear = new Date(now.getFullYear(), 0, 1).getTime();
  if (ts >= startYear) return "今年";
  return "更早";
}
function brainTagHtml(m){
  var b = (m && m.brain) || "";
  if (!b) return "";
  var label = b === "skill" ? "本地技能" : b === "provider" ? "模型" : "";
  if (!label) return "";
  return '<span class="msg-model">'+esc(label)+'</span>';
}
function splitAttachments(text){
  var result = {body:[],files:[]};
  var lines = String(text||"").split("\n");
  for (var i=0;i<lines.length;i++){
    var m = lines[i].match(/^\[附件\]\s+(.+)$/);
    if (m) result.files.push(m[1].trim());
    else result.body.push(lines[i]);
  }
  return result;
}

export { msgSig, sessionItemHtml, dayGroup, brainTagHtml, splitAttachments };
