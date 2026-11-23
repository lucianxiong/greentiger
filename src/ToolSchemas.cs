using System;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GreenTiger
{
    /// <summary>
    /// 原生 function calling 的工具 Schema 目录（OpenAI tools 格式）。
    ///
    /// 以前工具只以文本形式写在系统提示里（```greentiger 围栏协议），API 大脑只能靠「照抄格式」发起调用；
    /// 现在把每个工具的参数变成结构化 JSON Schema 随请求下发，模型走 message.tool_calls 原生通道：
    /// 参数类型有约束、支持一次并行发起多个调用、token 用量可精确计量。
    /// 网页版大脑不支持 tools 字段，继续走围栏协议——两套协议并存，Schema 目录只服务 API 通道。
    /// </summary>
    public static class ToolSchemas
    {
        /// <summary>
        /// 生成当前模式可用的工具 Schema 列表。
        /// plan 模式只下发只读工具（与 Agent.ModeAllows 的白名单保持一致）；
        /// MCP 工具由调用方通过 AppendMcp 合并进来（Schema 由各 MCP 服务器声明）。
        /// </summary>
        public static List<object> Build(bool planMode)
        {
            List<object> tools = new List<object>();
            if (planMode)
            {
                Add(tools, "read_file", "读取工作目录内的文本文件，输出带行号。大文件先用 outline=true 拿文档索引，再用 offset/limit 分段读。",
                    obj(),
                    req("path"),
                    prop("path", "string", "文件路径（相对工作目录或绝对路径）", null),
                    prop("outline", "boolean", "true=只返回章节/标题/代码符号与行号索引，不返回正文（长文档第一步）", null),
                    prop("offset", "number", "起始行号（从 1 开始）", null),
                    prop("limit", "number", "读取的行数，默认 2000", null));
                Add(tools, "repo_map", "仓库结构概览：两层目录树 + 每目录文件数/大小/最近修改。开始干活前先看一眼整体结构，再决定读哪些文件；不要连环 list_dir 自己拼结构。symbols=true 时逐源码文件列出顶级符号（类/函数带行号），回答「某功能在哪个文件」类问题先用它。",
                    obj(),
                    prop("symbols", "boolean", "true = 符号深度模式（列每个源码文件的类/函数与行号）", null));
                Add(tools, "list_dir", "列出目录内容。", obj(),
                    req("path"), prop("path", "string", "目录路径，默认工作目录", null));
                Add(tools, "glob", "按通配符查找文件（如 **/*.cs），按修改时间倒序。定位文件用它，不要用 shell 再自己筛。",
                    obj(),
                    req("pattern"),
                    prop("pattern", "string", "通配符模式", null),
                    prop("path", "string", "起始目录，默认工作目录", null),
                    prop("limit", "number", "最多返回条数，默认 200", null));
                Add(tools, "grep", "在文件内容里做正则检索，返回 文件:行号: 内容。查符号、报错文本、配置项先用它。",
                    obj(),
                    req("pattern"),
                    prop("pattern", "string", "正则表达式", null),
                    prop("include", "string", "文件名过滤通配符，如 *.cs", null),
                    prop("path", "string", "起始目录，默认工作目录", null),
                    prop("ignore_case", "boolean", "忽略大小写", null),
                    prop("max_results", "number", "最多返回条数，默认 100", null));
                Add(tools, "web_fetch", "抓取网页并转成可读正文（推荐读文章/文档时用；要原始 HTML 或下文件用 http_get）。", obj(),
                    req("url"),
                    prop("url", "string", "目标 URL", null),
                    prop("max_chars", "number", "正文字符上限，默认 16000", null));
                Add(tools, "ask_user", "需要用户在多个方案间拍板时，弹出选项卡让用户选择（规划模式讨论方案时尤其有用）。", obj(),
                    req("question", "options"),
                    prop("question", "string", "要问用户的问题", null),
                    AskOptionsProp());
            Add(tools, "http_get", "HTTP GET 读取网页/下载文件。", obj(),
                    req("url"),
                    prop("url", "string", "目标 URL", null),
                    prop("save_to", "string", "保存到工作目录的相对路径（不填只返回内容）", null),
                    prop("max_bytes", "number", "最大下载字节数", null));
                Add(tools, "web_search", "网页搜索，用于查找资料、核实信息、收集来源。", obj(),
                    req("query"),
                    prop("query", "string", "搜索词", null),
                    prop("count", "number", "结果条数，默认 6", null));
                Add(tools, "wait", "等待若干秒（轮询外部状态时使用）。", obj(),
                    prop("seconds", "number", "等待秒数，默认 1", null));
                Add(tools, "finish", "声明任务完成（系统仍会做独立验收）。", obj(),
                    prop("summary", "string", "给用户的完成总结", null));
                Add(tools, "everything_search", "Everything 全盘极速搜索（只读）。找本机文件用向它。", obj(),
                    req("query"),
                    prop("query", "string", "Everything 搜索语法，如 ext:pdf size:>100mb", null),
                    prop("limit", "number", "最多返回条数，默认 100", null));
                Add(tools, "pc_scan", "本机风险扫描（只读，本地可解释启发式引擎）：缓存与 AI 模型占用、大文件、安装包、开机自启。", obj(),
                    prop("scopes", "string", "逗号分隔：caches,large,dup,installer,startup", null));
                Add(tools, "pc_disk_summary", "固定磁盘空间概览（只读）。", obj());
                Add(tools, "pc_startup_list", "开机自启清单（HKCU/HKLM Run，只读）。", obj());
                Add(tools, "pc_shred_preview", "粉碎前逐路径预览（只读，不删除）。", obj(),
                    req("paths"),
                    prop("paths", "array", "要预览的绝对文件路径列表", strArr()));
                Add(tools, "pc_uninstall_list", "列出本机已安装程序（只读，Windows 标准卸载注册表）。", obj());
                return tools;
            }

            Add(tools, "ask_user", "需要用户拍板时弹出选项卡让用户选择。只在真正需要用户决策、自己无法判断时使用（每个任务最多一两次），用户跳过时基于已有信息继续。", obj(),
                req("question", "options"),
                prop("question", "string", "要问用户的问题", null),
                AskOptionsProp());
            Add(tools, "shell", "执行 PowerShell 命令。命令必须非交互式（不要 pause/Read-Host 等会卡住的操作）。读文件用 read_file、找文件用 glob/grep，不要用 shell 代劳。失败时读懂错误再换方法，同一失败命令不要重复超过两次。dev server / watch / 长构建这类不结束的命令用 background=true。",
                obj(),
                req("command"),
                prop("command", "string", "要执行的 PowerShell 命令", null),
                prop("timeout", "number", "超时秒数，默认 120（background=true 时忽略）", null),
                prop("cwd", "string", "工作目录（相对工作目录），默认工作目录", null),
                prop("background", "boolean", "true=后台执行：立刻返回 id，用 shell_output 轮询输出、shell_kill 终止。适合 dev server / watch / 长构建", null));
            Add(tools, "shell_output", "查看后台 shell 的当前输出与运行状态（id 省略且只有一个在跑时自动选中）。轮询 dev server 启动是否就绪就用它。",
                obj(),
                prop("id", "string", "后台 shell 的 id（如 bg_1）", null));
            Add(tools, "shell_kill", "终止一个后台 shell 及其整棵子进程树。", obj(),
                req("id"),
                prop("id", "string", "后台 shell 的 id（如 bg_1）", null));
            Add(tools, "read_file", "读取工作目录内的文本文件，输出带行号。大文件先用 outline=true 拿文档索引，再用 offset/limit 分段读。",
                obj(),
                req("path"),
                prop("path", "string", "文件路径", null),
                prop("outline", "boolean", "true=只返回章节/标题/代码符号与行号索引，不返回正文（长文档第一步）", null),
                prop("offset", "number", "起始行号（从 1 开始）", null),
                prop("limit", "number", "读取的行数，默认 2000", null));
            Add(tools, "write_file", "写入/覆盖工作目录内的文本文件。改已有文件优先用 edit；不要用 write_file 重写整个文件。",
                obj(),
                req("path", "content"),
                prop("path", "string", "文件路径", null),
                prop("content", "string", "完整文件内容", null),
                prop("append", "boolean", "true 追加到文件末尾，默认覆盖", null));
            Add(tools, "edit", "对已读过的文件做精确字符串替换：把 old_string 换成 new_string。改之前必须先 read_file；old_string 必须在文件中唯一，多处命中会报错。",
                obj(),
                req("path", "old_string", "new_string"),
                prop("path", "string", "文件路径", null),
                prop("old_string", "string", "要替换的原文片段（带足上下文保证唯一）", null),
                prop("new_string", "string", "替换后的内容", null),
                prop("replace_all", "boolean", "替换所有出现，默认 false", null));
            Add(tools, "multiedit", "对同一文件按顺序应用多个替换（一次读/一次快照/一次写盘）。同一文件要改多处时用它，不要连续发多个 edit。全部成功才写入；任一失败整批取消。",
                obj(),
                req("path", "edits"),
                prop("path", "string", "文件路径", null),
                prop("edits", "array", "替换列表 [{old_string, new_string, replace_all?}]，按顺序应用", objEditArr()));
            Add(tools, "glob", "按通配符查找文件（如 **/*.cs），按修改时间倒序。定位文件用它。", obj(),
                req("pattern"),
                prop("pattern", "string", "通配符模式", null),
                prop("path", "string", "起始目录，默认工作目录", null),
                prop("limit", "number", "最多返回条数，默认 200", null));
            Add(tools, "grep", "在文件内容里做正则检索，返回 文件:行号: 内容。", obj(),
                req("pattern"),
                prop("pattern", "string", "正则表达式", null),
                prop("include", "string", "文件名过滤通配符，如 *.cs", null),
                prop("path", "string", "起始目录，默认工作目录", null),
                prop("ignore_case", "boolean", "忽略大小写", null),
                prop("max_results", "number", "最多返回条数，默认 100", null));
            Add(tools, "list_dir", "列出目录内容。", obj(),
                prop("path", "string", "目录路径，默认工作目录", null));
            Add(tools, "screenshot", "截取全屏或指定窗口，返回图片。", obj(),
                prop("mode", "string", "screen（全屏）或 window（指定窗口）", null),
                prop("title", "string", "窗口标题的一部分（mode=window 时必填）", null),
                prop("hide_ui", "boolean", "截图时隐藏悬浮提示", null),
                prop("quality", "number", "JPEG 质量 1-100，默认 82", null));
            Add(tools, "mouse", "鼠标控制：移动、点击、滚轮。", obj(),
                req("action"),
                prop("action", "string", "click / double_click / right_click / move / scroll", null),
                prop("x", "number", "屏幕 X 坐标", null),
                prop("y", "number", "屏幕 Y 坐标", null),
                prop("count", "number", "点击次数，默认 1", null),
                prop("amount", "number", "滚动量，默认 3", null),
                prop("button", "string", "click 用的鼠标键：left / right（默认按 action 推断）", null));
            Add(tools, "keyboard", "键盘输入：输入文本或发送组合键。", obj(),
                req("action"),
                prop("action", "string", "text（输入文本）或 keys（组合键）", null),
                prop("text", "string", "action=text 时要输入的文本", null),
                prop("keys", "string", "action=keys 时的组合键，如 CTRL+S", null),
                prop("window", "string", "先聚焦到标题匹配的窗口（可选）", null));
            Add(tools, "focus_window", "按标题把窗口切到前台。", obj(),
                req("title"), prop("title", "string", "窗口标题的一部分", null));
            Add(tools, "upload_file", "把本地文件作为附件上传给大脑。", obj(),
                req("path"), prop("path", "string", "文件路径", null));
            Add(tools, "http_get", "HTTP GET 读取网页/下载文件。", obj(),
                req("url"),
                prop("url", "string", "目标 URL", null),
                prop("save_to", "string", "保存到工作目录的相对路径（不填只返回内容）", null),
                prop("max_bytes", "number", "最大下载字节数", null));
            Add(tools, "web_search", "网页搜索，用于查找资料、核实信息、收集来源。", obj(),
                req("query"),
                prop("query", "string", "搜索词", null),
                prop("count", "number", "结果条数，默认 6", null));
            Add(tools, "clipboard", "读取或写入系统剪贴板。", obj(),
                req("action"),
                prop("action", "string", "get 或 set", null),
                prop("text", "string", "action=set 时写入的文本", null));
            Add(tools, "wait", "等待若干秒（轮询外部状态时使用）。", obj(),
                prop("seconds", "number", "等待秒数，默认 1", null));
            Add(tools, "finish", "声明任务完成（系统仍会做独立验收，未通过会被驳回修复）。", obj(),
                prop("summary", "string", "给用户的完成总结：做了什么、改了哪些文件、如何验证", null));
            Add(tools, "plan", "多步任务开始前先提交执行计划，等待用户批准后再继续。任务预计 3 步以上、要改多个文件、或有不可逆操作时用它；纯问答和一步能做完的事不要用。",
                obj(),
                req("title", "steps"),
                prop("title", "string", "计划标题", null),
                prop("steps", "array", "步骤列表（一步一件，用动作而不是愿望描述）", strArr()));
            Add(tools, "todo", "维护当前进度：每完成/开始一步就更新一次，用户界面按这份清单显示你在哪一步。",
                obj(),
                req("items"),
                prop("items", "array", "步骤对象列表 {title, status: pending|doing|done}", objArr()));
            Add(tools, "everything_search", "Everything 全盘极速搜索（文件名/扩展名/大小语法，索引实时）。需要找本机文件时先用它，不要用 shell 遍历磁盘。", obj(),
                req("query"),
                prop("query", "string", "Everything 搜索语法，如 ext:pdf size:>100mb 或文件名关键词", null),
                prop("limit", "number", "最多返回条数，默认 100，上限 300", null),
                prop("sort", "string", "可选排序：size-descending / date-modified-descending / path-ascending", null));
            Add(tools, "pc_scan", "本机风险扫描（本地可解释启发式引擎，非杀毒）：缓存与 AI 模型占用、大文件、下载目录安装包、重复文件、开机自启。", obj(),
                prop("scopes", "string", "逗号分隔范围：caches,large,dup,installer,startup，默认 caches,large,installer,startup", null));
            Add(tools, "pc_disk_summary", "固定磁盘空间概览：各盘剩余/总量（只读，工具箱体检页同源）。", obj());
            Add(tools, "pc_startup_list", "开机自启完整清单（HKCU/HKLM Run，只读）。看开机变慢时先查它。", obj());
            Add(tools, "pc_shred_preview", "粉碎前预览：逐路径返回能否粉碎、大小与拒绝原因，不删除任何文件。pc_shred 之前先用它核对。", obj(),
                req("paths"),
                prop("paths", "array", "要预览的绝对文件路径列表", strArr()));
            Add(tools, "pc_uninstall_list", "列出本机已安装程序（Windows 标准卸载注册表）。卸载前先用它核对名称。", obj());
            Add(tools, "pc_uninstall_launch", "启动某个已安装软件的标准卸载器（与控制面板卸载一致）。高危操作，用户会收到确认弹窗。", obj(),
                req("name"),
                prop("name", "string", "pc_uninstall_list 输出的完整程序名", null));
            Add(tools, "pc_clean_cache", "清理 pc_scan 发现的白名单缓存目录（仅限其输出里列出的 finding_id，防止任意路径删除）。占用中的文件自动跳过。", obj(),
                req("finding_id"),
                prop("finding_id", "string", "pc_scan 输出里的清理 id", null));
            Add(tools, "pc_shred", "文件粉碎：3 趟覆写（0x00→0xFF→随机）+ 改名 + 删除，不可恢复。系统/程序目录硬拒绝。高危操作，用户会收到确认弹窗。", obj(),
                req("paths"),
                prop("paths", "array", "要粉碎的绝对路径列表（每项一个文件）", strArr()),
                prop("passes", "number", "覆写趟数，默认 3，上限 7", null));
            Add(tools, "pc_launch_app", "启动本机程序：everything（搜索服务）/ hibit（HiBit Uninstaller 深度卸载清理）/ lightc（C 盘与 AI 缓存深度分析）。", obj(),
                req("app"),
                prop("app", "string", "everything 或 hibit 或 lightc", null));
            Add(tools, "save_memory", "把一条重要事实/偏好/坑写入长期记忆（下次任务开始自动带上）。跨项目通用的写 global；只与本工作目录有关的写 project。", obj(),
                req("content"),
                prop("content", "string", "要记住的内容（一句话到几句话）", null),
                new KeyValuePair<string, Dictionary<string, object>>("scope", ScopeProp()));
            Add(tools, "task", "并行派发只读分析子代理：每个子代理只有 read_file/list_dir/glob/grep/repo_map/http_get/web_fetch/web_search/everything_search/pc_scan/pc_uninstall_list，无写权限、无 shell、无 MCP，单任务 12 步 + 8 分钟上限，结论回喂给你。", obj(),
                req("tasks"),
                prop("tasks", "array", "子任务指令列表，1-4 条，每条是一段自包含的只读调查说明（要查什么、产出什么结论）", strArr()));
            return tools;
        }

        /// <summary>按名字白名单过滤 Schema 列表（子代理只下发它真正可用的工具）。</summary>
        public static List<object> Filter(List<object> schemas, HashSet<string> allowed)
        {
            List<object> result = new List<object>();
            if (schemas == null || allowed == null) return result;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < schemas.Count; i++)
            {
                Dictionary<string, object> tool = schemas[i] as Dictionary<string, object>;
                if (tool == null) continue;
                Dictionary<string, object> fn = tool.ContainsKey("function") ? tool["function"] as Dictionary<string, object> : null;
                if (fn == null) continue;
                string name = fn.ContainsKey("name") ? Convert.ToString(fn["name"]) : "";
                if (name.Length > 0 && allowed.Contains(name) && seen.Add(name)) result.Add(tool);
            }
            return result;
        }

        /// <summary>MCP 工具的 Schema：参数由远端服务器声明，这里按 mcp__ 前缀透传。</summary>
        public static void AppendMcp(List<object> tools, IList<KeyValuePair<string, string>> mcpTools)
        {
            if (mcpTools == null) return;
            for (int i = 0; i < mcpTools.Count; i++)
            {
                Dictionary<string, object> fn = new Dictionary<string, object>();
                fn["name"] = mcpTools[i].Key;
                fn["description"] = string.IsNullOrEmpty(mcpTools[i].Value) ? "MCP 远端工具" : mcpTools[i].Value;
                fn["parameters"] = AnyParams();
                Dictionary<string, object> tool = new Dictionary<string, object>();
                tool["type"] = "function";
                tool["function"] = fn;
                tools.Add(tool);
            }
        }

        // ---------- 构造辅助（保持 C# 5 语法兼容，不用字典初始化器简写） ----------

        /// <summary>
        /// parts 里允许两种元素：string[]（来自 req(...)，必填参数名）与
        /// KeyValuePair&lt;string, Dictionary&gt;（来自 prop(...)，属性定义）。统一在词法上兼容
        /// 「有 required / 无 required（无必填参数的工具）」两种调用形态。
        /// </summary>
        private static void Add(List<object> tools, string name, string description,
            Dictionary<string, object> _shape, params object[] parts)
        {
            Dictionary<string, object> parametersObj = new Dictionary<string, object>();
            parametersObj["type"] = "object";
            parametersObj["additionalProperties"] = false;   // strict 本地校验的 Schema 侧声明：未声明参数一律拒绝
            Dictionary<string, object> props = new Dictionary<string, object>();
            List<string> required = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string[] names = parts[i] as string[];
                if (names != null)
                {
                    for (int k = 0; k < names.Length; k++) required.Add(names[k]);
                    continue;
                }
                KeyValuePair<string, Dictionary<string, object>> propEntry;
                try { propEntry = (KeyValuePair<string, Dictionary<string, object>>)parts[i]; }
                catch { continue; }
                props[propEntry.Key] = propEntry.Value;
            }
            parametersObj["properties"] = props;
            if (required.Count > 0) parametersObj["required"] = required.ToArray();

            Dictionary<string, object> fn = new Dictionary<string, object>();
            fn["name"] = name;
            fn["description"] = description;
            fn["parameters"] = parametersObj;

            Dictionary<string, object> tool = new Dictionary<string, object>();
            tool["type"] = "function";
            tool["function"] = fn;
            tools.Add(tool);
        }

        private static string[] req(params string[] names) { return names; }

        /// <summary>save_memory.scope 的枚举声明（strict 本地校验会拦截越界值）。</summary>
        private static Dictionary<string, object> ScopeProp()
        {
            Dictionary<string, object> p = new Dictionary<string, object>();
            p["type"] = "string";
            p["description"] = "project（默认，当前工作目录）或 global（跨项目）";
            p["enum"] = new string[] { "project", "global" };
            return p;
        }

        private static KeyValuePair<string, Dictionary<string, object>> prop(string name, string type, string desc, object _)
        {
            Dictionary<string, object> p = new Dictionary<string, object>();
            p["type"] = type;
            if (!string.IsNullOrEmpty(desc)) p["description"] = desc;
            // 第 4 参数传 strArr()/objArr()/objEditArr() 时是完整数组形状（含 items）——
            // 3.19.0 之前这个参数被忽略，数组项 Schema 从未下发给 API 也没进本地校验（潜伏 bug，已修）。
            Dictionary<string, object> extra = _ as Dictionary<string, object>;
            if (extra != null)
            {
                foreach (KeyValuePair<string, object> kv in extra) p[kv.Key] = kv.Value;
            }
            return new KeyValuePair<string, Dictionary<string, object>>(name, p);
        }

        // ---------- strict 本地校验（3.19.0） ----------

        private static Dictionary<string, Dictionary<string, object>> _paramMap;
        private static readonly object MapGate = new object();

        /// <summary>工具名 → 参数 Schema。与 API 下发共用同一份 Build 构建（唯一事实源），本地校验不另写第二套规格。</summary>
        public static Dictionary<string, Dictionary<string, object>> ParameterMap()
        {
            if (_paramMap != null) return _paramMap;
            lock (MapGate)
            {
                if (_paramMap != null) return _paramMap;
                Dictionary<string, Dictionary<string, object>> map = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                List<object> tools = Build(false);
                for (int i = 0; i < tools.Count; i++)
                {
                    Dictionary<string, object> tool = tools[i] as Dictionary<string, object>;
                    if (tool == null) continue;
                    Dictionary<string, object> fn = tool["function"] as Dictionary<string, object>;
                    if (fn == null) continue;
                    string toolName = fn["name"] as string;
                    Dictionary<string, object> parameters = fn["parameters"] as Dictionary<string, object>;
                    if (string.IsNullOrEmpty(toolName) || parameters == null) continue;
                    map[toolName] = parameters;
                }
                _paramMap = map;
                return map;
            }
        }

        /// <summary>
        /// 按下发 Schema 严格校验工具参数：未知键拒绝（additionalProperties:false 的本地执行版）、
        /// 类型不符拒绝、枚举越界拒绝、数组/对象递归进 items/properties（含嵌套 required）。
        /// 文本围栏降级路径没有服务商侧约束解码，这里是两条协议共用的最后一道闸。
        /// 返回 false 时 error 为可直接回喂模型的完整错误（附正确输入示例）。
        /// </summary>
        public static bool ValidateAgainstSchema(string toolName, Dictionary<string, object> args, out string error)
        {
            error = "";
            Dictionary<string, object> parameters;
            if (!ParameterMap().TryGetValue(toolName, out parameters) || parameters == null) return true;   // 无 Schema（MCP 等）：不做严格校验
            Dictionary<string, object> props = PropsOf(parameters);
            foreach (KeyValuePair<string, object> kv in args)
            {
                if (kv.Value == null) continue;   // 可选参数 null 视为未提供；必填 null 在 Validate 必填环已拦截
                if (!props.ContainsKey(kv.Key))
                {
                    error = "工具「" + toolName + "」不接受参数「" + kv.Key + "」。允许的参数：" + string.Join(", ", new List<string>(props.Keys).ToArray()) +
                            "。" + ExampleHint(toolName, parameters);
                    return false;
                }
                string fail;
                Dictionary<string, object> propSchema = props[kv.Key] as Dictionary<string, object>;
                if (propSchema == null) continue;
                if (!ValidateValue(toolName, kv.Key, propSchema, kv.Value, out fail))
                {
                    error = fail + ExampleHint(toolName, parameters);
                    return false;
                }
            }
            return true;
        }

        private static Dictionary<string, object> PropsOf(Dictionary<string, object> schema)
        {
            object o;
            Dictionary<string, object> props = schema.TryGetValue("properties", out o) ? o as Dictionary<string, object> : null;
            return props != null ? props : new Dictionary<string, object>();
        }

        private static bool ValidateValue(string toolName, string path, Dictionary<string, object> schema, object value, out string error)
        {
            error = "";
            string type = schema.ContainsKey("type") ? schema["type"] as string : null;
            object enumObj;
            if (schema.TryGetValue("enum", out enumObj) && enumObj is IEnumerable)
            {
                string s = value as string;
                bool found = false;
                List<string> options = new List<string>();
                foreach (object o in (IEnumerable)enumObj)
                {
                    string os = o as string;
                    if (os != null) options.Add(os);
                    if (string.Equals(os, s, StringComparison.Ordinal)) found = true;
                }
                if (!found)
                {
                    error = "工具「" + toolName + "」的参数「" + path + "」必须是 " + string.Join("|", options.ToArray()) +
                            " 之一，收到 " + TypeLabel(value) + "：" + ShortValue(value) + "。";
                    return false;
                }
            }
            if (!string.IsNullOrEmpty(type) && !TypeMatches(type, value))
            {
                error = "工具「" + toolName + "」的参数「" + path + "」需要 " + JsonTypeName(type) +
                        "，收到 " + TypeLabel(value) + "：" + ShortValue(value) + "。";
                return false;
            }
            if (type == "array")
            {
                object itemsObj;
                if (!schema.TryGetValue("items", out itemsObj)) return true;
                Dictionary<string, object> items = itemsObj as Dictionary<string, object>;
                if (items == null) return true;
                int index = 0;
                foreach (object item in (IEnumerable)value)
                {
                    string itemFail;
                    if (!ValidateValue(toolName, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", items, item, out itemFail)) { error = itemFail; return false; }
                    index++;
                }
                return true;
            }
            if (type == "object")
            {
                Dictionary<string, object> dict = value as Dictionary<string, object>;
                if (dict == null) return true;
                object reqObj;
                if (schema.TryGetValue("required", out reqObj) && reqObj is IEnumerable)
                {
                    foreach (object o in (IEnumerable)reqObj)
                    {
                        string rname = o as string;
                        if (string.IsNullOrEmpty(rname)) continue;
                        if (!dict.ContainsKey(rname) || dict[rname] == null)
                        {
                            error = "工具「" + toolName + "」的参数「" + path + "」缺少必填字段「" + rname + "」。";
                            return false;
                        }
                    }
                }
                Dictionary<string, object> iprops = PropsOf(schema);
                if (iprops.Count > 0)
                {
                    foreach (KeyValuePair<string, object> kv in dict)
                    {
                        if (kv.Value == null) continue;
                        if (!iprops.ContainsKey(kv.Key))
                        {
                            error = "工具「" + toolName + "」的参数「" + path + "」不接受字段「" + kv.Key + "」。允许：" +
                                    string.Join(", ", new List<string>(iprops.Keys).ToArray()) + "。";
                            return false;
                        }
                        Dictionary<string, object> ps = iprops[kv.Key] as Dictionary<string, object>;
                        if (ps == null) continue;
                        string subFail;
                        if (!ValidateValue(toolName, path + "." + kv.Key, ps, kv.Value, out subFail)) { error = subFail; return false; }
                    }
                }
            }
            return true;
        }

        private static bool TypeMatches(string type, object value)
        {
            if (type == "string") return value is string;
            if (type == "boolean") return value is bool;
            if (type == "number") return value is int || value is long || value is double || value is float || value is decimal || value is short || value is byte;
            if (type == "integer") return value is int || value is long || value is short || value is byte;
            if (type == "array") return value is IEnumerable && !(value is string) && !(value is Dictionary<string, object>);
            if (type == "object") return value is Dictionary<string, object>;
            return true;
        }

        private static string JsonTypeName(string type)
        {
            if (type == "string") return "字符串";
            if (type == "boolean") return "布尔值";
            if (type == "number" || type == "integer") return "数字";
            if (type == "array") return "数组";
            if (type == "object") return "对象";
            return type;
        }

        private static string TypeLabel(object value)
        {
            if (value is string) return "字符串";
            if (value is bool) return "布尔值";
            if (value is int || value is long || value is double || value is float || value is decimal || value is short || value is byte) return "数字";
            if (value is IEnumerable) return "数组";
            if (value is Dictionary<string, object>) return "对象";
            return value == null ? "null" : value.GetType().Name;
        }

        private static string ShortValue(object value)
        {
            try
            {
                string s = Json.Stringify(value);
                if (s.Length > 80) s = s.Substring(0, 80) + "…";
                return s;
            }
            catch { return ""; }
        }

        /// <summary>从 Schema 生成正确输入示例（必填参数按类型给占位值；可选参数只列名字）。</summary>
        private static string ExampleHint(string toolName, Dictionary<string, object> parameters)
        {
            try
            {
                if (parameters == null)
                {
                    Dictionary<string, Dictionary<string, object>> map = ParameterMap();
                    if (!map.TryGetValue(toolName, out parameters) || parameters == null) return "";
                }
                Dictionary<string, object> props = PropsOf(parameters);
                if (props.Count == 0) return "";
                List<string> required = new List<string>();
                object reqObj;
                if (parameters.TryGetValue("required", out reqObj) && reqObj is IEnumerable)
                {
                    foreach (object o in (IEnumerable)reqObj)
                    {
                        string s = o as string;
                        if (!string.IsNullOrEmpty(s)) required.Add(s);
                    }
                }
                StringBuilder sb = new StringBuilder(" 示例：args = {");
                bool first = true;
                List<string> optional = new List<string>();
                foreach (string key in props.Keys)
                {
                    if (required.Contains(key))
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        sb.Append(key + ": " + Placeholder(props[key]));
                    }
                    else optional.Add(key);
                }
                sb.Append("}");
                if (optional.Count > 0) sb.Append("（可选：" + string.Join(", ", optional.ToArray()) + "）");
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string Placeholder(object propSchema)
        {
            Dictionary<string, object> p = propSchema as Dictionary<string, object>;
            string type = p != null && p.ContainsKey("type") ? p["type"] as string : "string";
            if (type == "number" || type == "integer") return "0";
            if (type == "boolean") return "true";
            if (type == "array") return "[]";
            if (type == "object") return "{}";
            return "\"...\"";
        }

        private static Dictionary<string, object> obj() { return new Dictionary<string, object>(); }

        private static Dictionary<string, object> strArr()
        {
            Dictionary<string, object> p = new Dictionary<string, object>();
            p["type"] = "string";
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["type"] = "array";
            a["items"] = p;
            return a;
        }

        private static Dictionary<string, object> objArr()
        {
            Dictionary<string, object> item = new Dictionary<string, object>();
            item["type"] = "object";
            Dictionary<string, object> props = new Dictionary<string, object>();
            Dictionary<string, object> title = new Dictionary<string, object>();
            title["type"] = "string";
            props["title"] = title;
            Dictionary<string, object> status = new Dictionary<string, object>();
            status["type"] = "string";
            status["enum"] = new string[] { "pending", "doing", "done" };
            props["status"] = status;
            item["properties"] = props;
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["type"] = "array";
            a["items"] = item;
            return a;
        }

        private static Dictionary<string, object> objEditArr()
        {
            Dictionary<string, object> item = new Dictionary<string, object>();
            item["type"] = "object";
            Dictionary<string, object> props = new Dictionary<string, object>();
            Dictionary<string, object> oldS = new Dictionary<string, object>();
            oldS["type"] = "string";
            props["old_string"] = oldS;
            Dictionary<string, object> newS = new Dictionary<string, object>();
            newS["type"] = "string";
            props["new_string"] = newS;
            Dictionary<string, object> ra = new Dictionary<string, object>();
            ra["type"] = "boolean";
            props["replace_all"] = ra;
            item["properties"] = props;
            item["required"] = new string[] { "old_string", "new_string" };
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["type"] = "array";
            a["items"] = item;
            return a;
        }

        /// <summary>ask_user 的 options 参数：数组项是 {label, description} 对象——给足结构，小模型才不会乱填。</summary>
        private static KeyValuePair<string, Dictionary<string, object>> AskOptionsProp()
        {
            Dictionary<string, object> item = new Dictionary<string, object>();
            item["type"] = "object";
            Dictionary<string, object> props = new Dictionary<string, object>();
            Dictionary<string, object> label = new Dictionary<string, object>();
            label["type"] = "string";
            label["description"] = "选项文本（短，直接可点）";
            props["label"] = label;
            Dictionary<string, object> desc = new Dictionary<string, object>();
            desc["type"] = "string";
            desc["description"] = "这个选项的含义/代价，一句即可";
            props["description"] = desc;
            item["properties"] = props;
            item["required"] = new string[] { "label" };
            Dictionary<string, object> arr = new Dictionary<string, object>();
            arr["type"] = "array";
            arr["description"] = "2-4 个互斥选项";
            arr["items"] = item;
            return new KeyValuePair<string, Dictionary<string, object>>("options", arr);
        }
        private static Dictionary<string, object> AnyParams()
        {
            Dictionary<string, object> p = new Dictionary<string, object>();
            p["type"] = "object";
            return p;
        }
    }
}
