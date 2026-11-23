/* Markdown 与语法高亮渲染层：把模型正文转成 HTML，零依赖语法高亮。 */
import { esc } from "./util.js";

var HL_KEYWORDS = {
  js:"var let const function return if else for while do break continue new class extends import export from await async try catch finally throw typeof instanceof of in this super null undefined true false delete void switch case default yield static get set",
  ts:"var let const function return if else for while do break continue new class extends implements interface type enum import export from await async try catch finally throw typeof instanceof of in this super null undefined true false delete void switch case default yield static readonly public private protected namespace declare as",
  cs:"using namespace public private protected internal static class struct interface enum record void var new return if else for foreach while do break continue switch case default try catch finally throw this base null true false async await out ref in params override virtual abstract sealed readonly const get set yield lock is as typeof nameof where partial event delegate operator",
  py:"def class return if elif else for while break continue import from as try except finally raise with lambda None True False and or not in is pass yield async await global nonlocal assert del print self",
  sh:"if then else elif fi for while do done case esac function return export local readonly set unset source exit echo cd printf read test shift trap",
  ps:"if else elseif foreach for while do switch function param return begin process end try catch finally throw filter where select",
  sql:"select from where insert into values update set delete join left right inner outer on group by order limit offset as and or not null create table drop alter index primary key",
  go:"func var const type struct interface return if else for range switch case default break continue go defer select chan map package import nil true false make new len cap append",
  rs:"fn let mut const struct enum impl trait pub use mod match if else for while loop return break continue Some None Ok Err self true false",
  java:"public private protected class interface extends implements void int long double float boolean char String new return if else for while do break continue switch case default try catch finally throw static final abstract synchronized import package this super null true false",
  rb:"def end class module if elsif else unless while until for in do return yield begin rescue ensure raise nil true false self require attr_accessor puts",
  php:"function class public private protected static return if else elseif foreach for while do switch case break continue echo new array null true false use namespace",
  c:"int char float double void struct union enum return if else for while do break continue switch case default sizeof typedef static const unsigned signed long short goto",
  cpp:"int char float double void struct class union enum return if else for while do break continue switch case default sizeof typedef static const unsigned signed long short namespace using template typename public private protected virtual override new delete nullptr true false auto constexpr",
  json:"true false null",
  yaml:"true false null",
  css:"important media supports keyframes import from to",
  html:"true false"
};
var HL_LINE_COMMENT = {js:"//",ts:"//",cs:"//",go:"//",rs:"//",java:"//",php:"//",c:"//",cpp:"//",ps:"#",py:"#",sh:"#",rb:"#",sql:"--",yaml:"#"};
var HL_TPL = {js:1,ts:1,go:1};
var HL_TYPES = {cs:1,java:1,ts:1,go:1,rs:1,c:1,cpp:1};

