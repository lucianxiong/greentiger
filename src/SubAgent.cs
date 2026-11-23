using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GreenTiger
{
    /// <summary>
    /// 只读分析子代理（3.21.0）：把可拆分的只读调查（多方案对比、全仓审查、批量资料搜集）
    /// 派给独立的迷你 agent 循环并行执行，结论回喂主循环。
    ///
    /// 安全边界（docs/security.md「子代理」节同步维护）：
    /// · 工具面 = ToolSchemas.Build(true)（plan 模式只读子集）按 AllowedTools 再过滤——
    ///   无 shell、无写文件、无 MCP、无交互工具（ask_user/wait/finish）；
    /// · 子代理的工具面里没有 task 本身——不可递归派生；
    /// · 每个子任务独立预算：12 步 + 8 分钟墙钟，到点如实返回部分结果或失败原因；
    /// · 越权调用不进入执行器，合成 permission_denied 结果回喂（保持协议对账完整）；
    /// · 结论截断后回喂主上下文，防子代理输出撑爆主会话；
    /// · 子任务之间、子任务与主任务之间互不影响（独立 ApiBrain 历史，异常各自兜底）。
    /// </summary>
    public class SubAgentRunner
    {
        public static readonly string[] AllowedTools = new string[]
        {
            "read_file", "list_dir", "glob", "grep", "repo_map",
            "http_get", "web_fetch", "web_search",
            "everything_search", "pc_scan", "pc_disk_summary", "pc_startup_list", "pc_uninstall_list", "pc_shred_preview"
        };

        public static readonly HashSet<string> AllowedToolsSet =
            new HashSet<string>(AllowedTools, StringComparer.OrdinalIgnoreCase);

        public const int MaxTasksPerCall = 4;
        public const int MaxStepsPerTask = 12;
        public const int TimeoutMsPerTask = 8 * 60 * 1000;
        public const int MaxOutputChars = 4000;
        public const int MaxPromptChars = 8000;
        /// <summary>并行度 2：Http 出口 ConnectionLimit=64 足够，限 2 是给工具层的共享状态（缓存等）留余量。</summary>
        public const int MaxParallel = 2;

        private readonly IAgentHost _app;
        private readonly Config _config;
        /// <summary>自检注入点；null = 生产路径 new ApiBrain(clone)（选举三元组已回写 Config）。</summary>
        private readonly Func<Config, int, IToolUsingBrain> _brainFactory;

        public SubAgentRunner(IAgentHost app, Config config) : this(app, config, null) { }

        public SubAgentRunner(IAgentHost app, Config config, Func<Config, int, IToolUsingBrain> brainFactory)
        {
            _app = app;
            _config = config;
            _brainFactory = brainFactory;
        }

        public async Task<ToolResult> RunAsync(Dictionary<string, object> args, CancellationToken token)
        {
            ToolResult result = new ToolResult();
            List<object> raw = Json.Array(args, "tasks");
            List<string> tasks = new List<string>();
            int dropped = 0;
            if (raw != null)
            {
                for (int i = 0; i < raw.Count; i++)
                {
                    string t = raw[i] == null ? "" : Convert.ToString(raw[i]).Trim();
                    if (t.Length == 0) continue;
                    if (tasks.Count >= MaxTasksPerCall) { dropped++; continue; }
                    if (t.Length > MaxPromptChars) t = t.Substring(0, MaxPromptChars) + "…（超长已截断）";
                    tasks.Add(t);
                }
            }
            if (tasks.Count == 0)
            {
                result.Ok = false;
                result.Error = "tasks 不能为空：给出 1-" + MaxTasksPerCall + " 条自包含的只读分析任务（每条写清楚要查什么、产出什么结论）。";
                return result;
            }

            _app.AddLog("info", "子代理：派发 " + tasks.Count + " 个只读子任务（并行 " + MaxParallel + "）");
            SubOutcome[] outcomes = new SubOutcome[tasks.Count];
            using (SemaphoreSlim gate = new SemaphoreSlim(MaxParallel))
            {
                List<Task> runners = new List<Task>();
                for (int i = 0; i < tasks.Count; i++)
                {
                    int idx = i;
                    runners.Add(Task.Run(async delegate
                    {
                        await gate.WaitAsync(token);
                        try { outcomes[idx] = await RunOne(tasks[idx], idx, token); }
                        finally { gate.Release(); }
                    }));
                }
                try { await Task.WhenAll(runners); }
                catch (OperationCanceledException)
                {
                    result.Ok = false;
                    result.Error = "子任务已取消（主任务停止或暂停）。";
                    return result;
                }
            }

            int okCount = 0;
            int totalSteps = 0;
            StringBuilder sb = new StringBuilder();
            sb.Append("子任务 ").Append(tasks.Count).Append(" 个 · 成功 ");
            for (int i = 0; i < outcomes.Length; i++)
            {
                if (outcomes[i] == null)
                {
                    SubOutcome missing = new SubOutcome();
                    missing.Error = "未完成（任务被取消）";
                    outcomes[i] = missing;
                }
                if (outcomes[i].Ok) okCount++;
            }
            sb.Append(okCount).Append(" · 失败 ").Append(outcomes.Length - okCount);
            if (dropped > 0) sb.Append(" · 超出 ").Append(MaxTasksPerCall).Append(" 条上限已忽略 ").Append(dropped).Append(" 条");
            sb.AppendLine();
            for (int i = 0; i < outcomes.Length; i++)
            {
                SubOutcome o = outcomes[i];
                totalSteps += o.Steps;
                sb.AppendLine();
                sb.Append("### 任务 ").Append(i + 1).Append("「").Append(o.Title).Append("」 · ")
                  .Append(o.Ok ? "完成" : "失败").Append(" · ").Append(o.Steps).Append(" 步").AppendLine();
                if (o.Ok)
                {
                    sb.AppendLine(ContextPolicy.TruncateToolResult(o.Text, 3400, 400));
                }
                else
                {
                    sb.Append("未产出结论：").AppendLine(string.IsNullOrEmpty(o.Error) ? "未知原因" : o.Error);
                }
            }
            sb.AppendLine();
            sb.Append("共 ").Append(totalSteps).Append(" 步。以上是只读子代理的独立结论；写操作与命令请你在主循环亲自执行。");
            result.Ok = okCount > 0;
            if (!result.Ok) result.Error = "全部子任务失败（各任务失败原因见输出）。";
            result.Output = sb.ToString();
            _app.AddLog("info", "子代理：完成（成功 " + okCount + "/" + outcomes.Length + "，共 " + totalSteps + " 步）");
            return result;
        }

        private async Task<SubOutcome> RunOne(string prompt, int index, CancellationToken token)
        {
            SubOutcome outcome = new SubOutcome();
            outcome.Title = prompt.Length > 24 ? prompt.Substring(0, 24) + "…" : prompt;
            Config clone = Json.Parse<Config>(Json.Stringify(_config));
            if (clone == null) clone = new Config();
            clone.Normalize();
            IToolUsingBrain brain = _brainFactory != null ? _brainFactory(clone, index) : new ApiBrain(clone);
            brain.SetTools(ToolSchemas.Filter(ToolSchemas.Build(true), AllowedToolsSet));
            int steps = 0;
            try
            {
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    linked.CancelAfter(TimeoutMsPerTask);
                    while (steps < MaxStepsPerTask)
                    {
                        string userText = steps == 0
                            ? "<system>" + SubSystemPrompt(_config.WorkDir, prompt) + "</system>\n\n<task>" + prompt + "</task>"
                            : "（继续子任务。若已可结论，请直接输出最终结论文本，不要再调用工具。）";
                        BrainReply reply = await brain.AskAsync(userText, null, null, linked.Token);
                        if (reply.TimedOut)
                        {
                            outcome.Error = "等待模型超时";
                            break;
                        }
                        if (!string.IsNullOrEmpty(reply.Error) && (reply.NativeCalls == null || reply.NativeCalls.Count == 0))
                        {
                            outcome.Error = reply.Error;
                            break;
                        }
                        if (!brain.NativeSupported)
                        {
                            outcome.Error = "当前端点不支持原生工具调用（已降级围栏协议），子任务无法可靠执行";
                            break;
                        }
                        steps++;
                        if (reply.NativeCalls != null && reply.NativeCalls.Count > 0)
                        {
                            int cap = Math.Min(reply.NativeCalls.Count, 8);
                            List<ToolResult> results = new List<ToolResult>();
                            for (int i = 0; i < cap; i++)
                            {
                                results.Add(await ExecuteForSub(reply.NativeCalls[i], linked.Token));
                            }
                            for (int i = cap; i < reply.NativeCalls.Count; i++) results.Add(SynthDenied(reply.NativeCalls[i]));
                            brain.AppendToolResults(reply.NativeCalls, results);
                            continue;
                        }
                        string finalText = reply.Text == null ? "" : reply.Text;
                        if (finalText.Trim().Length > 0)
                        {
                            outcome.Ok = true;
                            outcome.Text = finalText;
                            break;
                        }
                        // 空文本且无工具调用：追问一轮；步数预算兜底防死循环
                    }
                    if (steps >= MaxStepsPerTask && !outcome.Ok && string.IsNullOrEmpty(outcome.Error))
                        outcome.Error = "步数预算（" + MaxStepsPerTask + "）用尽仍未给出最终结论";
                    outcome.Steps = steps;
                }
            }
            catch (OperationCanceledException)
            {
                outcome.Error = token.IsCancellationRequested ? "主任务已取消" : "子任务超时（" + (TimeoutMsPerTask / 60000) + " 分钟上限）";
            }
            catch (Exception ex)
            {
                outcome.Error = "子任务异常：" + ex.Message;
            }
            finally
            {
                try { brain.ResetAsync().Wait(200); } catch { }
            }
            return outcome;
        }

        /// <summary>子代理的工具执行：白名单外直接合成拒绝结果（不进入执行器/审批），白名单内走真实执行。</summary>
        private async Task<ToolResult> ExecuteForSub(ToolCall call, CancellationToken token)
        {
            ToolResult denied = SynthDenied(call);
            if (denied != null) return denied;
            return await _app.Tools.ExecuteAsync(call, token);
        }

        private static ToolResult SynthDenied(ToolCall call)
        {
            string name = call == null ? "" : (call.Name ?? "");
            if (!SubAgentRunner.AllowedToolsSet.Contains(name))
            {
                ToolResult r = new ToolResult();
                r.Ok = false;
                r.Error = "子代理是只读分析面，不允许调用 " + (name.Length == 0 ? "(未知工具)" : name) +
                    "。只允许：" + string.Join(", ", AllowedTools) + "。";
                return r;
            }
            return null;
        }

        /// <summary>
        /// 子代理系统提示（3.26.0 起带上项目经验）：
        /// 主 agent 已经蒸馏出来的元规则与同类历史失败，对子代理同样有效——否则同一次任务里，
        /// 主循环刚学会「先读再改」，被派出去的子代理还在重犯同样的错。
        /// 对齐 Antigravity 的共享知识库与 Fable 5.2 协调大量子代理时的统一约束。
        /// 预算：规则 900 字 + 历史提醒 400 字，超出截断，不让子代理的预算花在提示词上。
        /// </summary>
        internal static string SubSystemPrompt(string workDir, string task)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("你是一个只读分析子代理，为主 agent 完成一项调查任务。\n");
            sb.Append("你只能使用本轮下发的只读工具（读文件/列目录/搜索/网页获取等），不能写文件、不能执行命令、不能询问用户。\n");
            sb.Append("调查完成后，输出一段纯文本作为最终结论（不要再调用工具）：结论先行、附证据（文件路径/数据/来源），控制在 600 字内。\n");
            sb.Append("无法完成时如实说明卡点与已确认的部分。任务纪律：同一个错误连续 2 次就换方法；连续 3 次同类失败直接停下说明。");
            string rules = workDir == null ? "" : Evolution.PromptSection(workDir);
            if (rules.Length > 900) rules = rules.Substring(0, 900) + "…";
            if (rules.Length > 0) sb.Append("\n\n").Append(rules);
            string history = workDir == null ? "" : Evolution.Relevant(workDir, task, 2);
            if (history.Length > 0)
            {
                if (history.Length > 400) history = history.Substring(0, 400) + "…";
                sb.Append("\n\n").Append(history);
            }
            return sb.ToString();
        }

        private class SubOutcome
        {
            public bool Ok;
            public string Text;
            public int Steps;
            public string Error;
            public string Title;

            public SubOutcome()
            {
                Ok = false;
                Text = "";
                Steps = 0;
                Error = "";
                Title = "";
            }
        }
    }
}
