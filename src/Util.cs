using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace GreenTiger
{
    public static class Fs
    {
        public static string Resolve(string workDir, string path)
        {
            if (string.IsNullOrEmpty(path)) return workDir;
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(string.IsNullOrEmpty(workDir) ? Environment.CurrentDirectory : workDir, path));
        }

        public static bool IsInside(string root, string path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return false;
            try
            {
                string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "file";
            char[] bad = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool ok = true;
                for (int i = 0; i < bad.Length; i++) { if (bad[i] == c) { ok = false; break; } }
                sb.Append(ok ? c : '_');
            }
            string result = sb.ToString().Trim();
            if (result.Length == 0) result = "file";
            if (result.Length > 120) result = result.Substring(0, 120);
            return result;
        }

        public static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
        }
    }

    public static class TextUtil
    {
        private static readonly Regex Ansi = new Regex("\u001b\\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

        public static string StripAnsi(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return Ansi.Replace(text, "");
        }

        public static string Truncate(string text, int max)
        {
            if (text == null) return "";
            if (text.Length <= max) return text;
            // SafeTruncate：截断点落在代理对中间会产出非法 UTF-16，进 JSON 请求会被严格端点拒收
            return SafeTruncate(text, Math.Max(0, max)) + "\n…（已截断，共 " + text.Length + " 字符）";
        }

        public static string OneLine(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            if (s.Length > max) s = SafeTruncate(s, max) + "…";
            return s;
        }

        /// <summary>按 BOM 猜编码解码字节数组，认不出就用 UTF-8 严格模式，再不行退 GBK。</summary>
        public static string Decode(byte[] bytes, int count)
        {
            if (bytes == null) return "";
            int n = count <= 0 ? bytes.Length : Math.Min(count, bytes.Length);
            if (n >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, n - 3);
            if (n >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, n - 2);
            if (n >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, n - 2);
            try { return new UTF8Encoding(false, true).GetString(bytes, 0, n); }
            catch
            {
                try { return Encoding.GetEncoding(936).GetString(bytes, 0, n); }
                catch { return Encoding.UTF8.GetString(bytes, 0, n); }
            }
        }

        /// <summary>
        /// 代理对安全截断：截断点落在 UTF-16 代理对（emoji/生僻字 U+2xxxx）中间时回退一个 char。
        /// 所有 Substring(0, n) 截断点都该走这里——孤立代理项经 JavaScriptSerializer 序列化成
        /// 非法 \ud83d 转义，严格端点直接拒收整个请求。
        /// </summary>
        public static string SafeTruncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return text == null ? "" : text;
            if (max <= 0) return "";
            if (text.Length <= max) return text;
            int cut = max;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1]) && cut < text.Length && char.IsLowSurrogate(text[cut]))
            {
                cut--;   // 截断点把代理对劈开了：回退一个 char 保住合法 UTF-16
            }
            return text.Substring(0, cut);
        }

        /// <summary>同上，但额外回传实际采用的编码 —— 改完原文再写回时要沿用同一编码。</summary>
        public static string Decode(byte[] bytes, out Encoding encoding)
        {
            encoding = new UTF8Encoding(false);
            if (bytes == null) return "";
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = new UTF8Encoding(true);
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = Encoding.Unicode;
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = Encoding.BigEndianUnicode;
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch
            {
                try { encoding = Encoding.GetEncoding(936); return encoding.GetString(bytes); }
                catch
                {
                    encoding = new UTF8Encoding(false);
                    return Encoding.UTF8.GetString(bytes);
                }
            }
        }

        /// <summary>把一段用户输入压成单行标题，去掉换行和多余空白。</summary>
        public static string CleanTitle(string text, int max)
        {
            if (text == null) return "";
            string s = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            if (max > 0 && s.Length > max) s = s.Substring(0, max).TrimEnd() + "…";
            return s;
        }

        private static readonly string[] TitleFillers = new string[]
        {
            "请帮我", "请你帮", "请你", "可不可以", "能不能", "可以帮我", "帮我",
            "请问", "你好，", "你好,", "你好", "麻烦你", "麻烦", "我希望", "希望你",
            "我想", "我要", "请", "！", "!"
        };

        /// <summary>
        /// 从首条用户消息自动生成会话标题（对齐 Claude Code：短、像名词短语，不带省略号）。
        /// 只取第一句/第一行，剥掉礼貌前缀与句尾标点，截到 18 字。
        /// </summary>
        public static string AutoTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string s = text.Trim();
            // 先剥礼貌前缀与 bang 命令前缀（含 ! 终端命令）
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < TitleFillers.Length; i++)
                {
                    string f = TitleFillers[i];
                    if (s.StartsWith(f, StringComparison.Ordinal))
                    {
                        s = s.Substring(f.Length).TrimStart(' ', '，', ',', '：', ':');
                        changed = true;
                        break;
                    }
                }
            }
            // 再取第一句/第一行
            int scan = Math.Min(s.Length, 120);
            int cut = scan;
            for (int i = 0; i < scan; i++)
            {
                char c = s[i];
                if (c == '\n' || c == '。' || c == '！' || c == '？' || c == '!') { cut = i; break; }
            }
            s = s.Substring(0, cut).Replace("\r", " ").Replace("\n", " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            s = s.TrimEnd('，', ',', '。', '.', '！', '!', '？', '?', '：', ':', '、', ' ');
            if (s.Length == 0) return "";
            const int max = 18;
            if (s.Length > max) s = s.Substring(0, max).TrimEnd();
            return s;
        }
    }

    /// <summary>
    /// 通配匹配。
    ///
    /// 之前有两份同名实现：Agent 里是 <c>GlobMatch(text, pattern)</c>，FileOps 里是
    /// <c>GlobMatch(pattern, relPath)</c> —— 名字一样、参数顺序相反、星号语义也不同，
    /// 照着签名抄就抄错。现在按用途分成两个名字不同、语义写在名字里的方法。
    /// </summary>
    public static class Globbing
    {
        /// <summary>
        /// 许可清单通配（工具白名单）。<c>*</c> 匹配任意字符序列（含路径分隔符与冒号），
        /// <c>?</c> 匹配单个字符，大小写不敏感。
        /// </summary>
        public static bool Permit(string text, string pattern)
        {
            string hay = (text ?? "").ToLowerInvariant();
            string pat = (pattern ?? "").ToLowerInvariant();
            int t = 0, p = 0, star = -1, match = 0;
            while (t < hay.Length)
            {
                if (p < pat.Length && (pat[p] == '?' || pat[p] == hay[t])) { t++; p++; }
                else if (p < pat.Length && pat[p] == '*') { star = p++; match = t; }
                else if (star >= 0) { p = star + 1; t = ++match; }
                else return false;
            }
            while (p < pat.Length && pat[p] == '*') p++;
            return p == pat.Length;
        }

        /// <summary>
        /// 文件路径通配（glob 工具）。<c>**</c> 跨目录、<c>*</c> 不跨目录、<c>[abc]</c> 字符类，
        /// 大小写不敏感；不含斜杠的模式额外用「裸文件名」再试一次，所以 <c>*.md</c> 能命中 <c>docs/x/y.md</c>。
        /// </summary>
        public static bool Path(string pattern, string relPath)
        {
            string rel = (relPath ?? "").Replace('\\', '/');
            Regex re;
            try { re = new Regex(PathPatternToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch (ArgumentException) { return false; }
            if (re.IsMatch(rel)) return true;
            int slash = rel.LastIndexOf('/');
            string name = slash < 0 ? rel : rel.Substring(slash + 1);
            return re.IsMatch(name);
        }

        /// <summary>把 glob 翻译成正则：路径分隔符统一按 / 处理，并在缺 斜杠 的模式前补 **/。</summary>
        public static string PathPatternToRegex(string pattern)
        {
            string p = (pattern ?? "").Replace('\\', '/');
            if (p.Length == 0) p = "**/*";
            if (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            if (p.IndexOf('/') < 0) p = "**/" + p;

            StringBuilder sb = new StringBuilder("^");
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '*')
                {
                    bool dbl = i + 1 < p.Length && p[i + 1] == '*';
                    if (dbl)
                    {
                        bool slashAfter = i + 2 < p.Length && p[i + 2] == '/';
                        if (slashAfter) { sb.Append("(?:.*/)?"); i += 2; }
                        else { sb.Append(".*"); i += 1; }
                    }
                    else sb.Append("[^/]*");
                }
                else if (c == '?') sb.Append("[^/]");
                else if (c == '[')
                {
                    int end = p.IndexOf(']', i + 1);
                    if (end > i) { sb.Append(p.Substring(i, end - i + 1)); i = end; }
                    else sb.Append("\\[");
                }
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append("$");
            return sb.ToString();
        }
    }

    /// <summary>读取 .git/HEAD 推当前分支名（不启动 git 进程，无仓库返回空串）。</summary>
    public static class GitUtil
    {
        // 缓存是三个字段的组合状态，多线程同时刷新会读到「新目录 + 旧分支」这类撕裂结果；
        // 用一把小锁把「读 + 写」当成一个原子步骤。
        private static readonly object _cacheGate = new object();
        private static string _cacheDir = "";
        private static string _cacheBranch = "";
        private static DateTime _cacheAt = DateTime.MinValue;

        public static string CurrentBranch(string workDir)
        {
            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir)) return "";
            try
            {
                lock (_cacheGate)
                {
                    if (_cacheDir == workDir && (DateTime.Now - _cacheAt).TotalSeconds < 2) return _cacheBranch;
                    string branch = ReadBranch(workDir);
                    _cacheDir = workDir;
                    _cacheBranch = branch;
                    _cacheAt = DateTime.Now;
                    return branch;
                }
            }
            catch { return ""; }
        }

        private static string ReadBranch(string workDir)
        {
            string gitPath = Path.Combine(workDir, ".git");
            string head;
            if (File.Exists(gitPath))
            {
                // worktree / 子模块：.git 是一行 "gitdir: 路径"
                string firstLine = "";
                using (StreamReader r = new StreamReader(gitPath)) firstLine = r.ReadLine() ?? "";
                if (!firstLine.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return "";
                string gd = firstLine.Substring(7).Trim();
                if (gd.StartsWith(".")) gd = Path.GetFullPath(Path.Combine(workDir, gd));
                head = Path.Combine(gd, "HEAD");
            }
            else
            {
                head = Path.Combine(gitPath, "HEAD");
            }
            if (!File.Exists(head)) return "";
            string content = File.ReadAllText(head).Trim();
            if (content.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
            {
                string branch = content.Substring("ref: refs/heads/".Length).Trim();
                // 分支名里不会有空格
                int sp = branch.IndexOfAny(new char[] { ' ', '\t', '\r', '\n' });
                if (sp >= 0) branch = branch.Substring(0, sp);
                return branch;
            }
            // detached HEAD：取短 hash
            string hex = content.Trim();
            if (hex.Length >= 7 && IsHex(hex)) return "(" + hex.Substring(0, 7) + ")";
            return "";
        }

        private static bool IsHex(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }
    }

    public static class RandomUtil
    {
        private static readonly Random Rnd = new Random();
        private static readonly object Gate = new object();


        public static int Next(int max)
        {
            lock (Gate) { return Rnd.Next(max); }
        }
    }

    /// <summary>WinForms UI 线程调度器，用于文件夹选择框、剪贴板等 STA 操作。</summary>
    public static class Ui
    {
        private static Control _invoker;

        public static void Attach()
        {
            _invoker = new Control();
            _invoker.CreateControl();
        }

        public static T Invoke<T>(Func<T> func)
        {
            if (_invoker == null) return func();
            if (_invoker.InvokeRequired) return (T)_invoker.Invoke(func);
            return func();
        }

        public static void Invoke(Action action)
        {
            if (_invoker == null) { action(); return; }
            if (_invoker.InvokeRequired) _invoker.Invoke(action);
            else action();
        }
    }

    /// <summary>窗口激活工具。绿虎不再启动任何外接浏览器。</summary>
    public static class Browsers
    {
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>按窗口标题激活已运行的绿虎浏览器（第二实例唤醒）。</summary>
        public static bool ActivateWindow(string titlePart)
        {
            try
            {
                if (string.IsNullOrEmpty(titlePart)) return false;
                IntPtr hWnd = FindWindow(null, titlePart);
                if (hWnd == IntPtr.Zero)
                {
                    // 标题会随网页变化：枚举所有顶层窗口做模糊匹配。
                    EnumWindows(delegate(IntPtr h, IntPtr p)
                    {
                        if (!IsWindowVisible(h)) return true;
                        int len = GetWindowTextLength(h);
                        if (len <= 0) return true;
                        StringBuilder sb = new StringBuilder(len + 2);
                        GetWindowText(h, sb, sb.Capacity);
                        if (sb.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hWnd = h;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);
                }
                if (hWnd == IntPtr.Zero) return false;
                ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
                return true;
            }
            catch { return false; }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    }

    public static class TimeUtil
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>当前 Unix 毫秒。全项目的消息时间戳、会话时间戳、调度时间戳都从这里出。</summary>
        public static long UnixMs()
        {
            return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;
        }

        /// <summary>当前 Unix 秒（OpenAI 兼容端点的 created 字段要的是秒）。</summary>
        public static long UnixSeconds()
        {
            return (long)(DateTime.UtcNow - Epoch).TotalSeconds;
        }

        /// <summary>毫秒时间戳 → 本地时间。非正值（缺失 / 未初始化）返回 DateTime.MinValue。</summary>
        public static DateTime FromUnix(long ms)
        {
            if (ms <= 0) return DateTime.MinValue;
            try { return Epoch.AddMilliseconds(ms).ToLocalTime(); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>毫秒时间戳 → 本地时间字符串；非正值返回空串，方便调用方直接拼进文本。</summary>
        public static string FromUnix(long ms, string format)
        {
            DateTime t = FromUnix(ms);
            return t == DateTime.MinValue ? "" : t.ToString(format, CultureInfo.InvariantCulture);
        }

        /// <summary>本地 DateTime → 毫秒时间戳。</summary>
        public static long ToUnix(DateTime local)
        {
            if (local <= DateTime.MinValue) return 0;
            try { return (long)(local.ToUniversalTime() - Epoch).TotalMilliseconds; }
            catch { return 0; }
        }

        public static string Duration(long ms)
        {
            if (ms < 1000) return ms + "ms";
            if (ms < 60000) return (ms / 1000.0).ToString("0.0") + "s";
            return (ms / 60000).ToString("0") + "m" + ((ms % 60000) / 1000).ToString("0") + "s";
        }

        /// <summary>毫秒时间戳转「刚刚 / N 分钟前 / N 小时前 / 日期」，用于 glob 结果与列表。</summary>
        public static string Ago(long ms)
        {
            if (ms <= 0) return "未知";
            long diff = Message.Now() - ms;
            if (diff < 0) diff = 0;
            if (diff < 60 * 1000) return "刚刚";
            if (diff < 3600 * 1000) return (diff / 60000) + " 分钟前";
            if (diff < 24 * 3600 * 1000L) return (diff / 3600000) + " 小时前";
            if (diff < 30L * 24 * 3600 * 1000) return (diff / 86400000) + " 天前";
            string ago = FromUnix(ms, "yyyy-MM-dd");
            return ago.Length > 0 ? ago : "未知";
        }
    }

    public static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        /// <summary>Esc（VK_ESCAPE=0x1B）当前是否被按住。键鼠工具执行前的全局急停检查。</summary>
        public static bool EscapeHeld()
        {
            return (GetAsyncKeyState(0x1B) & 0x8000) != 0;
        }

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        public static string ForegroundTitle()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            int len = GetWindowTextLength(h);
            if (len <= 0) return "";
            StringBuilder sb = new StringBuilder(len + 2);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static IntPtr FindWindowByTitle(string needle, out string foundTitle, out string processName)
        {
            IntPtr result = IntPtr.Zero;
            string bestTitle = "";
            string bestProcess = "";
            if (string.IsNullOrEmpty(needle)) needle = "";
            string lower = needle.ToLowerInvariant();
            int bestScore = -1;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;
                StringBuilder sb = new StringBuilder(len + 2);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (string.IsNullOrEmpty(title)) return true;
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                string pname = "";
                try { pname = Process.GetProcessById((int)pid).ProcessName; }
                catch { }
                int score = -1;
                if (lower.Length == 0) score = 0;
                else if (title.ToLowerInvariant().Contains(lower)) score = 100 - Math.Min(50, title.Length / 10);
                else if (pname.ToLowerInvariant().Contains(lower)) score = 50;
                if (score > bestScore)
                {
                    bestScore = score;
                    result = hWnd;
                    bestTitle = title;
                    bestProcess = pname;
                }
                return true;
            }, IntPtr.Zero);
            foundTitle = bestTitle;
            processName = bestProcess;
            if (bestScore < 0) return IntPtr.Zero;
            return result;
        }

        public static Point CursorPosition()
        {
            POINT p;
            GetCursorPos(out p);
            return new Point(p.X, p.Y);
        }

        /// <summary>按标题片段收集所有可见的顶层窗口。</summary>
        public static List<IntPtr> FindVisibleWindowsByTitle(string part)
        {
            List<IntPtr> result = new List<IntPtr>();
            if (string.IsNullOrEmpty(part)) return result;
            string lower = part.ToLowerInvariant();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;
                StringBuilder sb = new StringBuilder(len + 2);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString().ToLowerInvariant().Contains(lower)) result.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>读取窗口客户区在屏幕上的矩形，失败时返回空矩形。</summary>
        public static Rectangle WindowBounds(IntPtr hWnd)
        {
            RECT rect;
            if (!GetWindowRect(hWnd, out rect)) return Rectangle.Empty;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return Rectangle.Empty;
            return new Rectangle(rect.Left, rect.Top, width, height);
        }

        public static void Hide(IntPtr hWnd)
        {
            try { ShowWindow(hWnd, 0); } catch { }
        }

        public static void ShowNoActivate(IntPtr hWnd)
        {
            try { ShowWindow(hWnd, 4); } catch { }
        }

        public static void Restore(IntPtr hWnd)
        {
            try { ShowWindow(hWnd, 9); } catch { }
        }

        public static void Click(int x, int y, string button, int count)
        {
            SetCursorPos(x, y);
            Thread.Sleep(40);
            uint down = MOUSEEVENTF_LEFTDOWN;
            uint up = MOUSEEVENTF_LEFTUP;
            if (button == "right") { down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; }
            else if (button == "middle") { down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; }
            for (int i = 0; i < Math.Max(1, count); i++)
            {
                mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(35);
                mouse_event(up, 0, 0, 0, UIntPtr.Zero);
                if (i + 1 < Math.Max(1, count)) Thread.Sleep(70);
            }
        }

        public static void Scroll(int amount)
        {
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(amount * 120)), UIntPtr.Zero);
        }

        public static void Key(byte vk, bool down)
        {
            keybd_event(vk, 0, down ? 0 : KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }

    public static class OsInfo
    {
        private static string _psVersion;
        private static readonly object PsGate = new object();

        public static string Describe()
        {
            string os = Environment.OSVersion.VersionString;
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string screen = "";
            try
            {
                Rectangle r = SystemInformation.VirtualScreen;
                screen = r.Width + "x" + r.Height + " @ (" + r.X + "," + r.Y + ")";
            }
            catch { }
            return "Windows " + os + "; 用户 " + user + "; 屏幕 " + screen + "; .NET " + Environment.Version;
        }

        public static string PowerShellVersion()
        {
            lock (PsGate)
            {
                if (_psVersion != null) return _psVersion;
            }
            string version = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -Command \"$PSVersionTable.PSVersion.ToString()\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    version = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(3000);
                }
            }
            catch { version = ""; }
            lock (PsGate) { _psVersion = version; }
            return version;
        }
    }
}
