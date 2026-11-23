using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GreenTiger
{
    /// <summary>
    /// 工作目录的内置文件浏览器后端。
    /// 所有路径都相对工作目录解析，越界一律拒绝；删除走回收站，不做永久删除。
    /// </summary>
    public class FileBrowser
    {
        private readonly Config _config;

        public FileBrowser(Config config)
        {
            _config = config;
        }

        private string Root
        {
            get
            {
                string root = _config.WorkDir;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    throw new InvalidOperationException("还没有设置工作目录");
                return Path.GetFullPath(root);
            }
        }

        /// <summary>把相对路径解析成绝对路径，并确认仍在工作目录内。</summary>
        public string ResolveSafe(string relative)
        {
            string root = Root;
            string full = Fs.Resolve(root, relative);
            if (!Fs.IsInside(root, full)) throw new InvalidOperationException("路径超出工作目录");
            return full;
        }

        /// <summary>转成相对工作目录的路径，用 / 分隔，便于界面拼面包屑。</summary>
        public string ToRelative(string full)
        {
            string root = Root;
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return "";
            string r = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string rel = full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full.Substring(r.Length) : full;
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }

        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".json", ".xml", ".yml", ".yaml", ".ini", ".conf", ".cfg", ".log",
            ".cs", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".css", ".scss", ".less", ".html", ".htm",
            ".py", ".rb", ".go", ".rs", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".php", ".lua", ".sh",
            ".ps1", ".bat", ".cmd", ".sql", ".toml", ".csv", ".tsv", ".gitignore", ".env", ".editorconfig"
        };

        public static bool IsText(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return true;
            return TextExtensions.Contains(ext);
        }

        /// <summary>列目录。返回给界面的每一项都带相对路径。</summary>
        public string ListJson(string relative, bool showHidden)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            try
            {
                string dir = ResolveSafe(relative);
                if (!Directory.Exists(dir))
                {
                    d["ok"] = false;
                    d["error"] = "目录不存在";
                    return Json.Stringify(d);
                }
                List<object> items = new List<object>();
                DirectoryInfo info = new DirectoryInfo(dir);
                FileSystemInfo[] entries = info.GetFileSystemInfos();
                Array.Sort(entries, delegate(FileSystemInfo a, FileSystemInfo b)
                {
                    bool ad = a is DirectoryInfo;
                    bool bd = b is DirectoryInfo;
                    if (ad != bd) return ad ? -1 : 1;
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
                foreach (FileSystemInfo e in entries)
                {
                    bool hidden = (e.Attributes & FileAttributes.Hidden) != 0 || (e.Attributes & FileAttributes.System) != 0;
                    if (hidden && !showHidden) continue;
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    bool isDir = e is DirectoryInfo;
                    item["name"] = e.Name;
                    item["dir"] = isDir;
                    item["path"] = ToRelative(e.FullName);
                    item["hidden"] = hidden;
                    item["mtime"] = e.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond;
                    if (isDir)
                    {
                        item["size"] = 0;
                        item["ext"] = "";
                    }
                    else
                    {
                        FileInfo fi = (FileInfo)e;
                        item["size"] = fi.Length;
                        item["ext"] = Path.GetExtension(e.Name).TrimStart('.').ToLowerInvariant();
                        item["text"] = IsText(e.Name);
                    }
                    items.Add(item);
                }
                d["ok"] = true;
                d["path"] = ToRelative(dir);
                d["root"] = Root;
                d["parent"] = ToRelative(dir) == "" ? "" : ToRelative(Path.GetDirectoryName(dir));
                d["items"] = items;
            }
            catch (Exception ex)
            {
                d["ok"] = false;
                d["error"] = ex.Message;
            }
            return Json.Stringify(d);
        }

        /// <summary>
        /// 选择模式下列目录：path 为空列出本地驱动器；否则列出该绝对目录。
        /// 这是只读枚举，不限制在工作目录内——用于“选择工作目录”和“附加文件”时让用户浏览整个文件系统。
        /// </summary>
        public string ListPickerJson(string absolute, bool showHidden)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            try
            {
                if (string.IsNullOrEmpty(absolute))
                {
                    List<object> drives = new List<object>();
                    foreach (DriveInfo drive in DriveInfo.GetDrives())
                    {
                        DriveType t = drive.DriveType;
                        if (t != DriveType.Fixed && t != DriveType.Removable && t != DriveType.Network && t != DriveType.Ram) continue;
                        Dictionary<string, object> item = new Dictionary<string, object>();
                        string name = drive.Name.TrimEnd('\\');
                        item["name"] = name;
                        item["dir"] = true;
                        item["path"] = name + "\\";
                        item["size"] = 0;
                        item["ext"] = "";
                        drives.Add(item);
                    }
                    d["ok"] = true;
                    d["path"] = "";
                    d["parent"] = "";
                    d["root"] = "";
                    d["drives"] = true;
                    d["items"] = drives;
                    return Json.Stringify(d);
                }
                string full = Path.GetFullPath(absolute);
                if (!Directory.Exists(full))
                {
                    d["ok"] = false;
                    d["error"] = "目录不存在";
                    return Json.Stringify(d);
                }
                List<object> items = new List<object>();
                DirectoryInfo info = new DirectoryInfo(full);
                FileSystemInfo[] entries = info.GetFileSystemInfos();
                Array.Sort(entries, delegate(FileSystemInfo a, FileSystemInfo b)
                {
                    bool ad = a is DirectoryInfo;
                    bool bd = b is DirectoryInfo;
                    if (ad != bd) return ad ? -1 : 1;
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
                foreach (FileSystemInfo e in entries)
                {
                    bool hidden = (e.Attributes & FileAttributes.Hidden) != 0 || (e.Attributes & FileAttributes.System) != 0;
                    if (hidden && !showHidden) continue;
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    bool isDir = e is DirectoryInfo;
                    item["name"] = e.Name;
                    item["dir"] = isDir;
                    item["path"] = e.FullName;
                    item["hidden"] = hidden;
                    item["mtime"] = e.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond;
                    if (isDir)
                    {
                        item["size"] = 0;
                        item["ext"] = "";
                    }
                    else
                    {
                        FileInfo fi = (FileInfo)e;
                        item["size"] = fi.Length;
                        item["ext"] = Path.GetExtension(e.Name).TrimStart('.').ToLowerInvariant();
                        item["text"] = IsText(e.Name);
                    }
                    items.Add(item);
                }
                d["ok"] = true;
                d["path"] = full.Replace('\\', '/');
                d["root"] = full.Replace('\\', '/');
                d["parent"] = Directory.GetParent(full) == null ? "" : Directory.GetParent(full).FullName.Replace('\\', '/');
                d["items"] = items;
            }
            catch (Exception ex)
            {
                d["ok"] = false;
                d["error"] = ex.Message;
            }
            return Json.Stringify(d);
        }

        /// <summary>读文本文件预览。超过 maxBytes 只返回前一段。</summary>
        /* ---------- 全盘浏览模式 ----------
         * 列目录复用 ListPickerJson（任意绝对路径 / 驱动器列表，只读）。
         * 写操作（新建/重命名/删除/外部打开）走下面这些 absolute 版本：
         * 不限制在工作目录内，但系统盘根、Windows、Program Files 等关键位置禁止写。
         */

        /// <summary>系统级保护：全盘模式下不允许对这些位置做任何写操作。</summary>
        private static void AssertSafeWrite(string absolute)
        {
            string full = Path.GetFullPath(absolute);
            string root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("路径非法");

            string trimmed = full.TrimEnd('\\', '/');
            if (string.Equals(trimmed, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能修改盘符根目录");

            string winRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\');
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string lower = full.ToLowerInvariant();
            string[] blocked = new string[]
            {
                winRoot + "\\windows",
                pf.TrimEnd('\\'),
                pf86.TrimEnd('\\')
            };
            for (int i = 0; i < blocked.Length; i++)
            {
                if (string.IsNullOrEmpty(blocked[i])) continue;
                if (lower.StartsWith(blocked[i].ToLowerInvariant(), StringComparison.Ordinal))
                    throw new InvalidOperationException("系统关键目录受保护，不能修改");
            }
        }

    }
}
