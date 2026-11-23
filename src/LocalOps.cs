using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace GreenTiger
{
    /// <summary>
    /// 本机运维工具集：Everything 全盘搜索 / 已装程序清单 / 本地风险扫描 / 缓存清理 / 文件粉碎 / 三软件直达。
    /// 能力边界来自对三个软件的二进制逆向（结论见 docs/）：Everything 的官方调用面是 es.exe（IPC 存在但 es 即官方封装）；
    /// HiBit Uninstaller 为 UPX 加壳、无稳定 CLI → 只走标准卸载注册表 + GUI 直达；
    /// LightC 为 Go 单体内嵌 Web UI、无 CLI → 只做直达，AI 缓存规则按其字符串证据重建。
    /// 双消费面：Agent 工具（Tools.cs，文本输出）与工具箱 HTTP API（WebServer /api/toolbox/*，结构化 JSON）。
    /// 破坏性操作（粉碎 / 缓存清理 / 启动卸载器）一律 danger：必须经用户确认，代码内再保留硬护栏。
    /// </summary>
    public static class LocalOps
    {
        public const string EverythingPath = @"D:\Everything\Everything.exe";
        public const string HiBitPath = @"E:\卸载\卸载\HiBitUninstaller_3.0.25\HiBitUninstaller_3.0.25.exe";
        public const string LightCPath = @"D:\LightC\LightC.exe";

        private static readonly object Gate = new object();

        /// <summary>数据目录；应用未初始化时回退到 exe 目录（自检/裸库调用场景）。</summary>
        private static string DataRoot()
        {
            try
            {
                string root = AppPaths.Root;
                if (!string.IsNullOrEmpty(root)) return root;
            }
            catch { }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>es.exe 探测顺序：数据目录 bin → exe 旁 bin → exe 旁 → PATH。</summary>
        public static string EsExe()
        {
            string[] cands = new string[]
            {
                Path.Combine(DataRoot(), "bin", "es.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "es.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "es.exe")
            };
            for (int i = 0; i < cands.Length; i++)
            {
                if (File.Exists(cands[i])) return cands[i];
            }
            return null;
        }

        public static string AppPath(string app)
        {
            string key = (app == null ? "" : app.Trim().ToLowerInvariant());
            if (key == "everything") return EverythingPath;
            if (key == "hibit" || key == "hibit_uninstaller") return HiBitPath;
            if (key == "lightc") return LightCPath;
            return null;
        }

        public static string SummarizePaths(Dictionary<string, object> args)
        {
            List<string> paths = PathList(args, "paths");
            if (paths.Count == 0) return "";
            if (paths.Count == 1) return paths[0];
            return paths.Count + " 个文件：" + paths[0] + " 等";
        }

        // ================================================================ Everything 搜索
        /// <summary>结构化搜索行（工具箱 JSON 用）。ok=false 时 error 给出原因。</summary>
        public static List<Dictionary<string, object>> SearchRows(string query, int limit, string sort, out bool ok, out string error)
        {
            ok = false;
            error = "";
            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
            query = query == null ? "" : query.Trim();
            if (query.Length == 0) { error = "请输入搜索词"; return items; }
            string es = EsExe();
            if (es == null)
            {
                error = "找不到 es.exe（Everything 官方命令行）。请把它放到 " + Path.Combine(DataRoot(), "bin", "es.exe");
                return items;
            }
            if (limit < 1) limit = 1;
            if (limit > 300) limit = 300;
            // 查询词消毒：es 对带引号的整段按短语匹配（实测零结果且不报错）；
            // 且以 - 开头的词会被 es 当选项解析（注入面），Everything 的排除语义是 !，统一改写。
            query = query.Replace("\"", "");
            string[] words = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder queryBuf = new StringBuilder();
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                if (w.Length > 1 && w[0] == '-') w = "!" + w.Substring(1);
                if (queryBuf.Length > 0) queryBuf.Append(' ');
                queryBuf.Append(w);
            }
            query = queryBuf.ToString();
            if (query.Length == 0) { error = "搜索词消毒后为空（不要只用引号/选项符号）"; return items; }
            if (query.Length > 300) query = query.Substring(0, 300);
            if (!string.IsNullOrEmpty(sort))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(sort, "^[a-z][a-z-]{0,40}$"))
                {
                    error = "排序参数非法（只允许字母与连字符，如 size-descending）";
                    return items;
                }
            }
            string arguments = "-json -size -dm -n " + limit.ToString();
            if (!string.IsNullOrEmpty(sort)) arguments += " -sort " + sort;
            arguments += " " + query;
            int exitCode;
            string stderrText;
            string stdout = RunCapture(es, arguments, 15000, out stderrText, out exitCode);
            if (stdout == null) { error = "Everything 查询超时（确认 Everything 正在运行，可在工具箱「极速搜索」点「启动 Everything 并重试」）"; return items; }
            string trimmed = stdout.Trim();
            if (trimmed.Length == 0)
            {
                error = "Everything 返回空（exit=" + exitCode + "，索引可能仍在加载，请稍等几秒重试）";
                return items;
            }
            if (trimmed == "[]") { ok = true; return items; }
            object parsed = Json.Parse(trimmed);
            List<object> rows = parsed as List<object>;
            if (rows == null)
            {
                object[] arr = parsed as object[];
                if (arr != null) rows = new List<object>(arr);
            }
            if (rows == null)
            {
                error = "Everything 输出无法解析（确认 Everything 正在运行）；原始输出头：" + Head(trimmed, 120);
                return items;
            }
            for (int i = 0; i < rows.Count && items.Count < limit; i++)
            {
                Dictionary<string, object> row = rows[i] as Dictionary<string, object>;
                if (row == null) continue;
                string full = Json.Str(row, "filename", "");
                if (full.EndsWith("\\")) full = full.TrimEnd('\\');
                if (full.Length == 0) continue;
                string name = Path.GetFileName(full);
                long size = 0;
                try { size = Convert.ToInt64(row.ContainsKey("size") && row["size"] != null ? row["size"] : 0); }
                catch { size = 0; }
                string mtime = "";
                try
                {
                    double ft = Convert.ToDouble(row.ContainsKey("date_modified") && row["date_modified"] != null ? row["date_modified"] : 0);
                    if (ft > 0) mtime = DateTime.FromFileTime((long)ft).ToString("yyyy-MM-dd HH:mm:ss");
                }
                catch { }
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["name"] = name;
                item["path"] = full;
                item["size"] = size;
                item["mtime"] = mtime;
                items.Add(item);
            }
            ok = true;
            return items;
        }

        public static ToolResult Search(Dictionary<string, object> args)
        {
            ToolResult r = new ToolResult();
            string query = Json.Str(args, "query", "");
            int limit = Json.Int(args, "limit", 100);
            string sort = Json.Str(args, "sort", "");
            bool ok; string error;
            List<Dictionary<string, object>> items = SearchRows(query, limit, sort, out ok, out error);
            if (!ok)
            {
                r.Ok = false;
                r.ErrorCode = query.Trim().Length == 0 ? ToolErrorCode.BadArgs : ToolErrorCode.Unavailable;
                r.Error = error;
                return r;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("Everything 搜索结果（").Append(items.Count).Append(" 条，索引实时）：\n");
            for (int i = 0; i < items.Count; i++)
            {
                sb.Append(i + 1).Append(". ").Append(items[i]["name"])
                  .Append(" [").Append(HumanBytes((long)items[i]["size"])).Append("]")
                  .Append(!string.IsNullOrEmpty((string)items[i]["mtime"]) ? " " + (string)items[i]["mtime"] : "")
                  .Append("\n   ").Append(items[i]["path"]).Append("\n");
            }
            if (items.Count == 0) sb.Append("（无结果）\n");
            if (sb.Length > 14000) sb.Length = 14000;
            r.Output = sb.ToString();
            return r;
        }

        // ================================================================ 风险扫描（结构化核心）
        public class ScanFinding
        {
            public string Id;
            public string Title;
            public string Category;
            public long Bytes;
            public int Files;
            public int Risk;
            public string Explain;
            public List<string> Paths = new List<string>();
            public bool CanClean;
        }

        private static readonly string[][] CacheTargets = new string[][]
        {
            new string[] { "用户临时目录", "%LOCALAPPDATA%\\Temp", "0.30", "临时文件堆积拖慢磁盘与杀软检查，可安全清理（占用中的文件自动跳过）。" },
            new string[] { "Windows 临时目录", "C:\\Windows\\Temp", "0.30", "系统级临时文件与安装残留。" },
            new string[] { "Windows 更新缓存", "C:\\Windows\\SoftwareDistribution\\Download", "0.25", "已装更新的安装包缓存，需要时系统会重新下载。" },
            new string[] { "Chrome 缓存", "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\Default\\Cache", "0.20", "浏览器图片/JS 缓存，清理后首次加载略慢。" },
            new string[] { "Chrome Code Cache", "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\Default\\Code Cache", "0.20", "浏览器编译缓存。" },
            new string[] { "Edge 缓存", "%LOCALAPPDATA%\\Microsoft\\Edge\\User Data\\Default\\Cache", "0.20", "浏览器图片/JS 缓存。" },
            new string[] { "pip 缓存", "%LOCALAPPDATA%\\pip\\cache", "0.50", "Python 包下载缓存。" },
            new string[] { "npm 缓存", "%APPDATA%\\npm-cache", "0.50", "Node 包缓存。" },
            new string[] { "回收站（统计）", "C:\\$Recycle.Bin", "0.40", "回收站实际占用，建议定期清空。" },
            new string[] { "Ollama 模型", "%USERPROFILE%\\.ollama\\models", "0.90", "本地大模型动辄数 GB～数十 GB，不用的可删（ollama rm）。" },
            new string[] { "HuggingFace 缓存", "%USERPROFILE%\\.cache\\huggingface", "0.90", "HF 模型/数据集缓存。" },
            new string[] { "LM Studio 模型", "%USERPROFILE%\\.lmstudio", "0.90", "LM Studio 模型库。" },
            new string[] { "ComfyUI 模型", "%USERPROFILE%\\ComfyUI\\models", "0.70", "ComfyUI 模型目录（若装过）。" }
        };

        public static List<ScanFinding> ScanFindings(string scopes)
        {
            List<ScanFinding> findings = new List<ScanFinding>();
            DateTime start = DateTime.UtcNow;
            scopes = (scopes == null ? "caches,large,installer,startup" : scopes).ToLowerInvariant();
            bool doCaches = scopes.IndexOf("caches") >= 0 || scopes.Length == 0;
            bool doLarge = scopes.IndexOf("large") >= 0;
            bool doDup = scopes.IndexOf("dup") >= 0;
            bool doInstaller = scopes.IndexOf("installer") >= 0;
            bool doStartup = scopes.IndexOf("startup") >= 0;
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (doCaches)
            {
                for (int i = 0; i < CacheTargets.Length; i++)
                {
                    string dir = Expand(CacheTargets[i][1]);
                    if (!Directory.Exists(dir)) continue;
                    long bytes; int files; bool complete;
                    DirSize(dir, start.AddSeconds(20), out bytes, out files, out complete);
                    if (bytes <= 64L * 1024 * 1024) continue;
                    double weight = ParseD(CacheTargets[i][2]);
                    int baseRisk = bytes > 1073741824L ? 60 : bytes > 524288000L ? 45 : bytes > 104857600L ? 30 : 18;
                    ScanFinding f = new ScanFinding();
                    f.Id = "cache-" + FindingId(dir);
                    f.Title = CacheTargets[i][0];
                    f.Category = "缓存与模型";
                    f.Bytes = bytes;
                    f.Files = files;
                    f.Risk = Math.Min(80, (int)(baseRisk * (0.6 + 0.4 * weight)));
                    f.Explain = CacheTargets[i][3] + (complete ? "" : "（统计到预算中断，实际可能更大）");
                    f.Paths.Add(dir);
                    f.CanClean = true;
                    findings.Add(f);
                }
            }
            if (doInstaller)
            {
                string dl = Path.Combine(profile, "Downloads");
                if (Directory.Exists(dl))
                {
                    List<string> inst = new List<string>();
                    long bytes = 0;
                    try
                    {
                        FileInfo[] all = new DirectoryInfo(dl).GetFiles();
                        for (int i = 0; i < all.Length; i++)
                        {
                            string ext = all[i].Extension.ToLowerInvariant();
                            if ((ext == ".exe" || ext == ".msi" || ext == ".msix") && all[i].Length > 52428800L)
                            {
                                inst.Add(all[i].FullName); bytes += all[i].Length;
                            }
                        }
                    }
                    catch { }
                    if (inst.Count > 0)
                    {
                        ScanFinding f = new ScanFinding();
                        f.Id = "dl-installers";
                        f.Title = "下载目录大安装包 " + inst.Count + " 个";
                        f.Category = "安装残留";
                        f.Bytes = bytes;
                        f.Files = inst.Count;
                        f.Risk = 40;
                        f.Explain = "已安装过的软件安装包是纯冗余；确认对应软件已装好后可粉碎。";
                        f.Paths.AddRange(inst);
                        findings.Add(f);
                    }
                }
            }
            if (doLarge)
            {
                List<string[]> big = new List<string[]>();
                WalkLarge(profile, start.AddSeconds(30), big, 200L * 1024 * 1024, 40);
                if (big.Count > 0)
                {
                    long bytes = 0;
                    for (int i = 0; i < big.Count; i++) bytes += long.Parse(big[i][1]);
                    ScanFinding f = new ScanFinding();
                    f.Id = "large-top";
                    f.Title = "大文件 TOP " + big.Count + "（>200MB）";
                    f.Category = "磁盘空间";
                    f.Bytes = bytes;
                    f.Files = big.Count;
                    f.Risk = 45;
                    f.Explain = "视频/模型/镜像类文件，确认无用后可粉碎。";
                    for (int i = 0; i < big.Count; i++) f.Paths.Add(big[i][0]);
                    findings.Add(f);
                }
            }
            if (doDup)
            {
                long waste; List<string> dupPaths;
                DuplicateScan(start.AddSeconds(25), out waste, out dupPaths);
                if (dupPaths.Count > 0)
                {
                    ScanFinding f = new ScanFinding();
                    f.Id = "dup-files";
                    f.Title = "重复文件 " + (dupPaths.Count / 2) + " 组（内容完全相同）";
                    f.Category = "重复文件";
                    f.Bytes = waste;
                    f.Files = dupPaths.Count;
                    f.Risk = 55;
                    f.Explain = "MD5 一致的文件组，每组保留一份即可；粉碎前请自行确认保留哪一份。";
                    f.Paths.AddRange(dupPaths);
                    findings.Add(f);
                }
            }
            if (doStartup)
            {
                List<string> runs = StartupItems();
                ScanFinding f = new ScanFinding();
                f.Id = "startup";
                f.Title = "开机自启 " + runs.Count + " 条";
                f.Category = "启动风险";
                f.Bytes = 0;
                f.Files = runs.Count;
                f.Risk = runs.Count < 12 ? 30 : 60;
                f.Explain = "注册表 Run 键自启项，条目越多开机越慢；来源不明的建议用 HiBit「启动管理器」逐条禁用验证。";
                f.Paths.AddRange(runs);
                findings.Add(f);
            }
            findings.Sort(delegate(ScanFinding a, ScanFinding b) { return b.Risk.CompareTo(a.Risk); });
            return findings;
        }

        public static ToolResult Scan(Dictionary<string, object> args)
        {
            ToolResult r = new ToolResult();
            string scopes = Json.Str(args, "scopes", "caches,large,installer,startup");
            List<ScanFinding> findings = ScanFindings(scopes);
            long total = 0;
            foreach (ScanFinding f in findings) total += f.Bytes;
            StringBuilder sb = new StringBuilder();
            sb.Append("本机风险扫描结果（本地可解释引擎，启发式评分非杀毒结论）：\n");
            foreach (ScanFinding f in findings)
            {
                sb.Append("[风险 ").Append(f.Risk).Append("] ").Append(f.Category).Append(" | ").Append(f.Title)
                  .Append(" | ").Append(HumanBytes(f.Bytes));
                if (f.Files > 0) sb.Append(" | ").Append(f.Files).Append(" 个文件");
                sb.Append("\n");
                if (f.CanClean) sb.Append("   清理 id: ").Append(f.Id).Append("\n");
                if (f.Paths.Count > 0 && f.Category != "启动风险")
                {
                    sb.Append("   路径:\n");
                    for (int i = 0; i < f.Paths.Count && i < 30; i++) sb.Append("   - ").Append(f.Paths[i]).Append("\n");
                }
                else
                {
                    for (int i = 0; i < f.Paths.Count && i < 30; i++) sb.Append("   - ").Append(f.Paths[i]).Append("\n");
                }
                sb.Append("   说明: ").Append(f.Explain).Append("\n");
            }
            if (findings.Count == 0)
            {
                sb.Append("（本次范围没有值得处理的项目，很干净）\n");
            }
            else
            {
                sb.Append("\n合计：").Append(findings.Count).Append(" 项发现，涉及 ").Append(HumanBytes(total)).Append("。\n");
                sb.Append("粉碎用 pc_shred {\"paths\":[...]}（高危，逐次确认）；缓存清理用 pc_clean_cache {\"finding_id\":\"...\"}（仅限上面列出的白名单目录）。\n");
            }
            if (sb.Length > 14000) sb.Length = 14000;
            r.Output = sb.ToString();
            return r;
        }

        public static ToolResult Clean(Dictionary<string, object> args)
        {
            string id = Json.Str(args, "finding_id", "").Trim();
            long freed; int skipped; string target; bool ok; string error;
            return CleanById(id, out freed, out skipped, out target, out ok, out error);
        }

        public static ToolResult CleanById(string id, out long freed, out int skipped, out string target, out bool ok, out string error)
        {
            ToolResult r = new ToolResult();
            freed = 0; skipped = 0; target = null; ok = false; error = "";
            if (string.IsNullOrEmpty(id))
            {
                r.Ok = false; r.ErrorCode = ToolErrorCode.BadArgs; r.Error = "缺少 finding_id";
                error = r.Error;
                return r;
            }
            target = ResolveCleanTarget(id);
            if (target == null)
            {
                r.Ok = false; r.ErrorCode = ToolErrorCode.BadArgs;
                r.Error = "finding_id 不在白名单内：只允许清理扫描结果里列出的缓存目录 id（防止任意路径删除）";
                error = r.Error;
                return r;
            }
            CleanDir(target, ref freed, ref skipped);
            ok = true;
            r.Output = "已清理 " + target + "：释放 " + HumanBytes(freed) + "，跳过占用中文件 " + skipped + " 个。";
            return r;
        }

        /// <summary>
        /// 把清理 id 解析回白名单目录。扫描结果输出的是 cache-<hash>，旧版清理只认裸 <hash>，
        /// 两者必须同时接受；但仍然只允许 CacheTargets 里的目录，任意路径一律拒绝。
        /// </summary>
        internal static string ResolveCleanTarget(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            id = id.Trim();
            for (int i = 0; i < CacheTargets.Length; i++)
            {
                string dir = Expand(CacheTargets[i][1]);
                string hash = FindingId(dir);
                if (string.Equals(id, hash, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(id, "cache-" + hash, StringComparison.OrdinalIgnoreCase)) return dir;
            }
            return null;
        }

        /// <summary>自检用：返回白名单第 index 项的扫描 id，与 ScanFindings 输出保持同一格式。</summary>
        internal static string CacheCleanId(int index)
        {
            if (index < 0 || index >= CacheTargets.Length) return "";
            return "cache-" + FindingId(Expand(CacheTargets[index][1]));
        }

        internal static int CacheTargetCount { get { return CacheTargets.Length; } }

        internal static string CacheTargetPath(int index)
        {
            if (index < 0 || index >= CacheTargets.Length) return "";
            return Expand(CacheTargets[index][1]);
        }

        // ================================================================ 已装程序清单
        public static ToolResult UninstallList()
        {
            ToolResult r = new ToolResult();
            List<string[]> items = UninstallEntries();
            StringBuilder sb = new StringBuilder();
            sb.Append("已安装程序 ").Append(items.Count).Append(" 项（Windows 标准卸载注册表）。用 pc_uninstall_launch {\"name\":\"...\"} 启动标准卸载器：\n");
            int n = Math.Min(items.Count, 160);
            for (int i = 0; i < n; i++)
            {
                string[] it = items[i];
                sb.Append(i + 1).Append(". ").Append(it[0]);
                if (it[1].Length > 0) sb.Append("  v").Append(it[1]);
                if (it[2].Length > 0) sb.Append("  | ").Append(it[2]);
                if (it[3].Length > 0) sb.Append("  | ").Append(it[3]);
                sb.Append("\n");
            }
            if (items.Count > n) sb.Append("…（其余 ").Append(items.Count - n).Append(" 项省略）\n");
            r.Output = sb.ToString();
            return r;
        }

        public static ToolResult UninstallLaunch(Dictionary<string, object> args)
        {
            return UninstallLaunchByName(Json.Str(args, "name", ""));
        }

        public static ToolResult UninstallLaunchByName(string name)
        {
            ToolResult r = new ToolResult();
            name = (name == null ? "" : name.Trim());
            if (name.Length == 0)
            {
                r.Ok = false; r.ErrorCode = ToolErrorCode.BadArgs; r.Error = "name 不能为空（先查清单核对名称）";
                return r;
            }
            List<string[]> items = UninstallEntries();
            string[] hit = null;
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i][0], name, StringComparison.OrdinalIgnoreCase)) { hit = items[i]; break; }
            }
            if (hit == null)
            {
                List<string[]> partial = new List<string[]>();
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i][0].IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) partial.Add(items[i]);
                }
                if (partial.Count == 1) hit = partial[0];
                else if (partial.Count > 1)
                {
                    r.Ok = false; r.ErrorCode = ToolErrorCode.BadArgs;
                    r.Error = "命中多个程序，请用完整名称重试：" + string.Join(" / ", PartialNames(partial, 8));
                    return r;
                }
            }
            if (hit == null || hit[4].Length == 0)
            {
                r.Ok = false; r.ErrorCode = ToolErrorCode.NotFound;
                r.Error = "未找到「" + name + "」的标准卸载器（可先核对名称）";
                return r;
            }
            string exe, arguments;
            SplitCmd(hit[4], out exe, out arguments);
            if (!File.Exists(exe))
            {
                r.Ok = false; r.ErrorCode = ToolErrorCode.NotFound;
                r.Error = "卸载器文件不存在：" + exe;
                return r;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "\"" + exe + "\"";
                psi.Arguments = arguments;
                psi.UseShellExecute = true;
                Process.Start(psi);
                r.Output = "已启动标准卸载器：" + hit[0] + " → " + hit[4];
                return r;
            }
            catch (Exception ex)
            {
                r.Ok = false; r.ErrorCode = ToolErrorCode.ToolError; r.Error = "启动失败：" + ex.Message;
                return r;
            }
        }

        // ================================================================ 文件粉碎
        public static ToolResult Shred(Dictionary<string, object> args)
        {
            ToolResult r = new ToolResult();
            List<string> paths = PathList(args, "paths");
            if (paths.Count == 0)
            {
                r.Ok = false; r.ErrorCode = ToolErrorCode.BadArgs; r.Error = "paths 不能为空（绝对路径，每项一个文件）";
                return r;
            }
            int passes = Json.Int(args, "passes", 3);
            StringBuilder sb = new StringBuilder();
            int okCount = 0;
            long freed = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                string guard = ShredGuard(paths[i]);
                if (guard != null)
                {
                    sb.Append("✗ ").Append(paths[i]).Append(" — ").Append(guard).Append("\n");
                    continue;
                }
                string full = Path.GetFullPath(paths[i].Trim().Trim('"'));
                try
                {
                    long size = new FileInfo(full).Length;
                    ShredOne(full, passes);
                    okCount++;
                    freed += size;
                    sb.Append("✓ ").Append(full).Append(" — ").Append(HumanBytes(size)).Append("，").Append(passes).Append(" 趟覆写完成\n");
                }
                catch (Exception ex)
                {
                    sb.Append("✗ ").Append(full).Append(" — 失败：").Append(ex.Message).Append("\n");
                }
            }
            r.Ok = okCount > 0;
            if (!r.Ok) r.ErrorCode = ToolErrorCode.ToolError;
            r.Output = "粉碎结果：成功 " + okCount + "/" + paths.Count + "，释放 " + HumanBytes(freed) + "\n" + sb.ToString();
            if (r.Output.Length > 12000) r.Output = r.Output.Substring(0, 12000);
            return r;
        }

        /// <summary>粉碎预览（工具箱用）：逐路径给出可否粉碎、大小与拒绝原因，不做任何删除。</summary>
        public static List<Dictionary<string, object>> ShredPreviewList(List<string> paths)
        {
            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
            if (paths == null) return items;
            foreach (string raw in paths)
            {
                Dictionary<string, object> item = new Dictionary<string, object>();
                string p = raw == null ? "" : raw.Trim().Trim('"');
                item["path"] = p;
                string guard = ShredGuard(p);
                if (guard != null)
                {
                    item["ok"] = false;
                    item["reason"] = guard;
                    item["bytes"] = 0L;
                }
                else
                {
                    try
                    {
                        item["ok"] = true;
                        item["bytes"] = new FileInfo(Path.GetFullPath(p)).Length;
                        item["reason"] = "";
                    }
                    catch (Exception ex)
                    {
                        item["ok"] = false;
                        item["reason"] = ex.Message;
                        item["bytes"] = 0L;
                    }
                }
                items.Add(item);
            }
            return items;
        }

        private static string ShredGuard(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "空路径";
            string p = raw.Trim().Trim('"');
            if (!Path.IsPathRooted(p)) return "不是绝对路径";
            string full;
            try { full = Path.GetFullPath(p); }
            catch { return "路径非法"; }
            string low = full.ToLowerInvariant();
            if (low.Length <= 3 || low[1] != ':') return "拒绝粉碎盘根";
            string[] protectedPrefixes = new string[]
            {
                "c:\\windows", "d:\\windows", "e:\\windows", "f:\\windows",
                "c:\\program files", "d:\\program files", "e:\\program files", "f:\\program files",
                "c:\\program files (x86)", "d:\\program files (x86)", "e:\\program files (x86)", "f:\\program files (x86)",
                "c:\\programdata", "d:\\programdata", "e:\\programdata", "f:\\programdata",
                DataRoot().ToLowerInvariant()
            };
            for (int i = 0; i < protectedPrefixes.Length; i++)
            {
                if (low.StartsWith(protectedPrefixes[i])) return "受保护系统路径";
            }
            if (Directory.Exists(full)) return "是目录（粉碎只针对单个文件，请展开后逐文件提交）";
            if (!File.Exists(full)) return "文件不存在";
            return null;
        }

        private static void ShredOne(string full, int passes)
        {
            long size = new FileInfo(full).Length;
            byte[] randomBuf = new byte[4 * 1024 * 1024];
            using (FileStream fs = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                for (int pass = 0; pass < passes; pass++)
                {
                    fs.Seek(0, SeekOrigin.Begin);
                    long left = size;
                    while (left > 0)
                    {
                        int chunk = (int)Math.Min(4L * 1024 * 1024, left);
                        if (pass == 0) { byte[] zeros = new byte[chunk]; fs.Write(zeros, 0, chunk); }
                        else if (pass == 1) { byte[] ones = new byte[chunk]; Fill(ones, (byte)0xFF); fs.Write(ones, 0, chunk); }
                        else
                        {
                            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                            {
                                int fill = chunk;
                                while (fill > 0)
                                {
                                    int step = Math.Min(fill, randomBuf.Length);
                                    rng.GetBytes(randomBuf, 0, step);
                                    fs.Write(randomBuf, 0, step);
                                    fill -= step;
                                }
                            }
                        }
                        left -= chunk;
                    }
                    fs.Flush();
                }
                fs.SetLength(0);
                fs.Flush();
            }
            string renamed = Path.Combine(Path.GetDirectoryName(full), Guid.NewGuid().ToString("N").Substring(0, 16));
            File.Move(full, renamed);
            File.Delete(renamed);
        }

        private static void Fill(byte[] buf, byte value)
        {
            for (int i = 0; i < buf.Length; i++) buf[i] = value;
        }

        // ================================================================ 三软件直达
        public static ToolResult LaunchApp(Dictionary<string, object> args)
        {
            return LaunchAppByName(Json.Str(args, "app", ""));
        }

        public static ToolResult LaunchAppByName(string app)
        {
            ToolResult r = new ToolResult();
            app = (app == null ? "" : app.Trim());
            string path = AppPath(app);
            if (path == null)
            {
                r.Ok = false; r.ErrorCode = ToolErrorCode.BadArgs;
                r.Error = "app 只支持 everything / hibit / lightc";
                return r;
            }
            if (!File.Exists(path))
            {
                r.Ok = false; r.ErrorCode = ToolErrorCode.NotFound;
                r.Error = "程序不存在：" + path;
                return r;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = path;
                psi.WorkingDirectory = Path.GetDirectoryName(path);
                psi.UseShellExecute = true;
                Process.Start(psi);
                r.Output = "已启动：" + app + " → " + path;
                return r;
            }
            catch (Exception ex)
            {
                r.Ok = false; r.ErrorCode = ToolErrorCode.ToolError; r.Error = "启动失败：" + ex.Message;
                return r;
            }
        }

        // ================================================================ 内部实现
        private static string Head(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n);
        }

        private static double ParseD(string s)
        {
            double d;
            if (double.TryParse(s, out d)) return d;
            return 0.5;
        }

        public static string HumanBytes(long n)
        {
            double v = n;
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
            int u = 0;
            while (v >= 1024 && u < 4) { v /= 1024; u++; }
            if (u == 0) return ((long)v).ToString() + units[u];
            return v.ToString("F1") + units[u];
        }

        private static string Expand(string p)
        {
            string r = p.Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            r = r.Replace("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            r = r.Replace("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return Environment.ExpandEnvironmentVariables(r);
        }

        private static string FindingId(string path)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(path));
                StringBuilder sb = new StringBuilder(8);
                for (int i = 0; i < 4; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string RunCapture(string exe, string arguments, int timeoutMs, out string stderrText, out int exitCode)
        {
            stderrText = "";
            exitCode = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                using (Process proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string errout = proc.StandardError.ReadToEnd();
                    stderrText = errout == null ? "" : errout.Trim();
                    if (!proc.WaitForExit(timeoutMs)) { try { proc.Kill(); } catch { } return null; }
                    exitCode = proc.ExitCode;
                    return stdout;
                }
            }
            catch { return null; }
        }

        private static void DirSize(string root, DateTime deadline, out long bytes, out int files, out bool complete)
        {
            bytes = 0; files = 0; complete = true;
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                DirectoryInfo di;
                try { di = new DirectoryInfo(dir); } catch { continue; }
                FileInfo[] fs;
                try { fs = di.GetFiles(); } catch { fs = new FileInfo[0]; }
                for (int i = 0; i < fs.Length; i++)
                {
                    try { bytes += fs[i].Length; files++; } catch { }
                }
                DirectoryInfo[] ds;
                try { ds = di.GetDirectories(); } catch { ds = new DirectoryInfo[0]; }
                for (int i = 0; i < ds.Length; i++)
                {
                    if ((ds[i].Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    stack.Push(ds[i].FullName);
                }
                if (DateTime.UtcNow > deadline) { complete = false; return; }
            }
        }

        private static void CleanDir(string root, ref long freed, ref int skipped)
        {
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                FileInfo[] fs;
                try { fs = new DirectoryInfo(dir).GetFiles(); } catch { continue; }
                for (int i = 0; i < fs.Length; i++)
                {
                    try
                    {
                        long size = fs[i].Length;
                        fs[i].Delete();
                        freed += size;
                    }
                    catch { skipped++; }
                }
                DirectoryInfo[] ds;
                try { ds = new DirectoryInfo(dir).GetDirectories(); } catch { continue; }
                for (int i = 0; i < ds.Length; i++)
                {
                    if ((ds[i].Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    CleanDir(ds[i].FullName, ref freed, ref skipped);
                }
            }
        }

        private static void WalkLarge(string root, DateTime deadline, List<string[]> big, long threshold, int cap)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && big.Count < cap)
            {
                string dir = stack.Pop();
                FileInfo[] fs;
                try { fs = new DirectoryInfo(dir).GetFiles(); } catch { continue; }
                for (int i = 0; i < fs.Length && big.Count < cap; i++)
                {
                    try { if (fs[i].Length >= threshold) big.Add(new string[] { fs[i].FullName, fs[i].Length.ToString() }); }
                    catch { }
                }
                DirectoryInfo[] ds;
                try { ds = new DirectoryInfo(dir).GetDirectories(); } catch { continue; }
                for (int i = 0; i < ds.Length; i++)
                {
                    if ((ds[i].Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    string name = ds[i].Name.ToLowerInvariant();
                    if (name == "windows" || name == "$recycle.bin") continue;
                    stack.Push(ds[i].FullName);
                }
                if (DateTime.UtcNow > deadline) return;
            }
        }

        private static void DuplicateScan(DateTime deadline, out long waste, out List<string> flat)
        {
            waste = 0;
            flat = new List<string>();
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] bases = new string[]
            {
                Path.Combine(profile, "Downloads"),
                Path.Combine(profile, "Desktop"),
                Path.Combine(profile, "Documents")
            };
            List<string[]> pool = new List<string[]>();
            for (int b = 0; b < bases.Length; b++)
            {
                if (!Directory.Exists(bases[b])) continue;
                Stack<string> stack = new Stack<string>();
                stack.Push(bases[b]);
                while (stack.Count > 0 && pool.Count < 4000)
                {
                    string dir = stack.Pop();
                    FileInfo[] fs;
                    try { fs = new DirectoryInfo(dir).GetFiles(); } catch { continue; }
                    for (int i = 0; i < fs.Length && pool.Count < 4000; i++)
                    {
                        if (fs[i].Length >= 1048576L && fs[i].Length <= 314572800L) pool.Add(new string[] { fs[i].FullName, fs[i].Length.ToString() });
                    }
                    DirectoryInfo[] ds;
                    try { ds = new DirectoryInfo(dir).GetDirectories(); } catch { continue; }
                    for (int i = 0; i < ds.Length; i++)
                    {
                        if ((ds[i].Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        stack.Push(ds[i].FullName);
                    }
                    if (DateTime.UtcNow > deadline) return;
                }
            }
            Dictionary<string, List<string>> bySize = new Dictionary<string, List<string>>();
            for (int i = 0; i < pool.Count; i++)
            {
                if (!bySize.ContainsKey(pool[i][1])) bySize[pool[i][1]] = new List<string>();
                bySize[pool[i][1]].Add(pool[i][0]);
            }
            int hashed = 0;
            foreach (KeyValuePair<string, List<string>> kv in bySize)
            {
                if (kv.Value.Count < 2) continue;
                Dictionary<string, List<string>> byHash = new Dictionary<string, List<string>>();
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    if (hashed > 1200 || DateTime.UtcNow > deadline) break;
                    string hash = Md5File(kv.Value[i]);
                    hashed++;
                    if (hash == null) continue;
                    if (!byHash.ContainsKey(hash)) byHash[hash] = new List<string>();
                    byHash[hash].Add(kv.Value[i]);
                }
                foreach (KeyValuePair<string, List<string>> g in byHash)
                {
                    if (g.Value.Count >= 2)
                    {
                        long sz = long.Parse(kv.Key);
                        waste += sz * (g.Value.Count - 1);
                        for (int i = 0; i < g.Value.Count; i++) flat.Add(g.Value[i]);
                    }
                }
                if (DateTime.UtcNow > deadline) break;
            }
        }

        private static string Md5File(string path)
        {
            try
            {
                using (MD5 md5 = MD5.Create())
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] hash = md5.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder(32);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        private static List<string> StartupItems()
        {
            List<string> items = new List<string>();
            CollectRun(Registry.CurrentUser, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", "HKCU", items);
            CollectRun(Registry.LocalMachine, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", "HKLM", items);
            return items;
        }

        private static void CollectRun(RegistryKey hive, string sub, string label, List<string> items)
        {
            try
            {
                using (RegistryKey k = hive.OpenSubKey(sub))
                {
                    if (k == null) return;
                    string[] names = k.GetValueNames();
                    for (int i = 0; i < names.Length; i++) items.Add(label + "\\" + names[i] + " = " + Convert.ToString(k.GetValue(names[i])));
                }
            }
            catch { }
        }

        /// <summary>Agent 工具箱：磁盘空间概览（只读，DriveInfo 即时读取）。</summary>
        public static ToolResult DiskSummary()
        {
            ToolResult r = new ToolResult();
            List<Dictionary<string, object>> rows = DiskRows();
            StringBuilder sb = new StringBuilder();
            sb.Append("固定磁盘 ").Append(rows.Count).Append(" 块：\n");
            for (int i = 0; i < rows.Count; i++)
            {
                sb.Append(rows[i]["name"]).Append("  ").Append(rows[i]["label"])
                  .Append("  剩余 ").Append(rows[i]["free_human"])
                  .Append(" / 共 ").Append(rows[i]["total_human"]).Append("\n");
            }
            if (rows.Count == 0) sb.Append("（没有可读的固定磁盘）\n");
            r.Output = sb.ToString();
            return r;
        }

        /// <summary>Agent 工具箱：开机自启清单（只读 Run 键）。</summary>
        public static ToolResult StartupList()
        {
            ToolResult r = new ToolResult();
            List<string> items = StartupItems();
            StringBuilder sb = new StringBuilder();
            sb.Append("开机自启 ").Append(items.Count).Append(" 条（HKCU/HKLM Run）：\n");
            for (int i = 0; i < items.Count; i++) sb.Append(i + 1).Append(". ").Append(items[i]).Append("\n");
            if (items.Count == 0) sb.Append("（没有 Run 键自启项）\n");
            sb.Append("来源不明的条目建议先用 pc_launch_app 打开 HiBit 启动管理器逐条禁用验证。\n");
            r.Output = sb.ToString();
            return r;
        }

        /// <summary>Agent 工具箱：粉碎前预览（只读，不删除）。</summary>
        public static ToolResult ShredPreview(Dictionary<string, object> args)
        {
            ToolResult r = new ToolResult();
            List<string> paths = PathList(args, "paths");
            if (paths.Count == 0)
            {
                r.Ok = false; r.ErrorCode = ToolErrorCode.BadArgs; r.Error = "缺少 paths（要预览的绝对文件路径列表）";
                return r;
            }
            List<Dictionary<string, object>> items = ShredPreviewList(paths);
            StringBuilder sb = new StringBuilder();
            int okCount = 0;
            sb.Append("粉碎预览（未删除任何文件）：\n");
            for (int i = 0; i < items.Count; i++)
            {
                bool can = Convert.ToBoolean(items[i]["ok"]);
                if (can) okCount++;
                sb.Append(can ? "✓ " : "✗ ").Append(items[i]["path"]);
                if (can) sb.Append(" — ").Append(HumanBytes(Convert.ToInt64(items[i]["bytes"])));
                else sb.Append(" — ").Append(items[i]["reason"]);
                sb.Append("\n");
            }
            sb.Append("可粉碎 ").Append(okCount).Append("/").Append(items.Count)
              .Append("。确认后再用 pc_shred 执行（高危，会再次弹确认）。\n");
            r.Output = sb.ToString();
            return r;
        }

        /// <summary>固定磁盘空间概览（工具箱体检页顶部展示；DriveInfo 即时读取，无 IO 扫描成本）。</summary>
        public static List<Dictionary<string, object>> DiskRows()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            try
            {
                System.IO.DriveInfo[] drives = System.IO.DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    System.IO.DriveInfo d = drives[i];
                    try
                    {
                        if (!d.IsReady || d.DriveType != System.IO.DriveType.Fixed) continue;
                        Dictionary<string, object> row = new Dictionary<string, object>();
                        row["name"] = d.Name;
                        string label = d.VolumeLabel;
                        row["label"] = string.IsNullOrEmpty(label) ? "本地磁盘" : label;
                        row["total"] = d.TotalSize;
                        row["free"] = d.AvailableFreeSpace;
                        row["total_human"] = HumanBytes(d.TotalSize);
                        row["free_human"] = HumanBytes(d.AvailableFreeSpace);
                        rows.Add(row);
                    }
                    catch { }
                }
            }
            catch { }
            return rows;
        }

        /// <summary>在资源管理器中定位：文件=选中高亮（/select），目录=直接打开。只读性质，无破坏性。</summary>
        public static bool RevealPath(string raw, out string error)
        {
            error = "";
            raw = (raw == null ? "" : raw.Trim().Trim('"'));
            if (raw.Length == 0) { error = "路径为空"; return false; }
            if (!Path.IsPathRooted(raw)) { error = "不是绝对路径"; return false; }
            string full;
            try { full = Path.GetFullPath(raw); }
            catch { error = "路径非法"; return false; }
            try
            {
                if (File.Exists(full))
                {
                    Process.Start("explorer.exe", "/select,\"" + full + "\"");
                    return true;
                }
                if (Directory.Exists(full))
                {
                    Process.Start("explorer.exe", "\"" + full + "\"");
                    return true;
                }
                error = "路径不存在";
                return false;
            }
            catch (Exception ex)
            {
                error = "打开失败：" + ex.Message;
                return false;
            }
        }

        public static List<string[]> UninstallEntries()
        {
            List<string[]> items = new List<string[]>();
            string[][] roots = new string[][]
            {
                new string[] { "HKLM", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall" },
                new string[] { "HKLM", "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall" },
                new string[] { "HKCU", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall" }
            };
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int r = 0; r < roots.Length; r++)
            {
                RegistryKey hive = roots[r][0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                RegistryKey root;
                try { root = hive.OpenSubKey(roots[r][1]); } catch { root = null; }
                if (root == null) continue;
                using (root)
                {
                    string[] keys = root.GetSubKeyNames();
                    for (int i = 0; i < keys.Length; i++)
                    {
                        if (seen.ContainsKey(keys[i])) continue;
                        seen[keys[i]] = true;
                        RegistryKey k;
                        try { k = root.OpenSubKey(keys[i]); } catch { k = null; }
                        if (k == null) continue;
                        using (k)
                        {
                            string name = Convert.ToString(k.GetValue("DisplayName"));
                            if (string.IsNullOrEmpty(name)) continue;
                            string version = Str(k.GetValue("DisplayVersion"));
                            string publisher = Str(k.GetValue("Publisher"));
                            string uninstall = Str(k.GetValue("UninstallString"));
                            string size = "";
                            object est = k.GetValue("EstimatedSize");
                            if (est is int) size = HumanBytes(((int)est) * 1024L);
                            items.Add(new string[] { name, version, publisher, size, uninstall, est is int ? ((int)est).ToString() : "" });
                        }
                    }
                }
            }
            items.Sort(delegate(string[] a, string[] b) { return string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase); });
            return items;
        }

        private static string Str(object v)
        {
            return v == null ? "" : Convert.ToString(v);
        }

        private static void SplitCmd(string cmd, out string exe, out string arguments)
        {
            cmd = cmd.Trim();
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0) { exe = cmd.Substring(1, end - 1); arguments = cmd.Substring(end + 1).Trim(); return; }
            }
            int sp = cmd.IndexOf(' ');
            if (sp > 0) { exe = cmd.Substring(0, sp); arguments = cmd.Substring(sp + 1).Trim(); }
            else { exe = cmd; arguments = ""; }
        }

        private static string[] PartialNames(List<string[]> items, int cap)
        {
            string[] names = new string[Math.Min(items.Count, cap)];
            for (int i = 0; i < names.Length; i++) names[i] = items[i][0];
            return names;
        }

        public static List<string> PathList(Dictionary<string, object> args, string key)
        {
            List<string> result = new List<string>();
            if (args == null || !args.ContainsKey(key) || args[key] == null) return result;
            if (args[key] is string)
            {
                string one = (string)args[key];
                if (one.Trim().Length > 0) result.Add(one.Trim());
                return result;
            }
            System.Collections.IEnumerable seq = args[key] as System.Collections.IEnumerable;
            if (seq == null) return result;
            foreach (object item in seq)
            {
                if (item == null) continue;
                string s = item.ToString();
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim());
            }
            return result;
        }
    }
}
