using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GreenTiger
{
    public partial class App : ApplicationContext, IAgentHost
    {
        private readonly object _gate = new object();
        private readonly List<Session> _sessions = new List<Session>();
        private readonly List<LogEntry> _logs = new List<LogEntry>();
        private readonly Dictionary<string, Artifact> _artifacts = new Dictionary<string, Artifact>();
        private readonly List<string> _artifactOrder = new List<string>();   // 插入顺序，用于容量淘汰
        private const int MaxArtifacts = 300;
        private readonly HashSet<string> _alwaysAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Process> _processes = new List<Process>();
        private string _activeSessionId;
        private bool _running;
        private string _phase;
        private long _startedAt;
        private ToolCall _pendingApproval;
        private ReviewVerdict _approvalReview;
        private TaskCompletionSource<string> _approvalTcs;
        private string _approvalAnswer;
        private bool _pollingStatus;
        private bool _shuttingDown;
        private string _lastBrain;
        private string _lastLocalDetail;
        private NotifyIcon _tray;
        private System.Windows.Forms.Timer _statusTimer;
        private readonly string _uiUrl;

        public Config Config { get; private set; }
        public ToolExecutor Tools { get; private set; }
        public AgentRunner Agent { get; private set; }
        public WebServer Server { get; private set; }

        /// <summary>执行前后的最后一道闸门。</summary>
        public Guard Guard { get; private set; }
        /// <summary>选脑器：OpenAI 兼容 API 服务商（云端或本机）。</summary>
        public BrainRouter Brains { get; private set; }
        /// <summary>工作目录的内置文件浏览器。</summary>
        public FileBrowser Files { get; private set; }
        /// <summary>MCP 外部工具服务器（stdio JSON-RPC）。</summary>
        public McpHost Mcp { get; private set; }
        /// <summary>定时任务调度器（Scheduled tasks）。</summary>
        public TaskScheduler Scheduler { get; private set; }

        public object StateLock { get { return _gate; } }
        /// 阶段1：熔断标志（同工具同参数连续失败≥3次）
        public bool CircuitBroken { get; set; }

        private System.Timers.Timer _watchTimer;
        private long _lastUserActivity = Message.Now();

        /// <summary>watch mode 巡检：任务运行中用户离席超过阈值 → 自动暂停（可续跑）。</summary>
        private void CheckWatchMode()
        {
            try
            {
                if (!Config.WatchMode || !Agent.Running) return;
                long idleMs = Message.Now() - _lastUserActivity;
                long threshold = Math.Max(1, Config.WatchIdleMinutes) * 60000L;
                if (idleMs < threshold) return;
                Pause();
                AddLog("warn", "watch mode：检测到用户已离开超过 " + Math.Max(1, Config.WatchIdleMinutes) + " 分钟，任务已自动暂停；回来后点「继续」续跑。");
            }
            catch (Exception ex) { try { Log.Warn("watch mode 巡检失败: " + ex.Message); } catch { } }
        }

        /// <summary>用户活动打点（watch mode 判断「人还在」）：发消息/审批/改设置/界面心跳都会刷新。</summary>
        public void TouchUserActivity()
        {
            _lastUserActivity = Message.Now();
        }

        public App()
        {
            Config = Config.Load();
            ChromeTheme.Set(Config.ResolveTheme(Config.Theme)); // 浏览器窗口换肤中枢同步初始主题（auto 解析为系统深浅）
            if (!string.IsNullOrEmpty(Config.WorkDir) && !Directory.Exists(Config.WorkDir)) Config.WorkDir = "";
            Tools = new ToolExecutor(this, Config);
            Guard = new Guard();
            Brains = new BrainRouter(Config);
            Files = new FileBrowser(Config);
            Mcp = new McpHost();
            Mcp.Start(Config);
            Agent = new AgentRunner(this, Config, Guard, Brains);
            _phase = "就绪";
            _startedAt = Message.Now();

            Log.Line += OnLogLine;
            Log.Info("GreenTiger " + AppInfo.Version + " 启动");
            Log.Info("数据目录: " + AppPaths.Root);
            Trace.Cleanup(30);   // 日志/轨迹/统计轮转清理（保留 30 天，tasks.jsonl 超 5MB 归档）
            CleanupStaleTempOnUpdate();   // 版本变更 → 清 %TEMP% 可再生测试产物（旧数据不跨版本残留）
            ExportEmbeddedUi();
            LoadSessions();
            EnsureActiveSession();
            LoadArtifactsFromSessions();

            // watch mode 巡检（3.20.0）：任务运行中用户离席超过阈值 → 自动暂停（可续跑）
            _watchTimer = new System.Timers.Timer(30000);
            _watchTimer.Elapsed += delegate { CheckWatchMode(); };
            _watchTimer.AutoReset = true;
            _watchTimer.Start();

            Server = new WebServer(this, Program.DevMode);
            Server.Start(Program.HttpPort, Program.LanMode);
            _uiUrl = Server.UiUrl;
            try { File.WriteAllText(AppPaths.UiUrlFile, _uiUrl); }
            catch (Exception ex) { Log.Warn("写入控制台地址文件失败（外部脚本可能找不到控制台）: " + ex.Message); }

            Scheduler = new TaskScheduler(this);
            Scheduler.Start();

            CreateTray();
            if (!Program.NoBrowser)
            {
                // 必须等 Application.Run 的消息循环起来后再建 WebView2 窗口，
                // 否则异步初始化没有 WinForms 同步上下文，窗口会不可用。
                Application.Idle += OpenStartupWindows;
            }

            _statusTimer = new System.Windows.Forms.Timer();
            _statusTimer.Interval = 5000;
            _statusTimer.Tick += delegate { PollStatus(); };
            _statusTimer.Start();
        }

        public Session ActiveSession
        {
            get
            {
                lock (_gate)
                {
                    for (int i = 0; i < _sessions.Count; i++)
                    {
                        if (_sessions[i].Id == _activeSessionId) return _sessions[i];
                    }
                    if (_sessions.Count > 0) { _activeSessionId = _sessions[0].Id; return _sessions[0]; }
                    return null;
                }
            }
        }

        public string GetPhase()
        {
            lock (_gate) { return _phase; }
        }

        public void SetPhase(string phase)
        {
            bool changed = false;
            lock (_gate)
            {
                if (_phase != phase) { _phase = phase; changed = true; }
            }
            if (changed) BroadcastStatus();
        }

        public void SetRunning(bool running)
        {
            lock (_gate) { _running = running; }
            BroadcastStatus();
        }

        public void AddLog(string level, string text)
        {
            LogEntry entry = new LogEntry(level, text);
            lock (_gate)
            {
                _logs.Add(entry);
                if (_logs.Count > 300) _logs.RemoveRange(0, _logs.Count - 300);
            }
            try
            {
                Server.Broadcast("{\"type\":\"log\",\"entry\":" + Json.Stringify(entry) + "}");
            }
            catch { }
        }

        /// <summary>文件级记忆规则摘要（/api/rules）：工作区祖先到当前目录的项目规则 + 用户 rules.md。</summary>
        public Dictionary<string, object> RulesSummary()
        {
            Dictionary<string, object> root = new Dictionary<string, object>();
            List<object> projectFiles = new List<object>();
            List<string> rulePaths = ContextPolicy.ProjectRuleFiles(Config.WorkDir);
            for (int i = 0; i < rulePaths.Count; i++)
            {
                Dictionary<string, object> info = RuleInfo(rulePaths[i]);
                if (info != null) projectFiles.Add(info);
            }
            root["projectFiles"] = projectFiles;
            root["project"] = projectFiles.Count == 0 ? null : projectFiles[projectFiles.Count - 1];
            List<object> pathRules = new List<object>();
            List<string> pathRulePaths = ContextPolicy.ProjectPathRuleFiles(Config.WorkDir);
            for (int i = 0; i < pathRulePaths.Count; i++)
            {
                ContextPolicy.ProjectPathRule rule = ContextPolicy.LoadProjectPathRule(pathRulePaths[i]);
                if (rule == null) continue;
                Dictionary<string, object> info = RuleInfo(rule.Path);
                if (info == null) continue;
                info["paths"] = rule.Paths;
                info["baseDir"] = rule.BaseDirectory;
                pathRules.Add(info);
            }
            root["pathRules"] = pathRules;
            root["user"] = RuleInfo(AppPaths.RulesFile);
            return root;
        }

        private Dictionary<string, object> RuleInfo(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return null;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["path"] = path;
                d["chars"] = text.Length;
                string first = text.Trim();
                int nl = first.IndexOf('\n');
                d["firstLine"] = nl > 0 ? first.Substring(0, nl) : first;
                return d;
            }
            catch { return null; }
        }

        /// <summary>把系统错误作为一条聊天消息显示（避免"不回复"假象：被拒绝时用户至少看到原因）。</summary>
        public void PushSystemError(string text)
        {
            AddLog("error", text);
            try
            {
                Session session = ActiveSession;
                if (session == null) return;
                Message sys = new Message();
                sys.Role = "system";
                sys.Text = text;
                sys.Status = "error";
                lock (_gate)
                {
                    session.Messages.Add(sys);
                    session.Touch();
                }
                SaveActiveSession();
                BroadcastState();
            }
            catch (Exception ex) { Log.Warn("PushSystemError 显示系统消息失败: " + ex.Message); }
        }

        private void OnLogLine(string level, string text)
        {
            AddLog(level, text);
        }

        private void BroadcastStatus()
        {
            try
            {
                Dictionary<string, object> status = new Dictionary<string, object>();
                lock (_gate)
                {
                    status["running"] = _running;
                    status["phase"] = _phase;
                }
                Server.Broadcast("{\"type\":\"status\",\"status\":" + Json.Stringify(status) + "}");
            }
            catch { }
        }

        public void BroadcastAssistant(Message message)
        {
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = message.Id;
                d["text"] = message.Text;
                d["status"] = message.Status;
                d["brain"] = message.Brain;
                Server.Broadcast("{\"type\":\"assistant\",\"message\":" + Json.Stringify(d) + "}");
            }
            catch { }
        }

        public void BroadcastState()
        {
            if (_shuttingDown) return;
            try { Server.Broadcast("{\"type\":\"state\",\"state\":" + StateJson() + "}"); }
            catch (Exception ex) { Log.Warn("广播状态失败: " + ex.Message); }
        }

        public string StateJson()
        {
            Dictionary<string, object> root = new Dictionary<string, object>();
            lock (_gate)
            {
                root["version"] = AppInfo.Version;
                root["config"] = ConfigDto();
                root["status"] = StatusDto();
                root["sessions"] = SessionSummariesDto();
                root["active"] = ActiveSessionDto();
                root["approval"] = ApprovalDto();
                root["logs"] = LogsDto();
                root["stats"] = StatsDto();
                root["outcome"] = Agent == null ? "" : Agent.LastOutcome;
                root["task"] = TaskDto();
                root["taskStats"] = TaskStatsDto();
                root["alerts"] = AlertsDto();
            }
            root["brain"] = BrainDto();
            return Json.Stringify(root);
        }

        private Dictionary<string, object> ConfigDto()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["workDir"] = Config.WorkDir;
            d["autoRun"] = Config.AutoRun;
            d["theme"] = Config.Theme;
            d["uiVersion"] = _uiVersion;
            d["maxSteps"] = Config.MaxSteps;
            d["commandTimeout"] = Config.CommandTimeout;
            d["replyTimeout"] = Config.ReplyTimeout;
            d["taskTimeoutSeconds"] = Config.TaskTimeoutSeconds;
            d["maxTaskTokens"] = Config.MaxTaskTokens;
            d["maxToolRetries"] = Config.MaxToolRetries;
            d["maxRepairs"] = Config.MaxRepairs;
            d["showInspector"] = Config.ShowInspector;
            d["hideOnScreenshot"] = Config.HideOnScreenshot;
            d["uiMotion"] = Config.UiMotion;
            d["uiSounds"] = Config.UiSounds;
            d["readOnly"] = Config.ReadOnly;
            d["sandboxMode"] = Config.SandboxMode;
            d["supervisorReview"] = Config.SupervisorReview;
            d["watchMode"] = Config.WatchMode;
            d["watchIdleMinutes"] = Config.WatchIdleMinutes;
            d["networkPolicy"] = Config.NetworkPolicy ?? "off";
            d["networkAllowlist"] = Config.NetworkAllowlist ?? new List<string>();
            d["highRiskPolicy"] = Config.HighRiskPolicy;
            d["mode"] = Config.Mode;
            d["reasoningEffort"] = Config.ReasoningEffort;
            d["providerId"] = Config.ProviderId;
            d["providerKey"] = Config.ProviderKey;
            d["providerEndpoint"] = Config.ProviderEndpoint;
            d["providerModel"] = Config.ProviderModel;
            // P0：免 Key 公益池只够连通性验证（自文档口径），选中它跑任务前界面要提醒
            bool trialTier = Providers.IsTrialTier(Config);
            d["brainTrial"] = trialTier;
            d["brainTrialNote"] = trialTier
                ? "当前是免 Key 免费档：匿名队列有限速，可能排队或遇到 402/429；轻量对话与工具调用可用，重任务建议换正式服务商。"
                : "";
            d["autoPool"] = Config.AutoPool;
            d["providerPool"] = Config.ProviderPool;
            d["notify"] = Config.Notify;
            d["providerName"] = Providers.DisplayName(Config);
            d["workspaces"] = WorkspacesDto();
            d["tasks"] = TasksDto();
            d["permitRules"] = Config.PermitRules;
            return d;
        }

        private Dictionary<string, object> StatusDto()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["running"] = _running;
            d["phase"] = _phase;
            d["startedAt"] = _startedAt;
            d["sessionId"] = _activeSessionId;
            d["version"] = AppInfo.Version;
            d["gitBranch"] = GitUtil.CurrentBranch(Config.WorkDir);
            return d;
        }

        private List<Dictionary<string, object>> SessionSummariesDto()
        {
            List<Session> sorted = new List<Session>(_sessions);
            // 置顶优先，其余按最近更新倒序
            sorted.Sort(delegate(Session a, Session b)
            {
                int p = (b.Pinned ? 1 : 0).CompareTo(a.Pinned ? 1 : 0);
                return p != 0 ? p : b.UpdatedAt.CompareTo(a.UpdatedAt);
            });
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            for (int i = 0; i < sorted.Count; i++)
            {
                Session s = sorted[i];
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = s.Id;
                d["title"] = s.Title;
                d["updatedAt"] = s.UpdatedAt;
                d["createdAt"] = s.CreatedAt;
                d["messageCount"] = s.Messages.Count;
                d["toolCount"] = s.ToolCount;
                d["active"] = s.Id == _activeSessionId;
                d["pinned"] = s.Pinned;
                d["archived"] = s.Archived;
                list.Add(d);
            }
            return list;
        }

        private Dictionary<string, object> ActiveSessionDto()
        {
            Session s = FindActiveSessionUnsafe();
            if (s == null) return null;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["id"] = s.Id;
            d["title"] = s.Title;
            d["workDir"] = s.WorkDir;
            d["createdAt"] = s.CreatedAt;
            d["updatedAt"] = s.UpdatedAt;
            d["toolCount"] = s.ToolCount;
            d["turnCount"] = s.TurnCount;
            d["contextChars"] = s.ContextChars;
            List<Dictionary<string, object>> messages = new List<Dictionary<string, object>>();
            for (int i = 0; i < s.Messages.Count; i++)
            {
                Message m = s.Messages[i];
                Dictionary<string, object> md = new Dictionary<string, object>();
                md["id"] = m.Id;
                md["role"] = m.Role;
                md["text"] = TextUtil.Truncate(m.Text, 200000);
                md["time"] = m.Time;
                md["status"] = m.Status;
                md["brain"] = m.Brain;
                List<Dictionary<string, object>> tools = new List<Dictionary<string, object>>();
                for (int j = 0; j < m.ToolCalls.Count; j++)
                {
                    ToolCall t = m.ToolCalls[j];
                    Dictionary<string, object> td = new Dictionary<string, object>();
                    td["id"] = t.Id;
                    td["name"] = t.Name;
                    td["argsJson"] = TextUtil.Truncate(t.ArgsJson, 12000);
                    td["command"] = TextUtil.Truncate(t.Command, 12000);
                    td["risk"] = t.Risk;
                    td["riskReason"] = t.RiskReason;
                    td["status"] = t.Status;
                    td["output"] = TextUtil.Truncate(t.Output, 12000);
                    td["diff"] = TextUtil.Truncate(t.Diff, 4000);
                    td["backupPath"] = t.BackupPath == null ? "" : t.BackupPath;
                    td["exitCode"] = t.ExitCode;
                    // 阶段2：错误码 / 归因 / 执行次数，供界面与外部脚本查询
                    td["errorCode"] = t.ErrorCode;
                    td["errorClass"] = t.ErrorClass;
                    td["attempts"] = t.Attempts;
                    td["durationMs"] = t.DurationMs;
                    td["requiresApproval"] = t.RequiresApproval;
                    td["approvalStatus"] = t.ApprovalStatus;
                    List<Dictionary<string, object>> arts = new List<Dictionary<string, object>>();
                    for (int k = 0; k < t.Artifacts.Count; k++)
                    {
                        Artifact a = t.Artifacts[k];
                        Dictionary<string, object> ad = new Dictionary<string, object>();
                        ad["id"] = a.Id;
                        ad["name"] = a.Name;
                        ad["mime"] = a.Mime;
                        ad["size"] = a.Size;
                        ad["kind"] = a.Kind;
                        arts.Add(ad);
                    }
                    td["artifacts"] = arts;
                    tools.Add(td);
                }
                md["toolCalls"] = tools;
                messages.Add(md);
            }
            d["messages"] = messages;
            return d;
        }

        private Dictionary<string, object> ApprovalDto()
        {
            if (_pendingApproval == null) return null;
            ToolCall t = _pendingApproval;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["id"] = t.Id;
            d["name"] = t.Name;
            d["argsJson"] = t.ArgsJson;
            d["command"] = t.Command;
            d["risk"] = t.Risk;
            d["riskReason"] = t.RiskReason;
            d["diff"] = string.IsNullOrEmpty(t.Diff) ? "" : t.Diff;
            d["target"] = ApprovalTarget(t);
            d["workDir"] = Config.WorkDir ?? "";
            d["approvalReason"] = !string.IsNullOrWhiteSpace(t.RiskReason) ? t.RiskReason :
                (string.Equals(t.Name, "plan", StringComparison.OrdinalIgnoreCase) ? "计划需要你审阅并批准后，Agent 才会继续执行。" :
                (string.Equals(t.Risk, "danger", StringComparison.OrdinalIgnoreCase) ? "高风险操作需要单独确认" :
                (!Config.AutoRun ? "默认权限模式下，副作用操作需要确认" : "该工具调用需要确认")));
            ReviewVerdict rv;
            lock (_gate) { rv = _approvalReview; }
            if (rv != null)
            {
                Dictionary<string, object> rd = new Dictionary<string, object>();
                rd["verdict"] = rv.Verdict;
                rd["note"] = rv.Note ?? "";
                d["review"] = rd;
            }
            d["canAlwaysAllow"] = !string.Equals(t.Risk, "danger", StringComparison.OrdinalIgnoreCase);
            d["alwaysAllowScope"] = "「本任务同工具免确认」只对本次任务生效；「永久允许此工具」写入权限规则并落盘，重启后仍有效（shell 不提供永久档）。";
            return d;
        }

        internal static string ApprovalTarget(ToolCall call)
        {
            if (call == null) return "";
            try
            {
                Dictionary<string, object> args = Json.ParseObject(call.ArgsJson);
                if (args != null)
                {
                    string[] keys = new string[] { "path", "file", "directory", "url", "application", "target", "recipient" };
                    string[] labels = new string[] { "路径", "文件", "目录", "网址", "应用", "目标", "收件人" };
                    for (int i = 0; i < keys.Length; i++)
                    {
                        string value = Json.Str(args, keys[i], "").Trim();
                        if (value.Length > 0) return labels[i] + " · " + TextUtil.Truncate(value, 500);
                    }
                    if (string.Equals(call.Name, "task", StringComparison.OrdinalIgnoreCase))
                    {
                        string prompt = Json.Str(args, "prompt", "").Trim();
                        if (prompt.Length > 0) return "子任务 · " + TextUtil.Truncate(prompt, 500);
                    }
                }
            }
            catch { }
            if (string.Equals(call.Name, "shell", StringComparison.OrdinalIgnoreCase)) return "PowerShell 命令";
            return "";
        }

        private List<Dictionary<string, object>> LogsDto()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            int start = Math.Max(0, _logs.Count - 120);
            for (int i = start; i < _logs.Count; i++)
            {
                LogEntry e = _logs[i];
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["time"] = e.Time;
                d["level"] = e.Level;
                d["text"] = TextUtil.Truncate(e.Text, 2000);
                list.Add(d);
            }
            return list;
        }

        private Dictionary<string, object> StatsDto()
        {
            Session s = FindActiveSessionUnsafe();
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["messages"] = s == null ? 0 : s.Messages.Count;
            d["tools"] = s == null ? 0 : s.ToolCount;
            d["turns"] = s == null ? 0 : s.TurnCount;
            d["contextChars"] = s == null ? 0 : s.ContextChars;
            // 上限按当前模型的有效窗口算（不再写死 80k）：界面百分比与服务端压缩阈值同源
            d["contextLimit"] = ContextPolicy.EffectiveCompactChars(Config);
            return d;
        }

        /// <summary>当前会话的任务执行记录（终态、步数、验收摘要）。</summary>
        private Dictionary<string, object> TaskDto()
        {
            Session s = FindActiveSessionUnsafe();
            Dictionary<string, object> d = new Dictionary<string, object>();
            TaskRecord t = s == null ? null : s.Task;
            d["id"] = t == null ? "" : t.TaskId;
            d["outcome"] = t == null ? "" : t.Outcome;
            d["errorClass"] = t == null ? "" : t.ErrorClass;
            d["steps"] = t == null ? 0 : t.Steps;
            d["repairs"] = t == null ? 0 : t.Repairs;
            d["retries"] = t == null ? 0 : t.Retries;
            d["verification"] = t == null ? "" : t.Verification;
            d["durationMs"] = t == null ? 0 : t.DurationMs;
            d["budgetExceeded"] = t != null && t.BudgetExceeded;
            d["promptTokens"] = t == null ? 0 : t.PromptTokens;
            d["completionTokens"] = t == null ? 0 : t.CompletionTokens;
            d["cachedTokens"] = t == null ? 0 : t.CachedTokens;
            // 阶段1：token 预算是「钱」的硬上限，界面要能看出是「没用量」还是「额度小」
            d["tokenLimit"] = Config == null ? 0 : Config.MaxTaskTokens;
            d["tokensUsed"] = t == null ? 0 : t.PromptTokens + t.CompletionTokens;
            d["tokensEstimated"] = t == null ? 0 : t.TokensEstimated;
            d["promptChars"] = t == null ? 0 : t.PromptChars;
            d["completionChars"] = t == null ? 0 : t.CompletionChars;
            // 阶段4：续跑相关状态，供界面判断要不要显示「继续任务」
            d["interrupted"] = t != null && t.Interrupted;
            d["resumable"] = t != null && t.Resumable;
            d["resumeCount"] = t == null ? 0 : t.ResumeCount;
            d["approvals"] = ApprovalTicketsDto(s);
            d["parked"] = ParkedTasksDto(s);
            return d;
        }

        /// <summary>「可继续」暂存任务（被新任务顶下来的暂停/中断任务），界面用来提供续跑入口。</summary>
        private static List<Dictionary<string, object>> ParkedTasksDto(Session s)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            if (s == null || s.ParkedTasks == null) return list;
            for (int i = 0; i < s.ParkedTasks.Count; i++)
            {
                TaskRecord t = s.ParkedTasks[i];
                if (t == null) continue;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = t.TaskId;
                d["outcome"] = t.Outcome;
                d["title"] = TextUtil.OneLine(t.UserText, 60);
                d["steps"] = t.Steps;
                d["resumeCount"] = t.ResumeCount;
                d["updatedAt"] = t.EndedAt > 0 ? t.EndedAt : t.StartedAt;
                list.Add(d);
            }
            return list;
        }

        /// <summary>最近的审批票据（最多 5 张），界面用来显示「当时在等什么、批没批」。</summary>
        private static List<Dictionary<string, object>> ApprovalTicketsDto(Session s)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            if (s == null || s.Approvals == null) return list;
            int start = Math.Max(0, s.Approvals.Count - 5);
            for (int i = start; i < s.Approvals.Count; i++)
            {
                ApprovalTicket t = s.Approvals[i];
                if (t == null) continue;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = t.Id;
                d["tool"] = t.Tool;
                d["status"] = t.Status;
                d["risk"] = t.Risk;
                d["decision"] = t.Decision;
                d["createdAt"] = t.CreatedAt;
                d["expiresAt"] = t.ExpiresAt;
                d["note"] = t.Note;
                list.Add(d);
            }
            return list;
        }

        private TaskStatsSummary _taskStatsCache;
        private long _taskStatsCachedAt;
        private List<AlertItem> _alertsCache;
        private long _alertsCachedAt;
        private string _lastAlertFingerprint = "<none>";

        /// <summary>退化告警（阶段6/7）：告警带自己的 10 秒缓存。
        /// 曾经与任务统计共用 _taskStatsCachedAt，而状态广播先算统计再算告警，
        /// 导致告警的刷新判断永远不成立、AlertEngine 在生产路径上一条都产不出。</summary>
        private List<AlertItem> Alerts()
        {
            long now = Message.Now();
            if (_alertsCache == null || now - _alertsCachedAt > 10000)
            {
                _alertsCachedAt = now;
                _alertsCache = AlertEngine.Evaluate(TaskStore.Load(AlertEngine.Window(Config)), Config);
                string fp = AlertEngine.Fingerprint(_alertsCache);
                if (fp != _lastAlertFingerprint)
                {
                    _lastAlertFingerprint = fp;
                    if (_alertsCache.Count > 0)
                    {
                        for (int i = 0; i < _alertsCache.Count; i++)
                        {
                            AddLog(_alertsCache[i].Level == "critical" ? "error" : "warn",
                                "【告警】" + _alertsCache[i].Message);
                        }
                        Log.Warn("执行质量告警：" + fp);
                    }
                }
            }
            return _alertsCache == null ? new List<AlertItem>() : _alertsCache;
        }

        private List<object> AlertsDto()
        {
            List<AlertItem> items = Alerts();
            List<object> list = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                Dictionary<string, object> a = new Dictionary<string, object>();
                a["level"] = items[i].Level;
                a["code"] = items[i].Code;
                a["message"] = items[i].Message;
                a["count"] = items[i].Count;
                a["total"] = items[i].Total;
                list.Add(a);
            }
            return list;
        }

        /// <summary>跨会话任务统计（阶段6）。带 10 秒缓存，避免每次状态广播都读文件。</summary>
        private Dictionary<string, object> TaskStatsDto()
        {
            long now = Message.Now();
            if (_taskStatsCache == null || now - _taskStatsCachedAt > 10000)
            {
                _taskStatsCache = TaskStore.Summary(200);
                _taskStatsCachedAt = now;
            }
            TaskStatsSummary st = _taskStatsCache;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["total"] = st.Total;
            d["succeeded"] = st.Succeeded;
            d["failed"] = st.Failed;
            d["incomplete"] = st.Incomplete;
            d["cancelled"] = st.Cancelled;
            d["paused"] = st.Paused;
            d["waitingApproval"] = st.WaitingApproval;
            d["budgetExceeded"] = st.BudgetExceeded;
            d["verificationFailed"] = st.VerificationFailed;
            d["successRate"] = Math.Round(st.SuccessRate, 3);
            d["p50Ms"] = st.P50Ms;
            d["p95Ms"] = st.P95Ms;
            d["avgMs"] = st.AvgMs;
            d["promptTokens"] = st.PromptTokens;
            d["completionTokens"] = st.CompletionTokens;
            d["tokensEstimated"] = st.TokensEstimated;
            d["cost"] = st.CostNote;
            return d;
        }

        public Dictionary<string, object> BrainDto()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            BrainProbe probe = Brains.Api.CachedProbe();
            if (probe == null) probe = new BrainProbe();
            Dictionary<string, object> api = new Dictionary<string, object>();
            api["available"] = probe.Available;
            // 未探测成功时也要把「用户选定的端点 / 模型」交回界面：模型胶囊与切换面板靠它显示当前模型，
            // 否则没填 Key 的瞬间界面就把 deepseek-v4-pro 说成「未接入模型」，用户看不出自己选了哪个。
            api["endpoint"] = string.IsNullOrEmpty(probe.Endpoint) ? (Config.ProviderEndpoint ?? "") : probe.Endpoint;
            api["model"] = string.IsNullOrEmpty(probe.Model) ? (Config.ProviderModel ?? "") : probe.Model;
            api["kind"] = probe.Kind;
            api["detail"] = probe.Detail;
            List<string> models = probe.Models;
            if ((models == null || models.Count == 0) && !string.IsNullOrEmpty(Config.ProviderId))
            {
                ProviderInfo preset = Providers.Find(Config.ProviderId);
                if (preset != null) models = preset.Models;
            }
            api["models"] = models ?? new List<string>();
            d["provider"] = api;
            d["providerId"] = Config.ProviderId;
            d["providerName"] = Providers.DisplayName(Config);
            d["active"] = EffectiveBrain();
            d["contextChars"] = ContextPolicy.EffectiveCompactChars(Config);
            return d;
        }

        /// <summary>当前会实际使用的脑：端点可用就是 provider（云端或本机），否则 none。没有第二个「必须登录」的通道。</summary>
        public string EffectiveBrain()
        {
            if (string.IsNullOrEmpty(Config.ProviderEndpoint)) return "none";
            BrainProbe probe = Brains.Api.CachedProbe();
            if (probe != null && probe.Available) return "provider";
            return "none";
        }

        /// <summary>强制重新探测 API 服务商（用户点「重新检测」时用）。</summary>
        private Session FindActiveSessionUnsafe()
        {
            for (int i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].Id == _activeSessionId) return _sessions[i];
            }
            if (_sessions.Count > 0)
            {
                _activeSessionId = _sessions[0].Id;
                return _sessions[0];
            }
            return null;
        }

        private void EnsureActiveSession()
        {
            lock (_gate)
            {
                if (FindActiveSessionUnsafe() == null) CreateSessionUnsafe();
            }
        }

        private Session CreateSessionUnsafe()
        {
            Session s = new Session();
            s.WorkDir = Config.WorkDir;
            _sessions.Add(s);
            if (_sessions.Count > 60)
            {
                _sessions.Sort(delegate(Session a, Session b) { return b.UpdatedAt.CompareTo(a.UpdatedAt); });
                _sessions.RemoveRange(50, _sessions.Count - 50);
            }
            _activeSessionId = s.Id;
            SaveSessionUnsafe(s);
            return s;
        }

        public void NewSession()
        {
            if (Agent.Running)
            {
                AddLog("warn", "任务运行中，暂时不能新建会话。");
                return;
            }
            lock (_gate)
            {
                CreateSessionUnsafe();
            }
            BroadcastState();
        }

        /// <summary>从指定消息分叉：复制该消息及其之前的完整对话到新会话。</summary>
        public void ForkSession(string messageId)
        {
            if (Agent.Running)
            {
                AddLog("warn", "任务运行中，暂时不能分叉会话。");
                return;
            }
            lock (_gate)
            {
                Session cur = FindActiveSessionUnsafe();
                if (cur == null) return;
                List<Message> msgs = cur.Messages ?? new List<Message>();
                int cut = -1;
                for (int i = 0; i < msgs.Count; i++)
                {
                    if (msgs[i].Id == messageId) { cut = i; break; }
                }
                if (cut < 0) return;

                Session fork = new Session();
                fork.Title = (cur.Title ?? "会话") + " · 分支";
                fork.WorkDir = cur.WorkDir;
                fork.SchemaVersion = cur.SchemaVersion;
                fork.CreatedAt = Message.Now();
                fork.UpdatedAt = fork.CreatedAt;
                for (int i = 0; i <= cut; i++)
                {
                    Message src = msgs[i];
                    Message copy = new Message();
                    copy.Id = "m_" + Guid.NewGuid().ToString("N").Substring(0, 12);
                    copy.Role = src.Role;
                    copy.Text = src.Text;
                    copy.Time = src.Time;
                    copy.Status = src.Status;
                    copy.Brain = src.Brain;
                    copy.ToolCalls = src.ToolCalls ?? new List<ToolCall>();
                    fork.Messages.Add(copy);
                    fork.ContextChars += (src.Text ?? "").Length;
                }
                _sessions.Add(fork);
                if (_sessions.Count > 60)
                {
                    _sessions.Sort(delegate(Session a, Session b) { return b.UpdatedAt.CompareTo(a.UpdatedAt); });
                    _sessions.RemoveRange(50, _sessions.Count - 50);
                }
                _activeSessionId = fork.Id;
                SaveSessionUnsafe(fork);
            }
            AddLog("info", "已从消息分叉出新会话（保留此前完整对话）");
            BroadcastState();
        }

        public void SelectSession(string id)
        {
            if (Agent.Running)
            {
                AddLog("warn", "任务运行中，暂时不能切换会话。");
                return;
            }
            bool workDirChanged = false;
            lock (_gate)
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    if (_sessions[i].Id == id)
                    {
                        _activeSessionId = id;
                        // 会话记住自己的工作目录，切回来时一并恢复。
                        string dir = _sessions[i].WorkDir;
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                            !string.Equals(dir, Config.WorkDir, StringComparison.OrdinalIgnoreCase))
                        {
                            Config.WorkDir = dir;
                            Config.Save();
                            workDirChanged = true;
                        }
                        break;
                    }
                }
            }
            if (workDirChanged) AddLog("info", "已切换到该会话的工作目录: " + Config.WorkDir);
            BroadcastState();
        }

        public void RenameSession(string id, string title)
        {
            string clean = TextUtil.CleanTitle(title, 60);
            if (clean.Length == 0)
            {
                AddLog("warn", "会话标题不能为空。");
                return;
            }
            lock (_gate)
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    if (_sessions[i].Id == id)
                    {
                        _sessions[i].Title = clean;
                        _sessions[i].Touch();
                        SaveSessionUnsafe(_sessions[i]);
                        break;
                    }
                }
            }
            AddLog("info", "会话已重命名为「" + clean + "」");
            BroadcastState();
        }

        public void PinSession(string id, bool pinned)
        {
            lock (_gate)
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    if (_sessions[i].Id == id)
                    {
                        _sessions[i].Pinned = pinned;
                        SaveSessionUnsafe(_sessions[i]);
                        break;
                    }
                }
            }
            AddLog("info", pinned ? "会话已置顶" : "会话已取消置顶");
            BroadcastState();
        }

        public void ArchiveSession(string id, bool archived)
        {
            lock (_gate)
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    if (_sessions[i].Id == id)
                    {
                        _sessions[i].Archived = archived;
                        SaveSessionUnsafe(_sessions[i]);
                        break;
                    }
                }
                // 归档当前会话时自动切到第一个未归档会话，避免界面停在看不见的会话上
                if (archived && _activeSessionId == id)
                {
                    for (int i = 0; i < _sessions.Count; i++)
                    {
                        if (!_sessions[i].Archived) { _activeSessionId = _sessions[i].Id; break; }
                    }
                }
            }
            AddLog("info", archived ? "会话已归档" : "会话已取消归档");
            BroadcastState();
        }

        /// <summary>批量归档 / 取消归档：侧栏多选后一次提交，避免前端循环打 N 次状态广播。</summary>
        public void ArchiveSessions(List<string> ids, bool archived)
        {
            if (ids == null || ids.Count == 0) return;
            HashSet<string> set = new HashSet<string>(ids);
            int hit = 0;
            lock (_gate)
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    if (!set.Contains(_sessions[i].Id) || _sessions[i].Archived == archived) continue;
                    _sessions[i].Archived = archived;
                    SaveSessionUnsafe(_sessions[i]);
                    hit++;
                }
                // 归档掉当前会话时切到第一个未归档会话，避免界面停在已归档项上
                if (archived && set.Contains(_activeSessionId))
                {
                    for (int i = 0; i < _sessions.Count; i++)
                    {
                        if (!_sessions[i].Archived) { _activeSessionId = _sessions[i].Id; break; }
                    }
                }
            }
            if (hit > 0) AddLog("info", archived ? ("已归档 " + hit + " 个会话") : ("已取消归档 " + hit + " 个会话"));
            BroadcastState();
        }

        /// <summary>批量删除：语义与 DeleteSession 一致（运行中拒绝、删完至少留一个新会话）。</summary>
        public void DeleteSessions(List<string> ids)
        {
            if (ids == null || ids.Count == 0) return;
            if (Agent.Running)
            {
                AddLog("warn", "任务运行中，暂时不能删除会话。");
                return;
            }
            HashSet<string> set = new HashSet<string>(ids);
            int hit = 0;
            lock (_gate)
            {
                for (int i = _sessions.Count - 1; i >= 0; i--)
                {
                    if (!set.Contains(_sessions[i].Id)) continue;
                    DropSessionArtifactsUnsafe(_sessions[i]);
                    try
                    {
                        string file = Path.Combine(AppPaths.SessionsDir, _sessions[i].Id + ".json");
                        if (File.Exists(file)) File.Delete(file);
                    }
                    catch { }
                    _sessions.RemoveAt(i);
                    hit++;
                }
                if (_sessions.Count == 0) CreateSessionUnsafe();
                else if (set.Contains(_activeSessionId)) _activeSessionId = _sessions[0].Id;
            }
            if (hit > 0) AddLog("info", "已删除 " + hit + " 个会话");
            BroadcastState();
        }

        public Session FindSession(string id)
        {
            lock (_gate)
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    if (_sessions[i].Id == id) return _sessions[i];
                }
                return null;
            }
        }

        /// <summary>导出指定会话（为空时导出当前会话）的 Markdown 文本。</summary>
        public string ExportMarkdown(string id)
        {
            Session s = string.IsNullOrEmpty(id) ? ActiveSession : FindSession(id);
            if (s == null) s = ActiveSession;
            if (s == null) return "";
            return SessionExport.ToMarkdown(s);
        }

        public string ExportFileName(string id)
        {
            Session s = string.IsNullOrEmpty(id) ? ActiveSession : FindSession(id);
            if (s == null) s = ActiveSession;
            return SessionExport.FileName(s);
        }

        public void DeleteSession(string id)
        {
            if (Agent.Running)
            {
                AddLog("warn", "任务运行中，暂时不能删除会话。");
                return;
            }
            lock (_gate)
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    if (_sessions[i].Id == id)
                    {
                        DropSessionArtifactsUnsafe(_sessions[i]);
                        _sessions.RemoveAt(i);
                        break;
                    }
                }
                try
                {
                    string file = Path.Combine(AppPaths.SessionsDir, id + ".json");
                    if (File.Exists(file)) File.Delete(file);
                }
                catch { }
                if (_sessions.Count == 0) CreateSessionUnsafe();
                else if (_activeSessionId == id) _activeSessionId = _sessions[0].Id;
            }
            BroadcastState();
        }

        private void LoadSessions()
        {
            try
            {
                AppPaths.EnsureDirectory(AppPaths.SessionsDir);
                string[] files = Directory.GetFiles(AppPaths.SessionsDir, "*.json");
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        Session s = Json.Parse<Session>(File.ReadAllText(files[i], Encoding.UTF8));
                        if (s != null && !string.IsNullOrEmpty(s.Id))
                        {
                            if (s.Messages == null) s.Messages = new List<Message>();
                            if (s.Title == null) s.Title = "新会话";
                            for (int m = 0; m < s.Messages.Count; m++)
                            {
                                if (s.Messages[m].ToolCalls == null) s.Messages[m].ToolCalls = new List<ToolCall>();
                                for (int t = 0; t < s.Messages[m].ToolCalls.Count; t++)
                                {
                                    if (s.Messages[m].ToolCalls[t].Artifacts == null) s.Messages[m].ToolCalls[t].Artifacts = new List<Artifact>();
                                }
                            }
                            if (s.SchemaVersion < 2) s.SchemaVersion = 2;
                            if (s.Approvals == null) s.Approvals = new List<ApprovalTicket>();
                            // 阶段4：进程重启后，上次未跑完的任务不能悬着——
                            // 把状态未知的工具调用标成 interrupted，任务标成 incomplete，并提示先核验。
                            // 同时清理过期审批票据，保证「批准」不会跨越有效期继续生效。
                            if (SessionRecovery.Recover(s, "进程重启", Config.ApprovalTicketSeconds) != null)
                            {
                                SaveSessionUnsafe(s);
                            }
                            _sessions.Add(s);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("读取会话失败: " + Path.GetFileName(files[i]) + " " + ex.Message);
                    }
                }
                _sessions.Sort(delegate(Session a, Session b) { return b.UpdatedAt.CompareTo(a.UpdatedAt); });
                if (_sessions.Count > 50) _sessions.RemoveRange(50, _sessions.Count - 50);
                if (_sessions.Count > 0)
                {
                    _activeSessionId = _sessions[0].Id;
                    if (string.IsNullOrEmpty(_sessions[0].WorkDir)) _sessions[0].WorkDir = Config.WorkDir;
                }
            }
            catch (Exception ex)
            {
                Log.Error("加载会话失败", ex);
            }
        }

        private void LoadArtifactsFromSessions()
        {
            lock (_gate)
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    Session s = _sessions[i];
                    for (int m = 0; m < s.Messages.Count; m++)
                    {
                        for (int t = 0; t < s.Messages[m].ToolCalls.Count; t++)
                        {
                            ToolCall call = s.Messages[m].ToolCalls[t];
                            for (int a = 0; a < call.Artifacts.Count; a++)
                            {
                                if (!string.IsNullOrEmpty(call.Artifacts[a].Id)) _artifacts[call.Artifacts[a].Id] = call.Artifacts[a];
                            }
                        }
                    }
                }
            }
        }

        private void SaveSessionUnsafe(Session s)
        {
            try
            {
                AppPaths.EnsureDirectory(AppPaths.SessionsDir);
                string file = Path.Combine(AppPaths.SessionsDir, s.Id + ".json");
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, Json.Stringify(s), new UTF8Encoding(false));
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch (Exception ex)
            {
                Log.Warn("保存会话失败: " + ex.Message);
            }
        }

        public void SaveActiveSession()
        {
            lock (_gate)
            {
                Session s = FindActiveSessionUnsafe();
                if (s != null)
                {
                    s.Touch();
                    SaveSessionUnsafe(s);
                }
            }
        }

        public void StartChat(string text, string[] files)
        {
            if (Agent.Running)
            {
                PushSystemError("已有任务在运行，请先等待当前任务结束或点「停止」。");
                return;
            }
            if (string.IsNullOrEmpty(text) && (files == null || files.Length == 0))
            {
                PushSystemError("消息为空，没有可执行的内容。");
                return;
            }
            // 斜杠命令（/help /stats 等）不需要工作目录，放行到 Agent 内部再分流
            bool isSlash = text != null && text.TrimStart().StartsWith("/");
            bool localQuery = (files == null || files.Length == 0) && SelfMaintenance.Command(text).Length > 0;
            if (!isSlash && !localQuery && (string.IsNullOrEmpty(Config.WorkDir) || !Directory.Exists(Config.WorkDir)))
            {
                PushSystemError("请先选择有效的工作目录（右上角「工作目录」或命令面板），否则绿虎无法执行任务。");
                return;
            }
            Session session = ActiveSession;
            if (session != null)
            {
                lock (_gate)
                {
                    if (session.Title == "新会话" && !string.IsNullOrEmpty(text))
                    {
                        string title = TextUtil.AutoTitle(text);
                        session.Title = title.Length == 0 ? "新会话" : title;
                    }
                    session.WorkDir = Config.WorkDir;
                    session.Touch();
                    SaveSessionUnsafe(session);
                }
            }
            // ! 前缀（bang 命令）：不在 UI 本地执行 shell，
            // 而是改写为 canned prompt 走主循环——Agent 自己调 shell 工具，
            // 受 Guard 与审批策略约束，高危命令照样拦。
            string bang = text == null ? "" : text.TrimStart();
            if (bang.StartsWith("!") && bang.Length > 1 && bang[1] != '!')
            {
                string cmd = bang.Substring(1).TrimStart();
                if (cmd.Length > 0)
                {
                    text = "【终端直执】用户在输入框用 ! 前缀直接给了一条命令。请用 shell 工具在工作目录里执行它，把原始输出简要汇报给用户；如果命令有风险，按正常流程请求确认：\n" + cmd;
                }
            }
            BroadcastState();
            Task.Run(async delegate
            {
                try { await Agent.RunAsync(text, files).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log.Error("任务启动失败", ex);
                    PushSystemError("任务失败：" + ex.Message);
                }
            });
        }

        /// <summary>
        /// 继续上次中断的任务（阶段4）。与 StartChat 的区别：不新建任务、不追加用户消息，
        /// 由 AgentRunner 复用原任务记录与预算计数续跑；不可续跑时给出原因而不是静默失败。
        /// </summary>
        /// <summary>
        /// 继续任务。taskId 为空时续跑当前活动任务；给了 taskId 就从「可继续」暂存区取回那个任务再续跑
        /// （取回前会把当前可续跑的任务也存进暂存区，不丢任何一个）。
        /// </summary>
        public bool ResumeActiveTask(string taskId = "")
        {
            if (Agent.Running)
            {
                PushSystemError("已有任务在运行，不能同时续跑。请先等待结束或点「停止」。");
                return false;
            }
            Session session = ActiveSession;
            if (session == null)
            {
                PushSystemError("没有活动会话。");
                return false;
            }
            string wantId = taskId == null ? "" : taskId.Trim();
            if (wantId.Length > 0 && (session.Task == null || session.Task.TaskId != wantId))
            {
                TaskRecord parked = TaskParking.Find(session, wantId);
                if (parked == null)
                {
                    PushSystemError("找不到这个可继续任务（可能已被替换或清理）：" + wantId);
                    return false;
                }
                if (session.Task != null && session.Task.Resumable) TaskParking.Park(session, session.Task);
                TaskParking.Take(session, wantId);
                session.Task = parked;
                SaveActiveSession();
                BroadcastState();
            }
            string why;
            if (!ApprovalStore.CanResume(session, out why))
            {
                PushSystemError("无法继续任务：" + why);
                return false;
            }
            if (string.IsNullOrEmpty(Config.WorkDir) || !Directory.Exists(Config.WorkDir))
            {
                PushSystemError("请先选择有效的工作目录，再继续任务。");
                return false;
            }
            BroadcastState();
            Task.Run(async delegate
            {
                try { await Agent.ResumeAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log.Error("续跑失败", ex);
                    PushSystemError("继续任务失败：" + ex.Message);
                }
            });
            return true;
        }



        /// <summary>外部 /v1/chat/completions 用：跑一轮对话并把最终助手文本返回给调用方。</summary>
        public async Task<string> RunChatExternalAsync(string text)
        {
            if (Agent.Running) throw new InvalidOperationException("已有任务在运行，请稍候再试");
            if (SelfMaintenance.Command(text).Length == 0 && (string.IsNullOrEmpty(Config.WorkDir) || !Directory.Exists(Config.WorkDir)))
                throw new InvalidOperationException("绿虎尚未设置工作目录");
            await Agent.RunAsync(text, null).ConfigureAwait(false);
            Session session = ActiveSession;
            if (session == null || session.Messages == null) return "";
            for (int i = session.Messages.Count - 1; i >= 0; i--)
            {
                Message m = session.Messages[i];
                if (m != null && m.Role == "assistant" && !string.IsNullOrEmpty(m.Text)) return m.Text;
            }
            return "";
        }

        public void Stop()
        {
            Agent.Stop();
            SetPhase("正在停止…");
            TaskCompletionSource<string> tcs;
            lock (_gate) { tcs = _approvalTcs; _approvalTcs = null; _pendingApproval = null; }
            if (tcs != null) tcs.TrySetResult("deny");
            BroadcastState();
        }

        /// <summary>
        /// 暂停：与停止共用取消路径，但任务记为 paused 且可续跑（预算与步数沿用）。
        /// 暂停时若正卡在审批上，这次审批按「未决定」作废（票据记为 denied）：
        /// 续跑时会重新问一次，而不是把没点过的批准偷偷当成同意。
        /// </summary>
        public void Pause()
        {
            Agent.Pause();
            SetPhase("正在暂停…");
            TaskCompletionSource<string> tcs;
            lock (_gate) { tcs = _approvalTcs; _approvalTcs = null; _pendingApproval = null; }
            if (tcs != null) tcs.TrySetResult("deny");
            BroadcastState();
        }

        /// <summary>
        /// 重试：复用当前会话最后一条用户消息（含附件）重新生成。
        /// 删除该用户消息之后的所有回复，避免历史重复。
        /// </summary>
        public void RetryLast()
        {
            if (Agent.Running)
            {
                AddLog("warn", "已有任务在运行。");
                return;
            }
            Session session = ActiveSession;
            if (session == null)
            {
                AddLog("warn", "没有活动会话。");
                return;
            }
            Message lastUser = null;
            lock (_gate)
            {
                for (int i = session.Messages.Count - 1; i >= 0; i--)
                {
                    if (session.Messages[i].Role == "user") { lastUser = session.Messages[i]; break; }
                }
                if (lastUser == null)
                {
                    AddLog("warn", "没有可重试的用户消息。");
                    return;
                }
                // 删除该用户消息及其后的所有回复：RunAsync 会以解析出的原文+附件重建用户消息，
                // 保留旧消息会让会话里出现两条相同输入（本地大脑上下文也会重复一次）。
                int idx = session.Messages.IndexOf(lastUser);
                if (idx >= 0)
                {
                    session.Messages.RemoveRange(idx, session.Messages.Count - idx);
                    session.Touch();
                }
                SaveSessionUnsafe(session);
            }
            // 从消息文本解析附件行，恢复出原始用户输入
            List<string> files = new List<string>();
            StringBuilder body = new StringBuilder();
            foreach (string line in lastUser.Text.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("[附件] ", StringComparison.Ordinal))
                    files.Add(t.Substring("[附件] ".Length).Trim());
                else
                    body.AppendLine(line);
            }
            string text = body.ToString().Trim();
            if (string.IsNullOrEmpty(text) && files.Count == 0)
            {
                AddLog("warn", "没有可重试的内容。");
                return;
            }
            if (string.IsNullOrEmpty(Config.WorkDir) || !Directory.Exists(Config.WorkDir))
            {
                AddLog("error", "请先选择有效的工作目录。");
                return;
            }
            BroadcastState();
            Task.Run(async delegate
            {
                try { await Agent.RunAsync(text, files.ToArray()).ConfigureAwait(false); }
                catch (Exception ex) { Log.Error("重试任务启动失败", ex); AddLog("error", ex.Message); }
            });
        }

        /// <summary>
        /// 编辑消息：更新指定用户消息文本，
        /// 删除其后所有回复，然后以新文本重新运行任务。
        /// </summary>
        public void EditMessage(int index, string newText)
        {
            if (Agent.Running)
            {
                AddLog("warn", "已有任务在运行，请先停止再编辑。");
                return;
            }
            newText = newText == null ? "" : newText.Trim();
            if (newText.Length == 0)
            {
                AddLog("warn", "编辑后的消息为空。");
                return;
            }
            Session session = ActiveSession;
            if (session == null)
            {
                AddLog("warn", "没有活动会话。");
                return;
            }
            // 附件行从编辑后的文本里恢复（与重试同一套解析），编辑不能把附件弄丢。
            List<string> files = new List<string>();
            lock (_gate)
            {
                if (index < 0 || index >= session.Messages.Count)
                {
                    AddLog("warn", "消息索引超出范围。");
                    return;
                }
                if (session.Messages[index].Role != "user")
                {
                    AddLog("warn", "只能编辑用户消息。");
                    return;
                }
                StringBuilder body = new StringBuilder();
                foreach (string line in newText.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.StartsWith("[附件] ", StringComparison.Ordinal))
                        files.Add(t.Substring("[附件] ".Length).Trim());
                    else
                        body.AppendLine(line);
                }
                string parsedText = body.ToString().Trim();
                if (parsedText.Length == 0 && files.Count == 0)
                {
                    AddLog("warn", "编辑后的消息为空。");
                    return;
                }
                // 删除原消息与其后的所有回复：RunAsync 会以新文本重建用户消息。
                session.Messages.RemoveRange(index, session.Messages.Count - index);
                session.Touch();
                SaveSessionUnsafe(session);
                newText = parsedText;
            }
            if (string.IsNullOrEmpty(Config.WorkDir) || !Directory.Exists(Config.WorkDir))
            {
                AddLog("error", "请先选择有效的工作目录。");
                return;
            }
            string[] rerunFiles = files.ToArray();
            BroadcastState();
            Task.Run(async delegate
            {
                try { await Agent.RunAsync(newText, rerunFiles).ConfigureAwait(false); }
                catch (Exception ex) { Log.Error("编辑消息重跑失败", ex); AddLog("error", ex.Message); }
            });
        }

        /// <summary>撤回消息：删除指定消息及其之后的所有内容。</summary>
        public void UndoMessage(int index)
        {
            if (Agent.Running)
            {
                AddLog("warn", "已有任务在运行，请先停止再撤回。");
                return;
            }
            Session session = ActiveSession;
            if (session == null)
            {
                AddLog("warn", "没有活动会话。");
                return;
            }
            lock (_gate)
            {
                if (index < 0 || index >= session.Messages.Count)
                {
                    AddLog("warn", "消息索引超出范围。");
                    return;
                }
                if (session.Messages.Count > index)
                    session.Messages.RemoveRange(index, session.Messages.Count - index);
                session.Touch();
                SaveSessionUnsafe(session);
            }
            BroadcastState();
        }

        /// <summary>
        /// 审批前算「改了什么」：write_file 用新内容、edit 用同一套替换算法试算，结果挂到 call.Diff。
        /// 只是预览——算不出来也不影响审批本身。
        /// </summary>
        private void PrepareApprovalDiff(ToolCall call)
        {
            if (call == null) return;
            string name = (call.Name == null ? "" : call.Name.ToLowerInvariant());
            if (name != "write_file" && name != "edit") return;
            try
            {
                Dictionary<string, object> args = Json.ParseObject(call.ArgsJson);
                if (args == null) return;
                string path = Json.Str(args, "path", "");
                if (path.Length == 0) return;
                string full = Fs.Resolve(Config.WorkDir, path);
                bool exists = File.Exists(full);
                string before = "";
                if (exists)
                {
                    Encoding enc;
                    before = FileOps.ReadTextAuto(full, out enc);
                }
                string after;
                if (name == "edit")
                {
                    if (!exists) return;
                    FileOps.EditResult preview = FileOps.ApplyEdit(before, Json.Str(args, "old_string", ""),
                        Json.Str(args, "new_string", ""), Json.Bool(args, "replace_all", false));
                    if (!preview.Ok)
                    {
                        call.Diff = "（无法预览：这次编辑会失败。" + preview.Error + "）";
                        return;
                    }
                    after = preview.Text;
                }
                else
                {
                    string content = Json.Str(args, "content", "");
                    after = Json.Bool(args, "append", false) && exists ? before + content : content;
                }
                string shown = FileOps.Diff(before, after, path, 120);
                if (shown.Length > 4000) shown = shown.Substring(0, 4000) + "\n…（diff 过长，已截断）";
                call.Diff = shown;
            }
            catch (Exception ex)
            {
                call.Diff = "（无法预览 diff：" + ex.Message + "）";
            }
        }

        public async Task<string> RequestApprovalAsync(ToolCall call, CancellationToken token)
        {
            PrepareApprovalDiff(call);
            TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
            lock (_gate)
            {
                _pendingApproval = call;
                _approvalReview = null;
                _approvalTcs = tcs;
            }
            BroadcastState();
            // 监督闸门（3.14.0）：danger 确认附一份 AI 风险审查。后台跑、25 秒上限、
            // 只展示不放行——审查结果回来时若确认框还开着就刷新显示，用户不必等它。
            if (Config.SupervisorReview && string.Equals(call.Risk, "danger", StringComparison.OrdinalIgnoreCase))
            {
                ToolCall reviewedCall = call;
                string rvName = reviewedCall.Name;
                string rvCommand = reviewedCall.Command;
                string rvReason = reviewedCall.RiskReason;
                string rvArgs = reviewedCall.ArgsJson;
                if (string.IsNullOrEmpty(rvCommand) && !string.IsNullOrEmpty(rvArgs) && rvArgs.Length <= 600)
                    rvCommand = rvArgs;
                Task.Run(async delegate
                {
                    try
                    {
                        ReviewVerdict v = await Supervisor.ReviewAsync(Brains, rvName, rvCommand, rvReason).ConfigureAwait(false);
                        bool push = false;
                        lock (_gate)
                        {
                            if (ReferenceEquals(_pendingApproval, reviewedCall))
                            {
                                _approvalReview = v;
                                push = true;
                            }
                        }
                        if (push) BroadcastState();
                    }
                    catch { }
                });
            }
            using (token.Register(delegate { try { tcs.TrySetCanceled(); } catch { } }))
            {
                string decision;
                try
                {
                    // 审批框长时间无人处理时自动拒绝，避免任务永久卡在“等待确认”。
                    // 时长取配置里的票据有效期，避免两处各写一个数、改一处就静默漂移。
                    int waitSec = Config != null && Config.ApprovalTicketSeconds > 0 ? Config.ApprovalTicketSeconds : 600;
                    Task<string> timeoutTask = Task.Delay(TimeSpan.FromSeconds(waitSec))
                        .ContinueWith<string>(delegate { return "deny"; });
                    Task<string> winner = await Task.WhenAny<string>(tcs.Task, timeoutTask).ConfigureAwait(false);
                    if (winner == timeoutTask)
                    {
                        AddLog("warn", "等待确认超时（10 分钟），已自动拒绝该工具调用。");
                    }
                    decision = winner == tcs.Task ? await tcs.Task.ConfigureAwait(false) : "deny";
                }
                finally
                {
                    // 无论用户确认、超时还是任务被停止，都要把确认框收回去。
                    bool cleared = false;
                    lock (_gate)
                    {
                        if (ReferenceEquals(_approvalTcs, tcs))
                        {
                            _pendingApproval = null;
                            _approvalReview = null;
                            _approvalTcs = null;
                            cleared = true;
                        }
                    }
                    if (cleared) BroadcastState();
                }
                if (decision == "always")
                {
                    lock (_gate) { _alwaysAllow.Add(call.Name); }
                    decision = "allow";
                }
                if (decision == "always_persist")
                {
                    // scope 第三档：
                    // 写一条 tool:* 的 allow 权限规则并立刻落盘，重启后仍有效。
                    ApplyPersistentAllow(call.Name);
                    decision = "allow";
                }
                BroadcastState();
                return decision;
            }
        }

        public bool IsAlwaysAllowed(string toolName)
        {
            lock (_gate) { return _alwaysAllow.Contains(toolName); }
        }

        /// <summary>审批弹层的「永久允许」入口：写规则 + 立即落盘 + 日志。</summary>
        internal void ApplyPersistentAllow(string toolName)
        {
            if (TryAddPersistentAllow(Config, toolName))
            {
                try { Config.Save(); } catch (Exception ex) { AddLog("warn", "永久允许规则落盘失败：" + ex.Message); }
                AddLog("info", "已永久允许工具「" + toolName + "」（写入权限规则，可在设置 → 权限规则中撤销）。");
            }
        }

        /// <summary>
        /// 永久允许的规则写入（纯逻辑，自检直接断言）。返回 false = 没写（空名 / shell / 已存在）。
        /// shell 刻意排除：命令之间差异巨大，工具级永久放行 shell 等于关闭全部命令审批，
        /// 这个粒度的放行应该由用户在设置面板里按「shell:具体命令*」手写。
        /// </summary>
        internal static bool TryAddPersistentAllow(Config cfg, string toolName)
        {
            if (cfg == null || string.IsNullOrWhiteSpace(toolName)) return false;
            if (string.Equals(toolName, "shell", StringComparison.OrdinalIgnoreCase)) return false;
            string match = toolName + ":*";
            lock (cfg)
            {
                if (cfg.PermitRules == null) cfg.PermitRules = new List<PermitRule>();
                for (int i = 0; i < cfg.PermitRules.Count; i++)
                {
                    PermitRule r = cfg.PermitRules[i];
                    if (r != null && string.Equals(r.Match, match, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.Action, "allow", StringComparison.OrdinalIgnoreCase)) return false;
                }
                PermitRule nr = new PermitRule();
                nr.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                nr.Name = "审批弹层·永久允许";
                nr.Match = match;
                nr.Action = "allow";
                cfg.PermitRules.Add(nr);
            }
            return true;
        }

        public void ResetAlwaysAllowed()
        {
            lock (_gate) { _alwaysAllow.Clear(); }
        }

        public void ResolveApproval(string id, string decision)
        {
            ResolveApproval(id, decision, "");
        }

        /// <summary>answer：ask_user 选项卡的用户回答（decision=allow 时随放行带回给 Agent）。</summary>
        public void ResolveApproval(string id, string decision, string answer)
        {
            TaskCompletionSource<string> tcs = null;
            lock (_gate)
            {
                if (_pendingApproval != null && _pendingApproval.Id == id && _approvalTcs != null)
                {
                    tcs = _approvalTcs;
                    _approvalTcs = null;
                    _pendingApproval = null;
                    _approvalAnswer = answer ?? "";
                }
            }
            if (tcs != null)
            {
                if (decision == "allow" || decision == "deny" || decision == "always") tcs.TrySetResult(decision);
                else tcs.TrySetResult("deny");
            }
            BroadcastState();
        }

        /// <summary>取走最近一次审批携带的用户回答（ask_user 专用；取走即清空）。</summary>
        public string TakeApprovalAnswer()
        {
            lock (_gate) { string a = _approvalAnswer; _approvalAnswer = null; return a; }
        }

        public List<Dictionary<string, object>> McpServersDto()
        {
            lock (_gate) { return Mcp.StatusDto(Config); }
        }

        /// <summary>本机其它客户端里已配置的 MCP 服务器（不返回环境密钥明文以外的额外数据）。</summary>
        public List<Dictionary<string, object>> DiscoverMcpDto()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            Dictionary<string, bool> own = new Dictionary<string, bool>();
            lock (_gate)
            {
                if (Config.McpServers != null)
                    foreach (string k in Config.McpServers.Keys) own[k] = true;
            }
            foreach (McpServerDef d in McpDiscovery.Scan())
            {
                Dictionary<string, object> row = d.ToDto();
                row["alreadyAdded"] = own.ContainsKey(d.Name);
                list.Add(row);
            }
            return list;
        }

        /// <summary>新增 / 启停 / 删除一个自定义 MCP 服务器，落库后热重启 MCP 宿主。</summary>
        public void SaveMcpServer(Dictionary<string, object> body)
        {
            string name = (Json.Str(body, "name", "") ?? "").Trim();
            if (name.Length == 0) throw new InvalidOperationException("服务器名称不能为空");
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,40}$"))
                throw new InvalidOperationException("服务器名称只能用字母、数字、- 和 _");
            bool remove = Json.Bool(body, "remove", false);
            lock (_gate)
            {
                if (Config.McpServers == null) Config.McpServers = new Dictionary<string, object>();
                if (remove) Config.McpServers.Remove(name);
                else if (!body.ContainsKey("command"))
                {
                    // 仅启停：已配置的保留原命令；自动发现的 server 先固化一条带开关的配置
                    bool on = Json.Bool(body, "enabled", true);
                    if (Config.McpServers.ContainsKey(name))
                    {
                        Dictionary<string, object> cur = Config.McpServers[name] as Dictionary<string, object>;
                        if (cur != null) cur["enabled"] = on;
                    }
                    else
                    {
                        McpServerDef found = null;
                        foreach (McpServerDef sd in McpDiscovery.Scan()) if (sd.Name == name) { found = sd; break; }
                        if (found == null) throw new InvalidOperationException("找不到这个服务器，请填写启动命令");
                        found.Enabled = on;
                        Config.McpServers[name] = found.ToDto();
                    }
                }
                else
                {
                    McpServerDef d = McpDiscovery.ParseDef(name, body, "绿虎配置");
                    if (d == null) throw new InvalidOperationException("请填写启动命令");
                    d.Enabled = Json.Bool(body, "enabled", true);
                    Config.McpServers[name] = d.ToDto();
                }
                Config.Save();
            }
            Mcp.Restart(Config);
            BroadcastState();
        }

        public void UpdateConfig(Dictionary<string, object> body)
        {
            bool changed = false;
            bool probeAfterSave = false;
            string oldTheme = Config.Theme;
            lock (_gate)
            {
                string workDir = Json.Str(body, "workDir", Config.WorkDir);
                if (workDir != null)
                {
                    workDir = workDir.Trim();
                    if (workDir.Length > 0 && !Directory.Exists(workDir))
                        throw new InvalidOperationException("工作目录不存在: " + workDir);
                    if (Config.WorkDir != workDir) { Config.WorkDir = workDir; changed = true; }
                }
                if (body.ContainsKey("autoRun")) { Config.AutoRun = Json.Bool(body, "autoRun", Config.AutoRun); changed = true; }
                if (body.ContainsKey("theme")) { Config.Theme = Json.Str(body, "theme", Config.Theme); changed = true; }
                if (body.ContainsKey("maxSteps")) { Config.MaxSteps = Json.Int(body, "maxSteps", Config.MaxSteps); changed = true; }
                if (body.ContainsKey("commandTimeout")) { Config.CommandTimeout = Json.Int(body, "commandTimeout", Config.CommandTimeout); changed = true; }
                if (body.ContainsKey("replyTimeout")) { Config.ReplyTimeout = Json.Int(body, "replyTimeout", Config.ReplyTimeout); changed = true; }
                // —— 预算与重试：这些字段此前只存在于 config.json，界面既看不到也改不了 ——
                if (body.ContainsKey("taskTimeoutSeconds")) { Config.TaskTimeoutSeconds = Json.Int(body, "taskTimeoutSeconds", Config.TaskTimeoutSeconds); changed = true; }
                if (body.ContainsKey("maxTaskTokens")) { Config.MaxTaskTokens = Json.Int(body, "maxTaskTokens", Config.MaxTaskTokens); changed = true; }
                if (body.ContainsKey("maxToolRetries")) { Config.MaxToolRetries = Json.Int(body, "maxToolRetries", Config.MaxToolRetries); changed = true; }
                if (body.ContainsKey("maxRepairs")) { Config.MaxRepairs = Json.Int(body, "maxRepairs", Config.MaxRepairs); changed = true; }
                if (body.ContainsKey("showInspector")) { Config.ShowInspector = Json.Bool(body, "showInspector", Config.ShowInspector); changed = true; }
                if (body.ContainsKey("hideOnScreenshot")) { Config.HideOnScreenshot = Json.Bool(body, "hideOnScreenshot", Config.HideOnScreenshot); changed = true; }
                if (body.ContainsKey("uiMotion")) { Config.UiMotion = Json.Bool(body, "uiMotion", Config.UiMotion); changed = true; }
                if (body.ContainsKey("uiSounds")) { Config.UiSounds = Json.Bool(body, "uiSounds", Config.UiSounds); changed = true; }
                // —— Harness 适配字段 ——
                if (body.ContainsKey("mode")) { Config.Mode = Json.Str(body, "mode", Config.Mode); changed = true; }
                if (body.ContainsKey("reasoningEffort")) { Config.ReasoningEffort = Json.Str(body, "reasoningEffort", Config.ReasoningEffort); changed = true; }
                bool providerTouched = false;
                if (body.ContainsKey("providerId")) { Config.ProviderId = Json.Str(body, "providerId", Config.ProviderId); changed = true; providerTouched = true; }
                if (body.ContainsKey("providerKey")) { Config.ProviderKey = Json.Str(body, "providerKey", Config.ProviderKey); changed = true; providerTouched = true; }
                if (body.ContainsKey("providerEndpoint")) { Config.ProviderEndpoint = Json.Str(body, "providerEndpoint", Config.ProviderEndpoint); changed = true; providerTouched = true; }
                if (body.ContainsKey("providerModel")) { Config.ProviderModel = Json.Str(body, "providerModel", Config.ProviderModel); changed = true; providerTouched = true; }
                bool poolTouched = false;
                if (body.ContainsKey("autoPool")) { Config.AutoPool = Json.Bool(body, "autoPool", Config.AutoPool); changed = true; poolTouched = true; }
                if (body.ContainsKey("providerPool"))
                {
                    List<object> rawPool = Json.Array(body, "providerPool");
                    List<ProviderPoolEntry> pool = new List<ProviderPoolEntry>();
                    if (rawPool != null)
                    {
                        foreach (object o in rawPool)
                        {
                            Dictionary<string, object> r = o as Dictionary<string, object>;
                            if (r == null) continue;
                            ProviderPoolEntry e = new ProviderPoolEntry();
                            e.Id = Json.Str(r, "id", e.Id);
                            e.ProviderId = Json.Str(r, "providerId", "");
                            e.Endpoint = Json.Str(r, "endpoint", "");
                            e.Model = Json.Str(r, "model", "");
                            e.Key = Json.Str(r, "key", "");
                            e.Disabled = Json.Bool(r, "disabled", false);
                            if (e.Endpoint.Length == 0 && e.ProviderId.Length == 0) continue;
                            pool.Add(e);
                        }
                    }
                    Config.ProviderPool = pool;
                    changed = true;
                    poolTouched = true;
                }
                // —— 阶段7：试运行安全开关 ——
                if (body.ContainsKey("readOnly")) { Config.ReadOnly = Json.Bool(body, "readOnly", Config.ReadOnly); changed = true; }
                if (body.ContainsKey("supervisorReview")) { Config.SupervisorReview = Json.Bool(body, "supervisorReview", Config.SupervisorReview); changed = true; }
                if (body.ContainsKey("networkPolicy"))
                {
                    string np = Json.Str(body, "networkPolicy", "off").Trim().ToLowerInvariant();
                    if (np != "off" && np != "allowlist") np = "off";
                    if (!string.Equals(Config.NetworkPolicy, np, StringComparison.Ordinal)) { Config.NetworkPolicy = np; changed = true; }
                }
                if (body.ContainsKey("networkAllowlist"))
                {
                    List<string> allow = new List<string>();
                    System.Collections.ArrayList arr = body["networkAllowlist"] as System.Collections.ArrayList;
                    if (arr != null)
                    {
                        foreach (object item in arr)
                        {
                            string host = (item == null ? "" : item.ToString().Trim().ToLowerInvariant());
                            if (host.Length > 0 && host.IndexOf(' ') < 0 && allow.IndexOf(host) < 0) allow.Add(host);
                        }
                    }
                    Config.NetworkAllowlist = allow;
                    changed = true;
                }
                if (body.ContainsKey("watchMode")) { Config.WatchMode = Json.Bool(body, "watchMode", Config.WatchMode); changed = true; }
                if (body.ContainsKey("watchIdleMinutes")) { Config.WatchIdleMinutes = Math.Max(1, Math.Min(120, Json.Int(body, "watchIdleMinutes", Config.WatchIdleMinutes))); changed = true; }
                if (body.ContainsKey("sandboxMode"))
                {
                    bool wantSandbox = Json.Bool(body, "sandboxMode", Config.SandboxMode);
                    if (wantSandbox && !Config.SandboxMode)
                    {
                        string sbxErr;
                        if (!Sandbox.EnsureSandboxDir(out sbxErr))
                            throw new InvalidOperationException("沙箱目录准备失败：" + sbxErr);
                        Config.SandboxMode = true;
                        // 开启即把工作目录切到沙箱目录：用户不需要理解两个目录的概念
                        if (!Sandbox.IsInSandbox(Config.WorkDir)) Config.WorkDir = Sandbox.SandboxDir();
                        changed = true;
                    }
                    else if (!wantSandbox && Config.SandboxMode)
                    {
                        Config.SandboxMode = false;   // 关闭不动工作目录，切回哪里由用户自己决定
                        changed = true;
                    }
                }
                if (body.ContainsKey("highRiskPolicy")) { Config.HighRiskPolicy = Json.Str(body, "highRiskPolicy", Config.HighRiskPolicy); changed = true; }
                // 工具权限白/黑名单：复用 /api/config，不另开一套接口
                if (body.ContainsKey("permitRules"))
                {
                    List<object> raw = Json.Array(body, "permitRules");
                    List<PermitRule> rules = new List<PermitRule>();
                    if (raw != null)
                    {
                        foreach (object o in raw)
                        {
                            Dictionary<string, object> r = o as Dictionary<string, object>;
                            if (r == null) continue;
                            PermitRule pr = new PermitRule();
                            pr.Id = Json.Str(r, "id", pr.Id);
                            pr.Name = Json.Str(r, "name", "");
                            pr.Match = Json.Str(r, "match", "");
                            pr.Action = Json.Str(r, "action", "allow");
                            if (pr.Match.Length == 0) continue;
                            if (pr.Action != "deny") pr.Action = "allow";
                            rules.Add(pr);
                        }
                    }
                    Config.PermitRules = rules;
                    changed = true;
                }
                Config.Normalize();
                if (changed) Config.Save();
                if (providerTouched || poolTouched)
                {
                    // 服务商或大脑池变更：选举状态作废，回到「按设置探测」，下次选举不粘滞。
                    Brains.OnConfigChanged();
                }
                if (providerTouched)
                {
                    // 换了服务商/模型：探测缓存立即失效，旧模型的对话历史对新模型没有意义，一并清掉。
                    // 收紧后立刻探测一次（后台线程），用户点完保存 1 秒内就能看到「已就绪 / 无响应」，
                    // 不用干等到下一次 5 秒轮询。
                    Brains.Api.ForgetProbe();
                    Brains.Api.ClearHistory();
                    probeAfterSave = true;
                }
                if (changed && Config.Theme != oldTheme) ChromeTheme.Set(Config.ResolveTheme(Config.Theme)); // 深浅切换时通知浏览器窗口一起换
                Session s = FindActiveSessionUnsafe();
                if (s != null && !string.IsNullOrEmpty(Config.WorkDir)) s.WorkDir = Config.WorkDir;
            }
            if (changed) BroadcastState();
            if (probeAfterSave)
            {
                Task.Run(delegate
                {
                    try { Brains.Api.Probe(true); BroadcastState(); } catch { }
                });
            }
        }

        public string PickFolder()
        {
            try
            {
                return Ui.Invoke<string>(delegate
                {
                    using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                    {
                        dialog.Description = "选择绿虎工作目录";
                        dialog.ShowNewFolderButton = true;
                        if (dialog.ShowDialog() == DialogResult.OK) return dialog.SelectedPath;
                        return "";
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error("选择目录失败", ex);
                return "";
            }
        }

        // ================= Harness 适配：工作区 / 定时任务 =================

        private Dictionary<string, object> WorkspacesDto()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            List<object> list = new List<object>();
            List<WorkspaceCfg> ws = Config.Workspaces;
            for (int i = 0; i < ws.Count; i++)
            {
                WorkspaceCfg w = ws[i];
                if (w == null) continue;
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["id"] = w.Id;
                item["name"] = w.Name;
                item["path"] = w.Path;
                item["active"] = string.Equals(Config.WorkDir, w.Path, StringComparison.OrdinalIgnoreCase);
                list.Add(item);
            }
            d["list"] = list;
            return d;
        }

        private List<object> TasksDto()
        {
            List<object> list = new List<object>();
            List<ScheduledTask> tasks = Config.ScheduledTasks;
            for (int i = 0; i < tasks.Count; i++)
            {
                ScheduledTask t = tasks[i];
                if (t == null) continue;
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["id"] = t.Id;
                item["name"] = t.Name;
                item["schedule"] = t.Schedule;
                item["prompt"] = t.Prompt;
                item["enabled"] = t.Enabled;
                item["lastRunAt"] = t.LastRunAt;
                list.Add(item);
            }
            return list;
        }

        /// <summary>定时任务调度器读取的配置快照。</summary>
        public List<ScheduledTask> SnapshotScheduledTasks()
        {
            lock (_gate)
            {
                List<ScheduledTask> copy = new List<ScheduledTask>();
                for (int i = 0; i < Config.ScheduledTasks.Count; i++)
                    if (Config.ScheduledTasks[i] != null) copy.Add(Config.ScheduledTasks[i]);
                return copy;
            }
        }

        /// <summary>定时任务到期：新建会话并把任务 Prompt 交给 Agent 执行。</summary>
        public void RunScheduledTask(ScheduledTask task, long triggeredAt)
        {
            try
            {
                // 有任务在跑时不标记「已运行」也不静默丢弃：下一轮 tick 会重试，
                // 否则定时任务与用户任务撞车就会被永久跳过，且界面上什么也看不到。
                if (Agent.Running)
                {
                    AddLog("warn", "定时任务「" + task.Name + "」到点，但当前有任务在运行，稍后自动重试");
                    return;
                }
                // StartChat 会因工作目录无效直接拒绝：先在这里校验，拒绝路径不消耗当天的触发
                //（以前先落 LastRunAt 再派发，被拒后当天就静默丢失了）。
                if (string.IsNullOrEmpty(Config.WorkDir) || !Directory.Exists(Config.WorkDir))
                {
                    AddLog("error", "定时任务「" + task.Name + "」到点，但没有有效的工作目录，本轮跳过（未标记已运行，修正工作目录后会重试）");
                    return;
                }
                lock (_gate)
                {
                    for (int i = 0; i < Config.ScheduledTasks.Count; i++)
                    {
                        if (Config.ScheduledTasks[i] != null && Config.ScheduledTasks[i].Id == task.Id)
                        {
                            Config.ScheduledTasks[i].LastRunAt = triggeredAt;
                            break;
                        }
                    }
                    Config.Save();
                }
                Log.Info("定时任务触发: " + task.Name + " → " + task.Prompt);
                AddLog("info", "定时任务「" + task.Name + "」已触发，开始执行: " + task.Prompt);
                NewSession();
                StartChat(task.Prompt, null);
            }
            catch (Exception ex)
            {
                Log.Error("定时任务执行失败", ex);
                AddLog("error", "定时任务「" + task.Name + "」执行失败: " + ex.Message);
            }
        }

        /// <summary>工作区管理：add / delete / switch。</summary>
        public string ManageWorkspaces(string action, Dictionary<string, object> body)
        {
            lock (_gate)
            {
                if (action == "add")
                {
                    string name = Json.Str(body, "name", "").Trim();
                    string path = Json.Str(body, "path", "").Trim();
                    if (name.Length == 0) name = path.Length > 0 ? Path.GetFileName(path.TrimEnd('\\')) : "新工作区";
                    if (name.Length == 0) name = "工作区";
                    if (path.Length > 0 && !Directory.Exists(path)) throw new InvalidOperationException("工作区路径不存在: " + path);
                    WorkspaceCfg w = new WorkspaceCfg();
                    w.Name = name;
                    w.Path = path.Length > 0 ? path : Config.WorkDir;
                    Config.Workspaces.Add(w);
                    if (w.Path.Length > 0 && Directory.Exists(w.Path)) Config.WorkDir = w.Path;
                    Config.Save();
                    BroadcastState();
                    return "已添加工作区「" + w.Name + "」并切换为当前工作目录";
                }
                if (action == "delete")
                {
                    string id = Json.Str(body, "id", "");
                    for (int i = 0; i < Config.Workspaces.Count; i++)
                    {
                        if (Config.Workspaces[i] != null && Config.Workspaces[i].Id == id)
                        {
                            Config.Workspaces.RemoveAt(i);
                            Config.Save();
                            BroadcastState();
                            return "已删除工作区";
                        }
                    }
                    return "未找到该工作区";
                }
                if (action == "switch")
                {
                    string id = Json.Str(body, "id", "");
                    for (int i = 0; i < Config.Workspaces.Count; i++)
                    {
                        if (Config.Workspaces[i] != null && Config.Workspaces[i].Id == id)
                        {
                            string path = Config.Workspaces[i].Path;
                            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                                throw new InvalidOperationException("工作区路径不存在: " + path);
                            Config.WorkDir = path;
                            Config.Save();
                            Session s = FindActiveSessionUnsafe();
                            if (s != null) s.WorkDir = path;
                            BroadcastState();
                            return "已切换到工作区「" + Config.Workspaces[i].Name + "」";
                        }
                    }
                    return "未找到该工作区";
                }
                throw new InvalidOperationException("未知操作: " + action);
            }
        }

        /// <summary>定时任务管理：add / update / delete / toggle。</summary>
        public string ManageScheduledTasks(string action, Dictionary<string, object> body)
        {
            lock (_gate)
            {
                if (action == "add")
                {
                    ScheduledTask t = new ScheduledTask();
                    t.Name = Json.Str(body, "name", "新任务").Trim();
                    t.Schedule = Json.Str(body, "schedule", "09:00").Trim();
                    t.Prompt = Json.Str(body, "prompt", "").Trim();
                    t.Enabled = Json.Bool(body, "enabled", true);
                    if (t.Name.Length == 0) t.Name = "新任务";
                    if (t.Prompt.Length == 0) throw new InvalidOperationException("定时任务需要填写要执行的指令（Prompt）");
                    Config.ScheduledTasks.Add(t);
                    Config.Save();
                    BroadcastState();
                    return "已添加定时任务「" + t.Name + "」（" + t.Schedule + "）";
                }
                if (action == "update")
                {
                    string id = Json.Str(body, "id", "");
                    for (int i = 0; i < Config.ScheduledTasks.Count; i++)
                    {
                        ScheduledTask t = Config.ScheduledTasks[i];
                        if (t == null || t.Id != id) continue;
                        if (body.ContainsKey("name")) t.Name = Json.Str(body, "name", t.Name).Trim();
                        if (body.ContainsKey("schedule")) t.Schedule = Json.Str(body, "schedule", t.Schedule).Trim();
                        if (body.ContainsKey("prompt")) t.Prompt = Json.Str(body, "prompt", t.Prompt).Trim();
                        if (body.ContainsKey("enabled")) t.Enabled = Json.Bool(body, "enabled", t.Enabled);
                        if (t.Prompt.Length == 0) throw new InvalidOperationException("定时任务需要填写要执行的指令（Prompt）");
                        Config.Save();
                        BroadcastState();
                        return "已更新定时任务「" + t.Name + "」";
                    }
                    return "未找到该定时任务";
                }
                if (action == "delete")
                {
                    string id = Json.Str(body, "id", "");
                    for (int i = 0; i < Config.ScheduledTasks.Count; i++)
                    {
                        if (Config.ScheduledTasks[i] != null && Config.ScheduledTasks[i].Id == id)
                        {
                            Config.ScheduledTasks.RemoveAt(i);
                            Config.Save();
                            BroadcastState();
                            return "已删除定时任务";
                        }
                    }
                    return "未找到该定时任务";
                }
                if (action == "toggle")
                {
                    string id = Json.Str(body, "id", "");
                    bool enable = Json.Bool(body, "enabled", true);
                    for (int i = 0; i < Config.ScheduledTasks.Count; i++)
                    {
                        ScheduledTask t = Config.ScheduledTasks[i];
                        if (t == null || t.Id != id) continue;
                        t.Enabled = enable;
                        Config.Save();
                        BroadcastState();
                        return enable ? "已启用定时任务" : "已暂停定时任务";
                    }
                    return "未找到该定时任务";
                }
                throw new InvalidOperationException("未知操作: " + action);
            }
        }


        public string[] PickFiles()
        {
            try
            {
                return Ui.Invoke<string[]>(delegate
                {
                    using (OpenFileDialog dialog = new OpenFileDialog())
                    {
                        dialog.Title = "选择要附加给模型的文件";
                        dialog.Multiselect = true;
                        dialog.Filter = "所有文件|*.*";
                        if (dialog.ShowDialog() == DialogResult.OK) return dialog.FileNames;
                        return new string[0];
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error("选择文件失败", ex);
                return new string[0];
            }
        }

        public string SaveUpload(string name, byte[] bytes)
        {
            AppPaths.EnsureDirectory(AppPaths.UploadsDir);
            string safe = Fs.SafeName(string.IsNullOrEmpty(name) ? "upload.bin" : name);
            string path = Path.Combine(AppPaths.UploadsDir, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + safe);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>把工作目录里的文件回滚到某个改动前快照。
        /// 校验：快照必须来自 backups 目录、目标必须落在工作目录内；
        /// 回滚前先把当前内容再存一份快照，保证这一步也能再回滚。</summary>
        public string RestoreBackup(string snapshotPath, string targetPath, out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrEmpty(snapshotPath)) { error = "缺少快照路径"; return ""; }
                string snap = Path.GetFullPath(snapshotPath);
                string backupsRoot = Path.GetFullPath(AppPaths.BackupsDir);
                if (!Fs.IsInside(backupsRoot, snap)) { error = "快照不在备份目录内，已拒绝"; return ""; }
                if (!File.Exists(snap)) { error = "快照文件已不存在（可能已被清理）"; return ""; }
                string target = Fs.Resolve(Config.WorkDir, targetPath ?? "");
                if (!Fs.IsInside(Config.WorkDir, target)) { error = "目标路径在工作目录之外，已拒绝"; return ""; }
                if (!File.Exists(target)) { error = "目标文件已不存在：" + target; return ""; }
                string keepError;
                FileOps.Snapshot(target, "rollback", out keepError);
                File.Copy(snap, target, true);
                AddLog("info", "已回滚 " + target + " 到快照 " + Path.GetFileName(snap));
                return target;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return "";
            }
        }

        /// <summary>把工作目录恢复到某个 shell checkpoint（清单位于 backups 目录；根目录、清单路径校验在 Checkpoint 内）。</summary>
        public string RestoreCheckpoint(string manifestPath, out string error)
        {
            error = "";
            try
            {
                string summary = Checkpoint.Restore(manifestPath, Config.WorkDir, out error);
                if (string.IsNullOrEmpty(summary)) return "";
                AddLog("info", "checkpoint 回滚：" + summary);
                return summary;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return "";
            }
        }

        /// <summary>「设置 → 记忆」候选清单（自动沉淀待审核）。</summary>
        public List<Dictionary<string, object>> MemoryCandidatesDto()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            List<string> items = AgentMemory.LoadCandidates(Config.WorkDir);
            for (int i = 0; i < items.Count; i++)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = i;
                d["time"] = items[i].Length > 16 ? items[i].Substring(0, 16) : items[i];
                d["text"] = items[i];
                list.Add(d);
            }
            return list;
        }

        /// <summary>采纳/丢弃一条记忆候选。accept=true 进项目记忆（封顶生效）。</summary>
        public bool ReviewMemoryCandidate(int id, bool accept, out string error)
        {
            error = "";
            string err;
            string line = accept ? AgentMemory.AcceptCandidate(Config.WorkDir, id, out err) : AgentMemory.DismissCandidate(Config.WorkDir, id, out err);
            if (string.IsNullOrEmpty(line)) { error = err; return false; }
            AddLog("info", accept ? "记忆候选已采纳进项目记忆" : "记忆候选已丢弃");
            return true;
        }

        public Artifact CreateArtifact(string path, string mime, string kind)
        {
            Artifact a = new Artifact();
            a.Path = path;
            a.Name = Path.GetFileName(path);
            a.Mime = mime;
            a.Kind = kind;
            try { a.Size = new FileInfo(path).Length; } catch { a.Size = 0; }
            lock (_gate)
            {
                _artifacts[a.Id] = a;
                _artifactOrder.Add(a.Id);
                TrimArtifactsUnsafe();
            }
            return a;
        }

        /// <summary>artifact 只进不出会让字典无限增长，超过上限时淘汰最早生成的那批。</summary>
        private void TrimArtifactsUnsafe()
        {
            while (_artifactOrder.Count > MaxArtifacts)
            {
                string oldest = _artifactOrder[0];
                _artifactOrder.RemoveAt(0);
                _artifacts.Remove(oldest);
            }
        }

        /// <summary>删除会话时把该会话产生的 artifact 一并摘掉，已删会话的文件不再能通过 /api/artifact/&lt;id&gt; 下载。</summary>
        private void DropSessionArtifactsUnsafe(Session s)
        {
            if (s == null || s.Messages == null) return;
            for (int m = 0; m < s.Messages.Count; m++)
            {
                List<ToolCall> calls = s.Messages[m].ToolCalls;
                if (calls == null) continue;
                for (int t = 0; t < calls.Count; t++)
                {
                    List<Artifact> arts = calls[t].Artifacts;
                    if (arts == null) continue;
                    for (int a = 0; a < arts.Count; a++)
                    {
                        string aid = arts[a].Id;
                        if (string.IsNullOrEmpty(aid)) continue;
                        _artifacts.Remove(aid);
                        _artifactOrder.Remove(aid);
                    }
                }
            }
        }

        public Artifact GetArtifact(string id)
        {
            lock (_gate)
            {
                Artifact a;
                if (_artifacts.TryGetValue(id, out a)) return a;
                return null;
            }
        }

        public void RegisterProcess(Process process)
        {
            lock (_gate) { _processes.Add(process); }
        }

        public void UnregisterProcess(Process process)
        {
            lock (_gate) { _processes.Remove(process); }
        }

        /// <summary>重开控制台窗口：浏览器渲染异常（白屏/卡死）时的兜底，不涉及任何模型连接。</summary>
        public void RestartBrowser()
        {
            Task.Run(delegate
            {
                try
                {
                    BrowserWindow.CloseAll();
                    Thread.Sleep(300);
                    OpenUi();
                    AddLog("info", "控制台窗口已重开。");
                }
                catch (Exception ex) { Log.Error("重开控制台失败", ex); AddLog("error", "重开控制台失败：" + ex.Message); }
            });
        }

        private void OpenStartupWindows(object sender, EventArgs e)
        {
            Application.Idle -= OpenStartupWindows;
            OpenUi();
        }

        public void OpenUi()
        {
            if (!BrowserWindow.Available)
            {
                Log.Error("绿虎浏览器不可用：缺少 WebView2 运行时");
                AddLog("error", "绿虎浏览器组件不可用。请确认系统已安装 Microsoft Edge WebView2 Runtime，且 lib\\WebView2 目录存在。");
                try
                {
                    MessageBox.Show("绿虎的控制台需要绿虎浏览器（WebView2 Runtime）。\n\n请安装 Microsoft Edge WebView2 Runtime 后重新打开。",
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
                return;
            }
            if (!BrowserWindow.OpenConsole(_uiUrl))
            {
                Log.Error("打开控制台窗口失败");
                AddLog("error", "控制台窗口打开失败，请重试。");
            }
        }

        /// <summary>绿虎浏览器：原生 WebView2 多标签窗口（真渲染）。组件缺失时给出提示。</summary>
        public bool OpenGreenTigerBrowser(string url)
        {
            if (!BrowserWindow.Available)
            {
                AddLog("error", "绿虎浏览器组件不可用（缺少 WebView2 运行时）。请确认系统已安装 Microsoft Edge WebView2 Runtime，且 lib\\WebView2 目录存在。");
                return false;
            }
            bool ok = false;
            try { ok = BrowserWindow.OpenTab(url, true); }
            catch (Exception ex) { Log.Error("打开绿虎浏览器失败", ex); }
            if (ok)
            {
                AddLog("info", "绿虎浏览器已打开" + (string.IsNullOrEmpty(url) ? "" : "：" + url));
            }
            else
            {
                AddLog("error", "绿虎浏览器打开失败。");
            }
            return ok;
        }

        /// <summary>在资源管理器中打开数据目录内的指定目录（供插件/UI 管理等使用）。</summary>
        public bool OpenExplorer(string relPath)
        {
            try
            {
                string root = AppPaths.Root;
                string full = Path.GetFullPath(Path.Combine(root, relPath ?? ""));
                if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(full, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                {
                    AddLog("warn", "拒绝打开数据目录之外的路径：" + full);
                    return false;
                }
                if (!Directory.Exists(full)) Directory.CreateDirectory(full);
                using (System.Diagnostics.Process.Start("explorer.exe", "\"" + full + "\"")) { }
                AddLog("info", "已打开目录：" + full);
                return true;
            }
            catch (Exception ex)
            {
                AddLog("error", "打开目录失败：" + ex.Message);
                return false;
            }
        }

        private void PollStatus()
        {
            if (_pollingStatus || _shuttingDown) return;
            _pollingStatus = true;
            Task.Run(async delegate
            {
                try
                {
                    // 刷新端点探测结果，让界面上的「模型」指示器保持准确。
                    // 没配端点时不打网络（界面显示「未接入模型」，引导用户去配置）。
                    if (!string.IsNullOrEmpty(Config.ProviderEndpoint))
                    {
                        BrainProbe probe = Brains.Api.Probe(false);
                        string effective = EffectiveBrain();
                        if (_lastBrain != effective || _lastLocalDetail != probe.Detail)
                        {
                            _lastBrain = effective;
                            _lastLocalDetail = probe.Detail;
                            BroadcastState();
                        }
                    }
                }
                catch { }
                finally { _pollingStatus = false; }
            });
        }

        /// <summary>任务终态系统通知（托盘气泡）：长任务完成/失败时提醒，秒回的任务不打扰。config.Notify 关闭。</summary>
        public void NotifyTaskEnd(TaskRecord task, string userText)
        {
            if (_tray == null || task == null) return;
            try
            {
                if (!Config.Notify) return;
                if (task.DurationMs > 0 && task.DurationMs < 8000) return;
                string text;
                ToolTipIcon icon = ToolTipIcon.Info;
                if (task.Outcome == TaskOutcome.Succeeded) text = "任务完成";
                else if (task.Outcome == TaskOutcome.Failed) { text = "任务失败"; icon = ToolTipIcon.Error; }
                else if (task.Outcome == TaskOutcome.Incomplete) text = "任务未完成，可继续";
                else return;
                string title = (userText ?? "").Replace("\r", " ").Replace("\n", " ");
                if (title.Length > 60) title = title.Substring(0, 60) + "…";
                if (title.Length == 0) title = "点击托盘图标查看";
                _tray.ShowBalloonTip(5000, AppInfo.Name + " · " + text, title, icon);
            }
            catch { }
        }

        /// <summary>等待用户确认的系统通知：人不在屏幕前时不错过审批。</summary>
        public void NotifyApprovalNeeded(string toolName)
        {
            if (_tray == null) return;
            try
            {
                if (!Config.Notify) return;
                _tray.ShowBalloonTip(6000, AppInfo.Name + " · 需要你的确认", "「" + toolName + "」等待批准", ToolTipIcon.Warning);
            }
            catch { }
        }

        private void CreateTray()
        {
            try
            {
                _tray = new NotifyIcon();
                Icon icon = null;
                try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                if (icon == null) icon = SystemIcons.Application;
                _tray.Icon = icon;
                _tray.Text = AppInfo.Name + " " + AppInfo.Version + " - 桌面 Agent";
                _tray.Visible = true;
                ContextMenuStrip menu = new ContextMenuStrip();
                // 控制台就是绿虎浏览器的主页标签，托盘只留「打开/新建」两个高频入口。
                menu.Items.Add("打开控制台", null, delegate(object sender, EventArgs e) { OpenUi(); });
                menu.Items.Add("新建会话", null, delegate(object sender, EventArgs e) { NewSession(); BroadcastState(); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("打开数据目录", null, delegate(object sender, EventArgs e) { try { Process.Start("explorer.exe", AppPaths.Root); } catch { } });
                menu.Items.Add("退出 " + AppInfo.Name, null, delegate(object sender, EventArgs e) { Shutdown(); });
                _tray.ContextMenuStrip = menu;
                _tray.DoubleClick += delegate(object sender, EventArgs e) { OpenUi(); };
            }
            catch (Exception ex)
            {
                Log.Error("创建托盘失败", ex);
            }
        }

        public void Shutdown()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            Log.Info("GreenTiger 退出");
            try { Agent.Stop(); } catch { }
            try { if (Mcp != null) Mcp.Shutdown(); } catch { }   // 关掉 npx 起的 MCP 子进程，否则退出后残留 node 进程
            try { if (Scheduler != null) { Scheduler.Stop(); Scheduler.Dispose(); } } catch { }
            try { if (_statusTimer != null) _statusTimer.Stop(); } catch { }
            try { Server.Stop(); } catch { }
            try { BrowserWindow.CloseAll(); } catch { }
            lock (_gate)
            {
                for (int i = 0; i < _processes.Count; i++)
                {
                    try { if (!_processes[i].HasExited) _processes[i].Kill(); } catch { }
                }
                _processes.Clear();
            }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { if (File.Exists(AppPaths.UiUrlFile)) File.Delete(AppPaths.UiUrlFile); } catch { }
            try
            {
                Ui.Invoke(delegate { ExitThread(); });
            }
            catch { }
        }
    }
}