function jsonDepth(str){
  var depth = 0, inStr = false, escNext = false;
  for (var i=0;i<str.length;i++){
    var c = str.charAt(i);
    if (inStr){
      if (escNext) { escNext = false; continue; }
      if (c === "\\") { escNext = true; continue; }
      if (c === "\"") inStr = false;
      continue;
    }
    if (c === "\"") { inStr = true; continue; }
    if (c === "{" || c === "[") depth++;
    else if (c === "}" || c === "]") depth--;
  }
  return depth;
}
function hlConf(lang){
  var key = String(lang||"").toLowerCase().replace(/^language-/,"").split(/[\s:{?]/)[0];
  if (key === "javascript") key = "js";
  if (key === "typescript") key = "ts";
  if (key === "csharp" || key === "c#") key = "cs";
  if (key === "python") key = "py";
  if (key === "bash" || key === "shell" || key === "console") key = "sh";
  if (key === "golang") key = "go";
  if (key === "rust") key = "rs";
  if (key === "ruby") key = "rb";
  if (!Object.prototype.hasOwnProperty.call(HL_KEYWORDS, key)) return null;
  var words = HL_KEYWORDS[key] ? HL_KEYWORDS[key].split(" ") : [];
  var map = {};
  for (var i=0;i<words.length;i++) map[words[i]] = 1;
  return {kw:map, line:HL_LINE_COMMENT[key]||"", tpl:!!HL_TPL[key], types:!!HL_TYPES[key]};
}

function highlightCode(code, lang){
  var raw = String(code==null?"":code);
  var conf = hlConf(lang);
  if (!conf) return esc(raw);
  var out = [], buf = "", i = 0, n = raw.length;
  function flush(){ if (buf){ out.push(esc(buf)); buf = ""; } }
  function wrap(cls, text){ flush(); out.push('<span class="'+cls+'">'+esc(text)+'</span>'); }
  while (i < n){
    var ch = raw.charAt(i), next = raw.charAt(i+1);
    if (conf.line && raw.substr(i, conf.line.length) === conf.line){
      var le = raw.indexOf("\n", i);
      if (le < 0) le = n;
      wrap("hl-c", raw.slice(i, le));
      i = le;
      continue;
    }
    if (ch === "/" && next === "*"){
      var be = raw.indexOf("*/", i+2);
      be = be < 0 ? n : be + 2;
      wrap("hl-c", raw.slice(i, be));
      i = be;
      continue;
    }
    if (ch === '"' || ch === "'" || (ch === "`" && conf.tpl)){
      var j = i + 1, closed = false;
      while (j < n){
        var cj = raw.charAt(j);
        if (cj === "\\"){ j += 2; continue; }
        if (cj === ch){ closed = true; j++; break; }
        if (cj === "\n" && ch !== "`") break;
        j++;
      }
      if (closed && j - i > 1){ wrap("hl-s", raw.slice(i, j)); i = j; continue; }
    }
    if (ch >= "0" && ch <= "9" && !/[A-Za-z0-9_$]/.test(raw.charAt(i-1))){
      var jn = i;
      while (jn < n && /[0-9a-fA-FxX._]/.test(raw.charAt(jn))) jn++;
      wrap("hl-n", raw.slice(i, jn));
      i = jn;
      continue;
    }
    if (/[A-Za-z_$]/.test(ch)){
      var jw = i;
      while (jw < n && /[A-Za-z0-9_$]/.test(raw.charAt(jw))) jw++;
      var word = raw.slice(i, jw);
      var after = raw.charAt(jw);
      if (conf.kw[word]) wrap("hl-k", word);
      else if (after === "(") wrap("hl-f", word);
      else if (conf.types && /^[A-Z]/.test(word)) wrap("hl-t", word);
      else buf += word;
      i = jw;
      continue;
    }
    buf += ch;
    i++;
  }
  flush();
  return out.join("");
}

function codeBlockHtml(lang,code){
  var label = lang ? lang : "text";
  var body = code.replace(/\n$/,"");
  return '<div class="codeblock"><div class="code-head"><span>'+esc(label)+'</span><button class="copy-btn" data-copy-code>复制</button></div><pre><code>'+highlightCode(body,lang)+'</code></pre></div>';
}
function inlineMd(text){
  var codes = [], links = [];
  var s = text.replace(/`([^`\n]+)`/g,function(m,c){
    codes.push(c); return "\u0001"+(codes.length-1)+"\u0001";
  });
  s = s.replace(/\[([^\]\n]+)\]\((https?:\/\/[^\s)]+)\)/g,function(m,label,url){
    links.push({label:label,url:url}); return "\u0002"+(links.length-1)+"\u0002";
  });
  s = s.replace(/(https?:\/\/[^\s<>"'\u0002]+)/g,function(url){
    var trail = "";
    while (/[.,;:!?)\]，。；：！？）】]$/.test(url)) { trail = url.slice(-1)+trail; url = url.slice(0,-1); }
    return '<a href="'+url+'" target="_blank" rel="noopener noreferrer">'+url+'</a>'+trail;
  });
  s = s.replace(/\*\*([^*\n]+)\*\*/g,"<strong>$1</strong>");
  // 下划线强调必须落在词边界上：否则 mcp__context7__query-docs 会被渲染成 mcpcontext7query-docs
  s = s.replace(/(^|[\s(（"'>])__([^_\n]+)__(?=$|[\s)）"'<.,，。；;:：!！?？])/g,"$1<strong>$2</strong>");
  s = s.replace(/(^|[\s(（])\*([^*\n]+)\*(?=$|[\s).,，。；;:：!！?？])/g,"$1<em>$2</em>");
  s = s.replace(/~~([^~\n]+)~~/g,"<del>$1</del>");
  s = s.replace(/\u0002(\d+)\u0002/g,function(m,i){
    var l = links[+i]; if(!l) return "";
    return '<a href="'+l.url+'" target="_blank" rel="noopener noreferrer">'+l.label+'</a>';
  });
  s = s.replace(/\u0001(\d+)\u0001/g,function(m,i){
    return '<code class="inline">'+codes[+i]+'</code>';
  });
  return s;
}
function splitTableRow(line){
  var s = line.trim();
  if (s.charAt(0)==="|") s = s.slice(1);
  if (s.charAt(s.length-1)==="|") s = s.slice(0,-1);
  return s.split("|").map(function(c){ return c.trim(); });
}
function diffHtml(text){
  if (!text) return "";
  var lines = String(text).split("\n");
  var html = "", oldNo = 0, newNo = 0;
  for (var i=0;i<lines.length;i++){
    var l = lines[i], cls = "diff-ctx", on = "", nn = "";
    if (l.indexOf("@@")===0){
      var m = l.match(/@@\s*-(\d+)(?:,\d+)?\s*\+(\d+)/);
      if (m){ oldNo = parseInt(m[1],10); newNo = parseInt(m[2],10); }
      html += '<span class="diff-hunk">'+esc(l)+'\n</span>';
      continue;
    }
    if (l.indexOf("+++")===0 || l.indexOf("---")===0 || l.indexOf("diff ")===0 || l.indexOf("index ")===0){
      html += '<span class="diff-hunk">'+esc(l)+'\n</span>';
      continue;
    }
    if (l.charAt(0) === "+"){ cls = "diff-add"; nn = String(newNo++); }
    else if (l.charAt(0) === "-"){ cls = "diff-del"; on = String(oldNo++); }
    else { on = String(oldNo++); nn = String(newNo++); }
    html += '<span class="'+cls+'"><i class="dl-no">'+on+'</i><i class="dl-no">'+nn+'</i><b>'+esc(l)+'</b>\n</span>';
  }
  return html;
}

export { HL_KEYWORDS, HL_LINE_COMMENT, HL_TPL, HL_TYPES, jsonDepth, hlConf, highlightCode, codeBlockHtml, inlineMd, splitTableRow, diffHtml };
