using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GreenTiger
{
    /// <summary>一条 hook 配置（config.json 的 Hooks 数组项）。</summary>
    public class HookRule
    {
        public string Match { get; set; }      /// <summary>工具名正则（对 shell|write_file 生效；空 = 匹配全部）。</summary>
        public string Phase { get; set; }      /// <summary>pre / post / both。</summary>
        public string Command { get; set; }    /// <summary>PowerShell 命令。</summary>
        public int Timeout { get; set; }       /// <summary>秒，默认 30。</summary>

        public HookRule()
        {
            Match = "";
            Phase = "pre";
            Command = "";
            Timeout = 30;
        }
    }

    /// <summary>一次 hook 执行的结果。</summary>
    public class HookOutcome
    {
        public bool Blocked { get; set; }
        public string Reason { get; set; }
        public string Output { get; set; }

        public HookOutcome()
        {
            Blocked = false;
            Reason = "";
            Output = "";
        }
    }

    /// <summary>
    /// Hooks（对齐 Claude Code 的 tool-use hooks，v3.6 蒸馏注入）：
    /// 工具执行前/后运行用户定义的 PowerShell 命令，环境变量携带调用上下文——
    ///   GT_TOOL（工具名）、GT_ARGS（参数 JSON）、GT_PHASE（pre/post）、GT_TASK（任务 id）。
    /// pre-hook 退出码 2 = 阻断该工具调用，stderr 作为原因回灌给模型；
    /// 其它非零退出码记警告不阻断（hook 本身的 bug 不能瘫痪 agent）。
    /// post-hook 只通知不阻断。匹配按 .NET 正则对工具名执行，空串匹配全部。
    /// </summary>
    public static class Hooks
    {
        public static List<HookRule> Normalize(IList<HookRule> hooks)
        {
            List<HookRule> result = new List<HookRule>();
            if (hooks == null) return result;
            for (int i = 0; i < hooks.Count; i++)
            {
                HookRule h = hooks[i];
                if (h == null || string.IsNullOrWhiteSpace(h.Command)) continue;
                HookRule n = new HookRule();
                n.Match = (h.Match ?? "").Trim();
                n.Phase = (h.Phase ?? "pre").Trim().ToLowerInvariant();
                if (n.Phase != "pre" && n.Phase != "post" && n.Phase != "both") n.Phase = "pre";
                n.Command = h.Command.Trim();
                n.Timeout = h.Timeout > 0 && h.Timeout <= 600 ? h.Timeout : 30;
                result.Add(n);
            }
            return result;
        }

        private static bool Matches(HookRule hook, string toolName)
        {
            if (string.IsNullOrEmpty(hook.Match)) return true;
            try { return System.Text.RegularExpressions.Regex.IsMatch(toolName ?? "", hook.Match, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
            catch { return false; }   // 非法正则的 hook 静默跳过，不瘫痪调用
        }

        /// <summary>PowerShell 单引号字面量：内部单引号翻倍转义，换行/反斜杠原样保留。</summary>
        private static string PsLiteral(string value)
        {
            string v = value == null ? "" : value;
            return "'" + v.Replace("'", "''") + "'";
        }

        /// <summary>执行某个阶段的所有匹配 hook。phase = "pre" 或 "post"。</summary>
        public static async Task<HookOutcome> RunAsync(IList<HookRule> hooks, string phase, ToolCall call,
            string argsJson, string taskId, string workDir, CancellationToken token)
        {
            HookOutcome outcome = new HookOutcome();
            if (hooks == null || hooks.Count == 0 || call == null || string.IsNullOrEmpty(call.Name)) return outcome;
            for (int i = 0; i < hooks.Count; i++)
            {
                HookRule hook = hooks[i];
                if (hook.Phase != phase && hook.Phase != "both") continue;
                if (!Matches(hook, call.Name)) continue;

                string scriptPath = Path.Combine(Path.GetTempPath(), "GreenTiger",
                    "hook-" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".ps1");
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("$ErrorActionPreference = 'Continue'");
                    sb.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
                    // 上下文变量写在脚本里，而不是塞进 ProcessStartInfo.EnvironmentVariables：
                    // Windows 环境块本身可能已存在大小写重复的键（HTTPS_PROXY / https_proxy 同时存在很常见），
                    // 一旦访问那个字典就会抛 ArgumentException，导致 hook 整段静默失效（本机实测踩到）。
                    sb.AppendLine("$env:GT_TOOL = " + PsLiteral(call.Name));
                    sb.AppendLine("$env:GT_ARGS = " + PsLiteral(argsJson));
                    sb.AppendLine("$env:GT_PHASE = " + PsLiteral(phase));
                    sb.AppendLine("$env:GT_TASK = " + PsLiteral(taskId));
                    sb.AppendLine(hook.Command);
                    Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                    File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(true));

                    ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                        "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    psi.StandardOutputEncoding = Encoding.UTF8;
                    psi.StandardErrorEncoding = Encoding.UTF8;
                    psi.WorkingDirectory = string.IsNullOrEmpty(workDir) ? Environment.CurrentDirectory : workDir;

                    int exitCode = -1;
                    string stdout = "", stderr = "";
                    using (Process process = new Process())
                    {
                        process.StartInfo = psi;
                        process.Start();
                        var outTask = process.StandardOutput.ReadToEndAsync();
                        var errTask = process.StandardError.ReadToEndAsync();
                        bool exited = await Task.Run(new Func<bool>(delegate { return process.WaitForExit(hook.Timeout * 1000); })).ConfigureAwait(false);
                        if (!exited)
                        {
                            try { process.Kill(); } catch { }
                            outcome.Output += "[hook 超时（" + hook.Timeout + "s）] ";
                            exitCode = 124;
                        }
                        else
                        {
                            exitCode = process.ExitCode;
                        }
                        stdout = await outTask.ConfigureAwait(false);
                        stderr = await errTask.ConfigureAwait(false);
                    }
                    string piece = "[hook " + phase + ":" + (i + 1) + "] exit=" + exitCode +
                        (stdout.Trim().Length > 0 ? " | " + TextUtil.OneLine(stdout.Trim(), 400) : "");
                    outcome.Output = (outcome.Output.Length > 0 ? outcome.Output + "\n" : "") + piece;

                    if (phase == "pre" && exitCode == 2)
                    {
                        // Claude Code 语义：exit 2 = 阻断，stderr 给模型当原因
                        outcome.Blocked = true;
                        outcome.Reason = "被 pre-hook 阻断（" + (i + 1) + "）：" +
                            (stderr.Trim().Length > 0 ? TextUtil.OneLine(stderr.Trim(), 500) : "hook 未给出原因");
                        return outcome;
                    }
                    if (exitCode != 0 && exitCode != 124)
                    {
                        outcome.Output += " | 警告：hook 非零退出（不阻断）" +
                            (stderr.Trim().Length > 0 ? "：" + TextUtil.OneLine(stderr.Trim(), 200) : "");
                    }
                }
                catch (Exception ex)
                {
                    outcome.Output = (outcome.Output.Length > 0 ? outcome.Output + "\n" : "") +
                        "[hook " + phase + ":" + (i + 1) + " 执行失败（不阻断）] " + ex.Message;
                }
                finally
                {
                    try { File.Delete(scriptPath); } catch { }
                }
            }
            return outcome;
        }
    }
}
