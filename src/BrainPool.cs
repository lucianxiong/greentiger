using System;
using System.Collections.Generic;

namespace GreenTiger
{
    /// <summary>
    /// 大脑池的一个候选。签名（端点|模型|Key，小写化）是池内去重、粘滞与冷却的依据。
    /// </summary>
    public class BrainCandidate
    {
        public string Label;
        public string ProviderId;
        public string Endpoint;
        public string Model;
        public string Key;
        /// <summary>3=设置里的当前服务商 2=显式 ProviderPool 条目 1=目录免 Key 免费档。</summary>
        public int Priority;

        public string Signature
        {
            get { return Sig(Endpoint) + "|" + Sig(Model) + "|" + Sig(Key); }
        }

        private static string Sig(string s)
        {
            return (s ?? "").Trim().ToLowerInvariant();
        }
    }

    /// <summary>一个候选的探测结果（Elect 的输入）。</summary>
    public class BrainPoolProbeResult
    {
        public BrainCandidate Candidate;
        public bool Available;
        public int LatencyMs;
        public string Detail;

        public BrainPoolProbeResult()
        {
            Available = false;
            LatencyMs = 0;
            Detail = "";
        }
    }

    /// <summary>
    /// 大脑池：候选构建 + 择优选举。纯函数（不做网络 IO），自检直接喂假数据验证排序规则。
    ///
    /// 候选来源（Priority 高者优先）：① 设置里的当前服务商（含手工自定义端点）；
    /// ② config.ProviderPool 显式条目（自带 Key，仅存本机）；③ AutoPool 时目录里的
    /// 免 Key 免费档自动入池（Pollinations 弱档例外，只在用户显式选中时使用）——有 Key
    /// 的免费档（如智谱）等用户真的配置它时经①入池，否则一个没 Key 的 401 候选每次任务都白探一轮。
    /// </summary>
    public static class BrainPool
    {
        public static List<BrainCandidate> BuildCandidates(Config cfg)
        {
            List<BrainCandidate> list = new List<BrainCandidate>();
            if (cfg == null) return list;

            // 1) 设置里的当前服务商永远优先（含手工填的自定义端点）
            if (!string.IsNullOrEmpty(cfg.ProviderEndpoint))
                list.Add(Make(cfg.ProviderId, cfg.ProviderEndpoint, cfg.ProviderModel, cfg.ProviderKey, 3));

            // 2) 显式池条目
            if (cfg.ProviderPool != null)
            {
                for (int i = 0; i < cfg.ProviderPool.Count; i++)
                {
                    ProviderPoolEntry e = cfg.ProviderPool[i];
                    if (e == null || e.Disabled) continue;
                    if (string.IsNullOrEmpty(e.Endpoint)) continue;
                    // 与主配置同端点同模型的不重复入池
                    if (Same(e.Endpoint, cfg.ProviderEndpoint) && Same(e.Model, cfg.ProviderModel)) continue;
                    list.Add(Make(e.ProviderId, e.Endpoint, e.Model, e.Key, 2));
                }
            }

            // 3) AutoPool：目录里免 Key 的免费档自动入池
            if (cfg.AutoPool)
            {
                List<ProviderInfo> catalog = Providers.Catalog();
                for (int i = 0; i < catalog.Count; i++)
                {
                    ProviderInfo p = catalog[i];
                    if (!p.Free) continue;
                    if (!string.IsNullOrEmpty(p.KeyUrl)) continue;
                    // 免 Key 弱档不自动入池：匿名队列有限速，而且 openai-fast 能力弱于旗舰；
                    // 用户显式选中它时走 ①，优先级最高，但不会被拿去替用户接管正式任务。
                    if (string.Equals(p.Id, "pollinations", StringComparison.OrdinalIgnoreCase)) continue;
                    // 本机端点不自动入池：服务不一定在跑，白探一轮就是每次任务多等一次超时。
                    // 用户选中它时走 ①，优先级最高。
                    if (Providers.IsLocal(p)) continue;
                    string model = p.Models.Count > 0 ? p.Models[0] : "";
                    if (string.IsNullOrEmpty(p.Endpoint) || string.IsNullOrEmpty(model)) continue;
                    if (Contains(list, p.Endpoint, model)) continue;
                    list.Add(Make(p.Id, p.Endpoint, model, "", 1));
                }
            }

            // 端点+模型去重（大小写不敏感；先出现者保留 = 高优先级保留）
            List<BrainCandidate> deduped = new List<BrainCandidate>();
            for (int i = 0; i < list.Count; i++)
            {
                if (Contains(deduped, list[i].Endpoint, list[i].Model)) continue;
                deduped.Add(list[i]);
            }
            return deduped;
        }

        /// <summary>
        /// 择优：可用优先；同可用看优先级（高优先）；同级看延迟（低优先）。
        /// 粘滞规则：当前脑活着、优先级不低于最优、延迟不超过最优的 1.3 倍 + 250ms 时不切换，
        /// 避免任务之间来回跳。全部不可用返回 null。
        /// </summary>
        public static BrainCandidate Elect(List<BrainPoolProbeResult> results, string stickySignature)
        {
            if (results == null || results.Count == 0) return null;
            BrainPoolProbeResult best = null;
            BrainPoolProbeResult sticky = null;
            for (int i = 0; i < results.Count; i++)
            {
                BrainPoolProbeResult r = results[i];
                if (r == null || r.Candidate == null || !r.Available) continue;
                if (best == null || BetterThan(r, best)) best = r;
                if (stickySignature != null && r.Candidate.Signature == stickySignature) sticky = r;
            }
            if (best == null) return null;
            if (sticky != null && sticky.Candidate != best.Candidate)
            {
                if (sticky.Candidate.Priority >= best.Candidate.Priority &&
                    sticky.LatencyMs <= best.LatencyMs * 1.3 + 250)
                {
                    return sticky.Candidate;
                }
            }
            return best.Candidate;
        }

        private static bool BetterThan(BrainPoolProbeResult a, BrainPoolProbeResult b)
        {
            if (a.Candidate.Priority != b.Candidate.Priority) return a.Candidate.Priority > b.Candidate.Priority;
            if (a.LatencyMs != b.LatencyMs) return a.LatencyMs < b.LatencyMs;
            return string.CompareOrdinal(a.Candidate.Signature, b.Candidate.Signature) < 0;
        }

        private static bool Contains(List<BrainCandidate> list, string endpoint, string model)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (Same(list[i].Endpoint, endpoint) && Same(list[i].Model, model)) return true;
            }
            return false;
        }

        private static bool Same(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static BrainCandidate Make(string providerId, string endpoint, string model, string key, int priority)
        {
            BrainCandidate c = new BrainCandidate();
            c.ProviderId = providerId ?? "";
            c.Endpoint = (endpoint ?? "").Trim();
            c.Model = (model ?? "").Trim();
            c.Key = key ?? "";
            c.Priority = priority;
            c.Label = Providers.DisplayNameFor(c.ProviderId, c.Endpoint, c.Model);
            return c;
        }
    }
}
