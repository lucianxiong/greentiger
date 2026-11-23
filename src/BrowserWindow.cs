using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
#if WEBVIEW2_HOST
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
#endif

namespace GreenTiger
{
    /// <summary>
    /// 绿虎浏览器：绿虎自带的 WebView2 多标签浏览器窗口（真渲染，非投屏）。
    /// 控制台与内置网页都跑在这个窗口里，不启动任何外接浏览器。
    /// 编译时未启用 WEBVIEW2_HOST 或组件缺失时 Available=false，调用方给出提示。
    /// </summary>
    public static class BrowserWindow
    {
#if WEBVIEW2_HOST
        private static BrowserForm _win;
        private static CoreWebView2Environment _environment;
        private static Task<CoreWebView2Environment> _environmentTask;
        private static readonly object _environmentGate = new object();
        private static int _debugPort;
#endif

        /// <summary>WebView2 运行时与托管组件是否可用。</summary>
        public static bool Available
        {
            get
            {
#if WEBVIEW2_HOST
                try
                {
                    string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                    return !string.IsNullOrEmpty(version);
                }
                catch { return false; }
#else
                return false;
#endif
            }
        }

        /// <summary>绿虎浏览器的 CDP 调试端口（供脚本化调试，与模型通道无关）。</summary>
        public static int DebugPort
        {
            get
            {
#if WEBVIEW2_HOST
                if (_debugPort <= 0)
                {
                    int port = HttpUtil.FindFreePort(9333);
                    _debugPort = port > 0 ? port : 9333;
                }
                return _debugPort;
#else
                return 0;
#endif
            }
        }

        /// <summary>打开一个标签页；url 为空时打开起始页。必须在 UI 线程以外也可安全调用。</summary>
        public static bool OpenTab(string url, bool activate)
        {
#if WEBVIEW2_HOST
            bool ok = false;
            Ui.Invoke(delegate
            {
                try
                {
                    if (!Available)
                    {
                        Log.Error("绿虎浏览器不可用：缺少 WebView2 运行时");
                        return;
                    }
                    bool created = false;
                    if (_win == null || _win.IsDisposed)
                    {
                        _win = new BrowserForm();
                        created = true;
                    }
                    if (!string.IsNullOrEmpty(url))
                    {
                        _win.AddTab(url, activate);
                    }
                    else if (created || _win.TabCount == 0 || activate)
                    {
                        // 没有网址时打开起始页；已有窗口时也新建一个标签，
                        // 这样点击「打开绿虎浏览器」有明确反馈，而不是静默聚焦。
                        _win.AddTab(_win.StartPageHtml(), true, true);
                    }
                    if (created) _win.Show();
                    if (activate)
                    {
                        if (_win.WindowState == FormWindowState.Minimized)
                            _win.WindowState = FormWindowState.Normal;
                        _win.Activate();
                        _win.BringToFront();
                    }
                    ok = true;
                }
                catch (Exception ex)
                {
                    Log.Error("打开绿虎浏览器失败", ex);
                }
            });
            return ok;
#else
            return false;
#endif
        }

        /// <summary>打开控制台；已有控制台标签时直接激活，避免重复开标签。</summary>
        public static bool OpenConsole(string url)
        {
#if WEBVIEW2_HOST
            bool ok = false;
            Ui.Invoke(delegate
            {
                try
                {
                    if (!Available) return;
                    bool created = false;
                    if (_win == null || _win.IsDisposed)
                    {
                        _win = new BrowserForm();
                        created = true;
                    }
                    // 控制台启动 URL 带一次性 token，标签里通常已去掉查询串；
                    // 按去掉查询串的地址匹配，避免每次「打开控制台」都新开一个标签。
                    string match = url == null ? "" : url;
                    int query = match.IndexOf('?');
                    if (query > 0) match = match.Substring(0, query);
                    _win.SetConsoleBase(url);
                    if (!_win.ActivateTabByExactUrl(match))
                    {
                        _win.AddTab(url, true);
                    }
                    if (created) _win.Show();
                    else
                    {
                        if (_win.WindowState == FormWindowState.Minimized)
                            _win.WindowState = FormWindowState.Normal;
                        _win.Activate();
                        _win.BringToFront();
                    }
                    ok = true;
                }
                catch (Exception ex)
                {
                    Log.Error("打开控制台失败", ex);
                }
            });
            return ok;
#else
            return false;
#endif
        }

        /// <summary>聚焦绿虎浏览器窗口（用于第二实例唤醒）。</summary>
        public static bool Activate()
        {
#if WEBVIEW2_HOST
            bool ok = false;
            Ui.Invoke(delegate
            {
                try
                {
                    if (_win == null || _win.IsDisposed) return;
                    if (_win.WindowState == FormWindowState.Minimized)
                        _win.WindowState = FormWindowState.Normal;
                    _win.Activate();
                    _win.BringToFront();
                    ok = true;
                }
                catch { }
            });
            return ok;
#else
            return false;
#endif
        }

        /// <summary>激活网址以指定前缀开头的标签页。</summary>
        public static bool ActivateTabByUrl(string urlPart)
        {
#if WEBVIEW2_HOST
            bool ok = false;
            Ui.Invoke(delegate
            {
                try
                {
                    if (_win == null || _win.IsDisposed) return;
                    ok = _win.ActivateTabByUrl(urlPart);
                    if (ok)
                    {
                        if (_win.WindowState == FormWindowState.Minimized)
                            _win.WindowState = FormWindowState.Normal;
                        _win.Activate();
                        _win.BringToFront();
                    }
                }
                catch { }
            });
            return ok;
#else
            return false;
#endif
        }

        /// <summary>关闭包含指定网址片段的标签页。</summary>
        public static bool CloseTabByUrl(string urlPart)
        {
#if WEBVIEW2_HOST
            bool ok = false;
            Ui.Invoke(delegate
            {
                try
                {
                    if (_win != null && !_win.IsDisposed) ok = _win.CloseTabByUrl(urlPart);
                }
                catch { }
            });
            return ok;
#else
            return false;
#endif
        }

        /// <summary>关闭整个绿虎浏览器窗口（退出绿虎时用）。</summary>
        public static void CloseAll()
        {
#if WEBVIEW2_HOST
            Ui.Invoke(delegate
            {
                try
                {
                    if (_win != null && !_win.IsDisposed) _win.Close();
                }
                catch { }
                _win = null;
            });
#endif
        }

#if WEBVIEW2_HOST
        /// <summary>所有标签共享同一个 WebView2 环境（缓存 + CDP 调试端口）。</summary>
        internal static Task<CoreWebView2Environment> EnsureEnvironmentAsync()
        {
            lock (_environmentGate)
            {
                if (_environmentTask != null) return _environmentTask;
                _environmentTask = CreateEnvironmentAsync();
                return _environmentTask;
            }
        }

        /// <summary>
        /// 清理 WebView2 持久 profile 里的 GPU 着色器/GPU 缓存：这些缓存损坏是
        /// 「网页部分字形（数字/符号）不显示、重绘才出现」这一 WebView2 已知症状的常见根源。
        /// 实际位置在 profile 下 EBWebView（及 EBWebView/Default）子目录；只删可再生缓存，
        /// Cookie / 登录态 / LocalStorage / 密码等用户数据一概不动。
        /// </summary>
        private static void CleanupBrowserCaches(string profileFolder)
        {
            string[] roots;
            try
            {
                if (!Directory.Exists(profileFolder)) return;
                roots = Directory.GetDirectories(profileFolder, "EBWebView", SearchOption.AllDirectories);
                if (roots.Length == 0) roots = new string[] { profileFolder };
            }
            catch { return; }
            string[] cacheDirs = { "GPUCache", "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "DawnWebGPUCache", "DawnGraphiteCache", "Code Cache" };
            int removed = 0;
            for (int r = 0; r < roots.Length; r++)
            {
                for (int i = 0; i < cacheDirs.Length; i++)
                {
                    try
                    {
                        string dir = Path.Combine(roots[r], cacheDirs[i]);
                        if (!Directory.Exists(dir)) continue;
                        Directory.Delete(dir, true);
                        removed++;
                    }
                    catch { /* 被占用就跳过 */ }
                }
            }
            if (removed > 0) Log.Info("已清理浏览器 GPU/着色器缓存 " + removed + " 项（防字形不显示）。");
        }

        private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            try
            {
                if (_environment != null) return _environment;
                string folder = AppPaths.WebView2Profile;
                AppPaths.EnsureDirectory(folder);
                CleanupBrowserCaches(folder);
                CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions();
                options.AdditionalBrowserArguments =
                    "--remote-debugging-port=" + DebugPort +
                    " --disable-background-timer-throttling" +
                    " --disable-backgrounding-occluded-windows" +
                    " --disable-renderer-backgrounding" +
                    " --disable-gpu-shader-disk-cache" +
                    " --disable-features=CalculateNativeWinOcclusion";
                _environment = await CoreWebView2Environment.CreateAsync(null, folder, options).ConfigureAwait(true);
                Log.Info("绿虎浏览器已就绪，CDP 端口 " + DebugPort);
                return _environment;
            }
            catch
            {
                lock (_environmentGate) { _environmentTask = null; }
                throw;
            }
        }
#endif
    }

#if WEBVIEW2_HOST
    /// <summary>绿虎浏览器窗口本体（深色主题 + 多标签 WebView2）。</summary>
    public class BrowserForm : Form
    {
        public class Tab
        {
            public WebView2Wrapper View;
            public string Title = "新标签页";
            public string Url = "";
        }

