using System;
using System.Collections.Generic;
using System.Text;

namespace GreenTiger
{
    public class AlertItem
    {
        /// <summary>warn / critical。</summary>
        public string Level { get; set; }
        /// <summary>稳定告警码：verification_fail_rate / budget_exhaust_rate / incomplete_rate。</summary>
        public string Code { get; set; }
        public string Message { get; set; }
        public int Count { get; set; }
        public int Total { get; set; }

        public AlertItem()
        {
            Level = "warn";
            Code = "";
            Message = "";
        }
    }

    /// <summary>
    /// 阶段6/7：退化告警。对最近 N 个任务统计验证失败率、预算耗尽率、未完成率，
    /// 超过阈值就产生结构化告警（/api/state.alerts + 日志 + trace）。
    /// 纯函数：输入任务记录与阈值，输出告警列表，本身不做 I/O，可单测。
    /// 阈值为 0 表示关闭该类告警；样本不足（少于 MinSample，默认 5）不告警，避免小样本误报。
    /// </summary>
    public static class AlertEngine
    {
        public const int DefaultWindow = 20;
        public const int DefaultMinSample = 5;

        public static int Window(Config config)
        {
            int v = config == null ? 0 : config.AlertWindowTasks;
            return v > 0 ? v : DefaultWindow;
        }

        /// <summary>评估最近 tasks 条任务。返回告警列表（空=健康）。</summary>
        public static List<AlertItem> Evaluate(IList<TaskRecord> tasks, Config config)
        {
            List<AlertItem> alerts = new List<AlertItem>();
            if (tasks == null || tasks.Count == 0) return alerts;
            int minSample = config == null || config.AlertMinSample < 0 ? DefaultMinSample : config.AlertMinSample;
            if (tasks.Count < Math.Max(1, minSample)) return alerts;

            int total = tasks.Count;
            int verificationFail = 0;
            int budgetExhausted = 0;
            int notSucceeded = 0;
            for (int i = 0; i < total; i++)
            {
                TaskRecord r = tasks[i];
                if (r == null) continue;
                if (r.ErrorClass == ErrorClass.VerificationFailed) verificationFail++;
                if (r.BudgetExceeded) budgetExhausted++;
                if (r.Outcome != TaskOutcome.Succeeded) notSucceeded++;
            }

            CheckRate(alerts, config, total, verificationFail, "verification_fail_rate",
                config == null ? 0 : config.AlertVerificationFailRate,
                "验证失败率过高：模型声称完成但证据不足的比例异常", config == null ? 0.5 : 0.5);
            CheckRate(alerts, config, total, budgetExhausted, "budget_exhaust_rate",
                config == null ? 0 : config.AlertBudgetExhaustRate,
                "预算耗尽率过高：任务频繁因步数/耗时/重试预算停止", config == null ? 0 : 0.3);
            CheckRate(alerts, config, total, notSucceeded, "incomplete_rate",
                config == null ? 0 : config.AlertIncompleteRate,
                "任务未成功率过高：近期执行质量退化", config == null ? 0 : 0.6);
            return alerts;
        }

        private static void CheckRate(List<AlertItem> alerts, Config config, int total, int count,
            string code, double threshold, string text, double defaultThreshold)
        {
            if (threshold <= 0) threshold = defaultThreshold;
            double rate = (double)count / total;
            if (rate < threshold) return;
            AlertItem item = new AlertItem();
            item.Code = code;
            item.Count = count;
            item.Total = total;
            // 超过阈值的 1.5 倍升级为 critical
            item.Level = rate >= threshold * 1.5 ? "critical" : "warn";
            item.Message = text + "（" + count + "/" + total + " = " + Math.Round(rate * 100, 1) + "%，阈值 " + Math.Round(threshold * 100, 1) + "%）";
            alerts.Add(item);
        }

        /// <summary>告警列表变化时写日志与轨迹（去重：与上次比较，只在内容变化时记一次）。</summary>
        public static string Fingerprint(List<AlertItem> alerts)
        {
            if (alerts == null || alerts.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < alerts.Count; i++)
            {
                sb.Append(alerts[i].Code).Append(':').Append(alerts[i].Count).Append(';');
            }
            return sb.ToString();
        }
    }
}
