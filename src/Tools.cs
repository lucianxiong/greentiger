using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GreenTiger
{
    public class ToolResult
    {
        public bool Ok { get; set; }
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public string Risk { get; set; }
        public string RiskReason { get; set; }
        public string Command { get; set; }
        public string Cwd { get; set; }
        public bool TimedOut { get; set; }
        /// 阶段2：稳定错误码 ok/timeout/permission/not_found/bad_args/unavailable/tool_error
        public string ErrorCode { get; set; }
        /// 阶段2：是否可自动重试（超时/网络抖动=true；参数错/拒绝=false）
        public bool Retryable { get; set; }
        /// 阶段2：实际执行次数（含重试），由 ToolRuntime 填写
        public int Attempts { get; set; }
        /// 阶段2：工具输出里是否出现疑似指令注入（只标记，不删除证据）
        public bool InjectionSuspected { get; set; }
        public List<Artifact> Artifacts { get; set; }

        public ToolResult()
        {
            Ok = true;
            Output = "";
            Error = "";
            ErrorCode = "ok";
            Retryable = false;
            Attempts = 0;
            InjectionSuspected = false;
            Risk = "safe";
            RiskReason = "";
            Command = "";
            Cwd = "";
            Artifacts = new List<Artifact>();
        }
    }

    /// <summary>屏幕区域截图，抽出来是为了让自检可以直接覆盖这段逻辑。</summary>
    public static class ScreenCapture
    {
        public static void Capture(Rectangle bounds, string path, int quality)
        {
            Rectangle src = Rectangle.Intersect(bounds, SystemInformation.VirtualScreen);
            if (src.Width <= 0 || src.Height <= 0) src = bounds;
            if (src.Width <= 0 || src.Height <= 0) throw new InvalidOperationException("截图区域无效");
            using (Bitmap bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(src.Left, src.Top, 0, 0, src.Size, CopyPixelOperation.SourceCopy);
                ImageCodecInfo jpg = Encoder();
                if (jpg != null)
                {
                    using (EncoderParameters ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                        bmp.Save(path, jpg, ep);
                    }
                }
                else bmp.Save(path, ImageFormat.Jpeg);
            }
        }

        /// <summary>长边超过 maxDim 时等比降采样并重编码回同一路径（模型吃图按长边计 token）。失败保留原图。</summary>
        public static bool Downsample(string path, int maxDim, int quality)
        {
            try
            {
                using (Bitmap srcBmp = new Bitmap(path))
                {
                    int max = Math.Max(srcBmp.Width, srcBmp.Height);
                    if (max <= maxDim) return false;
                    double scale = (double)maxDim / max;
                    int nw = Math.Max(1, (int)Math.Round(srcBmp.Width * scale));
                    int nh = Math.Max(1, (int)Math.Round(srcBmp.Height * scale));
                    using (Bitmap dst = new Bitmap(nw, nh))
                    using (Graphics g = Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(srcBmp, 0, 0, nw, nh);
                        ImageCodecInfo jpg = Encoder();
                        if (jpg != null)
                        {
                            using (EncoderParameters ep = new EncoderParameters(1))
                            {
                                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                                dst.Save(path, jpg, ep);
                            }
                        }
                        else dst.Save(path, ImageFormat.Jpeg);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                try { Log.Warn("截图降采样失败（保留原图）: " + ex.Message); } catch { }
                return false;
            }
        }

        public static ImageCodecInfo Encoder()
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
            for (int i = 0; i < codecs.Length; i++)
            {
                if (codecs[i].FormatID == ImageFormat.Jpeg.Guid) return codecs[i];
            }
            return null;
        }
    }

    public class ToolExecutor
    {
        private readonly IAgentHost _app;
        private readonly Config _config;

        // 外部数据流记忆（监督第三防，数据流能力标记）：最近几条不可信来源的返回内容留底
        // （网页抓取 / 搜索结果 / HTTP 响应 / MCP 工具输出）。当这些内容出现在 shell、写文件
        // 或其他外部工具的参数里时，确认框明说数据从哪来——「下载后执行」「网页内容进命令」
        // 这类注入链对用户可见。模型侧防线是 untrusted_content 包裹（Agent/Brain），这里是用户侧防线。
        private readonly object _flowGate = new object();
        private readonly List<ExtFlowRecord> _extFlows = new List<ExtFlowRecord>();

        public ToolExecutor(IAgentHost app, Config config)
        {
            _app = app;
            _config = config;
        }

        private static readonly string OwnProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        private static readonly uint OwnProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        /// <summary>P0 computer-use 防线：用户按住 Esc = 全局急停，键鼠/聚焦动作开始前检查。</summary>
        private static void AssertNotEmergencyStop()
        {
            if (Native.EscapeHeld())
                throw new InvalidOperationException("用户按住 Esc 急停，本次操作已取消。不要立即重试；先向用户确认是否继续。");
        }

        /// <summary>
        /// P0 防反馈回路：前台窗口是绿虎自身时禁止键鼠——否则被注入的指令可以操作
        /// 绿虎自己的界面（包括点掉确认框）。想操作其它应用请先 focus_window 把目标带到前台。
        /// </summary>
        private static void AssertNotSelfWindow()
        {
            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero) return;
                uint pid;
                Native.GetWindowThreadProcessId(fg, out pid);
                if (pid == OwnProcessId)
                    throw new InvalidOperationException("前台窗口是绿虎自身，禁止对绿虎窗口执行键鼠操作（防反馈回路）。请先 focus_window 目标应用。");
            }
            catch (InvalidOperationException) { throw; }
            catch { }
        }

        public static string Arg(Dictionary<string, object> args, string key, string fallback)
        {
            return Json.Str(args, key, fallback);
        }

        public static int ArgInt(Dictionary<string, object> args, string key, int fallback)
        {
            return Json.Int(args, key, fallback);
        }

        public static bool ArgBool(Dictionary<string, object> args, string key, bool fallback)
        {
            return Json.Bool(args, key, fallback);
        }

        public void Analyze(ToolCall call)
        {
            call.Risk = "safe";
            call.RiskReason = "";
            Dictionary<string, object> args = Json.ParseObject(call.ArgsJson);
            if (args == null) args = new Dictionary<string, object>();
            string name = (call.Name == null ? "" : call.Name.ToLowerInvariant());
            if (name == "shell")
            {
                string command = Arg(args, "command", "");
                call.Command = command;
                string reason = ShellRiskCompound(command);
                if (reason.Length > 0) { call.Risk = "danger"; call.RiskReason = reason; }
                else { call.Risk = "normal"; }
                // 不可信内容流入执行通道：网页/工具返回的内容出现在命令里，
                // 直接升 danger——执行不可逆，用户必须知情后才放行。
                string flowFrom = DetectExtFlow(call.ArgsJson);
                if (!string.IsNullOrEmpty(flowFrom))
                {
                    call.Risk = "danger";
                    call.RiskReason = (call.RiskReason.Length > 0 ? call.RiskReason + "；" : "") +
                        "命令内容包含来自「" + flowFrom + "」的返回内容（网页/工具输出可能携带注入指令，请确认这是你要执行的）";
                }
                // 网络白名单（执行安全第三件）：allowlist 模式下命令里的出网目标必须在白名单内。
                // 这是注入防御链的最后一环——内容可见（上一行）管"谁把它塞进命令"，
                // 白名单管"数据能发到哪里"，两道闸一起关才闭环。
                string netBlocked = FirstNetworkTargetOutsideAllowlist(command);
                if (!string.IsNullOrEmpty(netBlocked))
                {
                    call.Risk = "danger";
                    call.RiskReason = (call.RiskReason.Length > 0 ? call.RiskReason + "；" : "") +
                        "命令要访问的网络目标「" + netBlocked + "」不在网络白名单内（设置 → 权限与运行限制可调整）";
                }
            }
            else if (name == "write_file" || name == "read_file" || name == "list_dir" || name == "edit")
            {
                string path = Arg(args, "path", "");
                string full = Fs.Resolve(_config.WorkDir, path);
                call.Command = full;
                if (!Fs.IsInside(_config.WorkDir, full))
                {
                    call.Risk = "danger";
                    call.RiskReason = "路径在工作目录之外";
                }
                else call.Risk = "safe";
                // 不可信内容流入写文件（只黄标不升级）：落盘在工作目录内、可逆，
                // 但用户应该知道文件内容有一部分来自网页/工具输出。
                if (name != "read_file" && name != "list_dir")
                {
                    string wFlowFrom = DetectExtFlow(call.ArgsJson);
                    if (!string.IsNullOrEmpty(wFlowFrom))
                        call.RiskReason = (call.RiskReason ?? "") +
                            "文件内容包含来自「" + wFlowFrom + "」的返回内容（请确认写入内容符合预期）";
                }
            }
            else if (name == "multiedit")
            {
                // multiedit 走 MultiEditAsync 自己的路径校验；这里只补数据流可见性
                string mFlowFrom = DetectExtFlow(call.ArgsJson);
                if (!string.IsNullOrEmpty(mFlowFrom))
                    call.RiskReason = "文件内容包含来自「" + mFlowFrom + "」的返回内容（请确认写入内容符合预期）";
            }
            else if (name == "repo_map")
            {
                // 只读概览，不读文件内容：永远是 safe
                call.Risk = "safe";
            }
            else if (name == "glob" || name == "grep")
            {
                // 只读检索：目录在工作目录内就是 safe，越界仍要确认
                string path = Arg(args, "path", ".");
                string full = Fs.Resolve(_config.WorkDir, path);
                call.Command = full;
                if (!Fs.IsInside(_config.WorkDir, full))
                {
                    call.Risk = "danger";
                    call.RiskReason = "检索目录在工作目录之外";
                }
                else call.Risk = "safe";
            }
            else if (name == "mouse" || name == "keyboard" || name == "focus_window")
            {
                // P0：真实键鼠/窗口控制一律 danger 逐次确认——被劫持的模型能操作的是
                // 用户已登录的网银/邮箱/微信，这不是 normal 级能兜住的面。
                // 票据机制保证同参数的重复调用在有效期内不重复弹窗。
                call.Risk = "danger";
                call.RiskReason = name == "focus_window"
                    ? "切换前台窗口（真实桌面控制的第一步）"
                    : "真实键鼠控制，可操作任何已登录应用";
            }
            else if (name == "web_fetch")
            {
                // 只读抓取 + 正文转换：不落盘，风险等级按 http_get 的「公网 normal / 内网 danger」
                call.Risk = "normal";
                if (IsInternalHost(Arg(args, "url", "")))
                {
                    call.Risk = "danger";
                    call.RiskReason = "要抓取的是本机/内网地址";
                }
                string fetched = FirstNetworkTargetOutsideAllowlist(Arg(args, "url", ""));
                if (!string.IsNullOrEmpty(fetched))
                {
                    call.Risk = "danger";
                    call.RiskReason = "要抓取的目标「" + fetched + "」不在网络白名单内（设置 → 权限与运行限制可调整）";
                }
            }
            else if (name == "http_get")
            {
                string saveTo = Arg(args, "save_to", "");
                call.Risk = "normal";
                if (!string.IsNullOrEmpty(saveTo))
                {
                    string full = Fs.Resolve(_config.WorkDir, saveTo);
                    call.Command = full;
                    if (!Fs.IsInside(_config.WorkDir, full))
                    {
                        call.Risk = "danger";
                        call.RiskReason = "下载路径在工作目录之外";
                    }
                }
                // 本机/内网地址单独标注：模型可以借此读到内部服务页（含带凭据的本地面板），
                // 不能和公网 GET 同等待遇。
                if (call.Risk != "danger" && IsInternalHost(Arg(args, "url", "")))
                {
                    call.Risk = "danger";
                    call.RiskReason = "访问本机或内网地址";
                }
                string gotNet = FirstNetworkTargetOutsideAllowlist(Arg(args, "url", ""));
                if (!string.IsNullOrEmpty(gotNet))
                {
                    call.Risk = "danger";
                    call.RiskReason = "要下载的目标「" + gotNet + "」不在网络白名单内（设置 → 权限与运行限制可调整）";
                }
            }
            else if (name == "web_search")
            {
                call.Risk = "normal";
            }
            else if (name == "upload_file")
            {
                string full = Fs.Resolve(_config.WorkDir, Arg(args, "path", ""));
                call.Command = full;
                if (!Fs.IsInside(_config.WorkDir, full))
                {
                    call.Risk = "danger";
                    call.RiskReason = "要上传的文件在工作目录之外";
                }
                else call.Risk = "normal";
            }
            else if (name == "clipboard")
            {
                string action = Arg(args, "action", "get").ToLowerInvariant();
                call.Risk = action == "get" ? "danger" : "normal";
                if (call.Risk == "danger") call.RiskReason = "读取剪贴板可能包含密码、验证码等敏感内容";
            }
            else if (name == "screenshot")
            {
                call.Risk = "safe";
            }
            else if (name == "wait" || name == "finish")
            {
                call.Risk = "safe";
            }
            else if (name == "plan" || name == "todo")
            {
                // 虚拟工具：计划/进度由 Agent 层管理与审批，不执行本机命令。
                call.Risk = "safe";
            }
            else if (name == "file_search" || name == "pc_scan" || name == "pc_uninstall_list"
                  || name == "pc_disk_summary" || name == "pc_startup_list" || name == "pc_shred_preview")
            {
                // 本机只读探查：查询本机文件索引 / 扫描统计 / 读卸载注册表 / 磁盘与自启清单 / 粉碎预览，不写任何文件。
                call.Risk = "safe";
                if (name == "file_search") call.Command = Arg(args, "query", "");
                else if (name == "pc_shred_preview") call.Command = LocalOps.SummarizePaths(args);
            }
            else if (name == "save_memory")
            {
                call.Risk = "normal";
                call.RiskReason = "写入长期记忆文件";
                call.Command = TextUtil.OneLine(Arg(args, "content", ""), 60);
            }
            else if (name == "task")
            {
                call.Risk = "normal";
                call.RiskReason = "派发只读分析子代理（无写权限无 shell，独立预算与墙钟）";
                List<object> taskList = Json.Array(args, "tasks");
                string taskPreview = taskList != null && taskList.Count > 0 && taskList[0] != null ? Convert.ToString(taskList[0]) : "";
                call.Command = TextUtil.OneLine(taskPreview, 60) + (taskList != null && taskList.Count > 1 ? " 等 " + taskList.Count + " 项" : "");
            }
            else if (name == "pc_uninstall_launch")
            {
                call.Risk = "danger";
                call.RiskReason = "将启动该软件的标准卸载器，卸载不可逆";
                call.Command = Arg(args, "name", "");
            }
            else if (name == "pc_clean_cache")
            {
                call.Risk = "danger";
                call.RiskReason = "将删除白名单缓存目录内的文件（占用中的自动跳过），不可恢复";
                call.Command = Arg(args, "finding_id", "");
            }
            else if (name == "pc_shred")
            {
                call.Risk = "danger";
                call.RiskReason = "文件将被多趟覆写后删除，不可恢复；系统/程序目录硬拒绝";
                call.Command = LocalOps.SummarizePaths(args);
            }
            else if (name.StartsWith("mcp__"))
            {
                // MCP 外部能力（浏览器操作/数据库/文件读写等）边界不可控：
                // 这里必须标 danger，否则自动执行模式下会绕过确认——注释里的「统一确认」才成立。
                call.Risk = "danger";
                call.RiskReason = "MCP 外部工具：第三方能力，逐次确认";
                // 监督第三防：参数里混着「别的来源」的返回内容（其他服务器 / 网页 / 搜索），
                // 确认框里明说，用户才知道数据正流向这台外部工具。
                try
                {
                    string flowFrom = DetectCrossServerFlow(call.Name, call.ArgsJson);
                    if (!string.IsNullOrEmpty(flowFrom))
                        call.RiskReason += "；参数包含来自「" + flowFrom + "」的返回内容（不可信数据流入外部工具，请确认这是你要的）";
                }
                catch { }
            }
            else
            {
                call.Risk = "normal";
                call.RiskReason = "未知工具";
            }
            if (call.RequiresApproval && call.Risk != "danger") call.RiskReason = "已开启逐条确认";
        }

        // ---------------- 网络白名单（执行安全第三件） ----------------

        private static readonly System.Text.RegularExpressions.Regex UrlHostRegex =
            new System.Text.RegularExpressions.Regex(@"https?://([A-Za-z0-9._-]+)(?::\d+)?",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// allowlist 模式下，返回文本里第一个不在白名单内的出网主机（都合规返回 null）。
        /// policy 非 allowlist 时恒返回 null（白名单关闭=维持既有行为）。
        /// 覆盖 URL 形态（http(s)://host）；无协议形态（curl host）第一版不抓——
        /// 宁可漏抓也别把参数值误判成主机。
        /// </summary>
        internal string FirstNetworkTargetOutsideAllowlist(string text)
        {
            if (_config == null || !string.Equals(_config.NetworkPolicy, "allowlist", StringComparison.OrdinalIgnoreCase))
                return null;
            List<string> allow = _config.NetworkAllowlist;
            if (text == null || text.Length == 0) return null;
            System.Text.RegularExpressions.MatchCollection ms = UrlHostRegex.Matches(text);
            for (int i = 0; i < ms.Count; i++)
            {
                string host = ms[i].Groups[1].Value.ToLowerInvariant();
                if (host.Length == 0) continue;
                if (!HostInAllowlist(host, allow)) return host;
            }
            return null;
        }

        /// <summary>主机是否在白名单内：等于条目，或属于条目的子域（host 以 "."+条目 结尾）。空白名单=全部拦截（fail-closed）。</summary>
        internal static bool HostInAllowlist(string host, List<string> allow)
        {
            if (allow == null || allow.Count == 0) return false;
            for (int i = 0; i < allow.Count; i++)
            {
                string entry = (allow[i] == null ? "" : allow[i].Trim().ToLowerInvariant());
                if (entry.Length == 0) continue;
                if (entry.StartsWith("*.", StringComparison.Ordinal)) entry = entry.Substring(1);   // *.x.com → .x.com
                if (string.Equals(host, entry, StringComparison.Ordinal)) return true;
                if (host.EndsWith("." + entry, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>是否本机/内网地址（SSRF 面）：回环、私有网段、链路本地、.local 与裸主机名。</summary>
        internal static bool IsInternalHost(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            string host = uri.Host == null ? "" : uri.Host.ToLowerInvariant();
            if (host.Length == 0) return false;
            if (host == "localhost" || host == "::1" || host == "[::1]") return true;
            if (host.EndsWith(".local", StringComparison.Ordinal) ||
                host.EndsWith(".internal", StringComparison.Ordinal) ||
                host.EndsWith(".lan", StringComparison.Ordinal)) return true;
            System.Net.IPAddress ip;
            if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out ip)) return false;
            byte[] b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                if (b[0] == 0 || b[0] == 127 || b[0] == 10) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 169 && b[1] == 254) return true;
            }
            return false;
        }

        private static string ShellRisk(string command)
        {
            if (string.IsNullOrEmpty(command)) return "";
            string c = command.ToLowerInvariant();
            if (Regex.IsMatch(c, @"\bformat(-volume)?\b")) return "格式化磁盘";
            if (Regex.IsMatch(c, @"(^|[;&|\s])(remove-item|del|erase|rd|rmdir|rm|clear-content)(\.exe)?\s")) return "删除或清空文件/目录";
            if (Regex.IsMatch(c, @"\bdiskpart\b") || c.Contains("clear-disk") || c.Contains("initialize-disk")) return "磁盘分区/清空操作";
            if (Regex.IsMatch(c, @"\bshutdown\b") || c.Contains("restart-computer") || c.Contains("stop-computer")) return "关机/重启";
            if (c.Contains("bcdedit") || c.Contains("bootrec") || Regex.IsMatch(c, @"vssadmin\s+delete") || Regex.IsMatch(c, @"wbadmin\s+delete")) return "修改启动项/删除备份";
            if (Regex.IsMatch(c, @"reg(\.exe)?\s+delete\s+hklm\\(sam|security|system)")) return "删除关键注册表";
            if (c.Contains("set-mppreference") || Regex.IsMatch(c, @"add-mppreference.*exclusion") || c.Contains("disable-windowsoptionalfeature") || c.Contains("uninstall-windowsfeature")) return "修改安全软件/系统功能";
            // irm/iwr（Invoke-RestMethod / Invoke-WebRequest 别名）是最常见的下载摇篮写法，必须覆盖
            if (Regex.IsMatch(c, @"(invoke-expression|iex).*\b(downloadstring|downloadfile|invoke-webrequest|invoke-restmethod|curl|wget|irm|iwr)\b") || Regex.IsMatch(c, @"(curl|wget|invoke-webrequest|invoke-restmethod|\birm\b|\biwr\b).*\|\s*(iex|invoke-expression|powershell)")) return "下载并执行未知代码";
            if (Regex.IsMatch(c, @"net\s+user\s+.*\s+/add") || Regex.IsMatch(c, @"net\s+localgroup\s+administrators\s+.*\s+/add")) return "创建账户/提权";
            if (Regex.IsMatch(c, @"taskkill.*\/im\s+(powershell|pwsh|winlogon|csrss|lsass)(\.exe)?\b")) return "结束关键系统/PowerShell 进程";
            if (Regex.IsMatch(c, @"schtasks.*\s+/create") || c.Contains("new-service") || Regex.IsMatch(c, @"sc(\.exe)?\s+create")) return "创建计划任务/服务（持久化）";
            if (Regex.IsMatch(c, @"takeown") || Regex.IsMatch(c, @"icacls.*(windows|system32)")) return "修改系统目录权限";
            if (Regex.IsMatch(c, @"cipher\s+/[wr]")) return "擦除磁盘数据";
            if (c.Contains("mimikatz") || Regex.IsMatch(c, @"procdump.*lsass") || Regex.IsMatch(c, @"\blsass\b")) return "读取凭据/进程内存";
            if (Regex.IsMatch(c, @"remove-item.*([a-z]:\\windows|[a-z]:\\program files)")) return "删除系统目录";
            if (Regex.IsMatch(c, @"(rd|rmdir)\s+/s\s+/q\s+[a-z]:\\")) return "递归删除盘符根目录";
            if (Regex.IsMatch(c, @"del\s+/[fsq].*[a-z]:\\")) return "强制删除根目录文件";
            if (Regex.IsMatch(c, @"(copy|move).*\\startup") || Regex.IsMatch(c, @"reg\s+add.*\\run")) return "写入开机启动项";
            if (c.Contains("set-executionpolicy unrestricted")) return "放宽 PowerShell 执行策略";
            if (c.Contains("enable-psremoting")) return "开启 PowerShell 远程";
            if (Regex.IsMatch(c, @"netsh\s+advfirewall\s+set\s+allprofiles\s+state\s+off")) return "关闭防火墙";
            if (Regex.IsMatch(c, @"wmic\s+shadowcopy\s+delete") || c.Contains("fsutil usn deletejournal")) return "删除卷影/日志";
            if (Regex.IsMatch(c, @"powershell(\.exe)?.*\s-enc(odedcommand)?\b")) return "使用编码命令隐藏代码";
            return "";
        }

        /// <summary>
        /// 复合命令风险判定：整串先过一遍黑名单，再按换行 / ; / | / && / || 拆子命令
        /// （引号内的分隔符不拆），每个子命令单独过黑名单——的
        /// 「shell 操作符感知拆分」路线：不能让「危险命令 && 无害命令」把危险段藏进复合命令里。
        /// 引号不闭合 = 无法可靠解析 → fail-closed，直接按 danger 报告让用户确认。
        /// </summary>
        internal static string ShellRiskCompound(string command)
        {
            if (string.IsNullOrEmpty(command)) return "";
            string whole = ShellRisk(command);
            if (whole.Length > 0) return whole;
            List<string> parts;
            if (!TrySplitShellCommand(command, out parts))
                return "命令包含未闭合的引号，无法可靠解析（fail-closed，需人工确认）";
            for (int i = 0; i < parts.Count; i++)
            {
                string reason = ShellRisk(parts[i]);
                if (reason.Length > 0) return reason + "（复合命令第 " + (i + 1) + " 段）";
            }
            return "";
        }

        /// <summary>引号感知的命令拆分。返回 false 表示引号未闭合、解析不可靠。</summary>
        private static bool TrySplitShellCommand(string command, out List<string> parts)
        {
            parts = new List<string>();
            if (command == null) return true;
            StringBuilder cur = new StringBuilder();
            char quote = '\0';
            for (int i = 0; i < command.Length; i++)
            {
                char ch = command[i];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                    cur.Append(ch);
                    continue;
                }
                if (ch == '\'' || ch == '"') { quote = ch; cur.Append(ch); continue; }
                if (ch == '\n' || ch == ';' || ch == '|')
                {
                    parts.Add(cur.ToString());
                    cur.Length = 0;
                    continue;
                }
                cur.Append(ch);
            }
            if (quote != '\0') return false;
            parts.Add(cur.ToString());
            return true;
        }

        /// <summary>
        /// 来源不可信的工具：网页/搜索/第三方 MCP 的返回内容可能携带注入指令，
        /// 回灌给模型时统一包 untrusted_content 标签（Agent.cs 文本协议与
        /// Brain.cs 原生 function calling 两条路径都包）。
        /// </summary>
        public static bool IsUntrustedSource(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            string n = toolName.ToLowerInvariant();
            return n == "web_search" || n == "web_fetch" || n == "http_get" ||
                   n.StartsWith("mcp__", StringComparison.Ordinal);
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken token)
        {
            Dictionary<string, object> args = Json.ParseObject(call.ArgsJson);
            if (args == null) args = new Dictionary<string, object>();
            string name = (call.Name == null ? "" : call.Name.ToLowerInvariant());
            try
            {
                if (name == "shell") return await ShellAsync(call, args, token).ConfigureAwait(false);
                if (name == "read_file") return await ReadFileAsync(call, args, token).ConfigureAwait(false);
                if (name == "edit") return PostToolHooks.Attach(await EditAsync(call, args, token).ConfigureAwait(false), call, args, _config.WorkDir);
                if (name == "multiedit") return PostToolHooks.Attach(await MultiEditAsync(call, args, token).ConfigureAwait(false), call, args, _config.WorkDir);
                if (name == "shell_output") return await ShellOutputAsync(call, args, token).ConfigureAwait(false);
                if (name == "shell_kill") return await ShellKillAsync(call, args, token).ConfigureAwait(false);
                if (name == "glob") return await GlobAsync(call, args, token).ConfigureAwait(false);
                if (name == "grep") return await GrepAsync(call, args, token).ConfigureAwait(false);
                if (name == "write_file") return PostToolHooks.Attach(await WriteFileAsync(call, args, token).ConfigureAwait(false), call, args, _config.WorkDir);
                if (name == "list_dir") return await ListDirAsync(call, args, token).ConfigureAwait(false);
                if (name == "screenshot") return await ScreenshotAsync(call, args, token).ConfigureAwait(false);
                if (name == "mouse") return await MouseAsync(call, args, token).ConfigureAwait(false);
                if (name == "keyboard") return await KeyboardAsync(call, args, token).ConfigureAwait(false);
                if (name == "focus_window") return await FocusWindowAsync(call, args, token).ConfigureAwait(false);
                if (name == "upload_file") return await UploadFileAsync(call, args, token).ConfigureAwait(false);
                if (name == "http_get") { ToolResult r = await HttpGetAsync(call, args, token).ConfigureAwait(false); RecordWebResult(args, r); return r; }
                if (name == "web_fetch") { ToolResult r = await WebFetchAsync(call, args, token).ConfigureAwait(false); RecordWebResult(args, r); return r; }
                if (name == "web_search") { ToolResult r = await WebSearchAsync(call, args, token).ConfigureAwait(false); RecordWebResult(args, r); return r; }
                if (name == "clipboard") return await ClipboardAsync(call, args, token).ConfigureAwait(false);
                if (name == "wait") return await WaitAsync(call, args, token).ConfigureAwait(false);
                if (name == "finish") return Finish(call, args);
                if (name == "plan") return PlanAsync(call, args);
                if (name == "todo") return TodoAsync(call, args);
                if (name == "file_search") return LocalOps.Search(args);
                if (name == "pc_scan") return LocalOps.Scan(args);
                if (name == "pc_disk_summary") return LocalOps.DiskSummary();
                if (name == "pc_startup_list") return LocalOps.StartupList();
                if (name == "pc_shred_preview") return LocalOps.ShredPreview(args);
                if (name == "pc_uninstall_list") return LocalOps.UninstallList();
                if (name == "pc_uninstall_launch") return LocalOps.UninstallLaunch(args);
                if (name == "pc_clean_cache") return LocalOps.Clean(args);
                if (name == "pc_shred") return LocalOps.Shred(args);
                if (name == "save_memory") return SaveMemory(args);
                if (name == "task") return await new SubAgentRunner(_app, _config).RunAsync(args, token);
                if (name == "repo_map") return RepoMap(Json.Bool(args, "symbols", false));
                if (name.StartsWith("mcp__"))
                {
                    ToolResult mcpResult = new ToolResult();
                    try
                    {
                        mcpResult.Output = await _app.Mcp.CallAsync(call.Name, call.ArgsJson, token).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(mcpResult.Output)) mcpResult.Output = "(MCP 调用完成，无文本输出)";
                        RecordMcpFlow(call.Name, mcpResult.Output);
                    }
                    catch (Exception ex)
                    {
                        mcpResult.Ok = false;
                        mcpResult.ErrorCode = ToolErrorCode.Unavailable;
                        mcpResult.Retryable = true;
                        mcpResult.Error = "MCP 调用失败: " + ex.Message;
                    }
                    return mcpResult;
                }
                ToolResult unknown = new ToolResult();
                unknown.Ok = false;
                unknown.ErrorCode = ToolErrorCode.UnknownTool;
                unknown.Error = "未知工具: " + call.Name;
                return unknown;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("工具执行失败: " + name, ex);
                ToolResult error = new ToolResult();
                error.Ok = false;
                error.ErrorCode = ToolErrorCode.ToolError;
                error.Error = ex.Message;
                return error;
            }
        }

        private static string NewRunPath(string prefix, string ext)
        {
            AppPaths.EnsureDirectory(AppPaths.RunsDir);
            return Path.Combine(AppPaths.RunsDir, prefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + RandomUtil.Next(9999).ToString("0000") + ext);
        }

        /// <summary>
        /// 子代理执行的硬性边界（P0 修复）。
        ///
        /// 主循环里「越界访问」走 Analyze → danger → 用户审批这条链；子代理没有审批 UI，
        /// 以前直接 ExecuteAsync 等于完全没有路径边界——read_file C:\Users\... 凭据文件会被静默读走。
        /// 这里在子代理执行入口按路径参数硬拦：不在工作目录内一律拒绝，http_get 内网地址一律拒绝。
        /// 返回 null 表示通过；否则返回要回给模型的失败结果。
        /// </summary>
        private ToolResult SubagentBoundaryCheck(ToolCall call, Dictionary<string, object> args)
        {
            if (call == null || string.IsNullOrEmpty(call.Name)) return null;
            string name = call.Name.ToLowerInvariant();
            if (name == "http_get" || name == "web_search" || name == "web_fetch")
            {
                string url = Arg(args, "url", "");
                if ((name == "http_get" || name == "web_fetch") && !string.IsNullOrEmpty(url) && IsInternalHost(url))
                {
                    ToolResult denied = new ToolResult();
                    denied.Ok = false;
                    denied.ErrorCode = ToolErrorCode.Permission;
                    denied.Error = "子代理禁止访问内网地址（" + url + "）。需要访问内网时由主任务申请批准后执行。";
                    return denied;
                }
                return null;
            }
            if (name != "read_file" && name != "write_file" && name != "list_dir" && name != "glob"
                && name != "grep" && name != "edit") return null;
            string rawPath = Arg(args, "path", ".");
            if (string.IsNullOrEmpty(rawPath)) rawPath = ".";
            string resolved = Fs.Resolve(_config.WorkDir, rawPath);
            if (!Fs.IsInside(_config.WorkDir, resolved))
            {
                ToolResult outside = new ToolResult();
                outside.Ok = false;
                outside.ErrorCode = ToolErrorCode.Permission;
                outside.Error = "路径越界（" + rawPath + " → " + resolved + "）：子代理只能访问工作目录内的文件。" +
                                "需要读取工作目录之外的路径时，由主任务发起并经用户批准。";
                return outside;
            }
            return null;
        }

        /// <summary>save_memory：显式写入长期记忆（用户意图明确，不走候选审核）。scope=global 跨项目。</summary>
        private ToolResult SaveMemory(Dictionary<string, object> args)
        {
            ToolResult result = new ToolResult();
            string content = Arg(args, "content", "");
            string scope = (Arg(args, "scope", "project") ?? "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(content))
            {
                result.Ok = false;
                result.Error = "content 不能为空。要记住什么就写什么，一句话到几句话。";
                return result;
            }
            string err;
            string file = AgentMemory.AppendExplicit(scope == "global" ? "global" : "project", _config.WorkDir, content, out err);
            if (string.IsNullOrEmpty(file))
            {
                result.Ok = false;
                result.Error = "记忆写入失败：" + err;
                return result;
            }
            result.Ok = true;
            result.Output = "已写入" + (scope == "global" ? "全局记忆" : "项目记忆") + "（" + Path.GetFileName(file) + "）：" + TextUtil.OneLine(content, 100);
            return result;
        }

        private async Task<ToolResult> ShellAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string command = Arg(args, "command", "");
            if (string.IsNullOrEmpty(command.Trim()))
            {
                ToolResult empty = new ToolResult();
                empty.Ok = false;
                empty.Error = "command 不能为空";
                return empty;
            }
            // 后台执行（run_in_background）：dev server / 长构建 / watch
            // 这类不结束的命令不阻塞对话，启动后立刻返回 id，用 shell_output 轮询、shell_kill 终止。
            if (ArgBool(args, "background", false))
            {
                return StartBackgroundShell(call, args, command, token);
            }
            int timeout = ArgInt(args, "timeout", _config.CommandTimeout);
            if (timeout < 5) timeout = 5;
            if (timeout > 3600) timeout = 3600;
            string cwdArg = Arg(args, "cwd", "");
            string cwd = string.IsNullOrEmpty(cwdArg) ? _config.WorkDir : Fs.Resolve(_config.WorkDir, cwdArg);
            if (!Directory.Exists(cwd)) cwd = _config.WorkDir;
            string scriptPath = NewRunPath("run", ".ps1");
            StringBuilder script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Continue'");
            script.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
            script.AppendLine("$OutputEncoding = [System.Text.Encoding]::UTF8");
            script.AppendLine("$ProgressPreference = 'SilentlyContinue'");
            script.AppendLine("# GreenTiger shell " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            script.AppendLine(SelfRepairEvidence.VerificationCommand(command));
            File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(true));
            call.Command = command;
            ToolResult result = new ToolResult();
            result.Command = command;
            result.Cwd = cwd;
            string argsLine = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \"" + scriptPath + "\"";
            bool sandboxed = Sandbox.ShouldRunSandboxed(_config, cwd);
            if (Sandbox.BlockOutsideCwd(_config, cwd))
            {
                // 沙箱模式下 cwd 越出沙箱 = fail-closed：绝不静默回退到中完整性执行
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.Permission;
                result.Error = "沙箱模式下命令的工作目录必须在沙箱目录内（" + Sandbox.SandboxDir() +
                               "），已拒绝执行。需要动沙箱外的文件请让用户先关闭沙箱模式。";
                return result;
            }
            // 未沙箱 shell = 用户已批准的中完整性执行，写什么不受 ACL 约束——执行前增量留档工作目录，
            // /undo 可把文件恢复到这条命令之前（沙箱内写入受 ACL 限制，不留档）。
            string ckNote = "";
            if (!sandboxed)
            {
                try
                {
                    string ckError;
                    string manifest = Checkpoint.Create(cwd, TaskIdOf(), Checkpoint.Caps.Default(), out ckError);
                    if (!string.IsNullOrEmpty(manifest))
                    {
                        call.BackupPath = "gtckpt:" + manifest;
                        ckNote = "[checkpoint 已留档：/undo 可回滚这条命令对文件的改动]";
                    }
                    else if (!string.IsNullOrEmpty(ckError))
                    {
                        ckNote = "[checkpoint 未留档：" + ckError + "]";
                    }
                }
                catch (Exception ckEx) { ckNote = "[checkpoint 未留档：" + ckEx.Message + "]"; }
            }
            ProcessStartInfo psi = sandboxed
                ? Sandbox.LauncherPsi(scriptPath, cwd)
                : new ProcessStartInfo("powershell.exe", argsLine);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = cwd;
            // 字节级捕获：不用 BeginOutputReadLine 的字符串回调（它把编码锁死在 StandardOutputEncoding）。
            // 子进程输出可能不是 UTF-8（老 CRT 程序按系统代码页 GBK 写管道），收齐字节后统一走
            // TextUtil.Decode 的 BOM → UTF-8 严格 → GBK 兜底链，中文输出不再变成替换符。
            MemoryStream outBuf = new MemoryStream();
            MemoryStream errBuf = new MemoryStream();
            Process process = new Process();
            process.StartInfo = psi;
            process.Start();
            _app.RegisterProcess(process);
            Task outReader = Task.Run(delegate { DrainStream(process.StandardOutput.BaseStream, outBuf); });
            Task errReader = Task.Run(delegate { DrainStream(process.StandardError.BaseStream, errBuf); });
            CancellationTokenRegistration cancelReg = default(CancellationTokenRegistration);
            try
            {
                cancelReg = token.Register(delegate
                {
                    try { if (!process.HasExited) KillTree(process); }
                    catch { }
                });
                bool exited = await Task.Run(new Func<bool>(delegate { return process.WaitForExit(timeout * 1000); })).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!exited)
                {
                    result.TimedOut = true;
                    result.Ok = false;
                    result.ErrorCode = "timeout";
                    result.Retryable = true;
                    KillTree(process);
                    try { process.WaitForExit(3000); } catch { }
                    result.Error = "命令执行超过 " + timeout + " 秒，已终止。";
                }
                // 排空读取任务：进程退出后流关闭，读循环自然结束；给 5 秒上限防僵尸进程卡住。
                // 旧实现用 WaitForExit(int) 不保证异步回调排空，命令最后的 stderr 关键行会概率性丢失。
                Task.WaitAll(new Task[] { outReader, errReader }, 5000);
                string outText = TextUtil.Decode(outBuf.ToArray(), 0);
                string errText = TextUtil.Decode(errBuf.ToArray(), 0);
                result.ExitCode = 0;
                try { result.ExitCode = process.ExitCode; } catch { }
                result.Output = TextUtil.StripAnsi(outText);
                if (sandboxed) result.Output = "【沙箱·低完整性】" + result.Output;
                if (!string.IsNullOrEmpty(errText)) result.Output += (result.Output.Length > 0 ? "\n[stderr]\n" : "") + TextUtil.StripAnsi(errText);
                if (result.Output.Length > 4000)
                {
                    string longPath = NewRunPath("out", ".txt");
                    File.WriteAllText(longPath, result.Output, new UTF8Encoding(false));
                    Artifact artifact = _app.CreateArtifact(longPath, "text/plain", "file");
                    result.Artifacts.Add(artifact);
                    result.Output = "输出较长（" + result.Output.Length + " 字符），已保存为附件：" + Path.GetFileName(longPath) + "\n" +
                                    TextUtil.Truncate(result.Output, 2500);
                }
                if (!result.TimedOut) result.Ok = result.ExitCode == 0;
                else result.Ok = false;
                if (result.Output.Length == 0) result.Output = "(无输出，exit=" + result.ExitCode + ")";
                if (ckNote.Length > 0) result.Output += (result.Output.Length > 0 ? "\n" : "") + ckNote;
                Log.Info("shell 完成: exit=" + result.ExitCode + ", " + result.Output.Length + " 字符, " + TextUtil.OneLine(command, 100));
                await Task.Yield();
                return result;
            }
            finally
            {
                try { cancelReg.Dispose(); } catch { }
                _app.UnregisterProcess(process);
                try { process.Dispose(); } catch { }
            }
        }

        /// <summary>把子进程输出流读到缓冲区，流关闭（进程退出）后自然返回。</summary>
        private static void DrainStream(Stream stream, MemoryStream buffer)
        {
            DrainStream(stream, buffer, 0, null);
        }

        /// <summary>带封顶的输出收集：超过 cap 就丢旧留新（滑动窗口），并标记截断。</summary>
        private static void DrainStream(Stream stream, MemoryStream buffer, int cap, BgShell owner)
        {
            try
            {
                byte[] chunk = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    lock (buffer)
                    {
                        buffer.Write(chunk, 0, read);
                        if (cap > 0 && buffer.Length > cap)
                        {
                            byte[] all = buffer.ToArray();
                            int keep = cap / 2;
                            buffer.SetLength(0);
                            buffer.Write(all, all.Length - keep, keep);
                            if (owner != null) owner.OutputTruncated = true;
                        }
                    }
                }
            }
            catch { /* 进程被杀/管道断开都会抛——输出收多少算多少 */ }
            finally
            {
                try { stream.Dispose(); } catch { }
            }
        }

        /// <summary>后台启动 shell：立刻返回 id，进程继续跑，输出封顶收集。</summary>
        private ToolResult StartBackgroundShell(ToolCall call, Dictionary<string, object> args, string command, CancellationToken token)
        {
            ToolResult result = new ToolResult();
            string cwdArg = Arg(args, "cwd", "");
            string cwd = string.IsNullOrEmpty(cwdArg) ? _config.WorkDir : Fs.Resolve(_config.WorkDir, cwdArg);
            if (!Directory.Exists(cwd)) cwd = _config.WorkDir;
            string scriptPath = NewRunPath("bgrun", ".ps1");
            StringBuilder script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Continue'");
            script.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
            script.AppendLine("$OutputEncoding = [System.Text.Encoding]::UTF8");
            script.AppendLine("$ProgressPreference = 'SilentlyContinue'");
            script.AppendLine("# GreenTiger background shell " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            script.AppendLine(command);
            try { File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = "脚本落盘失败：" + ex.Message;
                return result;
            }
            call.Command = command;
            result.Command = command;
            result.Cwd = cwd;
            if (Sandbox.BlockOutsideCwd(_config, cwd))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.Permission;
                result.Error = "沙箱模式下命令的工作目录必须在沙箱目录内（" + Sandbox.SandboxDir() +
                               "），已拒绝执行。需要动沙箱外的文件请让用户先关闭沙箱模式。";
                return result;
            }
            bool sandboxed = Sandbox.ShouldRunSandboxed(_config, cwd);
            // 未沙箱的后台命令同样先留档；留档失败不阻断启动。
            string ckNote = "";
            if (!sandboxed)
            {
                try
                {
                    string ckError;
                    string manifest = Checkpoint.Create(cwd, TaskIdOf(), Checkpoint.Caps.Default(), out ckError);
                    if (!string.IsNullOrEmpty(manifest))
                    {
                        call.BackupPath = "gtckpt:" + manifest;
                        ckNote = "[checkpoint 已留档：/undo 可回滚这条命令对文件的改动]";
                    }
                    else if (!string.IsNullOrEmpty(ckError))
                    {
                        ckNote = "[checkpoint 未留档：" + ckError + "]";
                    }
                }
                catch (Exception ckEx) { ckNote = "[checkpoint 未留档：" + ckEx.Message + "]"; }
            }
            ProcessStartInfo psi = sandboxed
                ? Sandbox.LauncherPsi(scriptPath, cwd)
                : new ProcessStartInfo("powershell.exe",
                    "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \"" + scriptPath + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = cwd;
            BgShell bg = new BgShell();
            bg.Command = command;
            bg.StartedAt = Message.Now();
            try
            {
                bg.Process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = "后台启动失败：" + ex.Message;
                return result;
            }
            lock (_bgGate)
            {
                _bgSeq++;
                bg.Id = "bg_" + _bgSeq;
                _bgShells[bg.Id] = bg;
            }
            _app.RegisterProcess(bg.Process);
            Task.Run(delegate { DrainStream(bg.Process.StandardOutput.BaseStream, bg.Out, BgOutputCapBytes, bg); });
            Task.Run(delegate { DrainStream(bg.Process.StandardError.BaseStream, bg.Err, BgOutputCapBytes / 4, bg); });
            Task.Run(delegate
            {
                try
                {
                    bg.Process.WaitForExit();
                    lock (_bgGate)
                    {
                        bg.Exited = true;
                        try { bg.ExitCode = bg.Process.ExitCode; } catch { }
                    }
                }
                catch (Exception ex)
                {
                    // 后台进程状态失真会让 shell_output / shell_kill 判断错误，必须留痕。
                    Log.Warn("后台 shell " + bg.Id + " 等待退出失败：" + ex.Message);
                    lock (_bgGate) { bg.Exited = true; }
                }
            });
            result.Ok = true;
            result.Output = "已在后台启动（id=" + bg.Id + "）。命令不会阻塞当前任务；" +
                            "用 shell_output（id=" + bg.Id + "）查看输出，shell_kill（id=" + bg.Id + "）终止。";
            if (ckNote.Length > 0) result.Output += "\n" + ckNote;
            Log.Info("后台 shell " + bg.Id + " 启动: " + TextUtil.OneLine(command, 100));
            return result;
        }

        /// <summary>查看后台 shell 的当前输出（id 省略且只有一个后台 shell 时自动选中）。</summary>
        private async Task<ToolResult> ShellOutputAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            ToolResult result = new ToolResult();
            string id = Arg(args, "id", "");
            BgShell bg;
            lock (_bgGate)
            {
                if (string.IsNullOrEmpty(id))
                {
                    BgShell running = null;
                    int alive = 0;
                    foreach (KeyValuePair<string, BgShell> kv in _bgShells)
                    {
                        if (!kv.Value.Exited) { running = kv.Value; alive++; }
                    }
                    if (alive == 1) bg = running;
                    else if (alive == 0 && _bgShells.Count == 1) bg = new List<BgShell>(_bgShells.Values)[0];
                    else
                    {
                        result.Output = "没有正在运行的后台 shell" + (alive == 0 && _bgShells.Count == 0 ? "。" : "（有多个，请指定 id：" + string.Join("、", new List<string>(_bgShells.Keys).ToArray()) + "）");
                        return result;
                    }
                }
                else if (!_bgShells.TryGetValue(id, out bg) || bg == null)
                {
                    result.Ok = false;
                    result.ErrorCode = ToolErrorCode.NotFound;
                    result.Error = "没有 id=" + id + " 的后台 shell（现有：" + string.Join("、", new List<string>(_bgShells.Keys).ToArray()) + "）";
                    return result;
                }
            }
            string outText = TextUtil.Decode(SnapshotBytes(bg.Out), 0);
            string errText = TextUtil.Decode(SnapshotBytes(bg.Err), 0);
            string combined = outText;
            if (!string.IsNullOrEmpty(errText)) combined += (combined.Length > 0 ? "\n[stderr]\n" : "") + errText;
            combined = TextUtil.StripAnsi(combined);
            StringBuilder sb = new StringBuilder();
            long elapsed = Message.Now() - bg.StartedAt;
            sb.Append("[").Append(bg.Id).Append("] ").Append(bg.Exited
                ? "已退出（exit=" + bg.ExitCode + "，运行 " + TimeUtil.Duration(elapsed) + "）"
                : "仍在运行（已 " + TimeUtil.Duration(elapsed) + "）").Append('\n');
            if (bg.OutputTruncated) sb.Append("（输出超过 ").Append(Fs.HumanSize(BgOutputCapBytes)).Append(" 已截断，保留最新的）\n");
            sb.Append(TextUtil.OneLine(bg.Command, 120)).Append("\n\n");
            if (combined.Length == 0) sb.Append("（暂无输出）");
            else sb.Append(ContextPolicy.TruncateHeadTail(combined, 6000, 3000));
            result.Output = sb.ToString();
            await Task.Yield();
            return result;
        }

        /// <summary>终止后台 shell（连同它的整棵子进程树）。</summary>
        private async Task<ToolResult> ShellKillAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            ToolResult result = new ToolResult();
            string id = Arg(args, "id", "");
            BgShell bg;
            lock (_bgGate)
            {
                if (string.IsNullOrEmpty(id) || !_bgShells.TryGetValue(id, out bg) || bg == null)
                {
                    result.Ok = false;
                    result.ErrorCode = ToolErrorCode.NotFound;
                    result.Error = "没有 id=" + id + " 的后台 shell（现有：" + string.Join("、", new List<string>(_bgShells.Keys).ToArray()) + "）";
                    return result;
                }
            }
            long elapsed = Message.Now() - bg.StartedAt;
            if (!bg.Exited) KillTree(bg.Process);
            lock (_bgGate)
            {
                bg.Exited = true;
                try { bg.ExitCode = bg.Process.ExitCode; } catch { }
            }
            result.Output = "已终止 " + bg.Id + "（运行了 " + TimeUtil.Duration(elapsed) + "）：" + TextUtil.OneLine(bg.Command, 120);
            Log.Info("后台 shell " + id + " 已终止");
            await Task.Yield();
            return result;
        }

        /// <summary>后台 shell 输出快照（带锁拷贝，与 DrainStream 的写入锁同一对象）。</summary>
        private static byte[] SnapshotBytes(MemoryStream buf)
        {
            lock (buf)
            {
                return buf.ToArray();
            }
        }

        // ---------------- MCP 跨服务器数据流（监督第三防） ----------------

        /// <summary>一条不可信来源的返回内容留底：来源描述、内容（截断）、什么时候。</summary>
        internal class ExtFlowRecord
        {
            public string Source;
            public string Text;
            public long At;
        }

        /// <summary>MCP 结果留底（source = 外部工具 <服务器名>）。查不到服务器名就不留底。</summary>
        internal void RecordMcpFlow(string fullName, string output)
        {
            try
            {
                string server = McpServerOf(fullName);
                if (!string.IsNullOrEmpty(server)) RecordExtResult("外部工具 " + server, output);
            }
            catch { }
        }

        /// <summary>留底核心：来源描述 + 输出（上限 24 条，超了丢最旧的；正文只留前 8KB——探针用不了那么长）。</summary>
        internal void RecordExtResult(string source, string output)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(output)) return;
            lock (_flowGate)
            {
                ExtFlowRecord r = new ExtFlowRecord();
                r.Source = source;
                r.Text = output.Length > 8192 ? output.Substring(0, 8192) : output;
                r.At = Message.Now();
                _extFlows.Add(r);
                while (_extFlows.Count > 24) _extFlows.RemoveAt(0);
            }
        }

        private string McpServerOf(string fullName)
        {
            try { return _app.Mcp != null ? _app.Mcp.ServerOf(fullName) : ""; }
            catch { return ""; }
        }

        /// <summary>web 三兄弟的结果留底：来源描述取参数里的 url / query（失败结果不留底）。</summary>
        private void RecordWebResult(Dictionary<string, object> args, ToolResult result)
        {
            try
            {
                if (result == null || !result.Ok || string.IsNullOrEmpty(result.Output)) return;
                string url = Arg(args, "url", "");
                string source;
                if (!string.IsNullOrEmpty(url)) source = "网页 " + (url.Length > 100 ? url.Substring(0, 100) + "…" : url);
                else
                {
                    string q = Arg(args, "query", "");
                    source = string.IsNullOrEmpty(q) ? "搜索结果" : "搜索 " + (q.Length > 60 ? q.Substring(0, 60) + "…" : q);
                }
                RecordExtResult(source, result.Output);
            }
            catch { }
        }

        /// <summary>
        /// 检测本次调用的参数里是否包含「不可信来源」之前返回的内容（shell / 写文件用，无同源豁免）。
        /// 归一化（去空白/引号/反斜杠）后做 32 字符滑窗探针：模型转述结果时空格引号转义都会变，
        /// 归一化能显著提高召回；连续命中基本不可能是巧合，误报代价只是确认框多一行黄字。
        /// </summary>
        internal string DetectExtFlow(string argsJson)
        {
            if (string.IsNullOrEmpty(argsJson)) return null;
            List<ExtFlowRecord> flows;
            lock (_flowGate)
            {
                if (_extFlows.Count == 0) return null;
                flows = new List<ExtFlowRecord>(_extFlows);
            }
            return DetectFlowCore(flows, null, argsJson, Message.Now());
        }

        /// <summary>
        /// 检测本次 MCP 调用的参数里是否包含其他来源的返回内容（同源豁免：服务器自己的记录不算）。
        /// 服务器名查不到（未连接）时无法豁免——宁可全查，宁严勿松。
        /// </summary>
        internal string DetectCrossServerFlow(string fullName, string argsJson)
        {
            if (string.IsNullOrEmpty(argsJson)) return null;
            string server = McpServerOf(fullName);
            List<ExtFlowRecord> flows;
            lock (_flowGate)
            {
                if (_extFlows.Count == 0) return null;
                flows = new List<ExtFlowRecord>(_extFlows);
            }
            return DetectFlowCore(flows, string.IsNullOrEmpty(server) ? null : "外部工具 " + server, argsJson, Message.Now());
        }

        /// <summary>检测核心（纯逻辑，自检直接构造 flows 断言）。exemptSource 非空时跳过同源记录。</summary>
        internal static string DetectFlowCore(List<ExtFlowRecord> flows, string exemptSource, string argsJson, long now)
        {
            if (flows == null || flows.Count == 0 || string.IsNullOrEmpty(argsJson)) return null;
            string normArgs = NormalizeFlowText(argsJson);
            if (normArgs.Length < 32) return null;
            // 窗口 32 / 步长 16：共享段 ≥47 字符即可保证存在完全落入共享段的探针窗口
            // （64/32 的组合会让探针被前后缀污染而漏检，实测踩过）。32 字符无空白命中不会是巧合。
            for (int i = 0; i + 32 <= normArgs.Length; i += 16)
            {
                string probe = normArgs.Substring(i, 32);
                for (int k = 0; k < flows.Count; k++)
                {
                    ExtFlowRecord r = flows[k];
                    if (r == null || string.IsNullOrEmpty(r.Text)) continue;
                    if (now - r.At > 15 * 60 * 1000) continue;   // 15 分钟前的留底不再算
                    if (exemptSource != null && string.Equals(r.Source, exemptSource, StringComparison.OrdinalIgnoreCase)) continue;
                    string normText = NormalizeFlowText(r.Text);
                    if (normText.Length < 32) continue;
                    if (normText.IndexOf(probe, StringComparison.Ordinal) >= 0) return r.Source;
                }
            }
            return null;
        }

        /// <summary>数据流检测用的归一化：去掉空白、引号与反斜杠（JSON 转义差异归一）。</summary>
        internal static string NormalizeFlowText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '"' || c == '\\') continue;
                b.Append(c);
            }
            return b.ToString();
        }

        // ---------------- repo_map ----------------

        private static readonly string[] RepoMapSkip = new string[]
        {
            "node_modules", ".git", "dist", "bin", "obj", "packages", ".zwork", ".greentiger",
            "__pycache__", ".vs", ".idea", ".svn", "target", "build", ".cache", "GreenTiger-data"
        };

        /// <summary>
        /// 仓库结构概览（评估报告 #6）：目录树 + 每目录文件数/大小/最近修改 + 类型分布。
        /// 只看两层，不读文件内容；让模型一上来就对仓库有整体感，省掉连环 list_dir。
        /// symbols=true 深度模式：逐源码文件列出顶级符号（类/函数，带行号）——「这个功能在哪个文件」
        /// 一眼可见。正则级提取（构建链不引 tree-sitter，导航用途足够，不追求编译级精确）。
        /// </summary>
        private ToolResult RepoMap(bool symbols)
        {
            ToolResult result = new ToolResult();
            string root = _config.WorkDir;
            try
            {
                if (!Directory.Exists(root))
                {
                    result.Ok = false;
                    result.Error = "工作目录不存在：" + root;
                    return result;
                }
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(root + "（两层概览；列目录用 list_dir，找文件用 glob，全文检索用 grep）");
                if (symbols)
                {
                    // 符号模式：递归源码文件（排除表同样生效），每文件一行「路径: 符号 (行号) …」
                    List<string> srcFiles = new List<string>();
                    CollectSourceFiles(root, srcFiles, 0);
                    srcFiles.Sort(StringComparer.OrdinalIgnoreCase);
                    if (srcFiles.Count == 0) sb.AppendLine("（没有发现源码文件；symbols 模式认 .cs/.js/.ts/.mjs/.cjs/.ps1/.py）");
                    int shown = 0;
                    for (int f = 0; f < srcFiles.Count && shown < 40; f++)
                    {
                        List<string> syms = FileSymbols(srcFiles[f]);
                        if (syms.Count == 0) continue;
                        shown++;
                        // .NET Framework 没有 Path.GetRelativePath，手写前缀剥离
                        string rel = srcFiles[f];
                        string rootTrim = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (rel.StartsWith(rootTrim, StringComparison.OrdinalIgnoreCase))
                            rel = rel.Substring(rootTrim.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        sb.AppendLine(rel + ": " + string.Join(" · ", syms.ToArray()));
                        if (syms.Count >= 24) sb.AppendLine("    …（符号更多，用 grep 定位）");
                    }
                    if (srcFiles.Count > shown) sb.AppendLine("…（共 " + srcFiles.Count + " 个源码文件，只列前 " + shown + " 个有符号的）");
                }
                else
                {
                    List<string> dirs = new List<string>(Directory.GetDirectories(root));
                    List<string> files = new List<string>(Directory.GetFiles(root));
                    dirs.Sort(StringComparer.OrdinalIgnoreCase);
                    int dirCount = 0;
                    foreach (string dir in dirs)
                    {
                        if (dirCount >= 60) { sb.AppendLine("…（目录太多，只列前 60 个）"); break; }
                        string dn = Path.GetFileName(dir);
                        if (Array.IndexOf(RepoMapSkip, dn, 0, RepoMapSkip.Length) >= 0 ||
                            dn.StartsWith(".", StringComparison.Ordinal)) { continue; }
                        dirCount++;
                        DirStat(dir, dn, sb, 0);
                    }
                    if (files.Count > 0)
                    {
                        sb.AppendLine("· 顶层文件 " + files.Count + " 个：" +
                            string.Join(", ", TakeNames(files, 10)) + (files.Count > 10 ? " …" : ""));
                    }
                }
                result.Output = sb.ToString();
                if (result.Output.Length > 4000) result.Output = result.Output.Substring(0, 4000) + "\n…（截断）";
                result.Ok = true;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ex.Message;
            }
            return result;
        }

        private static void DirStat(string dir, string name, StringBuilder sb, int depth)
        {
            try
            {
                string[] files = Directory.GetFiles(dir);
                string[] subs = Directory.GetDirectories(dir);
                long bytes = 0;
                DateTime latest = DateTime.MinValue;
                foreach (string f in files)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(f);
                        bytes += fi.Length;
                        if (fi.LastWriteTime > latest) latest = fi.LastWriteTime;
                    }
                    catch { }
                }
                foreach (string s in subs)
                {
                    try
                    {
                        DirectoryInfo di = new DirectoryInfo(s);
                        if (di.LastWriteTime > latest) latest = di.LastWriteTime;
                    }
                    catch { }
                }
                sb.AppendLine((depth == 0 ? "├─ " : "│   ├─ ") + name + "/ · " + files.Length + " 文件 · " +
                    (bytes < 1024 ? bytes + " B" : bytes < 1048576 ? (bytes / 1024) + " KB" : (bytes / 1048576) + " MB") +
                    (latest > DateTime.MinValue ? " · 改于 " + latest.ToString("MM-dd HH:mm") : ""));
                // 第二层：只列有内容的子目录，最多 8 个
                if (depth == 0)
                {
                    int shown = 0;
                    foreach (string s in subs)
                    {
                        string sn = Path.GetFileName(s);
                        if (Array.IndexOf(RepoMapSkip, sn, 0, RepoMapSkip.Length) >= 0 ||
                            sn.StartsWith(".", StringComparison.Ordinal)) continue;
                        if (Directory.GetFiles(s).Length == 0 && Directory.GetDirectories(s).Length == 0) continue;
                        if (shown >= 8) { sb.AppendLine("│   └─ …"); break; }
                        DirStat(s, sn, sb, 1);
                        shown++;
                    }
                }
            }
            catch { }
        }

        private static string TakeNames(List<string> files, int max)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < files.Count && i < max; i++) names.Add(Path.GetFileName(files[i]));
            return string.Join(", ", names.ToArray());
        }

        // ---------------- repo_map symbols 深度模式 ----------------

        private static readonly string[] SymbolExts = new string[] { ".cs", ".js", ".ts", ".mjs", ".cjs", ".ps1", ".py" };
        private static readonly string[] ControlWords = new string[]
        {
            "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return",
            "throw", "else", "do", "try", "finally", "fixed", "checked", "unchecked"
        };

        private static void CollectSourceFiles(string dir, List<string> acc, int depth)
        {
            if (depth > 4 || acc.Count > 200) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    if (Array.IndexOf(SymbolExts, Path.GetExtension(f).ToLowerInvariant()) >= 0) acc.Add(f);
                }
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string dn = Path.GetFileName(d);
                    if (Array.IndexOf(RepoMapSkip, dn, 0, RepoMapSkip.Length) >= 0 ||
                        dn.StartsWith(".", StringComparison.Ordinal)) continue;
                    CollectSourceFiles(d, acc, depth + 1);
                }
            }
            catch { }
        }

        /// <summary>单文件符号提取（顶级为主，控制噪音）。返回「符号 (行)」片段，最多 24 个。</summary>
        internal static List<string> FileSymbols(string path)
        {
            List<string> syms = new List<string>();
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                string[] lines = File.ReadAllLines(path);
                int max = Math.Min(lines.Length, 3000);
                for (int i = 0; i < max && syms.Count < 24; i++)
                {
                    string line = lines[i];
                    if (line.Length == 0 || line.Length > 200) continue;
                    string t = line.Trim();
                    if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal) ||
                        t.StartsWith("/*", StringComparison.Ordinal) || t.StartsWith("#", StringComparison.Ordinal)) continue;
                    string indent = line.Substring(0, line.Length - t.Length);
                    bool shallow = indent.Length <= 4;
                    string sym = null;
                    if (ext == ".cs")
                    {
                        System.Text.RegularExpressions.Match m =
                            System.Text.RegularExpressions.Regex.Match(t,
                                @"(?:class|interface|enum|struct|record)\s+([A-Za-z_]\w*)");
                        if (m.Success && shallow) sym = m.Groups[1].Value;
                        else if (shallow && t.Contains("(") && !t.Contains(";") && !t.Contains("="))
                        {
                            // C# 方法签名：缩进浅、含 "("、不以 ; 结尾、不含 =（排除属性/字段初始化）
                            int p = t.IndexOf('(');
                            string head = t.Substring(0, p).Trim();
                            string name = head.Length > 0 ? head.Substring(head.LastIndexOf(' ') + 1) : "";
                            if (name.Length > 0 && Array.IndexOf(ControlWords, name) < 0 && !char.IsDigit(name[0]))
                                sym = name;
                        }
                    }
                    else if (ext == ".ps1")
                    {
                        System.Text.RegularExpressions.Match m =
                            System.Text.RegularExpressions.Regex.Match(t, @"^function\s+([A-Za-z_][\w-]*)");
                        if (m.Success) sym = m.Groups[1].Value;
                    }
                    else if (ext == ".py")
                    {
                        if (indent.Length == 0)
                        {
                            System.Text.RegularExpressions.Match m =
                                System.Text.RegularExpressions.Regex.Match(t, @"^(?:def|class)\s+([A-Za-z_]\w*)");
                            if (m.Success) sym = m.Groups[1].Value;
                        }
                    }
                    else   // .js/.ts/.mjs/.cjs
                    {
                        if (indent.Length == 0)
                        {
                            System.Text.RegularExpressions.Match m =
                                System.Text.RegularExpressions.Regex.Match(t,
                                    @"^(?:export\s+)?(?:default\s+)?(?:async\s+)?function\*?\s+([A-Za-z_$][\w$]*)");
                            if (m.Success) sym = m.Groups[1].Value;
                            else
                            {
                                m = System.Text.RegularExpressions.Regex.Match(t, @"^(?:export\s+)?class\s+([A-Za-z_$][\w$]*)");
                                if (m.Success) sym = m.Groups[1].Value;
                                else
                                {
                                    m = System.Text.RegularExpressions.Regex.Match(t,
                                        @"^(?:export\s+)?(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?\(");
                                    if (m.Success) sym = m.Groups[1].Value;
                                }
                            }
                        }
                    }
                    if (sym != null) syms.Add(sym + " (" + (i + 1) + ")");
                }
            }
            catch { }
            return syms;
        }

        private static void KillTree(Process process)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi)) { p.WaitForExit(3000); }
            }
            catch (Exception ex) { Log.Warn("taskkill 终止进程树失败 (PID " + process.Id + ")：" + ex.Message); }
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception ex) { Log.Warn("强制终止进程失败 (PID " + process.Id + ")：" + ex.Message); }
        }

        /// <summary>路径边界检查：越界一律标成 danger 并写明原因，由审批流程逐条确认。
        /// 执行层不再重复拦截——越界是否放行由用户在审批卡上决定；但受控试运行(allow)不豁免，见 Agent.IsBoundaryRisk。</summary>
        private ToolResult CheckPathRisk(ToolCall call, string full, bool write)
        {
            ToolResult result = new ToolResult();
            result.Command = full;
            if (!Fs.IsInside(_config.WorkDir, full))
            {
                call.Risk = "danger";
                call.RiskReason = "路径在工作目录之外";
            }
            return result;
        }

        // 解码统一走 TextUtil.Decode（BOM → UTF-8 严格 → GBK 兜底）。
        // 以前这里是同一套算法的第二份拷贝，改一处会漏一处。
        private static string DecodeText(byte[] bytes)
        {
            return TextUtil.Decode(bytes, 0);
        }

        private async Task<ToolResult> ReadFileAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string path = Arg(args, "path", "");
            string full = Fs.Resolve(_config.WorkDir, path);
            ToolResult result = CheckPathRisk(call, full, false);
            if (!result.Ok) return result;
            if (!File.Exists(full))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.NotFound;
                result.Error = "文件不存在: " + full;
                return result;
            }
            FileInfo info = new FileInfo(full);
            if (info.Length > 8 * 1024 * 1024)
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.BadArgs;
                result.Error = "文件太大（" + Fs.HumanSize(info.Length) + "），请用 offset/limit 分段读，或用 shell 的 Get-Content -TotalCount。";
                return result;
            }
            // 兼容旧参数 max_bytes：给了就按字节截断；给了 offset/limit 就按行切片。
            int maxBytes = ArgInt(args, "max_bytes", 0);
            if (maxBytes <= 0) maxBytes = (int)Math.Min(info.Length, 8 * 1024 * 1024);
            int offset = ArgInt(args, "offset", 1);
            if (offset < 1) offset = 1;
            int limit = ArgInt(args, "limit", 0);
            bool ranged = args.ContainsKey("offset") || args.ContainsKey("limit");
            if (limit <= 0) limit = ranged ? 400 : 2000;
            if (limit > 5000) limit = 5000;

            byte[] bytes;
            using (FileStream fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int length = (int)Math.Min(fs.Length, Math.Max(1, maxBytes));
                bytes = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = await fs.ReadAsync(bytes, read, length - read, token).ConfigureAwait(false);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < length) Array.Resize(ref bytes, read);
            }
            // UTF-16/UTF-32 文本（Windows 记事本「Unicode」、Out-File -Encoding unicode 产物）
            // 每第二个字节就是 0x00，不能只凭「含 0 字节」判二进制——先查 BOM，命中就走对应解码。
            bool binary;
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            {
                binary = false;   // UTF-16 BOM：一定是文本
            }
            else if (bytes.Length >= 4 && ((bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) ||
                                           (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)))
            {
                binary = false;   // UTF-32 BOM
            }
            else
            {
                binary = false;
                for (int i = 0; i < Math.Min(bytes.Length, 8000); i++) { if (bytes[i] == 0) { binary = true; break; } }
            }
            if (binary)
            {
                result.Ok = false;
                result.Error = "这是二进制文件（" + Fs.HumanSize(info.Length) + "）。请使用 upload_file 上传，或让 shell 处理。";
                return result;
            }
            string text = DecodeText(bytes);
            string norm = text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (norm.EndsWith("\n", StringComparison.Ordinal)) norm = norm.Substring(0, norm.Length - 1);
            string[] lines = norm.Length == 0 ? new string[] { "" } : norm.Split('\n');
            // 3.30.0 长文档：先给索引，不把正文倒进上下文。模型再按 offset/limit 读需要的段。
            if (ArgBool(args, "outline", false))
            {
                StringBuilder ob = new StringBuilder();
                ob.Append("文件: ").Append(full).Append('\n');
                ob.Append("大小: ").Append(Fs.HumanSize(info.Length)).Append("，行数: ").Append(lines.Length).Append("\n\n");
                ob.Append(FileOps.OutlineText(norm, full, 240));
                ob.Append("\n\n用 offset/limit 读具体行段；不要凭索引猜正文。");
                result.Output = ob.ToString();
                NoteRead(full);
                return result;
            }
            if (offset > lines.Length)
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.BadArgs;
                result.Error = "offset=" + offset + " 超出文件行数（共 " + lines.Length + " 行）。";
                return result;
            }
            int start = offset - 1;
            int stop = Math.Min(lines.Length, start + limit);
            bool cut = false;
            StringBuilder sb = new StringBuilder();
            sb.Append("文件: ").Append(full).Append('\n');
            sb.Append("大小: ").Append(Fs.HumanSize(info.Length)).Append("，行数: ").Append(lines.Length);
            if (start > 0 || stop < lines.Length) sb.Append("，本次显示第 ").Append(start + 1).Append('-').Append(stop).Append(" 行");
            sb.Append("\n\n");
            int budgetChars = ContextPolicy.ReadFileChars(_config);
            for (int i = start; i < stop; i++)
            {
                string line = lines[i];
                if (line.Length > 4000) line = line.Substring(0, 4000) + "…";
                string numbered = (i + 1).ToString().PadLeft(6) + "  " + line + "\n";
                if (sb.Length + numbered.Length > budgetChars)
                {
                    sb.Append("…（输出已达上限，未显示第 ").Append(i + 1).Append(" 行之后的内容，可用 offset 继续读）\n");
                    cut = true;
                    break;
                }
                sb.Append(numbered);
            }
            if (!cut && stop < lines.Length)
            {
                sb.Append("…（还有 ").Append(lines.Length - stop).Append(" 行，可用 offset=").Append(stop + 1).Append(" 继续读）\n");
            }
            result.Output = sb.ToString();
            NoteRead(full);
            return result;
        }

        /// <summary>
        /// 精确编辑（Edit）：
        /// 必须先用 read_file 读过这个文件；old_string 命中多处时拒绝，除非显式 replace_all。
        /// 改动前留快照，输出里带 diff，模型和用户都能看到到底改了什么。
        /// </summary>
        private async Task<ToolResult> EditAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string path = Arg(args, "path", "");
            string oldText = Arg(args, "old_string", "");
            string newText = Arg(args, "new_string", "");
            bool replaceAll = ArgBool(args, "replace_all", false);
            string full = Fs.Resolve(_config.WorkDir, path);
            ToolResult result = CheckPathRisk(call, full, true);
            if (!result.Ok) return result;
            if (string.IsNullOrEmpty(path))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.BadArgs;
                result.Error = "path 不能为空";
                return result;
            }
            if (!File.Exists(full))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.NotFound;
                result.Error = "文件不存在: " + full + "。新建文件请用 write_file。";
                return result;
            }
            if (!WasRead(full))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.Permission;
                result.Error = "改之前先读：" + full + " 这次任务里还没被 read_file 读过。" +
                                "先 read_file 看清当前内容（含缩进与上下文）再 edit，避免凭印象盲改。";
                return result;
            }
            FileInfo info = new FileInfo(full);
            if (info.Length > 8 * 1024 * 1024)
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.BadArgs;
                result.Error = "文件太大（" + Fs.HumanSize(info.Length) + "），edit 只适合常规源码/文本文件；请用 shell 处理。";
                return result;
            }
            Encoding enc;
            string original = FileOps.ReadTextAuto(full, out enc);
            FileOps.EditResult edit = FileOps.ApplyEdit(original, oldText, newText, replaceAll);
            if (!edit.Ok)
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.BadArgs;
                result.Error = edit.Error;
                return result;
            }
            string backupError;
            string backup = FileOps.Snapshot(full, TaskIdOf(), out backupError);
            call.BackupPath = backup;
            try
            {
                File.WriteAllText(full, edit.Text, enc);
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.ToolError;
                result.Error = "写入失败：" + ex.Message;
                return result;
            }
            CaptureChangeDiff(call, path, original, edit.Text);
            int beforeLines = CountLines(original);
            int afterLines = CountLines(edit.Text);
            string diff = FileOps.Diff(original, edit.Text, path, 60);
            StringBuilder sb = new StringBuilder();
            sb.Append("已修改 ").Append(full).Append("（起始第 ").Append(edit.Line).Append(" 行，替换 ")
              .Append(edit.Count).Append(" 处，匹配方式 ").Append(edit.Mode).Append("）\n");
            sb.Append("行数：").Append(beforeLines).Append(" → ").Append(afterLines).Append('\n');
            if (!string.IsNullOrEmpty(backup)) sb.Append("原文快照：").Append(backup).Append('\n');
            else sb.Append("（快照失败：").Append(backupError).Append("，本次改动没有备份）\n");
            sb.Append('\n').Append(diff);
            string editSyntax = SyntaxCheckNote(full);
            if (editSyntax.Length > 0) sb.Append("\n\n").Append(editSyntax);
            result.Output = sb.ToString();
            NoteRead(full); // 改完内容变了，仍然算「读过」，且后续 edit 的上下文以最新内容为准
            await Task.Yield();
            return result;
        }

        /// <summary>
        /// 批量编辑（MultiEdit）：对同一文件按顺序应用多个 old→new 替换。
        /// 与逐次 edit 相比只做一次读、一次快照、一次写盘；任一步失败立即中止且不写盘
        /// （全部成功才一次性写回，文件保持原样，不会出现「改了一半」的中间状态）。
        /// </summary>
        private async Task<ToolResult> MultiEditAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string path = Arg(args, "path", "");
            ToolResult result = new ToolResult();
            List<object> editsRaw = Json.Array(args, "edits");
            if (string.IsNullOrEmpty(path))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.BadArgs;
                result.Error = "path 不能为空";
                return result;
            }
            if (editsRaw.Count == 0)
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.BadArgs;
                result.Error = "edits 不能为空：[{\"old_string\":\"...\",\"new_string\":\"...\"}, ...]";
                return result;
            }
            string full = Fs.Resolve(_config.WorkDir, path);
            result.Command = full;
            ToolResult risk = CheckPathRisk(call, full, true);
            if (risk != null && !risk.Ok) return risk;
            if (!File.Exists(full))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.NotFound;
                result.Error = "文件不存在: " + full + "。新建文件请用 write_file。";
                return result;
            }
            if (!WasRead(full))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.Permission;
                result.Error = "改之前先读：" + full + " 这次任务里还没被 read_file 读过。先 read_file 再 multiedit。";
                return result;
            }
            FileInfo info = new FileInfo(full);
            if (info.Length > 8 * 1024 * 1024)
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.BadArgs;
                result.Error = "文件太大（" + Fs.HumanSize(info.Length) + "），请用 shell 处理。";
                return result;
            }
            Encoding enc;
            string current = FileOps.ReadTextAuto(full, out enc);
            string original = current;
            List<FileOps.EditResult> applied = new List<FileOps.EditResult>();
            for (int i = 0; i < editsRaw.Count; i++)
            {
                Dictionary<string, object> item = editsRaw[i] as Dictionary<string, object>;
                if (item == null)
                {
                    result.Ok = false;
                    result.ErrorCode = ToolErrorCode.BadArgs;
                    result.Error = "edits[" + i + "] 不是对象";
                    return result;
                }
                string oldText = Arg(item, "old_string", "");
                string newText = Arg(item, "new_string", "");
                bool replaceAll = ArgBool(item, "replace_all", false);
                if (oldText.Length == 0)
                {
                    result.Ok = false;
                    result.ErrorCode = ToolErrorCode.BadArgs;
                    result.Error = "edits[" + i + "] 缺少 old_string（清空内容请用 write_file）";
                    return result;
                }
                FileOps.EditResult edit = FileOps.ApplyEdit(current, oldText, newText, replaceAll);
                if (!edit.Ok)
                {
                    result.Ok = false;
                    result.ErrorCode = ToolErrorCode.BadArgs;
                    result.Error = "edits[" + i + "] 失败：" + edit.Error +
                        (i > 0 ? "\n（前面的 " + applied.Count + " 个替换未写入磁盘——全部成功才一次性写回，文件保持原样。）" : "");
                    return result;
                }
                current = edit.Text;
                applied.Add(edit);
            }
            string backupError;
            string backup = FileOps.Snapshot(full, TaskIdOf(), out backupError);
            call.BackupPath = backup;
            try
            {
                File.WriteAllText(full, current, enc);
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.ToolError;
                result.Error = "写入失败：" + ex.Message;
                return result;
            }
            CaptureChangeDiff(call, path, original, current);
            string diff = FileOps.Diff(original, current, path, 60);
            StringBuilder sb = new StringBuilder();
            sb.Append("已应用 ").Append(applied.Count).Append("/").Append(editsRaw.Count).Append(" 个替换到 ").Append(full).Append('\n');
            for (int i = 0; i < applied.Count; i++)
            {
                sb.Append("- edits[").Append(i).Append("] 第 ").Append(applied[i].Line).Append(" 行，替换 ")
                  .Append(applied[i].Count).Append(" 处（").Append(applied[i].Mode).Append("）\n");
            }
            sb.Append("行数：").Append(CountLines(original)).Append(" → ").Append(CountLines(current)).Append('\n');
            if (!string.IsNullOrEmpty(backup)) sb.Append("原文快照：").Append(backup).Append('\n');
            sb.Append('\n').Append(diff);
            string multiSyntax = SyntaxCheckNote(full);
            if (multiSyntax.Length > 0) sb.Append("\n\n").Append(multiSyntax);
            result.Output = sb.ToString();
            NoteRead(full);
            await Task.Yield();
            return result;
        }

        /// <summary>按通配符找文件（Glob）：结果按修改时间倒序，附大小与时间。</summary>
        private async Task<ToolResult> GlobAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string pattern = Arg(args, "pattern", "");
            string path = Arg(args, "path", ".");
            ToolResult result = new ToolResult();
            if (string.IsNullOrEmpty(pattern))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.BadArgs;
                result.Error = "pattern 不能为空，例如 **/*.cs 或 *.md";
                return result;
            }
            string root = Fs.Resolve(_config.WorkDir, path);
            result.Command = root;
            if (!Directory.Exists(root))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.NotFound;
                result.Error = "目录不存在: " + root;
                return result;
            }
            int limit = ArgInt(args, "limit", 200);
            if (limit <= 0) limit = 200;
            if (limit > 1000) limit = 1000;
            bool truncated;
            List<FileOps.GlobHit> hits = FileOps.Glob(root, pattern, limit, 20000, out truncated);
            if (hits.Count == 0)
            {
                result.Output = "没有匹配 " + pattern + " 的文件（目录：" + root + "）";
                await Task.Yield();
                return result;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("匹配 ").Append(pattern).Append("：").Append(hits.Count).Append(" 个文件");
            if (truncated) sb.Append("（已截断）");
            sb.Append('\n');
            for (int i = 0; i < hits.Count; i++)
            {
                FileOps.GlobHit h = hits[i];
                sb.Append(h.Path).Append("  (").Append(Fs.HumanSize(h.Size)).Append(", ")
                  .Append(TimeUtil.Ago(h.Modified)).Append(")\n");
            }
            result.Output = sb.ToString();
            await Task.Yield();
            return result;
        }

        /// <summary>内容检索（Grep）：返回 文件:行号: 内容，跳过二进制与大文件。</summary>
        private async Task<ToolResult> GrepAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string pattern = Arg(args, "pattern", "");
            string include = Arg(args, "include", "");
            bool ignoreCase = ArgBool(args, "ignore_case", false);
            string path = Arg(args, "path", ".");
            ToolResult result = new ToolResult();
            if (string.IsNullOrEmpty(pattern))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.BadArgs;
                result.Error = "pattern 不能为空（正则）";
                return result;
            }
            string root = Fs.Resolve(_config.WorkDir, path);
            result.Command = root;
            if (!Directory.Exists(root))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.NotFound;
                result.Error = "目录不存在: " + root;
                return result;
            }
            int maxHits = ArgInt(args, "max_results", 100);
            if (maxHits <= 0) maxHits = 100;
            if (maxHits > 500) maxHits = 500;
            int scanned;
            bool truncated;
            List<FileOps.GrepHit> hits;
            try
            {
                hits = FileOps.Grep(root, pattern, include, ignoreCase, maxHits, 3000, out scanned, out truncated);
            }
            catch (ArgumentException ex)
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.BadArgs;
                result.Error = ex.Message;
                return result;
            }
            StringBuilder sb = new StringBuilder();
            if (hits.Count == 0)
            {
                sb.Append("没有匹配 ").Append(pattern).Append(" 的内容（扫描 ").Append(scanned).Append(" 个文本文件）");
                result.Output = sb.ToString();
                await Task.Yield();
                return result;
            }
            sb.Append("匹配 ").Append(pattern).Append("：").Append(hits.Count).Append(" 处，扫描 ").Append(scanned).Append(" 个文件");
            if (truncated) sb.Append("（已截断，可缩小 include 或提高 max_results）");
            sb.Append('\n');
            for (int i = 0; i < hits.Count; i++)
            {
                sb.Append(hits[i].Path).Append(':').Append(hits[i].Line).Append(": ").Append(hits[i].Text).Append('\n');
            }
            result.Output = sb.ToString();
            await Task.Yield();
            return result;
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int n = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') n++;
            if (text.EndsWith("\n", StringComparison.Ordinal)) n--;
            return n;
        }

        /// <summary>子代理执行深度：>0 表示当前是子代理在执行工具（TaskSubagentAsync 期间递增）。</summary>
        private int _subagentDepth;

        // ---------- 后台 shell（run_in_background / BashOutput / KillBash） ----------
        // dev server / watch / 长构建不阻塞对话：shell(background=true) 启动后立刻返回 id，
        // shell_output 轮询输出，shell_kill 终止。输出环形封顶（512KB），进程随应用退出被杀。

        private const int BgOutputCapBytes = 512 * 1024;

        private class BgShell
        {
            public string Id;
            public string Command;
            public Process Process;
            public MemoryStream Out = new MemoryStream();
            public MemoryStream Err = new MemoryStream();
            public long StartedAt;
            public bool OutputTruncated;
            public int ExitCode = -1;
            public bool Exited;
        }

        private readonly object _bgGate = new object();
        private readonly Dictionary<string, BgShell> _bgShells = new Dictionary<string, BgShell>(StringComparer.Ordinal);
        private int _bgSeq;

        private void NoteRead(string full)
        {
            // 子代理读过的文件不能记进主任务的已读清单：主代理之后 edit 该文件时
            // WasRead 会凭子代理的旧读取直接放行，「先读后改」不变量被架空。
            if (_subagentDepth > 0) return;
            Session session = _app.ActiveSession;
            if (session == null || session.Task == null) return;
            lock (_app.StateLock)
            {
                if (session.Task.ReadFiles == null) session.Task.ReadFiles = new List<string>();
                for (int i = 0; i < session.Task.ReadFiles.Count; i++)
                {
                    if (string.Equals(session.Task.ReadFiles[i], full, StringComparison.OrdinalIgnoreCase)) return;
                }
                session.Task.ReadFiles.Add(full);
                if (session.Task.ReadFiles.Count > 200) session.Task.ReadFiles.RemoveAt(0);
            }
        }

        private bool WasRead(string full)
        {
            Session session = _app.ActiveSession;
            if (session == null || session.Task == null || session.Task.ReadFiles == null) return false;
            lock (_app.StateLock)
            {
                for (int i = 0; i < session.Task.ReadFiles.Count; i++)
                {
                    if (string.Equals(session.Task.ReadFiles[i], full, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        private string TaskIdOf()
        {
            Session session = _app.ActiveSession;
            return session == null || session.Task == null ? "manual" : session.Task.TaskId;
        }

        private async Task<ToolResult> WriteFileAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string path = Arg(args, "path", "");
            string content = Arg(args, "content", "");
            bool append = ArgBool(args, "append", false);
            string full = Fs.Resolve(_config.WorkDir, path);
            ToolResult result = CheckPathRisk(call, full, true);
            if (!result.Ok) return result;
            if (string.IsNullOrEmpty(path))
            {
                result.Ok = false;
                result.Error = "path 不能为空";
                return result;
            }
            if (content.Length > 4 * 1024 * 1024)
            {
                result.Ok = false;
                result.Error = "content 太大（" + content.Length + " 字符），请改用 shell 分段写入。";
                return result;
            }
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            bool existed = File.Exists(full);
            long oldSize = existed ? new FileInfo(full).Length : 0;
            string before = "";
            string beforeError = "";
            if (existed)
            {
                try
                {
                    Encoding oldEncoding;
                    before = FileOps.ReadTextAuto(full, out oldEncoding);
                }
                catch (Exception ex) { beforeError = ex.Message; }
            }
            string after = append && existed ? before + content : content;
            string backup = "";
            string backupError = "";
            // 覆盖已有文件前留快照：写错/写坏时能拷回去，不靠「记得先备份」
            if (existed && !append) backup = FileOps.Snapshot(full, TaskIdOf(), out backupError);
            call.BackupPath = backup;
            if (append) File.AppendAllText(full, content, new UTF8Encoding(false));
            else File.WriteAllText(full, content, new UTF8Encoding(false));
            if (beforeError.Length == 0) CaptureChangeDiff(call, path, before, after);
            else if (call != null) call.Diff = "（无法读取写入前内容：" + beforeError + "）";
            result.Output = (append ? "已追加" : "已写入") + " " + Fs.HumanSize(Encoding.UTF8.GetByteCount(content)) + " 到 " + full +
                            "\n原大小: " + Fs.HumanSize(oldSize) + "，新大小: " + Fs.HumanSize(new FileInfo(full).Length);
            if (!string.IsNullOrEmpty(backup)) result.Output += "\n原文快照: " + backup;
            else if (existed && !append) result.Output += "\n（快照失败：" + backupError + "，原内容未备份）";
            // append 是分段写入的中间态（写大文件），中间态语法不完整是正常的，不检查
            if (!append)
            {
                string writeSyntax = SyntaxCheckNote(full);
                if (writeSyntax.Length > 0) result.Output += "\n\n" + writeSyntax;
            }
            await Task.Yield();
            return result;
        }

        /// <summary>保留文件工具执行前后差异，供会话审阅 UI 展示；预览失败不应阻止实际写入。</summary>
        private static void CaptureChangeDiff(ToolCall call, string path, string before, string after)
        {
            if (call == null) return;
            try
            {
                string shown = FileOps.Diff(before, after, path, 120);
                call.Diff = shown.Length > 4000 ? shown.Substring(0, 4000) + "\n…（diff 过长，已截断）" : shown;
            }
            catch (Exception ex)
            {
                call.Diff = "（无法预览 diff：" + ex.Message + "）";
            }
        }

        /// <summary>
        /// 写入后的语法自检（写盘即自检，错误当轮回灌）：
        /// edit/multiedit/write_file 成功写盘后立即检查语法，错误直接附在工具输出里回喂——
        /// 模型当轮就能看到并自己修掉，不用等用户跑构建才发现。
        /// 只对有便宜检查器的类型生效；检查不到（类型无检查器 / 外部工具不存在 / 超时）保持安静不加噪音。
        /// 写入本身已成功：自检结果只作提示，不改变 Ok。
        /// </summary>
        private static string SyntaxCheckNote(string full)
        {
            try
            {
                if (string.IsNullOrEmpty(full) || !File.Exists(full)) return "";
                string ext = Path.GetExtension(full).ToLowerInvariant();
                if (new FileInfo(full).Length > 2 * 1024 * 1024) return "";
                if (ext == ".json")
                {
                    try
                    {
                        new System.Web.Script.Serialization.JavaScriptSerializer()
                            .DeserializeObject(File.ReadAllText(full, Encoding.UTF8));
                        return "【语法自检】JSON 解析通过。";
                    }
                    catch (Exception ex)
                    {
                        return "【语法自检未通过】JSON 解析失败：" + ShortCheckError(ex.Message) +
                               "\n请立即用 edit 修复语法错误再继续（或 /undo 回滚这次改动）。";
                    }
                }
                if (ext == ".js" || ext == ".mjs" || ext == ".cjs")
                {
                    string err = RunChecker("node", "--check \"" + full + "\"", 15000);
                    if (err == null) return "";
                    if (err.Length == 0) return "【语法自检】node --check 通过。";
                    return "【语法自检未通过】node --check 报错：\n" + ShortCheckError(err) +
                           "\n请立即用 edit 修复语法错误再继续（或 /undo 回滚这次改动）。";
                }
                if (ext == ".py")
                {
                    string err = RunChecker("python",
                        "-c \"import ast,sys; ast.parse(open(sys.argv[1],encoding='utf-8').read())\" \"" + full + "\"", 15000);
                    if (err == null) return "";
                    if (err.Length == 0) return "【语法自检】Python ast 解析通过。";
                    return "【语法自检未通过】Python 语法错误：\n" + ShortCheckError(err) +
                           "\n请立即用 edit 修复语法错误再继续（或 /undo 回滚这次改动）。";
                }
                if (ext == ".ps1")
                {
                    string script = "[void][scriptblock]::Create((Get-Content -Raw -LiteralPath '" + full.Replace("'", "''") + "'))";
                    string err = RunChecker("powershell.exe",
                        "-NoLogo -NoProfile -NonInteractive -Command " + script, 20000);
                    if (err == null) return "";
                    if (err.Length == 0) return "【语法自检】PowerShell 解析通过。";
                    return "【语法自检未通过】PowerShell 语法错误：\n" + ShortCheckError(err) +
                           "\n请立即用 edit 修复语法错误再继续（或 /undo 回滚这次改动）。";
                }
                if (ext == ".cs")
                {
                    string csc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
                    if (!File.Exists(csc))
                        csc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                            @"Microsoft.NET\Framework\v4.0.30319\csc.exe");
                    if (!File.Exists(csc)) return "";
                    AppPaths.EnsureDirectory(AppPaths.TempRoot);
                    string outDll = Path.Combine(AppPaths.TempRoot, "gt-syntax-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dll");
                    string err = RunChecker(csc, "/nologo /t:library /codepage:65001 /out:\"" + outDll + "\" \"" + full + "\"", 40000);
                    try { File.Delete(outDll); } catch { }
                    if (err == null) return "";
                    // 单文件编译缺引用/缺 partial 另一半必然有 CS0xxx 语义错误——只把 CS1xxx（语法类）当真，
                    // 完整编译请跑 build。
                    StringBuilder syntax = new StringBuilder();
                    string[] lines = err.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string ln = lines[i].Trim();
                        if (ln.Length == 0) continue;
                        if (ln.IndexOf("error CS1", StringComparison.Ordinal) >= 0)
                        {
                            if (syntax.Length > 0) syntax.Append('\n');
                            syntax.Append(ln);
                        }
                    }
                    if (syntax.Length == 0) return "【语法自检】csc 语法解析通过（缺引用的语义错误忽略，完整编译请跑 build）。";
                    return "【语法自检未通过】C# 语法错误：\n" + ShortCheckError(syntax.ToString()) +
                           "\n请立即用 edit 修复语法错误再继续（或 /undo 回滚这次改动）。";
                }
                return "";
            }
            catch (Exception ex)
            {
                Log.Warn("语法自检异常（不影响写入结果）: " + ex.Message);
                return "";
            }
        }

        /// <summary>跑一个外部语法检查器。返回 "" = 通过；null = 检查器不可用/超时（静默跳过）；其余 = 报错文本。</summary>
        private static string RunChecker(string exe, string arguments, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    // 检查器输出都很小（几行错误）：stderr 与 stdout 都是小缓冲，同步收齐再等退出
                    string errorText = p.StandardError.ReadToEnd();
                    string outputText = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    string text = (errorText + "\n" + outputText).Trim();
                    return p.ExitCode == 0 ? "" : text;
                }
            }
            catch
            {
                return null;   // 工具不存在（Win32Exception）等：视为本机没有这个检查器
            }
        }

        private static string ShortCheckError(string text)
        {
            if (text == null) text = "";
            text = text.Trim();
            if (text.Length > 800) text = text.Substring(0, 800) + "…（其余报错已省略）";
            return text;
        }

        private async Task<ToolResult> ListDirAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string path = Arg(args, "path", ".");
            string full = Fs.Resolve(_config.WorkDir, path);
            ToolResult result = CheckPathRisk(call, full, false);
            if (!result.Ok) return result;
            if (!Directory.Exists(full))
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.NotFound;
                result.Error = "目录不存在: " + full;
                return result;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("目录: " + full);
            DirectoryInfo dir = new DirectoryInfo(full);
            DirectoryInfo[] dirs = dir.GetDirectories();
            FileInfo[] files = dir.GetFiles();
            int count = 0;
            for (int i = 0; i < dirs.Length && count < 200; i++)
            {
                sb.AppendLine("[DIR ] " + dirs[i].Name + "\\");
                count++;
            }
            for (int i = 0; i < files.Length && count < 200; i++)
            {
                sb.AppendLine("[FILE] " + files[i].Name + "  " + Fs.HumanSize(files[i].Length) + "  " + files[i].LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                count++;
            }
            if (dirs.Length + files.Length > 200) sb.AppendLine("…（仅显示前 200 项）");
            result.Output = sb.ToString();
            await Task.Yield();
            return result;
        }

        private async Task<ToolResult> ScreenshotAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string mode = Arg(args, "mode", "screen").ToLowerInvariant();
            if (mode != "window") mode = "screen";
            string title = Arg(args, "title", "");
            int quality = ArgInt(args, "quality", 82);
            if (quality < 40) quality = 40;
            if (quality > 95) quality = 95;
            bool hideUi = ArgBool(args, "hide_ui", _config.HideOnScreenshot);

            IntPtr target = IntPtr.Zero;
            string targetTitle = "";
            Rectangle bounds;
            if (mode == "window")
            {
                if (string.IsNullOrEmpty(title))
                {
                    ToolResult missing = new ToolResult();
                    missing.Ok = false;
                    missing.Error = "mode=window 时必须提供 title（窗口标题的一部分）";
                    return missing;
                }
                string found = "";
                string process = "";
                target = Native.FindWindowByTitle(title, out found, out process);
                if (target == IntPtr.Zero)
                {
                    ToolResult notFound = new ToolResult();
                    notFound.Ok = false;
                    notFound.Error = "找不到窗口: " + title + "。可以先用 focus_window 或让用户确认窗口标题。";
                    return notFound;
                }
                targetTitle = found;
                if (Native.IsIconic(target)) Native.Restore(target);
                Native.SetForegroundWindow(target);
                await Task.Delay(360, token).ConfigureAwait(false);
                bounds = Native.WindowBounds(target);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    ToolResult empty = new ToolResult();
                    empty.Ok = false;
                    empty.Error = "无法读取窗口尺寸（可能已最小化）: " + found;
                    return empty;
                }
            }
            else
            {
                bounds = SystemInformation.VirtualScreen;
            }
            if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("无法读取屏幕尺寸");

            List<IntPtr> hidden = new List<IntPtr>();
            if (hideUi)
            {
                List<IntPtr> own = Native.FindVisibleWindowsByTitle(AppInfo.Name);
                for (int i = 0; i < own.Count; i++)
                {
                    if (own[i] == target) continue;
                    Native.Hide(own[i]);
                    hidden.Add(own[i]);
                }
                if (hidden.Count > 0) await Task.Delay(260, token).ConfigureAwait(false);
            }

            // 敏感输入接管（3.20.0）：绿虎浏览器窗口里正有密码/支付输入框聚焦时拒绝截图，
            // 防止用户正在输入的凭据经截图进入模型上下文。
            IntPtr sensitiveHwnd = BrowserForm.SensitiveCaptureTarget(target, mode != "window");
            if (sensitiveHwnd != IntPtr.Zero)
            {
                for (int i = 0; i < hidden.Count; i++) Native.ShowNoActivate(hidden[i]);
                ToolResult blocked = new ToolResult();
                blocked.Ok = false;
                blocked.Error = "检测到绿虎浏览器窗口里有密码/支付输入框处于焦点，为避免敏感信息进入上下文，这次截图已跳过。等用户离开该输入框后再试，或直接用文字向用户确认。";
                Log.Warn("截图被敏感输入守卫拦截");
                return blocked;
            }

            AppPaths.EnsureDirectory(AppPaths.ShotsDir);
            string path = Path.Combine(AppPaths.ShotsDir, "shot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".jpg");
            Rectangle src = Rectangle.Intersect(bounds, SystemInformation.VirtualScreen);
            if (src.Width <= 0 || src.Height <= 0) src = bounds;
            bool downsampled = false;
            try
            {
                ScreenCapture.Capture(bounds, path, quality);
                downsampled = ScreenCapture.Downsample(path, 1568, quality);   // 长边超 1568 等比降采样，省图像 token
            }
            catch (Exception capEx)
            {
                for (int i = 0; i < hidden.Count; i++) Native.ShowNoActivate(hidden[i]);
                ToolResult failed = new ToolResult();
                failed.Ok = false;
                string hint = capEx.Message;
                // CopyFromScreen 在锁屏 / 远程桌面断开 / 无交互式桌面会话时会抛 GDI+ 通用错误。
                if (capEx is System.Runtime.InteropServices.ExternalException ||
                    hint.IndexOf("GDI+", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    hint.IndexOf("通用", StringComparison.Ordinal) >= 0)
                {
                    failed.Error = "截图失败：当前会话可能已锁屏、远程桌面已断开，或没有交互式桌面（服务/会话 0 隔离）。请先解锁屏幕或在本地交互会话中重试。原始错误：" + hint;
                }
                else
                {
                    failed.Error = "截图失败：" + hint;
                }
                Log.Warn("截图失败: " + failed.Error);
                return failed;
            }
            finally
            {
                for (int i = 0; i < hidden.Count; i++) Native.ShowNoActivate(hidden[i]);
            }

            Point cursor = Native.CursorPosition();
            string active = Native.ForegroundTitle();
            ToolResult result = new ToolResult();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(mode == "window" ? "已截取窗口：" + targetTitle : "已截取全屏。");
            sb.AppendLine("图像: " + path + (downsampled ? "（长边超 1568 已降采样）" : ""));
            sb.AppendLine("区域: " + src.Width + "x" + src.Height + " @ (" + src.Left + "," + src.Top + ")");
            sb.AppendLine("当前鼠标: (" + cursor.X + "," + cursor.Y + ")");
            sb.AppendLine("当前前台窗口: " + active);
            if (hidden.Count > 0) sb.AppendLine("已临时隐藏 " + hidden.Count + " 个绿虎窗口，截图后已恢复。");
            result.Output = sb.ToString();
            Artifact artifact = _app.CreateArtifact(path, "image/jpeg", "image");
            result.Artifacts.Add(artifact);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            return result;
        }

        private async Task<ToolResult> MouseAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            AssertNotEmergencyStop();
            AssertNotSelfWindow();
            string action = Arg(args, "action", "click").ToLowerInvariant();
            int x = ArgInt(args, "x", Native.CursorPosition().X);
            int y = ArgInt(args, "y", Native.CursorPosition().Y);
            int count = ArgInt(args, "count", action == "double_click" ? 2 : 1);
            string button = Arg(args, "button", action == "right_click" ? "right" : "left");
            if (action == "scroll")
            {
                int amount = ArgInt(args, "amount", 3);
                Native.SetCursorPos(x, y);
                Thread.Sleep(80);
                Native.Scroll(amount);
            }
            else if (action == "move")
            {
                Native.SetCursorPos(x, y);
            }
            else
            {
                Native.Click(x, y, button, count);
            }
            await Task.Delay(150, token).ConfigureAwait(false);
            Point cursor = Native.CursorPosition();
            ToolResult result = new ToolResult();
            result.Output = "鼠标操作完成: " + action + " (" + x + "," + y + ") button=" + button + " count=" + count + "，当前坐标 (" + cursor.X + "," + cursor.Y + ")";
            return result;
        }

        private async Task<ToolResult> KeyboardAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            AssertNotEmergencyStop();
            string window = Arg(args, "window", "");
            if (!string.IsNullOrEmpty(window)) FocusWindow(window);
            AssertNotSelfWindow();
            string action = Arg(args, "action", "text").ToLowerInvariant();
            string text = Arg(args, "text", "");
            string keys = Arg(args, "keys", "");
            if (action == "keys" || !string.IsNullOrEmpty(keys))
            {
                SendKeysCombo(keys);
                await Task.Delay(120, token).ConfigureAwait(false);
                ToolResult result = new ToolResult();
                result.Output = "已发送按键: " + keys;
                return result;
            }
            if (string.IsNullOrEmpty(text)) throw new InvalidOperationException("text 或 keys 不能为空");
            string old = Ui.Invoke<string>(delegate
            {
                try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
                catch { return ""; }
            });
            Ui.Invoke(delegate { Clipboard.SetText(text); });
            await Task.Delay(80, token).ConfigureAwait(false);
            Native.Key(0x11, true);
            Native.Key(0x56, true);
            Native.Key(0x56, false);
            Native.Key(0x11, false);
            await Task.Delay(180, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(old))
            {
                Task restore = Task.Run(delegate
                {
                    Thread.Sleep(800);
                    try { Ui.Invoke(delegate { Clipboard.SetText(old); }); } catch { }
                });
                GC.KeepAlive(restore);
            }
            ToolResult typed = new ToolResult();
            typed.Output = "已通过剪贴板输入 " + text.Length + " 个字符。";
            return typed;
        }

        private static void FocusWindow(string title)
        {
            AssertNotEmergencyStop();
            string foundTitle = "";
            string process = "";
            IntPtr hwnd = Native.FindWindowByTitle(title, out foundTitle, out process);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("找不到窗口: " + title);
            if (string.Equals(process, OwnProcessName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能聚焦绿虎自身的窗口（防反馈回路：外部注入的内容不能借绿虎界面自我授权）。");
            Native.ShowWindow(hwnd, 9);
            Native.SetForegroundWindow(hwnd);
            Thread.Sleep(250);
        }

        private static void SendKeysCombo(string keys)
        {
            if (string.IsNullOrEmpty(keys)) throw new InvalidOperationException("keys 不能为空");
            string[] parts = keys.Split('+');
            List<byte> codes = new List<byte>();
            for (int i = 0; i < parts.Length; i++)
            {
                byte code = VirtualKey(parts[i].Trim());
                if (code == 0) throw new InvalidOperationException("无法识别按键: " + parts[i]);
                codes.Add(code);
            }
            for (int i = 0; i < codes.Count; i++) Native.Key(codes[i], true);
            Thread.Sleep(40);
            for (int i = codes.Count - 1; i >= 0; i--) Native.Key(codes[i], false);
        }

        private static byte VirtualKey(string name)
        {
            string n = name.ToUpperInvariant();
            if (n == "CTRL" || n == "CONTROL") return 0x11;
            if (n == "SHIFT") return 0x10;
            if (n == "ALT") return 0x12;
            if (n == "WIN" || n == "WINDOWS") return 0x5B;
            if (n == "ENTER" || n == "RETURN") return 0x0D;
            if (n == "TAB") return 0x09;
            if (n == "ESC" || n == "ESCAPE") return 0x1B;
            if (n == "SPACE") return 0x20;
            if (n == "BACKSPACE") return 0x08;
            if (n == "DELETE" || n == "DEL") return 0x2E;
            if (n == "INSERT") return 0x2D;
            if (n == "HOME") return 0x24;
            if (n == "END") return 0x23;
            if (n == "PAGEUP" || n == "PGUP") return 0x21;
            if (n == "PAGEDOWN" || n == "PGDN") return 0x22;
            if (n == "UP") return 0x26;
            if (n == "DOWN") return 0x28;
            if (n == "LEFT") return 0x25;
            if (n == "RIGHT") return 0x27;
            if (n.Length == 1)
            {
                char c = n[0];
                if (c >= 'A' && c <= 'Z') return (byte)c;
                if (c >= '0' && c <= '9') return (byte)c;
            }
            if (n.StartsWith("F"))
            {
                int f;
                if (int.TryParse(n.Substring(1), out f) && f >= 1 && f <= 12) return (byte)(0x70 + f - 1);
            }
            return 0;
        }

        private async Task<ToolResult> FocusWindowAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string title = Arg(args, "title", "");
            if (string.IsNullOrEmpty(title)) throw new InvalidOperationException("title 不能为空");
            string foundTitle = "";
            string process = "";
            IntPtr hwnd = Native.FindWindowByTitle(title, out foundTitle, out process);
            if (hwnd == IntPtr.Zero)
            {
                ToolResult fail = new ToolResult();
                fail.Ok = false;
                fail.Error = "找不到窗口: " + title;
                return fail;
            }
            Native.ShowWindow(hwnd, 9);
            Native.SetForegroundWindow(hwnd);
            await Task.Delay(200, token).ConfigureAwait(false);
            ToolResult result = new ToolResult();
            result.Output = "已聚焦窗口: " + foundTitle + " (进程 " + process + ")";
            return result;
        }

        private async Task<ToolResult> UploadFileAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string path = Arg(args, "path", "");
            string full = Fs.Resolve(_config.WorkDir, path);
            // 与读写类工具一致：upload_file 也会把文件包成可下载的 artifact，必须先过路径边界
            ToolResult result = CheckPathRisk(call, full, false);
            if (!result.Ok) return result;
            if (!File.Exists(full))
            {
                result.Ok = false;
                result.Error = "文件不存在: " + full;
                return result;
            }
            FileInfo info = new FileInfo(full);
            if (info.Length > 100L * 1024 * 1024)
            {
                result.Ok = false;
                result.Error = "文件超过 100MB";
                return result;
            }
            string mime = MimeOf(full);
            Artifact artifact = _app.CreateArtifact(full, mime, "file");
            result.Artifacts.Add(artifact);
            result.Output = "已准备上传文件: " + full + " (" + Fs.HumanSize(info.Length) + ")";
            await Task.Yield();
            return result;
        }

        public static string MimeOf(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".png") return "image/png";
            if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
            if (ext == ".gif") return "image/gif";
            if (ext == ".webp") return "image/webp";
            if (ext == ".pdf") return "application/pdf";
            if (ext == ".txt" || ext == ".md" || ext == ".log" || ext == ".csv") return "text/plain";
            if (ext == ".json") return "application/json";
            if (ext == ".html" || ext == ".htm") return "text/html";
            return "application/octet-stream";
        }

        /// <summary>
        /// web_fetch（WebFetch）：抓页面 → 去脚本/样式 → 转可读正文 → 截断。
        ///
        /// 和 http_get 的分工：http_get 是「原样取字节」（下载文件、取 JSON、看原始 HTML），
        /// web_fetch 是「读懂这一页」（模型直接读 markdown 正文，比裸 HTML 省数倍 token）。
        /// 15 分钟内同一 URL 复用结果：一轮任务里反复读同一页不会重复打网络。
        /// </summary>
        private async Task<ToolResult> WebFetchAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string url = Arg(args, "url", "").Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("只允许 http/https URL");
            int maxChars = ArgInt(args, "max_chars", 16000);
            if (maxChars < 500) maxChars = 500;
            if (maxChars > 60000) maxChars = 60000;

            ToolResult result = new ToolResult();
            string cached;
            if (WebFetchCache.TryGet(url, out cached))
            {
                result.Output = TextUtil.Truncate(cached, maxChars) + "\n\n（来自 15 分钟内的缓存）";
                return result;
            }

            Uri originalUri;
            Uri.TryCreate(url, UriKind.Absolute, out originalUri);
            Func<string, bool> hopGuard = delegate(string u)
            {
                Uri hop;
                if (!Uri.TryCreate(u, UriKind.Absolute, out hop)) return false;
                if (originalUri != null && string.Equals(hop.Host, originalUri.Host, StringComparison.OrdinalIgnoreCase)) return true;
                return !IsInternalHost(u);
            };
            byte[] bytes = await Task.Run(new Func<byte[]>(delegate { return HttpUtil.GetBytesGuarded(url, 30000, 3 * 1024 * 1024, token, hopGuard); }), token).ConfigureAwait(false);
            string raw = DecodeText(bytes);
            string text = WebFetchCache.ToReadable(raw);
            if (text.Trim().Length == 0)
            {
                result.Ok = false;
                result.Error = "页面没有可读正文（可能是纯脚本页/接口返回）。需要原始内容请用 http_get。";
                return result;
            }
            WebFetchCache.Put(url, text);
            result.Output = TextUtil.Truncate(text, maxChars);
            return result;
        }

        private async Task<ToolResult> HttpGetAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string url = Arg(args, "url", "");
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("只允许 http/https URL");
            int maxBytes = ArgInt(args, "max_bytes", 2 * 1024 * 1024);
            string saveTo = Arg(args, "save_to", "");
            // 逐跳 SSRF 防护：首跳已经过 Analyze + 审批（内网会标 danger），重定向换主机后
            // 不得转入内网——防「审批的是公网 URL、302 后实际打内网」的绕过。同主机跳转放行。
            Uri originalUri;
            Uri.TryCreate(url, UriKind.Absolute, out originalUri);
            Func<string, bool> hopGuard = delegate(string u)
            {
                Uri hop;
                if (!Uri.TryCreate(u, UriKind.Absolute, out hop)) return false;
                if (originalUri != null && string.Equals(hop.Host, originalUri.Host, StringComparison.OrdinalIgnoreCase)) return true;
                return !IsInternalHost(u);
            };
            byte[] bytes = await Task.Run(new Func<byte[]>(delegate { return HttpUtil.GetBytesGuarded(url, 30000, maxBytes, token, hopGuard); }), token).ConfigureAwait(false);
            ToolResult result = new ToolResult();
            if (!string.IsNullOrEmpty(saveTo))
            {
                string full = Fs.Resolve(_config.WorkDir, saveTo);
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(full, bytes);
                Artifact artifact = _app.CreateArtifact(full, MimeOf(full), "file");
                result.Artifacts.Add(artifact);
                result.Output = "已下载 " + Fs.HumanSize(bytes.Length) + " 到 " + full;
            }
            else
            {
                string text = DecodeText(bytes);
                result.Output = TextUtil.Truncate(text, 20000);
            }
            return result;
        }

        /// <summary>
        /// web_search：Bing 网页搜索（Web search 能力）。
        /// 抓取搜索结果页并解析标题/链接/摘要，返回纯文本给模型；失败时返回错误文本由模型处理。
        /// 需要网络；本机网络不可用时模型会收到明确的失败信息。
        /// </summary>
        private async Task<ToolResult> WebSearchAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            ToolResult result = new ToolResult();
            string query = Arg(args, "query", "");
            if (query.Length == 0)
            {
                result.Ok = false;
                result.ErrorCode = ToolErrorCode.BadArgs;
                result.Error = "缺少 query 参数";
                return result;
            }
            if (query.Length > 200) query = query.Substring(0, 200);
            int count = ArgInt(args, "count", 6);
            if (count < 1) count = 1;
            if (count > 10) count = 10;

            string url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query) + "&count=" + count;
            const string searchUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
            try
            {
                HttpResponse http = await Task.Run(delegate
                {
                    return HttpUtil.Get(url, new HttpOptions
                    {
                        TimeoutMs = 20000,
                        UserAgent = searchUa,
                        Accept = "text/html"
                    }.WithHeader("Accept-Language", "zh-CN,zh;q=0.9"));
                }, token).ConfigureAwait(false);

                string html = http.Body;
                if (!http.Ok)
                {
                    result.Ok = false;
                    result.Error = "网页搜索失败: " + (string.IsNullOrEmpty(http.Error) ? ("HTTP " + http.Status) : http.Error);
                    return result;
                }

                List<string> items = ParseBingResults(html, count);
                if (items.Count == 0)
                {
                    // 兜底：直接返回摘要文本片段
                    string text = TextUtil.StripAnsi(html);
                    int idx = text.IndexOf("There are no results", StringComparison.OrdinalIgnoreCase);
                    result.Output = "未找到相关搜索结果。" + (idx >= 0 ? "" : "（搜索页无有效条目）");
                    return result;
                }
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("搜索「" + query + "」的结果（" + items.Count + " 条）：");
                for (int i = 0; i < items.Count; i++) sb.AppendLine(items[i]);
                result.Output = sb.ToString();
                return result;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = "网页搜索异常: " + ex.Message;
                return result;
            }
        }

        private static List<string> ParseBingResults(string html, int max)
        {
            List<string> items = new List<string>();
            try
            {
                // 解析 <li class="b_algo"> 块：<h2><a href="...">title</a></h2> + <p>snippet</p>
                string marker = "b_algo";
                int pos = 0;
                int guard = 0;
                while (items.Count < max && guard++ < 80)
                {
                    int start = html.IndexOf(marker, pos, StringComparison.Ordinal);
                    if (start < 0) break;
                    int liStart = html.LastIndexOf("<li", start, StringComparison.Ordinal);
                    if (liStart < 0) break;
                    int liEnd = html.IndexOf("</li>", start, StringComparison.Ordinal);
                    if (liEnd < 0) break;
                    string block = html.Substring(liStart, liEnd - liStart);
                    pos = liEnd + 5;

                    string title = "";
                    int aHref = block.IndexOf("href=\"", StringComparison.Ordinal);
                    int h2 = block.IndexOf("<h2", StringComparison.Ordinal);
                    if (h2 >= 0)
                    {
                        int tStart = block.IndexOf(">", h2, StringComparison.Ordinal);
                        int tEnd = block.IndexOf("</h2>", h2, StringComparison.Ordinal);
                        if (tStart >= 0 && tEnd > tStart)
                        {
                            title = StripTags(block.Substring(tStart + 1, tEnd - tStart - 1));
                        }
                    }
                    string url = "";
                    if (aHref >= 0)
                    {
                        int uStart = aHref + 6;
                        int uEnd = block.IndexOf("\"", uStart, StringComparison.Ordinal);
                        if (uEnd > uStart) url = block.Substring(uStart, uEnd - uStart);
                    }
                    string snippet = "";
                    int pStart = block.IndexOf("<p", StringComparison.Ordinal);
                    if (pStart >= 0)
                    {
                        int ps = block.IndexOf(">", pStart, StringComparison.Ordinal);
                        int pe = block.IndexOf("</p>", pStart, StringComparison.Ordinal);
                        if (ps >= 0 && pe > ps) snippet = StripTags(block.Substring(ps + 1, pe - ps - 1));
                    }
                    if (url.Length > 0 && title.Length > 0)
                    {
                        items.Add((items.Count + 1) + ". " + TextUtil.OneLine(title, 120) + "\n   链接: " + url + (snippet.Length > 0 ? "\n   摘要: " + TextUtil.OneLine(snippet, 220) : ""));
                    }
                }
            }
            catch { }
            return items;
        }

        private static string StripTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            System.Text.RegularExpressions.Regex rx = new System.Text.RegularExpressions.Regex("<[^>]+>");
            string clean = rx.Replace(text, " ");
            clean = System.Net.WebUtility.HtmlDecode(clean);
            clean = System.Text.RegularExpressions.Regex.Replace(clean, "\\s+", " ").Trim();
            return clean;
        }

        private async Task<ToolResult> ClipboardAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            string action = Arg(args, "action", "get").ToLowerInvariant();
            ToolResult result = new ToolResult();            if (action == "set")
            {
                string text = Arg(args, "text", "");
                Ui.Invoke(delegate { Clipboard.SetText(text); });
                result.Output = "已写入剪贴板 " + text.Length + " 个字符。";
            }
            else
            {
                string text = Ui.Invoke<string>(delegate { try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { return ""; } });
                result.Output = "剪贴板内容（" + text.Length + " 字符）：\n" + TextUtil.Truncate(text, 10000);
            }
            await Task.Yield();
            return result;
        }

        private async Task<ToolResult> WaitAsync(ToolCall call, Dictionary<string, object> args, CancellationToken token)
        {
            int seconds = ArgInt(args, "seconds", 1);
            if (seconds < 0) seconds = 0;
            if (seconds > 30) seconds = 30;
            await Task.Delay(seconds * 1000, token).ConfigureAwait(false);
            ToolResult result = new ToolResult();
            result.Output = "已等待 " + seconds + " 秒。";
            return result;
        }

        private ToolResult Finish(ToolCall call, Dictionary<string, object> args)
        {
            ToolResult result = new ToolResult();
            result.Output = Arg(args, "summary", "任务完成。");
            return result;
        }

        /// <summary>计划工具（虚拟）：把计划步骤回显给模型，批准状态由审批流决定。/plan。</summary>
        private ToolResult PlanAsync(ToolCall call, Dictionary<string, object> args)
        {
            ToolResult result = new ToolResult();
            string title = Arg(args, "title", "计划");
            List<object> steps = Json.Array(args, "steps");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("计划标题：" + title);
            sb.AppendLine("计划步骤：");
            if (steps.Count == 0) sb.AppendLine("- （未列出步骤）");
            for (int i = 0; i < steps.Count; i++)
            {
                sb.AppendLine((i + 1) + ". " + steps[i]);
            }
            sb.AppendLine();
            sb.AppendLine(call.ApprovalStatus == "denied"
                ? "用户拒绝了这份计划，请根据反馈调整后重新提交 plan，或直接回答用户的问题。"
                : "计划已批准，请按上述步骤逐步执行；每完成一步可用 todo 工具更新进度。");
            result.Ok = true;
            result.Output = sb.ToString();
            return result;
        }

        /// <summary>进度工具（虚拟）：记录计划各步状态。todoWrite/todoUpdate。</summary>

        private ToolResult TodoAsync(ToolCall call, Dictionary<string, object> args)
        {            ToolResult result = new ToolResult();
            List<object> items = Json.Array(args, "items");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("进度清单：");
            if (items.Count == 0) sb.AppendLine("- （空）");
            for (int i = 0; i < items.Count; i++)
            {
                Dictionary<string, object> item = items[i] as Dictionary<string, object>;
                if (item == null) { sb.AppendLine("- " + items[i]); continue; }
                string title = Arg(item, "title", Arg(item, "name", ""));
                string status = Arg(item, "status", "pending");
                string mark = status == "done" ? "[x]" : (status == "doing" ? "[→]" : "[ ]");
                sb.AppendLine(mark + " " + title);
            }
            sb.AppendLine();
            sb.AppendLine("已记录进度。继续执行下一步。");
            result.Ok = true;
            result.Output = sb.ToString();
            return result;
        }
    }
}
