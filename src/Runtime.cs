using System;
using System.Collections.Generic;
using System.Text;

namespace GreenTiger
{
    /// <summary>
    /// 任务终态。与旧的中文 phase 字符串并存：phase 给界面看，outcome 给程序看。
    /// 只有这四个（加 waiting_approval）是允许出现在任务记录里的结束状态。
    /// </summary>
    public static class TaskOutcome
    {
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
        public const string Incomplete = "incomplete";
        public const string WaitingApproval = "waiting_approval";
        /// <summary>用户主动暂停：不是终态，可续跑（与 cancelled 的区别就在这里）。</summary>
        public const string Paused = "paused";
        /// <summary>任务进行中（仅用于持久化的任务记录，不是终态）。</summary>
        public const string Running = "running";

        public static bool IsTerminal(string outcome)
        {
            return outcome == Succeeded || outcome == Failed || outcome == Cancelled || outcome == Incomplete;
        }

        public static string Describe(string outcome)
        {
            if (outcome == Succeeded) return "已完成";
            if (outcome == Failed) return "失败";
            if (outcome == Cancelled) return "已取消";
            if (outcome == Incomplete) return "未完成";
            if (outcome == WaitingApproval) return "等待审批";
            if (outcome == Paused) return "已暂停";
            if (outcome == Running) return "进行中";
            return "未开始";
        }
    }

    /// <summary>
    /// 失败归因。区分「模型输出错」「工具错」「权限拒绝」「预算耗尽」「验收未过」「内部异常」，
    /// 这样统计和恢复才能按类别处理，而不是笼统一个 failed。
    /// </summary>
    public static class ErrorClass
    {
        public const string None = "";
        /// <summary>工具执行本身失败（超时、崩溃、远端错误）。</summary>
        public const string ToolError = "tool_error";
        /// <summary>被护栏或权限规则拒绝、用户拒绝。</summary>
        public const string PermissionDenied = "permission_denied";
        /// <summary>步数/耗时/重试/修复预算耗尽。</summary>
        public const string BudgetExceeded = "budget_exceeded";
        /// <summary>独立验收未通过（模型声称完成但证据不足）。</summary>
        public const string VerificationFailed = "verification_failed";
        public const string Internal = "internal_error";
        public const string Cancelled = "cancelled";
    }

    /// <summary>工具层的稳定错误码。模型看到的是码 + 是否可重试，而不是一段自由文本。</summary>
    public static class ToolErrorCode
    {
        public const string Ok = "ok";
        public const string Timeout = "timeout";
        public const string BadArgs = "bad_args";
        public const string NotFound = "not_found";
        public const string Permission = "permission_denied";
        public const string Unavailable = "unavailable";
        public const string ToolError = "tool_error";
        public const string UnknownTool = "unknown_tool";
        public const string Interrupted = "interrupted";
        public const string Budget = "budget_exceeded";
    }

    /// <summary>
    /// 任务预算：步数 + 总耗时 + 修复次数 + 工具重试次数。
    /// 所有预算集中在一处判断，避免「只限步数、模型慢慢磨」或「重试无限」。
    /// </summary>
    public class TaskBudget
    {
        public int MaxSteps { get; set; }
        public int MaxSeconds { get; set; }
        public int MaxRepairs { get; set; }
        public int MaxRetries { get; set; }
        /// <summary>token 预算上限（输入+输出累计），0=不限。只有回传用量的通道才可能触发。</summary>
        public int MaxTokens { get; set; }

        public int Steps { get; set; }
        public int Repairs { get; set; }
        public int Retries { get; set; }
        /// <summary>本任务已消耗的 token 总量（含压缩调用）。0 表示通道不上报用量，不能当成「没花钱」。</summary>
        public long Tokens { get; set; }
        public long StartedAtMs { get; set; }

        public TaskBudget()
        {
            MaxSteps = 40;
            MaxSeconds = 1800;
            MaxRepairs = 2;
            MaxRetries = 5;
            MaxTokens = 0;
            StartedAtMs = Message.Now();
        }

