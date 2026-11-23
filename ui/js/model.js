/* 界面元数据：快捷键表、权限模式中文名与设置开关模板。 */
import { esc } from "./util.js";

var SHORTCUTS = [
  {group:"会话与面板", items:[
    {keys:["Ctrl","K"], desc:"打开命令面板"},
    {keys:["Ctrl","N"], desc:"新建会话"},
    {keys:["Ctrl",","], desc:"打开设置"},
    {keys:["Ctrl","B"], desc:"显示 / 隐藏侧栏"},
    {keys:["Ctrl","Shift","L"], desc:"打开运行日志"},
    {keys:["Ctrl","Shift","O"], desc:"在资源管理器打开工作目录"},
    {keys:["Ctrl","/"], desc:"显示这张快捷键表"}
  ]},
  {group:"输入框", items:[
    {keys:["Enter"], desc:"发送"},
    {keys:["Shift","Enter"], desc:"换行"},
    {keys:["Ctrl","Enter"], desc:"发送（等同 Enter）"},
    {keys:["↑","↓"], desc:"回溯上一条 / 下一条已发送内容"},
    {keys:["/"], desc:"斜杠命令补全"},
    {keys:["@"], desc:"引用工作目录里的文件"},
    {keys:["Esc"], desc:"停止当前任务，或关闭弹层"}
  ]},
  {group:"会话与消息", items:[
    {keys:["双击"], desc:"编辑该条消息并重发"},
    {keys:["F2"], desc:"重命名侧栏里聚焦的会话"},
    {keys:["↑","↓"], desc:"在弹层或补全列表里移动高亮"},
    {keys:["Tab"], desc:"在弹层内循环焦点"}
  ]}
];

function keyLabel(key){
  var map = {ctrl:"Ctrl", shift:"Shift", enter:"Enter", esc:"Esc", up:"↑", down:"↓"};
  return map[String(key||"").toLowerCase()] || key;
}

function shortcutHtml(list){
  return list.map(function(g){
    return '<div class="shortcut-group"><h5>'+esc(g.group)+'</h5>'
      + g.items.map(function(it){
          return '<div class="shortcut-row"><span class="shortcut-desc">'+esc(it.desc)+'</span>'
            +'<span class="shortcut-keys">'+it.keys.map(function(k){ return '<kbd>'+esc(keyLabel(k))+'</kbd>'; }).join("")
            +'</span></div>';
        }).join("")
      +'</div>';
  }).join("");
}

function toggleRowHtml(id,title,desc,checked){
  // aria-label：开关本身没有可见文字，屏幕阅读器否则只会念「复选框」
  return '<div class="toggle-row"><div><div class="toggle-title">'+esc(title)+'</div><div class="toggle-desc">'+esc(desc)+'</div></div>'
    +'<label class="switch"><input type="checkbox" id="'+id+'"'+(checked?" checked":"")+' aria-label="'+esc(title)+'"><span></span></label></div>';
}
function modeNameZh(m){
  if (m === "plan") return "规划模式";
  if (m === "acceptEdits") return "自动接受编辑";
  if (m === "auto") return "自动执行";
  return "逐项确认";
}

