using System;
using System.IO;
using System.Text;

namespace GreenTiger
{
    /// <summary>
    /// App 的内嵌 UI 资产与版本戳部分（从 App.cs 拆出，保持 partial class 边界）。
    /// 负责：版本变更时清理可再生测试产物、按版本戳把内嵌 UI 覆盖到数据目录 ui\、
    /// 清理历史版本遗留文件。names[] 必须与 tools/project-manifest.js 的 UI_ASSETS 一致。
    /// </summary>
    public partial class App
    {
        // 把嵌入 UI 同步到数据目录 ui\：用版本戳避免每次启动都覆盖运行目录，
        // 应用版本变化时同步整份 UI 资源。
        /// <summary>应用版本戳文件名（数据目录根）：内容为上次运行的版本号。</summary>
        private const string AppStampName = ".app-version";

        /// <summary>
        /// 更新后清理旧缓存：数据目录根的 .app-version 已存在且与当前版本不一致（真正的升级）时，
        /// 清掉 %TEMP%\GreenTiger 下全部可再生测试产物（截图/e2e 会话/dump/评测临时目录）。
        /// 这些目录由测试与工具生成、随时可重建——旧版本遗留只占磁盘还可能干扰排障。
        /// 首次运行（没有版本戳，例如 smoke/e2e/shot 的全新临时数据目录）只写戳、不清理。
        /// 保留 artifacts / uploads（应用功能数据）与 e2e-deps（依赖缓存，重拉要联网）。
        /// 用户数据（sessions/config/logs）永远不动。
        /// </summary>
        private void CleanupStaleTempOnUpdate()
        {
            try
            {
                string stampFile = Path.Combine(AppPaths.Root, AppStampName);
                string last = "";
                try { if (File.Exists(stampFile)) last = File.ReadAllText(stampFile, Encoding.UTF8).Trim(); } catch { }
                if (last == AppInfo.Version) return;   // 同版本：什么都不动
                if (last.Length == 0)
                {
                    // 首次运行只记录版本戳：否则会把本次会话刚生成的截图、恢复夹具，
                    // 甚至并行测试实例正在使用的目录一并删掉。
                    try { File.WriteAllText(stampFile, AppInfo.Version, new UTF8Encoding(false)); }
                    catch (Exception ex) { Log.Warn("写入应用版本戳失败: " + ex.Message); }
                    return;
                }
                string tempBase = Path.Combine(Path.GetTempPath(), "GreenTiger");
                if (Directory.Exists(tempBase))
                {
                    // 按已知测试产物前缀删除（黑名单制，不是"全删留白名单"）：
                    // 应用自身的临时数据根（如恢复冒烟的 recovery-*）也在 TEMP 下，
                    // 全目录扫删会把正在运行的应用自己的数据根删掉——只删确定可再生的测试产物。
                    string[] stalePrefixes = { "shot-", "ui-e2e-", "dump-", "b2-", "b3-", "bd-", "c-17",
                        "el-", "drill-", "eval-2026", "recovery-17", "m-17", "p-17", "pv-", "vf-" };
                    string[] staleFiles = { "badge-btn.png", "clip-bottom.png", "clip-topright.png",
                        "sys.png", "selftest.log", "vf-home-dark.png", "vf-home-light.png", "vf-overlay.png" };
                    int removed = 0;
                    foreach (string entry in Directory.GetFileSystemEntries(tempBase))
                    {
                        string name = Path.GetFileName(entry) ?? "";
                        if (string.Equals(entry, AppPaths.Root, StringComparison.OrdinalIgnoreCase)) continue;   // 永不删自己
                        bool stale = false;
                        for (int i = 0; i < stalePrefixes.Length; i++) if (name.StartsWith(stalePrefixes[i], StringComparison.Ordinal)) stale = true;
                        for (int i = 0; i < staleFiles.Length; i++) if (string.Equals(name, staleFiles[i], StringComparison.OrdinalIgnoreCase)) stale = true;
                        if (!stale) continue;
                        try
                        {
                            if (Directory.Exists(entry)) Directory.Delete(entry, true);
                            else File.Delete(entry);
                            removed++;
                        }
                        catch { /* 单项被占用就跳过，不影响其它 */ }
                    }
                    if (removed > 0) Log.Info("版本更新（" + (last.Length == 0 ? "首次运行" : last) + " → " + AppInfo.Version + "）：已清理临时测试产物 " + removed + " 项。");
                }
                try { File.WriteAllText(stampFile, AppInfo.Version, new UTF8Encoding(false)); }
                catch (Exception ex) { Log.Warn("写入应用版本戳失败: " + ex.Message); }
            }
            catch (Exception ex)
            {
                Log.Warn("更新清理失败（忽略）: " + ex.Message);
            }
        }

