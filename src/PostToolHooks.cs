using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GreenTiger
{
    /// <summary>
    /// 内置确定性质检（3.22.0，PostToolUse 模式）：文件写入类工具成功落盘后，
    /// 按扩展名跑零配置的语法检查，失败信息就地追加进工具结果回喂给模型——
    /// 把「改完的代码还能不能解析」从提示词层的自觉（反死循环纪律）升级为
    /// 确定性的机器检查：模型在下一轮就看到精确报错。
    ///
    /// 与 v3.6 蒸馏轮的 <see cref="Hooks"/>（config.Hooks 用户自配 pre/post 命令）是两层互补：
    /// · 本类 = 内置默认层，零配置自动生效，只做只读检查（不执行用户/仓库提供的命令体）；
    /// · Hooks = 用户配置层，pre 可阻断、post 可通知，命令体来自用户自己的配置。
    ///
    /// 安全边界：本类刻意不读任何项目级/仓库级配置（.greentiger/hooks.json 之类）——
    /// 让仓库内容定义在本机执行的命令等于给不可信仓库一个静默执行面，是注入向量。
    /// .cs 不做逐次编译（太贵，编译验证交给项目构建链）；html/css 无可靠检查器，均跳过。
    /// </summary>
    public static class PostToolHooks
    {
        private const int TimeoutMs = 20000;
        private const int MaxNoteChars = 1200;

        /// <summary>
        /// 对一次成功的文件写入执行匹配的质检。返回要追加进工具输出的说明，
        /// 空串 = 不适用或检查通过。子代理只读面不会走到这里（无写入类工具）。
        /// </summary>
        public static string Run(string toolName, Dictionary<string, object> args, string workDir)
        {
            if (string.IsNullOrEmpty(toolName) || args == null) return "";
            string n = toolName.ToLowerInvariant();
            if (n != "write_file" && n != "edit" && n != "multiedit") return "";
            string path = ArgStr(args, "path");
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                if (!Path.IsPathRooted(path)) path = Path.Combine(string.IsNullOrEmpty(workDir) ? "." : workDir, path);
                path = Path.GetFullPath(path);
                if (!File.Exists(path)) return "";
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".js" || ext == ".mjs" || ext == ".cjs")
                    return RunProcess("node", "--check \"" + path + "\"", path, "node --check");
                if (ext == ".json")
                    return RunProcess("node",
                        "-e \"JSON.parse(require('fs').readFileSync(process.argv[1],'utf8'));\" \"" + path + "\"",
                        path, "JSON.parse");
                if (ext == ".py")
                    return RunProcess("python",
                        "-c \"import ast,sys;ast.parse(open(sys.argv[1],encoding='utf-8').read())\" \"" + path + "\"",
                        path, "ast.parse");
                return "";
            }
            catch { return ""; }
        }

        /// <summary>工具执行后的统一入口：检查通过原样返回，未通过把说明追加进结果输出。</summary>
        public static ToolResult Attach(ToolResult result, ToolCall call, Dictionary<string, object> args, string workDir)
        {
            if (result == null || !result.Ok || call == null) return result;
            string note = Run(call.Name, args, workDir);
            if (note.Length == 0) return result;
            result.Output = (result.Output ?? "") + "\n\n" + note;
            return result;
        }

        private static string ArgStr(Dictionary<string, object> args, string key)
        {
            object v;
            if (!args.TryGetValue(key, out v) || v == null) return "";
            return Convert.ToString(v);
        }

        private static string RunProcess(string exe, string arguments, string path, string label)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return "";
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(TimeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "[hooks] " + label + "（" + Path.GetFileName(path) + "）检查超时（" + (TimeoutMs / 1000) +
                            "s）。这是机器检查超时不是文件错误，请人工或用 shell 复核该文件。";
                    }
                    if (p.ExitCode == 0) return "";
                    string detail = ((stderr ?? "") + "\n" + (stdout ?? "")).Trim();
                    if (detail.Length > MaxNoteChars) detail = detail.Substring(0, MaxNoteChars) + "…（截断）";
                    return "[hooks] " + label + " 对 " + Path.GetFileName(path) + " 检查未通过（exit " + p.ExitCode + "）：\n" +
                        detail + "\n以上是确定性的机器检查结果，不是建议——请立即修复该文件后继续任务。";
                }
            }
            catch (Exception ex)
            {
                // 检查器不在 PATH（如 python）≠ 文件有问题：静默跳过，不阻塞任务
                if (ex is System.ComponentModel.Win32Exception) return "";
                try { Log.Warn("质检钩子启动失败（跳过）: " + ex.Message); } catch { }
                return "";
            }
        }
    }
}
