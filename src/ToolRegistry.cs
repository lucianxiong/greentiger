using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GreenTiger
{
    /// <summary>
    /// 工具元数据。以前工具是 ExecuteAsync 里的一串 if，能力、风险、超时、重试全散落在调用点；
    /// 这里把「这个工具是什么、有多危险、能不能重试」变成可查询的数据。
    /// </summary>
    public class ToolSpec
    {
        public string Name { get; set; }
        public string Summary { get; set; }
        /// <summary>safe（只读/无副作用）/ normal（可能改本机文件）/ danger（高风险，强制审批）。</summary>
        public string RiskLevel { get; set; }
        /// <summary>额外超时秒数，0 表示由工具自身或调用参数决定。</summary>
        public int TimeoutSeconds { get; set; }
        /// <summary>自动重试上限。只对幂等工具有意义。</summary>
        public int MaxRetries { get; set; }
        /// <summary>同样参数重复执行是否安全（决定能否自动重放）。</summary>
        public bool Idempotent { get; set; }
        /// <summary>是否会产生本机/远端副作用（决定是否需要幂等键与状态核验）。</summary>
        public bool WritesExternal { get; set; }
        public string[] RequiredArgs { get; set; }

        public ToolSpec()
        {
            Name = "";
            Summary = "";
            RiskLevel = "normal";
            TimeoutSeconds = 0;
            MaxRetries = 0;
            Idempotent = false;
            WritesExternal = false;
            RequiredArgs = new string[0];
        }
    }

    public class ToolValidation
    {
        public bool Ok { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }

        public ToolValidation()
        {
            Ok = true;
            Code = ToolErrorCode.Ok;
            Message = "";
        }

        public static ToolValidation Fail(string code, string message)
        {
            ToolValidation v = new ToolValidation();
            v.Ok = false;
            v.Code = code;
            v.Message = message;
            return v;
        }
    }

    /// <summary>
    /// 工具注册中心：唯一的能力目录 + 参数 Schema 校验入口。
    /// 未注册的工具不会被静默执行；MCP 工具按 mcp__ 前缀放行（Schema 由远端服务器负责）。
    /// </summary>
    public static class ToolRegistry
    {
        private static readonly Dictionary<string, ToolSpec> Specs = Build();

        private static Dictionary<string, ToolSpec> Build()
        {
            Dictionary<string, ToolSpec> map = new Dictionary<string, ToolSpec>(StringComparer.OrdinalIgnoreCase);

            Add(map, new ToolSpec
            {
                Name = "ask_user",
                Summary = "需要用户拍板时弹出选项卡，等待用户选择或输入",
                RiskLevel = "safe",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = false,
                RequiredArgs = new string[] { "question" }
            });
            Add(map, new ToolSpec
            {
                Name = "shell",
                Summary = "执行 PowerShell 命令",
                RiskLevel = "danger",
                TimeoutSeconds = 0,
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = true,
                RequiredArgs = new string[] { "command" }
            });
            Add(map, new ToolSpec
            {
                Name = "read_file",
                Summary = "读取工作目录内的文本文件",
                RiskLevel = "safe",
                MaxRetries = 2,
                Idempotent = true,
                RequiredArgs = new string[] { "path" }
            });
            Add(map, new ToolSpec
            {
                Name = "write_file",
                Summary = "写入/覆盖工作目录内的文件",
                RiskLevel = "normal",
                MaxRetries = 1,
                Idempotent = true,
                WritesExternal = true,
                RequiredArgs = new string[] { "path", "content" }
            });
            Add(map, new ToolSpec
            {
                Name = "edit",
                Summary = "对已读过的文件做精确字符串替换（可批量替换同一处多处出现）",
                RiskLevel = "normal",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = true,
                RequiredArgs = new string[] { "path", "old_string", "new_string" }
            });
            Add(map, new ToolSpec
            {
                Name = "multiedit",
                Summary = "对同一文件按顺序应用多个替换（一次读/一次快照/一次写盘，全部成功才写入）",
                RiskLevel = "normal",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = true,
                RequiredArgs = new string[] { "path", "edits" }
            });
            Add(map, new ToolSpec
            {
                Name = "shell_output",
                Summary = "查看后台 shell 的当前输出与运行状态",
                RiskLevel = "safe",
                MaxRetries = 2,
                Idempotent = true
            });
            Add(map, new ToolSpec
            {
                Name = "shell_kill",
                Summary = "终止一个后台 shell 及其子进程树",
                RiskLevel = "normal",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = true
            });
            Add(map, new ToolSpec
            {
                Name = "glob",
                Summary = "按通配符查找文件（**/*.cs），按修改时间倒序",
                RiskLevel = "safe",
                MaxRetries = 2,
                Idempotent = true,
                RequiredArgs = new string[] { "pattern" }
            });
            Add(map, new ToolSpec
            {
                Name = "grep",
                Summary = "在文件内容里做正则检索，返回 文件:行号: 内容",
                RiskLevel = "safe",
                MaxRetries = 2,
                Idempotent = true,
                RequiredArgs = new string[] { "pattern" }
            });
            Add(map, new ToolSpec
            {
                Name = "repo_map",
                Summary = "仓库结构概览：两层目录树 + 每目录文件数/大小/最近修改",
                RiskLevel = "safe",
                MaxRetries = 2,
                Idempotent = true,
                RequiredArgs = new string[] { }
            });
            Add(map, new ToolSpec
            {
                Name = "list_dir",
                Summary = "列出目录内容",
                RiskLevel = "safe",
                MaxRetries = 2,
                Idempotent = true,
                // path 不是必填：实现里默认 "."（工作目录）。小模型经常只发 {"path":""} 或干脆不发，
                // 卡在参数校验上等于让一个本来能跑的任务白跑一轮。
                RequiredArgs = new string[] { }
            });
            Add(map, new ToolSpec
            {
                Name = "screenshot",
                Summary = "截屏或截取屏幕区域",
                RiskLevel = "safe",
                MaxRetries = 1,
                Idempotent = true,
                WritesExternal = true
            });
            Add(map, new ToolSpec
            {
                Name = "mouse",
                Summary = "鼠标移动/点击",
                // P0：真实键鼠能操作用户已登录的任何应用（网银/邮箱/微信），必须逐次确认
                RiskLevel = "danger",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = true
            });
            Add(map, new ToolSpec
            {
                Name = "keyboard",
                Summary = "键盘输入/组合键",
                RiskLevel = "danger",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = true
            });
            Add(map, new ToolSpec
            {
                Name = "focus_window",
                Summary = "按标题切换前台窗口",
                RiskLevel = "danger",
                MaxRetries = 1,
                Idempotent = true,
                WritesExternal = true
            });
            Add(map, new ToolSpec
            {
                Name = "upload_file",
                Summary = "把本地文件作为附件上传给大脑",
                RiskLevel = "normal",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = true,
                RequiredArgs = new string[] { "path" }
            });
            Add(map, new ToolSpec
            {
                Name = "web_fetch",
                Summary = "抓取网页并转成可读正文（Markdown 风格，15 分钟缓存）",
                RiskLevel = "normal",
                MaxRetries = 2,
                Idempotent = true,
                WritesExternal = false,
                RequiredArgs = new string[] { "url" }
            });
            Add(map, new ToolSpec
            {
                Name = "http_get",
                Summary = "HTTP GET 请求（可保存到工作目录）",
                RiskLevel = "normal",
                MaxRetries = 2,
                Idempotent = true,
                WritesExternal = false,
                RequiredArgs = new string[] { "url" }
            });
            Add(map, new ToolSpec
            {
                Name = "web_search",
                Summary = "网页搜索",
                RiskLevel = "normal",
                MaxRetries = 2,
                Idempotent = true,
                RequiredArgs = new string[] { "query" }
            });
            Add(map, new ToolSpec
            {
                Name = "clipboard",
                Summary = "读取或写入剪贴板",
                RiskLevel = "normal",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = true
            });
            Add(map, new ToolSpec
            {
                Name = "wait",
                Summary = "等待若干秒",
                RiskLevel = "safe",
                MaxRetries = 0,
                Idempotent = true
            });
            Add(map, new ToolSpec
            {
                Name = "finish",
                Summary = "声明任务完成（仍要过独立验收）",
                RiskLevel = "safe",
                MaxRetries = 0,
                Idempotent = true
            });
            Add(map, new ToolSpec
            {
                Name = "plan",
                Summary = "提交执行计划等待用户批准",
                RiskLevel = "safe",
                MaxRetries = 0,
                Idempotent = true
            });
            Add(map, new ToolSpec
            {
                Name = "todo",
                Summary = "更新任务待办进度",
                RiskLevel = "safe",
                MaxRetries = 0,
                Idempotent = true
            });
            Add(map, new ToolSpec
            {
                Name = "everything_search",
                Summary = "Everything 全盘极速搜索（只读）",
                RiskLevel = "safe",
                MaxRetries = 1,
                Idempotent = true,
                RequiredArgs = new string[] { "query" }
            });
            Add(map, new ToolSpec
            {
                Name = "pc_scan",
                Summary = "本机风险扫描（只读启发式引擎）",
                RiskLevel = "safe",
                MaxRetries = 0,
                Idempotent = true
            });
            Add(map, new ToolSpec
            {
                Name = "pc_disk_summary",
                Summary = "固定磁盘空间概览（只读）",
                RiskLevel = "safe",
                MaxRetries = 1,
                Idempotent = true
            });
            Add(map, new ToolSpec
            {
                Name = "pc_startup_list",
                Summary = "开机自启清单（Run 键，只读）",
                RiskLevel = "safe",
                MaxRetries = 0,
                Idempotent = true
            });
            Add(map, new ToolSpec
            {
                Name = "pc_shred_preview",
                Summary = "粉碎前逐路径预览（只读，不删除）",
                RiskLevel = "safe",
                MaxRetries = 0,
                Idempotent = true,
                RequiredArgs = new string[] { "paths" }
            });
            Add(map, new ToolSpec
            {
                Name = "pc_uninstall_list",
                Summary = "列出已安装程序（标准卸载注册表，只读）",
                RiskLevel = "safe",
                MaxRetries = 1,
                Idempotent = true
            });
            Add(map, new ToolSpec
            {
                Name = "pc_uninstall_launch",
                Summary = "启动某软件的标准卸载器（高危）",
                RiskLevel = "danger",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = true,
                RequiredArgs = new string[] { "name" }
            });
            Add(map, new ToolSpec
            {
                Name = "pc_clean_cache",
                Summary = "清理白名单缓存目录（高危）",
                RiskLevel = "danger",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = true,
                RequiredArgs = new string[] { "finding_id" }
            });
            Add(map, new ToolSpec
            {
                Name = "pc_shred",
                Summary = "文件粉碎：3 趟覆写后删除（高危不可恢复）",
                RiskLevel = "danger",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = true,
                RequiredArgs = new string[] { "paths" }
            });
            Add(map, new ToolSpec
            {
                Name = "pc_launch_app",
                Summary = "启动 Everything/HiBit/LightC 本机程序",
                RiskLevel = "normal",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = true,
                RequiredArgs = new string[] { "app" }
            });
            Add(map, new ToolSpec
            {
                Name = "save_memory",
                Summary = "写入长期记忆（项目或全局，下次任务开始自动注入）",
                RiskLevel = "normal",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = false,
                RequiredArgs = new string[] { "content" }
            });
            Add(map, new ToolSpec
            {
                Name = "task",
                Summary = "并行派发只读分析子代理（无写权限无 shell，独立预算与墙钟）",
                RiskLevel = "normal",
                MaxRetries = 0,
                Idempotent = false,
                WritesExternal = false,
                RequiredArgs = new string[] { "tasks" }
            });
            return map;
        }

        private static void Add(Dictionary<string, ToolSpec> map, ToolSpec spec)
        {
            map[spec.Name] = spec;
        }

        public static bool IsMcpTool(string name)
        {
            return name != null && name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>取工具规格；MCP 工具返回一个通用的远端规格，未注册的本地工具返回 null。</summary>
        public static ToolSpec Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            ToolSpec spec;
            if (Specs.TryGetValue(name, out spec)) return spec;
            if (IsMcpTool(name))
            {
                ToolSpec mcp = new ToolSpec();
                mcp.Name = name;
                mcp.Summary = "MCP 远端工具";
                mcp.RiskLevel = "normal";
                mcp.MaxRetries = 0;
                mcp.Idempotent = false;
                mcp.WritesExternal = true;
                return mcp;
            }
            return null;
        }

        public static bool IsRegistered(string name)
        {
            return Get(name) != null;
        }

        public static List<ToolSpec> All()
        {
            List<ToolSpec> list = new List<ToolSpec>(Specs.Values);
            list.Sort(delegate(ToolSpec a, ToolSpec b) { return string.CompareOrdinal(a.Name, b.Name); });
            return list;
        }

        public static string[] Names()
        {
            List<ToolSpec> all = All();
            string[] names = new string[all.Count];
            for (int i = 0; i < all.Count; i++) names[i] = all[i].Name;
            return names;
        }

        /// <summary>
        /// 统一参数校验：工具是否注册、参数是否合法 JSON 对象、必填项是否齐全。
        /// 这是「模型请求未注册工具」「参数不符合 Schema」两道验收的执行点。
        /// </summary>
        public static ToolValidation Validate(string name, string argsJson)
        {
            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
            {
                return ToolValidation.Fail(ToolErrorCode.BadArgs, "工具名称为空。每次工具调用必须给出 tool 字段。");
            }
            ToolSpec spec = Get(name);
            if (spec == null)
            {
                return ToolValidation.Fail(ToolErrorCode.UnknownTool,
                    "未注册的工具「" + name + "」。可用工具：" + string.Join(", ", Names()) + "；MCP 工具用 mcp__ 前缀。");
            }

            Dictionary<string, object> args = null;
            if (!string.IsNullOrEmpty(argsJson) && argsJson.Trim().Length > 0)
            {
                args = Json.ParseObject(argsJson);
                if (args == null)
                {
                    return ToolValidation.Fail(ToolErrorCode.BadArgs,
                        "工具「" + name + "」的 args 不是合法的 JSON 对象，无法执行。");
                }
            }
            else
            {
                args = new Dictionary<string, object>();
            }

            if (spec.RequiredArgs != null)
            {
                for (int i = 0; i < spec.RequiredArgs.Length; i++)
                {
                    string key = spec.RequiredArgs[i];
                    if (!args.ContainsKey(key))
                    {
                        return ToolValidation.Fail(ToolErrorCode.BadArgs,
                            "工具「" + name + "」缺少必填参数「" + key + "」。");
                    }
                    object value = args[key];
                    if (value == null)
                    {
                        return ToolValidation.Fail(ToolErrorCode.BadArgs,
                            "工具「" + name + "」的参数「" + key + "」为 null。");
                    }
                    // 除 content 外，必填参数不允许空字符串（content 允许写空文件）
                    string s = value as string;
                    if (s != null && s.Trim().Length == 0 && !string.Equals(key, "content", StringComparison.Ordinal))
                    {
                        return ToolValidation.Fail(ToolErrorCode.BadArgs,
                            "工具「" + name + "」的参数「" + key + "」为空字符串。");
                    }
                }
            }

            // ---- strict 本地校验（3.19.0）：两条协议路径共用的最后一道闸 ----
            // 围栏降级路径没有服务商侧约束解码（类型错/拼错枚举/编造参数名都进得来）；
            // 用与 API 下发同源的 ToolSchemas 硬校验，失败结构化回喂（附正确输入示例），不执行。
            Dictionary<string, Dictionary<string, object>> paramMap = ToolSchemas.ParameterMap();
            Dictionary<string, object> schemaParams;
            if (paramMap.TryGetValue(name, out schemaParams) && schemaParams != null)
            {
                // 可选参数传 null 视为未提供（必填 null 已在上方拦截）
                List<string> nullKeys = new List<string>();
                foreach (KeyValuePair<string, object> kv in args)
                {
                    if (kv.Value == null) nullKeys.Add(kv.Key);
                }
                for (int i = 0; i < nullKeys.Count; i++) args.Remove(nullKeys[i]);
                string strictError;
                if (!ToolSchemas.ValidateAgainstSchema(name, args, out strictError))
                {
                    return ToolValidation.Fail(ToolErrorCode.BadArgs, strictError);
                }
            }
            return new ToolValidation();
        }
    }

    /// <summary>
    /// 工具执行运行时：所有工具调用的唯一入口。
    /// 负责 Schema 校验 → 幂等键 → 执行 → 错误归一 → 有限重试 → 输出清洗 → 审计轨迹。
    /// Agent 循环不再直接碰 ToolExecutor，避免出现「绕过校验直接执行」的第二条路径。
    /// </summary>
    public class ToolRuntime
    {
        private readonly ToolExecutor _executor;
        private readonly Config _config;

        public ToolRuntime(ToolExecutor executor, Config config)
        {
            _executor = executor;
            _config = config;
        }

        public async Task<ToolResult> InvokeAsync(ToolCall call, TaskBudget budget, string taskId, CancellationToken token)
        {
            if (call == null) throw new ArgumentNullException("call");

            ToolValidation validation = ToolRegistry.Validate(call.Name, call.ArgsJson);
            if (!validation.Ok)
            {
                ToolResult invalid = new ToolResult();
                invalid.Ok = false;
                invalid.ExitCode = -1;
                invalid.ErrorCode = validation.Code;
                invalid.Retryable = false;
                invalid.Error = validation.Message;
                invalid.Output = validation.Message;
                Trace.Event(taskId, "tool_rejected", BuildRejectData(call, validation));
                return invalid;
            }

            ToolSpec spec = ToolRegistry.Get(call.Name);
            if (string.IsNullOrEmpty(call.IdempotencyKey) && spec != null && spec.WritesExternal)
            {
                call.IdempotencyKey = (taskId == null ? "" : taskId) + ":" + call.Id;
            }

            int attempt = 0;
            int maxAttempts = spec == null ? 1 : Math.Max(1, spec.MaxRetries + 1);
            ToolResult result = null;
            // Hooks（v3.6 蒸馏）：pre-hook 可阻断（exit 2），post-hook 只通知。
            // hook 运行在审批之后：用户已经放行的调用不被 hook 越权拦截吗？——不，
            // hook 是用户自己配置的本地策略，与审批同属「用户说了算」，pre 阻断优先于执行。
            List<HookRule> hooks = Hooks.Normalize(_config.Hooks);
            HookOutcome preOutcome = await Hooks.RunAsync(hooks, "pre", call, call.ArgsJson, taskId, _config.WorkDir, token).ConfigureAwait(false);
            if (preOutcome.Blocked)
            {
                ToolResult blocked = new ToolResult();
                blocked.Ok = false;
                blocked.ExitCode = -1;
                blocked.ErrorCode = ToolErrorCode.Permission;
                blocked.Retryable = false;
                blocked.Error = preOutcome.Reason;
                blocked.Output = preOutcome.Reason + (preOutcome.Output.Length > 0 ? "\n" + preOutcome.Output : "");
                Trace.Event(taskId, "tool_rejected", BuildRejectData(call, new ToolValidation { Ok = false, Code = ToolErrorCode.Permission, Message = preOutcome.Reason }));
                return blocked;
            }
            bool hasPreOutput = preOutcome.Output.Length > 0;
            while (attempt < maxAttempts)
            {
                attempt++;
                long started = Message.Now();
                // 超时兜底（消费 spec.TimeoutSeconds——以前只声明不消费）：
                // UI 封送的调用（clipboard/keyboard 经 Ui.Invoke）无法被 token 取消，
                // UI 线程被占时工具永久挂起，任务循环卡死且「停止」救不回来。
                // 这里到点后不再等待：执行任务留在后台，循环按超时错误继续推进。
                if (spec != null && spec.TimeoutSeconds > 0)
                {
                    Task<ToolResult> execTask = _executor.ExecuteAsync(call, token);
                    Task winner = await Task.WhenAny(execTask, Task.Delay(spec.TimeoutSeconds * 1000, token)).ConfigureAwait(false);
                    if (winner != execTask)
                    {
                        result = new ToolResult();
                        result.Ok = false;
                        result.TimedOut = true;
                        result.ExitCode = -1;
                        result.ErrorCode = ToolErrorCode.Timeout;
                        result.Retryable = false;
                        result.Error = "工具 " + call.Name + " 超过声明时限（" + spec.TimeoutSeconds + " 秒）未返回，已放弃等待（执行可能仍在后台继续）。";
                        result.Output = result.Error;
                        long elapsedTo = Message.Now() - started;
                        Trace.Event(taskId, "tool_call", BuildCallData(call, result, elapsedTo, attempt));
                        return result;
                    }
                    result = execTask.Result;
                }
                else
                {
                    result = await _executor.ExecuteAsync(call, token).ConfigureAwait(false);
                }
                long elapsed = Message.Now() - started;
                result = Normalize(result, spec, call.Name);
                result.Attempts = attempt;

                // post-hook：只通知不阻断；pre-hook 的非阻断输出也一并追加到结果尾部让模型/用户可见
                if (hooks.Count > 0)
                {
                    HookOutcome postOutcome = await Hooks.RunAsync(hooks, "post", call, call.ArgsJson, taskId, _config.WorkDir, token).ConfigureAwait(false);
                    string hookNote = (hasPreOutput ? preOutcome.Output + "\n" : "") + postOutcome.Output;
                    if (hookNote.Trim().Length > 0)
                    {
                        result.Output = (result.Output == null ? "" : result.Output) + "\n" + hookNote.TrimEnd();
                    }
                }

                Trace.Event(taskId, "tool_call", BuildCallData(call, result, elapsed, attempt));
                if (result.Ok) return result;
                if (!result.Retryable) return result;
                if (spec == null || !spec.Idempotent) return result;
                // 已经是最后一次尝试：不会再重试，就不能扣任务级的重试预算
                if (attempt >= maxAttempts) return result;

                string budgetReason;
                if (!budget.TryConsumeRetry(out budgetReason))
                {
                    result.Error = AppendNote(result.Error, budgetReason + "，不再自动重试。");
                    result.Retryable = false;
                    return result;
                }

                try { await Task.Delay(200 * attempt, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
            return result;
        }

        private static Dictionary<string, object> BuildRejectData(ToolCall call, ToolValidation v)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["tool"] = call.Name;
            data["code"] = v.Code;
            data["reason"] = v.Message;
            return data;
        }

        private static Dictionary<string, object> BuildCallData(ToolCall call, ToolResult result, long elapsedMs, int attempt)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["tool"] = call.Name;
            data["attempt"] = attempt;
            data["ms"] = elapsedMs;
            data["ok"] = result.Ok;
            data["code"] = result.ErrorCode;
            data["retryable"] = result.Retryable;
            if (result.InjectionSuspected) data["injection_suspected"] = true;
            if (!string.IsNullOrEmpty(call.IdempotencyKey)) data["idem"] = call.IdempotencyKey;
            return data;
        }

        /// <summary>
        /// 错误归一：把散落的自由文本错误压成稳定错误码 + 可恢复性标记。
        /// 有副作用的非幂等工具一律不自动重放——宁可交回模型核验，也不重复扣款/重复写库。
        /// </summary>
        public static ToolResult Normalize(ToolResult result, ToolSpec spec, string toolName)
        {
            if (result == null)
            {
                ToolResult empty = new ToolResult();
                empty.Ok = false;
                empty.ErrorCode = ToolErrorCode.ToolError;
                empty.Error = "工具返回了空结果。";
                return empty;
            }
            if (result.ErrorCode == null) result.ErrorCode = "";
            if (result.Ok)
            {
                result.ErrorCode = ToolErrorCode.Ok;
                result.Retryable = false;
            }
            else
            {
                // 构造函数默认 ErrorCode="ok"：失败时必须重新归类，否则"失败但错误码 ok"
                if (result.ErrorCode.Length == 0 || result.ErrorCode == ToolErrorCode.Ok) result.ErrorCode = Classify(result);
                // 只有超时/服务不可用才是「可恢复」，且最终是否重试还要看工具是否幂等
                if (result.ErrorCode == ToolErrorCode.Timeout || result.ErrorCode == ToolErrorCode.Unavailable)
                {
                    result.Retryable = true;
                }
                if (result.ErrorCode == ToolErrorCode.BadArgs ||
                    result.ErrorCode == ToolErrorCode.Permission ||
                    result.ErrorCode == ToolErrorCode.NotFound ||
                    result.ErrorCode == ToolErrorCode.UnknownTool)
                {
                    result.Retryable = false;
                }
                if (spec != null && !spec.Idempotent) result.Retryable = false;
            }
            result.Output = SanitizeOutput(result.Output, result);
            return result;
        }

        private static string Classify(ToolResult result)
        {
            if (result.TimedOut) return ToolErrorCode.Timeout;
            string text = ((result.Error == null ? "" : result.Error) + " " + (result.Output == null ? "" : result.Output)).ToLowerInvariant();
            if (text.IndexOf("超时", StringComparison.Ordinal) >= 0 || text.IndexOf("timed out", StringComparison.Ordinal) >= 0) return ToolErrorCode.Timeout;
            if (text.IndexOf("不存在", StringComparison.Ordinal) >= 0 || text.IndexOf("not found", StringComparison.Ordinal) >= 0) return ToolErrorCode.NotFound;
            if (text.IndexOf("拒绝", StringComparison.Ordinal) >= 0 || text.IndexOf("不允许", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("工作目录之外", StringComparison.Ordinal) >= 0 || text.IndexOf("denied", StringComparison.Ordinal) >= 0) return ToolErrorCode.Permission;
            if (text.IndexOf("缺少", StringComparison.Ordinal) >= 0 || text.IndexOf("参数", StringComparison.Ordinal) >= 0) return ToolErrorCode.BadArgs;
            if (text.IndexOf("未注册", StringComparison.Ordinal) >= 0) return ToolErrorCode.UnknownTool;
            if (text.IndexOf("无法连接", StringComparison.Ordinal) >= 0 || text.IndexOf("连接失败", StringComparison.Ordinal) >= 0) return ToolErrorCode.Unavailable;
            return ToolErrorCode.ToolError;
        }

        // ANSI 剥离只在 Util.TextUtil 里有一份（含私有 CSI 序列 \e[?25l 这种带 ? 的），
        // 之前这里的副本缺了 '?'，鼠标光标开关一类的序列剥不干净。

        private static readonly string[] InjectionMarkers = new string[]
        {
            "ignore previous instructions", "ignore all previous", "disregard the above",
            "忽略之前的指令", "忽略上面的指令", "忽略以上所有", "你现在是", "新的系统提示", "system prompt:"
        };

        /// <summary>
        /// 工具输出一律按「数据」处理：去掉控制字符，超长截断，并对疑似指令注入打标记。
        /// 打标记而不是删除内容——删除会破坏证据，标记让模型和用户都知道这段不可信。
        /// </summary>
        public static string SanitizeOutput(string output, ToolResult result)
        {
            if (string.IsNullOrEmpty(output)) return output == null ? "" : output;
            string text = TextUtil.StripAnsi(output);
            if (text.IndexOf('\0') >= 0) text = text.Replace("\0", "");
            if (text.Length > 60000) text = TextUtil.SafeTruncate(text, 60000) + "\n…（输出过长已截断）";
            string lower = text.ToLowerInvariant();
            for (int i = 0; i < InjectionMarkers.Length; i++)
            {
                if (lower.IndexOf(InjectionMarkers[i], StringComparison.Ordinal) >= 0)
                {
                    if (result != null) result.InjectionSuspected = true;
                    break;
                }
            }
            return text;
        }

        private static string AppendNote(string text, string note)
        {
            if (string.IsNullOrEmpty(text)) return note;
            return text + "（" + note + "）";
        }
    }
}