        private string _uiVersion = "";   /// <summary>当前生效的 UI 版本戳（界面自动刷新依据）。</summary>

        private static readonly string[] names = new string[]
        {
            "index.html", "style.css", "theme.css", "shell.js", "garden.css", "garden.js",
            "js/util.js", "js/markdown.js", "js/tools.js", "js/chat.js", "js/model.js", "js/app.js", "js/toolbox.js",
            "manifest.webmanifest", "icon-192.png", "icon-512.png", "swirl.png"
        };
        private static readonly object UiRepairGate = new object();

        private void ExportEmbeddedUi()
        {
            try
            {
                AppPaths.EnsureDirectory(AppPaths.UiDir);
                string stampFile = Path.Combine(AppPaths.UiDir, UiStampName);
                if (StampMatches(stampFile, AppInfo.Version))
                {
                    bool foundIssue;
                    string report = CheckUiAssets(true, out foundIssue);
                    if (foundIssue) Log.Info(report);
                    _uiVersion = AppInfo.Version;
                    return;
                }

                // 版本变更 = 旧 UI 整目录删除再重建（不是逐文件覆盖）：
                // 旧版本遗留的孤儿文件（改名的资源、历史编辑落点）不会被带进新版本。
                try
                {
                    if (Directory.Exists(AppPaths.UiDir)) Directory.Delete(AppPaths.UiDir, true);
                }
                catch (Exception ex) { Log.Warn("删除旧 UI 目录失败（将继续覆盖式同步）: " + ex.Message); }
                AppPaths.EnsureDirectory(AppPaths.UiDir);

                int written = 0;
                foreach (string name in names)
                {
                    Stream stream = typeof(WebServer).Assembly.GetManifestResourceStream("GreenTiger.ui." + name);
                    if (stream == null) continue;
                    // 模块落在 ui\js\ 子目录：资源名里的 / 换成目录分隔符并先建目录
                    string dst = Path.Combine(AppPaths.UiDir, name.Replace('/', Path.DirectorySeparatorChar));
                    string dstDir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                    using (stream)
                    using (FileStream fs = new FileStream(dst, FileMode.Create, FileAccess.Write))
                    {
                        stream.CopyTo(fs);
                        written++;
                    }
                }
                RemoveLegacyUiFiles();
                try { File.WriteAllText(stampFile, AppInfo.Version, new UTF8Encoding(false)); }
                catch (Exception ex) { Log.Warn("写入 UI 版本戳失败: " + ex.Message); }
                Log.Info("已同步 UI 到数据目录（" + AppInfo.Version + "，" + written + " 个文件）: " + AppPaths.UiDir);
                _uiVersion = AppInfo.Version;
            }
            catch (Exception ex) { Log.Warn("同步 UI 编辑源失败: " + ex.Message); }
        }

        internal static string CheckUiAssets(bool repair)
        {
            bool foundIssue;
            return CheckUiAssets(repair, out foundIssue);
        }

