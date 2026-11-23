using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GreenTiger
{
    public class SseClient
    {
        public HttpListenerResponse Response;
        public Stream Stream;
        public ManualResetEvent Done;
        public object Gate = new object();

        public SseClient()
        {
            Done = new ManualResetEvent(false);
        }
    }

    public class WebServer
    {
        private const string FaviconBase64 =
            "PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAzMiAzMiI+PGRlZnM+PGxpbmVhckdyYWRpZW50IGlkPSJiZyIgeDE9IjAiIHkxPSIwIiB4Mj0iMSIgeTI9IjEiPjxzdG9wIG9mZnNldD0iMCIgc3RvcC1jb2xvcj0iIzEwM2QyNSIvPjxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iIzA1MTYwZSIvPjwvbGluZWFyR3JhZGllbnQ+PGxpbmVhckdyYWRpZW50IGlkPSJzdCIgeDE9IjAiIHkxPSIwIiB4Mj0iMSIgeTI9IjAiPjxzdG9wIG9mZnNldD0iMCIgc3RvcC1jb2xvcj0iI2M5ZmZkNiIvPjxzdG9wIG9mZnNldD0iMSIgc3RvcC1jb2xvcj0iIzNmYzY3MCIvPjwvbGluZWFyR3JhZGllbnQ+PC9kZWZzPjxyZWN0IHdpZHRoPSIzMiIgaGVpZ2h0PSIzMiIgcng9IjcuNCIgZmlsbD0idXJsKCNiZykiLz48cmVjdCB4PSIuNiIgeT0iLjYiIHdpZHRoPSIzMC44IiBoZWlnaHQ9IjMwLjgiIHJ4PSI3IiBmaWxsPSJub25lIiBzdHJva2U9IiM3ZWUwOTYiIHN0cm9rZS1vcGFjaXR5PSIuMzgiIHN0cm9rZS13aWR0aD0iLjciLz48ZyBmaWxsPSJ1cmwoI3N0KSI+PHBhdGggZD0iTTUuNCA4LjlDOS4yIDkuOCAxMy42IDEwLjkgMTkuNyAxMi41TDE5LjIgMTQuOEMxMy41IDEzLjIgOS4yIDEyLjEgNS4wIDExLjNaIi8+PHBhdGggZD0iTTMuOSAxNS43QzEwLjEgMTYuOCAxNi42IDE3LjkgMjMuMSAxOS4yTDIyLjQgMjIuMUMxNi4xIDIwLjYgOS45IDE5LjYgMy41IDE4LjVaIi8+PHBhdGggZD0iTTcuMSAyMy4xQzEwLjYgMjMuMyAxNS4xIDIzLjUgMTguOCAyNEwxOC42IDI2LjJDMTQuOSAyNS43IDEwLjYgMjUuNSA2LjkgMjUuM1oiLz48L2c+PHBhdGggZD0iTTIxLjkgMjYuMkgyNi40VjIyLjYiIGZpbGw9Im5vbmUiIHN0cm9rZT0iIzkyZWNhNiIgc3Ryb2tlLW9wYWNpdHk9Ii43IiBzdHJva2Utd2lkdGg9IjEuMSIgc3Ryb2tlLWxpbmVjYXA9InJvdW5kIiBzdHJva2UtbGluZWpvaW49InJvdW5kIi8+PGNpcmNsZSBjeD0iMjYuNCIgY3k9IjIyLjYiIHI9IjEuMTUiIGZpbGw9IiNjOWZmZDYiLz48L3N2Zz4=";

        private readonly App _app;
        private readonly bool _dev;
        private HttpListener _listener;
        private Thread _thread;
        private Thread _heartbeat;
        private volatile bool _running;
        private readonly List<SseClient> _clients = new List<SseClient>();
        private readonly object _clientsGate = new object();
        public string Token { get; private set; }
        public int Port { get; private set; }
        private bool _lan;
        private readonly List<string> _allowedOrigins = new List<string>();

        public WebServer(App app, bool dev)
        {
            _app = app;
            _dev = dev;
            Token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        }

        public string UiUrl
        {
            get { return "http://127.0.0.1:" + Port + "/?token=" + Token; }
        }

        public void Start(int preferredPort, bool lan)
        {
            int port = HttpUtil.FindFreePort(preferredPort > 0 ? preferredPort : 17890);
            if (port == 0) throw new InvalidOperationException("没有可用的本地 HTTP 端口");
            Port = port;
            _listener = new HttpListener();
            _lan = lan;
            bool started = false;
            if (lan)
            {
                // 手机端 PWA：允许局域网访问。绑定 "+" 需要 urlacl 授权，失败时回退本机并给出命令。
                try
                {
                    _listener.Prefixes.Add("http://+:" + port + "/");
                    _listener.Start();
                    started = true;                       // 已启动就不应再 Start 一次
                }
                catch (Exception ex)
                {
                    _lan = false;
                    Log.Warn("局域网绑定失败（" + ex.Message + "），回退本机访问。需要先以管理员运行：netsh http add urlacl url=http://+:" + port + "/ user=Everyone");
                    try { _listener.Stop(); } catch { }
                    try { _listener.Close(); } catch { }
                    _listener = new HttpListener();
                    _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                }
            }
            else
            {
                _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            }
            if (!started)
            {
                try { _listener.Start(); }
                catch (Exception ex) { throw new InvalidOperationException("无法启动控制台服务：" + ex.Message, ex); }
            }
            BuildAllowedOrigins();
            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "GreenTigerHttp";
            _thread.Start();
            _heartbeat = new Thread(HeartbeatLoop);
            _heartbeat.IsBackground = true;
            _heartbeat.Name = "GreenTigerSseHeartbeat";
            _heartbeat.Start();
            Log.Info("控制台已启动: http://127.0.0.1:" + port + "/");
            // 令牌不写日志：日志可能被同步/分享，完整带令牌地址只放在数据目录的 ui.url 里。
            if (lan) Log.Info("局域网模式已启用，手机端访问：http://<本机局域网IP>:" + port +
                "/ （完整带令牌地址见数据目录的 ui.url 文件）");
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            try { if (_listener != null) _listener.Close(); } catch { }
            List<SseClient> clients;
            lock (_clientsGate)
            {
                clients = new List<SseClient>(_clients);
                _clients.Clear();
            }
            for (int i = 0; i < clients.Count; i++)
            {
                try { clients[i].Done.Set(); } catch { }
                try { clients[i].Response.Close(); } catch { }
            }
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext context = null;
                try { context = _listener.GetContext(); }
                catch (Exception ex)
                {
                    if (_running) Log.Warn("HTTP 监听异常: " + ex.Message);
                    continue;
                }
                ThreadPool.QueueUserWorkItem(delegate(object state) { Handle((HttpListenerContext)state); }, context);
            }
        }

        private void HeartbeatLoop()
        {
            while (_running)
            {
                Thread.Sleep(15000);
                BroadcastRaw(": ping\n\n");
            }
        }

        private void Handle(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            try
            {
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["Referrer-Policy"] = "no-referrer";
                response.Headers["X-Frame-Options"] = "DENY";
                string path = request.Url.AbsolutePath;
                if (path == "/api/health")
                {
                    // 未鉴权探针只回存活状态，不回版本号，减少局域网/本机信息暴露。
                    SendJson(response, 200, "{\"ok\":true}");
                    return;
                }
                if (path == "/favicon.ico")
                {
                    byte[] icon = Convert.FromBase64String(FaviconBase64);
                    response.StatusCode = 200;
                    response.ContentType = "image/svg+xml";
                    response.Headers["Cache-Control"] = "public, max-age=86400";
                    response.ContentLength64 = icon.Length;
                    response.OutputStream.Write(icon, 0, icon.Length);
                    response.Close();
                    return;
                }
                if (path == "/" || path == "/index.html")
                {
                    if (!AuthorizeUi(request, response)) return;
                    ServeUi(response);
                    return;
                }
                if (path == "/theme.css" || path == "/shell.js" || path == "/garden.css" || path == "/garden.js"
                    || path == "/style.css" || IsUiModule(path))
                {
                    if (!HasTokenCookie(request)) { ResponseText(response, 401, "unauthorized"); return; }
                    bool isJs = path.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
                    ServeAsset(request, response, path.Substring(1), isJs
                        ? "application/javascript; charset=utf-8"
                        : "text/css; charset=utf-8");
                    return;
                }
                if (path == "/manifest.webmanifest")
                {
                    if (!HasTokenCookie(request)) { ResponseText(response, 401, "unauthorized"); return; }
                    ServeAsset(request, response, "manifest.webmanifest", "application/manifest+json; charset=utf-8");
                    return;
                }
                if (path == "/icons/icon-192.png" || path == "/icons/icon-512.png" || path == "/icons/swirl.png")
                {
                    if (!HasTokenCookie(request)) { ResponseText(response, 401, "unauthorized"); return; }
                    string asset = path == "/icons/icon-192.png" ? "icon-192.png" : (path == "/icons/icon-512.png" ? "icon-512.png" : "swirl.png");
                    ServeAsset(request, response, asset, "image/png");
                    return;
                }
                // 统一出口：所有 /api 与 /v1 路由先过 Origin 校验，
                // 避免早期分支（v1 / explorer / artifact / events）绕过同源检查。
                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!CheckOrigin(request, response)) return;
                }
                if (path == "/v1/models")
                {
                    if (!AuthorizeApi(request, response)) return;
                    SendJson(response, 200, "{\"object\":\"list\",\"data\":[{\"id\":\"greentiger\",\"object\":\"model\",\"created\":1700000000,\"owned_by\":\"green-tiger\"}]}");
                    return;
                }
                if (path == "/v1/chat/completions")
                {
                    if (!AuthorizeApi(request, response)) return;
                    if (request.HttpMethod != "POST") { SendJson(response, 405, "{\"error\":{\"message\":\"POST required\"}}"); return; }
                    Dictionary<string, object> body = ReadJson(request);
                    List<object> msgs = Json.Array(body, "messages");
                    StringBuilder prompt = new StringBuilder();
                    for (int i = 0; i < msgs.Count; i++)
                    {
                        Dictionary<string, object> m = msgs[i] as Dictionary<string, object>;
                        if (m == null) continue;
                        prompt.AppendLine(Json.Str(m, "role") + ": " + Json.Str(m, "content"));
                    }
                    Task<string> runner = _app.RunChatExternalAsync(prompt.ToString());
                    // 注意：HTTP 请求在线程池线程上处理，这里的同步等待会占住线程十分钟。
                    // 外部兼容性端点调用极少，且再套一层 Task.Run(...).Wait() 只会白白多借一个线程，
                    // 所以保留同步等待，但不再额外包装。
                    try { runner.Wait(TimeSpan.FromMinutes(10)); }
                    catch (AggregateException agex)
                    {
                        Exception inner = agex.InnerException ?? agex;
                        SendJson(response, 500, "{\"error\":{\"message\":" + Json.Stringify(inner.Message) + "}}");
                        return;
                    }
                    if (!runner.IsCompleted) { SendJson(response, 504, "{\"error\":{\"message\":\"任务超时\"}}"); return; }
                    SendJson(response, 200, "{\"id\":\"chatcmpl-ext\",\"object\":\"chat.completion\",\"created\":" + TimeUtil.UnixSeconds()
                        + ",\"model\":\"greentiger\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
                        + Json.Stringify(runner.Result) + "},\"finish_reason\":\"stop\"}]}");
                    return;
                }
                if (path == "/api/explorer")
                {
                    if (!AuthorizeApi(request, response)) return;
                    string rel = "";
                    if (request.HttpMethod == "POST" && request.HasEntityBody)
                    {
                        try
                        {
                            string raw = new System.IO.StreamReader(request.InputStream, System.Text.Encoding.UTF8).ReadToEnd();
                            Dictionary<string, object> dict = Json.ParseObject(raw);
                            if (dict != null && dict.ContainsKey("path")) rel = Json.Str(dict, "path");
                        }
                        catch (Exception ex)
                        {
                            // 请求体畸形不是致命错误，但要留痕，否则前端「打开目录」静默失效无从排查。
                            Log.Warn("explorer 请求体解析失败: " + ex.Message);
                        }
                    }
                    bool ok = _app.OpenExplorer(rel);
                    SendJson(response, ok ? 200 : 400, ok ? "{\"ok\":true}" : "{\"ok\":false,\"error\":\"无法打开目录\"}");
                    return;
                }
                if (path.StartsWith("/api/artifact/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!AuthorizeApi(request, response)) return;
                    ServeArtifact(response, path.Substring("/api/artifact/".Length));
                    return;
                }
                if (path == "/api/events")
                {
                    if (!AuthorizeApi(request, response)) return;
                    ServeEvents(context);
                    return;
                }
                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!AuthorizeApi(request, response)) return;
                    if (request.HttpMethod == "OPTIONS") { response.StatusCode = 204; response.Close(); return; }
                    if (!CheckOrigin(request, response)) return;
                    HandleApi(request, response, path);
                    return;
                }
                ResponseText(response, 404, "Not Found");
            }
            catch (Exception ex)
            {
                Log.Error("HTTP 请求处理失败: " + request.Url, ex);
                try { SendJson(response, 500, "{\"ok\":false,\"error\":" + Json.Stringify(ex.Message) + "}"); }
                catch { }
            }
        }

        private bool AuthorizeUi(HttpListenerRequest request, HttpListenerResponse response)
        {
            string token = request.QueryString["token"];
            if (!string.IsNullOrEmpty(token) && token == Token)
            {
                response.AppendHeader("Set-Cookie", "greentiger_token=" + Token + "; HttpOnly; SameSite=Lax; Path=/; Max-Age=604800");
                response.StatusCode = 302;
                response.RedirectLocation = "/";
                response.Close();
                return false;
            }
            if (HasTokenCookie(request)) return true;
            string html = "<!doctype html><meta charset=utf-8><title>绿虎</title><style>body{background:#f4f9ee;color:#1c2a1a;font-family:system-ui;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}div{max-width:520px;padding:32px;border:1px solid #cfe2c0;border-radius:18px;background:#fcfef8}p{color:#70855f;line-height:1.7}</style><div><h2>绿虎</h2><p>请从系统托盘的绿虎图标选择「打开绿虎浏览器」。<br>直接访问此地址没有携带本次启动的访问令牌。</p></div>";
            ResponseText(response, 403, html, "text/html; charset=utf-8");
            return false;
        }

        private bool AuthorizeApi(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (HasTokenCookie(request)) return true;
            string token = request.Headers["X-GreenTiger-Token"];
            if (token == Token) return true;
            SendJson(response, 401, "{\"ok\":false,\"error\":\"unauthorized\"}");
            return false;
        }

        private bool HasTokenCookie(HttpListenerRequest request)
        {
            string cookie = request.Headers["Cookie"];
            if (string.IsNullOrEmpty(cookie)) return false;
            string[] parts = cookie.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                // 访问令牌 Cookie 名固定为 greentiger_token
                string name = part.Substring(0, eq).Trim();
                string value = part.Substring(eq + 1).Trim();
                if (value == Token && name == "greentiger_token") return true;
            }
            return false;
        }

        private bool CheckOrigin(HttpListenerRequest request, HttpListenerResponse response)
        {
            string origin = request.Headers["Origin"];
            if (string.IsNullOrEmpty(origin)) return true;
            // 严格白名单：本机回环 + （局域网模式下）本机 IPv4；不匹配一律 403，不做通配放行。
            if (_allowedOrigins.Contains(origin)) return true;
            SendJson(response, 403, "{\"ok\":false,\"error\":\"bad origin\"}");
            return false;
        }

        /// <summary>构建允许发起请求的来源集合。局域网模式下页面来源是 http://&lt;局域网IP&gt;:port，
        /// 只放行回环会让 PWA 遥控器上所有写接口 403，因此把本机 IPv4 一并加入（仍限定为本机地址）。</summary>
        private void BuildAllowedOrigins()
        {
            _allowedOrigins.Clear();
            _allowedOrigins.Add("http://127.0.0.1:" + Port);
            _allowedOrigins.Add("http://localhost:" + Port);
            if (!_lan) return;
            try
            {
                foreach (IPAddress ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        string a = "http://" + ip + ":" + Port;
                        if (!_allowedOrigins.Contains(a)) _allowedOrigins.Add(a);
                    }
                }
            }
            catch (Exception ex)
            {
                // 局域网模式下 DNS 枚举失败会让所有写接口 403，必须留痕，否则排查无从下手。
                Log.Warn("构建 Origin 白名单时枚举本机 IPv4 失败（局域网模式可能不可用）：" + ex.Message);
            }
        }

        private void ServeUi(HttpListenerResponse response)
        {
            byte[] bytes = LoadAsset("index.html");
            if (bytes == null) bytes = Encoding.UTF8.GetBytes("<h1>UI missing</h1>");
            // 阶段1/主题修复：服务器下发时直接把主题钉死在 <html>，不依赖 localStorage/JS 时序，
            // 避免 WebView2 缓存旧页 + 残留 localStorage 导致外壳(深色)与页面(浅色)不一致。
            string theme = Config.ResolveTheme(_app.Config == null ? "auto" : _app.Config.Theme);
            string html = Encoding.UTF8.GetString(bytes);
            // 缓存戳占位由服务端注入：UI 源码里不写死产品版本号，升版本只需改 AppInfo.Version。
            html = html.Replace("__GT_VERSION__", AppInfo.Version);
            // 直接改 <html data-theme> 与 meta，到达浏览器就是正确配色，不依赖任何 JS 时序
            html = System.Text.RegularExpressions.Regex.Replace(
                html, @"data-theme=""[a-z]+""", "data-theme=\"" + theme + "\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = System.Text.RegularExpressions.Regex.Replace(
                html, @"name=""color-scheme"" content=""[a-z]+""", "name=\"color-scheme\" content=\"" + theme + "\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = System.Text.RegularExpressions.Regex.Replace(
                html, @"name=""theme-color"" content=""#[0-9a-fA-F]+""", "name=\"theme-color\" content=\"" + (theme == "dark" ? "#000000" : "#4ca324") + "\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string inject = "<script>try{localStorage.setItem('greentiger.theme','" + theme + "');}catch(e){}</script>";
            int headIdx = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
            if (headIdx >= 0) html = html.Substring(0, headIdx + 6) + inject + html.Substring(headIdx + 6);
            else html = inject + html;
            bytes = Encoding.UTF8.GetBytes(html);
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'; font-src 'self' data:; frame-src * data: blob:; child-src *";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }

        /// <summary>前端静态资源（主题、交互组件、motion、workspace）。带 ETag 协商缓存，文件变化时自动失效。</summary>
        private void ServeAsset(HttpListenerRequest request, HttpListenerResponse response, string name, string mime)
        {
            string etag;
            byte[] bytes = LoadAsset(name, out etag);
            if (bytes == null) { ResponseText(response, 404, "Not Found"); return; }
            // 协商缓存：浏览器发来的 If-None-Match 与当前 etag 一致则返回 304，省带宽。
            // 用户在数据目录改了 UI 文件后，mtime 变化会让 etag 变化，浏览器自动重新拉取。
            string incoming = request.Headers["If-None-Match"];
            if (!string.IsNullOrEmpty(etag) && incoming == etag)
            {
                response.StatusCode = 304;
                response.Headers["ETag"] = etag;
                response.Close();
                return;
            }
            response.StatusCode = 200;
            response.ContentType = mime;
            if (!string.IsNullOrEmpty(etag)) response.Headers["ETag"] = etag;
            response.Headers["Cache-Control"] = "no-cache";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }


        // P3：ES 模块白名单。只放行已知文件名（当前七个），杜绝 /js/../ 之类的路径逃逸。
        private static readonly string[] UiModules =
        {
            "util.js", "markdown.js", "tools.js", "chat.js", "model.js", "app.js", "toolbox.js"
        };

        private static bool IsUiModule(string path)
        {
            if (path == null || !path.StartsWith("/js/", StringComparison.Ordinal)) return false;
            string name = path.Substring(4);
            return Array.IndexOf(UiModules, name) >= 0;
        }

        private static string MimeOfName(string name)
        {
            string lower = name.ToLowerInvariant();
            if (lower.EndsWith(".js")) return "application/javascript; charset=utf-8";
            if (lower.EndsWith(".css")) return "text/css; charset=utf-8";
            if (lower.EndsWith(".html") || lower.EndsWith(".htm")) return "text/html; charset=utf-8";
            if (lower.EndsWith(".json")) return "application/json; charset=utf-8";
            if (lower.EndsWith(".svg")) return "image/svg+xml";
            if (lower.EndsWith(".png")) return "image/png";
            if (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg")) return "image/jpeg";
            if (lower.EndsWith(".gif")) return "image/gif";
            if (lower.EndsWith(".webp")) return "image/webp";
            if (lower.EndsWith(".ico")) return "image/x-icon";
            if (lower.EndsWith(".woff2")) return "font/woff2";
            if (lower.EndsWith(".woff")) return "font/woff";
            if (lower.EndsWith(".ttf")) return "font/ttf";
            if (lower.EndsWith(".mp3")) return "audio/mpeg";
            if (lower.EndsWith(".mp4")) return "video/mp4";
            return "application/octet-stream";
        }

        /// <summary>
        /// 正式版读取嵌入资源。开发模式优先读取数据目录及源码目录。
        /// etag 基于文件最后修改时间+大小；嵌入资源用版本号。文件变化时 etag 自动变化，浏览器会重新下载。
        /// </summary>
        private byte[] LoadAsset(string name, out string etag)
        {
            etag = null;
            string dataFile = Path.Combine(AppPaths.UiDir, name);
            if (_dev && File.Exists(dataFile))
            {
                etag = "d-" + File.GetLastWriteTimeUtc(dataFile).Ticks.ToString() + "-" + new FileInfo(dataFile).Length;
                return File.ReadAllBytes(dataFile);
            }
            if (_dev)
            {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", name);
                if (!File.Exists(file)) file = Path.Combine(Environment.CurrentDirectory, "ui", name);
                if (File.Exists(file))
                {
                    etag = "d-" + File.GetLastWriteTimeUtc(file).Ticks.ToString() + "-" + new FileInfo(file).Length;
                    return File.ReadAllBytes(file);
                }
            }
            Stream stream = typeof(WebServer).Assembly.GetManifestResourceStream("GreenTiger.ui." + name);
            if (stream == null) return null;
            etag = "r-" + AppInfo.Version;
            // 资源流必须在任何异常路径上都释放（以前只在 CopyTo 成功后 Dispose）。
            using (stream)
            using (MemoryStream ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private byte[] LoadAsset(string name)
        {
            string etag;
            return LoadAsset(name, out etag);
        }

        private void ServeArtifact(HttpListenerResponse response, string id)
        {
            Artifact artifact = _app.GetArtifact(id);
            if (artifact == null || !File.Exists(artifact.Path))
            {
                ResponseText(response, 404, "artifact not found");
                return;
            }
            byte[] bytes = File.ReadAllBytes(artifact.Path);
            string name = Fs.SafeName(artifact.Name);
            string mime = string.IsNullOrEmpty(artifact.Mime) ? "application/octet-stream" : artifact.Mime;
            bool inline = (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                           mime.IndexOf("svg", StringComparison.OrdinalIgnoreCase) < 0) ||
                          mime.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) ||
                          mime.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
            response.StatusCode = 200;
            response.ContentType = mime;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Content-Disposition"] = ContentDisposition(name, inline);
            response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }


        /* ---------- Toolbox routes: /api/toolbox/<op> (POST JSON) ----------
         * search          {q, limit}            -> 本机索引实时搜索结果（只读）
         * scan            {scopes}              -> local risk scan findings (read-only)
         * uninstall_list  {}                    -> installed programs (read-only)
         * uninstall       {name, confirm}       -> launch standard uninstaller (confirm)
         * shred_preview   {paths}               -> guard/size preview, deletes nothing (read-only)
         * shred           {paths, confirm}      -> 3-pass overwrite destroy (confirm)
         * reveal          {path}                -> explorer select/locate (read-only, no confirm)
         * clean           {finding_id, confirm} -> whitelist cache clean (confirm)
         * grant_clean     {confirm}             -> persistent allow for pc_clean_cache only (confirm) */
        private void HandleToolbox(HttpListenerRequest request, HttpListenerResponse response, string op)
        {
            if (request.HttpMethod.ToUpperInvariant() != "POST")
            {
                SendJson(response, 405, "{\"ok\":false,\"error\":\"method not allowed\"}");
                return;
            }
            Dictionary<string, object> body = ReadJson(request);
            string error = null;
            switch (op)
            {
                case "search":
                {
                    string q = Json.Str(body, "q", "");
                    int limit = Json.Int(body, "limit", 100);
                    bool ok;
                    List<Dictionary<string, object>> items = LocalOps.SearchRows(q, limit, Json.Str(body, "sort", ""), out ok, out error);
                    if (!ok) { SendJson(response, 400, ToolboxError(error)); return; }
                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["items"] = items;
                    SendJson(response, 200, Json.Stringify(ToolboxOk(payload)));
                    return;
                }
                case "scan":
                {
                    List<LocalOps.ScanFinding> findings = LocalOps.ScanFindings(Json.Str(body, "scopes", "caches,large,installer,startup"));
                    List<object> rows = new List<object>();
                    long totalBytes = 0;
                    foreach (LocalOps.ScanFinding f in findings)
                    {
                        totalBytes += f.Bytes;
                        Dictionary<string, object> row = new Dictionary<string, object>();
                        row["id"] = f.Id;
                        row["title"] = f.Title;
                        row["category"] = f.Category;
                        row["bytes"] = f.Bytes;
                        row["human"] = LocalOps.HumanBytes(f.Bytes);
                        row["files"] = f.Files;
                        row["risk"] = f.Risk;
                        row["explain"] = f.Explain;
                        row["can_clean"] = f.CanClean;
                        row["paths"] = f.Paths;
                        rows.Add(row);
                    }
                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["items"] = rows;
                    payload["total"] = LocalOps.HumanBytes(totalBytes);
                    payload["disks"] = LocalOps.DiskRows();
                    SendJson(response, 200, Json.Stringify(ToolboxOk(payload)));
                    return;
                }
                case "uninstall_list":
                {
                    List<string[]> items = LocalOps.UninstallEntries();
                    List<object> rows = new List<object>();
                    foreach (string[] it in items)
                    {
                        Dictionary<string, object> row = new Dictionary<string, object>();
                        row["name"] = it[0];
                        row["version"] = it[1];
                        row["publisher"] = it[2];
                        row["size"] = it[3];
                        long sizeBytes = 0;
                        if (it.Length > 5 && it[5].Length > 0)
                        {
                            try { sizeBytes = Convert.ToInt64(it[5]) * 1024L; } catch { }
                        }
                        row["size_bytes"] = sizeBytes;
                        rows.Add(row);
                    }
                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["items"] = rows;
                    SendJson(response, 200, Json.Stringify(ToolboxOk(payload)));
                    return;
                }
                case "uninstall":
                {
                    if (!ToolboxConfirmed(body)) { SendJson(response, 400, ToolboxError("Missing confirmation flag: this will launch the system uninstaller, please confirm in the dialog first.")); return; }
                    ToolResult r = LocalOps.UninstallLaunchByName(Json.Str(body, "name", ""));
                    if (!r.Ok) { SendJson(response, 400, ToolboxError(r.Error)); return; }
                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["message"] = r.Output;
                    SendJson(response, 200, Json.Stringify(ToolboxOk(payload)));
                    return;
                }
                case "shred_preview":
                {
                    List<Dictionary<string, object>> items = LocalOps.ShredPreviewList(LocalOps.PathList(body, "paths"));
                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["items"] = items;
                    SendJson(response, 200, Json.Stringify(ToolboxOk(payload)));
                    return;
                }
                case "shred":
                {
                    if (!ToolboxConfirmed(body)) { SendJson(response, 400, ToolboxError("Missing confirmation flag: shredding is unrecoverable, please confirm in the dialog first.")); return; }
                    Dictionary<string, object> args = new Dictionary<string, object>();
                    args["paths"] = body.ContainsKey("paths") ? body["paths"] : null;
                    args["passes"] = 3;
                    ToolResult r = LocalOps.Shred(args);
                    if (!r.Ok) { SendJson(response, 400, ToolboxError(r.Error)); return; }
                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["message"] = r.Output;
                    SendJson(response, 200, Json.Stringify(ToolboxOk(payload)));
                    return;
                }
                case "reveal":
                {
                    string revealError;
                    if (!LocalOps.RevealPath(Json.Str(body, "path", ""), out revealError))
                    {
                        SendJson(response, 400, ToolboxError(revealError));
                        return;
                    }
                    Dictionary<string, object> revealPayload = new Dictionary<string, object>();
                    revealPayload["message"] = "已定位";
                    SendJson(response, 200, Json.Stringify(ToolboxOk(revealPayload)));
                    return;
                }
                case "clean":
                {
                    if (!ToolboxConfirmed(body)) { SendJson(response, 400, ToolboxError("Missing confirmation flag: files will be permanently deleted, please confirm in the dialog first.")); return; }
                    long freed; int skipped; string target; bool ok; string err;
                    LocalOps.CleanById(Json.Str(body, "finding_id", ""), out freed, out skipped, out target, out ok, out err);
                    if (!ok) { SendJson(response, 400, ToolboxError(err)); return; }
                    Dictionary<string, object> payload = new Dictionary<string, object>();
                    payload["message"] = "Cleaned " + target + ": freed " + LocalOps.HumanBytes(freed) + ", skipped " + skipped + " in-use files.";
                    SendJson(response, 200, Json.Stringify(ToolboxOk(payload)));
                    return;
                }
                case "grant_clean":
                {
                    // 用户显式授权：只放行 pc_clean_cache（LocalOps 仍只认白名单 finding_id），
                    // 不碰 pc_shred / pc_uninstall_launch 等更危险的动作。
                    if (!ToolboxConfirmed(body)) { SendJson(response, 400, ToolboxError("Missing confirmation flag: permission changes need explicit confirmation.")); return; }
                    _app.ApplyPersistentAllow("pc_clean_cache");
                    Dictionary<string, object> grant = new Dictionary<string, object>();
                    grant["message"] = "已授权绿虎清理白名单缓存（可在 设置 → 权限规则 撤销）。粉碎与卸载仍需逐次确认。";
                    SendJson(response, 200, Json.Stringify(ToolboxOk(grant)));
                    return;
                }
                default:
                    SendJson(response, 404, "{\"ok\":false,\"error\":\"unknown toolbox op\"}");
                    return;
            }
        }

        private static bool ToolboxConfirmed(Dictionary<string, object> body)
        {
            return body != null && body.ContainsKey("confirm") && body["confirm"] is bool && (bool)body["confirm"];
        }

        private static string ToolboxError(string message)
        {
            Dictionary<string, object> dto = new Dictionary<string, object>();
            dto["ok"] = false;
            dto["error"] = string.IsNullOrEmpty(message) ? "Operation failed" : message;
            return Json.Stringify(dto);
        }

        private static Dictionary<string, object> ToolboxOk(Dictionary<string, object> payload)
        {
            Dictionary<string, object> dto = new Dictionary<string, object>();
            dto["ok"] = true;
            if (payload != null)
            {
                foreach (KeyValuePair<string, object> kv in payload)
                {
                    dto[kv.Key] = kv.Value;
                }
            }
            return dto;
        }
        private static string ContentDisposition(string name, bool inline)
        {
            StringBuilder ascii = new StringBuilder();
            for (int i = 0; i < name.Length && ascii.Length < 100; i++)
            {
                char c = name[i];
                ascii.Append(c >= 32 && c < 127 && c != '"' && c != '\\' ? c : '_');
            }
            string fallback = ascii.ToString().Trim();
            if (fallback.Length == 0) fallback = "file";
            string encoded = Uri.EscapeDataString(name)
                .Replace("'", "%27").Replace("(", "%28").Replace(")", "%29").Replace("*", "%2A");
            return (inline ? "inline" : "attachment") + "; filename=\"" + fallback + "\"; filename*=UTF-8''" + encoded;
        }

        private void HandleApi(HttpListenerRequest request, HttpListenerResponse response, string path)
        {
            string method = request.HttpMethod.ToUpperInvariant();
            // watch mode 活动打点：除轮询/长连接外的 API 请求都算用户活动（界面点击/发消息/审批）。
            if (path != "/api/state" && path != "/api/events" && path != "/api/health")
            {
                _app.TouchUserActivity();
            }
            if (path == "/api/state" && method == "GET")
            {
                SendJson(response, 200, _app.StateJson());
                return;
            }
            if (path == "/api/session/export" && method == "GET")
            {
                string sessionId = QueryValue(request, "id");
                string markdown = _app.ExportMarkdown(sessionId);
                if (string.IsNullOrEmpty(markdown))
                {
                    SendJson(response, 404, "{\"ok\":false,\"error\":\"会话不存在\"}");
                    return;
                }
                byte[] mdBytes = Encoding.UTF8.GetBytes(markdown);
                response.StatusCode = 200;
                response.ContentType = "text/markdown; charset=utf-8";
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["Content-Disposition"] = ContentDisposition(_app.ExportFileName(sessionId), false);
                response.ContentLength64 = mdBytes.Length;
                response.OutputStream.Write(mdBytes, 0, mdBytes.Length);
                response.Close();
                return;
            }
            if (path == "/api/rules" && method == "GET")
            {
                SendJson(response, 200, Json.Stringify(_app.RulesSummary()));
                return;
            }
            /* ---------- 内置文件浏览器（只读部分） ---------- */
            if (path == "/api/fs/list" && method == "GET")
            {
                string p = QueryValue(request, "path");
                bool hidden = QueryValue(request, "hidden") == "1";
                if (QueryValue(request, "picker") == "1" || QueryValue(request, "mode") == "disk")
                    SendJson(response, 200, _app.Files.ListPickerJson(p, hidden));
                else
                    SendJson(response, 200, _app.Files.ListJson(p, hidden));
                return;
            }
            /* ---------- Toolbox (GUI): structured JSON, all logic reuses LocalOps ---------- */
            if (path.StartsWith("/api/toolbox/", StringComparison.OrdinalIgnoreCase))
            {
                if (!AuthorizeApi(request, response)) return;
                HandleToolbox(request, response, path.Substring("/api/toolbox/".Length));
                return;
            }
            /* ---------- 外部工具（MCP）：GET 必须在 405 分支之前 ---------- */
            if (path == "/api/mcp/servers" && method == "GET")
            {
                SendJson(response, 200, Json.Stringify(_app.McpServersDto()));
                return;
            }
            if (path == "/api/mcp/discover" && method == "GET")
            {
                SendJson(response, 200, Json.Stringify(_app.DiscoverMcpDto()));
                return;
            }
            if (path == "/api/ping")
            {
                _app.TouchUserActivity();
                SendJson(response, 200, "{\"ok\":true}");
                return;
            }
            if (path == "/api/memory/candidates")
            {
                Dictionary<string, object> dto = new Dictionary<string, object>();
                dto["candidates"] = _app.MemoryCandidatesDto();
                dto["globalLines"] = AgentMemory.GlobalLineCount();
                SendJson(response, 200, Json.Stringify(dto));
                return;
            }
            if (method != "POST")
            {
                SendJson(response, 405, "{\"ok\":false,\"error\":\"method not allowed\"}");
                return;
            }
            if (path == "/api/chat")
            {
                Dictionary<string, object> body = ReadJson(request);
                string text = Json.Str(body, "text");
                List<string> files = new List<string>();
                List<object> arr = Json.Array(body, "files");
                for (int i = 0; i < arr.Count; i++) if (arr[i] != null) files.Add(Convert.ToString(arr[i]));
                _app.StartChat(text, files.ToArray());
                SendJson(response, 200, "{\"ok\":true}");
                return;
            }
            if (path == "/api/stop")
            {
                _app.Stop();
                SendJson(response, 200, "{\"ok\":true}");
                return;
            }
            if (path == "/api/task/pause")
            {
                // 阶段4：暂停不是终态——任务记为 paused 并保留续跑能力
                _app.Pause();
                SendJson(response, 200, "{\"ok\":true}");
                return;
            }
            if (path == "/api/retry")
            {
                _app.RetryLast();
                SendJson(response, 200, "{\"ok\":true}");
                return;
            }
            if (path == "/api/task/resume")
            {
                // 阶段4：继续上次中断/等待审批的任务（复用原任务记录与预算，不新建任务）
                // 可选 body {taskId}：从「可继续」暂存区取回指定任务再续跑
                Dictionary<string, object> resumeBody = ReadJson(request);
                string resumeTaskId = resumeBody == null ? "" : Json.Str(resumeBody, "taskId", "");
                bool ok = _app.ResumeActiveTask(resumeTaskId);
                SendJson(response, 200, ok
                    ? "{\"ok\":true}"
                    : "{\"ok\":false,\"error\":\"任务当前不可继续（原因已写入会话提示）\"}");
                return;
            }
            if (path == "/api/session/edit")
            {
                Dictionary<string, object> body = ReadJson(request);
                int index = Json.Int(body, "index", -1);
                string text = Json.Str(body, "text");
                _app.EditMessage(index, text);
                SendJson(response, 200, "{\"ok\":true}");
                return;
            }
            if (path == "/api/session/undo")
            {
                Dictionary<string, object> body = ReadJson(request);
                int index = Json.Int(body, "index", -1);
                _app.UndoMessage(index);
                SendJson(response, 200, "{\"ok\":true}");
                return;
            }
            if (path == "/api/approval")
            {
                Dictionary<string, object> body = ReadJson(request);
                string id = Json.Str(body, "id");
                string decision = Json.Str(body, "decision", "deny");
                string answer = Json.Str(body, "answer", "");
                if (answer.Length > 2000) answer = answer.Substring(0, 2000);
                _app.ResolveApproval(id, decision, answer);
                SendJson(response, 200, "{\"ok\":true}");
                return;
            }
            if (path == "/api/mcp/servers" && method == "POST")
            {
                Dictionary<string, object> body = ReadJson(request);
                try
                {
                    _app.SaveMcpServer(body);
                    SendJson(response, 200, Json.Stringify(_app.McpServersDto()));
                }
                catch (InvalidOperationException ex)
                {
                    SendJson(response, 400, "{\"ok\":false,\"error\":" + Json.Stringify(ex.Message) + "}");
                }
                return;
            }
            if (path == "/api/config")
            {
                Dictionary<string, object> body = ReadJson(request);
                // 业务错误（如工作目录不存在）与其它路由保持一致：400 + 结构化 JSON，而不是退化成 500
                try
                {
                    _app.UpdateConfig(body);
                    SendJson(response, 200, _app.StateJson());
                }
                catch (InvalidOperationException ex)
                {
                    SendJson(response, 400, "{\"ok\":false,\"error\":" + Json.Stringify(ex.Message) + "}");
                }
                return;
            }
            if (path == "/api/backups/restore")
            {
                Dictionary<string, object> rb = ReadJson(request);
                string snapshot = Json.Str(rb, "snapshot", "");
                string target = Json.Str(rb, "path", "");
                string err;
                string restored = _app.RestoreBackup(snapshot, target, out err);
                if (restored.Length == 0)
                {
                    SendJson(response, 400, "{\"ok\":false,\"error\":" + Json.Stringify(err) + "}");
                    return;
                }
                SendJson(response, 200, "{\"ok\":true,\"path\":" + Json.Stringify(restored) + "}");
                return;
            }
            if (path == "/api/backups/restore_checkpoint")
            {
                // 界面「回滚到命令前」：shell checkpoint 的目录级恢复
                Dictionary<string, object> rb = ReadJson(request);
                string manifest = Json.Str(rb, "manifest", "");
                string err;
                string summary = _app.RestoreCheckpoint(manifest, out err);
                if (summary.Length == 0)
                {
                    SendJson(response, 400, "{\"ok\":false,\"error\":" + Json.Stringify(err) + "}");
                    return;
                }
                SendJson(response, 200, "{\"ok\":true,\"summary\":" + Json.Stringify(summary) + "}");
                return;
            }
            if (path == "/api/memory/review")
            {
                Dictionary<string, object> rb = ReadJson(request);
                int id = Json.Int(rb, "id", -1);
                bool accept = Json.Bool(rb, "accept", true);
                string err;
                if (!_app.ReviewMemoryCandidate(id, accept, out err))
                {
                    SendJson(response, 400, "{\"ok\":false,\"error\":" + Json.Stringify(err) + "}");
                    return;
                }
                SendJson(response, 200, "{\"ok\":true}");
                return;
            }
            if (path == "/api/providers")
            {
                List<object> list = new List<object>();
                List<ProviderInfo> catalog = Providers.Catalog();
                for (int i = 0; i < catalog.Count; i++)
                {
                    ProviderInfo p = catalog[i];
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    item["id"] = p.Id;
                    item["name"] = p.Name;
                    item["kind"] = p.Kind;
                    item["endpoint"] = p.Endpoint;
                    item["models"] = p.Models;
                    item["help"] = p.Help;
                    item["free"] = p.Free;
                    item["quota"] = p.Quota;
                    item["keyUrl"] = p.KeyUrl;
                    list.Add(item);
                }
                SendJson(response, 200, "{\"providers\":" + Json.Stringify(list) + "}");
                return;
            }
            if (path == "/api/open-url")
            {
                // 只允许 http/https，且只在系统浏览器打开：界面里的「申请 Key」按钮走这里，
                // 不在应用内嵌页面里加载第三方站点（避免登录态/钓鱼风险混进控制台）。
                Dictionary<string, object> body = ReadJson(request);
                string url = body == null ? "" : Json.Str(body, "url", "");
                string scheme = "";
                try { scheme = new Uri(url).Scheme.ToLowerInvariant(); } catch { scheme = ""; }
                if (url.Length == 0 || (scheme != "http" && scheme != "https"))
                {
                    SendJson(response, 400, "{\"ok\":false,\"error\":\"只允许 http/https 链接\"}");
                    return;
                }
                bool inApp = body != null && Json.Bool(body, "inApp", false);
                if (inApp)
                {
                    // 允许渲染：在绿虎浏览器（WebView2 真渲染）里打开，便于直接看页面/登录
                    bool opened = _app.OpenGreenTigerBrowser(url);
                    SendJson(response, opened ? 200 : 500, opened
                        ? "{\"ok\":true,\"mode\":\"inApp\"}"
                        : "{\"ok\":false,\"error\":\"绿虎浏览器不可用（缺 WebView2 运行时？）\",\"mode\":\"inApp\"}");
                    return;
                }
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    SendJson(response, 200, "{\"ok\":true,\"mode\":\"system\"}");
                }
                catch (Exception ex)
                {
                    SendJson(response, 500, "{\"ok\":false,\"error\":" + Json.Stringify(ex.Message) + "}");
                }
                return;
            }
            if (path == "/api/providers/test")
            {
                Dictionary<string, object> body = ReadJson(request);
                string id = Json.Str(body, "id");
                string key = Json.Str(body, "key");
                string endpoint = Json.Str(body, "endpoint");
                ProviderInfo p = Providers.Find(id);
                if (p == null)
                {
                    // 自定义 / 自建网关：没有目录条目，用请求里的端点临时建一个来探
                    p = new ProviderInfo();
                    p.Id = "custom";
                    p.Name = "自定义端点";
                    p.Kind = "openai-compatible";
                }
                if (!string.IsNullOrEmpty(endpoint)) p.Endpoint = endpoint.Trim();
                string reason;
                List<string> models = Providers.TestConnection(p, key, out reason);
                Dictionary<string, object> result = new Dictionary<string, object>();
                result["ok"] = models != null && models.Count > 0;
                result["models"] = models != null ? models : new List<string>();
                if (models == null) result["error"] = reason;
                SendJson(response, 200, Json.Stringify(result));
                return;
            }
            if (path == "/api/workspaces")
            {
                Dictionary<string, object> body = ReadJson(request);
                string action = Json.Str(body, "action", "list");
                try
                {
                    string msg = _app.ManageWorkspaces(action, body);
                    SendJson(response, 200, "{\"ok\":true,\"message\":" + Json.Stringify(msg) + "}");
                }
                catch (InvalidOperationException ex)
                {
                    SendJson(response, 400, "{\"ok\":false,\"error\":" + Json.Stringify(ex.Message) + "}");
                }
                return;
            }
            if (path == "/api/tasks")
            {
                Dictionary<string, object> body = ReadJson(request);
                string action = Json.Str(body, "action", "list");
                try
                {
                    string msg = _app.ManageScheduledTasks(action, body);
                    SendJson(response, 200, "{\"ok\":true,\"message\":" + Json.Stringify(msg) + "}");
                }
                catch (InvalidOperationException ex)
                {
                    SendJson(response, 400, "{\"ok\":false,\"error\":" + Json.Stringify(ex.Message) + "}");
                }
                return;
            }
            if (path == "/api/session/new")
            {
                _app.NewSession();
                SendJson(response, 200, _app.StateJson());
                return;
            }
            if (path == "/api/session/select")
            {
                Dictionary<string, object> body = ReadJson(request);
                _app.SelectSession(Json.Str(body, "id"));
                SendJson(response, 200, _app.StateJson());
                return;
            }
            if (path == "/api/session/rename")
            {
                Dictionary<string, object> body = ReadJson(request);
                _app.RenameSession(Json.Str(body, "id"), Json.Str(body, "title"));
                SendJson(response, 200, _app.StateJson());
                return;
            }
            if (path == "/api/session/delete")
            {
                Dictionary<string, object> body = ReadJson(request);
                _app.DeleteSession(Json.Str(body, "id"));
                SendJson(response, 200, _app.StateJson());
                return;
            }
            if (path == "/api/session/fork")
            {
                Dictionary<string, object> body = ReadJson(request);
                _app.ForkSession(Json.Str(body, "id"));
                SendJson(response, 200, _app.StateJson());
                return;
            }
            if (path == "/api/sessions/bulk")
            {
                Dictionary<string, object> body = ReadJson(request);
                string action = Json.Str(body, "action", "");
                List<string> ids = new List<string>();
                List<object> rawIds = Json.Array(body, "ids");
                if (rawIds != null)
                {
                    for (int i = 0; i < rawIds.Count; i++)
                    {
                        string one = rawIds[i] as string;
                        if (!string.IsNullOrEmpty(one)) ids.Add(one);
                    }
                }
                if (action == "delete") _app.DeleteSessions(ids);
                else if (action == "archive") _app.ArchiveSessions(ids, true);
                else if (action == "unarchive") _app.ArchiveSessions(ids, false);
                else
                {
                    SendJson(response, 400, "{\"ok\":false,\"error\":\"未知操作\"}");
                    return;
                }
                SendJson(response, 200, _app.StateJson());
                return;
            }
            if (path == "/api/session/pin" || path == "/api/session/archive")
            {
                Dictionary<string, object> body = ReadJson(request);
                object flag;
                bool value = body.TryGetValue("value", out flag) && flag is bool && (bool)flag;
                if (path == "/api/session/pin") _app.PinSession(Json.Str(body, "id"), value);
                else _app.ArchiveSession(Json.Str(body, "id"), value);
                SendJson(response, 200, _app.StateJson());
                return;
            }
            if (path == "/api/pick-folder")
            {
                string folder = _app.PickFolder();
                SendJson(response, 200, "{\"ok\":true,\"path\":" + Json.Stringify(folder) + "}");
                return;
            }
            if (path == "/api/pick-files")
            {
                string[] files = _app.PickFiles();
                SendJson(response, 200, "{\"ok\":true,\"files\":" + Json.Stringify(files) + "}");
                return;
            }
            if (path == "/api/upload")
            {
                string name = QueryValue(request, "name");
                byte[] bytes = ReadBytes(request, 100 * 1024 * 1024);
                string saved = _app.SaveUpload(name, bytes);
                string display = Fs.SafeName(string.IsNullOrEmpty(name) ? "upload.bin" : name);
                SendJson(response, 200, "{\"ok\":true,\"path\":" + Json.Stringify(saved) + ",\"name\":" + Json.Stringify(display) + "}");
                return;
            }
            if (path == "/api/browser3/open")
            {
                // 绿虎浏览器：原生 WebView2 多标签窗口（真渲染）。
                Dictionary<string, object> body = ReadJson(request);
                bool ok = _app.OpenGreenTigerBrowser(Json.Str(body, "url"));
                SendJson(response, 200, ok ? "{\"ok\":true}" : "{\"ok\":false,\"error\":\"绿虎浏览器不可用，请检查 WebView2 运行时\"}");
                return;
            }
            if (path == "/api/new-chat")
            {
                _app.NewSession();
                SendJson(response, 200, "{\"ok\":true}");
                return;
            }
            if (path == "/api/restart-browser")
            {
                _app.RestartBrowser();
                SendJson(response, 200, "{\"ok\":true}");
                return;
            }
            if (path == "/api/shutdown")
            {
                SendJson(response, 200, "{\"ok\":true}");
                _app.Shutdown();
                return;
            }
            if (path == "/api/check-update")
            {
                // 未配置更新源时只回报当前版本，由前端提示"已是最新版本"。
                SendJson(response, 200, "{\"ok\":true,\"version\":\"" + AppInfo.Version + "\",\"updateUrl\":\"\"}");
                return;
            }
            /* ---------- 内置文件浏览器（写操作） ---------- */
            /* ---------- 内置浏览器 ---------- */
            SendJson(response, 404, "{\"ok\":false,\"error\":\"not found\"}");
        }

        /// <summary>SSE 长连接：先推一次全量状态，再等客户端断开。鉴权与 Origin 校验在 Handle 里已完成。</summary>
        private void ServeEvents(HttpListenerContext context)
        {
            HttpListenerResponse response = context.Response;
            response.StatusCode = 200;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers["Cache-Control"] = "no-cache, no-transform";
            response.KeepAlive = true;
            response.Headers["X-Accel-Buffering"] = "no";
            response.SendChunked = true;
            SseClient client = new SseClient();
            client.Response = response;
            client.Stream = response.OutputStream;
            lock (_clientsGate) { _clients.Add(client); }
            try
            {
                WriteSse(client, "retry: 3000\n\n");
                if (!WriteSseWithTimeout(client, "data: {\"type\":\"state\",\"state\":" + _app.StateJson() + "}\n\n", 5000))
                {
                    DropClient(client);
                    return;
                }
                client.Done.WaitOne();
            }
            catch (Exception ex)
            {
                // SSE 断连/推送失败以前是静默吞掉，界面「已连接」状态可能与实际不符。
                Log.Warn("SSE 会话异常断开: " + ex.Message);
            }
            finally
            {
                lock (_clientsGate) { _clients.Remove(client); }
                try { response.Close(); } catch { }
                client.Done.Set();
            }
        }

        private void WriteSse(SseClient client, string text)
        {
            lock (client.Gate)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                client.Stream.Write(bytes, 0, bytes.Length);
                client.Stream.Flush();
            }
        }

        /// <summary>
        /// 带超时的 SSE 写入：单个 TCP 缓冲打满的僵尸客户端曾经会把同步广播链卡住几十秒，
        /// 连带 Agent 的状态推送 / 会话保存全部阻塞。超时返回 false，调用方应剔除该客户端。
        /// </summary>
        private bool WriteSseWithTimeout(SseClient client, string text, int timeoutMs)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            try
            {
                Task write = Task.Run(delegate
                {
                    lock (client.Gate)
                    {
                        client.Stream.Write(bytes, 0, bytes.Length);
                        client.Stream.Flush();
                    }
                });
                return write.Wait(timeoutMs);
            }
            catch { return false; }
        }

        private void DropClient(SseClient client)
        {
            lock (_clientsGate) { _clients.Remove(client); }
            try { client.Done.Set(); } catch { }
            try { client.Response.Close(); } catch { }
        }

        public void Broadcast(string json)
        {
            BroadcastRaw("data: " + json + "\n\n");
        }

        public void BroadcastRaw(string text)
        {
            List<SseClient> clients;
            lock (_clientsGate)
            {
                clients = new List<SseClient>(_clients);
            }
            for (int i = 0; i < clients.Count; i++)
            {
                SseClient client = clients[i];
                // 逐客户端限时写入：超时/异常都直接剔除，不让慢客户端拖住其它订阅者。
                if (!WriteSseWithTimeout(client, text, 2000))
                {
                    DropClient(client);
                }
            }
        }

        private static string QueryValue(HttpListenerRequest request, string key)
        {
            string query = request.Url == null ? "" : request.Url.Query;
            if (query.StartsWith("?", StringComparison.Ordinal)) query = query.Substring(1);
            string[] parts = query.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                string rawKey = Uri.UnescapeDataString(parts[i].Substring(0, eq));
                if (rawKey != key) continue;
                string raw = parts[i].Substring(eq + 1).Replace("+", " ");
                try { return Uri.UnescapeDataString(raw); }
                catch { return raw; }
            }
            return "";
        }

        private static Dictionary<string, object> ReadJson(HttpListenerRequest request)
        {
            string body = ReadText(request, 20 * 1024 * 1024);
            Dictionary<string, object> d = Json.ParseObject(body);
            return d == null ? new Dictionary<string, object>() : d;
        }

        private static string ReadText(HttpListenerRequest request, int maxBytes)
        {
            byte[] bytes = ReadBytes(request, maxBytes);
            return Encoding.UTF8.GetString(bytes);
        }

        private static byte[] ReadBytes(HttpListenerRequest request, int maxBytes)
        {
            if (request.ContentLength64 > maxBytes) throw new InvalidOperationException("请求体过大");
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = request.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    if (ms.Length > maxBytes) throw new InvalidOperationException("请求体过大");
                }
                return ms.ToArray();
            }
        }

        private static void SendJson(HttpListenerResponse response, int status, string json)
        {
            ResponseText(response, status, json, "application/json; charset=utf-8");
        }

        private static void ResponseText(HttpListenerResponse response, int status, string text, string contentType)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            response.StatusCode = status;
            response.ContentType = contentType;
            response.Headers["Cache-Control"] = "no-store";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }

        private static void ResponseText(HttpListenerResponse response, int status, string text)
        {
            ResponseText(response, status, text, "text/plain; charset=utf-8");
        }
    }
}
