using System;
using System.Collections.Generic;

namespace GreenTiger
{
    /// <summary>
    /// 阶段7：按任务类型的执行权限策略。
    /// 场景：调研类任务强制只读、批处理任务收紧步数预算、受控试运行对特定任务放开高风险确认。
    /// Match 命中用户消息（大小写不敏感子串）即生效；未命中的任务完全不受影响。
    /// 空字段一律继承全局配置——策略只做叠加收紧或显式放开，不引入新的执行路径。
    /// </summary>
    public class TaskTypePolicy
    {
        public string Name { get; set; }
        /// <summary>命中条件：用户消息包含该子串（大小写不敏感）。空 Match 永不命中。</summary>
        public string Match { get; set; }
        /// <summary>true=本类任务强制只读；false/null=继承全局 ReadOnly。</summary>
        public bool? ReadOnly { get; set; }
        /// <summary>""=继承全局 HighRiskPolicy；否则 ask/deny/allow。</summary>
        public string HighRiskPolicy { get; set; }
        /// <summary>0=继承全局 MaxSteps；>0=本类任务的步数预算。</summary>
        public int MaxSteps { get; set; }

        public TaskTypePolicy()
        {
            Name = "";
            Match = "";
            HighRiskPolicy = "";
        }
    }

    public static class TaskPolicyResolver
    {
        /// <summary>返回第一条命中的策略；没有命中返回 null（调用方按继承全局处理）。</summary>
        public static TaskTypePolicy Resolve(IList<TaskTypePolicy> policies, string userText)
        {
            if (policies == null || policies.Count == 0 || string.IsNullOrEmpty(userText)) return null;
            string hay = userText.ToLowerInvariant();
            for (int i = 0; i < policies.Count; i++)
            {
                TaskTypePolicy p = policies[i];
                if (p == null || string.IsNullOrEmpty(p.Match)) continue;
                if (hay.Contains(p.Match.ToLowerInvariant())) return p;
            }
            return null;
        }

        /// <summary>本任务生效的只读开关：策略显式指定优先，否则继承全局。</summary>
        public static bool EffectiveReadOnly(TaskTypePolicy policy, Config config)
        {
            if (policy != null && policy.ReadOnly.HasValue) return policy.ReadOnly.Value;
            return config != null && config.ReadOnly;
        }

        /// <summary>本任务生效的高风险策略：策略非空优先，否则继承全局（非法值归 ask）。</summary>
        public static string EffectiveHighRisk(TaskTypePolicy policy, Config config)
        {
            string v = policy != null ? policy.HighRiskPolicy : null;
            if (v != "ask" && v != "deny" && v != "allow") v = config == null ? "ask" : config.HighRiskPolicy;
            if (v != "ask" && v != "deny" && v != "allow") v = "ask";
            return v;
        }

        /// <summary>本任务生效的步数预算：策略 &gt;0 优先，且不超过全局上限的 2 倍（防止策略意外放大预算）。</summary>
        public static int EffectiveMaxSteps(TaskTypePolicy policy, Config config)
        {
            int global = config == null ? 40 : config.MaxSteps;
            if (policy == null || policy.MaxSteps <= 0) return global;
            int cap = global * 2;
            return policy.MaxSteps > cap ? cap : policy.MaxSteps;
        }
    }
}