        private static string CheckUiAssets(bool repair, out bool foundIssue)
        {
            lock (UiRepairGate)
            {
                StringBuilder details = new StringBuilder();
                int healthy = 0, pending = 0, restored = 0, failed = 0;
                foreach (string name in names)
                {
                    string path = Path.Combine(AppPaths.UiDir, name.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        long length = UiAssetLength(path);
                        if (length > 0)
                        {
                            healthy++;
                            details.AppendLine("健康 " + name);
                            continue;
                        }
                        string cause = length < 0 ? "缺失" : "零字节";
                        if (repair)
                        {
                            RestoreUiAsset(name, path);
                            restored++;
                            details.AppendLine("已修复 " + name + "（" + cause + "）");
                        }
                        else
                        {
                            pending++;
                            details.AppendLine("需修复 " + name + "（" + cause + "）");
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        details.AppendLine("失败 " + name + "（" + ex.Message + "）");
                    }
                }
                foundIssue = pending + restored + failed > 0;
                return "界面资源检查（缺失或零字节） " + AppPaths.UiDir + "\n健康 " + healthy + "，需修复 " + pending +
                    "，已修复 " + restored + "，失败 " + failed + "。\n" + details.ToString().TrimEnd();
            }
        }

        // 检查数据根、ui、子目录及文件。保留开发者在数据目录内编辑过的非空资源。
        private static long UiAssetLength(string path)
        {
            CheckUiDirectory(AppPaths.Root);
            CheckUiDirectory(AppPaths.UiDir);
            string parent = Path.GetDirectoryName(path);
            if (parent != AppPaths.UiDir) CheckUiDirectory(parent);
            FileAttributes? attributes = UiAttributes(path);
            if (!attributes.HasValue) return -1;
            if ((attributes.Value & FileAttributes.Directory) != 0)
                throw new IOException("文件路径被目录占用：" + path);
            return new FileInfo(path).Length;
        }

        private static void CheckUiDirectory(string path)
        {
            FileAttributes? attributes = UiAttributes(path);
            if (attributes.HasValue && (attributes.Value & FileAttributes.Directory) == 0)
                throw new IOException("资源目录路径被文件占用：" + path);
        }

        private static FileAttributes? UiAttributes(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("路径是重解析点：" + path);
                return attributes;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static void RestoreUiAsset(string name, string path)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temp = Path.Combine(directory, ".gt-ui-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool ownTemp = false;
            try
            {
                using (Stream source = typeof(App).Assembly.GetManifestResourceStream("GreenTiger.ui." + name))
                {
                    if (source == null || source.Length == 0) throw new IOException("程序中缺少内嵌副本");
                    using (FileStream target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        ownTemp = true;
                        source.CopyTo(target);
                        target.Flush(true);
                    }
                    long length = UiAssetLength(path);
                    if (length > 0) throw new IOException("检查后文件已被其他操作更新，请重新检查");
                    if (length < 0) File.Move(temp, path);
                    else File.Replace(temp, path, null);
                    ownTemp = false;
                    if (UiAssetLength(path) != source.Length) throw new IOException("恢复后文件大小与内嵌副本不一致");
                }
            }
            finally
            {
                if (ownTemp) File.Delete(temp);
            }
        }

        /// <summary>UI 版本戳文件名：内容为当前版本号。</summary>
        private const string UiStampName = ".ui-version";

        private static bool StampMatches(string stampFile, string version)
        {
            try
            {
                if (!File.Exists(stampFile)) return false;
                return string.Equals(File.ReadAllText(stampFile, Encoding.UTF8).Trim(), version, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>清理历史版本留在数据目录 ui\ 里的旧文件（新版已不再引用它们）。</summary>
        private static void RemoveLegacyUiFiles()
        {
            string[] legacy = new string[]
            {
                "workspace.js", "workspace.css", "motion.js", "motion.css",
                "interactions.js", "interactions.css",
                "components.html", "demo-gsap.html", "demo-three.html",
                // shell.css 曾经是「独立页面外壳」的样式，主控制台从来没 link 过它，
                // 现在连文件都不再随包，留在数据目录里只会被误当成在用。
                "shell.css"
            };
            foreach (string name in legacy)
            {
                try
                {
                    string file = Path.Combine(AppPaths.UiDir, name);
                    if (File.Exists(file)) { File.Delete(file); Log.Info("已清理旧版 UI 文件: " + name); }
                }
                catch (Exception ex) { Log.Warn("清理旧版 UI 文件失败 " + name + ": " + ex.Message); }
            }
        }
    }
}
