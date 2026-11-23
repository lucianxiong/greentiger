using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GreenTiger
{
    /// <summary>一条验收条件：可机读、可追溯、能对应到具体证据。</summary>
    public class AcceptanceCriterion
    {
        public string Id { get; set; }
        /// <summary>file_exists / claimed_path / finish_summary。</summary>
        public string Kind { get; set; }
        public string Target { get; set; }
        public string Detail { get; set; }
        public bool Required { get; set; }

        public AcceptanceCriterion()
        {
            Id = "c_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Kind = "";
            Target = "";
            Detail = "";
            Required = true;
        }
    }

    public class VerificationCheck
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Target { get; set; }
        public string Detail { get; set; }
        public bool Required { get; set; }
        public bool Passed { get; set; }

        public VerificationCheck()
        {
            Id = "";
            Kind = "";
            Target = "";
            Detail = "";
            Required = true;
            Passed = false;
        }
    }

    public class VerificationReport
    {
        public bool Passed { get; set; }
        public int RequiredTotal { get; set; }
        public int RequiredFailed { get; set; }
        public bool Unverifiable { get; set; }
        public string Summary { get; set; }
        public List<VerificationCheck> Checks { get; set; }

        public VerificationReport()
        {
            Passed = true;
            Summary = "";
            Checks = new List<VerificationCheck>();
        }

        /// <summary>给模型看的失败原因（只列失败项，避免噪声）。</summary>
        public string FailureText()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Checks.Count; i++)
            {
                VerificationCheck c = Checks[i];
                if (c.Passed) continue;
                sb.AppendLine("- " + c.Detail + (c.Required ? "（必需）" : "（参考）"));
            }
            return sb.ToString().TrimEnd();
        }

    }

    /// <summary>
    /// 独立验收器：任务是否成功由「可核验的证据」决定，而不是模型自己说完成。
    /// 只做能客观判定的检查（文件是否存在、声明是否为空），无法客观验证时如实标记 unverifiable，
    /// 不假装通过，也不凭猜测判失败。
    /// </summary>
    public static class Verifier
    {
        private static readonly Regex AbsPath = new Regex(@"[A-Za-z]:\\[^\s""'<>|，。；、）(]*", RegexOptions.Compiled);
        private static readonly Regex RelFile = new Regex(@"(?:^|[\s（(【])([A-Za-z0-9_\-\.]+(?:[\\/][A-Za-z0-9_\-\.]+)*\.(?:md|txt|json|csv|html|htm|png|jpg|jpeg|py|js|ts|cs|ps1|docx|xlsx|pptx|pdf|zip|log|yaml|yml))\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] ClaimWords = new string[]
        {
            "已保存", "已生成", "已创建", "已写入", "已导出", "保存到", "保存在", "输出到", "写入到", "生成在"
        };

        /// <summary>
        /// 从「本次任务实际做过的调用 + 最终回答」推导验收条件。
        /// 原则：宁少不多。只有能明确指向一个文件/路径的，才作为必需条件。
        /// </summary>
        public static List<AcceptanceCriterion> Build(string workDir, IList<ToolCall> taskCalls, string finalAnswer)
        {
            List<AcceptanceCriterion> criteria = new List<AcceptanceCriterion>();
            if (taskCalls == null) taskCalls = new List<ToolCall>();

            // 1) 成功的 write_file：它声称写过的文件必须真实存在（除非后续操作又移动/删除了它）
            for (int i = 0; i < taskCalls.Count; i++)
            {
                ToolCall call = taskCalls[i];
                if (call == null || !string.Equals(call.Name, "write_file", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(call.Status, "done", StringComparison.Ordinal)) continue;
                Dictionary<string, object> args = Json.ParseObject(call.ArgsJson);
                string path = Json.Str(args, "path", "");
                if (path.Length == 0) continue;
                string full;
                try { full = Path.GetFullPath(Fs.Resolve(workDir, path)); }
                catch { continue; }
                if (!Fs.IsInside(workDir, full)) continue;
                if (Superseded(taskCalls, i, Path.GetFileName(full))) continue;

                AcceptanceCriterion c = new AcceptanceCriterion();
                c.Kind = "file_exists";
                c.Target = full;
                c.Detail = "write_file 声称已写入 " + full;
                c.Required = true;
                criteria.Add(c);
            }

            // 2) 最终回答里「声称已保存/已生成」的路径必须存在。
            // 只看含声称词的句子——以前对全文提取：回答里顺带提到的 template.md（还没建）
            // 也会变成必需条件，制造假失败（「报告已保存到 out/a.md。模板可参考 template.md」→ 必须存在 template.md）。
            if (!string.IsNullOrEmpty(finalAnswer))
            {
                List<string> claimed = new List<string>();
                foreach (string sentence in Sentences(finalAnswer))
                {
                    if (!ClaimsAction(sentence)) continue;
                    MatchCollection abs = AbsPath.Matches(sentence);
                    for (int i = 0; i < abs.Count; i++)
                    {
                        string p = abs[i].Value.Trim().TrimEnd('.', ',', ';', ':', '）', ')');
                        if (p.Length < 4) continue;
                        if (!claimed.Contains(p)) claimed.Add(p);
                    }
                    MatchCollection rel = RelFile.Matches(sentence);
                    for (int i = 0; i < rel.Count; i++)
                    {
                        string p = rel[i].Groups[1].Value.Trim();
                        if (p.Length < 3) continue;
                        if (p.IndexOf("://", StringComparison.Ordinal) >= 0) continue;
                        if (!claimed.Contains(p)) claimed.Add(p);
                    }
                }
                for (int i = 0; i < claimed.Count; i++)
                {
                    string p = claimed[i];
                    string full;
                    try
                    {
                        full = Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Fs.Resolve(workDir, p));
                    }
                    catch { continue; }
                    // 只对工作目录内的路径做判定：目录外的存在性检查可能误伤（例如用户自己的文档）
                    if (!Fs.IsInside(workDir, full)) continue;
                    AcceptanceCriterion c = new AcceptanceCriterion();
                    c.Kind = "claimed_path";
                    c.Target = full;
                    c.Detail = "回答声称已生成 " + full;
                    c.Required = true;
                    criteria.Add(c);
                }
            }

            return criteria;
        }

        /// <summary>把回答拆成句（。！？!?与换行）。声称词是句级语义，全文扫描会把「顺带提到的文件」都当必需条件。</summary>
        private static IEnumerable<string> Sentences(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            string[] parts = Regex.Split(text, @"(?<=[。！？!?])\s*|\r?\n");
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i];
                if (!string.IsNullOrWhiteSpace(s)) yield return s;
            }
        }

        /// <summary>
        /// 后续调用里「以删除/移动/改名的方式处理过这个文件」，就不再要求它仍然存在。
        /// 只认删除类动词的 shell 命令——以前 read_file / list_dir 提到过文件名也算 supersede，
        /// 文件被误删时验收照样通过，产生假成功。
        /// </summary>
        private static bool Superseded(IList<ToolCall> calls, int fromIndex, string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            for (int i = fromIndex + 1; i < calls.Count; i++)
            {
                ToolCall later = calls[i];
                if (later == null || !string.Equals(later.Status, "done", StringComparison.Ordinal)) continue;
                string text = (later.ArgsJson == null ? "" : later.ArgsJson) + " " + (later.Command == null ? "" : later.Command);
                if (text.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string lower = text.ToLowerInvariant();
                if (Regex.IsMatch(lower, @"\b(del|erase|remove-item|rd|rmdir|rm|move-item|move|rename-item|ren|clear-content)\b"))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ClaimsAction(string answer)
        {
            for (int i = 0; i < ClaimWords.Length; i++)
            {
                if (answer.IndexOf(ClaimWords[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>执行验收。验证器自身异常一律视为「未通过」，绝不因为验证器坏了就判成功。</summary>
        public static VerificationReport Verify(IList<AcceptanceCriterion> criteria, string workDir, string finishSummary, bool usedFinish)
        {
            VerificationReport report = new VerificationReport();
            try
            {
                bool finishSummaryMissing = usedFinish && (finishSummary == null || finishSummary.Trim().Length == 0);

                if (criteria == null || criteria.Count == 0)
                {
                    if (finishSummaryMissing)
                    {
                        report.Passed = false;
                        report.RequiredTotal = 1;
                        report.RequiredFailed = 1;
                        VerificationCheck c0 = new VerificationCheck();
                        c0.Kind = "finish_summary";
                        c0.Target = "finish";
                        c0.Detail = "finish 的 summary 为空，无法说明完成了什么";
                        c0.Required = true;
                        c0.Passed = false;
                        report.Checks.Add(c0);
                        report.Summary = "验收未通过：1/1 项必需条件失败";
                        return report;
                    }
                    report.Unverifiable = true;
                    report.Passed = true;
                    report.Summary = "无可客观验证的验收条件（unverifiable）：本任务未产生可核验的文件证据。";
                    return report;
                }

                for (int i = 0; i < criteria.Count; i++)
                {
                    AcceptanceCriterion c = criteria[i];
                    VerificationCheck check = new VerificationCheck();
                    check.Id = c.Id;
                    check.Kind = c.Kind;
                    check.Target = c.Target;
                    check.Detail = c.Detail;
                    check.Required = c.Required;

                    if (c.Kind == "file_exists" || c.Kind == "claimed_path")
                    {
                        check.Passed = File.Exists(c.Target) || Directory.Exists(c.Target);
                        if (!check.Passed)
                        {
                            check.Detail = c.Detail + "，但该路径不存在";
                        }
                    }
                    else
                    {
                        // 未知条件类型不参与判定，标记为参考项
                        check.Required = false;
                        check.Passed = true;
                    }

                    if (check.Required)
                    {
                        report.RequiredTotal++;
                        if (!check.Passed) report.RequiredFailed++;
                    }
                    report.Checks.Add(check);
                }

                if (finishSummaryMissing)
                {
                    VerificationCheck c2 = new VerificationCheck();
                    c2.Kind = "finish_summary";
                    c2.Target = "finish";
                    c2.Detail = "finish 的 summary 为空，无法说明完成了什么";
                    c2.Required = true;
                    c2.Passed = false;
                    report.RequiredTotal++;
                    report.RequiredFailed++;
                    report.Checks.Add(c2);
                }

                report.Passed = report.RequiredFailed == 0;
                report.Summary = report.Passed
                    ? "验收通过：" + report.RequiredTotal + " 项必需条件全部满足"
                    : "验收未通过：" + report.RequiredFailed + "/" + report.RequiredTotal + " 项必需条件失败";
            }
            catch (Exception ex)
            {
                report.Passed = false;
                report.Summary = "验证器异常（按未通过处理）：" + ex.Message;
                report.RequiredFailed++;
                report.RequiredTotal++;
            }
            return report;
        }
    }
}
