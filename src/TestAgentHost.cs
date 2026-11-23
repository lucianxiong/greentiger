using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GreenTiger
{
    /// <summary>
    /// 脚本大脑：按预设顺序返回回复，记录收到的每条提示。
    /// 只用于自检，不参与生产路径。
    /// </summary>
    public class ScriptedBrain : IBrain
    {
        private readonly Queue<string> _script = new Queue<string>();
        private readonly Queue<bool> _timeouts = new Queue<bool>();
        public readonly List<string> Prompts = new List<string>();
        /// <summary>模拟大脑回传的用量：>0 才上报（网页版通道就是全 0，表示 unknown）。</summary>
        public long UsagePromptTokens { get; set; }
        public long UsageCompletionTokens { get; set; }
        /// <summary>模拟「模型想得很久」：用于测运行中暂停/停止。</summary>
        public int DelayMs { get; set; }

        public string Id { get { return "scripted"; } }
        public string Name { get { return "脚本大脑"; } }
        public bool SupportsFiles { get { return false; } }
        public string ReadyDetail { get { return ""; } }

        public ScriptedBrain Add(string reply)
        {
            _script.Enqueue(reply == null ? "" : reply);
            _timeouts.Enqueue(false);
            return this;
        }

        /// <summary>模拟「回复超时、只吐出半截内容」的大脑。</summary>
        public ScriptedBrain AddTimedOut(string partial)
        {
            _script.Enqueue(partial == null ? "" : partial);
            _timeouts.Enqueue(true);
            return this;
        }

        public Task<bool> EnsureReadyAsync(CancellationToken token)
        {
            return Task.FromResult(true);
        }

        public async Task<BrainReply> AskAsync(string text, string[] files, Action<string> onPartial, CancellationToken token)
        {
            Prompts.Add(text == null ? "" : text);
            if (DelayMs > 0) await Task.Delay(DelayMs).ConfigureAwait(false);
            string reply = _script.Count > 0 ? _script.Dequeue() : "任务结束。";
            bool timedOut = _timeouts.Count > 0 ? _timeouts.Dequeue() : false;
            BrainReply r = new BrainReply();
            r.Text = reply;
            r.Brain = Id;
            r.ElapsedMs = 1;
            r.TimedOut = timedOut;
            // 模拟 API 通道回传用量：设了值才上报，用来测 token 预算与成本统计
            r.PromptTokens = UsagePromptTokens;
            r.CompletionTokens = UsageCompletionTokens;
            if (timedOut) r.Error = "等待回复超时（脚本模拟）";
            if (onPartial != null)
            {
                try { onPartial(reply); } catch { }
            }
            return r;
        }

        public Task ResetAsync() { return Task.FromResult(0); }
        public Task StopAsync() { return Task.FromResult(0); }
    }

    /// <summary>
    /// 全内存宿主：让 AgentRunner 可以在不启动 HTTP / MCP / 托盘的情况下跑完整循环。
    /// 只实现 IAgentHost 的最小语义，不做任何 UI 与网络副作用。
    /// </summary>
    public class TestAgentHost : IAgentHost
    {
        private readonly object _gate = new object();
        private readonly Config _config;
        private readonly HashSet<string> _always = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ToolExecutor _tools;
        private string _phase = "就绪";

        public Session Session { get; set; }
        public List<string> Logs { get; private set; }
        public List<ToolCall> ApprovalRequests { get; private set; }
        /// <summary>审批结果：默认放行；设成 deny 可测「用户拒绝」路径。</summary>
        public string ApprovalDecision { get; set; }

        public TestAgentHost(Config config, Session session)
        {
            _config = config;
            Session = session;
            Logs = new List<string>();
            ApprovalRequests = new List<ToolCall>();
            ApprovalDecision = "allow";
            Guard = new Guard();
            Mcp = new McpHost();
        }

        public object StateLock { get { return _gate; } }
        public Session ActiveSession { get { return Session; } }
        public Guard Guard { get; private set; }
        public McpHost Mcp { get; private set; }
        public BrainRouter Brains { get { return null; } }
        public bool CircuitBroken { get; set; }

        public ToolExecutor Tools
        {
            get
            {
                if (_tools == null) _tools = new ToolExecutor(this, _config);
                return _tools;
            }
        }

        public void AddLog(string level, string text)
        {
            lock (_gate) { Logs.Add(level + " " + text); }
        }

        public void SetPhase(string phase) { _phase = phase; }
        public string GetPhase() { return _phase; }
        public void SetRunning(bool running) { }
        public void BroadcastState() { }
        public void BroadcastAssistant(Message message) { }
        public void SaveActiveSession() { }

        public void ResetAlwaysAllowed()
        {
            lock (_gate) { _always.Clear(); }
        }

        public bool IsAlwaysAllowed(string toolName)
        {
            lock (_gate) { return _always.Contains(toolName); }
        }

        public Task<string> RequestApprovalAsync(ToolCall call, CancellationToken token)
        {
            lock (_gate) { ApprovalRequests.Add(call); }
            return Task.FromResult(ApprovalDecision);
        }

        private string _askAnswer;
        /// <summary>ask_user 的预置回答（测试注入）；null/空 = 用户提交了空回答。</summary>
        public string AskAnswer
        {
            get { lock (_gate) { return _askAnswer; } }
            set { lock (_gate) { _askAnswer = value; } }
        }

        public string TakeApprovalAnswer()
        {
            lock (_gate) { string a = _askAnswer; _askAnswer = null; return a; }
        }

        public void NotifyTaskEnd(TaskRecord task, string userText) { }
        public void NotifyApprovalNeeded(string toolName) { }

        public void NewSession()
        {
            Session = new Session();
        }

        public string ExportMarkdown(string id) { return ""; }

        public Artifact CreateArtifact(string path, string mime, string kind)
        {
            Artifact a = new Artifact();
            a.Path = path;
            a.Name = string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path);
            a.Mime = mime;
            a.Kind = kind;
            try { if (File.Exists(path)) a.Size = new FileInfo(path).Length; } catch { }
            return a;
        }

        public void RegisterProcess(Process process) { }

        public string RestoreBackup(string snapshotPath, string targetPath, out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrEmpty(snapshotPath) || !System.IO.File.Exists(snapshotPath))
                {
                    error = "快照不存在";
                    return "";
                }
                System.IO.File.Copy(snapshotPath, targetPath, true);
                return targetPath;
            }
            catch (Exception ex) { error = ex.Message; return ""; }
        }
        /// <summary>测试宿主的 checkpoint 恢复：直接走真实 Checkpoint（校验内置）。</summary>
        public string RestoreCheckpoint(string manifestPath, out string error)
        {
            return Checkpoint.Restore(manifestPath, "", out error);
        }
        public void UnregisterProcess(Process process) { }
    }

    /// <summary>
    /// 原生工具协议脚本大脑（自检专用）：按预设顺序返回 BrainReply，
    /// 记录下发的 Schema 名单与 AppendToolResults 对账，供子代理循环级测试。
    /// </summary>
    public class ScriptedToolBrain : IToolUsingBrain
    {
        private readonly Queue<BrainReply> _script = new Queue<BrainReply>();
        public readonly List<string> SchemaNames = new List<string>();
        public readonly List<ToolCall> AppendedCalls = new List<ToolCall>();
        public readonly List<ToolResult> AppendedResults = new List<ToolResult>();

        public string Id { get { return "scripted-tool"; } }
        public string Name { get { return "脚本工具大脑"; } }
        public bool SupportsFiles { get { return false; } }
        public string ReadyDetail { get { return ""; } }
        public bool NativeSupported { get { return true; } }

        public ScriptedToolBrain AddNative(params ToolCall[] calls)
        {
            BrainReply r = new BrainReply();
            r.Text = "";
            r.Brain = Id;
            r.ElapsedMs = 1;
            List<ToolCall> list = new List<ToolCall>();
            for (int i = 0; i < calls.Length; i++) list.Add(calls[i]);
            r.NativeCalls = list;
            _script.Enqueue(r);
            return this;
        }

        public ScriptedToolBrain AddText(string text)
        {
            BrainReply r = new BrainReply();
            r.Text = text == null ? "" : text;
            r.Brain = Id;
            r.ElapsedMs = 1;
            r.NativeCalls = new List<ToolCall>();
            _script.Enqueue(r);
            return this;
        }

        public Task<bool> EnsureReadyAsync(CancellationToken token) { return Task.FromResult(true); }

        public Task<BrainReply> AskAsync(string text, string[] files, Action<string> onPartial, CancellationToken token)
        {
            BrainReply r = _script.Count > 0 ? _script.Dequeue() : new BrainReply();
            if (r.Text == null) r.Text = "";
            if (r.NativeCalls == null) r.NativeCalls = new List<ToolCall>();
            return Task.FromResult(r);
        }

        public void SetTools(List<object> schemas)
        {
            SchemaNames.Clear();
            if (schemas == null) return;
            for (int i = 0; i < schemas.Count; i++)
            {
                Dictionary<string, object> tool = schemas[i] as Dictionary<string, object>;
                if (tool == null) continue;
                Dictionary<string, object> fn = tool.ContainsKey("function") ? tool["function"] as Dictionary<string, object> : null;
                if (fn != null && fn.ContainsKey("name")) SchemaNames.Add(Convert.ToString(fn["name"]));
            }
        }

        public void AppendToolResults(IList<ToolCall> calls, IList<ToolResult> results)
        {
            int n = calls == null ? 0 : calls.Count;
            for (int i = 0; i < n; i++)
            {
                AppendedCalls.Add(calls[i]);
                AppendedResults.Add(results != null && i < results.Count ? results[i] : null);
            }
        }

        public Task ResetAsync() { return Task.FromResult(0); }
        public Task StopAsync() { return Task.FromResult(0); }
    }
}
