/* ============================================================
 * 绿虎（GreenTiger）统一页面外壳运行时
 * ------------------------------------------------------------
 * 职责只有三件事，避免每个页面各写一套：
 * 1. 主题：唯一存储键 greentiger.theme
 * 2. 主题切换按钮：自动挂载到任意带 [data-gt-theme-toggle] 的按钮
 * 3. 跨页面主题同步：监听 storage 事件，其它标签页切换时同步
 *
 * 对外 API（window.GreenTigerShell）：
 * getTheme() / setTheme(t) / toggleTheme() / applyTheme()
 * THEME_KEY / mountToggles(root)
 * 主题变更时在 document 上派发 CustomEvent('greentiger:theme',{detail:{theme}})
 *
 * 历史沿革：以前这里还挂着一套 .gt-* 外壳样式（ui/shell.css）和三个写死的 id 选择器，
 * 那些是给早已删掉的独立页面准备的，主控制台从来没用过，现在全部移除。
 * ============================================================ */
(function () {
 "use strict";

 var doc = document;

 /* 统一键：唯一主题存储键 */
 var THEME_KEY = "greentiger.theme";

 var VALID = { light: 1, dark: 1 };

 function safeGet(key) {
 try { return localStorage.getItem(key); } catch (e) { return null; }
 }

 function safeSet(key, value) {
 try { localStorage.setItem(key, value); return true; } catch (e) { return false; }
 }

 function normalize(value) {
 return VALID[value] ? value : null;
 }

 /* 读取当前主题：统一键 → 跟随系统偏好 */
 function getTheme() {
 var current = normalize(safeGet(THEME_KEY));
 if (current) return current;

 var prefersDark = false;
 try {
 prefersDark = !!(window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches);
 } catch (e) { }
 return prefersDark ? "dark" : "light";
 }

 /* 把主题写到 <html data-theme>，并同步 theme-color / color-scheme */
 function applyTheme(theme) {
 var next = normalize(theme) || getTheme();
 doc.documentElement.dataset.theme = next;

 var scheme = doc.querySelector('meta[name="color-scheme"]');
 if (scheme) scheme.setAttribute("content", next === "dark" ? "dark" : "light");

 var bar = doc.querySelector('meta[name="theme-color"]');
 /* 与 ui/theme.css 的 --bg 保持一致：深色炭灰、浅色中性，不再用品牌绿刷系统栏 */
 if (bar) bar.setAttribute("content", next === "dark" ? "#1e1e1e" : "#f7f9f4");

 paintToggles(next);
 return next;
 }

 function setTheme(theme) {
 var next = normalize(theme) || "light";
 safeSet(THEME_KEY, next);
 applyTheme(next);
 try {
 doc.dispatchEvent(new CustomEvent("greentiger:theme", { detail: { theme: next } }));
 } catch (e) { }
 return next;
 }

 function toggleTheme() {
 return setTheme(getTheme() === "dark" ? "light" : "dark");
 }

 /* 按钮文案与状态：所有页面的切换按钮共用同一套规则 */
 function paintToggle(btn, theme) {
 if (!btn) return;
 btn.dataset.themeState = theme;
 btn.setAttribute("aria-pressed", theme === "dark" ? "true" : "false");
 btn.setAttribute("aria-label", theme === "dark" ? "当前深色，切换到浅色" : "当前浅色，切换到深色");
 btn.textContent = theme === "dark" ? "深色 · 切换浅色" : "浅色 · 切换深色";
 }

function toggleButtons(root) {
 var scope = root || doc;
 return scope.querySelectorAll("[data-gt-theme-toggle]");
}

 function paintToggles(theme) {
 var list = toggleButtons();
 for (var i = 0; i < list.length; i++) paintToggle(list[i], theme);
 }

 /* 给页面里所有切换按钮挂事件（幂等，重复调用不会重复绑定） */
 function mountToggles(root) {
 var list = toggleButtons(root);
 for (var i = 0; i < list.length; i++) {
 var btn = list[i];
  if (btn.dataset.gtThemeBound === "1") { paintToggle(btn, getTheme()); continue; }
  btn.dataset.gtThemeBound = "1";
  btn.addEventListener("click", function (e) {
 e.preventDefault();
 toggleTheme();
 });
 paintToggle(btn, getTheme());
 }
 return list.length;
 }

 /* 其它标签页切换主题时同步本页 */
 window.addEventListener("storage", function (e) {
 if (e.key !== THEME_KEY) return;
 var next = normalize(e.newValue);
 if (next) applyTheme(next);
 });

 function boot() {
 applyTheme(getTheme());
 mountToggles();
 /* 标记外壳已就绪，供样式与测试使用 */
 doc.documentElement.dataset.gtShell = "1";
 }

 if (doc.readyState === "loading") {
 doc.addEventListener("DOMContentLoaded", boot);
 } else {
 boot();
 }

 /* 尽早执行一次，避免深色页面闪白 */
 applyTheme(getTheme());

 window.GreenTigerShell = {
 THEME_KEY: THEME_KEY,
 getTheme: getTheme,
 setTheme: setTheme,
 toggleTheme: toggleTheme,
 applyTheme: applyTheme,
 mountToggles: mountToggles
 };
})();
