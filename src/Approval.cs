using System;
using System.Collections.Generic;
using System.Text;

namespace GreenTiger
{
    /// <summary>审批票据状态。pending 待确认，granted 已批准待执行，denied 已拒绝，expired 过期，consumed 已使用。</summary>
    public static class ApprovalState
    {
        public const string Pending = "pending";
        public const string Granted = "granted";
        public const string Denied = "denied";
        public const string Expired = "expired";
        public const string Consumed = "consumed";
    }

    /// <summary>
    /// 审批票据：把「用户对某一次工具调用的批准」变成可持久化、可过期、只能使用一次的凭据。
    ///
    /// 解决的问题：审批原本只是一次阻塞式 await —— 用户点了「允许」之后如果进程被杀，
    /// 这个决定就彻底消失了，任务只能标记为等待审批再让用户重来一遍（而且还不知道当初批的是哪条命令）。
    /// 有了票据：决定先落盘再执行；崩溃重启后，同一工具 + 同一参数可以凭票据执行一次，
    /// 参数一变就匹配不上，必须重新确认。
    /// </summary>
    public class ApprovalTicket
    {
        public string Id { get; set; }
        public string TaskId { get; set; }
        public string SessionId { get; set; }
        /// <summary>工具名。</summary>
        public string Tool { get; set; }
        /// <summary>参数规范化后的指纹：只认「同一条命令」，改一个字符都不算同一条。</summary>
        public string ArgsHash { get; set; }
        public string Risk { get; set; }
        public string Status { get; set; }
        /// <summary>用户决定原文：allow / always / deny（保留 always 便于审计「本次始终允许」）。</summary>
        public string Decision { get; set; }
        public long CreatedAt { get; set; }
        public long DecidedAt { get; set; }
        /// <summary>票据有效期截止（毫秒时间戳）。过期的批准不能再用。</summary>
        public long ExpiresAt { get; set; }
        public string Note { get; set; }

        public ApprovalTicket()
        {
            Id = "ap_" + Guid.NewGuid().ToString("N").Substring(0, 10);
            TaskId = "";
            SessionId = "";
            Tool = "";
            ArgsHash = "";
            Risk = "safe";
            Status = ApprovalState.Pending;
            Decision = "";
            Note = "";
        }

        public bool IsLive(long nowMs)
        {
            return Status == ApprovalState.Granted && (ExpiresAt <= 0 || nowMs <= ExpiresAt);
        }

        /// <summary>这批批准是否针对这次调用：工具同名且参数指纹一致。</summary>
        public bool Matches(ToolCall call)
        {
            if (call == null) return false;
            if (!string.Equals(Tool, call.Name, StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(ArgsHash, ApprovalStore.HashArgs(call.ArgsJson), StringComparison.Ordinal);
        }

        public string Describe()
        {
            return Tool + "（" + Status + (Risk == "danger" ? "，高风险" : "") + "）";
        }
    }

    /// <summary>
    /// 票据仓库。票据跟随会话持久化（data/sessions/*.json），不需要数据库。
    /// 所有方法都对 null / 旧会话缺字段做兜底，保证旧会话文件能直接读。
    /// </summary>
    public static class ApprovalStore
    {
        /// <summary>默认有效期：与审批弹窗的 10 分钟等待超时保持一致。</summary>
        public const int DefaultTtlSeconds = 600;

        /// <summary>每个会话保留的票据上限，避免会话文件随长任务无限增长。</summary>
        public const int MaxTickets = 20;

        private static int Ttl(int ttlSeconds)
        {
            return ttlSeconds > 0 ? ttlSeconds : DefaultTtlSeconds;
        }

        public static List<ApprovalTicket> List(Session session)        {
            if (session == null) return new List<ApprovalTicket>();
            if (session.Approvals == null) session.Approvals = new List<ApprovalTicket>();
            return session.Approvals;
        }

        /// <summary>
        /// 参数指纹：解析成对象后按键排序重建字符串，再取稳定哈希。
        /// 目的是让「同样的调用写法略有不同（键序、空白）」仍能匹配上，
        /// 而「命令内容不同」一定匹配不上。
        /// </summary>
        public static string HashArgs(string argsJson)
        {
            string canonical = Canonical(argsJson);
            uint hash = 2166136261;
            for (int i = 0; i < canonical.Length; i++)
            {
                hash ^= canonical[i];
                hash *= 16777619;
            }
            return "h" + hash.ToString("x8");
        }

        private static string Canonical(string argsJson)
        {
            if (string.IsNullOrEmpty(argsJson)) return "";
            Dictionary<string, object> obj = Json.ParseObject(argsJson);
            if (obj == null) return argsJson.Trim();
            List<string> keys = new List<string>(obj.Keys);
            keys.Sort(StringComparer.Ordinal);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                sb.Append(keys[i]);
                sb.Append('=');
                sb.Append(CanonicalValue(obj[keys[i]]));
                sb.Append(';');
            }
            return sb.ToString();
        }

        private static string CanonicalValue(object value)
        {
            if (value == null) return "";
            Dictionary<string, object> nested = value as Dictionary<string, object>;
            if (nested != null) return Canonical(Json.Stringify(nested));
            List<string> list = value as List<string>;
            if (list != null)
            {
                List<string> sorted = new List<string>(list);
                sorted.Sort(StringComparer.Ordinal);
                return string.Join(",", sorted.ToArray());
            }
            return Convert.ToString(value);
        }

        /// <summary>
        /// 登记一次审批请求。同一任务里对同一工具同一参数重复请求时复用未决票据，
        /// 避免模型反复输出同一调用把票据表撑爆。
        /// </summary>
        public static ApprovalTicket Request(Session session, string taskId, ToolCall call, int ttlSeconds)
        {
            List<ApprovalTicket> list = List(session);
            string hash = HashArgs(call == null ? "" : call.ArgsJson);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                ApprovalTicket t = list[i];
                if (t == null) continue;
                if (t.Status != ApprovalState.Pending) continue;
                if (!SameTask(t.TaskId, taskId)) continue;
                if (string.Equals(t.Tool, call.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.ArgsHash, hash, StringComparison.Ordinal))
                {
                    return t;
                }
            }
            ApprovalTicket ticket = new ApprovalTicket();
            ticket.TaskId = taskId == null ? "" : taskId;
            ticket.SessionId = session == null ? "" : session.Id;
            ticket.Tool = call == null ? "" : call.Name;
            ticket.ArgsHash = hash;
            ticket.Risk = call == null ? "safe" : call.Risk;
            ticket.CreatedAt = Message.Now();
            ticket.ExpiresAt = ticket.CreatedAt + Ttl(ttlSeconds) * 1000L;
            list.Add(ticket);
            Prune(session, MaxTickets);
            return ticket;
        }