/* P3 第二轮：从主入口迁入的静态配置与纯函数 */
var SLASH_COMMANDS = [
  {cmd:"/new", desc:"新建会话", local:true},
  {cmd:"/clear", desc:"新建会话（旧会话入侧栏）"},
  {cmd:"/compact", desc:"压缩当前会话上下文"},
  {cmd:"/stats", desc:"会话统计"},
  {cmd:"/model", desc:"切换模型与优先级"},
  {cmd:"/init", desc:"分析工作目录并生成 AGENTS.md"},
  {cmd:"/review", desc:"只读检查最近改动"},
  {cmd:"/undo", desc:"回滚最近一次文件改动 / shell 命令前状态"},
  {cmd:"/stop", desc:"停止当前任务", local:true},
  {cmd:"/pause", desc:"暂停当前任务（可继续）", local:true},
  {cmd:"/retry", desc:"重试上一个回复", local:true},
  {cmd:"/settings", desc:"打开设置", local:true},
  {cmd:"/browser", desc:"打开绿虎浏览器", local:true},
  {cmd:"/memory", desc:"查看记忆摘要", local:true},
  {cmd:"/compact", desc:"立即压缩当前会话上下文", local:false},
  {cmd:"/rules", desc:"查看项目规则", local:true},
  {cmd:"/export", desc:"导出会话 Markdown", local:true},
  {cmd:"/help", desc:"命令面板", local:true},
  /* 主页示例 chip 已移除，改成斜杠快捷命令：选中后把任务文案填进输入框，不立即执行 */
  {cmd:"/看目录", desc:"看看工作目录里有什么", prompt:"看看工作目录里有什么"},
  {cmd:"/截屏", desc:"截屏看看当前屏幕", prompt:"截屏看看当前屏幕"},
  {cmd:"/搜资料", desc:"联网搜点资料", prompt:"联网搜点资料"},
  {cmd:"/整理文件", desc:"写个 PowerShell 整理文件", prompt:"写个 PowerShell 整理文件"},
  {cmd:"/开网页", desc:"用浏览器打开个网页", prompt:"用浏览器打开个网页"}
];
var SETTINGS_CATS = [
  {key:"workspace",label:"项目与工作区"},
  {key:"models",label:"模型与服务"},
  {key:"permissions",label:"权限与限制"},
  {key:"memory",label:"记忆"},
  {key:"automation",label:"定时任务"},
  {key:"extensions",label:"工具与扩展"},
  {key:"appearance",label:"应用行为"}
];
var HOME_PREFS = [
  {key:"uiMotion",label:"界面动效",read:function(c){ return c.uiMotion !== false; }},
  {key:"uiSounds",label:"界面提示音",read:function(c){ return !!c.uiSounds; }}
];

/* 模型状态读取：主入口用 bindState 注入当前 S，模块本身不持有全局状态。 */
var readState = function(){ return null; };
function bindState(fn){ readState = fn; }
function dsInfo(){
  // 唯一的通道是 OpenAI 兼容端点（主流云端服务商 / 自建网关）。没有「必须先登录某个网页版」的状态了，
  // 这里只回答一个问题：现在能不能聊。
  var S = readState();
  var b = (S && S.brain) || {};
  var api = b.provider || {};
  var ok = !!api.available;
  return {
    state: ok ? "ready" : "offline",
    label: ok ? "已就绪" : "未接入",
    cls: ok ? "ok" : "",
    chip: ok ? "已就绪" : "未接入模型",
    title: "",
    detail: ok ? (api.detail || "") : (api.detail || "还没有可用的模型")
  };
}
function brainInfo(){
  var S = readState();
  var b = (S && S.brain) || {};
  var api = b.provider || {};
  var active = b.active || "none";
  if (active === "provider"){
    return {active:"provider",cls:"api",short:(api.model || "模型"),
      detail:(b.providerName || "OpenAI 兼容") + " · " + (api.endpoint || "")};
  }
  // 没接 Key 也保留用户选定的模型名：胶囊写「模型 · 未接入」，不把当前选择藏成一句泛泛的“未接入模型”。
  var pending = api.model ? (api.model + " · 未接入") : "未接入模型";
  return {active:"none",cls:"none",short:pending,
    detail:api.detail || "设置 → 模型接入：选主流服务商并填入 API Key。"};
}
/* 连接胶囊写的是当前真正生效的模型；点开就是模型切换器。 */
function connInfo(){
  var bi = brainInfo();
  if (bi.active === "provider") return {state:"provider",cls:"ok",chip:bi.short,detail:bi.detail||""};
  return {state:"none",cls:"",chip:bi.short,detail:bi.detail||""};
}

export { SHORTCUTS, keyLabel, shortcutHtml, toggleRowHtml, modeNameZh, SLASH_COMMANDS, SETTINGS_CATS, HOME_PREFS, bindState, dsInfo, brainInfo, connInfo };
