using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace GreenTiger
{
    public static class AppInfo
    {
        public const string Name = "绿虎";
        public const string Version = "3.32.1";
    }

    /// <summary>所有落盘位置。默认在 %APPDATA%\GreenTiger，便携模式可用 --data-dir 覆盖。</summary>
    public static class AppPaths
    {
        private const string DataFolderName = "GreenTiger-data";
        private const string AppFolderName = "GreenTiger";
        private static string _root;
        private static string _dataRootOverride;
        private static bool _portable;

        /// <summary>true 时即使 exe 目录可写也强制使用系统数据目录（对应 --appdata 参数）。</summary>
        public static bool ForceSystemData;

        public static void Initialize(string dataDirOverride)
        {
            _dataRootOverride = dataDirOverride;
            if (!string.IsNullOrEmpty(dataDirOverride))
            {
                _portable = true;
                _root = Path.GetFullPath(dataDirOverride);
            }
            else
            {
                string portableRoot;
                if (!ForceSystemData && TryPortableRoot(out portableRoot))
                {
                    _portable = true;
                    _root = portableRoot;
                }
                else
                {
                    _portable = false;
                    _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
                }
            }
            EnsureDirectory(_root);
            EnsureDirectory(SessionsDir);
            EnsureDirectory(LogsDir);
            EnsureDirectory(UploadsDir);
            EnsureDirectory(RunsDir);
            EnsureDirectory(ShotsDir);
            EnsureDirectory(ArtifactsDir);
            EnsureDirectory(LocksDir);
            EnsureDirectory(BackupsDir);
        }

        /// <summary>
        /// 免安装（便携）模式：exe 所在目录可写、且不在系统/程序目录里时，
        /// 数据自动放到 exe 同目录的 GreenTiger-data\ 下——整个文件夹拷走就是一份完整应用。
        /// 想强制使用系统数据目录，用 --appdata 参数（或 --data-dir 指定其他位置）。
        /// </summary>
        private static bool TryPortableRoot(out string root)
        {
            root = null;
            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                if (entry == null || string.IsNullOrEmpty(entry.Location)) return false;
                string exeDir = Path.GetDirectoryName(entry.Location);
                if (string.IsNullOrEmpty(exeDir)) return false;
                string full = Path.GetFullPath(exeDir).TrimEnd('\\');
                if (full.Length == 0) return false;
                string winRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\');
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\');
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\');
                if (!string.IsNullOrEmpty(winRoot) && (string.Equals(full, winRoot, StringComparison.OrdinalIgnoreCase) || full.Length <= winRoot.Length))
                    return false;
                string lower = full.ToLowerInvariant();
                if (!string.IsNullOrEmpty(pf) && lower.StartsWith(pf.ToLowerInvariant() + "\\")) return false;
                if (!string.IsNullOrEmpty(pf86) && lower.StartsWith(pf86.ToLowerInvariant() + "\\")) return false;
                // 可写探测：能建能删临时文件才算便携
                string probe = Path.Combine(exeDir, ".greentiger_portable_probe");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                root = Path.Combine(exeDir, DataFolderName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string Root { get { return _root; } }
        public static string ConfigFile { get { return Path.Combine(_root, "config.json"); } }
        public static string SessionsDir { get { return Path.Combine(_root, "sessions"); } }
        /// <summary>任务租约锁文件目录（独占句柄，用于跨进程/跨机器互斥）。</summary>
        public static string LocksDir { get { return Path.Combine(_root, "locks"); } }
        public static string LogsDir { get { return Path.Combine(_root, "logs"); } }
        public static string UploadsDir { get { return Path.Combine(Path.GetTempPath(), AppFolderName, "uploads"); } }
        public static string RunsDir { get { return Path.Combine(Path.GetTempPath(), AppFolderName, "runs"); } }
        public static string ShotsDir { get { return Path.Combine(Path.GetTempPath(), AppFolderName, "shots"); } }
        public static string ArtifactsDir { get { return Path.Combine(Path.GetTempPath(), AppFolderName, "artifacts"); } }
        /// <summary>编辑前快照目录（每次 edit/write 前留一份原文，可手工回滚）。</summary>
        public static string BackupsDir { get { return Path.Combine(_root, "backups"); } }
        public static string TempRoot { get { return Path.Combine(Path.GetTempPath(), AppFolderName); } }
        public static string UiUrlFile { get { return Path.Combine(_root, "ui.url"); } }
        public static string LogFile { get { return Path.Combine(LogsDir, "greentiger-" + DateTime.Now.ToString("yyyyMMdd") + ".log"); } }

        /// <summary>运行时 UI 资源目录；WebServer 加载顺序：本目录 → 嵌入资源。</summary>
        public static string UiDir { get { return Path.Combine(_root, "ui"); } }

        /// <summary>插件目录：每个子目录一个插件（manifest.json + 资源文件）。</summary>

        /// <summary>用户级记忆规则文件（~/.claude/CLAUDE.md），自动注入系统提示。</summary>
        public static string RulesFile { get { return Path.Combine(_root, "rules.md"); } }

        public static string LocalDataRoot
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string root = Path.Combine(local, AppFolderName);
                EnsureDirectory(root);
                return root;
            }
        }

        /// <summary>
        /// 浏览器 profile 位置：便携模式（--data-dir）放在数据目录里，方便整体搬走，测试也能互不干扰；
        /// 系统模式放在 %LOCALAPPDATA%\GreenTiger 下。
        /// </summary>
        public static string WebView2Profile
        {
            get
            {
                string dir = _portable
                    ? Path.Combine(_root, "profiles", "browser-profile")
                    : Path.Combine(LocalDataRoot, "browser-profile");
                EnsureDirectory(dir);
                return dir;
            }
        }

        public static void EnsureDirectory(string dir)
        {
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { }
        }
    }

    public static class Log
    {
        private static readonly object Gate = new object();
        public static event Action<string, string> Line;

        public static void Info(string message) { Write("info", message); }
        public static void Warn(string message) { Write("warn", message); }
        public static void Error(string message) { Write("error", message); }

        public static void Error(string message, Exception ex)
        {
            Write("error", message + (ex == null ? "" : " :: " + Describe(ex)));
        }

        /// <summary>
        /// 异常的可读摘要：类型、消息、InnerException 链、首帧堆栈。
        ///
        /// 以前只用 ex.Message 记日志，线上拿到一条「值不在预期范围内」根本无从下手；
        /// 这里统一给出完整链路。堆栈只取前若干帧（够定位调用来源，不至于把日志刷成几 KB）。
        /// </summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "";
            StringBuilder sb = new StringBuilder();
            Exception e = ex;
            int depth = 0;
            while (e != null && depth < 4)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
                e = e.InnerException;
                depth++;
            }
            string frames = FirstFrames(ex, 6);
            if (frames.Length > 0) sb.Append(" | ").Append(frames);
            return sb.ToString();
        }

        /// <summary>取堆栈里最有信息量的几行：跳过异常类自身，只留真正涉及本项目的前 N 帧。</summary>
        private static string FirstFrames(Exception ex, int max)
        {
            if (ex == null || string.IsNullOrEmpty(ex.StackTrace)) return "";
            string[] lines = ex.StackTrace.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder sb = new StringBuilder();
            int kept = 0;
            for (int i = 0; i < lines.Length && kept < max; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                // 形如 "at Namespace.Method(args) 文件路径:行号"，去掉文件路径部分只留署名与行号
                int at = line.IndexOf(" in ", StringComparison.Ordinal);
                if (at > 0) line = line.Substring(0, at);
                if (sb.Length > 0) sb.Append(" >> ");
                sb.Append(line);
                kept++;
            }
            return sb.ToString();
        }


        private static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
            try
            {
                Action<string, string> handler = Line;
                if (handler != null) handler(level, message);
            }
            catch { }
        }
    }

    public class Config
    {
        public string WorkDir { get; set; }
        public bool AutoRun { get; set; }
        public string Theme { get; set; }
        public int MaxSteps { get; set; }
        public int CommandTimeout { get; set; }
        public int ReplyTimeout { get; set; }
        /// <summary>单任务总耗时预算（秒）。到点后任务进入 incomplete，而不是无限跑。</summary>
        public int TaskTimeoutSeconds { get; set; }
        /// <summary>验收未通过时允许的自动修复轮次上限。</summary>
        public int MaxRepairs { get; set; }
        /// <summary>单任务内所有工具的自动重试总次数上限。</summary>
        public int MaxToolRetries { get; set; }
        /// <summary>
        /// 阶段1：单任务 token 预算（输入+输出合计，累计口径，含压缩调用），0=不限。
        /// 默认只按「真实用量」计算；网页版没有用量，见 BudgetUsesEstimatedTokens。
        /// </summary>
        public int MaxTaskTokens { get; set; }
        /// <summary>
        /// 网页版通道按字符粗估 token（中文≈1 token/字，其它≈4 字符/token），用于成本展示。
        /// 估算值永远单独记在 TokensEstimated 里，不与实测值混在一起。
        /// </summary>
        public bool EstimateWebTokens { get; set; }
        /// <summary>
        /// 是否让估算值也参与 token 预算。默认 false：拿不准的数字不适合当硬上限；
        /// 打开后网页版也能被 MaxTaskTokens 拦住（按估算值）。
        /// </summary>
        public bool BudgetUsesEstimatedTokens { get; set; }
        /// <summary>
        /// 网页版 token 估算的校正系数（默认 1.0）。字符数是精确的，只有「字符→token」的比率是经验值；
        /// 知道自己通道偏差的用户可以按实测比例校准（例如中文密集时 1.2）。
        /// </summary>
        public double WebTokenEstimateFactor { get; set; }
        /// <summary>阶段5：规则层（CLAUDE.md/AGENTS.md/rules.md 合计）字符预算，0=默认 8000。</summary>
        public int MaxRulesChars { get; set; }
        /// <summary>阶段5：任务状态锚点字符预算，0=默认 2000。</summary>
        public int MaxStateChars { get; set; }
        /// <summary>阶段5：触发自动压缩的上下文字符阈值，0=默认 60000。</summary>
        public int ContextCompactChars { get; set; }
        /// <summary>阶段5：单任务自动压缩次数上限，0=默认 3。</summary>
        public int ContextCompactLimit { get; set; }
        /// <summary>阶段6/7：告警统计窗口（最近 N 个任务），0=默认 20。</summary>
        public int AlertWindowTasks { get; set; }
        /// <summary>告警最小样本数，少于该数不告警（避免小样本误报），0=默认 5。</summary>
        public int AlertMinSample { get; set; }
        /// <summary>验证失败率告警阈值（0-1，0=用默认 0.5）。</summary>
        public double AlertVerificationFailRate { get; set; }
        /// <summary>预算耗尽率告警阈值（0-1，0=用默认 0.3）。</summary>
        public double AlertBudgetExhaustRate { get; set; }
        /// <summary>任务未成功率告警阈值（0-1，0=用默认 0.6）。</summary>
        public double AlertIncompleteRate { get; set; }
        /// <summary>阶段7：按任务类型的执行权限策略。命中用户消息即覆盖 ReadOnly/HighRiskPolicy/MaxSteps，空字段继承全局。</summary>
        public List<TaskTypePolicy> TaskTypePolicies { get; set; }
        /// <summary>阶段4：审批票据有效期（秒）。过期后同一调用必须重新确认，默认 600（与审批等待超时一致）。</summary>
        public int ApprovalTicketSeconds { get; set; }
        /// <summary>
        /// 阶段7：只读/影子运行。开启后所有会产生副作用的工具（写文件、shell、鼠标键盘、上传、MCP）一律拒绝，
        /// 用于在真实任务上先观察「会做什么」而不真的做。
        /// </summary>
        public bool ReadOnly { get; set; }
        /// <summary>
        /// 阶段7：高风险动作策略。ask=逐条确认（默认）；deny=直接禁止；allow=不确认直接执行（仅限受控试运行）。
        /// </summary>
        public string HighRiskPolicy { get; set; }
        public bool ShowInspector { get; set; }
        /// <summary>沙箱模式（低完整性）：工作目录=数据目录 sandbox\ws，命令只能写沙箱目录，普通命令免审批。见 src/Sandbox.cs。</summary>
        public bool SandboxMode { get; set; }
        /// <summary>监督闸门：danger 确认弹层附 AI 风险审查（只展示不放行）。见 src/Supervisor.cs。</summary>
        public bool SupervisorReview { get; set; }
        /// <summary>网络白名单策略："off"（默认，不限制出网）或 "allowlist"（命令与抓取目标必须在白名单内，否则强制确认）。</summary>
        public string NetworkPolicy { get; set; }
        /// <summary>网络白名单：域名条目（github.com 精确匹配，.github.com 子域随通；空列表+allowlist=全部出网都要确认）。</summary>
        public List<string> NetworkAllowlist { get; set; }
        /// <summary>watch mode（离席自动暂停）：任务运行中超过 WatchIdleMinutes 无用户活动 → 自动暂停（可续跑）。</summary>
        public bool WatchMode { get; set; }
        /// <summary>watch mode 阈值（分钟），1-120。</summary>
        public int WatchIdleMinutes { get; set; }
        public bool HideOnScreenshot { get; set; }
        public bool UiMotion { get; set; }
        public bool UiSounds { get; set; }
        // —— DeepSeek Harness 适配能力 ——
        /// <summary>Agent 模式：plan（只读规划）/ normal（默认）/ acceptEdits（自动接受编辑）。</summary>
        public string Mode { get; set; }
        /// <summary>
        /// 推理强度：low / medium / high / xhigh。极高档在提示词中启用更深的分解与核验；线协议按后端兼容级别降级。
        /// </summary>
        public string ReasoningEffort { get; set; }
        /// <summary>
        /// API 大脑是否走原生 function calling（tools Schema 下发 + message.tool_calls）。
        /// 端点不支持时自动降级文本围栏协议；false 可强制全程围栏协议。
        /// </summary>
        public bool NativeTools { get; set; }
        /// <summary>当前模型服务商 id（Provider 目录，如 deepseek-api / openai / kimi / qwen / ollama…）。</summary>
        public string ProviderId { get; set; }
        /// <summary>服务商 API Key（内存中的明文；磁盘上只存 ProviderKeyEnc 的 DPAPI 密文，见 Save/LoadSecrets）。</summary>
        public string ProviderKey { get; set; }
        /// <summary>ProviderKey 的 DPAPI 密文（dp1:Base64）。只在 Save 时写入，Load 后立即解回明文并清空。</summary>
        public string ProviderKeyEnc { get; set; }
        /// <summary>服务商端点（选择目录后自动填充，可手工改）。</summary>
        public string ProviderEndpoint { get; set; }
        /// <summary>服务商模型（选择目录后自动填充，可手工改）。</summary>
        public string ProviderModel { get; set; }
        /// <summary>命名工作区列表（Workspace）。</summary>
        public List<WorkspaceCfg> Workspaces { get; set; }
        /// <summary>定时任务列表（Scheduled tasks）。</summary>
        public List<ScheduledTask> ScheduledTasks { get; set; }
        /// <summary>权限规则（permission rules）：deny 直接拦截，allow 跳过逐条确认。规则按顺序匹配，deny 优先。</summary>
        public List<PermitRule> PermitRules { get; set; }
        /// <summary>Hooks（tool-use hooks）：工具执行前/后运行本地命令；pre-hook exit 2 阻断调用。</summary>
        public List<HookRule> Hooks { get; set; }
        /// <summary>系统通知（托盘气泡）：任务完成/失败、等待确认时提醒。false 关闭。</summary>
        public bool Notify { get; set; }
        /// <summary>大脑池开关：开启后每次任务开始并行探测池内候选、择优使用，失败自动轮换（任务粒度）。</summary>
        public bool AutoPool { get; set; }
        /// <summary>显式大脑池条目（优先级低于设置里的当前服务商，高于目录免 Key 免费档）。Key 仅存本机 config.json。</summary>
        public List<ProviderPoolEntry> ProviderPool { get; set; }
        /// <summary>MCP 服务器开关覆盖：{"github":true,"postgres":false}。缺省按内置默认。</summary>
        public Dictionary<string, object> McpEnabled { get; set; }
        /// <summary>MCP 进程环境变量：{"GITHUB_TOKEN":"...","DATABASE_URL":"postgres://..."}。</summary>
        public Dictionary<string, object> McpEnv { get; set; }
        /// <summary>用户自定义的 MCP 服务器：常见 mcpServers 格式。</summary>
        public Dictionary<string, object> McpServers { get; set; }

        public Config()
        {
            WorkDir = "";
            AutoRun = false;
            Theme = "auto";
            McpServers = new Dictionary<string, object>();
            MaxSteps = 40;
            CommandTimeout = 120;
            ReplyTimeout = 300;
            TaskTimeoutSeconds = 1800;
            MaxRepairs = 2;
            MaxToolRetries = 5;
            MaxTaskTokens = 0;
            EstimateWebTokens = true;
            BudgetUsesEstimatedTokens = false;
            WebTokenEstimateFactor = 1.0;
            MaxRulesChars = 8000;
            MaxStateChars = 2000;
            ContextCompactChars = 0;   // 0 = 按模型窗口自动推导（ContextPolicy.EffectiveCompactChars）
            ContextCompactLimit = 3;
            AlertWindowTasks = 20;
            AlertMinSample = 5;
            AlertVerificationFailRate = 0.5;
            AlertBudgetExhaustRate = 0.3;
            AlertIncompleteRate = 0.6;
            TaskTypePolicies = new List<TaskTypePolicy>();
            ApprovalTicketSeconds = ApprovalStore.DefaultTtlSeconds;
            ReadOnly = false;
            HighRiskPolicy = "ask";
            SandboxMode = false;
            SupervisorReview = true;
            NetworkPolicy = "off";
            NetworkAllowlist = new List<string>();
            ShowInspector = false;
            // P0：默认把绿虎自身窗口排除在截图外（防反馈回路：模型不该看到含审批框的自己的输出）
            WatchMode = false;
            WatchIdleMinutes = 5;
            HideOnScreenshot = true;
            UiMotion = true;
            UiSounds = false;
            Mode = "normal";
            ReasoningEffort = "medium";
            NativeTools = true;
            ProviderId = "";
            ProviderKey = "";
            ProviderEndpoint = "";
            ProviderModel = "";
            Workspaces = new List<WorkspaceCfg>();
            ScheduledTasks = new List<ScheduledTask>();
            PermitRules = new List<PermitRule>();
            Hooks = new List<HookRule>();
            Notify = true;
            AutoPool = false;
            ProviderPool = new List<ProviderPoolEntry>();
        }

        public static Config Load()
        {
            try
            {
                if (File.Exists(AppPaths.ConfigFile))
                {
                    Config cfg = Json.Parse<Config>(File.ReadAllText(AppPaths.ConfigFile, Encoding.UTF8));
                    if (cfg != null)
                    {
                        cfg.Normalize();
                        cfg.LoadSecrets();
                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取配置失败，使用默认配置", ex);
                // 手改配置打错一个类型就会整份解析失败回退默认——紧接着的任何 Save 会把
                // 默认配置写回磁盘，ProviderKey/权限规则/定时任务全部丢失且无法恢复。
                // 先把坏文件备份出来，用户至少有得救。
                try
                {
                    if (File.Exists(AppPaths.ConfigFile))
                    {
                        string backup = AppPaths.ConfigFile + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                        File.Copy(AppPaths.ConfigFile, backup, true);
                        Log.Error("损坏的配置已备份到: " + backup, null);
                    }
                }
                catch { }
            }
            Config fresh = new Config();
            fresh.Normalize();
            return fresh;
        }

        /// <summary>把 "auto" 解析成实际的 dark/light：读 Windows 应用主题（AppsUseLightTheme）。light/dark 原样返回。</summary>
        public static string ResolveTheme(string theme)
        {
            if (theme == "light" || theme == "dark") return theme;
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    int iv;
                    try { iv = Convert.ToInt32(v); } catch { iv = 1; }
                    return iv == 0 ? "dark" : "light";
                }
            }
            catch { }
            return "light";
        }

        public void Normalize()
        {
            if (WorkDir == null) WorkDir = "";
            if (Theme != "light" && Theme != "dark" && Theme != "auto") Theme = "auto";
            if (ReasoningEffort != "low" && ReasoningEffort != "medium" && ReasoningEffort != "high" && ReasoningEffort != "xhigh") ReasoningEffort = "medium";
            if (MaxSteps < 1) MaxSteps = 1;
            if (MaxSteps > 120) MaxSteps = 120;
            if (CommandTimeout < 5) CommandTimeout = 5;
            if (CommandTimeout > 3600) CommandTimeout = 3600;
            if (ReplyTimeout < 30) ReplyTimeout = 30;
            if (ReplyTimeout > 1800) ReplyTimeout = 1800;
            if (TaskTimeoutSeconds < 30) TaskTimeoutSeconds = 30;
            if (TaskTimeoutSeconds > 86400) TaskTimeoutSeconds = 86400;
            if (MaxRepairs < 0) MaxRepairs = 0;
            if (MaxRepairs > 10) MaxRepairs = 10;
            if (MaxToolRetries < 0) MaxToolRetries = 0;
            if (MaxToolRetries > 50) MaxToolRetries = 50;
            if (MaxTaskTokens < 0) MaxTaskTokens = 0;
            if (MaxTaskTokens > 20000000) MaxTaskTokens = 20000000;
            if (MaxRulesChars < 0) MaxRulesChars = 0;
            if (MaxRulesChars > 100000) MaxRulesChars = 100000;
            if (MaxStateChars < 0) MaxStateChars = 0;
            if (MaxStateChars > 20000) MaxStateChars = 20000;
            // 旧版本的默认阈值是写死的 60000（网页版经验值）。现在阈值按模型窗口推导，
            // 把「恰好等于旧默认值」的配置归一为 0（自动），避免老配置永远用不上新公式；
            // 显式配成其它值仍然视为用户覆盖，优先级最高。
            if (ContextCompactChars == 60000) ContextCompactChars = 0;
            if (ContextCompactChars < 0) ContextCompactChars = 0;
            if (ContextCompactChars > 2000000) ContextCompactChars = 2000000;
            if (ContextCompactLimit < 0) ContextCompactLimit = 0;
            if (ContextCompactLimit > 50) ContextCompactLimit = 50;
            if (WebTokenEstimateFactor <= 0) WebTokenEstimateFactor = 1.0;
            if (WebTokenEstimateFactor > 5) WebTokenEstimateFactor = 5;
            if (AlertWindowTasks < 0) AlertWindowTasks = 0;
            if (AlertWindowTasks > 1000) AlertWindowTasks = 1000;
            if (AlertMinSample < 0) AlertMinSample = 0;
            if (AlertVerificationFailRate < 0) AlertVerificationFailRate = 0;
            if (AlertVerificationFailRate > 1) AlertVerificationFailRate = 1;
            if (AlertBudgetExhaustRate < 0) AlertBudgetExhaustRate = 0;
            if (AlertBudgetExhaustRate > 1) AlertBudgetExhaustRate = 1;
            if (AlertIncompleteRate < 0) AlertIncompleteRate = 0;
            if (AlertIncompleteRate > 1) AlertIncompleteRate = 1;
            if (HighRiskPolicy != "deny" && HighRiskPolicy != "allow") HighRiskPolicy = "ask";
            if (Mode != "plan" && Mode != "acceptEdits") Mode = "normal"; // 旧配置 standard/minimal/ptc/creator 一律归一为 normal
            if (ProviderId == null) ProviderId = "";
            if (ProviderKey == null) ProviderKey = "";
            if (ProviderKeyEnc == null) ProviderKeyEnc = "";
            if (ProviderEndpoint == null) ProviderEndpoint = "";
            ProviderEndpoint = ProviderEndpoint.Trim().TrimEnd('/');
            if (ProviderModel == null) ProviderModel = "";
            ProviderModel = ProviderModel.Trim();
            if (Workspaces == null) Workspaces = new List<WorkspaceCfg>();
            if (ScheduledTasks == null) ScheduledTasks = new List<ScheduledTask>();
            if (PermitRules == null) PermitRules = new List<PermitRule>();
            Hooks = GreenTiger.Hooks.Normalize(Hooks);
            if (ProviderPool == null) ProviderPool = new List<ProviderPoolEntry>();
            for (int i = 0; i < ProviderPool.Count; i++) if (ProviderPool[i] == null) ProviderPool[i] = new ProviderPoolEntry();
            if (TaskTypePolicies == null) TaskTypePolicies = new List<TaskTypePolicy>();
            if (ApprovalTicketSeconds <= 0) ApprovalTicketSeconds = ApprovalStore.DefaultTtlSeconds;
            if (ApprovalTicketSeconds < 30) ApprovalTicketSeconds = 30;
            if (ApprovalTicketSeconds > 86400) ApprovalTicketSeconds = 86400;
            for (int i = 0; i < TaskTypePolicies.Count; i++) if (TaskTypePolicies[i] == null) TaskTypePolicies[i] = new TaskTypePolicy();
            for (int i = 0; i < Workspaces.Count; i++) if (Workspaces[i] == null) Workspaces[i] = new WorkspaceCfg();
            for (int i = 0; i < ScheduledTasks.Count; i++) if (ScheduledTasks[i] == null) ScheduledTasks[i] = new ScheduledTask();
        }

        public void Save()
        {
            try
            {
                AppPaths.EnsureDirectory(AppPaths.Root);
                // 固定 .tmp 名在并发保存（任务收尾 + 设置修改）时会互踩：用唯一 tmp 名，
                // Move 到目标本身是原子的，失败的那份留下也不影响目标文件完整性。
                string tmp = AppPaths.ConfigFile + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                File.WriteAllText(tmp, SerializeForDisk(), new UTF8Encoding(false));
                if (File.Exists(AppPaths.ConfigFile)) File.Delete(AppPaths.ConfigFile);
                File.Move(tmp, AppPaths.ConfigFile);
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败", ex);
            }
        }

        /// <summary>
        /// 序列化到磁盘的形式：密钥只落 DPAPI 密文，明文一律清空。
        /// 在副本上操作（Json 往返克隆），不动活对象——Save 可能在任务收尾线程被调，
        /// 直接把 this.ProviderKey 置空会让并发的模型请求瞬间读到空 Key。
        /// DPAPI 加密失败时回退明文（可用性优先），日志已提醒。
        /// </summary>
        private string SerializeForDisk()
        {
            Config copy = Json.Parse<Config>(Json.Stringify(this));
            if (copy == null) copy = this;
            string enc = SecretBox.Encrypt(ProviderKey);
            if (enc != null)
            {
                copy.ProviderKeyEnc = enc;
                copy.ProviderKey = "";
            }
            if (copy.ProviderPool != null)
            {
                for (int i = 0; i < copy.ProviderPool.Count; i++)
                {
                    ProviderPoolEntry e = copy.ProviderPool[i];
                    if (e == null) continue;
                    string poolEnc = SecretBox.Encrypt(e.Key);
                    if (poolEnc != null)
                    {
                        e.KeyEnc = poolEnc;
                        e.Key = "";
                    }
                }
            }
            return Json.Stringify(copy);
        }

        /// <summary>
        /// 把磁盘上的密文解回内存明文（ProviderKey / 池条目 Key），并把 *Enc 字段清空。
        /// 兼容三层：dp1 密文 → 解密；旧明文配置 → 原样进内存，下次 Save 起转密文；
        /// 解密失败（换了 Windows 用户/机器）→ 保留明文（通常为空），用户重填 Key 即可。
        /// </summary>
        public void LoadSecrets()
        {
            try
            {
                string plain = ProviderKey ?? "";
                string enc = ProviderKeyEnc ?? "";
                string decrypted = enc.Length > 0 ? SecretBox.Decrypt(enc) : "";
                ProviderKey = !string.IsNullOrEmpty(decrypted) ? decrypted : plain;
                ProviderKeyEnc = "";
                if (ProviderPool != null)
                {
                    for (int i = 0; i < ProviderPool.Count; i++)
                    {
                        ProviderPoolEntry e = ProviderPool[i];
                        if (e == null) continue;
                        string p = e.Key ?? "";
                        string d = string.IsNullOrEmpty(e.KeyEnc) ? "" : SecretBox.Decrypt(e.KeyEnc);
                        e.Key = !string.IsNullOrEmpty(d) ? d : p;
                        e.KeyEnc = "";
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取加密密钥失败", ex);
            }
        }
    }

    public class Message
    {
        public string LocalAction { get; set; }
        public string Id { get; set; }
        public string Role { get; set; }
        public string Text { get; set; }
        public long Time { get; set; }
        public string Status { get; set; }
        public string Brain { get; set; }
        public List<ToolCall> ToolCalls { get; set; }

        public Message()
        {
            Id = "m_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            Role = "assistant";
            Text = "";
            Time = Now();
            Status = "done";
            Brain = "";
            ToolCalls = new List<ToolCall>();
        }

        public static long Now()
        {
            return TimeUtil.UnixMs();
        }
    }

    public class ToolCall
    {
        public string TaskId { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string ArgsJson { get; set; }
        public string Command { get; set; }
        public string Risk { get; set; }
        public string RiskReason { get; set; }
        public string Status { get; set; }
        /// <summary>调用来源：""=模型 JSON 协议；"auto"=从回复自动提取的 Shell 命令（自动执行模式）。</summary>
        public string Source { get; set; }
        public string Output { get; set; }
        public int ExitCode { get; set; }
        public long StartedAt { get; set; }
        public long FinishedAt { get; set; }
        public long DurationMs { get; set; }
        public bool RequiresApproval { get; set; }
        public string ApprovalStatus { get; set; }
        public List<Artifact> Artifacts { get; set; }
        /// <summary>阶段2：稳定错误码（ok/timeout/bad_args/...），用于统计与恢复判断。</summary>
        public string ErrorCode { get; set; }
        /// <summary>阶段1：失败归因（tool_error/permission_denied/budget_exceeded...）。</summary>
        public string ErrorClass { get; set; }
        /// <summary>阶段4：有副作用调用的幂等键，重复提交时用于识别同一次操作。</summary>
        public string IdempotencyKey { get; set; }
        /// <summary>阶段2：实际执行次数（含重试）。</summary>
        public int Attempts { get; set; }
        /// <summary>写文件/编辑的改动预览（统一 diff），审批时给用户看「到底改了什么」。</summary>
        public string Diff { get; set; }
        /// <summary>改动前的原文快照路径（backups 目录下）。用于界面上的「回滚」入口。</summary>
        public string BackupPath { get; set; }
        /// <summary>
        /// 原生 function calling：服务商返回的 call id（tool_call_id）。
        /// role:"tool" 回传结果时必须带上它；文本围栏协议此字段为空。
        /// </summary>
        public string ProviderCallId { get; set; }

        public ToolCall()
        {
            Id = "t_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            Name = "";
            ArgsJson = "";
            Command = "";
            Risk = "safe";
            RiskReason = "";
            Status = "pending";
            Source = "";
            Output = "";
            ExitCode = 0;
            Artifacts = new List<Artifact>();
            ApprovalStatus = "";
            ErrorCode = "";
            ErrorClass = "";
            IdempotencyKey = "";
            Attempts = 0;
            Diff = "";
        }
    }

    public class Artifact
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Mime { get; set; }
        public long Size { get; set; }
        public string Kind { get; set; }

        public Artifact()
        {
            Id = "a_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            Name = "";
            Path = "";
            Mime = "application/octet-stream";
            Kind = "file";
        }
    }

    public class Session
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public string WorkDir { get; set; }
        public List<Message> Messages { get; set; }
        public int ToolCount { get; set; }
        public int TurnCount { get; set; }
        public int ContextChars { get; set; }
        /// <summary>阶段4：当前/最近一次任务的执行记录（与聊天历史分离，可查询、可恢复）。</summary>
        public TaskRecord Task { get; set; }
        /// <summary>阶段5：结构化任务状态（计划/待办/产出文件）。压缩后消息可被摘要，这里的约束不会丢。</summary>
        public TaskState TaskState { get; set; }
        /// <summary>阶段4：审批票据（用户对某次工具调用的批准/拒绝，可持久化、可过期、一次性）。</summary>
        public List<ApprovalTicket> Approvals { get; set; }
        /// <summary>
        /// 阶段4：被新任务顶下来的「可继续」任务（暂停/中断/等待审批）。
        /// 单会话只留一份活动任务记录，这些不丢，仍可按 taskId 续跑（最多 5 个）。
        /// </summary>
        public List<TaskRecord> ParkedTasks { get; set; }
        /// <summary>会话文件结构版本，便于后续增量迁移而不破坏旧文件。</summary>
        public int SchemaVersion { get; set; }
        /// <summary>置顶：在侧栏独立成组并排在最前（旧文件缺字段时默认 false）。</summary>
        public bool Pinned { get; set; }
        /// <summary>归档：默认从会话列表隐藏，可从「已归档」入口查看与恢复。</summary>
        public bool Archived { get; set; }

        public Session()
        {
            Id = "s_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Title = "新会话";
            CreatedAt = Message.Now();
            UpdatedAt = CreatedAt;
            WorkDir = "";
            Messages = new List<Message>();
            ToolCount = 0;
            TurnCount = 0;
            ContextChars = 0;
            Approvals = new List<ApprovalTicket>();
            ParkedTasks = new List<TaskRecord>();
            SchemaVersion = 2;
        }

        public void Touch()
        {
            UpdatedAt = Message.Now();
        }

        public void RecalculateContext()
        {
            int total = 0;
            for (int i = 0; i < Messages.Count; i++)
            {
                Message m = Messages[i];
                if (m.Text != null) total += m.Text.Length;
                if (m.ToolCalls != null)
                {
                    for (int j = 0; j < m.ToolCalls.Count; j++)
                    {
                        ToolCall t = m.ToolCalls[j];
                        if (t.Command != null) total += t.Command.Length;
                        if (t.Output != null) total += Math.Min(t.Output.Length, 4000);
                    }
                }
            }
            ContextChars = total;
        }
    }

    public class LogEntry
    {
        public long Time { get; set; }
        public string Level { get; set; }
        public string Text { get; set; }

        public LogEntry() { Time = Message.Now(); Level = "info"; Text = ""; }
        public LogEntry(string level, string text) { Time = Message.Now(); Level = level; Text = text; }
    }

    /// <summary>命名工作区（Workspace：一个文件夹 + 名字，切换即改工作目录）。</summary>
    public class WorkspaceCfg
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }

        public WorkspaceCfg()
        {
            Id = "w_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Name = "";
            Path = "";
        }
    }

    /// <summary>定时任务：到点把 Prompt 交给 Agent 执行（Scheduled tasks）。</summary>
    public class ScheduledTask
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>"HH:mm"（每天此时）或 "every:N"（每 N 分钟）。</summary>
        public string Schedule { get; set; }
        public string Prompt { get; set; }
        public bool Enabled { get; set; }
        /// <summary>上次触发时间（Unix 毫秒），防止重复触发。</summary>
        public long LastRunAt { get; set; }

        public ScheduledTask()
        {
            Id = "t_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Name = "";
            Schedule = "09:00";
            Prompt = "";
            Enabled = true;
            LastRunAt = 0;
        }
    }

    /// <summary>
    /// 权限规则（permission rules）。匹配串形如 "tool:pattern"，如 "read_file:**"、
    /// "shell:Get-ChildItem*"。* 匹配任意字符（含空），? 匹配单个字符，大小写不敏感。
    /// </summary>
    public class PermitRule
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>"tool:pattern"，如 "read_file:**"、"shell:Remove-Item*"。大小写不敏感。</summary>
        public string Match { get; set; }
        /// <summary>"allow"（跳过逐条确认）或 "deny"（直接拦截）。</summary>
        public string Action { get; set; }

        public PermitRule()
        {
            Id = "pr_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Name = "";
            Match = "";
            Action = "allow";
        }
    }

    /// <summary>大脑池条目（AutoPool 开启时参与择优轮换）。ProviderId 可空=自定义端点；Key 仅存本机。</summary>
    public class ProviderPoolEntry
    {
        public string Id { get; set; }
        public string ProviderId { get; set; }
        public string Endpoint { get; set; }
        public string Model { get; set; }
        public string Key { get; set; }
        /// <summary>Key 的 DPAPI 密文（dp1:Base64）。只在 Save 时写入，Load 后解回 Key。</summary>
        public string KeyEnc { get; set; }
        /// <summary>临时停用：true 时不参与选举。</summary>
        public bool Disabled { get; set; }

        public ProviderPoolEntry()
        {
            Id = "bp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            ProviderId = "";
            Endpoint = "";
            Model = "";
            Key = "";
            Disabled = false;
        }
    }
}
