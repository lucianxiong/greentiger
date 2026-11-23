using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GreenTiger
{
    /// <summary>
    /// 一次 HTTP 调用的结果。4xx/5xx 也算"到了服务端"，
    /// Body 里保留响应体，调用方可以看清对方报了什么，而不是只拿到一个 null。
    /// </summary>
    public class HttpResponse
    {
        public bool Ok { get; set; }
        public int Status { get; set; }
        public string Body { get; set; }
        public string Error { get; set; }
        public bool Timeout { get; set; }
        /// <summary>3.23.0：响应 Content-Type（SSE/JSON 分流用）。</summary>
        public string ContentType { get; set; }
        /// <summary>3.23.0：响应头（MCP Streamable HTTP 的 Mcp-Session-Id 等）。</summary>
        public Dictionary<string, string> Headers { get; set; }
        /// <summary>大小写不敏感取响应头；没有返回空串。</summary>
        public string Header(string name)
        {
            if (Headers == null || string.IsNullOrEmpty(name)) return "";
            foreach (KeyValuePair<string, string> kv in Headers)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value ?? "";
            }
            return "";
        }

        public HttpResponse()
        {
            Ok = false;
            Status = 0;
            Body = "";
            Error = "";
            Timeout = false;
        }
    }

    /// <summary>请求参数。缺的字段用默认值，调用方不用为每个场景塞满一长串参数。</summary>
    public class HttpOptions
    {
        public int TimeoutMs { get; set; }
        public string ApiKey { get; set; }
        public string UserAgent { get; set; }
        public string Accept { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public CancellationToken Token { get; set; }
        /// <summary>是否自动跟随 3xx 重定向。SSRF 防护场景设为 false，由调用方逐跳复查目标。</summary>
        public bool AllowAutoRedirect { get; set; }

        public HttpOptions()
        {
            TimeoutMs = 30000;
            ApiKey = "";
            UserAgent = "";
            Accept = "";
            AllowAutoRedirect = true;
        }

        public static HttpOptions Of(int timeoutMs)
        {
            return new HttpOptions { TimeoutMs = timeoutMs };
        }

        public HttpOptions WithKey(string apiKey)
        {
            ApiKey = apiKey;
            return this;
        }
        /// <summary>3.23.0：附加自定义头（MCP 会话头、远程授权等）。</summary>
        public HttpOptions WithHeaders(Dictionary<string, string> headers)
        {
            Headers = headers;
            return this;
        }
        /// <summary>3.23.0：覆盖 Accept（MCP Streamable HTTP 需要 application/json, text/event-stream）。</summary>
        public HttpOptions WithAccept(string accept)
        {
            Accept = accept;
            return this;
        }

        public HttpOptions WithHeader(string name, string value)
        {
            if (Headers == null) Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Headers[name] = value;
            return this;
        }

        public HttpOptions WithToken(CancellationToken token)
        {
            Token = token;
            return this;
        }
    }

    /// <summary>
    /// 全项目唯一的 HTTP 出口。
    ///
    /// 之前有三份互不相干的 HttpWebRequest 实现（Cdp.cs / Brain.cs / Tools.cs），
    /// 各自的超时、代理、UA、错误处理都不一样：Bug 只在一个地方修，另两个地方照旧。
    /// 这里把它们收敛成一出口，同时补上两处 .NET Framework 默认的坑：
    ///   · DefaultConnectionLimit 默认每台主机只有 2 条 —— 本地同时打 CDP 与模型服务会排队；
    ///   · Expect100Continue 默认开启 —— 每个 POST 都多一次 100-continue 往返。
    /// </summary>
    public static class HttpUtil
    {
        private static bool _configured;

        static HttpUtil()
        {
            Configure();
        }

        /// <summary>幂等，重复调用无副作用。</summary>
        public static void Configure()
        {
            if (_configured) return;
            _configured = true;
            try
            {
                ServicePointManager.DefaultConnectionLimit = 64;
                ServicePointManager.Expect100Continue = false;
            }
            catch (Exception ex)
            {
                Log.Warn("HTTP 连接池参数配置失败，可能影响并发：" + ex.Message);
            }
        }

        private static HttpWebRequest CreateRequest(string url, string method, HttpOptions options)
        {
            HttpOptions o = options ?? HttpOptions.Of(30000);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Timeout = o.TimeoutMs;
            request.ReadWriteTimeout = o.TimeoutMs;
            request.Proxy = null;
            request.AllowAutoRedirect = o.AllowAutoRedirect;
            request.UserAgent = string.IsNullOrEmpty(o.UserAgent) ? "GreenTiger/" + AppInfo.Version : o.UserAgent;
            request.Accept = string.IsNullOrEmpty(o.Accept) ? "application/json,text/plain,*/*" : o.Accept;
            if (!string.IsNullOrEmpty(o.ApiKey)) request.Headers["Authorization"] = "Bearer " + o.ApiKey;
            if (o.Headers != null)
            {
                foreach (KeyValuePair<string, string> pair in o.Headers)
                {
                    if (string.IsNullOrEmpty(pair.Key)) continue;
                    if (string.Equals(pair.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        request.ContentType = pair.Value;
                        continue;
                    }
                    request.Headers[pair.Key] = pair.Value;
                }
            }
            return request;
        }

        private static HttpResponse Read(HttpWebResponse response)
        {
            HttpResponse result = new HttpResponse();
            result.Status = (int)response.StatusCode;
            result.Ok = result.Status >= 200 && result.Status < 300;
            try
            {
                result.ContentType = response.ContentType ?? "";
                result.Headers = new Dictionary<string, string>();
                foreach (string key in response.Headers.AllKeys)
                {
                    if (string.IsNullOrEmpty(key)) continue;
                    result.Headers[key] = response.Headers[key];
                }
            }
            catch { }
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                result.Body = reader.ReadToEnd();
            }
            if (!result.Ok) result.Error = "HTTP " + result.Status;
            return result;
        }

        private static HttpResponse Fail(Exception ex, string what)
        {
            HttpResponse result = new HttpResponse();
            WebException web = ex as WebException;
            if (web != null)
            {
                result.Timeout = web.Status == WebExceptionStatus.Timeout;
                HttpWebResponse response = web.Response as HttpWebResponse;
                if (response != null)
                {
                    // 4xx 响应体是关键诊断信息（如端点不支持 tools 需要降级协议），
                    // 读取失败必须留痕，不能静默退化成一句 ex.Message；同时释放响应对象。
                    try
                    {
                        using (response) { return Read(response); }
                    }
                    catch (Exception readEx)
                    {
                        Log.Warn("读取错误响应体失败: " + readEx.Message);
                    }
                }
            }
            result.Error = what + ": " + ex.Message;
            return result;
        }

        /// <summary>日志里不打完整 URL（可能带 query/token），只留 scheme://host/path 的前半段。</summary>
        private static string ShortUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int cut = url.IndexOf('?');
            string s = cut > 0 ? url.Substring(0, cut) : url;
            return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
        }

        /// <summary>GET / POST 的统一实现。带 body 时按 POST 处理。</summary>
        public static HttpResponse Request(string url, string method, string body, string contentType, HttpOptions options)
        {
            Configure();
            HttpOptions o = options ?? HttpOptions.Of(30000);
            if (o.Token.CanBeCanceled && o.Token.IsCancellationRequested)
            {
                HttpResponse cancelled = new HttpResponse();
                cancelled.Error = "已取消";
                return cancelled;
            }
            HttpWebRequest request = null;
            try
            {
                request = CreateRequest(url, method, o);
                if (body != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    request.ContentType = string.IsNullOrEmpty(contentType)
                        ? "application/json; charset=utf-8"
                        : contentType;
                    request.ContentLength = bytes.Length;
                    using (Stream stream = request.GetRequestStream())
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                if (o.Token.CanBeCanceled)
                {
                    CancellationTokenRegistration reg = o.Token.Register(delegate { try { request.Abort(); } catch { } });
                    using (reg)
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        return Read(response);
                    }
                }
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    return Read(response);
                }
            }
            catch (Exception ex)
            {
                return Fail(ex, (method ?? "GET") + " " + ShortUrl(url));
            }
        }

        public static HttpResponse Get(string url, HttpOptions options)
        {
            return Request(url, "GET", null, null, options);
        }

        public static HttpResponse Post(string url, string body, string contentType, HttpOptions options)
        {
            return Request(url, "POST", body, contentType, options);
        }

        public static HttpResponse PostJson(string url, string json, HttpOptions options)
        {
            return Post(url, json, "application/json; charset=utf-8", options);
        }

        /// <summary>只要文本的快捷方式：失败（含 4xx/5xx 无 body、网络错误、超时）返回 null。</summary>
        public static string GetString(string url, int timeoutMs)
        {
            return GetStringAuthorized(url, timeoutMs, "");
        }

        /// <summary>GET 文本；apiKey 非空时带 Authorization: Bearer。失败返回 null。</summary>
        public static string GetStringAuthorized(string url, int timeoutMs, string apiKey)
        {
            HttpResponse response = Get(url, HttpOptions.Of(timeoutMs).WithKey(apiKey));
            if (response.Ok || !string.IsNullOrEmpty(response.Body)) return response.Body;
            return null;
        }

        /// <summary>
        /// 保留给只需要 body 的调用点：成功返回响应体，4xx/5xx 也把响应体带回来（能看清服务端报错），
        /// 只有网络层彻底失败才返回 null。这是原先 Brain.HttpPost 的语义。
        /// </summary>
        public static string PostJsonText(string url, string json, int timeoutMs, string apiKey)
        {
            HttpResponse response = PostJson(url, json, HttpOptions.Of(timeoutMs).WithKey(apiKey));
            if (response.Ok || !string.IsNullOrEmpty(response.Body)) return response.Body;
            return null;
        }

        /// <summary>
        /// SSE 流式 POST：模型 API 原生 function calling 走这里。
        /// 服务端一边生成一边推 data: 行，onLine 逐行回调，调用方增量更新界面与累积 tool_calls。
        /// 返回 HttpResponse：Ok=true 时 Body 为空（内容已在回调里消费）；4xx/5xx 时 Body 保留错误体，
        /// 调用方据此做「端点不支持 tools / stream_options」的降级判断。
        /// 与 Request() 不同：TimeoutMs 只约束建立连接与单次读间隔，总时长由 token 与响应结束决定。
        /// </summary>
        public static HttpResponse PostJsonStream(string url, string json, int timeoutMs, string apiKey,
            Action<string> onLine, CancellationToken token)
        {
            Configure();
            HttpResponse result = new HttpResponse();
            HttpWebRequest request = null;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Accept = "text/event-stream";
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.Proxy = null;
                request.UserAgent = "GreenTiger/" + AppInfo.Version;
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey;
                }
                byte[] bytes = Encoding.UTF8.GetBytes(json ?? "");
                request.ContentLength = bytes.Length;
                using (Stream rs = request.GetRequestStream())
                {
                    rs.Write(bytes, 0, bytes.Length);
                }
                using (token.Register(delegate { try { request.Abort(); } catch { } }))
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    result.Status = (int)response.StatusCode;
                    result.Ok = true;
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (token.IsCancellationRequested) break;
                            if (line.Length == 0) continue;
                            try { onLine(line); } catch (OperationCanceledException) { throw; }
                            catch { /* 单行回调失败不终止流：下一行可能就恢复 */ }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.Error = "已取消";
                result.Timeout = true;
            }
            catch (WebException wex)
            {
                result.Error = "POST " + ShortUrl(url) + ": " + wex.Message;
                if (wex.Status == WebExceptionStatus.Timeout) result.Timeout = true;
                // 取消令牌触发 request.Abort() 后抛的是 WebException(RequestCanceled)，不是 OCE：
                // 这里统一识别，让上层按「用户停止」而不是「网络错误」处理。
                if (wex.Status == WebExceptionStatus.RequestCanceled || token.IsCancellationRequested)
                {
                    result.Error = "已取消";
                    result.Timeout = true;
                    return result;
                }
                HttpWebResponse errResp = wex.Response as HttpWebResponse;
                if (errResp != null)
                {
                    try
                    {
                        using (errResp)
                        {
                            result.Status = (int)errResp.StatusCode;
                            using (StreamReader reader = new StreamReader(errResp.GetResponseStream(), Encoding.UTF8))
                            {
                                result.Body = reader.ReadToEnd();
                            }
                        }
                    }
                    catch (Exception bodyEx)
                    {
                        // 端点不支持 tools 时靠这个响应体判断是否需要降级为文本围栏协议，读失败要留痕。
                        Log.Warn("读取流式请求的错误响应体失败: " + bodyEx.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = "POST " + ShortUrl(url) + ": " + ex.Message;
            }
            return result;
        }

        public static byte[] GetBytes(string url, int timeoutMs, int maxBytes)
        {
            return GetBytes(url, timeoutMs, maxBytes, CancellationToken.None);
        }

        /// <summary>带取消的下载：请求飞行中也能被「停止任务」打断，不用干等到 30 秒超时。</summary>
        /// <summary>
        /// 带逐跳 SSRF 防护的下载：关闭自动重定向，每次 3xx 由调用方复查目标后手动跟随。
        /// 防「审批时看的是公网 URL、302 后实际打内网」的绕过。hopAllowed 返回 false 抛 IOException。
        /// </summary>
        public static byte[] GetBytesGuarded(string url, int timeoutMs, int maxBytes, CancellationToken token, Func<string, bool> hopAllowed)
        {
            string current = url;
            for (int hop = 0; hop < 6; hop++)
            {
                if (hopAllowed != null && !hopAllowed(current))
                {
                    throw new IOException("重定向目标被安全策略拒绝（内网/受限地址）: " + current);
                }
                HttpOptions o = HttpOptions.Of(timeoutMs).WithToken(token);
                o.AllowAutoRedirect = false;
                byte[] body = null;
                int status = 0;
                string location = null;
                try
                {
                    HttpWebRequest request = CreateRequest(current, "GET", o);
                    using (token.Register(delegate { try { request.Abort(); } catch { } }))
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        status = (int)response.StatusCode;
                        if (status >= 300 && status < 400)
                        {
                            location = response.Headers["Location"];
                        }
                        else
                        {
                            using (Stream stream = response.GetResponseStream())
                            using (MemoryStream ms = new MemoryStream())
                            {
                                byte[] buffer = new byte[64 * 1024];
                                int read;
                                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    token.ThrowIfCancellationRequested();
                                    ms.Write(buffer, 0, read);
                                    if (maxBytes > 0 && ms.Length > maxBytes) throw new IOException("响应超过大小限制 " + Fs.HumanSize(maxBytes));
                                }
                                body = ms.ToArray();
                            }
                        }
                    }
                }
                catch (IOException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    throw new IOException("GET " + ShortUrl(current) + ": " + ex.Message);
                }
                if (body != null) return body;
                if (string.IsNullOrEmpty(location)) throw new IOException("HTTP " + status + "（无重定向目标）: " + ShortUrl(current));
                Uri baseUri = new Uri(current);
                Uri resolved;
                if (!Uri.TryCreate(baseUri, location, out resolved))
                {
                    throw new IOException("重定向地址无法解析: " + location);
                }
                current = resolved.ToString();
            }
            throw new IOException("重定向次数超过上限（6 次）: " + ShortUrl(url));
        }

        public static byte[] GetBytes(string url, int timeoutMs, int maxBytes, CancellationToken token)
        {
            Configure();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.Proxy = null;
            request.UserAgent = "GreenTiger/" + AppInfo.Version;
            using (token.Register(delegate { try { request.Abort(); } catch { } }))
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    ms.Write(buffer, 0, read);
                    if (maxBytes > 0 && ms.Length > maxBytes) throw new IOException("响应超过大小限制 " + Fs.HumanSize(maxBytes));
                }
                return ms.ToArray();
            }
        }

        public static bool PortOpen(int port)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(250)) return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        public static int FindFreePort(int start)
        {
            for (int port = start; port < start + 200 && port < 65535; port++)
            {
                if (PortOpen(port)) continue;
                // 双保险探测：HTTP 控制台最终由 HttpListener（http.sys）使用，CDP 调试端口由浏览器进程用原始
                // TCP 监听。任一探测成功都视为可用，避免单一探测在特定环境下误判（如 Winsock 层异常）。
                bool tcpOk = false;
                try
                {
                    TcpListener listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                    tcpOk = true;
                }
                catch { }
                bool httpOk = false;
                try
                {
                    HttpListener probe = new HttpListener();
                    probe.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    probe.Start();
                    probe.Stop();
                    httpOk = true;
                }
                catch { }
                if (tcpOk || httpOk) return port;
            }
            return 0;
        }
    }
}
