using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace GreenTiger
{
    internal static class SelfRepairEvidence
    {
        // 命令全串匹配：日志里的同名文本、带管道或分号的另一条命令不能充当验证。
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private const string Candidate = @"dist/GreenTiger\.candidate\.exe";
        private static readonly Regex BuildCandidate = new Regex(
            @"\A(?:(?:powershell(?:\.exe)? +(?:-NoProfile +)?(?:-ExecutionPolicy +Bypass +)?-File +(?:\./)?|(?:& +)?\./)build\.ps1) +-Out +(?:\./)?(" + Candidate + @")\z", Options);
        private static readonly Regex TestCandidate = new Regex(
            @"\A(?:& +)?(?:\./)?(" + Candidate + @") +--self-test\z", Options);
        private static readonly Regex UiCandidate = new Regex(
            @"\Anode(?:\.exe)? +(?:\./)?tools/ui-e2e\.js +--candidate\z", Options);

        private static string Normalize(string command)
        {
            return (command ?? "").Trim().Replace('\\', '/');
        }

        // PowerShell -File 默认吞掉子进程的非零退出码；GUI 版 exe 通过管道等待进程完成。
        internal static string VerificationCommand(string command)
        {
            string normalized = Normalize(command);
            bool selfTest = TestCandidate.IsMatch(normalized);
            if (!selfTest && !BuildCandidate.IsMatch(normalized) && !UiCandidate.IsMatch(normalized)) return command;
            return "$ErrorActionPreference = 'Stop'\n$LASTEXITCODE = 0\n" + command +
                (selfTest ? " | Out-Host" : "") +
                "\n$gtVerificationOk = $?\nif ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\nif (-not $gtVerificationOk) { exit 1 }";
        }

        private static string ProjectSource(string path, string workDir)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            if (!string.IsNullOrEmpty(workDir))
            {
                try
                {
                    string root = Path.GetFullPath(workDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    string full = Path.GetFullPath(Path.Combine(root, path));
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return "";
                    path = full.Substring(root.Length);
                }
                catch (ArgumentException) { return ""; }
                catch (NotSupportedException) { return ""; }
                catch (IOException) { return ""; }
            }
            path = Normalize(path);
            if (path.StartsWith("./", StringComparison.Ordinal)) path = path.Substring(2);
            if (path.IndexOf(':') >= 0 || path.StartsWith("/", StringComparison.Ordinal) ||
                Array.IndexOf(path.Split('/'), "..") >= 0) return "";
            return Regex.IsMatch(path, @"\A(?:src/[^/]+\.cs|ui/(?:.+/)?[^/]+\.(?:js|css|html|webmanifest)|tools/[^/]+\.js|build\.ps1|package\.json|app\.manifest)\z", Options) ? path : "";
        }

        internal static void Check(List<ToolCall> calls, VerificationReport report, string workDir = null)
        {
            string changed = "";
            bool built = false, selfTested = false, uiTested = false, uiChanged = false;
            if (calls != null) foreach (ToolCall call in calls)
            {
                if (call == null) continue;
                Dictionary<string, object> args = Json.ParseObject(call.ArgsJson);
                bool success = call.Status == "done" && call.ExitCode == 0;
                if (call.Name == "write_file" || call.Name == "edit" || call.Name == "multiedit")
                {
                    if (!success || !(call.Diff ?? "").StartsWith("--- a/", StringComparison.Ordinal)) continue;
                    string path = ProjectSource(Json.Str(args, "path", ""), workDir);
                    if (path == "") continue;
                    changed = path;
                    uiChanged |= path.StartsWith("ui/", StringComparison.OrdinalIgnoreCase);
                    built = selfTested = uiTested = false;
                    continue;
                }
                if (changed == "" || call.Name != "shell" || Json.Bool(args, "background", false)) continue;
                string cwd = Json.Str(args, "cwd", "");
                if (cwd != "" && cwd != "." && cwd != @".\" && cwd != "./" &&
                    (string.IsNullOrEmpty(workDir) || !string.Equals(workDir.TrimEnd('\\', '/'), cwd.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))) continue;
                string command = Normalize(Json.Str(args, "command", ""));
                if (command != Normalize(call.Command)) continue;
                if (BuildCandidate.IsMatch(command))
                {
                    built = success;
                    selfTested = uiTested = false;
                }
                else if (TestCandidate.IsMatch(command)) selfTested = built && success;
                else if (UiCandidate.IsMatch(command)) uiTested = built && success;
            }
            bool passed = changed != "" && built && selfTested && (!uiChanged || uiTested);
            string detail = changed == "" ? "缺少对绿虎项目源码的实际修改记录。请用 edit、multiedit 或 write_file 写入修复。"
                : !built ? @"最后一次源码变更后缺少成功的候选构建。请在项目根目录运行 powershell -ExecutionPolicy Bypass -File build.ps1 -Out dist\GreenTiger.candidate.exe。"
                : !selfTested ? @"请在项目根目录成功运行 .\dist\GreenTiger.candidate.exe --self-test，验证刚构建的候选版本。"
                : uiChanged && !uiTested ? "界面源码已变更。请在项目根目录运行 node tools/ui-e2e.js --candidate，验证候选程序的实际界面。"
                : "已修改 " + changed + "，之后构建候选并通过自检" + (uiChanged ? "和候选界面端到端验证。" : "。");
            report.Checks.Add(new VerificationCheck { Id = "self_repair", Kind = "source_repair", Required = true,
                Target = changed, Detail = detail, Passed = passed });
            report.RequiredTotal++;
            if (!passed) report.RequiredFailed++;
            report.Passed = report.Passed && passed;
            report.Unverifiable = false;
            report.Summary = (report.Passed ? "验收通过" : "验收未通过") + "（" +
                (report.RequiredTotal - report.RequiredFailed) + "/" + report.RequiredTotal + "）：" + detail;
        }

        internal static void SelfTest(Action<string, bool, string> check)
        {
            ToolCall edit = new ToolCall { Name = "edit", ArgsJson = "{\"path\":\"src/Agent.cs\"}", Status = "done",
                Diff = "--- a/src/Agent.cs\n+++ b/src/Agent.cs\n@@ -1,1 +1,1 @@\n-broken\n+fixed" };
            Func<string, ToolCall> shell = delegate(string command)
            {
                return new ToolCall { Name = "shell", Status = "done", Command = command,
                    ArgsJson = Json.Stringify(new Dictionary<string, object> { { "command", command } }) };
            };
            Func<ToolCall[], bool> passes = delegate(ToolCall[] entries)
            {
                VerificationReport report = new VerificationReport();
                Check(new List<ToolCall>(entries), report);
                return report.Passed;
            };
            ToolCall npm = shell("npm test");
            ToolCall build = shell(@"powershell -ExecutionPolicy Bypass -File build.ps1 -Out dist\GreenTiger.candidate.exe");
            ToolCall candidate = shell(@".\dist\GreenTiger.candidate.exe --self-test");
            check("self_repair_evidence", !passes(new[] { edit, npm }) && passes(new[] { edit, build, candidate }),
                "实际写入后构建候选并通过候选自检");
            check("self_repair_order", !passes(new[] { npm, edit }) && !passes(new[] { edit, npm, edit }) &&
                !passes(new[] { edit, candidate }), "写入后需重新验证，旧候选不能作为证据");
            ToolCall bad = shell(candidate.Command); bad.ExitCode = 1;
            ToolCall background = shell(candidate.Command);
            background.ArgsJson = Json.Stringify(new Dictionary<string, object> { { "command", candidate.Command }, { "background", true } });
            check("self_repair_false_evidence", !passes(new[] { edit, build, shell("echo '" + candidate.Command + "'") }) &&
                !passes(new[] { edit, build, shell(candidate.Command + "; exit 0") }) && !passes(new[] { edit, build, shell("git status") }) &&
                !passes(new[] { edit, build, background }) && !passes(new[] { edit, build, candidate, bad }) && !passes(new[] { build, candidate }),
                "忽略打印命令、复合命令、后台启动、非零退出及无源码写入");
            ToolCall ui = new ToolCall { Name = "edit", Status = "done", ArgsJson = "{\"path\":\"ui/js/app.js\"}", Diff = "--- a/ui/js/app.js" };
            ToolCall render = shell("node tools/ui-e2e.js --candidate");
            check("self_repair_candidate_ui", !passes(new[] { ui, build, candidate }) &&
                !passes(new[] { ui, build, candidate, shell("node tools/ui-e2e.js") }) &&
                !passes(new[] { ui, build, candidate, render, edit, build, candidate }) &&
                passes(new[] { ui, edit, build, candidate, render }), "UI 源码需最后一次写入后在同一构建运行浏览器端到端验证");
        }
    }
}