        /// <summary>落用户决定。deny → denied；allow/always → granted（待执行，一次性）。</summary>
        public static void Decide(Session session, ApprovalTicket ticket, string decision)
        {
            if (ticket == null) return;
            string d = decision == null ? "" : decision.Trim().ToLowerInvariant();
            ticket.Decision = d;
            ticket.DecidedAt = Message.Now();
            if (d == "allow" || d == "always")
            {
                ticket.Status = ApprovalState.Granted;
                ticket.Note = d == "always" ? "本次始终允许" : "用户允许一次";
            }
            else
            {
                ticket.Status = ApprovalState.Denied;
                ticket.Note = "用户拒绝或等待超时";
            }
        }

        /// <summary>执行完毕（无论成败）后标记已使用：一张批准只能换一次执行。</summary>
        public static void Consume(ApprovalTicket ticket)
        {
            if (ticket == null) return;
            if (ticket.Status != ApprovalState.Granted) return;
            ticket.Status = ApprovalState.Consumed;
            ticket.Note = "已执行，票据失效";
        }

        /// <summary>
        /// 找出针对这次调用仍然有效的批准并放行。命中后不立即消费 ——
        /// 只有真的执行完才消费，这样「已批准但执行前崩溃」不会白丢一次批准。
        /// 调用方必须先过权限规则与执行策略检查，票据不能绕过它们。
        /// </summary>
        public static bool TryAuthorize(Session session, ToolCall call, out ApprovalTicket ticket)
        {
            return TryAuthorize(session, call, null, out ticket);
        }

        /// <summary>
        /// 同上，但把批准限定在「同一个任务」内。
        /// 为什么：票据在会话里存活，如果只按「工具 + 参数」匹配，任务 A 里批过的危险命令
        /// 会在有效期内自动放行任务 B 里一模一样的命令 —— 用户批的是「当时那件事」，不是这条命令永久通行证。
        /// 续跑复用同一个 taskId，所以恢复路径不受影响。
        /// </summary>
        public static bool TryAuthorize(Session session, ToolCall call, string taskId, out ApprovalTicket ticket)
        {
            ticket = null;
            List<ApprovalTicket> list = List(session);
            long now = Message.Now();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                ApprovalTicket t = list[i];
                if (t == null || !t.IsLive(now)) continue;
                if (!t.Matches(call)) continue;
                if (!SameTask(t.TaskId, taskId)) continue;
                ticket = t;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 票据是否属于这个任务。任一侧为空（旧票据 / 调用方未指定）时不启用任务维度，
        /// 保持与历史会话文件的兼容，但真实路径两边都会带上 taskId。
        /// </summary>
        private static bool SameTask(string ticketTaskId, string taskId)
        {
            if (string.IsNullOrEmpty(ticketTaskId) || string.IsNullOrEmpty(taskId)) return true;
            return string.Equals(ticketTaskId, taskId, StringComparison.Ordinal);
        }

        /// <summary>把过期的待决/已批准票据标记为过期，返回处理数量。启动与每步审批前调用。</summary>
        public static int Expire(Session session, int ttlSeconds)
        {
            List<ApprovalTicket> list = List(session);
            long now = Message.Now();
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                ApprovalTicket t = list[i];
                if (t == null) continue;
                if (t.Status != ApprovalState.Pending && t.Status != ApprovalState.Granted) continue;
                long deadline = t.ExpiresAt > 0 ? t.ExpiresAt : t.CreatedAt + Ttl(ttlSeconds) * 1000L;
                if (now <= deadline) continue;
                string was = t.Status;
                t.Status = ApprovalState.Expired;
                t.Note = "票据已过期（" + was + "→expired）";
                n++;
            }
            return n;
        }

