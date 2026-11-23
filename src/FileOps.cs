using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GreenTiger
{
    /// <summary>
    /// 文件工具核心算法（Read / Edit / Glob / Grep）：
    /// 精确替换、按行切片、通配匹配、内容检索、统一 diff 预览。
    /// 全部做成纯函数，自检可以直接断言，不依赖 Agent 循环。
    /// </summary>
    public static class FileOps
    {
        /// <summary>遍历时跳过的目录：全是构建产物/依赖/元数据，搜它们只会拖慢且没有价值。</summary>
        private static readonly string[] SkipDirs = new string[]
        {
            ".git", ".svn", ".hg", "node_modules", "bower_components", "__pycache__",
            ".vs", ".idea", ".nuget", "packages", "bin", "obj", ".gradle", ".next", ".cache"
        };

        public class GlobHit
        {
            public string Path { get; set; }
            public long Size { get; set; }
            public long Modified { get; set; }
        }

        public class GrepHit
        {
            public string Path { get; set; }
            public int Line { get; set; }
            public string Text { get; set; }
        }

        public class EditResult
        {
            public bool Ok { get; set; }
            public string Text { get; set; }
            public int Count { get; set; }
            public string Mode { get; set; }
            public string Error { get; set; }
            public int Line { get; set; }

            public EditResult()
            {
                Text = "";
                Mode = "";
                Error = "";
            }
        }

        public static bool SkipDir(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            for (int i = 0; i < SkipDirs.Length; i++)
            {
                if (string.Equals(name, SkipDirs[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // 迁移到 Globbing.PathPatternToRegex（Util.cs）。保留这个转发是因为外部工具/插件脚本会用它。
        public static string GlobToRegex(string pattern)
        {
            return Globbing.PathPatternToRegex(pattern == null ? "" : pattern.Trim());
        }

        // 实现收在 Util.Globbing（文件名/许可两种通配共一处）。
        // 历史包袱：这里原本也叫 GlobMatch(pattern, relPath)，与 AgentRunner.GlobMatch(text, pattern)
        // 同名但参数顺序相反 —— 现已拆成 Globbing.Path / Globbing.Permit 两个不同的名字。
        public static bool GlobMatch(string pattern, string relPath)
        {
            return Globbing.Path(pattern, relPath);
        }

        /// <summary>
        /// 按通配列出文件（相对 root，正斜杠），按修改时间倒序——最近改过的通常最相关。
        /// maxEntries 只是遍历上限，防止在大仓库里跑到天荒地老。
        /// </summary>
        public static List<GlobHit> Glob(string root, string pattern, int limit, int maxEntries, out bool truncated)
        {
            List<GlobHit> hits = new List<GlobHit>();
            truncated = false;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return hits;
            if (limit <= 0) limit = 200;
            if (maxEntries <= 0) maxEntries = 20000;

            Regex re;
            try { re = new Regex(GlobToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch (ArgumentException) { return hits; }

            int visited = 0;
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(dir); }
                catch (Exception) { continue; }
                for (int i = 0; i < entries.Length; i++)
                {
                    string full = entries[i];
                    string name = Path.GetFileName(full);
                    bool isDir = false;
                    try { isDir = (File.GetAttributes(full) & FileAttributes.Directory) == FileAttributes.Directory; }
                    catch (Exception) { continue; }
                    if (isDir)
                    {
                        if (SkipDir(name)) continue;
                        stack.Push(full);
                        continue;
                    }
                    if (++visited > maxEntries) { truncated = true; break; }
                    string rel = Rel(root, full);
                    if (!re.IsMatch(rel)) continue;
                    GlobHit hit = new GlobHit();
                    hit.Path = rel;
                    try
                    {
                        FileInfo info = new FileInfo(full);
                        hit.Size = info.Length;
                        hit.Modified = TimeUtil.ToUnix(info.LastWriteTimeUtc.ToLocalTime());
                    }
                    catch (Exception) { }
                    hits.Add(hit);
                }
                if (truncated) break;
            }
            hits.Sort(delegate(GlobHit a, GlobHit b) { return b.Modified.CompareTo(a.Modified); });
            if (hits.Count > limit)
            {
                hits.RemoveRange(limit, hits.Count - limit);
                truncated = true;
            }
            return hits;
        }

        /// <summary>内容检索：逐行正则匹配，跳过二进制与超大文件。</summary>
        public static List<GrepHit> Grep(string root, string pattern, string include, bool ignoreCase,
            int maxHits, int maxFiles, out int filesScanned, out bool truncated)
        {
            List<GrepHit> hits = new List<GrepHit>();
            filesScanned = 0;
            truncated = false;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return hits;
            if (maxHits <= 0) maxHits = 100;
            if (maxFiles <= 0) maxFiles = 3000;

            Regex re;
            try
            {
                RegexOptions opt = RegexOptions.CultureInvariant;
                if (ignoreCase) opt |= RegexOptions.IgnoreCase;
                re = new Regex(pattern, opt);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException("正则无效：" + ex.Message);
            }

            Regex includeRe = null;
            if (!string.IsNullOrEmpty(include))
            {
                try { includeRe = new Regex(GlobToRegex(include), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
                catch (ArgumentException) { includeRe = null; }
            }

            List<string> files = new List<string>();
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            bool fileCap = false;
            while (stack.Count > 0 && !fileCap)
            {
                string dir = stack.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(dir); }
                catch (Exception) { continue; }
                for (int i = 0; i < entries.Length; i++)
                {
                    string full = entries[i];
                    string name = Path.GetFileName(full);
                    bool isDir = false;
                    try { isDir = (File.GetAttributes(full) & FileAttributes.Directory) == FileAttributes.Directory; }
                    catch (Exception) { continue; }
                    if (isDir)
                    {
                        if (!SkipDir(name)) stack.Push(full);
                        continue;
                    }
                    string rel = Rel(root, full);
                    if (includeRe != null && !includeRe.IsMatch(rel))
                    {
                        int slash = rel.LastIndexOf('/');
                        if (!includeRe.IsMatch(slash < 0 ? rel : rel.Substring(slash + 1))) continue;
                    }
                    files.Add(full);
                    if (files.Count >= maxFiles) { fileCap = true; truncated = true; break; }
                }
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);

            for (int f = 0; f < files.Count; f++)
            {
                string full = files[f];
                FileInfo info;
                try { info = new FileInfo(full); } catch (Exception) { continue; }
                if (info.Length > 2 * 1024 * 1024) continue;
                string text;
                try
                {
                    byte[] head = ReadHead(full, 8192);
                    // UTF-16 文本每第二个字节是 0x00，不能只凭 0 字节判二进制——先查 BOM
                    bool hasBom = head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF));
                    if (!hasBom && LooksBinary(head)) continue;
                    // 解码走 BOM → UTF-8 严格 → GBK 兜底链：GBK 文件按硬 UTF-8 解成替换符后，
                    // 中文 pattern 永远搜不到且无任何提示。
                    byte[] all = File.ReadAllBytes(full);
                    text = TextUtil.Decode(all, all.Length);
                }
                catch (Exception) { continue; }
                filesScanned++;
                string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                string rel = Rel(root, full);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length > 4000) line = TextUtil.SafeTruncate(line, 4000);
                    if (!re.IsMatch(line)) continue;
                    GrepHit hit = new GrepHit();
                    hit.Path = rel;
                    hit.Line = i + 1;
                    hit.Text = line.Length > 300 ? TextUtil.SafeTruncate(line, 300) + "…" : line;
                    hits.Add(hit);
                    if (hits.Count >= maxHits) { truncated = true; break; }
                }
                if (hits.Count >= maxHits) break;
            }
            return hits;
        }

        /// <summary>
        /// 精确替换（Edit）：
        /// 1) 先按原文精确匹配；2) 找不到再按「忽略行尾空白 + 统一换行」的行块匹配；
        /// 命中多处且未开 replace_all 时拒绝——宁可报错也不猜用户要改哪一处。
        /// </summary>
        public static EditResult ApplyEdit(string original, string oldText, string newText, bool replaceAll)
        {
            EditResult r = new EditResult();
            if (original == null) original = "";
            if (string.IsNullOrEmpty(oldText))
            {
                r.Error = "old_string 不能为空（要做整文件覆盖请用 write_file）";
                return r;
            }
            if (newText == null) newText = "";
            if (string.Equals(oldText, newText, StringComparison.Ordinal))
            {
                r.Error = "old_string 与 new_string 完全相同，这次编辑没有任何变化";
                return r;
            }

            List<int> indexes = FindAll(original, oldText);
            if (indexes.Count > 0)
            {
                if (indexes.Count > 1 && !replaceAll)
                {
                    r.Count = indexes.Count;
                    r.Line = LineOf(original, indexes[1]);
                    r.Error = "old_string 在文件里出现 " + indexes.Count + " 次（第 2 次在第 " + r.Line +
                              " 行），无法确定改哪一处。请补充更多上下文让它唯一，或用 replace_all=true。";
                    return r;
                }
                StringBuilder sb = new StringBuilder();
                int cursor = 0;
                for (int i = 0; i < indexes.Count; i++)
                {
                    sb.Append(original, cursor, indexes[i] - cursor);
                    sb.Append(newText);
                    cursor = indexes[i] + oldText.Length;
                }
                sb.Append(original, cursor, original.Length - cursor);
                r.Ok = true;
                r.Text = sb.ToString();
                r.Count = indexes.Count;
                r.Mode = "exact";
                r.Line = LineOf(original, indexes[0]);
                return r;
            }

            EditResult lines = ApplyEditByLines(original, oldText, newText, replaceAll);
            if (lines != null) return lines;

            r.Error = "在文件里找不到 old_string。请先用 read_file 读当前内容，按原文（含缩进）重试；" +
                      "如果只是想追加内容，用 write_file 的 append=true。";
            return r;
        }

        /// <summary>行块匹配：容忍行尾空白差异与 CRLF/LF 差异，输出保留原文件的换行风格。</summary>
        private static EditResult ApplyEditByLines(string original, string oldText, string newText, bool replaceAll)
        {
            string nl = original.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            string norm = original.Replace("\r\n", "\n").Replace("\r", "\n");
            bool endsWithNewline = norm.EndsWith("\n", StringComparison.Ordinal);
            string[] have = SplitKeep(norm);
            string[] want = SplitKeep(oldText.Replace("\r\n", "\n").Replace("\r", "\n"));
            if (want.Length == 0) return null;

            List<int> starts = new List<int>();
            for (int i = 0; i + want.Length <= have.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < want.Length; j++)
                {
                    if (!string.Equals(have[i + j].TrimEnd(), want[j].TrimEnd(), StringComparison.Ordinal))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) starts.Add(i);
            }
            if (starts.Count == 0) return null;

            EditResult r = new EditResult();
            r.Mode = "lines";
            r.Line = starts[0] + 1;
            if (starts.Count > 1 && !replaceAll)
            {
                r.Count = starts.Count;
                r.Line = starts[1] + 1;
                r.Error = "old_string 按行匹配到 " + starts.Count + " 处（第 2 处从第 " + r.Line +
                          " 行开始），无法确定改哪一处。请补充更多上下文，或用 replace_all=true。";
                return r;
            }
            if (replaceAll && starts.Count > 1)
            {
                // 区间重叠校验：old 自重叠（文件 a\na\na，old=a\na，starts=[0,1]）时
                // 逐段复制会跳过重叠行、静默吞掉原文。宁可报错让模型带更多上下文重试。
                for (int k = 1; k < starts.Count; k++)
                {
                    if (starts[k] < starts[k - 1] + want.Length)
                    {
                        r.Count = starts.Count;
                        r.Error = "old_string 与自身重叠（多处命中区间交叠，第 " + (starts[k] + 1) + " 行与第 " + (starts[k - 1] + 1) +
                                  " 行的匹配重叠），无法安全地 replace_all。请扩大 old_string 上下文或缩小范围。";
                        return r;
                    }
                }
            }

            string[] add = SplitKeep(newText.Replace("\r\n", "\n").Replace("\r", "\n"));
            List<string> output = new List<string>();
            int cursor = 0;
            for (int k = 0; k < starts.Count; k++)
            {
                int at = starts[k];
                for (int i = cursor; i < at; i++) output.Add(have[i]);
                for (int i = 0; i < add.Length; i++) output.Add(add[i]);
                cursor = at + want.Length;
            }
            for (int i = cursor; i < have.Length; i++) output.Add(have[i]);

            string joined = string.Join(nl, output.ToArray());
            if (endsWithNewline && !joined.EndsWith(nl, StringComparison.Ordinal)) joined += nl;
            r.Ok = true;
            r.Text = joined;
            r.Count = starts.Count;
            return r;
        }

        /// <summary>统一 diff（前缀/后缀裁剪 + 3 行上下文），用于审批弹窗里「改了什么」。</summary>
        public static string Diff(string before, string after, string label, int maxChangeLines)
        {
            if (before == null) before = "";
            if (after == null) after = "";
            if (maxChangeLines <= 0) maxChangeLines = 80;
            string[] a = SplitKeep(before.Replace("\r\n", "\n").Replace("\r", "\n"));
            string[] b = SplitKeep(after.Replace("\r\n", "\n").Replace("\r", "\n"));

            int pre = 0;
            while (pre < a.Length && pre < b.Length && string.Equals(a[pre], b[pre], StringComparison.Ordinal)) pre++;
            int suf = 0;
            while (suf < a.Length - pre && suf < b.Length - pre &&
                   string.Equals(a[a.Length - 1 - suf], b[b.Length - 1 - suf], StringComparison.Ordinal)) suf++;

            int aEnd = a.Length - suf;
            int bEnd = b.Length - suf;
            int removed = aEnd - pre;
            int added = bEnd - pre;
            if (removed == 0 && added == 0) return "(没有差异)";

            int ctx = 3;
            int from = Math.Max(0, pre - ctx);
            int aTo = Math.Min(a.Length, aEnd + ctx);
            int bTo = Math.Min(b.Length, bEnd + ctx);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("--- a/" + label);
            sb.AppendLine("+++ b/" + label);
            sb.AppendLine("@@ -" + (from + 1) + "," + Math.Max(0, aTo - from) +
                          " +" + (from + 1) + "," + Math.Max(0, bTo - from) + " @@");

            for (int i = from; i < pre; i++) sb.AppendLine(" " + a[i]);

            int shown = 0;
            for (int i = pre; i < aEnd; i++)
            {
                if (shown >= maxChangeLines) { sb.AppendLine("…（还有 " + (aEnd - i) + " 行删除未显示）"); break; }
                sb.AppendLine("-" + a[i]);
                shown++;
            }
            shown = 0;
            for (int i = pre; i < bEnd; i++)
            {
                if (shown >= maxChangeLines) { sb.AppendLine("…（还有 " + (bEnd - i) + " 行新增未显示）"); break; }
                sb.AppendLine("+" + b[i]);
                shown++;
            }
            for (int i = aEnd; i < aTo; i++) sb.AppendLine(" " + a[i]);
            sb.Append("（共 +" + added + " 行 / −" + removed + " 行）");
            return sb.ToString();
        }

        /// <summary>
        /// 读取文本并按原编码写回（GBK 文件不会被改成 UTF-8 乱码）。
        /// 识别失败时退回 UTF-8，与 write_file 的行为一致。
        /// </summary>
        private static readonly Regex RxMdHeading = new Regex(@"^\s{0,3}(#{1,6})[ \t]+(.+)$", RegexOptions.Compiled);
        private static readonly Regex RxCnChapter = new Regex(@"^\s*(第[零一二三四五六七八九十百千万两0-9]+[章节篇部回卷讲])(.*)$", RegexOptions.Compiled);
        private static readonly Regex RxCnSection = new Regex(@"^\s*([一二三四五六七八九十]+)、\s*(.+)$", RegexOptions.Compiled);
        private static readonly Regex RxNumHeading = new Regex(@"^\s*(\d{1,3}(?:\.\d{1,3}){0,3})[.、)）]\s+(.{2,})$", RegexOptions.Compiled);
        private static readonly Regex RxCodeSymbol = new Regex(
            @"^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|async|export|func|function|def|class|interface|enum|struct|namespace|type)\b.*)$",
            RegexOptions.Compiled);

        /// <summary>
        /// 3.30.0 长文档索引：只回标题/章节/代码符号与行号，不回正文。
        /// 这是 Claude「先给模型文档地图，再让它按需读原文」的落地——几百页文档先 outline，
        /// 模型再按 offset/limit 取相关段，而不是一次整读后被头尾截断。
        /// </summary>
        public static string OutlineText(string text, string path, int maxEntries)
        {
            if (maxEntries <= 0) maxEntries = 240;
            string norm = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = norm.Length == 0 ? new string[0] : norm.Split('\n');
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            bool markdown = ext == ".md" || ext == ".markdown" || ext == ".mdx";
            bool plain = ext == ".txt" || ext == ".log" || ext == ".csv";
            List<string> hits = new List<string>();
            int seen = 0;
            string last = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                string body = line.Trim();
                if (body.Length == 0 || body.Length > 200) continue;
                bool hit = false;
                if (markdown || plain)
                {
                    Match m = RxMdHeading.Match(line);
                    if (m.Success) hit = true;
                }
                if (!hit) { if (RxCnChapter.IsMatch(line)) hit = true; }
                if (!hit) { if (RxCnSection.IsMatch(line)) hit = true; }
                if (!hit && (markdown || plain)) { if (RxNumHeading.IsMatch(line)) hit = true; }
                if (!hit && !markdown && !plain && (ext == ".cs" || ext == ".js" || ext == ".ts" || ext == ".tsx"
                    || ext == ".jsx" || ext == ".py" || ext == ".java" || ext == ".go" || ext == ".rs" || ext == ".cpp"
                    || ext == ".c" || ext == ".h" || ext == ".rb" || ext == ".php" || ext == ".kt" || ext == ".swift"))
                {
                    if (RxCodeSymbol.IsMatch(line)) hit = true;
                }
                if (!hit) continue;
                if (body == last) continue;   // 连续重复标题只留一条
                last = body;
                seen++;
                if (hits.Count >= maxEntries) continue;
                string shown = body.Length > 120 ? body.Substring(0, 120) + "…" : body;
                hits.Add("L" + (i + 1) + "  " + shown);
            }
            if (hits.Count == 0)
                return "（未识别到章节标题或代码符号；用 offset/limit 分段读，或先 grep 关键字定位）";
            StringBuilder sb = new StringBuilder();
            sb.Append("【文档索引】识别 ").Append(seen).Append(" 处标题/符号，共 ").Append(lines.Length).Append(" 行");
            if (seen > hits.Count) sb.Append("（只列前 ").Append(hits.Count).Append(" 处，其余用 grep 按标题搜）");
            sb.AppendLine();
            for (int i = 0; i < hits.Count; i++) sb.AppendLine(hits[i]);
            return sb.ToString();
        }


        public static string ReadTextAuto(string full, out Encoding enc)
        {
            byte[] bytes = File.ReadAllBytes(full);
            // 编码嗅探只有一份实现（TextUtil.Decode），这里不再自己抄一遍。
            return TextUtil.Decode(bytes, out enc);
        }

        /// <summary>
        /// 改动前留一份原文快照（data/backups/&lt;taskId&gt;），回滚时直接拷回去即可。
        /// 快照失败不阻断编辑，错误由调用方写进输出，避免「因为备份失败所以什么都不改」。
        /// </summary>
        public static string Snapshot(string full, string taskId, out string error)
        {
            error = "";
            try
            {
                string safe = string.IsNullOrEmpty(taskId) ? "manual" : taskId;
                foreach (char bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
                string dir = Path.Combine(AppPaths.BackupsDir, safe);
                Directory.CreateDirectory(dir);
                string name = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "-" + Path.GetFileName(full);
                string dest = Path.Combine(dir, name);
                File.Copy(full, dest, true);
                string[] files = Directory.GetFiles(dir);
                if (files.Length > 100)
                {
                    Array.Sort(files, StringComparer.Ordinal);
                    for (int i = 0; i < files.Length - 100; i++)
                    {
                        try { File.Delete(files[i]); } catch (Exception) { }
                    }
                }
                return dest;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return "";
            }
        }

        /// <summary>把文本按行切开，末尾换行不产生多余空行（便于 diff 与按行编辑）。</summary>
        private static string[] SplitKeep(string text)
        {
            if (text == null || text.Length == 0) return new string[0];
            string t = text;
            if (t.EndsWith("\n", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);
            return t.Split('\n');
        }

        private static List<int> FindAll(string haystack, string needle)
        {
            List<int> list = new List<int>();
            if (string.IsNullOrEmpty(needle)) return list;
            int at = 0;
            while (true)
            {
                int found = haystack.IndexOf(needle, at, StringComparison.Ordinal);
                if (found < 0) break;
                list.Add(found);
                at = found + needle.Length;
                if (at >= haystack.Length) break;
            }
            return list;
        }

        private static int LineOf(string text, int index)
        {
            if (index <= 0) return 1;
            int line = 1;
            int max = Math.Min(index, text.Length);
            for (int i = 0; i < max; i++) if (text[i] == '\n') line++;
            return line;
        }

        private static byte[] ReadHead(string path, int max)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int len = (int)Math.Min(fs.Length, max);
                byte[] buf = new byte[len];
                int read = fs.Read(buf, 0, len);
                if (read < len) Array.Resize(ref buf, read);
                return buf;
            }
        }

        private static bool LooksBinary(byte[] data)
        {
            if (data == null) return false;
            int n = Math.Min(data.Length, 8192);
            for (int i = 0; i < n; i++) if (data[i] == 0) return true;
            return false;
        }

        private static string Rel(string root, string full)
        {
            string r = root.TrimEnd('\\', '/');
            string p = full;
            if (p.StartsWith(r, StringComparison.OrdinalIgnoreCase) && p.Length > r.Length) p = p.Substring(r.Length);
            p = p.TrimStart('\\', '/');
            return p.Replace('\\', '/');
        }
    }
}
