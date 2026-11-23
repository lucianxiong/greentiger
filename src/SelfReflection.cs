using System;
using System.Collections.Generic;
using System.Text;

namespace GreenTiger
{
    internal static class SelfReflection
    {
        internal static string Describe(Session session)
        {
            if (session == null) return "当前会话没有可回顾的执行记录。";
            List<Message> messages = session.Messages ?? new List<Message>();
            TaskRecord task = session.Task;
            long started = task == null ? 0 : task.StartedAt;
            long ended = task == null ? 0 : task.EndedAt;
            int lastUser = -1;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i] == null || messages[i].Role != "user") continue;
                Message reply = i + 1 < messages.Count ? messages[i + 1] : null;
                if (reply != null && reply.Role == "assistant" && !string.IsNullOrEmpty(reply.LocalAction)) continue;
                lastUser = i;
                break;
            }
            int local = messages.FindLastIndex(m => m != null && m.Role == "assistant"
                && (m.LocalAction == "doctor" || m.LocalAction == "repair"));

            if (local >= 0 && local > lastUser && messages[local].Time >= Math.Max(started, ended))
                return LocalReport(messages[local]);

            int first = Math.Max(0, lastUser + 1);
            if (task == null && lastUser < 0)
            {
                // 旧记录缺少轮次边界时，只回顾最近一条包含工具调用的消息。
                first = messages.FindLastIndex(m => m != null && m.Role == "assistant"
                    && m.ToolCalls != null && m.ToolCalls.Count > 0);
                if (first < 0) return "当前会话没有可回顾的执行记录。";
            }
            List<ToolCall> calls = new List<ToolCall>();
            for (int i = first; i < messages.Count; i++)
            {
                Message message = messages[i];
                if (message == null || message.Role != "assistant" || message.ToolCalls == null) continue;
                if (started > 0 && message.Time < started || ended > 0 && message.Time > ended) continue;
                foreach (ToolCall call in message.ToolCalls)
                    if (call != null) calls.Add(call);
            }
            string heading = task == null ? "最近一段会话记录（缺少任务终态）"
                : task.ResumeCount > 0 ? "最近一次续跑记录" : "最近一次任务记录";
            StringBuilder report = new StringBuilder();
            report.AppendLine(heading);
            if (task != null) report.AppendLine("任务状态：" + TaskOutcome.Describe(task.Outcome));
            if (calls.Count == 0) report.AppendLine("这段记录没有工具调用。");

            Dictionary<string, int> counts = new Dictionary<string, int>();
            List<string> labels = new List<string>();
            foreach (ToolCall call in calls)
            {
                string label = ToolName(call.Name) + " · " + ResultLabel(call);
                int count;
                if (!counts.TryGetValue(label, out count)) labels.Add(label);
                counts[label] = count + 1;
            }
            foreach (string label in labels)
                report.AppendLine("- " + label + (counts[label] > 1 ? " × " + counts[label] : ""));

            string verification = task == null ? "" : task.Verification ?? "";
            bool verified = verification.StartsWith("验收通过：", StringComparison.Ordinal)
                || verification.StartsWith("验收通过（", StringComparison.Ordinal);
            bool verificationFailed = verification.StartsWith("验收未通过", StringComparison.Ordinal)
                || verification.StartsWith("验证器异常", StringComparison.Ordinal);
            report.AppendLine(verified ? "已记录的验收条件通过。" : verificationFailed ? "验收未通过。" : "没有可确认验收通过的记录。");
            report.Append("下一步：" + NextStep(task, calls, verified, verificationFailed));
            return report.ToString();
        }

        private static string LocalReport(Message message)
        {
            bool doctor = message.LocalAction == "doctor";
            string summary = doctor ? "最近一次本地操作是检查绿虎的运行状态。" : "最近一次本地操作是运行绿虎的修复流程。";
            if (string.IsNullOrWhiteSpace(message.Text)) return summary + " 没有保存结果，建议重新检查。";
            return summary + Environment.NewLine + message.Text + Environment.NewLine
                + (doctor ? "下一步：界面资源缺失或为空时，输入 /repair 即可恢复。若故障来自源码，在绿虎源码工作目录输入 /repair 故障描述。"
                          : "下一步：用原来的操作复查故障现象，并对照本地报告确认修复范围。");
        }

        private static string ToolName(string name)
        {
            if (ToolRegistry.IsMcpTool(name)) return "MCP 工具";
            return ToolRegistry.IsRegistered(name) ? name : "未识别的工具";
        }

        private static string ResultLabel(ToolCall call)
        {
            switch (call.Status)
            {
                case "done": return "已完成";
                case "awaiting": return "待确认";
                case "pending": return "待执行";
                case "approved": return "已批准，未记录执行结果";
                case "denied": case "blocked": return "已拒绝，未执行";
                case "running": case "interrupted": return "已开始，结果未确认";
                case "error":
                    if (call.Attempts == 0 && call.StartedAt == 0)
                    {
                        if (call.ErrorCode == ToolErrorCode.Permission) return "已拒绝，未执行";
                        if (call.ErrorCode == ToolErrorCode.BadArgs || call.ErrorCode == ToolErrorCode.UnknownTool)
                            return "调用无效，未执行";
                        return "调用报错，执行情况未确认";
                    }
                    return "执行失败";
                default: return "执行情况未确认";
            }
        }

        private static string NextStep(TaskRecord task, List<ToolCall> calls, bool verified, bool verificationFailed)
        {
            string outcome = task == null ? "" : task.Outcome;
            if (outcome == TaskOutcome.WaitingApproval || calls.Exists(c => c.Status == "awaiting"))
                return "先处理待确认项，确认后再检查执行结果。";
            if (outcome == TaskOutcome.Cancelled) return "任务已经取消，重新执行前先核对已有改动。";
            if (outcome == TaskOutcome.Paused || task != null && task.Interrupted)
                return "恢复前先核对中断位置和已完成的操作。";
            if (outcome == TaskOutcome.Running) return "等待本轮结束，再核对终态和验收结果。";
            if (verificationFailed) return "根据未通过的验收条件修正结果，再用同一条件复查。";
            if (outcome == TaskOutcome.Succeeded && verified) return "本次已记录的验收条件已满足，可以结束。";
            if (outcome == TaskOutcome.Failed || outcome == TaskOutcome.Incomplete || calls.Exists(c => c.Status == "error"))
                return "检查失败或未完成的步骤，确认原因后再复查原始目标。";
            return "用原任务的交付物或操作结果核对是否达到目标。";
        }

        internal static void SelfTest(Action<string, bool, string> check)
        {
            Session sample = new Session();
            sample.Messages.Add(new Message { Role = "user", Time = 1 });
            sample.Messages.Add(new Message { Time = 2, ToolCalls = new List<ToolCall> {
                new ToolCall { Name = "write_file", Status = "done" } } });
            sample.Messages.Add(new Message { Role = "user", Time = 3 });
            sample.Messages.Add(new Message { Time = 4, ToolCalls = new List<ToolCall> {
                new ToolCall { Name = "shell", Status = "error", Attempts = 1, Command = "SECRET", Output = "SECRET" },
                new ToolCall { Name = "edit", Status = "awaiting" },
                new ToolCall { Name = "shell", Status = "error", ErrorCode = ToolErrorCode.Permission } } });
            string old = Describe(sample);
            check("reflect scoped legacy calls", old.Contains("执行失败") && old.Contains("待确认")
                && old.Contains("未执行") && !old.Contains("write_file") && !old.Contains("SECRET"), old);
            sample.Task = new TaskRecord { StartedAt = 3, EndedAt = 5, Outcome = TaskOutcome.Succeeded,
                Verification = "无可客观验证的验收条件（unverifiable）" };
            check("reflect unverified outcome", Describe(sample).Contains("没有可确认验收通过"), "");
            sample.Task.Verification = "验收通过（2/2）：已复查原始症状";
            check("reflect source repair verification", Describe(sample).Contains("已记录的验收条件通过"), "");
            sample.Messages.Add(new Message { LocalAction = "doctor", Time = 6, Text = "检查报告" });
            string local = Describe(sample);
            check("reflect latest local check", local.Contains("检查报告") && local.Contains("检查绿虎") && local.Contains("输入 /repair 即可恢复")
                && !local.Contains("执行失败"), local);
            sample.Messages.Add(new Message { LocalAction = "repair", Time = 7, Text = "已恢复 1 项资源" });
            string repair = Describe(sample);
            check("reflect latest local repair", repair.Contains("已恢复 1 项资源") && !repair.Contains("检查报告"), repair);
            sample.Messages.Add(new Message { Role = "user", Time = 8, Text = "你刚才做了什么" });
            sample.Messages.Add(new Message { LocalAction = "reflect", Time = 9, Text = repair });
            check("reflect twice keeps latest evidence", Describe(sample) == repair, "");
        }
    }
}
