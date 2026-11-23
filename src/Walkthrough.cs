using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GreenTiger
{
    /// <summary>
    /// 任务走查记录（对齐 Antigravity 的 Artifacts：给人类审阅的产物，而不是让人类读工具调用日志）。
    ///
    /// 每个终态任务在 工作目录\.greentiger\walkthroughs\ 生成一份 Markdown：
    /// 目标 / 结论 / **证据强度** / 执行时间线 / 改动与备份 / 未完成项。
    /// 三条纪律：
    /// · 证据强度先说：验收通过 = verified，只有模型自述 = unverified，验收没过 = failed。
    ///   这是 Fable 5.2「长跑时自己盯着结果能不能复现」在绿虎里的可落地版本——不假装验证过。
    /// · 时间线只写参数摘要与结果首行，不复制工具全文（记录是给人看的，不是第二份日志）。
    /// · 零模型调用、纯本地生成；工作目录只读时静默跳过。
    /// </summary>
    public static class Walkthrough
    {
        public const int MaxFiles = 60;
        public const int MaxAnswerChars = 900;

        private static readonly Regex NamePattern =
            new Regex("^[0-9]{8}-[0-9]{6}-[a-z0-9_]+\\.md$", RegexOptions.IgnoreCase);

        public static string Dir(string workDir)
        {
            if (string.IsNullOrEmpty(workDir)) return "";
            return Path.Combine(workDir, ".greentiger", "walkthroughs");
        }

        /// <summary>任务结束时生成一份走查记录，返回文件路径（失败返回 ""）。</summary>
        public static string Write(string workDir, TaskRecord task, IList<ToolCall> calls, string answer)
        {
            try
            {
                if (string.IsNullOrEmpty(workDir) || task == null) return "";
                string dir = Dir(workDir);
                Directory.CreateDirectory(dir);
                string name = Stamp(task.EndedAt > 0 ? task.EndedAt : Message.Now())
                    + "-" + Tail(task.TaskId, 6) + ".md";
                string file = Path.Combine(dir, name);
                File.WriteAllText(file, Compose(task, calls, answer), new UTF8Encoding(false));
                Prune(dir);
                return file;
            }
            catch (Exception ex)
            {
                try { Log.Warn("走查记录写入失败（忽略）: " + ex.Message); } catch { }
                return "";
            }
        }

        internal static string Compose(TaskRecord task, IList<ToolCall> calls, string answer)
        {
            List<ToolCall> list = new List<ToolCall>();
            if (calls != null) for (int i = 0; i < calls.Count; i++) if (calls[i] != null) list.Add(calls[i]);

            int failed = 0, awaiting = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Status == "error" || list[i].Status == "blocked" || list[i].Status == "denied") failed++;
                if (list[i].Status == "awaiting") awaiting++;
            }
            long tokens = task.PromptTokens + task.CompletionTokens + task.TokensEstimated;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 任务走查 · " + Trim(TextUtil.OneLine(task.UserText ?? "", 70), 70));
            sb.AppendLine();
            sb.AppendLine("- 生成时间：" + Local(task.EndedAt > 0 ? task.EndedAt : Message.Now(), "yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("- 结果：" + TaskOutcome.Describe(task.Outcome)
                + "｜耗时 " + Seconds(task.DurationMs) + "｜" + task.Steps + " 步"
                + "｜工具 " + list.Count + " 次" + (failed > 0 ? "（失败 " + failed + "）" : "")
                + (awaiting > 0 ? "（待确认 " + awaiting + "）" : ""));
            if (tokens > 0) sb.AppendLine("- 成本：≈" + tokens + " token（实测与估算合并）");
            sb.AppendLine();

            string strength = Strength(task);
            sb.AppendLine("## 证据强度：" + StrengthLabel(strength));
            sb.AppendLine();
            string verification = task.Verification ?? "";
            sb.AppendLine(string.IsNullOrWhiteSpace(verification) ? "（没有验收记录）" : verification);
            sb.AppendLine();
            sb.AppendLine("> " + StrengthHint(strength));
            sb.AppendLine();

            sb.AppendLine("## 结论");
            sb.AppendLine();
            string text = (answer ?? "").Trim();
            if (text.Length == 0) sb.AppendLine("（本次没有最终回复文本）");
            else
            {
                if (text.Length > MaxAnswerChars) text = text.Substring(0, MaxAnswerChars) + "\n\n…（结论已截断，全文见会话）";
                sb.AppendLine(text);
            }
            sb.AppendLine();

            sb.AppendLine("## 执行时间线");
            sb.AppendLine();
            if (list.Count == 0) sb.AppendLine("- （没有工具调用）");
            for (int i = 0; i < list.Count; i++)
            {
                ToolCall c = list[i];
                sb.Append("- ").Append(Local(c.StartedAt > 0 ? c.StartedAt : 0, "HH:mm:ss"))
                  .Append(" · `").Append(c.Name ?? "?").Append("` ").Append(StatusMark(c.Status));
                string args = ArgSummary(c);
                if (args.Length > 0) sb.Append(" · ").Append(args);
                string head = FirstLine(c.Output);
                if (head.Length > 0) sb.Append(" → ").Append(head);
                sb.AppendLine();
            }
            sb.AppendLine();

            List<string> changed = new List<string>();
            List<string> backups = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                ToolCall c = list[i];
                if (c.Name != "write_file" && c.Name != "edit" && c.Name != "multiedit") continue;
                if (c.Status != "done") continue;
                string path = Arg(c, "path");
                if (path.Length == 0) path = Arg(c, "file_path");
                if (path.Length == 0) continue;
                if (!changed.Contains(path)) changed.Add(path);
                if (!string.IsNullOrEmpty(c.BackupPath) && !backups.Contains(c.BackupPath)) backups.Add(c.BackupPath);
            }
            if (changed.Count > 0)
            {
                sb.AppendLine("## 改动");
                sb.AppendLine();
                for (int i = 0; i < changed.Count; i++) sb.AppendLine("- " + changed[i]);
                if (backups.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("改动前快照（可在界面上「回滚」或手工恢复）：");
                    for (int i = 0; i < backups.Count; i++) sb.AppendLine("- " + backups[i]);
                }
                sb.AppendLine();
            }

            List<string> risks = new List<string>();
            if (task.BudgetExceeded) risks.Add("预算耗尽提前收口（" + (task.ErrorDetail ?? "") + "）");
            if (task.Interrupted) risks.Add("任务曾被中断，见续跑记录");
            for (int i = 0; i < list.Count; i++)
            {
                ToolCall c = list[i];
                if (c.Status != "error" && c.Status != "blocked" && c.Status != "denied") continue;
                string reason = FirstLine(c.Output);
                risks.Add("`" + (c.Name ?? "?") + "` " + StatusMark(c.Status) + (reason.Length > 0 ? "：" + reason : ""));
                if (risks.Count >= 6) break;
            }
            if (risks.Count > 0)
            {
                sb.AppendLine("## 未完成与风险");
                sb.AppendLine();
                for (int i = 0; i < risks.Count; i++) sb.AppendLine("- " + risks[i]);
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine("本文件由绿虎在任务结束时自动生成，不调用模型；可直接编辑或删除。");
            return sb.ToString();
        }

        /// <summary>verified / unverified / failed / incomplete —— 只按客观记录判定，不参考模型措辞。</summary>
        internal static string Strength(TaskRecord task)
        {
            if (task == null) return "incomplete";
            string v = task.Verification ?? "";
            if (v.StartsWith("验收通过", StringComparison.Ordinal)) return "verified";
            if (v.StartsWith("验收未通过", StringComparison.Ordinal) || v.StartsWith("验证器异常", StringComparison.Ordinal))
                return "failed";
            if (task.Outcome != TaskOutcome.Succeeded) return "incomplete";
            return "unverified";
        }

        private static string StrengthLabel(string strength)
        {
            if (strength == "verified") return "已客观验证";
            if (strength == "failed") return "验收未通过";
            if (strength == "incomplete") return "未完成";
            return "仅模型自述（未客观验证）";
        }

        private static string StrengthHint(string strength)
        {
            if (strength == "verified") return "验收条件由独立验证器按文件/命令证据判定，可以按下面的时间线复核。";
            if (strength == "failed") return "有必需验收条件未满足：先看「未完成与风险」，不要按完成态交付。";
            if (strength == "incomplete") return "任务没有跑到成功终态，下面的结论只代表已完成的片段。";
            return "本次没有可客观核验的证据：结论来自模型自述，重要操作请按时间线自行复查。";
        }

        public static List<string> ListRecent(string workDir, int max)
        {
            List<string> names = new List<string>();
            string dir = Dir(workDir);
            if (dir.Length == 0 || !Directory.Exists(dir)) return names;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.md"); } catch { return names; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            Array.Reverse(files);
            for (int i = 0; i < files.Length && i < Math.Max(1, max); i++) names.Add(Path.GetFileName(files[i]));
            return names;
        }

        public static string Read(string workDir, string name, out string error)
        {
            error = "";
            string file = Path.Combine(Dir(workDir), name ?? "");
            if (!NamePattern.IsMatch(name ?? "")) { error = "文件名不合法"; return ""; }
            try
            {
                if (!File.Exists(file)) { error = "没有这条记录：" + name; return ""; }
                return File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception ex) { error = ex.Message; return ""; }
        }

        /// <summary>斜杠命令：/walkthrough 列表，/walkthrough 2 读第 2 条。</summary>
        public static string Command(string workDir, string input)
        {
            if (string.IsNullOrEmpty(workDir)) return "请先选择一个工作目录。";
            string text = (input ?? "").Trim();
            string rest = text.Length > 12 ? text.Substring(12).Trim() : "";
            List<string> names = ListRecent(workDir, MaxFiles);
            if (names.Count == 0)
                return "还没有走查记录：跑完一个任务后会自动生成在 .greentiger\\walkthroughs\\。";
            if (rest.Length == 0)
            {
                StringBuilder sb = new StringBuilder("## 最近的走查记录（" + names.Count + " 条，最新在前）\n");
                int shown = Math.Min(names.Count, 10);
                for (int i = 0; i < shown; i++)
                {
                    string title = Title(Path.Combine(Dir(workDir), names[i]));
                    sb.AppendLine((i + 1) + ". " + names[i] + (title.Length > 0 ? " · " + title : ""));
                }
                sb.AppendLine();
                sb.Append("用 `/walkthrough 序号` 看全文；文件在 " + Dir(workDir) + "。");
                return sb.ToString();
            }
            int index;
            if (int.TryParse(rest, out index))
            {
                if (index < 1 || index > names.Count) return "序号超出范围（1-" + names.Count + "）。";
                string error;
                string body = Read(workDir, names[index - 1], out error);
                return error.Length > 0 ? error : body;
            }
            string direct;
            string directError;
            direct = Read(workDir, rest, out directError);
            return directError.Length > 0 ? directError : direct;
        }

        private static string Title(string file)
        {
            try
            {
                using (StreamReader reader = new StreamReader(file, Encoding.UTF8))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        string line = reader.ReadLine();
                        if (line == null) break;
                        if (line.StartsWith("# ", StringComparison.Ordinal)) return line.Substring(2).Trim();
                    }
                }
            }
            catch { }
            return "";
        }

        private static void Prune(string dir)
        {
            try
            {
                string[] files = Directory.GetFiles(dir, "*.md");
                if (files.Length <= MaxFiles) return;
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                int remove = files.Length - MaxFiles;
                for (int i = 0; i < remove; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }

        private static string Arg(ToolCall call, string key)
        {
            if (call == null) return "";
            Dictionary<string, object> obj = Json.ParseObject(call.ArgsJson);
            if (obj == null) return "";
            return (Json.Str(obj, key, "") ?? "").Trim();
        }

        private static string ArgSummary(ToolCall call)
        {
            if (call == null) return "";
            string[] keys = new string[] { "command", "path", "file_path", "url", "pattern", "query", "tasks", "prompt" };
            for (int i = 0; i < keys.Length; i++)
            {
                string v = Arg(call, keys[i]);
                if (v.Length > 0) return Trim(TextUtil.OneLine(v, 90), 90);
            }
            if (!string.IsNullOrEmpty(call.ArgsJson)) return Trim(TextUtil.OneLine(call.ArgsJson, 70), 70);
            return "";
        }

        private static string StatusMark(string status)
        {
            if (status == "done") return "✓";
            if (status == "error") return "✗";
            if (status == "blocked" || status == "denied") return "⛔";
            if (status == "awaiting") return "⏸";
            if (status == "running") return "…";
            return "·";
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string one = TextUtil.OneLine(text, 110);
            return Trim(one, 110);
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length > max) s = s.Substring(0, max) + "…";
            return s;
        }

        private static string Tail(string text, int count)
        {
            string s = Regex.Replace(text ?? "", "[^a-zA-Z0-9_]", "");
            if (s.Length == 0) return "task";
            return s.Length <= count ? s : s.Substring(s.Length - count);
        }

        private static string Stamp(long ms)
        {
            return Local(ms, "yyyyMMdd-HHmmss");
        }

        private static string Local(long ms, string format)
        {
            if (ms <= 0) return "—";
            DateTime time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime();
            return time.ToString(format);
        }

        private static string Seconds(long ms)
        {
            if (ms <= 0) return "—";
            if (ms < 60000) return (ms / 1000.0).ToString("0.0") + "s";
            return (ms / 60000) + "m" + ((ms / 1000) % 60) + "s";
        }

        public static void SelfTest(Action<string, bool, string> check)
        {
            string work = Path.Combine(AppPaths.Root, "selftest-walkthrough");
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            try { Directory.CreateDirectory(work); } catch { }

            TaskRecord task = new TaskRecord();
            task.TaskId = "task_20261122_120000_abcdef";
            task.UserText = "把首页改成深色主题";
            task.Outcome = TaskOutcome.Succeeded;
            task.Steps = 4;
            task.DurationMs = 12500;
            task.PromptTokens = 1200;
            task.CompletionTokens = 300;
            task.Verification = "验收通过：2 项必需条件全部满足";

            List<ToolCall> calls = new List<ToolCall>();
            ToolCall read = new ToolCall();
            read.Name = "read_file";
            read.ArgsJson = "{\"path\":\"D:\\\\work\\\\index.html\"}";
            read.Status = "done";
            read.Output = "  <html>";
            read.StartedAt = Message.Now();
            calls.Add(read);
            ToolCall write = new ToolCall();
            write.Name = "write_file";
            write.ArgsJson = "{\"path\":\"D:\\\\work\\\\index.html\"}";
            write.Status = "done";
            write.BackupPath = "backups/index-1.html";
            calls.Add(write);
            ToolCall bad = new ToolCall();
            bad.Name = "shell";
            bad.ArgsJson = "{\"command\":\"npm run build\"}";
            bad.Status = "error";
            bad.Output = "npm ERR! missing script: build";
            calls.Add(bad);

            string file = Write(work, task, calls, "已把背景改为 #111，文字改为浅色。");
            check("walkthrough file written", file.Length > 0 && File.Exists(file), file);
            string body = file.Length > 0 && File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "";
            check("walkthrough leads with evidence strength", body.IndexOf("## 证据强度：已客观验证", StringComparison.Ordinal) >= 0, "");
            check("walkthrough lists timeline with failure", body.IndexOf("`shell` ✗", StringComparison.Ordinal) >= 0
                && body.IndexOf("npm run build", StringComparison.Ordinal) >= 0, "");
            check("walkthrough records change and backup", body.IndexOf("D:\\work\\index.html", StringComparison.Ordinal) >= 0
                && body.IndexOf("backups/index-1.html", StringComparison.Ordinal) >= 0, "");
            check("walkthrough marks risks", body.IndexOf("## 未完成与风险", StringComparison.Ordinal) >= 0, "");

            TaskRecord unverified = new TaskRecord();
            unverified.TaskId = "task_unverified";
            unverified.UserText = "总结一下这次改动";
            unverified.Outcome = TaskOutcome.Succeeded;
            unverified.Verification = "无可客观验证的验收条件（unverifiable）";
            check("walkthrough flags unverified work",
                Strength(unverified) == "unverified" && Compose(unverified, null, "改好了")
                    .IndexOf("仅模型自述", StringComparison.Ordinal) >= 0, "");

            List<string> listed = ListRecent(work, 10);
            check("walkthrough list newest first", listed.Count >= 1, listed.Count.ToString());
            string error;
            string readBack = Read(work, listed[0], out error);
            check("walkthrough read by name", error.Length == 0 && readBack.IndexOf("任务走查", StringComparison.Ordinal) >= 0, error);
            Read(work, "..\\..\\config.json", out error);
            check("walkthrough rejects path traversal", error.Length > 0, error);

            try { Directory.Delete(work, true); } catch { }
        }
    }
}