        // ---- 敏感输入接管（3.20.0）----
        private static readonly object SensitiveGate = new object();
        private static readonly List<IntPtr> SensitiveWindows = new List<IntPtr>();

        /// <summary>页面脚本上报：某浏览器窗口里有密码/支付输入框聚焦状态变化。</summary>
        internal static void SetSensitive(IntPtr formHandle, bool active)
        {
            lock (SensitiveGate)
            {
                if (active)
                {
                    if (!SensitiveWindows.Contains(formHandle)) SensitiveWindows.Add(formHandle);
                }
                else SensitiveWindows.Remove(formHandle);
            }
        }

        /// <summary>截图守卫：window 模式对指定窗口、screen 模式对前台窗口；命中敏感窗口返回其句柄，否则 Zero。</summary>
        internal static IntPtr SensitiveCaptureTarget(IntPtr targetWindow, bool screenMode)
        {
            lock (SensitiveGate)
            {
                if (SensitiveWindows.Count == 0) return IntPtr.Zero;
                IntPtr fg = screenMode ? Native.GetForegroundWindow() : targetWindow;
                for (int i = 0; i < SensitiveWindows.Count; i++)
                {
                    if (SensitiveWindows[i] == fg) return fg;
                }
                return IntPtr.Zero;
            }
        }

        private readonly List<Tab> _tabs = new List<Tab>();
        private Tab _active;
        private TabStrip _tabStrip;
        private RoundedTable _nav;
        private RoundedFlow _channels;
        private TextBox _address;
        private RoundedPanel _addressShell;
        private Panel _viewHost;
        private TableLayoutPanel _root;
        private RoundedPanel _deck;
        private RoundedPanel _viewShell;
        private string _consoleBase = "";
        private bool _chromeForced, _chromeHidden;
        private Button _btnBack, _btnForward, _btnRefresh, _btnHome, _btnNew, _btnGo;
        private GreenButton _btnAdd;
        private readonly List<FavoriteLink> _favorites = FavoritesStore.Load();
        private Image _logoImage;
        // 当前配色（跟随控制台的深浅设置）。属性形式暴露给 BuildUi 的各处使用。
        internal ChromePalette P = ChromeTheme.IsDark ? ChromePalette.Dark : ChromePalette.Light;
        private ChromePalette _paletteInUse;
        private Color _bg { get { return P.Bg; } }
        private Color _bar { get { return P.Bar; } }
        private Color _barLight { get { return P.BarLight; } }
        private Color _fg { get { return P.Fg; } }
        private Color _accent { get { return P.Accent; } }
        private Color _tabInactive { get { return P.TabInactive; } }
        private Color _sep { get { return P.Sep; } }
        private Color _line { get { return P.Line; } }
        private Color _addressBorder { get { return P.AddressBorder; } }

        public int TabCount { get { return _tabs.Count; } }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetDwmTheme();
        }

        /// <summary>DWM 属性跟随主题：亮色标题栏 / 暗色标题栏 + Windows 11 圆角。</summary>
        private void SetDwmTheme()
        {
            try
            {
                int dark = P.IsDark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
                int caption = ColorTranslator.ToWin32(P.IsDark ? P.Bg : P.Bar);
                DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
                int border = ColorTranslator.ToWin32(P.Line);
                DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
                // Windows 11：整个窗口圆角（DWMWA_WINDOW_CORNER_PREFERENCE=33, DWMWCP_ROUND=2）。
                int round = 2;
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            }
            catch { }
        }

