using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GreenTiger
{
    /// <summary>
    /// 结构化执行轨迹。每行一个 JSON 事件，落在 logs/trace-YYYYMMDD.jsonl。
    /// 目的不是"多写日志"，而是让一次任务能从 taskId 串起模型请求、工具调用、审批、验收与终态。
    /// 敏感值（Key、Token、密码）在写入前脱敏。
    /// </summary>
    public static class Trace
    {
        private static readonly object Gate = new object();

        public static string TraceFile
        {
            get { return Path.Combine(AppPaths.LogsDir, "trace-" + DateTime.Now.ToString("yyyyMMdd") + ".jsonl"); }
        }

        public static void Event(string taskId, string type, Dictionary<string, object> data)
        {
            try
            {
                Dictionary<string, object> row = new Dictionary<string, object>();
                row["ts"] = Message.Now();
                row["task"] = taskId == null ? "" : taskId;
                row["type"] = type == null ? "" : type;
                if (data != null)
                {
                    foreach (KeyValuePair<string, object> kv in data)
                    {
                        row[kv.Key] = Redact(kv.Key, kv.Value);
                    }
                }
                string line = Json.Stringify(row);
                lock (Gate)
                {
                    AppPaths.EnsureDirectory(AppPaths.LogsDir);
                    File.AppendAllText(TraceFile, line + "\n", new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                // 轨迹失败绝不能影响任务本身；但「哪一类事件、哪一行写不进去」必须留给排障。
                try { Log.Warn("trace 写入失败（" + (type ?? "?") + "）: " + Log.Describe(ex)); } catch { }
            }
        }

        public static void Event(string taskId, string type, string key, object value)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(key)) data[key] = value;
            Event(taskId, type, data);
        }

        /// <summary>
        /// 脱敏：字段名命中敏感词就整体替换，值里出现 secret/key 特征也替换。
        /// 计量字段（prompt_tokens/completion_tokens/tokens_estimated 等）以 _tokens 结尾，
        /// 只是用量数字不是凭据——以前整体命中 "token" 被脱敏掉，成本审计轨迹永远查不到用量。
        /// </summary>
        private static object Redact(string key, object value)
        {
            if (!string.IsNullOrEmpty(key))
            {
                string k = key.ToLowerInvariant();
                bool meter = k.EndsWith("tokens", StringComparison.Ordinal) || k.EndsWith("_tokens", StringComparison.Ordinal);
                if (!meter &&
                    (k.IndexOf("key", StringComparison.Ordinal) >= 0 ||
                     k.IndexOf("token", StringComparison.Ordinal) >= 0 ||
                     k.IndexOf("password", StringComparison.Ordinal) >= 0 ||
                     k.IndexOf("secret", StringComparison.Ordinal) >= 0 ||
                     k.IndexOf("authorization", StringComparison.Ordinal) >= 0))
                {
                    return "[redacted]";
                }
            }
            string s = value as string;
            if (s == null) return value;
            if (s.Length > 4000) s = s.Substring(0, 4000) + "…(truncated)";
            string lower = s.ToLowerInvariant();
            if (lower.IndexOf("api_key", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("apikey", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("authorization", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("bearer ", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("password", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("sk-", StringComparison.Ordinal) >= 0 ||
                lower.IndexOf("secret", StringComparison.Ordinal) >= 0)
            {
                return "[redacted]";
            }
            return s;
        }

        /// <summary>
        /// 日志轮转清理（启动时调用一次）：logs 目录保留最近 keepDays 天的 trace/log 文件；
        /// stats/tasks.jsonl 超过 5MB 归档成 tasks-YYYYMMDDHHMMSS.jsonl。以前无界增长，统计越跑越慢。
        /// </summary>
        public static void Cleanup(int keepDays)
        {
            try
            {
                if (keepDays <= 0) keepDays = 30;
                DateTime cutoff = DateTime.Now.AddDays(-keepDays);
                if (Directory.Exists(AppPaths.LogsDir))
                {
                    foreach (string file in Directory.GetFiles(AppPaths.LogsDir))
                    {
                        try
                        {
                            if (File.GetLastWriteTime(file) < cutoff && !string.Equals(file, AppPaths.LogFile, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Delete(file);
                            }
                        }
                        catch { }
                    }
                }
                string stats = null;
                try { stats = TaskStore.File_Stats; } catch { }
                if (!string.IsNullOrEmpty(stats) && File.Exists(stats))
                {
                    try
                    {
                        if (new FileInfo(stats).Length > 5L * 1024 * 1024)
                        {
                            string archive = Path.Combine(AppPaths.Root, "stats", "tasks-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".jsonl");
                            lock (Gate) { File.Move(stats, archive); }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("日志清理失败: " + Log.Describe(ex)); } catch { }
            }
        }
    }

    /// <summary>
    /// 任务记录仓库：每个任务结束追加一行到 stats/tasks.jsonl，并提供聚合统计。
    /// 与 sessions/*.json 并存——会话是聊天历史，这里是执行事实，便于按类别统计。
    /// </summary>
    public static class TaskStore
    {
        private static readonly object Gate = new object();

        public static string File_Stats
        {
            get
            {
                string dir = Path.Combine(AppPaths.Root, "stats");
                AppPaths.EnsureDirectory(dir);
                return Path.Combine(dir, "tasks.jsonl");
            }
        }

        public static void Record(TaskRecord task)
        {
            if (task == null || string.IsNullOrEmpty(task.TaskId)) return;
            try
            {
                // 直接序列化对象：属性名与 Json.Parse<TaskRecord> 的回读映射天然闭环。
                // 以前手写 snake_case 键（error_class/duration_ms/prompt_tokens…），JavaScriptSerializer
                // 按属性名回读时全部映射不上——ErrorClass/BudgetExceeded/耗时/token 永远是默认值，
                // 统计与告警（验收失败率/预算耗尽率）整体失效，而自检只断言了文件存在所以一直是绿的。
                // TaskId 属性名大小写不敏感可回读；老行的人工排查仍可按 "task"/"taskId" 别名找 id。
                string line = Json.Stringify(task);
                lock (Gate)
                {
                    File.AppendAllText(File_Stats, line + "\n", new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("任务记录写入失败（任务 " + (task == null ? "?" : task.TaskId) + "）: " + Log.Describe(ex)); } catch { }
            }
        }

        /// <summary>读取最近 max 条任务记录并聚合。文件不存在时返回空统计。</summary>
        public static TaskStatsSummary Summary(int max)
        {
            TaskStatsSummary summary = new TaskStatsSummary();
            List<TaskRecord> rows = Load(max);
            if (rows.Count == 0) return summary;

            List<long> durations = new List<long>();
            for (int i = 0; i < rows.Count; i++)
            {
                TaskRecord r = rows[i];
                summary.Total++;
                if (r.Outcome == TaskOutcome.Succeeded) summary.Succeeded++;
                else if (r.Outcome == TaskOutcome.Failed) summary.Failed++;
                else if (r.Outcome == TaskOutcome.Incomplete) summary.Incomplete++;
                else if (r.Outcome == TaskOutcome.Cancelled) summary.Cancelled++;
                else if (r.Outcome == TaskOutcome.Paused) summary.Paused++;
                else if (r.Outcome == TaskOutcome.WaitingApproval) summary.WaitingApproval++;
                if (r.BudgetExceeded) summary.BudgetExceeded++;
                if (r.ErrorClass == ErrorClass.VerificationFailed) summary.VerificationFailed++;
                summary.PromptTokens += r.PromptTokens;
                summary.CompletionTokens += r.CompletionTokens;
                summary.CachedTokens += r.CachedTokens;
                summary.TokensEstimated += r.TokensEstimated;
                durations.Add(r.DurationMs);
            }
            if (summary.PromptTokens > 0 || summary.CompletionTokens > 0)
            {
                summary.CostNote = "API 大脑实测 token（in=" + summary.PromptTokens +
                    (summary.CachedTokens > 0 ? "，缓存命中 " + summary.CachedTokens : "") +
                    " / out=" + summary.CompletionTokens + "）" +
                    (summary.TokensEstimated > 0 ? "；另有网页版估算 ≈" + summary.TokensEstimated + " token" : "");
            }
            else if (summary.TokensEstimated > 0)
            {
                summary.CostNote = "网页版估算 ≈" + summary.TokensEstimated + " token（按字符估算，非实测）";
            }
            durations.Sort();
            long total = 0;
            for (int i = 0; i < durations.Count; i++) total += durations[i];
            summary.AvgMs = durations.Count == 0 ? 0 : total / durations.Count;
            summary.P50Ms = Percentile(durations, 50);
            summary.P95Ms = Percentile(durations, 95);
            return summary;
        }

        private static long Percentile(List<long> sorted, int pct)
        {
            if (sorted.Count == 0) return 0;
            int idx = (int)Math.Round((pct / 100.0) * (sorted.Count - 1));
            if (idx < 0) idx = 0;
            if (idx >= sorted.Count) idx = sorted.Count - 1;
            return sorted[idx];
        }

        /// <summary>大小写不敏感取键：JavaScriptSerializer 保留原始大小写，别名字段新旧行写法不一致。</summary>
        private static string FindKeyInsensitive(Dictionary<string, object> obj, string key)
        {
            if (obj == null || string.IsNullOrEmpty(key)) return "";
            foreach (KeyValuePair<string, object> kv in obj)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value == null ? "" : Convert.ToString(kv.Value);
                }
            }
            return "";
        }

        public static List<TaskRecord> Load(int max)
        {
            List<TaskRecord> rows = new List<TaskRecord>();
            try
            {
                string file = File_Stats;
                if (!File.Exists(file)) return rows;
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                int start = Math.Max(0, lines.Length - Math.Max(1, max));
                for (int i = start; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    TaskRecord r = Json.Parse<TaskRecord>(lines[i]);
                    if (r == null) continue;
                    // id 从原始 JSON 里找（大小写不敏感）：新行是序列化对象（"TaskId"），
                    // 历史行手写过 "task"/"taskId" 别名——统一按不区分大小写取，保证同一个任务能被识别。
                    Dictionary<string, object> raw = Json.ParseObject(lines[i]);
                    string id = FindKeyInsensitive(raw, "taskId");
                    if (string.IsNullOrEmpty(id)) id = FindKeyInsensitive(raw, "task");
                    if (string.IsNullOrEmpty(id)) id = r.TaskId;
                    if (string.IsNullOrEmpty(id)) continue;
                    r.TaskId = id;
                    rows.Add(r);
                }
                // 同一 taskId 可能因为「续跑」多次落盘：只保留最后一次，否则成功率与耗时会被重复计数。
                if (rows.Count > 1)
                {
                    Dictionary<string, int> last = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int i = 0; i < rows.Count; i++) last[rows[i].TaskId] = i;
                    List<TaskRecord> deduped = new List<TaskRecord>();
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (last[rows[i].TaskId] == i) deduped.Add(rows[i]);
                    }
                    rows = deduped;
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("任务统计读取失败: " + Log.Describe(ex)); } catch { }
            }
            return rows;
        }
    }
}
