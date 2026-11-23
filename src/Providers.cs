using System;
using System.Collections.Generic;
using System.Text;

namespace GreenTiger
{
    /// <summary>模型服务商目录条目。</summary>
    public class ProviderInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>openai-compatible / ollama / lmstudio</summary>
        public string Kind { get; set; }
        public string Endpoint { get; set; }
        public List<string> Models { get; set; }
        public string Help { get; set; }
        /// <summary>是否有免费额度（或本机零成本）。界面据此标「免费」并给出速查入口。</summary>
        public bool Free { get; set; }
        /// <summary>免费额度的说明（额度/限速/条件），没有就留空。</summary>
        public string Quota { get; set; }
        /// <summary>申请 API Key 的页面（https）。本机服务为空。</summary>
        public string KeyUrl { get; set; }

        public ProviderInfo()
        {
            Id = "";
            Name = "";
            Kind = "openai-compatible";
            Endpoint = "";
            Models = new List<string>();
            Help = "";
            Free = false;
            Quota = "";
            KeyUrl = "";
        }
    }

    /// <summary>
    /// DeepSeek Harness 适配：内置主流模型服务商目录。
    /// 选择服务商后，端点与模型列表自动配置（写入 Config.ProviderEndpoint / ProviderModel，
    /// 实际请求走绿虎已有的 OpenAI 兼容通道——ApiBrain 的 /v1/chat/completions）。
    /// API Key 只写入本机 config.json，不上传任何地方。
    /// </summary>
    public static class Providers
    {
        private static List<ProviderInfo> _catalog;

        public static List<ProviderInfo> Catalog()
        {
            if (_catalog != null) return _catalog;
            _catalog = new List<ProviderInfo>();
            // 目录只收「当前旗舰」：每家只留真正能扛 Agent 任务的模型。
            // 免 Key 试验档、本机第三方桥与各家非旗舰尾巴一律不进目录——
            // 那些条目只能验证连通性，拿来跑任务是在给弱模型的畸形输出打补丁（见 IsTrialTier 的口径）。
            // 需要离线 / 自建网关 / 中转站时走「自定义端点」，不再往目录里塞占位模型名。
            _catalog.Add(Make("anthropic", "Claude 官方（OpenAI 兼容层）", "openai-compatible", "https://api.anthropic.com",
                new string[] { "claude-fable-5-2", "claude-opus-4.6" },
                "在 console.anthropic.com 创建 API Key。这里走 Anthropic 的 OpenAI 兼容层；直连不通（网络受限）时改走中转站：/connect relay <地址> <Key>。",
                false, "", "https://console.anthropic.com/settings/keys"));
            _catalog.Add(Make("deepseek-api", "DeepSeek 开放平台", "openai-compatible", "https://api.deepseek.com",
                new string[] { "deepseek-v4-pro" },
                "在 platform.deepseek.com 创建 API Key。v4-pro 是当前旗舰（1M 上下文），旧名 deepseek-chat / deepseek-reasoner 已弃用。",
                false, "", "https://platform.deepseek.com/api_keys"));
            _catalog.Add(Make("qwen", "阿里通义千问（百炼）", "openai-compatible", "https://dashscope.aliyuncs.com/compatible-mode/v1",
                new string[] { "qwen3.8-max" },
                "在 bailian.console.aliyun.com 开通百炼并创建 API Key（DashScope 兼容模式）。只保留 max 档：plus / flash 档留给自定义端点。",
                false, "新开通通常赠送额度", "https://bailian.console.aliyun.com/"));
            _catalog.Add(Make("zhipu", "智谱 GLM", "openai-compatible", "https://open.bigmodel.cn/api/paas/v4",
                new string[] { "glm-5.3" },
                "在 bigmodel.cn 创建 API Key（OpenAI 兼容端点）。glm-5.3 是当前旗舰。",
                false, "", "https://bigmodel.cn/usercenter/proj-mgmt/apikeys"));
            _catalog.Add(Make("kimi", "Moonshot Kimi", "openai-compatible", "https://api.moonshot.cn/v1",
                new string[] { "kimi-k3.1-pro" },
                "在 platform.moonshot.cn 创建 API Key。k3.1-pro 旗舰（1M 上下文）；moonshot-v1 与 k2 系列已下线。",
                false, "", "https://platform.moonshot.cn/console/api-keys"));
            // 目录到此为止：5 家官方服务商，每家只留当前旗舰模型。
            // 免 Key 公益池与本机第三方桥都不再内置——前者是弱模型且数据经第三方中转，
            // 后者是非官方线路；需要时用「自定义端点」自己填，目录不替用户做这个决定。
            return _catalog;
        }

        /// <summary>
        /// 是否本机端点（回环地址）。本机服务不一定在跑，AutoPool 不主动探测它——
        /// 否则每次任务都白等一轮 8080/11434 超时；用户在设置里选中它时照常走主配置。
        /// </summary>
        public static bool IsLocalEndpoint(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return false;
            Uri uri;
            if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out uri)) return false;
            return uri.IsLoopback;
        }

        public static bool IsLocal(ProviderInfo provider)
        {
            return provider != null && IsLocalEndpoint(provider.Endpoint);
        }

        /// <summary>
        /// 当前大脑是否是「免 Key 免费公益池」档。
        /// 这档只够连通性验证（CHANGELOG 3.10.10 同款口径）：拿来跑正经任务，稳定性
        /// 中间件的预算都花在给弱模型的畸形输出打补丁上。界面据此提示用户换正式服务商。
        /// </summary>
        public static bool IsTrialTier(Config config)
        {
            if (config == null) return false;
            string model = (config.ProviderModel ?? "").Trim();
            return model.IndexOf("openai-fast", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 从模型列表里挑一个能聊的：跳过 embedding / rerank / 语音这类不能对话的，
        /// 其余按「参数量」排序取最大——同机上 3B 的通用模型比 1.5B 的 coder 更会守工具协议，
        /// 小模型跑 Agent 最大的坑就是参数量不够。认不出参数量时按通用/对话关键词加权。
        /// </summary>
        public static string PickChatModel(List<string> models)
        {
            if (models == null || models.Count == 0) return "";
            string best = "";
            double bestScore = double.MinValue;
            for (int i = 0; i < models.Count; i++)
            {
                string id = models[i] ?? "";
                string low = id.ToLowerInvariant();
                if (low.Length == 0) continue;
                if (low.IndexOf("embed", StringComparison.Ordinal) >= 0) continue;
                if (low.IndexOf("rerank", StringComparison.Ordinal) >= 0) continue;
                if (low.IndexOf("whisper", StringComparison.Ordinal) >= 0) continue;
                if (low.IndexOf("tts", StringComparison.Ordinal) >= 0) continue;
                double score = ModelSizeBillions(low);
                if (low.IndexOf("instruct", StringComparison.Ordinal) >= 0) score += 0.6;
                if (low.IndexOf("chat", StringComparison.Ordinal) >= 0) score += 0.4;
                if (low.IndexOf("coder", StringComparison.Ordinal) >= 0) score += 0.2;
                if (low.IndexOf("vl", StringComparison.Ordinal) >= 0 || low.IndexOf("vision", StringComparison.Ordinal) >= 0) score -= 0.3;
                if (low.IndexOf("reason", StringComparison.Ordinal) >= 0 || low.IndexOf("think", StringComparison.Ordinal) >= 0) score -= 0.1;
                if (score > bestScore) { bestScore = score; best = id; }
            }
            return best;
        }

        /// <summary>从模型名里读参数量（qwen2.5:3b → 3，llama-3.1-8b → 8，gpt-oss:20b → 20）；读不出返回 0。</summary>
        public static double ModelSizeBillions(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return 0;
            double best = 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(modelId, @"(?<![\w.])(\d+(?:\.\d+)?)b(?![\w])"))
            {
                double v;
                if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v) && v > best) best = v;
            }
            return best;
        }

        private static ProviderInfo Make(string id, string name, string kind, string endpoint, string[] models, string help,
            bool free, string quota, string keyUrl)
        {
            ProviderInfo p = new ProviderInfo();
            p.Id = id;
            p.Name = name;
            p.Kind = kind;
            p.Endpoint = endpoint;
            for (int i = 0; i < models.Length; i++) p.Models.Add(models[i]);
            p.Help = help;
            p.Free = free;
            p.Quota = quota == null ? "" : quota;
            p.KeyUrl = keyUrl == null ? "" : keyUrl;
            return p;
        }

        public static ProviderInfo Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            List<ProviderInfo> all = Catalog();
            for (int i = 0; i < all.Count; i++)
                if (string.Equals(all[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    return all[i];
            return null;
        }

        /// <summary>
        /// 幂等拼接 OpenAI 兼容路径：目录里部分服务商端点自带 /v1（OpenAI/Kimi/Groq…），
        /// 部分不带（DeepSeek / 自建网关）——以前无脑追加 /v1/chat/completions，自带 /v1 的
        /// 全部变成 /v1/v1/... 404。统一走这里：已以 /v1 结尾就不再追加。
        /// </summary>
        public static string JoinV1(string endpoint, string path)
        {
            if (string.IsNullOrEmpty(endpoint)) return endpoint;
            string e = endpoint.TrimEnd('/');
            string suffix = string.IsNullOrEmpty(path) ? "" : "/" + path.TrimStart('/');
            if (e.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return e + suffix;
            return e + "/v1" + suffix;
        }

        /// <summary>
        /// 测试服务商连接并取回可用模型列表。
        /// GET {endpoint}/v1/models（自建网关兜底 /api/tags），带 Authorization: Bearer。
        /// 返回 null 表示连接失败（reason 给出原因）。
        /// </summary>
        public static List<string> TestConnection(ProviderInfo provider, string apiKey, out string reason)
        {
            reason = "";
            if (provider == null) { reason = "未知服务商"; return null; }
            string baseUrl = provider.Endpoint;
            if (string.IsNullOrEmpty(baseUrl)) { reason = "服务商缺少端点"; return null; }
            if (baseUrl.EndsWith("/", StringComparison.Ordinal)) baseUrl = baseUrl.TrimEnd('/');

            try
            {
                int status = 0;
                HttpResponse resp = HttpUtil.Get(JoinV1(baseUrl, "models"), HttpOptions.Of(10000).WithKey(apiKey));
                status = resp.Status;
                List<string> models = resp.Ok ? ParseOpenAiModels(resp.Body) : null;
                if (models == null || models.Count == 0)
                {
                    // 非标准网关兜底：{endpoint}/models（不带 /v1）。部分网关会把
                    // /v1/models 当普通聊天提示词处理，真正的模型列表在根路径 /models 下。
                    HttpResponse alt = HttpUtil.Get(baseUrl + "/models", HttpOptions.Of(8000).WithKey(apiKey));
                    if (alt.Ok)
                    {
                        status = alt.Status;
                        List<string> altModels = ParseOpenAiModels(alt.Body);
                        if (altModels.Count > 0) models = altModels;
                    }
                }
                if (models == null || models.Count == 0)
                {
                    // 自建网关 / 本机网关的 /api/tags 格式兜底（Ollama）
                    HttpResponse tags = HttpUtil.Get(baseUrl + "/api/tags", HttpOptions.Of(8000).WithKey(apiKey));
                    if (tags.Ok)
                    {
                        status = tags.Status;
                        List<string> ollama = ParseTags(tags.Body);
                        if (ollama.Count > 0) models = ollama;
                    }
                }
                if (models == null || models.Count == 0)
                {
                    // 把失败原因说清楚：Key 填错是最高频的接入问题，别报成「网络不通」。
                    if (status == 401 || status == 403) reason = "鉴权失败（HTTP " + status + "）：API Key 未填或无效";
                    else if (status == 0) reason = "端点无响应（检查网络或服务是否启动）";
                    else reason = "端点已响应（HTTP " + status + "），但没有可用模型";
                    return null;
                }
                return models;
            }
            catch (Exception ex)
            {
                reason = "连接失败: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 解析模型列表，尽量兼容三类形状：
        /// ① OpenAI 标准 {"data":[{"id":...}]}；② {"models":[...]}（Ollama 风格，id/name/model 均认）；
        /// ③ 裸数组 ["id",...] / [{"name":...},...]（部分自建网关的 /models）。
        /// 不是 JSON（如网关把 /v1/models 当聊天处理返回纯文本）时返回空列表。
        /// </summary>
        internal static List<string> ParseOpenAiModels(string body)
        {
            List<string> models = new List<string>();
            Dictionary<string, object> obj = Json.ParseObject(body);
            if (obj != null)
            {
                List<object> data = Json.Array(obj, "data");
                if (data.Count == 0) data = Json.Array(obj, "models");
                for (int i = 0; i < data.Count; i++) AddModelName(models, data[i]);
            }
            if (models.Count == 0)
            {
                List<object> top = Json.Parse<List<object>>(body);
                if (top != null)
                    for (int i = 0; i < top.Count; i++) AddModelName(models, top[i]);
            }
            return models;
        }

        /// <summary>从列表元素里取模型名：字符串直接用；对象认 id / name / model 三个字段。</summary>
        private static void AddModelName(List<string> models, object item)
        {
            string id;
            Dictionary<string, object> dict = item as Dictionary<string, object>;
            if (dict != null)
            {
                id = Json.Str(dict, "id", Json.Str(dict, "name", Json.Str(dict, "model", "")));
            }
            else
            {
                id = item as string;
                if (id == null) return;
            }
            id = (id ?? "").Trim();
            if (id.Length > 0 && !models.Contains(id)) models.Add(id);
        }

        internal static List<string> ParseTags(string body)
        {
            List<string> models = new List<string>();
            Dictionary<string, object> obj = Json.ParseObject(body);
            if (obj == null) return models;
            List<object> data = Json.Array(obj, "models");
            for (int i = 0; i < data.Count; i++)
            {
                Dictionary<string, object> item = data[i] as Dictionary<string, object>;
                if (item == null) continue;
                string id = Json.Str(item, "name", "");
                if (id.Length == 0) id = Json.Str(item, "model", "");
                if (id.Length > 0 && !models.Contains(id)) models.Add(id);
            }
            return models;
        }

        /// <summary>Provider 生效时的脑显示名（用于状态栏）。</summary>
        public static string DisplayName(Config cfg)
        {
            return DisplayNameFor(cfg.ProviderId, cfg.ProviderEndpoint, cfg.ProviderModel);
        }

        /// <summary>按三元组显示名（大脑池候选标签用；与 DisplayName 同口径）。</summary>
        public static string DisplayNameFor(string providerId, string endpoint, string model)
        {
            ProviderInfo p = Find(providerId);
            string name = p != null ? p.Name : (string.IsNullOrEmpty(endpoint) ? "API 服务商" : endpoint);
            if (!string.IsNullOrEmpty(model)) return name + " · " + model;
            return name;
        }
    }
}