        public BrowserForm()
        {
            Text = "绿虎";
            BackColor = _bg;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1360, 860);
            MinimumSize = new Size(760, 480);
            Font = new Font("Microsoft YaHei UI", 9F);
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            _paletteInUse = P;
            ChromeTheme.Changed += OnChromeThemeChanged;
            FormClosed += delegate { ChromeTheme.Changed -= OnChromeThemeChanged; };
            BuildUi();
            FormClosing += delegate
            {
                for (int i = 0; i < _tabs.Count; i++) _tabs[i].View.Dispose();
                _tabs.Clear();
                _active = null;
            };
            Shown += delegate
            {
                if (_tabs.Count == 0) AddTab(StartPageHtml(), true, true);
            };
        }

        /// <summary>控制台切换深浅色：整窗换肤。</summary>
        private void OnChromeThemeChanged(string theme)
        {
            if (InvokeRequired) { try { BeginInvoke((MethodInvoker)delegate { ApplyChromeTheme(theme == "dark"); }); } catch { } }
            else ApplyChromeTheme(theme == "dark");
        }

        /// <summary>把整棵控件树从旧配色刷成当前配色，并同步 DWM / 网页底色 / 起始页。</summary>
        private void ApplyChromeTheme(bool dark)
        {
            ChromePalette target = dark ? ChromePalette.Dark : ChromePalette.Light;
            if (ReferenceEquals(P, target)) return;
            ChromePalette from = _paletteInUse ?? P;
            P = target;
            _paletteInUse = P;
            if (_tabStrip != null) _tabStrip.P = P;
            RestyleTree(this, from);
            SetDwmTheme();
            if (_tabStrip != null) _tabStrip.Invalidate();
            for (int i = 0; i < _tabs.Count; i++)
            {
                Tab t = _tabs[i];
                if (t.View != null && t.View.ViewControl != null)
                {
                    try { t.View.ViewControl.DefaultBackgroundColor = P.Bg; } catch { }
                }
            }
            // 活动标签若停在起始页（纯 HTML），用新配色重生成
            if (_active != null && _active.Url != null && _active.Url.StartsWith("data:", StringComparison.Ordinal))
            {
                NavigateActive(StartPageHtml(), true);
            }
        }

        private Color MapColor(Color c, ChromePalette from)
        {
            if (c.ToArgb() == from.Bg.ToArgb()) return P.Bg;
            if (c.ToArgb() == from.Bar.ToArgb()) return P.Bar;
            if (c.ToArgb() == from.BarLight.ToArgb()) return P.BarLight;
            if (c.ToArgb() == from.Fg.ToArgb()) return P.Fg;
            if (c.ToArgb() == from.Accent.ToArgb()) return P.Accent;
            if (c.ToArgb() == from.TabInactive.ToArgb()) return P.TabInactive;
            if (c.ToArgb() == from.TabHover.ToArgb()) return P.TabHover;
            if (c.ToArgb() == from.Sep.ToArgb()) return P.Sep;
            if (c.ToArgb() == from.Line.ToArgb()) return P.Line;
            if (c.ToArgb() == from.AddressBorder.ToArgb()) return P.AddressBorder;
            if (c.ToArgb() == from.CloseInk.ToArgb()) return P.CloseInk;
            if (c.ToArgb() == from.TabActiveInk.ToArgb()) return P.TabActiveInk;
            return c;
        }

        private void RestyleTree(Control c, ChromePalette from)
        {
            c.BackColor = MapColor(c.BackColor, from);
            c.ForeColor = MapColor(c.ForeColor, from);
            IRoundedBorder rb = c as IRoundedBorder;
            if (rb != null) rb.BorderColor = MapColor(rb.BorderColor, from);
            foreach (Control child in c.Controls) RestyleTree(child, from);
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.BackColor = _bg;
            root.Margin = new Padding(0);
            root.Padding = new Padding(10, 10, 10, 10);
            // 关键：列宽必须显式 Percent 100，否则列停在子控件首选宽度，窗口放大后右侧露底
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 134));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);
            _root = root;

            // 顶部一体化面板：标签栏 + 导航栏 + 收藏夹栏合成一张圆角卡片，行间细分隔线
            RoundedPanel deck = new RoundedPanel();
            deck.Dock = DockStyle.Fill;
            deck.BackColor = _bar;
            deck.BorderColor = _line;
            deck.Radius = 14;
            deck.Margin = new Padding(0, 0, 0, 10);
            deck.Padding = new Padding(8, 6, 8, 6);
            _deck = deck;
            TableLayoutPanel deckRows = new TableLayoutPanel();
            deckRows.Dock = DockStyle.Fill;
            deckRows.BackColor = _bar;
            deckRows.ColumnCount = 1;
            deckRows.RowCount = 5;
            deckRows.Margin = new Padding(0);
            // 列宽显式 Percent 100：否则列停在子控件首选宽度，标签条会被截断成一条窄列
            deckRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            deckRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            deckRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            deckRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            deckRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            deckRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            deck.Controls.Add(deckRows);
            root.Controls.Add(deck, 0, 0);

            // 标签栏：单控件自绘（logo + 标签 + 关闭钮），无子控件布局
            _tabStrip = new TabStrip();
            _tabStrip.Dock = DockStyle.Fill;
            _tabStrip.BackColor = _bar;
            _tabStrip.P = P;
            _tabStrip.Margin = new Padding(0);
            _tabStrip.OnActivate = delegate(int idx) { if (idx >= 0 && idx < _tabs.Count) ActivateTab(_tabs[idx]); };
            _tabStrip.OnClose = delegate(int idx) { CloseTab(idx); };
            deckRows.Controls.Add(_tabStrip, 0, 0);
            deckRows.Controls.Add(MakeSep(), 0, 1);

            // 导航栏
            _nav = new RoundedTable();
            _nav.Dock = DockStyle.Fill;
            _nav.BackColor = _bar;
            _nav.Radius = 0;
            _nav.Margin = new Padding(0);
            _nav.Padding = new Padding(6, 4, 6, 4);
            _nav.ColumnCount = 7;
            _nav.RowCount = 1;
            _nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            _nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            _nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            _nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            _nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            _nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            _nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            deckRows.Controls.Add(_nav, 0, 2);
            deckRows.Controls.Add(MakeSep(), 0, 3);

            _btnNew = MakeNavButton("", "新建标签页", GtIcon.Plus);
            _btnBack = MakeNavButton("", "后退", GtIcon.Back);
            _btnForward = MakeNavButton("", "前进", GtIcon.Forward);
            _btnRefresh = MakeNavButton("", "刷新", GtIcon.Refresh);
            _btnHome = MakeNavButton("", "起始页", GtIcon.Home);
            _nav.Controls.Add(_btnNew, 0, 0);
            _nav.Controls.Add(_btnBack, 1, 0);
            _nav.Controls.Add(_btnForward, 2, 0);
            _nav.Controls.Add(_btnRefresh, 3, 0);
            _nav.Controls.Add(_btnHome, 4, 0);

            // 地址栏：原生边框无法圆角，用无边框文本框套在圆角面板里
            _addressShell = new RoundedPanel();
            _addressShell.Dock = DockStyle.Fill;
            _addressShell.Margin = new Padding(4, 2, 4, 2);
            _addressShell.BackColor = _barLight;
            _addressShell.BorderColor = _addressBorder;
            _addressShell.Radius = 10;
            _addressShell.Padding = new Padding(10, 0, 10, 0);
            _address = new TextBox();
            _address.Dock = DockStyle.Fill;
            _address.BorderStyle = BorderStyle.None;
            _address.BackColor = _addressShell.BackColor;
            _address.ForeColor = _fg;
            _address.Font = new Font("Segoe UI", 10F);
            _address.Margin = new Padding(0);
            _addressShell.Controls.Add(_address);
            _nav.Controls.Add(_addressShell, 5, 0);

            _btnGo = MakeNavButton("打开", "导航到地址", GtIcon.None);
            _btnGo.BackColor = _accent;
            _btnGo.ForeColor = P.TabActiveInk;
            _nav.Controls.Add(_btnGo, 6, 0);

            _btnNew.Click += delegate { AddTab(StartPageHtml(), true, true); };
            _btnHome.Click += delegate { NavigateActive(StartPageHtml(), true); };
            _btnRefresh.Click += delegate
            {
                if (_active == null || _active.View == null) return;
                try { _active.View.Reload(); } catch { }
            };
            _btnBack.Click += delegate { TryNav(-1); };
            _btnForward.Click += delegate { TryNav(1); };
            _btnGo.Click += delegate { GoAddress(); };
            _address.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { GoAddress(); e.SuppressKeyPress = true; }
            };

            // 收藏夹栏（左上角快捷入口）：用户可增删改，右键管理，持久化
            _channels = new RoundedFlow();
            _channels.Dock = DockStyle.Fill;
            _channels.BackColor = _bar;
            _channels.Radius = 0;
            _channels.Margin = new Padding(0);
            _channels.Padding = new Padding(4, 2, 4, 2);
            _channels.WrapContents = false;
            _channels.AutoScroll = false;
            _channels.FlowDirection = FlowDirection.LeftToRight;
            _channels.ContextMenuStrip = BuildFavBarMenu();
            deckRows.Controls.Add(_channels, 0, 4);

            RebuildFavorites();

            // 网页视区：圆角外壳
            RoundedPanel viewShell = new RoundedPanel();
            viewShell.Dock = DockStyle.Fill;
            viewShell.Margin = new Padding(0);
            viewShell.BackColor = _bg;
            viewShell.BorderColor = _line;
            viewShell.Radius = 14;
            _viewShell = viewShell;
            _viewHost = new Panel();
            _viewHost.Dock = DockStyle.Fill;
            _viewHost.Margin = new Padding(0);
            _viewHost.BackColor = _bg;
            viewShell.Controls.Add(_viewHost);
            root.Controls.Add(viewShell, 0, 1);
        }

        /// <summary>记录控制台地址：控制台是应用界面，不是普通网页。</summary>
        public void SetConsoleBase(string url)
        {
            try
            {
                Uri u = new Uri(url);
                _consoleBase = u.GetLeftPart(UriPartial.Authority) + "/";
            }
            catch { _consoleBase = ""; }
        }

        private bool IsConsoleTab(Tab tab)
        {
            if (tab == null) return false;
            string u = tab.Url ?? "";
            if (u.Length == 0 && tab.View != null) u = tab.View.Url ?? "";
            if (u.Length == 0) return false;
            if (_consoleBase.Length > 0 && u.StartsWith(_consoleBase, StringComparison.OrdinalIgnoreCase)) return true;
            return u.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase) ||
                   u.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 沉浸式控制台：只剩控制台一个标签时隐藏标签栏 / 地址栏 / 收藏栏，网页铺满整窗，
        /// frameless 布局。新开标签、跳到外部站点或按 Ctrl+L /
        /// Ctrl+Shift+B 时工具栏自动回来，浏览能力不受影响。
        /// </summary>
        public void ApplyChromeMode()
        {
            if (_root == null || _deck == null || _viewShell == null) return;
            bool hide = !_chromeForced && _tabs.Count == 1 && IsConsoleTab(_active);
            if (hide == _chromeHidden) return;
            _chromeHidden = hide;
            _deck.Visible = !hide;
            _root.RowStyles[0] = new RowStyle(SizeType.Absolute, hide ? 0f : 134f);
            _root.Padding = hide ? new Padding(0) : new Padding(10);
            _viewShell.Radius = hide ? 0 : 14;
            _viewShell.BorderColor = hide ? _bg : _line;
            _viewShell.Invalidate();
            _root.PerformLayout();
        }

        /// <summary>Ctrl+Shift+B：手动显隐浏览器工具栏。</summary>
        public void ToggleChrome()
        {
            _chromeForced = !_chromeForced;
            ApplyChromeMode();
        }

    /// <summary>顶部卡片内的行间细分隔线。</summary>
    private Panel MakeSep()
    {
        Panel p = new Panel();
        p.Dock = DockStyle.Fill;
        p.Height = 1;
        p.BackColor = _sep;
        p.Margin = new Padding(0);
        return p;
    }

        private ToolTip _tips; // 全窗共享一个 ToolTip，避免每次重建收藏夹都泄漏

        private GreenButton MakeNavButton(string text, string tip, GtIcon icon)
        {
            GreenButton b = new GreenButton();
            b.Text = text;
            b.GtIcon = icon;
            b.BackColor = _bar;
            b.ForeColor = _fg;
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(0, 0, 4, 0);
            b.Radius = 10;
            if (_tips == null) _tips = new ToolTip();
            _tips.SetToolTip(b, tip);
            return b;
        }

        private Image LoadLogo()
        {
            try
            {
                Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GreenTiger.ui.icon-192.png");
                if (s == null) s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GreenTiger.ui.swirl.png");
                if (s != null)
                {
                    using (s)
                    using (Image raw = Image.FromStream(s))
                    {
                        return new Bitmap(raw);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>重建收藏夹栏：每个收藏一个胶囊按钮（左键打开 / 右键编辑删除），末尾一个添加按钮。</summary>
        private void RebuildFavorites()
        {
            _channels.SuspendLayout();
            for (int i = _channels.Controls.Count - 1; i >= 0; i--) _channels.Controls[i].Dispose();
            _channels.Controls.Clear();
            for (int i = 0; i < _favorites.Count; i++)
            {
                int idx = i;
                FavoriteLink f = _favorites[i];
                GreenButton b = new GreenButton();
                b.Text = f.Name;
                b.AutoSize = true;
                b.Height = 28;
                b.Margin = new Padding(0, 0, 6, 0);
                b.Padding = new Padding(12, 0, 12, 0);
                b.Radius = 14;
                b.BackColor = _barLight;
                b.ForeColor = _fg;
                _tips.SetToolTip(b, f.Url);
                
                b.Click += delegate { AddTab(f.Url, true); };
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.BackColor = _bar;
                menu.ForeColor = _fg;
                menu.Items.Add("打开", null, delegate { AddTab(_favorites[idx].Url, true); });
                menu.Items.Add("编辑…", null, delegate { EditFavorite(idx); });
                menu.Items.Add("删除", null, delegate { RemoveFavorite(idx); });
                HookMenu(menu);
                b.ContextMenuStrip = menu;
                _channels.Controls.Add(b);
            }
            _btnAdd = new GreenButton();
            _btnAdd.GtIcon = GtIcon.Plus;
            _btnAdd.Width = 28;
            _btnAdd.Height = 28;
            _btnAdd.Margin = new Padding(0);
            _btnAdd.Radius = 14;
            _btnAdd.BackColor = _barLight;
            _btnAdd.ForeColor = _accent;
            
            _tips.SetToolTip(_btnAdd, "添加收藏（也可右键收藏夹栏）");
            _btnAdd.Click += delegate { AddFavorite(); };
            _channels.Controls.Add(_btnAdd);
            _channels.ResumeLayout(true);
            _channels.Invalidate(true);
            _channels.Update();
        }

        private ContextMenuStrip BuildFavBarMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.BackColor = _bar;
            menu.ForeColor = _fg;
            menu.Items.Add("添加收藏…", null, delegate { AddFavorite(); });
            HookMenu(menu);
            return menu;
        }

        /// <summary>菜单统一换肤 + 点击外部（含网页区）自动关闭。</summary>
        private void HookMenu(ContextMenuStrip menu)
        {
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.Opening += delegate
            {
                menu.BackColor = P.Bar;
                menu.ForeColor = P.Fg;
                menu.Renderer = new FlatMenuRenderer(P);
            };
            menu.Opened += delegate { MenuOutsideHook.Attach(menu); };
            menu.Closed += delegate { MenuOutsideHook.Detach(); };
        }

        private void AddFavorite()
        {
            string name = "", url = "";
            if (!PromptFavorite("添加收藏", ref name, ref url)) return;
            _favorites.Add(new FavoriteLink(name, url));
            FavoritesStore.Save(_favorites);
            RebuildFavorites();
        }

        private void EditFavorite(int idx)
        {
            if (idx < 0 || idx >= _favorites.Count) return;
            string name = _favorites[idx].Name;
            string url = _favorites[idx].Url;
            if (!PromptFavorite("编辑收藏", ref name, ref url)) return;
            _favorites[idx].Name = name;
            _favorites[idx].Url = url;
            FavoritesStore.Save(_favorites);
            RebuildFavorites();
        }

        private void RemoveFavorite(int idx)
        {
            if (idx < 0 || idx >= _favorites.Count) return;
            _favorites.RemoveAt(idx);
            FavoritesStore.Save(_favorites);
            RebuildFavorites();
        }

        /// <summary>添加/编辑收藏的对话框（名称 + 网址）。</summary>
        private bool PromptFavorite(string title, ref string name, ref string url)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(380, 168);
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.BackColor = _bg;
                f.ForeColor = _fg;
                f.Font = Font;

                // 标题栏跟随主题：深色模式去掉系统白色标题栏
                try
                {
                    IntPtr hDlg = f.Handle; // 强制创建句柄
                    int dark = P.IsDark ? 1 : 0;
                    DwmSetWindowAttribute(hDlg, 20, ref dark, sizeof(int));
                    int caption = ColorTranslator.ToWin32(P.IsDark ? P.Bg : P.Bar);
                    DwmSetWindowAttribute(hDlg, 35, ref caption, sizeof(int));
                }
                catch { }

                Label ln = new Label();
                ln.Text = "名称";
                ln.SetBounds(16, 16, 60, 20);
                ln.ForeColor = _fg;
                TextBox tn = new TextBox();
                tn.Text = name;
                tn.SetBounds(80, 13, 284, 26);
                tn.BackColor = _barLight;
                tn.ForeColor = _fg;
                tn.BorderStyle = BorderStyle.FixedSingle;
                Label lu = new Label();
                lu.Text = "网址";
                lu.SetBounds(16, 52, 60, 20);
                lu.ForeColor = _fg;
                TextBox tu = new TextBox();
                tu.Text = url;
                tu.SetBounds(80, 49, 284, 26);
                tu.BackColor = _barLight;
                tu.ForeColor = _fg;
                tu.BorderStyle = BorderStyle.FixedSingle;

                GreenButton ok = new GreenButton();
                ok.Text = "确定";
                ok.BackColor = _accent;
                ok.ForeColor = P.TabActiveInk;
                ok.Radius = 10;
                ok.SetBounds(196, 112, 80, 32);
                ok.DialogResult = DialogResult.OK;
                GreenButton cancel = new GreenButton();
                cancel.Text = "取消";
                cancel.BackColor = _barLight;
                cancel.ForeColor = _fg;
                cancel.Radius = 10;
                cancel.SetBounds(284, 112, 80, 32);
                cancel.DialogResult = DialogResult.Cancel;

                f.Controls.Add(ln);
                f.Controls.Add(tn);
                f.Controls.Add(lu);
                f.Controls.Add(tu);
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Shown += delegate { tn.Focus(); };
                if (f.ShowDialog(this) != DialogResult.OK) return false;
                string n = tn.Text.Trim();
                string u = tu.Text.Trim();
                if (n.Length == 0 || u.Length == 0) return false;
                name = n;
                url = u;
                return true;
            }
        }

        protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
        {
            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            bool shift = (keyData & Keys.Shift) == Keys.Shift;
            bool alt = (keyData & Keys.Alt) == Keys.Alt;
            Keys code = keyData & Keys.KeyCode;

            if (ctrl && shift && code == Keys.B) { ToggleChrome(); return true; }
            if (ctrl && code == Keys.T) { NewTab(); return true; }
            if (ctrl && code == Keys.W) { CloseActiveTab(); return true; }
            if (ctrl && code == Keys.L) { FocusAddressBar(); return true; }
            if (ctrl && code == Keys.R) { ReloadActive(); return true; }
            if (code == Keys.F5) { ReloadActive(); return true; }
            if (code == Keys.F12) { OpenDevTools(); return true; }
            if (alt && code == Keys.Left) { TryNav(-1); return true; }
            if (alt && code == Keys.Right) { TryNav(1); return true; }
            if (ctrl && code == Keys.Tab) { CycleTab(shift ? -1 : 1); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>网页内容获得焦点时，WebView2 会吞掉快捷键，这里统一转发给浏览器。</summary>
        internal bool HandleAccelerator(int virtualKey, bool control, bool alt, bool shift)
        {
            Keys key = (Keys)virtualKey;
            if (control && shift && key == Keys.B) { ToggleChrome(); return true; }
            if (control && key == Keys.T) { NewTab(); return true; }
            if (control && key == Keys.W) { CloseActiveTab(); return true; }
            if (control && key == Keys.L) { FocusAddressBar(); return true; }
            if (control && key == Keys.R) { ReloadActive(); return true; }
            if (key == Keys.F5) { ReloadActive(); return true; }
            if (key == Keys.F12) { OpenDevTools(); return true; }
            if (alt && key == Keys.Left) { TryNav(-1); return true; }
            if (alt && key == Keys.Right) { TryNav(1); return true; }
            if (control && key == Keys.Tab) { CycleTab(shift ? -1 : 1); return true; }
            return false;
        }

        public void NewTab()
        {
            AddTab(StartPageHtml(), true, true);
        }

        public void CloseActiveTab()
        {
            if (IsDisposed) return;
            BeginInvoke((Action)delegate { CloseTab(_tabs.IndexOf(_active)); });
        }

        public void FocusAddressBar()
        {
            // 沉浸式下地址栏是隐藏的：Ctrl+L 先把工具栏叫回来，再聚焦输入框。
            _chromeForced = true;
            ApplyChromeMode();
            _address.Focus();
            _address.SelectAll();
        }

        public void ReloadActive()
        {
            if (_active != null && _active.View != null) _active.View.Reload();
        }

        public void OpenDevTools()
        {
            if (_active != null && _active.View != null) _active.View.OpenDevTools();
        }

        public void CycleTab(int step)
        {
            if (_tabs.Count < 2) return;
            int i = _tabs.IndexOf(_active);
            i = (i + step + _tabs.Count) % _tabs.Count;
            ActivateTab(_tabs[i]);
        }

        private void GoAddress()
        {
            string url = NormalizeAddress(_address.Text);
            if (url.Length == 0) return;
            if (_active == null) AddTab(url, true);
            else _active.View.Navigate(url, false);
        }

        private static string NormalizeAddress(string raw)
        {
            string text = raw == null ? "" : raw.Trim();
            if (text.Length == 0) return "";
            if (text.IndexOf("://") >= 0) return text;
            if (text.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("edge:", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
                return text;
            bool looksLikeHost = text.IndexOf(' ') < 0 &&
                (text.IndexOf('.') >= 0 || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                 text.StartsWith("127.", StringComparison.OrdinalIgnoreCase));
            if (looksLikeHost) return "https://" + text;
            return "https://www.bing.com/search?q=" + Uri.EscapeDataString(text);
        }

        private void TryNav(int dir)
        {
            if (_active == null || _active.View == null) return;
            try
            {
                if (dir < 0) _active.View.GoBack(); else _active.View.GoForward();
            }
            catch { }
        }

        /// <summary>新开标签。htmlOnly 时内容用字符串渲染（起始页），否则当网址导航。</summary>
        public Tab AddTab(string target, bool activate, bool htmlOnly = false)
        {
            Tab tab = new Tab();
            tab.View = new WebView2Wrapper(_viewHost, target, htmlOnly, tab, this);
            tab.Title = htmlOnly ? "新标签页" : "加载中…";
            tab.Url = htmlOnly ? "" : (target ?? "");
            _tabs.Add(tab);
            PaintTabBar();
            ApplyChromeMode();
            if (activate || _active == null) ActivateTab(tab);
            return tab;
        }

        /// <summary>按视图查找所属标签（供 WebView2Wrapper 标题/地址同步回调使用）。</summary>
        /// <summary>刷新某标签在标签栏上的文字。</summary>
        public void RefreshTabTitle(Tab tab)
        {
            if (tab != null && tab == _active)
            {
                Text = "绿虎";
            }
            PaintTabBar();
        }

        /// <summary>地址栏同步：只有来源变化来自当前活动标签时才更新地址栏。</summary>
        public void SyncAddressIfActive(WebView2Wrapper view)
        {
            if (_active != null && _active.View == view) { SyncAddress(); ApplyChromeMode(); }
        }

        private void ActivateTab(Tab tab)
        {
            _active = tab;
            for (int i = 0; i < _tabs.Count; i++)
            {
                Tab t = _tabs[i];
                bool on = t == tab;
                if (t.View != null)
                {
                    if (t.View.ViewControl != null)
                    {
                        t.View.ViewControl.Visible = on;
                        if (on) t.View.ViewControl.BringToFront();
                    }
                    if (on) t.View.Focus();
                }
            }
            Text = "绿虎";
            SyncAddress();
            PaintTabBar();
            ApplyChromeMode();
        }

        private void PaintTabBar()
        {
            if (_logoImage == null) _logoImage = LoadLogo();
            List<string> titles = new List<string>();
            int active = -1;
            for (int i = 0; i < _tabs.Count; i++)
            {
                titles.Add(ShortTitle(_tabs[i].Title));
                if (_tabs[i] == _active) active = i;
            }
            _tabStrip.Reload(_logoImage, titles, active);
        }

        private static string ShortTitle(string title)
        {
            string t = string.IsNullOrEmpty(title) ? "新标签页" : title.Trim();
            return t.Length > 18 ? t.Substring(0, 17) + "…" : t;
        }

        private void CloseTab(int idx)
        {
            if (idx < 0 || idx >= _tabs.Count) return;
            Tab t = _tabs[idx];
            bool wasActive = t == _active;
            _tabs.RemoveAt(idx);
            if (t.View != null) t.View.Dispose();
            if (wasActive) _active = null;
            PaintTabBar();
            ApplyChromeMode();
            if (_tabs.Count == 0)
            {
                AddTab(StartPageHtml(), true, true);
                return;
            }
            if (wasActive)
            {
                int next = idx < _tabs.Count ? idx : _tabs.Count - 1;
                ActivateTab(_tabs[next]);
            }
            else if (_active != null)
            {
                ActivateTab(_active);
            }
        }

        /// <summary>按完整网址（忽略查询串和片段）激活标签，用于控制台地址。</summary>
        public bool ActivateTabByExactUrl(string url)
        {
            string target = StripQueryAndHash(url);
            if (target.Length == 0) return false;
            for (int i = 0; i < _tabs.Count; i++)
            {
                Tab t = _tabs[i];
                string current = t.Url;
                if (string.IsNullOrEmpty(current) && t.View != null) current = t.View.Url;
                if (string.Equals(StripQueryAndHash(current), target, StringComparison.OrdinalIgnoreCase))
                {
                    ActivateTab(t);
                    return true;
                }
            }
            return false;
        }

        private static string StripQueryAndHash(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int cut = url.IndexOfAny(new char[] { '?', '#' });
            return cut >= 0 ? url.Substring(0, cut) : url;
        }

        /// <summary>按网址片段查找并关闭标签。</summary>
        public bool CloseTabByUrl(string urlPart)
        {
            if (string.IsNullOrEmpty(urlPart)) return false;
            for (int i = 0; i < _tabs.Count; i++)
            {
                Tab t = _tabs[i];
                string url = t.Url;
                if (string.IsNullOrEmpty(url) && t.View != null) url = t.View.Url;
                if (!string.IsNullOrEmpty(url) && url.IndexOf(urlPart, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    CloseTab(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>激活网址以指定前缀开头的标签。前缀必须落在路径/查询/片段边界上，
        /// 避免控制台前缀误匹配同源的 /components 等页面。</summary>
        public bool ActivateTabByUrl(string urlPart)
        {
            if (string.IsNullOrEmpty(urlPart)) return false;
            for (int i = 0; i < _tabs.Count; i++)
            {
                Tab t = _tabs[i];
                string url = t.Url;
                if (string.IsNullOrEmpty(url) && t.View != null) url = t.View.Url;
                if (string.IsNullOrEmpty(url) || !url.StartsWith(urlPart, StringComparison.OrdinalIgnoreCase)) continue;
                bool boundary = url.Length == urlPart.Length;
                if (!boundary)
                {
                    char next = url[urlPart.Length];
                    boundary = next == '/' || next == '?' || next == '#';
                }
                if (boundary)
                {
                    ActivateTab(t);
                    return true;
                }
            }
            return false;
        }

        private void SyncAddress()
        {
            try
            {
                if (_active != null)
                {
                    string u = _active.View != null ? _active.View.Url : "";
                    _address.Text = string.IsNullOrEmpty(u) ? "" : u;
                }
            }
            catch { }
        }

        private void NavigateActive(string target, bool htmlOnly)
        {
            if (_active == null || _active.View == null) return;
            _active.View.Navigate(target, htmlOnly);
        }

        /// <summary>起始页：快捷入口来自可编辑收藏夹，而不是写死的六个站点。</summary>
        internal string StartPageHtml()
        {
            StringBuilder links = new StringBuilder();
            for (int i = 0; i < _favorites.Count; i++)
            {
                FavoriteLink f = _favorites[i];
                links.Append("<a href='").Append(HtmlAttr(f.Url)).Append("'>").Append(HtmlEsc(f.Name)).Append("</a>");
            }
            string logo = "<img src='data:image/png;base64,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' width='56' height='56' style='border-radius:14px'>";
            bool dark = P.IsDark;
            string bg = dark ? "#162215,#0b100b" : "#f4f9ee,#e9f4da";
            string ink = dark ? "#eef6e8" : "#1c2a1a";
            string boxBg = dark ? "rgba(141,232,92,.06)" : "rgba(255,255,255,.72)";
            string boxBorder = dark ? "rgba(141,232,92,.20)" : "rgba(76,163,36,.18)";
            string boxShadow = dark ? "0 18px 50px rgba(0,0,0,.28)" : "0 18px 50px rgba(76,163,36,.10)";
            string h1 = dark ? "#eef6e8,#8de85c" : "#1c2a1a,#3c8a19";
            string sub = dark ? "#7e9375" : "#70855f";
            return "<html><head><meta charset='utf-8'><title>新标签页</title><style>body{font-family:'Microsoft YaHei UI',system-ui;background:linear-gradient(150deg," + bg + ");color:" + ink + ";margin:0;display:flex;align-items:center;justify-content:center;min-height:100vh}.box{text-align:center;padding:40px;background:" + boxBg + ";border:1px solid " + boxBorder + ";border-radius:24px;box-shadow:" + boxShadow + "}h1{font-size:26px;margin:0 0 8px;background:linear-gradient(90deg," + h1 + ");-webkit-background-clip:text;background-clip:text;color:transparent}p{color:" + sub + ";font-size:13px;margin:0 0 20px}.links{display:flex;gap:10px;flex-wrap:wrap;justify-content:center}a{color:#fff;text-decoration:none;padding:8px 16px;border-radius:999px;background:linear-gradient(135deg,#4ca324,#3c8a19);font-size:13px}</style></head><body><div class='box'>" + logo + "<h1>绿虎浏览器</h1><p>在地址栏输入网址，或用下面的快捷入口直接进入（右键收藏夹可编辑）</p><div class='links'>" + links + "</div></div></body></html>";
        }

        private static string HtmlEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        private static string HtmlAttr(string s)
        {
            return HtmlEsc(s);
        }


    }

    /// <summary>收藏夹条目（浏览器左上角快捷入口，用户可增删改，持久化到数据目录）。</summary>
    public class FavoriteLink
    {
        public string Name = "";
        public string Url = "";
        public FavoriteLink() { }
        public FavoriteLink(string name, string url) { Name = name; Url = url; }
    }

    /// <summary>收藏夹存取：data 目录 browser-favorites.json；缺失/损坏时回退默认六个站点。</summary>
    /// <summary>右键菜单的纯色渲染器：去掉系统白色 image margin / 浅色高亮，跟随当前主题。</summary>
    internal class FlatMenuRenderer : ToolStripRenderer
    {
        private readonly ChromePalette _p;
        public FlatMenuRenderer(ChromePalette p) { _p = p; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(_p.Bar)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            ToolStripItem item = e.Item;
            Rectangle r = new Rectangle(Point.Empty, item.Size);
            Color fill = item.Selected ? _p.TabHover : _p.Bar;
            using (SolidBrush b = new SolidBrush(fill)) e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = _p.Fg;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle r = new Rectangle(Point.Empty, new Size(e.ToolStrip.Width - 1, e.ToolStrip.Height - 1));
            using (Pen pen = new Pen(_p.Line)) e.Graphics.DrawRectangle(pen, r);
        }
    }

    /// <summary>菜单打开期间，点击任何外部区域（含 WebView2 网页，其鼠标消息不进 WinForms）
    /// 都自动关闭菜单——默认的菜单自动关闭对网页区无效。</summary>
    /// <summary>
    /// 菜单打开期间挂低级鼠标钩子：点击任何外部区域（含 WebView2 网页——它的鼠标消息
    /// 不进 WinForms 过滤器，菜单自己的模态循环也不走 Application filter）都自动关闭菜单。
    /// </summary>
    internal static class MenuOutsideHook
    {
        private const int WH_MOUSE_LL = 14;
        private static IntPtr _hook;
        private static ContextMenuStrip _menu;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLL { public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public IntPtr Extra; }

        public static void Attach(ContextMenuStrip menu)
        {
            Detach();
            _menu = menu;
            if (_hook == IntPtr.Zero)
            {
                _hook = SetWindowsHookEx(WH_MOUSE_LL, HookProc, GetModuleHandle(null), 0);
                Log.Info("MenuOutsideHook 挂载结果 hook=" + _hook);
            }
        }

        public static void Detach()
        {
            _menu = null;
            if (_hook != IntPtr.Zero) { try { UnhookWindowsHookEx(_hook); } catch { } _hook = IntPtr.Zero; }
        }

        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _menu != null && _menu.Visible)
            {
                int msg = wParam.ToInt32();
                const int LBUTTONDOWN = 0x0201, RBUTTONDOWN = 0x0204, MBUTTONDOWN = 0x0207;
                if (msg == LBUTTONDOWN || msg == RBUTTONDOWN || msg == MBUTTONDOWN)
                {
                    MSLL info = (MSLL)Marshal.PtrToStructure(lParam, typeof(MSLL));
                    if (!_menu.Bounds.Contains(info.X, info.Y))
                    {
                        ContextMenuStrip m = _menu;
                        try { m.BeginInvoke((Action)delegate { try { m.Close(ToolStripDropDownCloseReason.AppClicked); } catch { } }); }
                        catch { }
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }

    internal static class FavoritesStore
    {
        private static string FilePath { get { return Path.Combine(AppPaths.Root, "browser-favorites.json"); } }

        /// <summary>空收藏夹：不预置任何站点，全部由用户自己添加。</summary>
        public static List<FavoriteLink> Defaults()
        {
            return new List<FavoriteLink>();
        }

        /// <summary>
        /// 读取收藏夹。文件存在且能解析时以其内容为准——空数组就是「用户清空了收藏」，
        /// 不能因为列表为空就回填默认站点（否则删完又自己长回来）。
        /// </summary>
        public static List<FavoriteLink> Load()
        {
            if (!File.Exists(FilePath)) return Defaults();
            try
            {
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                object parsed = Json.Parse(json);
                if (!(parsed is object[])) return Defaults();
                object[] arr = (object[])parsed;
                List<FavoriteLink> list = new List<FavoriteLink>();
                for (int i = 0; i < arr.Length; i++)
                {
                    Dictionary<string, object> d = arr[i] as Dictionary<string, object>;
                    if (d == null) continue;
                    string name = Json.Str(d, "name", "").Trim();
                    string url = Json.Str(d, "url", "").Trim();
                    if (name.Length == 0 || url.Length == 0) continue;
                    list.Add(new FavoriteLink(name, url));
                }
                return list;
            }
            catch (Exception ex)
            {
                Log.Warn("读取收藏夹失败，使用默认: " + ex.Message);
                return Defaults();
            }
        }

        public static void Save(List<FavoriteLink> items)
        {
            try
            {
                List<object> arr = new List<object>();
                for (int i = 0; i < items.Count; i++)
                {
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["name"] = items[i].Name;
                    d["url"] = items[i].Url;
                    arr.Add(d);
                }
                string json = Json.Stringify(arr);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Warn("保存收藏夹失败: " + ex.Message); }
        }
    }

    /// <summary>圆角矩形路径。</summary>
    internal static class UiPaint
    {
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0)
            {
                path.AddRectangle(r);
                path.CloseFigure();
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color Shift(Color c, float factor)
        {
            int R = Math.Max(0, Math.Min(255, (int)Math.Round(c.R * factor)));
            int G = Math.Max(0, Math.Min(255, (int)Math.Round(c.G * factor)));
            int B = Math.Max(0, Math.Min(255, (int)Math.Round(c.B * factor)));
            return Color.FromArgb(c.A, R, G, B);
        }
    }

    /// <summary>圆角 FlowLayoutPanel（标签栏 / 收藏夹栏）。</summary>
    /// <summary>自绘标签条：单控件直绘 logo + 标签药丸 + 关闭钮，命中测试处理点击。
    /// 不再使用子控件动态重建（FlowLayoutPanel 重排会留下像素残影）。</summary>
    internal class TabStrip : Control
    {
        public Action<int> OnActivate;
        public Action<int> OnClose;
        public ChromePalette P = ChromePalette.Light;
        private Image _logo;
        private List<string> _titles = new List<string>();
        private int _active = -1;
        private readonly List<Rectangle> _pillRects = new List<Rectangle>();
        private int _hoverPill = -1, _hoverClose = -1;

        public TabStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void Reload(Image logo, List<string> titles, int active)
        {
            _logo = logo;
            _titles = titles ?? new List<string>();
            _active = active;
            _hoverPill = -1;
            _hoverClose = -1;
            Invalidate();
            Update();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            _pillRects.Clear();
            int pad = 4, top = 4, h = 30, closeW = 20;
            int x = pad;

            if (_logo != null)
            {
                g.DrawImage(_logo, new Rectangle(x, top, 26, 26));
                x += 26 + 8;
            }

            int[] widths = new int[_titles.Count];
            int total = 0;
            for (int i = 0; i < _titles.Count; i++)
            {
                int w = TextRenderer.MeasureText(_titles[i], Font).Width + 26 + closeW;
                widths[i] = Math.Max(56, Math.Min(180, w));
                total += widths[i] + 6;
            }
            int avail = Math.Max(160, Width - x - pad);
            if (total > avail && widths.Length > 0)
            {
                int over = total - avail;
                for (int i = 0; i < widths.Length; i++)
                {
                    int shrink = (int)Math.Min(widths[i] - 44, Math.Ceiling((double)over * widths[i] / total));
                    widths[i] -= Math.Max(0, shrink);
                }
            }

            for (int i = 0; i < _titles.Count; i++)
            {
                bool on = i == _active;
                Rectangle pill = new Rectangle(x, top, widths[i], h);
                _pillRects.Add(pill);
                Color fill = on ? P.Accent
                    : (i == _hoverPill ? P.TabHover : P.TabInactive);
                using (GraphicsPath path = UiPaint.RoundRect(pill, 9))
                using (SolidBrush b = new SolidBrush(fill))
                    g.FillPath(b, path);

                Rectangle textRect = new Rectangle(pill.X + 8, pill.Y, pill.Width - 16 - closeW, pill.Height);
                TextRenderer.DrawText(g, _titles[i], Font, textRect, on ? P.TabActiveInk : P.Fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                Color ink = on ? P.CloseInkOnActive : P.CloseInk;
                if (i == _hoverClose) ink = on ? P.TabActiveInk : P.CloseInkHover;
                float cx = pill.Right - closeW / 2f - 2, cy = pill.Y + pill.Height / 2f;
                using (Pen pen = new Pen(ink, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, cx - 3.4f, cy - 3.4f, cx + 3.4f, cy + 3.4f);
                    g.DrawLine(pen, cx + 3.4f, cy - 3.4f, cx - 3.4f, cy + 3.4f);
                }
                x += pill.Width + 6;
            }
        }

        private int HitPill(Point p, out bool closeZone)
        {
            closeZone = false;
            for (int i = 0; i < _pillRects.Count; i++)
            {
                if (!_pillRects[i].Contains(p)) continue;
                closeZone = p.X >= _pillRects[i].Right - 20 - 2;
                return i;
            }
            return -1;
        }

        internal bool IsOverPill(Point p) { bool cz; return HitPill(p, out cz) >= 0; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool cz;
            int hit = HitPill(e.Location, out cz);
            if (hit != _hoverPill || (hit >= 0 && cz != (_hoverClose == hit)))
            {
                _hoverPill = hit;
                _hoverClose = cz ? hit : -1;
                Invalidate();
            }
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverPill != -1 || _hoverClose != -1) { _hoverPill = -1; _hoverClose = -1; Invalidate(); }
            Cursor = Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            bool cz;
            int hit = HitPill(e.Location, out cz);
            if (hit < 0) return;
            if (cz) { if (OnClose != null) OnClose(hit); }
            else if (OnActivate != null) OnActivate(hit);
        }
    }

    /// <summary>深浅主题中枢：控制台切换主题时通知所有绿虎浏览器窗口同步换肤。</summary>
    internal static class ChromeTheme
    {
        public static string Current = "light";
        public static event Action<string> Changed;
        public static bool IsDark { get { return Current == "dark"; } }

        public static void Set(string theme)
        {
            string t = theme == "dark" ? "dark" : "light";
            if (Current == t) return;
            Current = t;
            Action<string> h = Changed;
            if (h != null) h(t);
        }
    }

    /// <summary>绿虎浏览器窗口的整套界面配色（亮 = 浅绿，暗 = 柔和暗绿）。</summary>
    internal class ChromePalette
    {
        public bool IsDark;
        public Color Bg, Bar, BarLight, Fg, Accent;
        public Color TabInactive, TabHover, TabActiveInk;
        public Color Sep, Line, AddressBorder;
        public Color CloseInk, CloseInkHover, CloseInkOnActive;

        public static readonly ChromePalette Light = new ChromePalette
        {
            IsDark = false,
            Bg = Color.FromArgb(240, 247, 231),
            Bar = Color.FromArgb(252, 254, 248),
            BarLight = Color.FromArgb(238, 246, 228),
            Fg = Color.FromArgb(28, 42, 26),
            Accent = Color.FromArgb(76, 163, 36),
            TabInactive = Color.FromArgb(228, 240, 213),
            TabHover = Color.FromArgb(214, 232, 196),
            TabActiveInk = Color.White,
            Sep = Color.FromArgb(223, 236, 208),
            Line = Color.FromArgb(205, 226, 192),
            AddressBorder = Color.FromArgb(190, 214, 175),
            CloseInk = Color.FromArgb(112, 133, 95),
            CloseInkHover = Color.FromArgb(40, 60, 34),
            CloseInkOnActive = Color.FromArgb(226, 244, 220)
        };

        public static readonly ChromePalette Dark = new ChromePalette
        {
            IsDark = true,
            Bg = Color.FromArgb(0, 0, 0),
            Bar = Color.FromArgb(10, 10, 10),
            BarLight = Color.FromArgb(17, 17, 17),
            Fg = Color.FromArgb(240, 240, 240),
            Accent = Color.FromArgb(141, 232, 92),
            TabInactive = Color.FromArgb(22, 22, 22),
            TabHover = Color.FromArgb(29, 29, 29),
            TabActiveInk = Color.FromArgb(0, 0, 0),
            Sep = Color.FromArgb(42, 42, 42),
            Line = Color.FromArgb(58, 58, 58),
            AddressBorder = Color.FromArgb(58, 58, 58),
            CloseInk = Color.FromArgb(122, 122, 122),
            CloseInkHover = Color.FromArgb(240, 240, 240),
            CloseInkOnActive = Color.FromArgb(0, 0, 0)
        };
    }

    /// <summary>带描边的圆角容器（换肤时要同步描边色）。</summary>
    internal interface IRoundedBorder { Color BorderColor { get; set; } }

    internal class RoundedFlow : FlowLayoutPanel, IRoundedBorder
    {
        public int Radius = 12;
        public Color BorderColor { get; set; }

        public RoundedFlow()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 先用父容器底色填满整个矩形（清掉圆角外的"角缺"残像），再填圆角路径
            Color outer = Parent != null ? Parent.BackColor : BackColor;
            using (SolidBrush ob = new SolidBrush(outer)) e.Graphics.FillRectangle(ob, ClientRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = UiPaint.RoundRect(r, Radius))
            using (SolidBrush b = new SolidBrush(BackColor))
                e.Graphics.FillPath(b, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = UiPaint.RoundRect(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(BackColor)) e.Graphics.FillPath(b, path);
                if (BorderColor.A > 0)
                {
                    using (Pen pen = new Pen(BorderColor)) e.Graphics.DrawPath(pen, path);
                }
            }
        }
    }

    /// <summary>圆角 TableLayoutPanel（导航栏）。</summary>
    internal class RoundedTable : TableLayoutPanel, IRoundedBorder
    {
        public int Radius = 12;
        public Color BorderColor { get; set; }

        public RoundedTable()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color outer = Parent != null ? Parent.BackColor : BackColor;
            using (SolidBrush ob = new SolidBrush(outer)) e.Graphics.FillRectangle(ob, ClientRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = UiPaint.RoundRect(r, Radius))
            using (SolidBrush b = new SolidBrush(BackColor))
                e.Graphics.FillPath(b, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = UiPaint.RoundRect(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(BackColor)) e.Graphics.FillPath(b, path);
                if (BorderColor.A > 0)
                {
                    using (Pen pen = new Pen(BorderColor)) e.Graphics.DrawPath(pen, path);
                }
            }
        }
    }

    /// <summary>圆角普通面板（地址栏外壳 / 网页视区外壳）。</summary>
    internal class RoundedPanel : Panel, IRoundedBorder
    {
        public int Radius = 12;
        public Color BorderColor { get; set; }

        public RoundedPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color outer = Parent != null ? Parent.BackColor : BackColor;
            using (SolidBrush ob = new SolidBrush(outer)) e.Graphics.FillRectangle(ob, ClientRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = UiPaint.RoundRect(r, Radius))
            using (SolidBrush b = new SolidBrush(BackColor))
                e.Graphics.FillPath(b, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = UiPaint.RoundRect(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(BackColor)) e.Graphics.FillPath(b, path);
                if (BorderColor.A > 0)
                {
                    using (Pen pen = new Pen(BorderColor)) e.Graphics.DrawPath(pen, path);
                }
            }
        }
    }

    /// <summary>绿虎原创按钮：圆角、绿色系、自绘图标，替换系统默认方角按钮。</summary>
    public enum GtIcon { None, Back, Forward, Refresh, Home, Plus, Go, Close }

    public class GreenButton : Button
    {
        public GtIcon GtIcon = GtIcon.None;
        public int Radius = 9;
        private bool _hover;
        private bool _down;

        public GreenButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 先填父容器底色：圆角药丸外的四角不能残留基类方形填充或上一帧的像素
            Color outer = Parent != null ? Parent.BackColor : BackColor;
            using (SolidBrush ob = new SolidBrush(outer)) e.Graphics.FillRectangle(ob, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            // 双缓冲的 buffer 可能带着未初始化内存：第一步先把整个按钮画满父容器底色，
            // 圆角药丸外的角落和边缘永远不会露出杂色（黑角 / 彩色噪点的根源）。
            Color outer = Parent != null ? Parent.BackColor : BackColor;
            g.FillRectangle(new SolidBrush(outer), 0, 0, Width, Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color fill = BackColor;
            Color ink = ForeColor;
            if (_down) fill = UiPaint.Shift(BackColor, 0.82f);
            else if (_hover) fill = UiPaint.Shift(BackColor, 1.18f);
            using (GraphicsPath path = UiPaint.RoundRect(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
            }
            if (GtIcon != GtIcon.None)
            {
                using (Pen pen = new Pen(ink, 2.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    DrawIcon(g, pen);
                }
            }
            else if (Text.Length > 0)
            {
                TextRenderer.DrawText(g, Text, Font, r, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private void DrawIcon(Graphics g, Pen pen)
        {
            float cx = Width / 2f, cy = Height / 2f;
            switch (GtIcon)
            {
                case GtIcon.Back:
                    g.DrawLine(pen, cx + 5, cy, cx - 5, cy);
                    g.DrawLines(pen, new PointF[] { new PointF(cx - 1, cy - 5), new PointF(cx - 5, cy), new PointF(cx - 1, cy + 5) });
                    break;
                case GtIcon.Forward:
                    g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                    g.DrawLines(pen, new PointF[] { new PointF(cx + 1, cy - 5), new PointF(cx + 5, cy), new PointF(cx + 1, cy + 5) });
                    break;
                case GtIcon.Refresh:
                    g.DrawArc(pen, cx - 5, cy - 5, 10, 10, 45, 285);
                    g.DrawLines(pen, new PointF[] { new PointF(cx + 4.8f, cy - 5.4f), new PointF(cx + 6.2f, cy - 3.4f), new PointF(cx + 3.6f, cy - 2.8f) });
                    break;
                case GtIcon.Home:
                    g.DrawLines(pen, new PointF[] { new PointF(cx - 6, cy + 1), new PointF(cx, cy - 5), new PointF(cx + 6, cy + 1) });
                    g.DrawRectangle(pen, cx - 4, cy, 8, 6);
                    break;
                case GtIcon.Plus:
                    g.DrawLine(pen, cx - 4.5f, cy, cx + 4.5f, cy);
                    g.DrawLine(pen, cx, cy - 4.5f, cx, cy + 4.5f);
                    break;
                case GtIcon.Go:
                    g.DrawLine(pen, cx - 4, cy, cx + 4, cy);
                    g.DrawLines(pen, new PointF[] { new PointF(cx, cy - 4), new PointF(cx + 4, cy), new PointF(cx, cy + 4) });
                    break;
                case GtIcon.Close:
                    g.DrawLine(pen, cx - 4, cy - 4, cx + 4, cy + 4);
                    g.DrawLine(pen, cx + 4, cy - 4, cx - 4, cy + 4);
                    break;
            }
        }
    }

    /// <summary>WebView2 薄封装：懒初始化 + 标题/地址同步 + 导航/多标签。</summary>
    public class WebView2Wrapper
    {
        public WebView2 ViewControl;
        public string Url = "";
        private readonly Panel _host;
        private readonly string _initialTarget;
        private readonly bool _initialHtml;
        private readonly BrowserForm.Tab _owner;
        private readonly BrowserForm _form;
        private object _controller;
        private EventInfo _acceleratorEvent;
        private Delegate _acceleratorHandler;
        private bool _initStarted;
        private bool _ready;

        public WebView2Wrapper(Panel host, string target, bool htmlOnly, BrowserForm.Tab owner, BrowserForm form)
        {
            _host = host;
            _initialTarget = target;
            _initialHtml = htmlOnly;
            _owner = owner;
            _form = form;
            try
            {
                ViewControl = new WebView2();
                ViewControl.Visible = false;
                ViewControl.Dock = DockStyle.Fill;
                ViewControl.DefaultBackgroundColor = (_form != null ? _form.P.Bg : Color.FromArgb(240, 247, 231));
                _host.Controls.Add(ViewControl);
                ViewControl.BringToFront();
                InitAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("WebView2 初始化失败: " + ex.Message);
                ViewControl = null;
                FallbackLabel("绿虎浏览器组件不可用：" + ex.Message);
            }
        }

        private void FallbackLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Color.FromArgb(141, 232, 92);
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.Dock = DockStyle.Fill;
            l.Visible = false;
            _host.Controls.Add(l);
            l.BringToFront();
        }

        /// <summary>
        /// 老版 WinForms 封装未公开 CoreWebView2Controller，用反射接上 AcceleratorKeyPressed，
        /// 让网页获得焦点时 Ctrl+L/T/W 等快捷键仍由绿虎浏览器处理。
        /// </summary>
        private void HookControllerAccelerators()
        {
            try
            {
                FieldInfo field = typeof(WebView2).GetField("_coreWebView2Controller", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) return;
                _controller = field.GetValue(ViewControl);
                if (_controller == null) return;
                _acceleratorEvent = _controller.GetType().GetEvent("AcceleratorKeyPressed");
                if (_acceleratorEvent == null) return;
                _acceleratorHandler = Delegate.CreateDelegate(_acceleratorEvent.EventHandlerType, this, "OnAcceleratorKeyPressed", false);
                if (_acceleratorHandler != null) _acceleratorEvent.AddEventHandler(_controller, _acceleratorHandler);
            }
            catch (Exception ex)
            {
                Log.Warn("绿虎浏览器快捷键接管失败: " + ex.Message);
            }
        }

        private void OnAcceleratorKeyPressed(object sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
        {
            try
            {
                if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown &&
                    e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown) return;
                if (_form == null) return;
                Keys mods = Control.ModifierKeys;
                if (_form.HandleAccelerator((int)e.VirtualKey,
                    (mods & Keys.Control) == Keys.Control,
                    (mods & Keys.Alt) == Keys.Alt,
                    (mods & Keys.Shift) == Keys.Shift))
                    e.Handled = true;
            }
            catch { }
        }

        private async void InitAsync()
        {
            if (_initStarted) return;
            _initStarted = true;
            try
            {
                CoreWebView2Environment env = await BrowserWindow.EnsureEnvironmentAsync();
                await ViewControl.EnsureCoreWebView2Async(env);
                CoreWebView2 core = ViewControl.CoreWebView2;
                if (core == null)
                {
                    FallbackLabel("绿虎浏览器初始化失败：WebView2 核心为空。");
                    return;
                }
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.AreDevToolsEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = true;
                // 控制台是应用界面不是网页：禁用页面缩放（Ctrl+滚轮 / pinch）并锁死 100%——
                // WebView2 按来源持久化每个视图的缩放，误触一次整个窗口版面（含主页花园）
                // 就和别的窗口不一致，刷新也不会恢复。系统 DPI 缩放仍正常生效。
                core.Settings.IsZoomControlEnabled = false;
                ViewControl.ZoomFactor = 1.0;
                // 历史遗留的按来源持久化缩放：每次导航完成都强制回 100%
                core.NavigationCompleted += delegate { ViewControl.ZoomFactor = 1.0; };
                HookControllerAccelerators();
                // 敏感输入接管（3.20.0）：页面脚本在密码/支付输入框聚焦变化时 postMessage 上报，
                // 宿主据此拒绝截图这些窗口（用户正在输入的凭据不能经截图进入模型上下文）。
                core.WebMessageReceived += delegate(object wSender, CoreWebView2WebMessageReceivedEventArgs wArgs)
                {
                    try
                    {
                        string msg = wArgs.WebMessageAsJson;
                        if (string.IsNullOrEmpty(msg)) return;
                        IntPtr handle = (_form != null) ? _form.Handle : IntPtr.Zero;
                        if (handle == IntPtr.Zero) return;
                        if (msg.IndexOf("\"gt-sensitive\":true", StringComparison.Ordinal) >= 0) BrowserForm.SetSensitive(handle, true);
                        else if (msg.IndexOf("\"gt-sensitive\":false", StringComparison.Ordinal) >= 0) BrowserForm.SetSensitive(handle, false);
                    }
                    catch { }
                };
                try
                {
                    string sensitiveJs = "(function(){function f(){var a=null;try{a=document.activeElement;}catch(e){}" +
                        "var on=false;try{if(a&&a.tagName==='INPUT'){var t=(a.getAttribute('type')||'').toLowerCase();" +
                        "var ac=(a.getAttribute('autocomplete')||'').toLowerCase();var nm=(a.getAttribute('name')||'')+' '+(a.id||'');" +
                        "if(t==='password'||ac.indexOf('cc-')===0||ac.indexOf('current-password')>=0||ac.indexOf('new-password')>=0||/pass|pwd|card|cvv|cvc/i.test(nm))on=true;}}catch(e){}" +
                        "try{window.chrome.webview.postMessage({'gt-sensitive':on});}catch(e){}}" +
                        "document.addEventListener('focusin',f,true);document.addEventListener('focusout',function(){setTimeout(f,60);},true);f();})();";
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(sensitiveJs);
                }
                catch (Exception jsEx) { try { Log.Warn("敏感输入检测脚本注入失败: " + jsEx.Message); } catch { }
                }
                core.DocumentTitleChanged += delegate
                {
                    if (_owner != null) _owner.Title = core.DocumentTitle;
                    if (_form != null) _form.RefreshTabTitle(_owner);
                };
                core.SourceChanged += delegate
                {
                    Url = core.Source == null ? "" : core.Source;
                    if (_owner != null) _owner.Url = Url;
                    if (_form != null) _form.SyncAddressIfActive(this);
                };
                core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
                {
                    if (e != null && !string.IsNullOrEmpty(e.Uri)) Url = e.Uri;
                    if (_owner != null && !string.IsNullOrEmpty(Url)) _owner.Url = Url;
                    if (_form != null) _form.SyncAddressIfActive(this);
                };
                core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    try
                    {
                        e.Handled = true;
                        if (!string.IsNullOrEmpty(e.Uri) && _form != null)
                            _form.AddTab(e.Uri, true);
                    }
                    catch (Exception ex) { Log.Warn("打开新标签失败: " + ex.Message); }
                };
                core.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e)
                {
                    if (e != null) Log.Warn("绿虎浏览器进程异常: " + e.ProcessFailedKind);
                };
                _ready = true;
                if (_initialHtml)
                    core.NavigateToString(_initialTarget == null ? "" : _initialTarget);
                else if (!string.IsNullOrEmpty(_initialTarget))
                    core.Navigate(_initialTarget);
                if (_form != null) _form.RefreshTabTitle(_owner);
            }
            catch (Exception ex)
            {
                Log.Warn("WebView2 启动失败: " + ex.Message);
                FallbackLabel("绿虎浏览器启动失败：" + ex.Message);
            }
        }

        public void Navigate(string target, bool htmlOnly)
        {
            try
            {
                if (ViewControl == null) return;
                CoreWebView2 core = ViewControl.CoreWebView2;
                if (core == null)
                {
                    if (!_ready) return;
                    return;
                }
                if (htmlOnly) core.NavigateToString(target);
                else if (!string.IsNullOrEmpty(target)) core.Navigate(target);
            }
            catch (Exception ex) { Log.Warn("导航失败: " + ex.Message); }
        }

        public void Reload()
        {
            try { if (ViewControl != null && ViewControl.CoreWebView2 != null) ViewControl.CoreWebView2.Reload(); }
            catch { }
        }

        public void GoBack()
        {
            try
            {
                if (ViewControl != null && ViewControl.CoreWebView2 != null &&
                    ViewControl.CoreWebView2.CanGoBack) ViewControl.CoreWebView2.GoBack();
            }
            catch { }
        }

        public void GoForward()
        {
            try
            {
                if (ViewControl != null && ViewControl.CoreWebView2 != null &&
                    ViewControl.CoreWebView2.CanGoForward) ViewControl.CoreWebView2.GoForward();
            }
            catch { }
        }

        public void OpenDevTools()
        {
            try { if (ViewControl != null && ViewControl.CoreWebView2 != null) ViewControl.CoreWebView2.OpenDevToolsWindow(); }
            catch { }
        }

        public void Focus()
        {
            try { if (ViewControl != null) ViewControl.Focus(); } catch { }
        }

        public void Dispose()
        {
            try
            {
                if (_acceleratorEvent != null && _controller != null && _acceleratorHandler != null)
                    _acceleratorEvent.RemoveEventHandler(_controller, _acceleratorHandler);
            }
            catch { }
            _controller = null;
            _acceleratorEvent = null;
            _acceleratorHandler = null;
            try
            {
                if (ViewControl != null)
                {
                    ViewControl.Dispose();
                    ViewControl = null;
                }
            }
            catch { }
        }
    }
#endif
}
