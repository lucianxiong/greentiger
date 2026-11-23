using System;
using System.Collections.Generic;
using System.Text;

namespace GreenTiger
{
    /// <summary>把会话导出成可读的 Markdown，方便存档、贴进 issue 或交给别的模型继续。</summary>
    public static class SessionExport
    {
        private static string Time(long ms)
        {
            return TimeUtil.FromUnix(ms, "yyyy-MM-dd HH:mm:ss");
        }

        private static string Fence(string body, string lang)
        {
            string fence = "```";
            if (body != null && body.IndexOf("```", StringComparison.Ordinal) >= 0) fence = "````";
            return fence + (lang == null ? "" : lang) + "\n" + (body == null ? "" : body.TrimEnd()) + "\n" + fence + "\n";
        }

        private static string RoleLabel(Message m)
        {
            if (m.Role == "user") return "你";
            if (m.Role == "system") return "系统";
            return AppInfo.Name;
        }

        public static string ToMarkdown(Session session)
        {
            if (session == null) return "";
            if (session.Messages == null) session.Messages = new List<Message>();
            StringBuilder sb = new StringBuilder();
            sb.Append("# ").Append(string.IsNullOrEmpty(session.Title) ? "会话" : session.Title).Append("\n\n");
            sb.Append("- 会话 ID：").Append(session.Id).Append("\n");
            if (!string.IsNullOrEmpty(session.WorkDir)) sb.Append("- 工作目录：").Append(session.WorkDir).Append("\n");
            sb.Append("- 创建时间：").Append(Time(session.CreatedAt)).Append("\n");
            sb.Append("- 最后更新：").Append(Time(session.UpdatedAt)).Append("\n");
            sb.Append("- 统计：").Append(session.Messages.Count).Append(" 条消息，")
              .Append(session.ToolCount).Append(" 次工具调用，")
              .Append(session.TurnCount).Append(" 轮对话\n\n");
            sb.Append("---\n\n");
            for (int i = 0; i < session.Messages.Count; i++)
            {
                Message m = session.Messages[i];
                sb.Append("## ").Append(RoleLabel(m)).Append(" · ").Append(Time(m.Time));
                if (m.Status == "error") sb.Append("（失败）");
                sb.Append("\n\n");
                string text = m.Text == null ? "" : m.Text.Trim();
                if (text.Length > 0) sb.Append(text).Append("\n\n");
                if (m.ToolCalls != null)
                {
                    for (int j = 0; j < m.ToolCalls.Count; j++)
                    {
                        ToolCall call = m.ToolCalls[j];
                        sb.Append("### 工具 · ").Append(call.Name).Append("（").Append(StatusText(call.Status)).Append("）\n\n");
                        List<string> meta = new List<string>();
                        if (!string.IsNullOrEmpty(call.Risk) && call.Risk != "safe") meta.Add("风险：" + call.Risk);
                        if (call.DurationMs > 0) meta.Add("耗时：" + TimeUtil.Duration(call.DurationMs));
                        if (call.Status == "done" || call.Status == "error") meta.Add("exit=" + call.ExitCode);
                        if (!string.IsNullOrEmpty(call.ApprovalStatus)) meta.Add("确认：" + call.ApprovalStatus);
                        if (meta.Count > 0) sb.Append(string.Join(" · ", meta.ToArray())).Append("\n\n");
                        string body = string.IsNullOrEmpty(call.Command) ? call.ArgsJson : call.Command;
                        if (!string.IsNullOrEmpty(body))
                        {
                            sb.Append(Fence(body, call.Name == "shell" ? "powershell" : "json"));
                            sb.Append("\n");
                        }
                        if (!string.IsNullOrEmpty(call.Output))
                        {
                            sb.Append("输出：\n\n").Append(Fence(TextUtil.Truncate(call.Output, 4000), "text")).Append("\n");
                        }
                        if (call.Artifacts != null && call.Artifacts.Count > 0)
                        {
                            sb.Append("附件：");
                            for (int k = 0; k < call.Artifacts.Count; k++)
                            {
                                if (k > 0) sb.Append("、");
                                sb.Append(call.Artifacts[k].Name).Append("（").Append(Fs.HumanSize(call.Artifacts[k].Size)).Append("）");
                            }
                            sb.Append("\n\n");
                        }
                    }
                }
            }
            sb.Append("---\n\n由绿虎（GreenTiger）").Append(AppInfo.Version).Append(" 导出。\n");
            return sb.ToString();
        }

        private static string StatusText(string status)
        {
            if (status == "done") return "完成";
            if (status == "error") return "失败";
            if (status == "denied") return "已拒绝";
            if (status == "running") return "运行中";
            if (status == "awaiting") return "等待确认";
            if (status == "pending") return "待执行";
            return status == null ? "未知" : status;
        }

        public static string FileName(Session session)
        {
            string baseName = session == null || string.IsNullOrEmpty(session.Title) ? "greentiger-session" : session.Title;
            string safe = Fs.SafeName(TextUtil.CleanTitle(baseName, 50));
            if (safe.Length == 0 || safe == "file") safe = "greentiger-session";
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            return safe + "-" + stamp + ".md";
        }
    }
}
