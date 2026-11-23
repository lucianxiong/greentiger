using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GreenTiger
{
    /// <summary>计划里的一个待办步骤（todo 工具的结构化形态）。</summary>
    public class TodoItem
    {
        public string Title { get; set; }
        public string Status { get; set; }

        public TodoItem()
        {
            Title = "";
            Status = "pending";
        }
    }

    /// <summary>
    /// 阶段5：结构化的任务状态。计划、待办、已产出文件不再只存在于消息文本里，
    /// 而是从本任务实际执行过的调用推导出来、随会话持久化、每步渲染成锚点注入上下文。
    /// 上下文压缩后消息可以被摘要掉，这里的结构化约束不会丢。
    /// </summary>
    public class TaskState
    {
        public string PlanTitle { get; set; }
        public List<TodoItem> Todos { get; set; }
        public List<string> ProducedFiles { get; set; }
        public long UpdatedAt { get; set; }

        public TaskState()
        {
            PlanTitle = "";
            Todos = new List<TodoItem>();
            ProducedFiles = new List<string>();
            UpdatedAt = 0;
        }

        public bool HasContent
        {
            get { return (PlanTitle != null && PlanTitle.Length > 0) || Todos.Count > 0 || ProducedFiles.Count > 0; }
        }

        /// <summary>
        /// 从本任务实际执行过的调用推导任务状态。单一事实来源是 taskCalls：
        /// plan 调用给标题与步骤，todo 调用给最新进度，成功的 write_file 给产出文件。
        /// </summary>
        public static TaskState FromCalls(IList<ToolCall> calls)
        {
            TaskState state = new TaskState();
            if (calls == null) return state;
            for (int i = 0; i < calls.Count; i++)
            {
                ToolCall call = calls[i];
                if (call == null) continue;
                Dictionary<string, object> args = Json.ParseObject(call.ArgsJson);
                if (string.Equals(call.Name, "plan", StringComparison.OrdinalIgnoreCase))
                {
                    // 计划被批准过（done）或尚未批准（pending）都算「当前计划」；被拒绝的也保留，模型需要看到它
                    if (state.Todos.Count == 0 || string.Equals(call.Status, "done", StringComparison.Ordinal))
                    {
                        state.PlanTitle = Json.Str(args, "title", state.PlanTitle);
                        List<TodoItem> steps = StepsFromPlan(args);
                        if (steps.Count > 0) state.Todos = steps;
                    }
                }
                else if (string.Equals(call.Name, "todo", StringComparison.OrdinalIgnoreCase) && call.Status != "denied")
                {
                    List<TodoItem> items = TodosFromArgs(args);
                    if (items.Count > 0) state.Todos = items;
                }
                else if (string.Equals(call.Name, "write_file", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(call.Status, "done", StringComparison.Ordinal))
                {
                    string path = Json.Str(args, "path", "");
                    if (path.Length > 0 && !state.ProducedFiles.Contains(path)) state.ProducedFiles.Add(path);
                }
            }
            state.UpdatedAt = Message.Now();
            return state;
        }

        private static List<TodoItem> StepsFromPlan(Dictionary<string, object> args)
        {
            List<TodoItem> result = new List<TodoItem>();
            List<object> steps = Json.Array(args, "steps");
            for (int i = 0; i < steps.Count; i++)
            {
                TodoItem item = new TodoItem();
                item.Title = steps[i] is string ? (string)steps[i] : Convert.ToString(steps[i]);
                item.Status = i == 0 ? "doing" : "pending";
                result.Add(item);
            }
            return result;
        }

        private static List<TodoItem> TodosFromArgs(Dictionary<string, object> args)
        {
            List<TodoItem> result = new List<TodoItem>();
            List<object> items = Json.Array(args, "items");
            for (int i = 0; i < items.Count; i++)
            {
                Dictionary<string, object> item = items[i] as Dictionary<string, object>;
                if (item == null)
                {
                    TodoItem plain = new TodoItem();
                    plain.Title = Convert.ToString(items[i]);
                    result.Add(plain);
                    continue;
                }
                TodoItem t = new TodoItem();
                t.Title = Json.Str(item, "title", Json.Str(item, "name", ""));
                t.Status = Json.Str(item, "status", "pending");
                result.Add(t);
            }
            return result;
        }

        /// <summary>渲染任务状态锚点（含预算与上次失败信息），作为每步注入的结构化上下文层。</summary>
        public string Render(TaskBudget budget, IList<ToolCall> calls, int maxChars)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("【任务状态】第 ").Append(budget == null ? 0 : budget.Steps)
              .Append("/").Append(budget == null ? 0 : budget.MaxSteps).Append(" 步");
            if (budget != null)
            {
                sb.Append("，已用 ").Append(budget.ElapsedMs / 1000).Append("s/").Append(budget.MaxSeconds).Append("s");
                if (budget.Repairs > 0) sb.Append("，修复 ").Append(budget.Repairs).Append("/").Append(budget.MaxRepairs);
            }
            sb.AppendLine();
            if (Todos.Count > 0)
            {
                if (!string.IsNullOrEmpty(PlanTitle)) sb.Append("当前计划：").AppendLine(PlanTitle);
                int pending = 0;
                for (int i = 0; i < Todos.Count && i < 12; i++)
                {
                    TodoItem t = Todos[i];
                    string mark = t.Status == "done" ? "[x]" : (t.Status == "doing" ? "[→]" : "[ ]");
                    sb.Append(mark).Append(' ').AppendLine(t.Title);
                    if (t.Status != "done") pending++;
                }
                if (pending > 0) sb.Append("待完成步骤：").Append(pending).AppendLine(" 个");
            }
            if (ProducedFiles.Count > 0)
            {
                sb.Append("本任务已写入的文件（声明完成前必须真实存在）：");
                for (int i = 0; i < ProducedFiles.Count && i < 8; i++)
                {
                    sb.Append(i == 0 ? "" : "、").Append(ProducedFiles[i]);
                }
                sb.AppendLine();
            }
            if (calls != null)
            {
                ToolCall lastFail = null;
                for (int i = calls.Count - 1; i >= 0; i--)
                {
                    ToolCall c = calls[i];
                    if (c == null || c.Status == "done") continue;
                    lastFail = c;
                    break;
                }
                if (lastFail != null)
                {
                    sb.Append("上次失败：").Append(lastFail.Name).Append(" · ")
                      .Append(string.IsNullOrEmpty(lastFail.ErrorCode) ? "unknown" : lastFail.ErrorCode)
                      .Append(" · ").Append(TextUtil.OneLine(lastFail.Output, 80)).AppendLine();
                }
            }
            sb.Append("提醒：不要重复已失败的相同调用；声明完成前，产出的文件必须真实存在。");
            string text = sb.ToString();
            return ContextPolicy.ClampChars(text, maxChars);
        }
    }

    /// <summary>
    /// 阶段5：上下文分层预算。五层各自有界，避免长任务上下文无限膨胀：
    /// L1 固定核心（系统提示，静态）；L2 规则文件（CLAUDE.md/AGENTS.md/rules.md，总预算）；
    /// L3 任务状态（TaskState 锚点，字符预算）；L4 历史（超阈值压缩，单任务限次）；
    /// L5 工具输出（超长落盘为附件，正文只留头尾）。阈值可由配置覆盖。
    /// </summary>
    public static class ContextPolicy
    {
        public const int ToolResultContextChars = 16000;
        public const int ToolResultHeadChars = 4000;
        public const int ToolResultTailChars = 2000;
        public const int RuleFileChars = 6000;
        public const int MaxPathRuleFiles = 64;
        public const int MaxPathRuleBytes = 262144;

        public sealed class ProjectPathRule
        {
            public string Path { get; set; }
            public string BaseDirectory { get; set; }
            public string Content { get; set; }
            public List<string> Paths { get; set; }
        }

        /// <summary>
        /// 为当前工作目录列出仓库内从根目录到工作目录的规则文件。
        /// 按仓库根目录到工作目录加载 AGENTS.md、CLAUDE.md、.claude/CLAUDE.md、CLAUDE.local.md。
        /// 同一目录内 AGENTS.md 先于 Claude 规则，子目录规则随后追加。
        /// </summary>
        public static List<string> ProjectRuleFiles(string workDir)
        {
            List<string> result = new List<string>();
            List<string> scopes = ProjectScopes(workDir);
            for (int i = 0; i < scopes.Count; i++)
            {
                string agents = Path.Combine(scopes[i], "AGENTS.md");
                string claude = Path.Combine(scopes[i], "CLAUDE.md");
                string claudeProject = Path.Combine(scopes[i], ".claude", "CLAUDE.md");
                string claudeLocal = Path.Combine(scopes[i], "CLAUDE.local.md");
                AddProjectRuleFile(result, agents);
                AddProjectRuleFile(result, claude);
                AddProjectRuleFile(result, claudeProject);
                AddProjectRuleFile(result, claudeLocal);
            }
            return result;
        }

        private static void AddProjectRuleFile(List<string> result, string path)
        {
            try
            {
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                string parentPath = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parentPath) && Directory.Exists(parentPath) &&
                    (File.GetAttributes(parentPath) & FileAttributes.ReparsePoint) != 0) return;
                result.Add(path);
            }
            catch { }
        }

        /// <summary>列出当前仓库作用域内的 Claude Code .claude/rules Markdown 文件，跳过重解析点和过大的文件。</summary>
        public static List<string> ProjectPathRuleFiles(string workDir)
        {
            List<string> result = new List<string>();
            List<string> scopes = ProjectScopes(workDir);
            // Prefer the most specific scope if the repository has more than the budget; then restore broad-to-specific order.
            for (int i = scopes.Count - 1; i >= 0 && result.Count < MaxPathRuleFiles; i--)
            {
                string claudeDirectory = Path.Combine(scopes[i], ".claude");
                if (!Directory.Exists(claudeDirectory)) continue;
                try { if ((File.GetAttributes(claudeDirectory) & FileAttributes.ReparsePoint) != 0) continue; }
                catch { continue; }
                string directory = Path.Combine(claudeDirectory, "rules");
                if (!Directory.Exists(directory)) continue;
                List<string> scoped = new List<string>();
                CollectPathRuleFiles(directory, 0, scoped);
                int room = MaxPathRuleFiles - result.Count;
                if (scoped.Count > room) scoped.RemoveRange(room, scoped.Count - room);
                result.InsertRange(0, scoped);
            }
            return result;
        }

        public static ProjectPathRule LoadProjectPathRule(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                FileInfo info = new FileInfo(path);
                if (info.Length > MaxPathRuleBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                string text = File.ReadAllText(path, Encoding.UTF8);
                string body;
                List<string> paths = ParsePathRuleFrontMatter(text, out body);
                if (string.IsNullOrWhiteSpace(body)) return null;
                ProjectPathRule rule = new ProjectPathRule();
                rule.Path = info.FullName;
                DirectoryInfo current = info.Directory;
                while (current != null && !string.Equals(current.Name, ".claude", StringComparison.OrdinalIgnoreCase))
                    current = current.Parent;
                rule.BaseDirectory = current == null || current.Parent == null ? "" : current.Parent.FullName;
                rule.Content = body.Trim();
                rule.Paths = paths;
                return rule;
            }
            catch { return null; }
        }

        private static List<string> ProjectScopes(string workDir)
        {
            List<string> scopes = new List<string>();
            if (string.IsNullOrWhiteSpace(workDir)) return scopes;
            DirectoryInfo working;
            try { working = new DirectoryInfo(Path.GetFullPath(workDir)); }
            catch { return scopes; }
            if (!working.Exists) return scopes;

            DirectoryInfo repository = null;
            for (DirectoryInfo current = working; current != null; current = current.Parent)
            {
                string marker = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(marker) || File.Exists(marker)) { repository = current; break; }
            }
            for (DirectoryInfo current = working; current != null; current = repository == null ? null : current.Parent)
            {
                scopes.Add(current.FullName);
                if (repository == null || string.Equals(current.FullName, repository.FullName, StringComparison.OrdinalIgnoreCase)) break;
            }
            if (repository != null && (scopes.Count == 0 || !string.Equals(scopes[scopes.Count - 1], repository.FullName, StringComparison.OrdinalIgnoreCase)))
                return new List<string>();
            scopes.Reverse();
            return scopes;
        }

        private static void CollectPathRuleFiles(string directory, int depth, List<string> result)
        {
            if (depth > 8 || result.Count >= MaxPathRuleFiles) return;
            try
            {
                DirectoryInfo info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return;
                FileInfo[] files = info.GetFiles("*.md", SearchOption.TopDirectoryOnly);
                Array.Sort(files, delegate(FileInfo a, FileInfo b) { return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); });
                for (int i = 0; i < files.Length && result.Count < MaxPathRuleFiles; i++)
                {
                    try
                    {
                        if ((files[i].Attributes & FileAttributes.ReparsePoint) == 0 && files[i].Length <= MaxPathRuleBytes)
                            result.Add(files[i].FullName);
                    }
                    catch { }
                }
                DirectoryInfo[] children = info.GetDirectories();
                Array.Sort(children, delegate(DirectoryInfo a, DirectoryInfo b) { return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); });
                for (int i = 0; i < children.Length && result.Count < MaxPathRuleFiles; i++)
                    CollectPathRuleFiles(children[i].FullName, depth + 1, result);
            }
            catch (Exception ex)
            {
                // 项目规则是安全护栏的一部分：加载失败必须可见，不能静默降级成「没有规则」。
                Log.Warn("项目规则目录读取失败（" + directory + "）：" + ex.Message);
            }
        }

        private static List<string> ParsePathRuleFrontMatter(string text, out string body)
        {
            List<string> paths = new List<string>();
            body = text == null ? "" : text;
            if (string.IsNullOrEmpty(text)) return paths;
            string normalized = text.TrimStart('\uFEFF').Replace("\r\n", "\n");
            string[] lines = normalized.Split('\n');
            if (lines.Length < 3 || lines[0].Trim() != "---") return paths;
            int close = -1;
            for (int i = 1; i < lines.Length; i++)
                if (lines[i].Trim() == "---") { close = i; break; }
            if (close < 0) return paths;

            bool readingList = false;
            for (int i = 1; i < close; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                if (trimmed.StartsWith("paths:", StringComparison.OrdinalIgnoreCase))
                {
                    string value = trimmed.Substring(6).Trim();
                    readingList = value.Length == 0;
                    if (value.Length > 0) AddPathValues(paths, value);
                    continue;
                }
                if (!readingList) continue;
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("-", StringComparison.Ordinal))
                {
                    AddPathValues(paths, trimmed.Substring(1).Trim());
                    continue;
                }
                if (char.IsWhiteSpace(line[0])) continue;
                readingList = false;
            }
            body = string.Join("\n", lines, close + 1, lines.Length - close - 1).TrimStart('\n', '\r');
            return paths;
        }

        private static void AddPathValues(List<string> paths, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string cleaned = value.Trim();
            if (cleaned.StartsWith("[", StringComparison.Ordinal) && cleaned.EndsWith("]", StringComparison.Ordinal))
                cleaned = cleaned.Substring(1, cleaned.Length - 2);
            string[] values = cleaned.Split(',');
            for (int i = 0; i < values.Length; i++)
            {
                string pattern = values[i].Trim().Trim('"', '\'');
                if (pattern.Length > 0 && paths.IndexOf(pattern) < 0) paths.Add(pattern);
            }
        }

        public static int MaxRulesChars(Config config)
        {
            int v = config == null ? 0 : config.MaxRulesChars;
            // 下限钳到 1000：配成 1..200 时连第一条规则都装不进（remain<=200 直接跳过），
            // 规则层静默全灭且无任何报错，用户完全无法察觉自己的规则没生效。
            if (v > 0 && v < 1000) v = 1000;
            return v > 0 ? v : 8000;
        }

        public static int MaxStateChars(Config config)
        {
            int v = config == null ? 0 : config.MaxStateChars;
            return v > 0 ? v : 2000;
        }

        public static int CompactChars(Config config)
        {
            int v = config == null ? 0 : config.ContextCompactChars;
            return v > 0 ? v : 60000;
        }

        /// <summary>
        /// 有效窗口公式（御舆 Ch7）：压缩阈值不该是一个写死的常数，而应当由「这个模型到底能装多少」推出来。
        ///
        /// effective = 模型窗口 × 压缩水位(0.6) × 每 token 字符数(1.2)
        /// 用户显式配了 ContextCompactChars 就尊重用户（配置优先于推导）；
        /// 模型未知时按 32k 保守估计——宁可早压缩，也不要让请求撞上端点上限后整轮失败。
        /// </summary>
        public static int EffectiveCompactChars(Config config)
        {
            int configured = config == null ? 0 : config.ContextCompactChars;
            if (configured > 0) return configured;
            int window = ModelWindowTokens(config == null ? "" : config.ProviderModel);
            long chars = (long)(window * 0.6 * 1.2);
            if (chars < 8000) chars = 8000;
            // 3.30.0 长文本：1M 窗口按 600k 水位压缩（此前封顶 400k，几百页文档会在装得下时被提前摘要掉）
            if (chars > 600000) chars = 600000;
            return (int)chars;
        }

        /// <summary>
        /// 3.30.0 长文本预算：单条用户输入能装多少，不再写死 24000。
        /// 取窗口字符数的 40%（给系统提示、工具结果和输出留余量），1M 窗口封顶 300k 字。
        /// 以前所有模型都按 24k 截断，等于 1M 上下文只用了 2%——长文档刚贴进来就被砍掉。
        /// </summary>
        public static int InputCharBudget(Config config)
        {
            int window = ModelWindowTokens(config == null ? "" : config.ProviderModel);
            long chars = (long)(window * 1.2 * 0.4);
            if (chars < 8000) chars = 8000;
            if (chars > 300000) chars = 300000;
            return (int)chars;
        }

        /// <summary>文本围栏协议每次要把历史整段重发，输入预算收紧到 32k 字，避免弱端点直接超窗。</summary>
        public static int TextInputCharBudget(Config config)
        {
            int budget = InputCharBudget(config);
            return budget > 32000 ? 32000 : budget;
        }

        /// <summary>单条工具结果回灌的头部预算：窗口越大，允许模型一次看到的原文越多。</summary>
        public static int ToolResultHead(Config config)
        {
            int window = ModelWindowTokens(config == null ? "" : config.ProviderModel);
            if (window >= 500000) return 24000;
            if (window >= 200000) return 16000;
            if (window >= 100000) return 10000;
            return 6000;
        }

        /// <summary>单条工具结果回灌的尾部预算（保留结尾的错误/总结/统计行）。</summary>
        public static int ToolResultTail(Config config)
        {
            int window = ModelWindowTokens(config == null ? "" : config.ProviderModel);
            if (window >= 500000) return 8000;
            if (window >= 200000) return 5000;
            if (window >= 100000) return 3000;
            return 2000;
        }

        /// <summary>read_file 单次输出上限：与工具结果头尾预算对齐，避免读完再被二次截断。</summary>
        public static int ReadFileChars(Config config)
        {
            int chars = ToolResultHead(config) + ToolResultTail(config) + 2000;
            return chars > 36000 ? 36000 : chars;
        }

        /// <summary>API 层上下文编辑水位：超过它才批量老化旧工具结果，平时保持前缀稳定以命中 prompt cache。</summary>
        public static int ApiHistoryChars(Config config)
        {
            int chars = EffectiveCompactChars(config) * 4 / 5;
            return chars < 16000 ? 16000 : chars;
        }

        /// <summary>原生历史保留条数。以前固定 40 条，长任务每隔几步就丢前缀、缓存全废。</summary>
        public static int HistoryMessageLimit(Config config, bool native)
        {
            if (!native) return 40;
            int window = ModelWindowTokens(config == null ? "" : config.ProviderModel);
            if (window >= 500000) return 120;
            if (window >= 100000) return 96;
            return 64;
        }

        /// <summary>压缩时最多把多少字的历史喂给摘要模型；太小会漏细节，太大等于把整个窗口再烧一遍。</summary>
        public static int CompactTranscriptChars(Config config)
        {
            int window = ModelWindowTokens(config == null ? "" : config.ProviderModel);
            long chars = (long)(window * 1.2 * 0.25);
            if (chars < 16000) chars = 16000;
            if (chars > 180000) chars = 180000;
            return (int)chars;
        }

        /// <summary>压缩摘要的目标长度：窗口越大，允许保留的交接细节越多。</summary>
        public static int SummaryChars(Config config)
        {
            int window = ModelWindowTokens(config == null ? "" : config.ProviderModel);
            if (window >= 500000) return 3200;
            if (window >= 200000) return 2400;
            if (window >= 100000) return 1800;
            return 1200;
        }

        /// <summary>
        /// 长文本任务识别：用户一次贴了上万字，或附件里带了几十 KB 以上的文档，就注入长上下文工作法。
        /// 平时不注入，避免每轮都白背一段长文提示。
        /// </summary>
        public static string LongTextBrief(string userText, string[] files)
        {
            bool longText = userText != null && userText.Length >= 12000;
            bool longFile = false;
            if (files != null)
            {
                for (int i = 0; i < files.Length; i++)
                {
                    if (string.IsNullOrEmpty(files[i])) continue;
                    string ext = Path.GetExtension(files[i]).ToLowerInvariant();
                    bool doc = ext == ".txt" || ext == ".md" || ext == ".csv" || ext == ".json" || ext == ".log"
                        || ext == ".pdf" || ext == ".doc" || ext == ".docx" || ext == ".html" || ext == ".htm"
                        || ext == ".xml" || ext == ".yaml" || ext == ".yml";
                    if (!doc) continue;
                    try
                    {
                        if (File.Exists(files[i]) && new FileInfo(files[i]).Length >= 48 * 1024) { longFile = true; break; }
                    }
                    catch { }
                }
            }
            if (!longText && !longFile) return "";
            return "[长文本任务 · 长上下文工作法]\n"
                 + "这是长文，不要一次整读，按四步来：\n"
                 + "1. 建索引：read_file 加 {\"outline\":true} 先拿章节/标题/符号与行号，再按 offset/limit 只读相关段。\n"
                 + "2. 原文在上：先引原句（文件:行号）再下结论；数字、日期、人名、条款必须能指回原文，不许凭印象概括。\n"
                 + "3. 分块笔记：超出单次可读量就按章节切块，每块产「事实/数字/引文/位置」四栏笔记，写到 notes.md 或 todo，最后只读笔记汇总。\n"
                 + "4. 抗压缩：压缩后靠「文档索引 + 已读/未读行段 + 笔记文件」续接，不要凭记忆复述全文。\n"
                 + "结论里分开写「原文明确写了」和「我的推断」。";
        }

        /// <summary>模型名 → 上下文窗口（token）。按子串匹配，覆盖主流云端模型；认不出按 32768 保守估计。</summary>
        public static int ModelWindowTokens(string model)
        {
            string m = (model ?? "").ToLowerInvariant();
            if (m.Length == 0) return 32768;
            if (m.IndexOf("gemini", StringComparison.Ordinal) >= 0) return 1000000;
            if (m.IndexOf("gpt-4.1", StringComparison.Ordinal) >= 0) return 1000000;
            // Claude Fable / Mythos 5.x 是 1M 窗口（官方 2026-11 发布口径）；其余 Claude 仍按 200K 保守估
            if (m.IndexOf("fable", StringComparison.Ordinal) >= 0 || m.IndexOf("mythos", StringComparison.Ordinal) >= 0) return 1000000;
            if (m.IndexOf("claude", StringComparison.Ordinal) >= 0) return 200000;
            if (m.IndexOf("kimi", StringComparison.Ordinal) >= 0 || m.IndexOf("moonshot", StringComparison.Ordinal) >= 0)
                return m.IndexOf("k3", StringComparison.Ordinal) >= 0 ? 1000000 : 200000;
            if (m.IndexOf("qwen", StringComparison.Ordinal) >= 0)
            {
                if (m.IndexOf("qwen3", StringComparison.Ordinal) >= 0 || m.IndexOf("qwen-max", StringComparison.Ordinal) >= 0) return 128000;
                return 32768;   // qwen2.5 系列多数是 32k
            }
            if (m.IndexOf("deepseek", StringComparison.Ordinal) >= 0)
                return m.IndexOf("v4", StringComparison.Ordinal) >= 0 ? 1000000 : 128000;
            if (m.IndexOf("glm", StringComparison.Ordinal) >= 0) return 128000;
            if (m.IndexOf("gpt-4o", StringComparison.Ordinal) >= 0) return 128000;
            if (m.IndexOf("gpt-4", StringComparison.Ordinal) >= 0) return 128000;
            if (m.IndexOf("o1", StringComparison.Ordinal) == 0 || m.IndexOf("o3", StringComparison.Ordinal) == 0
                || m.IndexOf("o4", StringComparison.Ordinal) == 0) return 200000;
            if (m.IndexOf("llama", StringComparison.Ordinal) >= 0) return 128000;
            if (m.IndexOf("mistral", StringComparison.Ordinal) >= 0) return 32000;
            if (m.IndexOf("grok", StringComparison.Ordinal) >= 0) return 131072;
            return 32768;
        }

        public static int CompactLimit(Config config)
        {
            int v = config == null ? 0 : config.ContextCompactLimit;
            return v > 0 ? v : 3;
        }

        /// <summary>超长文本保留头尾、中间省略。短文本原样返回。省略标记带被省略的字数，不谎报。</summary>
        public static string TruncateHeadTail(string text, int head, int tail)
        {
            if (string.IsNullOrEmpty(text)) return text == null ? "" : text;
            if (text.Length <= head + tail) return text;
            int omitted = text.Length - head - tail;
            return text.Substring(0, head) +
                   "\n…（中间省略 " + omitted + " 字；完整输出保留在本机工具卡，模型端需分段重查）…\n" +
                   text.Substring(text.Length - tail);
        }

        /// <summary>
        /// 3.21.0 API 层工具结果老化：把最近 keepRecent 条之外的旧 role:"tool" 消息就地
        /// 替换为占位摘要（保留头部与 tool_call_id，协议结构不动）。会话层 MicroCompact
        /// 老化的是 session 拷贝；native 协议真正发给 API 的是 Brain 内部历史，老化必须
        /// 做在这里请求 token 才真的省下来。返回本次节省的字符数（无老化返回 0）。
        /// </summary>
        public static int AgeToolMessages(List<Dictionary<string, object>> messages, int keepRecent)
        {
            return AgeToolMessages(messages, keepRecent, 0);
        }

        /// <summary>
        /// 3.30.0 上下文编辑（Claude API context editing 口径）：不是每追加一条结果就改写旧消息，
        /// 而是历史超过 targetChars 时一次性把最老的工具结果批量折叠到 targetChars 以下。
        /// 这样两次编辑之间的请求前缀完全不变，OpenAI / DeepSeek 的自动 prompt cache 才能命中；
        /// 每轮都改一点等于每轮都让缓存失效，省下的 token 还不够赔缓存差价。
        /// </summary>
        public static int AgeToolMessages(List<Dictionary<string, object>> messages, int keepRecent, int targetChars)
        {
            if (messages == null || messages.Count == 0 || keepRecent < 1) return 0;
            int total = MessageChars(messages);
            if (targetChars > 0 && total <= targetChars) return 0;
            List<int> toolIdx = new List<int>();
            for (int i = 0; i < messages.Count; i++)
            {
                Dictionary<string, object> m = messages[i];
                if (m == null || !m.ContainsKey("role")) continue;
                if (!Convert.ToString(m["role"]).Equals("tool", StringComparison.Ordinal)) continue;
                toolIdx.Add(i);
            }
            int saved = 0;
            for (int i = 0; i + keepRecent < toolIdx.Count; i++)
            {
                if (targetChars > 0 && total - saved <= targetChars) break;
                Dictionary<string, object> m = messages[toolIdx[i]];
                object contentObj;
                if (!m.TryGetValue("content", out contentObj) || contentObj == null) continue;
                string content = contentObj as string;
                if (string.IsNullOrEmpty(content) || content.Length <= 800) continue;
                if (content.IndexOf("【已老化：", StringComparison.Ordinal) >= 0) continue;
                // untrusted_content 包裹的结构要保住：只折叠内部正文，标签继续成对
                string prefix = "";
                string suffix = "";
                if (content.StartsWith("<untrusted_content>\n", StringComparison.Ordinal))
                {
                    prefix = "<untrusted_content>\n";
                    suffix = "\n</untrusted_content>";
                    content = content.Substring(prefix.Length);
                    if (content.EndsWith(suffix, StringComparison.Ordinal))
                        content = content.Substring(0, content.Length - suffix.Length);
                }
                int cut = content.IndexOf('\n', 240);
                if (cut < 0 || cut > 440) cut = 240;
                string aged = content.Substring(0, cut) +
                    "\n…【已老化：原 " + content.Length + " 字符已折叠；如需完整内容请重新调用该工具获取】";
                string next = prefix + aged + suffix;
                saved += (contentObj as string).Length - next.Length;
                m["content"] = next;
            }
            if (saved > 0)
            {
                lock (AgeGate) { AgedToolCharsTotal += saved; }
            }
            return saved;
        }

        /// <summary>历史里所有字符串 content 的总字符数（缓存/上下文编辑的水位尺）。</summary>
        public static int MessageChars(List<Dictionary<string, object>> messages)
        {
            if (messages == null) return 0;
            int total = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                Dictionary<string, object> m = messages[i];
                if (m == null) continue;
                object content;
                if (!m.TryGetValue("content", out content)) continue;
                if (content is string) total += ((string)content).Length;
            }
            return total;
        }

        private static readonly object AgeGate = new object();
        /// <summary>跨会话累计的 API 层老化节省字符（/stats 展示）。</summary>
        public static long AgedToolCharsTotal = 0;

        /// <summary>
        /// 回灌给模型的工具结果截断（Anthropic writing-tools-for-agents「三件套」口径）：
        /// 标记里如实写 ① 原文完整大小（字数+行数）② 截断点对齐行边界（head 吸附到行尾、tail 吸附到行首，
        /// 400 字窗口内找不到换行就硬切，保证超长单行也能截）③ 给出收窄重查的具体手段。
        /// 截断提示是给模型的纠错接口：引导它改用 offset/limit、过滤、分页，而不是反复要全文。
        /// </summary>
        public static string TruncateToolResult(string text, int head, int tail)
        {
            if (string.IsNullOrEmpty(text)) return text == null ? "" : text;
            if (text.Length <= head + tail) return text;
            int headCut = SnapLineForward(text, head, 400);
            int tailStart = SnapLineBackward(text, text.Length - tail, 400);
            if (headCut >= tailStart) { headCut = head; tailStart = text.Length - tail; }
            if (headCut >= tailStart) return text;
            int lines = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lines++;
            string headPart = text.Substring(0, headCut);
            string tailPart = text.Substring(tailStart);
            // 吸附成功时 head 以换行结尾 / tail 以行首开头，分隔符只在硬切（无换行可吸附）时补
            string sepHead = headPart.Length == 0 || headPart[headPart.Length - 1] == '\n' ? "" : "\n";
            string sepTail = tailPart.Length == 0 || tailPart[0] == '\n' ? "" : "\n";
            return headPart + sepHead +
                   "…【已截断】原文共 " + text.Length + " 字符 / " + lines + " 行，此处保留开头 " + headCut +
                   " 字符与结尾 " + (text.Length - tailStart) + " 字符（已对齐行边界，中间省略 " + (tailStart - headCut) +
                   " 字符）。需要其余部分请收窄范围重查：read_file 用 offset/limit 分段读，grep 用 include / max_results 过滤，web_fetch 调小 max_chars。" +
                   sepTail + tailPart;
        }

        /// <summary>从 at 开始向后找换行（最多 window 字），找到返回换行后一位，找不到返回 at（硬切）。</summary>
        private static int SnapLineForward(string text, int at, int window)
        {
            int max = Math.Min(text.Length, at + window);
            for (int i = at; i < max; i++) if (text[i] == '\n') return i + 1;
            return at;
        }

        /// <summary>从 at 开始向前找换行（最多 window 字），找到返回换行后一位，找不到返回 at（硬切）。</summary>
        private static int SnapLineBackward(string text, int at, int window)
        {
            int min = Math.Max(0, at - window);
            for (int i = at - 1; i >= min; i--) if (text[i] == '\n') return i + 1;
            return at;
        }

        /// <summary>硬字符上限：超限截头（约束类内容截断尾部损失最小），截断处标注。</summary>
        public static string ClampChars(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || maxChars <= 0 || text.Length <= maxChars) return text == null ? "" : text;
            return text.Substring(0, maxChars) + "\n…（任务状态过长已截断）";
        }

        /// <summary>
        /// 规则层总预算：按广度到具体度装填（用户规则 → 项目规则 → 路径规则）。
        /// 装不下的规则截断并标注，保证 L2 层有界且匹配范围更窄的约束在后文明确覆盖。
        /// </summary>
        public static string ApplyRulesBudget(IList<KeyValuePair<string, string>> sections, int totalBudget)
        {
            if (sections == null || sections.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            int used = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                string header = sections[i].Key;
                string body = sections[i].Value == null ? "" : sections[i].Value.Trim();
                if (body.Length == 0) continue;
                if (body.Length > RuleFileChars) body = body.Substring(0, RuleFileChars) + "\n…（规则过长已截断）";
                int cost = header.Length + body.Length + 2;
                int remain = totalBudget - used;
                if (remain <= 200)
                {
                    sb.AppendLine("…（还有规则文件「" + header + "」未装入：超出规则层总预算 " + totalBudget + " 字）");
                    break;
                }
                if (cost > remain)
                {
                    // 截断长度必须钳到 body 实际长度内：header 很长（路径规则头可达 150+ 字）而正文很短时，
                    // Math.Max(100, …) 会超过 body.Length，Substring 直接抛 ArgumentOutOfRangeException——
                    // 该异常会沿 RulesSection → 系统提示 → 任务第一步冒出，整个工作目录的任务全部 Failed。
                    int keep = Math.Min(body.Length, Math.Max(100, remain - header.Length - 60));
                    if (keep < body.Length) body = body.Substring(0, keep) + "\n…（规则过长已按预算截断）";
                    cost = header.Length + body.Length + 2;
                }
                sb.AppendLine(header);
                sb.AppendLine(body);
                sb.AppendLine();
                used += cost;
            }
            return sb.ToString().TrimEnd();
        }
    }
}