        public static ApprovalTicket Pending(Session session)
        {
            List<ApprovalTicket> list = List(session);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] != null && list[i].Status == ApprovalState.Pending) return list[i];
            }
            return null;
        }

        public static ApprovalTicket Granted(Session session)
        {
            List<ApprovalTicket> list = List(session);
            long now = Message.Now();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] != null && list[i].IsLive(now)) return list[i];
            }
            return null;
        }

        /// <summary>
        /// 任务能否继续执行。规则刻意保守：
        /// 只有「等待审批」和「中断」可以续；已完成/已取消/失败的任务不允许借续跑复活。
        /// </summary>
        public static bool CanResume(Session session, out string reason)
        {
            reason = "";
            if (session == null)
            {
                reason = "没有活动会话";
                return false;
            }
            TaskRecord task = session.Task;
            if (task == null)
            {
                reason = "该会话没有可恢复的任务记录";
                return false;
            }
            if (task.Outcome == TaskOutcome.Succeeded ||
                task.Outcome == TaskOutcome.Cancelled ||
                task.Outcome == TaskOutcome.Failed)
            {
                reason = "任务已结束（" + task.Outcome + "），不能继续；请新建会话重新执行。";
                return false;
            }
            if (task.Outcome == TaskOutcome.WaitingApproval || task.Outcome == TaskOutcome.Incomplete ||
                task.Outcome == TaskOutcome.Paused)
            {
                return true;
            }
            reason = "任务当前状态为 " + task.Outcome + "，不能继续。";
            return false;
        }

        /// <summary>票据表裁剪：只保留最近 max 条（越旧越先丢，但已决定的历史也保留一部分供审计）。</summary>
        public static void Prune(Session session, int max)
        {
            List<ApprovalTicket> list = List(session);
            if (list.Count <= max) return;
            list.RemoveRange(0, list.Count - max);
        }
    }

    /// <summary>
    /// 恢复续跑的任务书：把「上次断在哪、批的是什么、还差什么」写清楚，
    /// 但绝不代替模型做决定，也不跳过验收。
    /// </summary>
    public static class ResumeBrief
    {
        public static string Build(Session session, TaskRecord task)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("【继续任务】上次任务在执行过程中被中断（" + (task == null ? "" : task.ErrorClass) + "），现在从断点继续。");
            sb.AppendLine("要求：不要重复已经成功完成的动作；先核验当前状态，再决定下一步；仍然遵守全部权限规则与验收要求。");
            if (task != null && !string.IsNullOrEmpty(task.ErrorDetail))
            {
                sb.AppendLine("中断原因：" + task.ErrorDetail);
            }

            ApprovalTicket pending = ApprovalStore.Pending(session);
            ApprovalTicket granted = ApprovalStore.Granted(session);
            if (granted != null)
            {
                sb.AppendLine("审批状态：用户此前已批准「" + granted.Describe() + "」，该批准仅对完全相同的调用有效，且只能使用一次。");
            }
            else if (pending != null)
            {
                sb.AppendLine("审批状态：此前有一次调用在等你确认（" + pending.Describe() + "），已过期或需要重新确认。");
            }
            else
            {
                sb.AppendLine("审批状态：没有未决的审批票据。");
            }

            TaskState state = session == null ? null : session.TaskState;
            if (state != null)
            {
                string anchor = state.Render(null, null, 2000);
                if (!string.IsNullOrEmpty(anchor))
                {
                    sb.AppendLine();
                    sb.AppendLine(anchor);
                }
            }

            List<ToolCall> recent = RecentCalls(session, 6);
            if (recent.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("【最近执行过的动作】");
                for (int i = 0; i < recent.Count; i++)
                {
                    ToolCall c = recent[i];
                    sb.AppendLine("- " + c.Name + " [" + c.Status + (string.IsNullOrEmpty(c.ErrorCode) ? "" : "/" + c.ErrorCode) + "] " +
                                  Util_Shorten(c.ArgsJson, 120));
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static List<ToolCall> RecentCalls(Session session, int max)
        {
            List<ToolCall> all = new List<ToolCall>();
            if (session == null || session.Messages == null) return all;
            for (int i = session.Messages.Count - 1; i >= 0 && all.Count < max; i--)
            {
                List<ToolCall> calls = session.Messages[i].ToolCalls;
                if (calls == null) continue;
                for (int j = calls.Count - 1; j >= 0 && all.Count < max; j--)
                {
                    if (calls[j] != null) all.Add(calls[j]);
                }
            }
            all.Reverse();
            return all;
        }

        private static string Util_Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string flat = text.Replace("\r", " ").Replace("\n", " ");
            if (flat.Length <= max) return flat;
            return flat.Substring(0, max) + "…";
        }
    }
}