        public static TaskBudget Create(Config config)
        {
            TaskBudget b = new TaskBudget();
            if (config != null)
            {
                b.MaxSteps = config.MaxSteps;
                b.MaxSeconds = config.TaskTimeoutSeconds;
                b.MaxRepairs = config.MaxRepairs;
                b.MaxRetries = config.MaxToolRetries;
                b.MaxTokens = config.MaxTaskTokens;
                // 推理强度不只写在提示词里，它也决定「有没有空间把话说清楚」：
                // 高档要多比较方案、多做交叉检查，40 步经常在中途被预算掐断；
                // 只加不减（low 不缩短任务能力），上限仍是配置里的 120 步。
                string eff = config.ReasoningEffort ?? "medium";
                if (eff == "high")
                {
                    b.MaxSteps = (int)Math.Min(120, Math.Round(b.MaxSteps * 1.5));
                    b.MaxRepairs = b.MaxRepairs + 1;
                }
                else if (eff == "xhigh")
                {
                    b.MaxSteps = Math.Min(120, b.MaxSteps * 2);
                    b.MaxRepairs = b.MaxRepairs + 2;
                }
            }
            if (b.MaxSteps < 1) b.MaxSteps = 1;
            if (b.MaxSeconds < 30) b.MaxSeconds = 30;
            if (b.MaxRepairs < 0) b.MaxRepairs = 0;
            if (b.MaxRetries < 0) b.MaxRetries = 0;
            b.StartedAtMs = Message.Now();
            return b;
        }

        public long ElapsedMs
        {
            get { return Math.Max(0, Message.Now() - StartedAtMs); }
        }

        /// <summary>
        /// 续跑用的预算：沿用原任务的步数/修复/重试计数（预算约束不因续跑被重置），
        /// 但耗时按本次续跑重新计时——否则「审批搁置一夜」会把总耗时预算直接耗光。
        /// </summary>
        public static TaskBudget Resume(Config config, TaskRecord task)
        {
            TaskBudget b = Create(config);
            if (task != null)
            {
                b.Steps = Math.Max(0, task.Steps);
                b.Repairs = Math.Max(0, task.Repairs);
                b.Retries = Math.Max(0, task.Retries);
                if (b.Steps > b.MaxSteps) b.Steps = b.MaxSteps;
                if (b.Repairs > b.MaxRepairs) b.Repairs = b.MaxRepairs;
                if (b.Retries > b.MaxRetries) b.Retries = b.MaxRetries;
                // token 是「花掉的钱」，不因续跑而回滚：续跑不该获得一份新的预算额度。
                // 估算通道（BudgetUsesEstimatedTokens）把消耗记在 TokensEstimated 而不是实测字段——
                // 以前恢复时只算实测值，网页通道暂停再续跑预算就清零，「暂停/续跑」可以无限绕过预算。
                b.Tokens = Math.Max(0, task.PromptTokens) + Math.Max(0, task.CompletionTokens);
                if (b.Tokens == 0 && task.TokensEstimated > 0) b.Tokens = task.TokensEstimated;
            }
            return b;
        }

        /// <summary>尝试消耗一步。返回 false 时 reason 说明撞到哪个预算。</summary>
        public bool TryConsumeStep(out string reason)
        {
            reason = "";
            if (Steps >= MaxSteps)
            {
                reason = "步数预算已用尽（" + Steps + "/" + MaxSteps + " 步）";
                return false;
            }
            if (ElapsedMs > MaxSeconds * 1000L)
            {
                reason = "总耗时预算已用尽（上限 " + MaxSeconds + " 秒）";
                return false;
            }
            Steps++;
            return true;
        }

        /// <summary>步与步之间的耗时检查（模型回复可能很久）。</summary>
        public bool Expired(out string reason)
        {
            reason = "";
            if (ElapsedMs <= MaxSeconds * 1000L) return false;
            reason = "总耗时预算已用尽（上限 " + MaxSeconds + " 秒）";
            return true;
        }

        /// <summary>累计一次模型调用的真实用量（通道不上报时调用方不会调到这里）。</summary>
        public void AddTokens(long promptTokens, long completionTokens)
        {
            if (promptTokens > 0) Tokens += promptTokens;
            if (completionTokens > 0) Tokens += completionTokens;
        }

        /// <summary>
        /// token 预算检查。返回 true 表示已超额，必须停止任务。
        /// 注意：网页版大脑从不回传用量，Tokens 会一直是 0——这时提示词是「无法判定」而不是「没花钱」。
        /// </summary>
        public bool TokenExceeded(out string reason)
        {
            reason = "";
            if (MaxTokens <= 0) return false;
            if (Tokens < MaxTokens) return false;
            reason = "Token 预算已用尽（" + Tokens + "/" + MaxTokens + " tokens）";
            return true;
        }

        public bool TryConsumeRepair(out string reason)
        {
            reason = "";
            if (Repairs >= MaxRepairs)
            {
                reason = "修复预算已用尽（" + Repairs + "/" + MaxRepairs + " 次）";
                return false;
            }
            Repairs++;
            return true;
        }

