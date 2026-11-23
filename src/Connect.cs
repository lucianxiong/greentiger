using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GreenTiger
{
    /// <summary>一次接入请求（解析后的意图）。</summary>
    public class ConnectRequest
    {
        /// <summary>claude / relay / ""（仅看状态）</summary>
        public string Kind { get; set; }
        public string Url { get; set; }
        public string Key { get; set; }
        public string Model { get; set; }

        public ConnectRequest()
        {
            Kind = "";
            Url = "";
            Key = "";
            Model = "";
        }
    }

    /// <summary>解析并探测后的接入目标。</summary>
    public class ConnectTarget
    {
        public bool Ok { get; set; }
        public string ProviderId { get; set; }
        public string Label { get; set; }
        public string Endpoint { get; set; }
        public string Key { get; set; }
        public string Model { get; set; }
        public List<string> Models { get; set; }
        public bool Free { get; set; }
        public string Detail { get; set; }

        public ConnectTarget()
        {
            ProviderId = "";
            Label = "";
            Endpoint = "";
            Key = "";
            Model = "";
            Detail = "";
            Models = new List<string>();
        }
    }

    /// <summary>
    /// 接入向导：把「Claude 官方兼容层 / 任意中转站」两条通道探测出来并写入配置。
    ///
    /// · Claude 官方提供 OpenAI 兼容层：base_url = https://api.anthropic.com/v1/，用 Claude API Key。
    ///   它忽略 strict、不返回 thinking、不支持 prompt caching；部分模型还会对
    ///   temperature &lt; 1 直接 400，所以温度要按模型省略（见 ModelQuirks）。
    /// · 直连不通时用中转站（任何 OpenAI 兼容网关），因此 relay 只需要 base url + key。
    /// · 本机自建网关（llama.cpp / Ollama / One-API）同属中转站形态，填回环地址即可。
    ///
    /// 全部探测都走唯一 HTTP 出口（HttpUtil），超时 4 秒，不重试、不把 Key 写进日志。
    /// </summary>
    public static class Connect
    {
        public const int ProbeTimeoutMs = 4000;
        public const string AnthropicEndpoint = "https://api.anthropic.com";

        public static ConnectRequest Parse(string input)
        {
            ConnectRequest req = new ConnectRequest();
            string text = (input ?? "").Trim();
            string rest = text.Length > 8 ? text.Substring(8).Trim() : "";
            if (rest.Length == 0) return req;
            string[] parts = rest.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = parts[0].ToLowerInvariant();
            if (verb == "claude" || verb == "relay")
            {
                req.Kind = verb;
                if (verb == "relay" || verb == "claude")
                {
                    if (parts.Length > 1 && LooksLikeUrl(parts[1]))
                    {
                        req.Url = parts[1];
                        if (parts.Length > 2) req.Key = parts[2];
                        if (parts.Length > 3) req.Model = parts[3];
                    }
                    else
                    {
                        if (parts.Length > 1) req.Key = parts[1];
                        if (parts.Length > 2) req.Model = parts[2];
                    }
                }
                else if (parts.Length > 1) req.Model = parts[1];
                return req;
            }
            // 只给了 URL：按中转站处理
            if (LooksLikeUrl(rest))
            {
                req.Kind = "relay";
                req.Url = parts[0];
                if (parts.Length > 1) req.Key = parts[1];
                if (parts.Length > 2) req.Model = parts[2];
                return req;
            }
            req.Kind = "?";
            return req;
        }

        private static bool LooksLikeUrl(string text)
        {
            return text != null && (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>归一化端点：只接受 http/https 绝对地址；去掉尾部斜杠；保留 /v1（JoinV1 幂等）。</summary>
        public static string NormalizeEndpoint(string url, out string error)
        {
            error = "";
            string text = (url ?? "").Trim();
            if (text.Length == 0) { error = "端点为空"; return ""; }
            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                error = "端点必须是 http:// 或 https:// 开头的绝对地址";
                return "";
            }
            if (uri.Host.Length == 0) { error = "端点缺少主机名"; return ""; }
            return text.TrimEnd('/');
        }

        /// <summary>Claude 优先的模型挑选：opus &gt; sonnet &gt; fable &gt; haiku &gt; 其它。</summary>
        public static string PickModel(List<string> models, bool preferClaude)
        {
            if (models == null || models.Count == 0) return "";
            string[] claudeOrder = new string[] { "claude-opus", "claude-sonnet", "claude-fable", "claude-haiku", "claude" };
            if (preferClaude)
            {
                for (int k = 0; k < claudeOrder.Length; k++)
                {
                    string best = "";
                    for (int i = 0; i < models.Count; i++)
                    {
                        string m = models[i] ?? "";
                        if (m.ToLowerInvariant().IndexOf(claudeOrder[k], StringComparison.Ordinal) < 0) continue;
                        if (best.Length == 0 || ScoreClaude(m) > ScoreClaude(best)) best = m;
                    }
                    if (best.Length > 0) return best;
                }
            }
            for (int i = 0; i < models.Count; i++)
            {
                string m = (models[i] ?? "").ToLowerInvariant();
                if (m.IndexOf("embed", StringComparison.Ordinal) >= 0 || m.IndexOf("rerank", StringComparison.Ordinal) >= 0) continue;
                if (m.IndexOf("pro", StringComparison.Ordinal) >= 0) return models[i];
            }
            for (int i = 0; i < models.Count; i++)
            {
                string m = (models[i] ?? "").ToLowerInvariant();
                if (m.IndexOf("embed", StringComparison.Ordinal) >= 0 || m.IndexOf("rerank", StringComparison.Ordinal) >= 0) continue;
                return models[i];
            }
            return models[0];
        }

        private static int ScoreClaude(string model)
        {
            string m = (model ?? "").ToLowerInvariant();
            int score = 0;
            if (m.IndexOf("opus", StringComparison.Ordinal) >= 0) score += 40;
            if (m.IndexOf("sonnet", StringComparison.Ordinal) >= 0) score += 30;
            if (m.IndexOf("fable", StringComparison.Ordinal) >= 0) score += 35;
            if (m.IndexOf("haiku", StringComparison.Ordinal) >= 0) score += 10;
            // 版本号越大越好：claude-opus-4.6 > claude-opus-4.5
            Match ver = Regex.Match(m, "-(\\d+)\\.(\\d+)");
            if (ver.Success)
            {
                int major;
                int minor;
                if (int.TryParse(ver.Groups[1].Value, out major)) score += Math.Min(major, 9);
                if (int.TryParse(ver.Groups[2].Value, out minor)) score += Math.Min(minor, 9);
            }
            return score;
        }

        /// <summary>探测模型列表：/v1/models → /models → /api/tags。全失败返回空列表与原因。</summary>
        public static List<string> ProbeModels(string endpoint, string key, int timeoutMs, out string error)
        {
            error = "";
            List<string> models = new List<string>();
            string baseUrl = (endpoint ?? "").TrimEnd('/');
            if (baseUrl.Length == 0) { error = "端点为空"; return models; }
            int timeout = timeoutMs > 0 ? timeoutMs : ProbeTimeoutMs;

            HttpResponse resp = HttpUtil.Get(Providers.JoinV1(baseUrl, "models"), HttpOptions.Of(timeout).WithKey(key));
            if (resp.Ok) models = Providers.ParseOpenAiModels(resp.Body);
            if (models.Count == 0)
            {
                HttpResponse alt = HttpUtil.Get(baseUrl + "/models", HttpOptions.Of(timeout).WithKey(key));
                if (alt.Ok) models = Providers.ParseOpenAiModels(alt.Body);
            }
            if (models.Count == 0)
            {
                HttpResponse tags = HttpUtil.Get(baseUrl + "/api/tags", HttpOptions.Of(timeout).WithKey(key));
                if (tags.Ok) models = Providers.ParseTags(tags.Body);
            }
            if (models.Count == 0)
            {
                if (resp.Status == 401 || resp.Status == 403) error = "鉴权失败（HTTP " + resp.Status + "）：Key 无效或未填";
                else if (resp.Status == 0) error = "端点无响应（服务没启动或网络不通）";
                else error = "端点已响应（HTTP " + resp.Status + "），但没返回模型列表";
            }
            return models;
        }

        /// <summary>把请求解析成可写入配置的目标（会做网络探测）。</summary>
        public static ConnectTarget Resolve(ConnectRequest req)
        {
            ConnectTarget target = new ConnectTarget();
            if (req == null) { target.Detail = "空的接入请求"; return target; }
            if (req.Kind == "claude") return ResolveClaude(req);
            if (req.Kind == "relay") return ResolveRelay(req);
            target.Detail = "不认识的接入方式：" + req.Kind;
            return target;
        }

        private static ConnectTarget ResolveClaude(ConnectRequest req)
        {
            if (!string.IsNullOrEmpty(req.Url)) return ResolveRelay(req);
            if (string.IsNullOrEmpty(req.Key))
            {
                ConnectTarget need = new ConnectTarget();
                need.Detail = "接 Claude 官方需要 API Key：`/connect claude <你的Key>`。\n"
                    + "没有 Key 或直连不通时走中转站：`/connect relay <中转站地址> <Key> [模型]`。";
                return need;
            }
            string error;
            List<string> models = ProbeModels(AnthropicEndpoint, req.Key, ProbeTimeoutMs, out error);
            if (models.Count == 0)
            {
                ConnectTarget fail = new ConnectTarget();
                fail.Detail = "Claude 官方端点不可用：" + error + "\n"
                    + "这是网络问题不是配置问题——改走中转站：`/connect relay <中转站地址> <Key> [模型]`，"
                    + "地址填到 /v1 为止（多数中转站是 https://xxx/v1）。";
                return fail;
            }
            ConnectTarget t = new ConnectTarget();
            t.Ok = true;
            t.ProviderId = "anthropic";
            t.Label = "Claude 官方（OpenAI 兼容层）";
            t.Endpoint = AnthropicEndpoint;
            t.Key = req.Key;
            t.Models = models;
            t.Model = !string.IsNullOrEmpty(req.Model) ? req.Model : PickModel(models, true);
            t.Detail = "按 token 计费（无免费额度）";
            return t;
        }

        private static ConnectTarget ResolveRelay(ConnectRequest req)
        {
            string error;
            string endpoint = NormalizeEndpoint(req.Url, out error);
            ConnectTarget t = new ConnectTarget();
            if (endpoint.Length == 0) { t.Detail = "中转站地址不合法：" + error; return t; }
            List<string> models = ProbeModels(endpoint, req.Key, ProbeTimeoutMs, out error);
            if (models.Count == 0)
            {
                t.Detail = "连不上这个中转站：" + error + "\n"
                    + "检查：① 地址是否填到 /v1（https://host/v1）；② Key 是否有效；③ 该平台是否允许你的网络直接访问。";
                return t;
            }
            t.Ok = true;
            t.ProviderId = "custom";
            t.Label = "中转站 " + endpoint;
            t.Endpoint = endpoint;
            t.Key = req.Key ?? "";
            t.Models = models;
            t.Model = !string.IsNullOrEmpty(req.Model) ? req.Model : PickModel(models, true);
            t.Free = false;
            t.Detail = "中转站按平台计费，绿虎只负责连上";
            return t;
        }

        /// <summary>两种接入方式的落地指引（没配好时原样给用户，别让他去搜）。</summary>
        public static string SetupGuide()
        {
            return "接模型有两种方式：\n"
                + "1. 官方兼容层：`/connect claude <Key>`（在 console.anthropic.com 建 Key）。直连不通会如实报错，不会假装接上。\n"
                + "2. 中转站 / 自建网关：`/connect relay <地址> <Key> [模型]`，地址填到 /v1 为止。\n"
                + "本机 llama.cpp / Ollama 也走第 2 种，填 `http://127.0.0.1:<端口>/v1`（llama.cpp 需带 --jinja）。";
        }

        public static string Banner(Config config)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("## 接入向导");
            sb.AppendLine();
            sb.AppendLine("当前大脑：**" + Providers.DisplayName(config) + "**"
                + (string.IsNullOrEmpty(config == null ? "" : config.ProviderEndpoint) ? "（未配置）" : ""));
            sb.AppendLine();
            sb.AppendLine("两种接入方式：");
            sb.AppendLine("1. `/connect claude <Key>` —— Claude 官方 OpenAI 兼容层，按 token 计费；直连不通会提示走中转站。");
            sb.AppendLine("2. `/connect relay <地址> <Key> [模型]` —— 任意 OpenAI 兼容中转站 / 自建网关，地址填到 /v1 为止。");
            sb.AppendLine();
            sb.AppendLine("国内服务商（DeepSeek / 通义 / 智谱 / Kimi）直接在设置 → 模型接入里选，填 Key 即可。");
            sb.AppendLine();
            sb.AppendLine(SetupGuide());
            return sb.ToString().TrimEnd();
        }

        /// <summary>接入成功后给用户看的回执（不打印 Key）。</summary>
        public static string Receipt(ConnectTarget target)
        {
            if (target == null) return "接入失败：空目标。";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("已接入 **" + target.Label + "**");
            sb.AppendLine("- 端点：" + target.Endpoint);
            sb.AppendLine("- 模型：**" + target.Model + "**"
                + (target.Models != null && target.Models.Count > 1 ? "（共发现 " + target.Models.Count + " 个，`/model` 可换）" : ""));
            sb.AppendLine("- 计费：" + target.Detail);
            sb.Append("下一轮任务就用这个大脑；要换回来，在设置 → 模型接入里重新选。");
            return sb.ToString();
        }

        public static void SelfTest(Action<string, bool, string> check)
        {
            ConnectRequest relay = Parse("/connect relay https://api.example.com/v1 sk-abc claude-sonnet-4.6");
            check("connect parses relay args", relay.Kind == "relay" && relay.Url == "https://api.example.com/v1"
                && relay.Key == "sk-abc" && relay.Model == "claude-sonnet-4.6", relay.Kind + "/" + relay.Url);

            ConnectRequest bare = Parse("/connect https://relay.example.com sk-1");
            check("connect treats bare url as relay", bare.Kind == "relay" && bare.Key == "sk-1", bare.Kind);

            ConnectRequest claude = Parse("/connect claude sk-ant-1");
            check("connect parses claude key", claude.Kind == "claude" && claude.Key == "sk-ant-1" && claude.Url.Length == 0, claude.Key);

            string error;
            check("connect normalizes endpoint", NormalizeEndpoint(" https://x.example.com/v1/ ", out error) == "https://x.example.com/v1", error);
            NormalizeEndpoint("ftp://x", out error);
            check("connect rejects non-http endpoint", error.Length > 0, error);
            NormalizeEndpoint("", out error);
            check("connect rejects empty endpoint", error.Length > 0, error);

            List<string> models = new List<string> { "gemini-3.5-flash", "claude-sonnet-4.6", "claude-opus-4.6", "text-embedding-3" };
            check("connect prefers opus over sonnet", PickModel(models, true) == "claude-opus-4.6", PickModel(models, true));
            check("connect falls back to pro model",
                PickModel(new List<string> { "gemini-3.5-flash", "gemini-3.1-pro" }, true) == "gemini-3.1-pro", "");
            check("connect skips embedding when no claude",
                PickModel(new List<string> { "text-embedding-3", "glm-4.7-flash" }, true) == "glm-4.7-flash", "");

            check("claude 4.7+ omits temperature", ModelQuirks.OmitTemperature("claude-opus-4.7")
                && ModelQuirks.OmitTemperature("claude-fable-5-2") && !ModelQuirks.OmitTemperature("claude-sonnet-4.6"), "");
            check("claude requests carry max_tokens", ModelQuirks.NeedsMaxTokens("claude-opus-4.6")
                && ModelQuirks.NeedsMaxTokens("claude-fable-5-2") && !ModelQuirks.NeedsMaxTokens("deepseek-v4-flash"), "");

            // 本机 mock：验证 /v1/models 解析与「桥只有 /health 时仍可接入」两条路径
            int port = 0;
            try
            {
                System.Net.Sockets.TcpListener probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                try
                {
                    probe.Start();
                    port = ((IPEndPoint)probe.LocalEndpoint).Port;
                }
                finally { probe.Stop(); }
                using (HttpListener listener = new HttpListener())
                {
                    string prefix = "http://127.0.0.1:" + port + "/";
                    listener.Prefixes.Add(prefix);
                    listener.Start();
                    System.Threading.Tasks.Task.Run(delegate
                    {
                        while (listener.IsListening)
                        {
                            HttpListenerContext ctx;
                            try { ctx = listener.GetContext(); } catch { break; }
                            string path = ctx.Request.Url.AbsolutePath;
                            string body;
                            int status = 200;
                            if (path.EndsWith("/models", StringComparison.Ordinal))
                                body = "{\"data\":[{\"id\":\"claude-sonnet-4.6\"},{\"id\":\"gemini-3.5-flash\"}]}";
                            else if (path == "/health")
                                body = "{\"status\":\"ok\"}";
                            else { status = 404; body = "{}"; }
                            byte[] bytes = Encoding.UTF8.GetBytes(body);
                            ctx.Response.StatusCode = status;
                            ctx.Response.ContentType = "application/json";
                            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                            try { ctx.Response.Close(); } catch { }
                        }
                    });
                    string probeError;
                    List<string> found = ProbeModels("http://127.0.0.1:" + port, "k", 3000, out probeError);
                    check("connect probes model list", found.Count == 2 && found.Contains("claude-sonnet-4.6"), probeError);

                    ConnectRequest relayReq = Parse("/connect relay http://127.0.0.1:" + port + " k");
                    ConnectTarget target = Resolve(relayReq);
                    check("connect resolves relay target", target.Ok && target.Model == "claude-sonnet-4.6"
                        && target.Endpoint.IndexOf("127.0.0.1") >= 0, target.Detail);
                    listener.Stop();
                }
            }
            catch (Exception ex)
            {
                check("connect probe mock", false, TextUtil.OneLine(ex.Message, 120));
            }
        }
    }

    /// <summary>
    /// 模型怪癖：把「接上就能跑」所需的差异集中在一处，别散落在请求构造里。
    /// 依据（Anthropic OpenAI 兼容层文档，2026-10 复核）：
    /// · Claude 4.7 / Fable / Mythos 对 temperature &lt; 1 直接 400 —— 这些模型必须省略该字段；
    /// · Claude 系请求缺 max_tokens 会被拒（原生 Messages 必填，兼容层同样要求）。
    /// </summary>
    public static class ModelQuirks
    {
        public const int DefaultMaxTokens = 8192;

        public static bool OmitTemperature(string model)
        {
            string m = (model ?? "").ToLowerInvariant();
            if (m.Length == 0) return false;
            if (m.IndexOf("fable", StringComparison.Ordinal) >= 0) return true;
            if (m.IndexOf("mythos", StringComparison.Ordinal) >= 0) return true;
            Match ver = Regex.Match(m, "claude-[a-z]+-(\\d+)\\.(\\d+)");
            if (ver.Success)
            {
                int major;
                if (int.TryParse(ver.Groups[1].Value, out major) && major >= 4)
                {
                    int minor;
                    if (int.TryParse(ver.Groups[2].Value, out minor) && (major > 4 || minor >= 7)) return true;
                }
            }
            if (m.IndexOf("claude-opus-5", StringComparison.Ordinal) >= 0) return true;
            if (m.IndexOf("claude-sonnet-5", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public static bool NeedsMaxTokens(string model)
        {
            string m = (model ?? "").ToLowerInvariant();
            return m.IndexOf("claude", StringComparison.Ordinal) >= 0;
        }
    }
}
