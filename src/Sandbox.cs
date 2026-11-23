using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace GreenTiger
{
    /// <summary>
    /// Windows 低完整性沙箱（评估报告第三阶段的 Windows 原生方案）。
    ///
    /// 结构：ToolExecutor 用普通 Process.Start 启动本 exe 的引导进程（--sandbox-exec），
    /// 引导进程把自己的 token 完整性降到 Low（S-1-16-4096，降级不需要特权），
    /// 再启动 powershell 跑命令脚本。输出经继承句柄直达外层管道——外层的超时、
    /// kill 树、编码解码全部零改动复用。
    ///
    /// 边界（如实）：低完整性进程不能写任何中完整性位置（用户常规文件、注册表 HKCU、
    /// 其它进程对象），只能写低完整性目录（沙箱目录）。因此「误删/误写本机」收敛到沙箱目录内。
    /// 它【不防】数据外发（网络不受完整性限制）与同用户密钥读取（DPAPI 不区分完整性）。
    /// 黑名单命令、键鼠、越界路径仍按原审批规则走，沙箱不是万能豁免。
    /// </summary>
    public static class Sandbox
    {
        public const string LowIntegritySid = "S-1-16-4096";

        // ---------------- P/Invoke（完整性级别） ----------------

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
            ref TOKEN_MANDATORY_LABEL tokenInformation, uint tokenInformationLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_MANDATORY_LABEL
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        private const int TokenIntegrityLevelClass = 25;   // TOKEN_INFORMATION_CLASS.TokenIntegrityLevel
        private const uint TokenQuery = 0x0008;
        private const uint TokenAdjustDefault = 0x0080;
        private const uint GroupIntegrity = 0x00000020;     // SE_GROUP_INTEGRITY

        /// <summary>把当前进程 token 的完整性级别降到 Low。降级是单向门，永远允许、不需要特权。</summary>
        public static bool LowerOwnIntegrity(out string error)
        {
            error = "";
            try
            {
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenAdjustDefault, out token))
                {
                    error = "OpenProcessToken 失败，Win32Error=" + Marshal.GetLastWin32Error();
                    return false;
                }
                try
                {
                    IntPtr sid;
                    if (!ConvertStringSidToSid(LowIntegritySid, out sid))
                    {
                        error = "ConvertStringSidToSid 失败，Win32Error=" + Marshal.GetLastWin32Error();
                        return false;
                    }
                    try
                    {
                        TOKEN_MANDATORY_LABEL label = new TOKEN_MANDATORY_LABEL();
                        label.Sid = sid;
                        label.Attributes = GroupIntegrity;
                        if (!SetTokenInformation(token, TokenIntegrityLevelClass, ref label,
                            (uint)Marshal.SizeOf(typeof(TOKEN_MANDATORY_LABEL))))
                        {
                            error = "SetTokenInformation 失败，Win32Error=" + Marshal.GetLastWin32Error();
                            return false;
                        }
                        return true;
                    }
                    finally { LocalFree(sid); }
                }
                finally { CloseHandle(token); }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ---------------- 引导模式 ----------------

        /// <summary>
        /// --sandbox-exec 入口：降完整性 → 启动 powershell 跑脚本 → 转发退出码。
        /// 不重定向子进程输出：powershell 继承本进程的标准句柄（外层 ToolExecutor 建立的管道），
        /// 字节原样回流，编码解码仍由外层统一处理。
        /// </summary>
        public static int RunSandboxed(string scriptPath, string workDir)
        {
            string err;
            if (!LowerOwnIntegrity(out err)) return 90;
            try
            {
                if (Directory.Exists(workDir)) Environment.CurrentDirectory = workDir;
            }
            catch { }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                    "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \"" + scriptPath + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                // 不赌「孙进程继承祖管道句柄」的平台细节：显式读子进程输出，泵到本进程的
                // stdout/stderr（那是外层 ToolExecutor 建立的管道，继承来的已打开句柄，
                // 写入不做访问检查）。字节原样转发，编码仍由外层统一解码。
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Stream consoleOut = Console.OpenStandardOutput())
                using (Stream consoleErr = Console.OpenStandardError())
                using (Process p = Process.Start(psi))
                {
                    Task fwdOut = Task.Run(delegate { Pump(p.StandardOutput.BaseStream, consoleOut); });
                    Task fwdErr = Task.Run(delegate { Pump(p.StandardError.BaseStream, consoleErr); });
                    p.WaitForExit();
                    try { Task.WaitAll(new Task[] { fwdOut, fwdErr }, 3000); }
                    catch { }
                    return p.ExitCode;
                }
            }
            catch
            {
                return 91;
            }
        }

        private static void Pump(Stream src, Stream dst)
        {
            try
            {
                byte[] buf = new byte[8192];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
            }
            catch { }
        }

        /// <summary>外层启动引导进程用的 ProcessStartInfo（重定向/编码由调用方继续设置）。</summary>
        public static ProcessStartInfo LauncherPsi(string scriptPath, string workDir)
        {
            string exe = Assembly.GetEntryAssembly().Location;
            ProcessStartInfo psi = new ProcessStartInfo(exe,
                "--sandbox-exec=\"" + scriptPath + "\" --sandbox-cwd=\"" + workDir + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = workDir;
            return psi;
        }

        // ---------------- 沙箱目录 ----------------

        /// <summary>沙箱工作目录：数据目录 sandbox\ws，完整性标签 Low，目录可整体删除重置。</summary>
        public static string SandboxDir()
        {
            string root = AppPaths.Root;
            if (string.IsNullOrEmpty(root)) root = Path.Combine(Path.GetTempPath(), "GreenTiger");
            return Path.Combine(root, "sandbox", "ws");
        }

        /// <summary>准备沙箱目录（幂等）：建目录 + icacls 设低完整性 + 标记文件。普通用户可做，无需管理员。</summary>
        public static bool EnsureSandboxDir(out string error)
        {
            error = "";
            try
            {
                string dir = SandboxDir();
                string marker = Path.Combine(dir, ".gt-sandbox-low");
                if (File.Exists(marker)) return true;
                Directory.CreateDirectory(dir);
                // (OI)(CI)：继承给目录内全部子项；L = Low。低完整性进程因此能写沙箱目录。
                ProcessStartInfo psi = new ProcessStartInfo("icacls.exe", "\"" + dir + "\" /setintegritylevel (OI)(CI)L");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    if (p.ExitCode != 0)
                    {
                        error = "icacls 设置低完整性失败（exit " + p.ExitCode + "）：" +
                            (e.Trim().Length > 0 ? e.Trim() : o.Trim());
                        return false;
                    }
                }
                File.WriteAllText(marker, DateTime.Now.ToString("o"));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool IsInSandbox(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try { return Fs.IsInside(SandboxDir(), Path.GetFullPath(dir)); }
            catch { return false; }
        }

        /// <summary>该次 shell 调用是否应走沙箱执行：沙箱开着，且 cwd 解析后落在沙箱目录内。</summary>
        public static bool ShouldRunSandboxed(Config cfg, string cwd)
        {
            if (cfg == null || !cfg.SandboxMode) return false;
            if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) return false;
            return IsInSandbox(cwd);
        }

        /// <summary>沙箱模式下 cwd 越出沙箱 = fail-closed 拒绝执行（绝不静默回退中完整性）。</summary>
        public static bool BlockOutsideCwd(Config cfg, string cwd)
        {
            if (cfg == null || !cfg.SandboxMode) return false;
            return !IsInSandbox(cwd);
        }

        /// <summary>
        /// 审批豁免判定（Agent.NeedsApproval 用）：仅「沙箱目录内的、非 danger 的 shell」免审批。
        /// 键鼠 / 网络 / 写模式子代理 / 黑名单命令 / 越界 cwd 一律不豁免。
        /// </summary>
        public static bool CoversCall(Config cfg, ToolCall call)
        {
            if (cfg == null || call == null || !cfg.SandboxMode) return false;
            if (!string.Equals(call.Name, "shell", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(call.Risk, "danger", StringComparison.OrdinalIgnoreCase)) return false;
            Dictionary<string, object> a = Json.ParseObject(call.ArgsJson);
            string cwdArg = a == null ? "" : Json.Str(a, "cwd", "");
            string cwd = string.IsNullOrEmpty(cwdArg) ? cfg.WorkDir : Fs.Resolve(cfg.WorkDir, cwdArg);
            if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) cwd = cfg.WorkDir;
            return IsInSandbox(cwd);
        }
    }
}