        public bool TryConsumeRetry(out string reason)
        {
            reason = "";
            if (Retries >= MaxRetries)
            {
                reason = "工具重试预算已用尽（" + Retries + "/" + MaxRetries + " 次）";
                return false;
            }
            Retries++;
            return true;
        }

        public string Snapshot()
        {
            return "steps=" + Steps + "/" + MaxSteps +
                   " elapsed=" + (ElapsedMs / 1000) + "s/" + MaxSeconds + "s" +
                   " repairs=" + Repairs + "/" + MaxRepairs +
                   " retries=" + Retries + "/" + MaxRetries +
                   (MaxTokens > 0 ? " tokens=" + Tokens + "/" + MaxTokens : "");
        }
    }

    /// <summary>
    /// 一次任务的持久化记录。与会话消息分离：消息是聊天历史，任务记录是可查询的执行事实。
    /// 阶段 4 用它做中断恢复：进程重启后 outcome=running 的任务即为「被中断」。
    /// </summary>
    public class TaskRecord
    {
        public string TaskId { get; set; }
        public string SessionId { get; set; }
        public string UserText { get; set; }
        public string Outcome { get; set; }
        public string ErrorClass { get; set; }
        public string ErrorDetail { get; set; }
        public int Steps { get; set; }
        public int Repairs { get; set; }
        public int Retries { get; set; }
        public long StartedAt { get; set; }
        public long EndedAt { get; set; }
        public long DurationMs { get; set; }
        /// <summary>验收摘要：通过/失败的条件数，以及无法客观验证时的说明。</summary>
        public string Verification { get; set; }
        public bool BudgetExceeded { get; set; }
        /// <summary>阶段6：本任务累计的输入/输出 token（仅 API 大脑可测，0=未知）。</summary>
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        /// <summary>3.22.0：命中 prompt 缓存的输入 token（0=未知）。</summary>
        public long CachedTokens { get; set; }
        /// <summary>网页版按字符估算的 token（与实测值分开记，不混用；0=未估算）。</summary>
        public long TokensEstimated { get; set; }
        /// <summary>本次任务实际发送/收到的字符数（精确值，便于审计估算口径）。</summary>
        public long PromptChars { get; set; }
        public long CompletionChars { get; set; }
        /// <summary>并发控制：同一任务同一时刻只允许一个 Worker 推进。</summary>
        public string RunOwner { get; set; }
        public long LeaseUntil { get; set; }
        /// <summary>任务记录版本号，每次关键状态变更递增。</summary>
        public int Version { get; set; }
        /// <summary>阶段4：本任务是否因进程中断/取消停在半途（区分「正常失败」与「可续跑」）。</summary>
        public bool Interrupted { get; set; }
        /// <summary>阶段4：当前是否可以「继续任务」（等待审批/中断时为真）。</summary>
        public bool Resumable { get; set; }
        /// <summary>阶段4：续跑次数，用于统计恢复成功率。</summary>
        public int ResumeCount { get; set; }
        /// <summary>
        /// 本任务里真正读过的文件（绝对路径）。edit 只允许改读过的文件——
        /// 先 Read 再 Edit，避免模型凭想象盲改；续跑沿用同一份清单。
        /// </summary>
        public List<string> ReadFiles { get; set; }

        public TaskRecord()
        {
            TaskId = "task_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            SessionId = "";
            UserText = "";
            Outcome = TaskOutcome.Running;
            ErrorClass = "";
            ErrorDetail = "";
            Verification = "";
            RunOwner = "";
            LeaseUntil = 0;
            Version = 1;
            ReadFiles = new List<string>();
        }

        public void Finish(string outcome, string errorClass, string detail)
        {
            Outcome = outcome;
            ErrorClass = errorClass == null ? "" : errorClass;
            ErrorDetail = detail == null ? "" : detail;
            EndedAt = Message.Now();
            DurationMs = EndedAt - StartedAt;
            Version++;
            RunOwner = "";
            LeaseUntil = 0;
        }
    }

    /// <summary>
    /// token 粗估：只用于「通道不回传用量」时给出成本量级。
    /// 中文（含全角标点）按 1 token/字，其它按 4 字符/token —— 这是经验值，不是测量值，
    /// 所以永远写进 TokensEstimated，绝不混进 PromptTokens/CompletionTokens。
    /// </summary>
    public static class TokenEstimator
    {
        /// <summary>按校正系数缩放后的估算值（系数来自 Config.WebTokenEstimateFactor）。</summary>
        public static long Estimate(string text, double factor)
        {
            long raw = Estimate(text);
            if (factor <= 0) factor = 1.0;
            return (long)Math.Round(raw * factor);
        }

