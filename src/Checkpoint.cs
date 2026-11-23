using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GreenTiger
{
    /// <summary>
    /// 全量 checkpoint：未沙箱 shell 命令执行前，把工作目录增量留档到 backups/&lt;taskId&gt;/shellck/，
    /// 让 /undo 能回滚「shell 改掉的文件」——此前只有 edit/write_file 覆盖前有单文件快照，
    /// shell 里一条 rm / git reset / 脚本重写不留任何痕迹（外部研究报告确认的最高价值缺口）。
    ///
    /// 增量设计：每任务一个版本店 store/&lt;seq&gt;/&lt;相对路径&gt;，每次 checkpoint 只拷贝相对上一个
    /// checkpoint 有变化（size/mtime 不同）的文件；每份 manifest 记录当时全量文件清单（tree）。
    /// 恢复到 seq N：每个文件取 ≤N 的最新版本回拷并复原 mtime；N 时不存在而当前有的文件删除
    /// （以 ≤N 最近一次同根 manifest 的 tree 为存在基线，没有基线就不删——宁可漏删不误删）。
    /// 上限（文件数/总字节/单文件字节）超了整段跳过——checkpoint 是尽力而为的安全网，审批才是闸门。
    /// </summary>
    public static class Checkpoint
    {
        private static readonly object Gate = new object();

        /// <summary>扫描与拷贝的上限；恢复扫描用 ForRestore（只枚举不拷贝，上限放宽）。</summary>
        public sealed class Caps
        {
            public int MaxFiles;
            public long MaxBytes;
            public long MaxFileBytes;
            /// <summary>目录名跳过清单（任一层级同名即跳过）；null = 用默认清单。</summary>
            public List<string> SkipDirNames;

            public static Caps Default()
            {
                Caps c = new Caps();
                c.MaxFiles = 3000;
                c.MaxBytes = 128L * 1024 * 1024;
                c.MaxFileBytes = 16L * 1024 * 1024;
                return c;
            }

            public static Caps ForRestore()
            {
                // 恢复扫描只为枚举现状（不做拷贝决策），文件数上限放宽；
                // 跳过清单必须与留档一致——否则恢复会动到 .git / node_modules 这些明确不管的目录。
                Caps c = new Caps();
                c.MaxFiles = 12000;
                c.MaxBytes = long.MaxValue;
                c.MaxFileBytes = long.MaxValue;
                return c;
            }

            public List<string> SkipList()
            {
                if (SkipDirNames != null) return SkipDirNames;
                return DefaultSkipDirs();
            }

            private static List<string> DefaultSkipDirs()
            {
                List<string> skip = new List<string>();
                skip.Add(".git");
                skip.Add(".svn");
                skip.Add(".hg");
                skip.Add("node_modules");
                skip.Add("__pycache__");
                skip.Add(".venv");
                skip.Add("venv");
                skip.Add(".greentiger");      // 绿虎自己的项目记忆目录
                skip.Add("greentiger-data");  // 便携数据目录（backups 就在里面，绝不能套娃）
                return skip;
            }
        }

        private sealed class Rec
        {
            public string Full;
            public string Rel;
            public long Size;
            public long Mtime;
        }

        private sealed class Ver
        {
            public long Size;
            public long Mtime;
            public int Seq;
            public string RelPath;
        }

        // ---------- 留档 ----------

        /// <summary>
        /// shell 命令执行前调用：增量留档工作目录，返回 manifest 绝对路径（backups/&lt;task&gt;/shellck/manifest-N.json）。
        /// 超上限 / 目录不存在等情况跳过并返回 ""（error 说明原因）——checkpoint 失败绝不阻断命令执行。
        /// </summary>
        public static string Create(string root, string taskId, Caps caps, out string error)
        {
            error = "";
            try
            {
                if (caps == null) caps = Caps.Default();
                if (string.IsNullOrEmpty(root)) { error = "工作目录为空"; return ""; }
                root = Path.GetFullPath(root);
                if (!Directory.Exists(root)) { error = "工作目录不存在：" + root; return ""; }
                if (UnderProtected(root)) { error = "工作目录在备份/沙箱目录内，不留 checkpoint"; return ""; }
                lock (Gate)
                {
                    string ck = CkDir(taskId);
                    string store = Path.Combine(ck, "store");
                    AppPaths.EnsureDirectory(ck);
                    int seq = NextSeq(ck);
                    List<Rec> files;
                    List<string> oversize;
                    int dirsSkipped;
                    if (!Scan(root, caps, out files, out oversize, out dirsSkipped, out error)) return "";
                    Dictionary<string, Ver> lastSeen = LoadState(ck, seq - 1, root);
                    List<Dictionary<string, object>> copied = new List<Dictionary<string, object>>();
                    List<object> tree = new List<object>();
                    foreach (Rec f in files)
                    {
                        tree.Add(f.Rel);
                        Ver prev;
                        if (lastSeen.TryGetValue(f.Rel, out prev) && prev.Size == f.Size && prev.Mtime == f.Mtime) continue;
                        string dest = Path.Combine(store, seq.ToString(CultureInfo.InvariantCulture), f.Rel);
                        string destDir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                        File.Copy(f.Full, dest, true);
                        Dictionary<string, object> item = new Dictionary<string, object>();
                        item["rel"] = f.Rel;
                        item["size"] = f.Size;
                        item["mtime"] = f.Mtime;
                        copied.Add(item);
                    }
                    Dictionary<string, object> man = new Dictionary<string, object>();
                    man["v"] = 1;
                    man["root"] = root;
                    man["seq"] = seq;
                    man["task"] = Fs.SafeName(string.IsNullOrEmpty(taskId) ? "manual" : taskId);
                    man["time"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    man["copied"] = copied;
                    man["oversize"] = oversize;
                    man["tree"] = tree;
                    man["scanFiles"] = files.Count;
                    man["dirsSkipped"] = dirsSkipped;
                    string manPath = Path.Combine(ck, "manifest-" + seq.ToString(CultureInfo.InvariantCulture) + ".json");
                    File.WriteAllText(manPath, Json.Stringify(man), new UTF8Encoding(false));
                    Log.Info("checkpoint 留档 seq=" + seq.ToString(CultureInfo.InvariantCulture) +
                             " 拷贝=" + copied.Count.ToString(CultureInfo.InvariantCulture) +
                             " 扫描=" + files.Count.ToString(CultureInfo.InvariantCulture) +
                             " 超大=" + oversize.Count.ToString(CultureInfo.InvariantCulture) + " 根=" + root);
                    return manPath;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return "";
            }
        }

        // ---------- 恢复 ----------

        /// <summary>
        /// 把工作目录恢复到 manifest 指向的状态：每个文件取 ≤N 的最新版本回拷并复原 mtime；
        /// N 之后新建的文件删除（以 ≤N 最近同根 manifest 的 tree 为存在基线；没有基线不删）；
        /// 超大未入档文件与跳过目录一律不动；N 之后被删掉的文件按 ≤N 版本找回来。
        /// 恢复前先把当前状态另存一份新档（回滚本身也要能再回滚）。返回人读摘要；失败返回 ""。
        /// </summary>
        public static string Restore(string manifestPath, string workDir, out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrEmpty(manifestPath)) { error = "缺少 checkpoint 清单"; return ""; }
                string man = Path.GetFullPath(manifestPath);
                if (!Fs.IsInside(AppPaths.BackupsDir, man)) { error = "checkpoint 清单不在备份目录内，已拒绝"; return ""; }
                if (!File.Exists(man)) { error = "checkpoint 清单不存在"; return ""; }
                Dictionary<string, object> m = Json.ParseObject(File.ReadAllText(man));
                if (m == null) { error = "checkpoint 清单解析失败"; return ""; }
                string root = Json.Str(m, "root");
                int seq = Json.Int(m, "seq", 0);
                string task = Json.Str(m, "task");
                if (string.IsNullOrEmpty(root) || seq < 1) { error = "checkpoint 清单字段不完整"; return ""; }
                root = Path.GetFullPath(root);
                string rootKey = NormalizeDir(root);
                if (!string.IsNullOrEmpty(workDir) && !Fs.IsInside(workDir, root)) { error = "checkpoint 根目录不在当前工作目录内，已拒绝"; return ""; }
                if (UnderProtected(root)) { error = "checkpoint 根目录在备份/沙箱目录内，已拒绝"; return ""; }
                if (!Directory.Exists(root)) { error = "checkpoint 根目录已不存在：" + root; return ""; }
                string ck = Path.GetDirectoryName(man);
                string store = Path.Combine(ck, "store");
                lock (Gate)
                {
                    // 存在基线：≤N 最近一次同根 manifest 的全量 tree（留档扫描要么全量成功、要么超限整段跳过不落清单）
                    List<string> existsAtN = null;
                    HashSet<string> oversizeAtN = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int s = seq; s >= 1; s--)
                    {
                        Dictionary<string, object> old = ReadManifest(ck, s);
                        if (old == null) continue;
                        string mroot = Json.Str(old, "root");
                        if (string.IsNullOrEmpty(mroot) || !NormalizeDir(mroot).Equals(rootKey, StringComparison.OrdinalIgnoreCase)) continue;
                        existsAtN = new List<string>();
                        List<object> tree = Json.Array(old, "tree");
                        foreach (object o in tree)
                        {
                            string rel = o as string;
                            if (!string.IsNullOrEmpty(rel)) existsAtN.Add(rel);
                        }
                        List<object> oversize = Json.Array(old, "oversize");
                        foreach (object o in oversize)
                        {
                            string rel = o as string;
                            if (!string.IsNullOrEmpty(rel)) oversizeAtN.Add(rel);
                        }
                        break;
                    }
                    bool anchor = existsAtN != null;
                    HashSet<string> existsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (anchor)
                    {
                        foreach (string rel in existsAtN) existsSet.Add(rel);
                    }
                    Dictionary<string, Ver> coverage = LoadState(ck, seq, root);
                    // 回滚前把当前状态另存一份新档；失败不阻断恢复
                    try
                    {
                        string preErr;
                        Create(root, task, Caps.Default(), out preErr);
                    }
                    catch { }
                    Caps rcaps = Caps.ForRestore();
                    List<Rec> current;
                    List<string> curOversize;
                    int dirsSkipped;
                    if (!Scan(root, rcaps, out current, out curOversize, out dirsSkipped, out error)) return "";
                    int restored = 0, deleted = 0, kept = 0;
                    HashSet<string> currentRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Rec f in current)
                    {
                        currentRels.Add(f.Rel);
                        Ver v;
                        if (coverage.TryGetValue(f.Rel, out v))
                        {
                            if (CopyVersion(store, v, root, f.Full)) restored++; else kept++;
                            continue;
                        }
                        if (existsSet.Contains(f.Rel) || oversizeAtN.Contains(f.Rel)) { kept++; continue; }   // N 时就存在但从未入档（超大）：不动
                        if (!anchor) { kept++; continue; }   // 没有存在基线：宁可漏删不误删
                        try
                        {
                            if (Directory.Exists(f.Full)) Directory.Delete(f.Full, true);
                            else File.Delete(f.Full);
                            deleted++;
                        }
                        catch { }
                    }
                    if (anchor)
                    {
                        // N 之后被删掉的文件：按 ≤N 版本找回来
                        foreach (string rel in existsAtN)
                        {
                            if (currentRels.Contains(rel)) continue;
                            Ver v;
                            if (!coverage.TryGetValue(rel, out v)) continue;   // 超大或无版本：无可恢复内容
                            string full = Path.Combine(root, rel);
                            if (!Fs.IsInside(root, full)) continue;            // 防 manifest 被改出越界路径
                            if (CopyVersion(store, v, root, full)) restored++;
                        }
                        // 清理 N 之后新建的空目录（自底向上）
                        HashSet<string> expected = ExpectedDirs(root, existsAtN);
                        List<string> currentDirs = new List<string>();
                        CollectDirs(root, rcaps, currentDirs);
                        for (int i = currentDirs.Count - 1; i >= 0; i--)
                        {
                            string d = currentDirs[i];
                            if (expected.Contains(NormalizeDir(d))) continue;
                            try
                            {
                                if (Directory.GetFiles(d).Length == 0 && Directory.GetDirectories(d).Length == 0)
                                {
                                    Directory.Delete(d);
                                    deleted++;
                                }
                            }
                            catch { }
                        }
                    }
                    string summary = "恢复 " + restored.ToString(CultureInfo.InvariantCulture) +
                                     " 个文件、删除 " + deleted.ToString(CultureInfo.InvariantCulture) +
                                     " 个新建文件/目录、保留 " + kept.ToString(CultureInfo.InvariantCulture) + " 个未入档文件";
                    if (!anchor) summary += "（无存在基线，未删除新增文件）";
                    Log.Info("checkpoint 回滚 seq=" + seq.ToString(CultureInfo.InvariantCulture) + "：" + summary);
                    return summary;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return "";
            }
        }

        // ---------- 内部 ----------

        /// <summary>把某个版本回拷到目标并复原 mtime；目标位置被 shell 换成了目录时先清掉。</summary>
        private static bool CopyVersion(string store, Ver v, string root, string target)
        {
            try
            {
                string src = Path.Combine(store, v.Seq.ToString(CultureInfo.InvariantCulture), v.RelPath);
                if (!File.Exists(src)) return false;   // 版本店被手工清理：退化为不动
                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                File.Copy(src, target, true);
                try { File.SetLastWriteTimeUtc(target, new DateTime(v.Mtime, DateTimeKind.Utc)); } catch { }
                return true;
            }
            catch { return false; }
        }

        private static string CkDir(string taskId)
        {
            string safe = Fs.SafeName(string.IsNullOrEmpty(taskId) ? "manual" : taskId);
            return Path.Combine(AppPaths.BackupsDir, safe, "shellck");
        }

        private static int NextSeq(string ck)
        {
            int max = 0;
            try
            {
                foreach (string f in Directory.GetFiles(ck, "manifest-*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (!name.StartsWith("manifest-", StringComparison.Ordinal)) continue;
                    int n;
                    if (int.TryParse(name.Substring(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > max) max = n;
                }
            }
            catch { }
            return max + 1;
        }

        /// <summary>把 ≤uptoSeq 的同根 manifest 折叠成「相对路径 → 最新版本」。</summary>
        private static Dictionary<string, Ver> LoadState(string ck, int uptoSeq, string root)
        {
            Dictionary<string, Ver> state = new Dictionary<string, Ver>(StringComparer.OrdinalIgnoreCase);
            if (uptoSeq < 1) return state;
            string rootKey = NormalizeDir(root);
            for (int s = 1; s <= uptoSeq; s++)
            {
                Dictionary<string, object> man = ReadManifest(ck, s);
                if (man == null) continue;
                string mroot = Json.Str(man, "root");
                if (string.IsNullOrEmpty(mroot) || !NormalizeDir(mroot).Equals(rootKey, StringComparison.OrdinalIgnoreCase)) continue;
                List<object> copied = Json.Array(man, "copied");
                foreach (object itemObj in copied)
                {
                    Dictionary<string, object> item = itemObj as Dictionary<string, object>;
                    if (item == null) continue;
                    string rel = Json.Str(item, "rel");
                    if (string.IsNullOrEmpty(rel)) continue;
                    Ver v = new Ver();
                    v.Size = Json.Long(item, "size", 0);
                    v.Mtime = Json.Long(item, "mtime", 0);
                    v.Seq = s;
                    v.RelPath = rel;
                    state[rel] = v;   // 后面的 seq 覆盖前面 = 每个文件取最新版本
                }
            }
            return state;
        }

        private static Dictionary<string, object> ReadManifest(string ck, int seq)
        {
            try
            {
                string path = Path.Combine(ck, "manifest-" + seq.ToString(CultureInfo.InvariantCulture) + ".json");
                if (!File.Exists(path)) return null;
                return Json.ParseObject(File.ReadAllText(path));
            }
            catch { return null; }
        }

        private static bool Scan(string root, Caps caps, out List<Rec> files, out List<string> oversize, out int dirsSkipped, out string error)
        {
            files = new List<Rec>();
            oversize = new List<string>();
            dirsSkipped = 0;
            error = "";
            string rootNorm = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            long totalBytes = 0;
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                string[] subDirs;
                string[] dirFiles;
                try
                {
                    subDirs = Directory.GetDirectories(dir);
                    dirFiles = Directory.GetFiles(dir);
                }
                catch { dirsSkipped++; continue; }   // 无权限/驱动器抖动：能扫多少算多少
                foreach (string sub in subDirs)
                {
                    if (SkipDir(Path.GetFileName(sub), caps)) continue;
                    if (UnderProtected(sub)) continue;
                    pending.Push(sub);
                }
                foreach (string f in dirFiles)
                {
                    if (files.Count >= caps.MaxFiles)
                    {
                        error = "文件数超过上限（" + caps.MaxFiles.ToString(CultureInfo.InvariantCulture) + "）";
                        return false;
                    }
                    Rec r = new Rec();
                    r.Full = f;
                    r.Rel = f.Substring(rootNorm.Length).Replace('/', '\\');
                    FileInfo fi = new FileInfo(f);
                    try { r.Size = fi.Length; r.Mtime = fi.LastWriteTimeUtc.Ticks; }
                    catch { continue; }
                    if (r.Size > caps.MaxFileBytes)
                    {
                        oversize.Add(r.Rel);
                    }
                    else
                    {
                        totalBytes += r.Size;
                        if (totalBytes > caps.MaxBytes)
                        {
                            error = "总大小超过上限（" + Fs.HumanSize(caps.MaxBytes) + "）";
                            return false;
                        }
                    }
                    files.Add(r);
                }
            }
            return true;
        }

        private static bool SkipDir(string name, Caps caps)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            foreach (string s in caps.SkipList())
            {
                if (s == lower) return true;
            }
            return false;
        }

        /// <summary>备份目录 / 沙箱目录本身（及内部）绝不留档也绝不恢复——backups 里有 checkpoint 自己，套娃会无限膨胀。</summary>
        private static bool UnderProtected(string path)
        {
            try
            {
                string p = Path.GetFullPath(path);
                if (Fs.IsInside(AppPaths.BackupsDir, p)) return true;
                string sandbox = Sandbox.SandboxDir();
                if (!string.IsNullOrEmpty(sandbox) && Fs.IsInside(sandbox, p)) return true;
                return false;
            }
            catch { return false; }
        }

        /// <summary>N 时已存在的相对路径 → 绝对路径集合（判断哪些目录不是新目录）。</summary>
        private static HashSet<string> ExpectedDirs(string root, List<string> rels)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rel in rels)
            {
                string dirRel = Path.GetDirectoryName(rel);
                while (!string.IsNullOrEmpty(dirRel))
                {
                    set.Add(NormalizeDir(Path.Combine(root, dirRel)));
                    string parent = Path.GetDirectoryName(dirRel);
                    if (parent == dirRel) break;
                    dirRel = parent;
                }
            }
            return set;
        }

        private static void CollectDirs(string root, Caps caps, List<string> into)
        {
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (string sub in subDirs)
                {
                    if (SkipDir(Path.GetFileName(sub), caps)) continue;
                    if (UnderProtected(sub)) continue;
                    into.Add(sub);
                    pending.Push(sub);
                }
            }
        }

        private static string NormalizeDir(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return path == null ? "" : path; }
        }
    }
}
