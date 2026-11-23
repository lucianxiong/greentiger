using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GreenTiger
{
    /// <summary>
    /// 项目记忆（对齐 Claude Code 的 auto-memory）+ 全局记忆层 + 候选审核（3.20.0）：
    /// · 自动沉淀（任务成功 / 失败教训）先进候选区 <c>工作目录\.greentiger\memory-candidates.md</c>，
    ///   用户在「设置 → 记忆」里采纳后才进 memory.md——自动写入的错误结论不能再直接污染后续任务；
    /// · 显式记忆（save_memory 工具）直接写入：用户意图明确，不走候选；
    /// · 全局记忆层：数据目录根 memory.md，跨项目用户偏好与事实（save_memory scope=global 或手工编辑）；
    /// · 条数与总字数双重封顶，超出丢最旧的（记忆是摘要不是日志）；
    /// · 写失败只留日志绝不影响任务（工作目录只读时静默退化为无记忆）；
    /// · 文件是纯 Markdown，用户可以直接编辑删改，下次任务立即生效。
    /// </summary>
    public static class AgentMemory
    {
        /// <summary>最多保留的任务记忆条数。</summary>
        public const int MaxEntries = 120;
        /// <summary>记忆文件总字数上限（超出从最旧开始丢）。</summary>
        public const int MaxTotalChars = 16 * 1024;
        /// <summary>注入系统提示时的单次上限。</summary>
        public const int PromptChars = 3000;
        /// <summary>候选区上限（丢最旧），防止长期不审核堆积。</summary>
        public const int MaxCandidates = 60;

        public static string MemoryFile(string workDir)
        {
            if (string.IsNullOrEmpty(workDir)) return "";
            return Path.Combine(workDir, ".greentiger", "memory.md");
        }

        public static string CandidatesFile(string workDir)
        {
            if (string.IsNullOrEmpty(workDir)) return "";
            return Path.Combine(workDir, ".greentiger", "memory-candidates.md");
        }

        /// <summary>全局记忆文件（跨项目）：数据目录根 memory.md。</summary>
        public static string GlobalMemoryFile()
        {
            string root = AppPaths.Root;
            if (string.IsNullOrEmpty(root)) return "";
            return Path.Combine(root, "memory.md");
        }

        /// <summary>拼一条任务记忆行（目标 / 结果 / 改动文件）。</summary>
        private static string BuildLine(string userText, string detail, IList<string> files)
        {
            string goal = TextUtil.OneLine(userText, 160);
            StringBuilder sb = new StringBuilder();
            sb.Append("- ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append(" 目标：").Append(goal);
            if (!string.IsNullOrEmpty(detail)) sb.Append("｜结果：").Append(TextUtil.OneLine(detail, 160));
            if (files != null && files.Count > 0)
            {
                List<string> names = new List<string>();
                for (int i = 0; i < files.Count && names.Count < 8; i++)
                {
                    string f = files[i];
                    if (string.IsNullOrEmpty(f)) continue;
                    string name;
                    try { name = Path.GetFileName(f); } catch { name = f; }
                    if (name.Length > 0 && !names.Contains(name)) names.Add(name);
                }
                if (names.Count > 0) sb.Append("｜改动：").Append(string.Join("、", names.ToArray()));
            }
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>
        /// 任务成功后自动沉淀一条——先入候选区，用户采纳才进记忆（防记忆污染）。
        /// 任何异常都吞掉（记忆是锦上添花，不能成为负担）。
        /// </summary>
        public static void Append(string workDir, string userText, string outcome, string detail, IList<string> files)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(userText)) return;
                AppendLineCapped(CandidatesFile(workDir), BuildLine(userText, detail, files), MaxCandidates);
            }
            catch (Exception ex)
            {
                try { Log.Warn("记忆候选写入失败（忽略）: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 失败教训（《御舆》Ch6：只保存无法推导的信息——任务为什么卡住，是会话记录里
        /// 最难事后推导的部分）。同样先入候选区；带 ⚠ 标记便于肉眼区分。
        /// </summary>
        public static void AppendLesson(string workDir, string userText, string blocker)
        {
            try
            {
                if (string.IsNullOrEmpty(workDir) || string.IsNullOrWhiteSpace(blocker)) return;
                string line = "- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " ⚠ 未完成："
                    + TextUtil.OneLine(userText ?? "", 120) + "｜卡点：" + TextUtil.OneLine(blocker, 140) + Environment.NewLine;
                AppendLineCapped(CandidatesFile(workDir), line, MaxCandidates);
            }
            catch (Exception ex) { try { Log.Warn("失败教训写入失败（忽略）: " + ex.Message); } catch { } }
        }

        /// <summary>显式记忆（save_memory 工具）：用户意图明确，直接写入，不走候选。返回落盘文件路径。</summary>
        public static string AppendExplicit(string scope, string workDir, string content, out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrWhiteSpace(content)) { error = "内容为空"; return ""; }
                string line = "- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " " + TextUtil.OneLine(content, 400) + Environment.NewLine;
                string file = scope == "global" ? GlobalMemoryFile() : MemoryFile(workDir);
                if (string.IsNullOrEmpty(file)) { error = "数据目录未就绪"; return ""; }
                AppendLineCapped(file, line, MaxEntries);
                return file;
            }
            catch (Exception ex) { error = ex.Message; return ""; }
        }

        /// <summary>候选清单（非空行，时间正序）。</summary>
        public static List<string> LoadCandidates(string workDir)
        {
            List<string> items = new List<string>();
            try
            {
                string file = CandidatesFile(workDir);
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return items;
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length > 0) items.Add(line);
                }
            }
            catch { }
            return items;
        }

        /// <summary>采纳候选：进项目记忆（封顶生效），从候选区移除。返回采纳的行。</summary>
        public static string AcceptCandidate(string workDir, int index, out string error)
        {
            error = "";
            List<string> items = LoadCandidates(workDir);
            if (index < 0 || index >= items.Count) { error = "候选序号超出范围"; return ""; }
            string line = items[index];
            AppendLineCapped(MemoryFile(workDir), line + Environment.NewLine, MaxEntries);
            items.RemoveAt(index);
            WriteCandidates(workDir, items);
            return line;
        }

        /// <summary>丢弃候选。返回丢弃的行。</summary>
        public static string DismissCandidate(string workDir, int index, out string error)
        {
            error = "";
            List<string> items = LoadCandidates(workDir);
            if (index < 0 || index >= items.Count) { error = "候选序号超出范围"; return ""; }
            string line = items[index];
            items.RemoveAt(index);
            WriteCandidates(workDir, items);
            return line;
        }

        /// <summary>全局记忆正文（无文件/空返回 ""）。</summary>
        public static string LoadGlobal()
        {
            try
            {
                string file = GlobalMemoryFile();
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return "";
                return (File.ReadAllText(file, Encoding.UTF8) ?? "").Trim();
            }
            catch { return ""; }
        }

        /// <summary>全局记忆条数（界面展示用）。</summary>
        public static int GlobalLineCount()
        {
            string body = LoadGlobal();
            if (body.Length == 0) return 0;
            int count = 0;
            string[] lines = body.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().StartsWith("- ", StringComparison.Ordinal)) count++;
            }
            return count;
        }

        /// <summary>读取项目记忆正文（无文件/空返回 ""）。调用方负责包标题并控制预算。</summary>
        public static string Load(string workDir)
        {
            try
            {
                string file = MemoryFile(workDir);
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return "";
                string text = File.ReadAllText(file, Encoding.UTF8);
                return text == null ? "" : text.Trim();
            }
            catch { return ""; }
        }

        /// <summary>系统提示里的记忆区块：全局层在前、项目层在后（各自预算裁剪；空层一分上下文都不占）。</summary>
        public static string PromptSection(string workDir)
        {
            StringBuilder sb = new StringBuilder();
            string globalBody = LoadGlobal();
            if (globalBody.Length > 0)
            {
                if (globalBody.Length > 2000) globalBody = ContextPolicy.TruncateHeadTail(globalBody, 1500, 400);
                sb.AppendLine("## 全局记忆（跨项目的用户偏好与事实，数据目录 memory.md）");
                sb.AppendLine(globalBody);
                sb.AppendLine();
            }
            string body = Load(workDir);
            if (body.Length > 0)
            {
                if (body.Length > PromptChars) body = ContextPolicy.TruncateHeadTail(body, PromptChars - 800, 500);
                sb.AppendLine("## 项目记忆（本工作目录的历史任务沉淀，自动维护；候选在「设置 → 记忆」审核，也可手动编辑 .greentiger\\memory.md）");
                sb.AppendLine(body);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 追加一行并应用双重封顶（条数/总字数超出丢最旧，至少保留最新一条）。
        /// </summary>
        private static void AppendLineCapped(string file, string line, int maxEntries)
        {
            string dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string existing = "";
            try { if (File.Exists(file)) existing = File.ReadAllText(file, Encoding.UTF8); } catch { existing = ""; }
            string merged = existing + line;
            List<string> lines = new List<string>(merged.Split('\n'));
            List<string> kept = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i].TrimEnd('\r');
                if (l.Length == 0) continue;
                kept.Add(l);
            }
            int start = Math.Max(0, kept.Count - maxEntries);
            while (start > 0)
            {
                int total = 0;
                for (int i = start; i < kept.Count; i++) total += kept[i].Length + 1;
                if (total <= MaxTotalChars) break;
                start--;
            }
            StringBuilder outBuf = new StringBuilder();
            for (int i = start; i < kept.Count; i++) outBuf.AppendLine(kept[i]);
            File.WriteAllText(file, outBuf.ToString(), new UTF8Encoding(false));
        }

        private static void WriteCandidates(string workDir, List<string> items)
        {
            try
            {
                string file = CandidatesFile(workDir);
                if (string.IsNullOrEmpty(file)) return;
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < items.Count; i++) sb.AppendLine(items[i]);
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