        public static long Estimate(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            long cjk = 0;
            long other = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if ((ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF) ||
                    (ch >= 0x3000 && ch <= 0x303F) || (ch >= 0xFF00 && ch <= 0xFFEF))
                {
                    cjk++;
                }
                else if (ch == '\r' || ch == '\n' || ch == '\t') { }
                else other++;
            }
            return cjk + other / 4;
        }
    }

    /// <summary>统一 Action 协议：模型每一步只能表达三种意图之一。</summary>
    public static class ActionKind
    {
        public const string ToolCall = "tool_call";
        public const string Final = "final";
        public const string RequestApproval = "request_approval";
    }

    /// <summary>一次模型动作的结构化解析结果（含校验失败的原因）。</summary>
    public class ActionRequest
    {
        public string Kind { get; set; }
        public string ToolName { get; set; }
        public string ArgsJson { get; set; }
        public string Text { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        public ActionRequest()
        {
            Kind = ActionKind.Final;
            ToolName = "";
            ArgsJson = "";
            Text = "";
            ErrorCode = "";
            ErrorMessage = "";
        }

        public bool IsValid
        {
            get { return string.IsNullOrEmpty(ErrorCode); }
        }
    }

    /// <summary>
    /// 把一次解析结果归类成唯一一种动作。三类动作互斥：
    /// 有审批请求 → request_approval；有工具调用 → tool_call；都没有 → final。
    /// 归类结果会写进轨迹（`action` 事件），便于按步分析「这一轮到底在干什么」。
    /// </summary>
    public static class ActionRouter
    {
        public static ActionRequest Classify(ToolParseResult parsed)
        {
            ActionRequest action = new ActionRequest();
            if (parsed == null) return action;
            if (parsed.Approval != null)
            {
                action.Kind = ActionKind.RequestApproval;
                action.ToolName = parsed.Approval.ToolName;
                action.ArgsJson = parsed.Approval.ArgsJson;
                action.Text = parsed.Approval.Text;
                action.ErrorCode = parsed.Approval.ErrorCode;
                action.ErrorMessage = parsed.Approval.ErrorMessage;
                // 同一轮里既要批准又要执行：不是「顺便执行」，而是协议违规。
                // 必须拒绝整轮 —— 否则那条工具调用会被静默丢掉或静默执行，两种都危险。
                if (action.IsValid && parsed.Calls != null && parsed.Calls.Count > 0)
                {
                    action.ErrorCode = ToolErrorCode.BadArgs;
                    action.ErrorMessage = "同一轮里既提出了审批请求、又给出了工具调用。一次只能表达一种动作：" +
                                          "先只输出审批请求，批准后再输出工具调用（或直接输出工具调用，由系统现场询问）。";
                }
                return action;
            }
            if (parsed.Calls != null && parsed.Calls.Count > 0)
            {
                action.Kind = ActionKind.ToolCall;
                return action;
            }
            action.Kind = ActionKind.Final;
            action.Text = parsed.Prose == null ? "" : parsed.Prose;
            return action;
        }
    }

    /// <summary>任务统计（阶段 6）：从任务记录聚合，不依赖外部服务。</summary>
    public class TaskStatsSummary
    {
        public int Total { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int Incomplete { get; set; }
        public int Cancelled { get; set; }
        public int Paused { get; set; }
        public int WaitingApproval { get; set; }
        public int BudgetExceeded { get; set; }
        public int VerificationFailed { get; set; }
        public long P50Ms { get; set; }
        public long P95Ms { get; set; }
        public long AvgMs { get; set; }
        /// <summary>成本口径：API 大脑记录实测 token；网页版只有按字符估算的量级。</summary>
        public string CostNote { get; set; }
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        /// <summary>3.22.0：缓存命中 token 合计。</summary>
        public long CachedTokens { get; set; }
        /// <summary>网页版估算 token 合计（与实测分开统计）。</summary>
        public long TokensEstimated { get; set; }

        public TaskStatsSummary()
        {
            CostNote = "unknown（当前大脑接口不返回 token 用量）";
        }

        public double SuccessRate
        {
            get { return Total == 0 ? 0 : (double)Succeeded / Total; }
        }
    }
}
