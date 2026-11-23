using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GreenTiger
{
    internal static class SelfKnowledge
    {
        private static readonly Lazy<string> ToolboxText = new Lazy<string>(ReadToolbox);

        internal static string Toolbox()
        {
            return ToolboxText.Value;
        }

        private static string ReadToolbox()
        {
            using (Stream stream = typeof(SelfKnowledge).Assembly.GetManifestResourceStream("GreenTiger.ui.index.html"))
            {
                if (stream == null) return "当前程序缺少内嵌工具箱页面。";
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string html = reader.ReadToEnd();
                    MatchCollection cards = Regex.Matches(html,
                        @"<button\b[^>]*?\sdata-tb\s*=\s*[""'](?<id>[^""']+)[""'][^>]*>(?<body>.*?)</button\s*>",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (cards.Count == 0) return "当前程序的内嵌页面中未识别到工具箱入口。";
                    StringBuilder result = new StringBuilder("工具箱（当前版本界面）");
                    foreach (Match card in cards)
                    {
                        string body = card.Groups["body"].Value;
                        string key = card.Groups["id"].Value;
                        string title = CardText(body, "tb-card-title");
                        result.AppendLine().Append("- ").Append(title.Length == 0 ? key : title);
                        result.Append("　").Append(CardText(body, "tb-card-desc"));
                        switch (key)
                        {
                            case "search":
                                AppendTools(result, "file_search");
                                result.Append("。搜索依赖本机索引服务，界面可定位文件。");
                                break;
                            case "scan":
                                AppendTools(result, "pc_scan", "pc_clean_cache");
                                result.Append("。可查看磁盘、缓存、占用空间的文件和自启项，清理仅面向允许的缓存。");
                                break;
                            case "uninstall":
                                AppendTools(result, "pc_uninstall_list", "pc_uninstall_launch");
                                result.Append("。执行时启动该软件的标准卸载程序。");
                                break;
                            case "shred":
                                AppendTools(result, "pc_shred");
                                result.Append("。界面支持先预览，实际粉碎需要确认。");
                                break;
                        }
                    }
                    return result.ToString();
                }
            }
        }

        private static string CardText(string body, string className)
        {
            Match match = Regex.Match(body, @"<span\b[^>]*?\sclass\s*=\s*[""'][^""']*?\b"
                + Regex.Escape(className) + @"\b[^""']*[""'][^>]*>(.*?)</span\s*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return WebUtility.HtmlDecode(Regex.Replace(match.Groups[1].Value, "<[^>]+>", "")).Trim();
        }

        private static void AppendTools(StringBuilder result, params string[] names)
        {
            List<string> registered = new List<string>();
            foreach (string name in names)
                if (ToolRegistry.IsRegistered(name)) registered.Add(name);
            if (registered.Count > 0) result.Append("。对应工具 ").Append(string.Join("、", registered.ToArray()));
        }

        internal static string Describe(McpHost mcp)
        {
            StringBuilder result = new StringBuilder();
            result.AppendLine(AppInfo.Name + " " + AppInfo.Version);
            result.AppendLine(Toolbox());
            result.AppendLine();
            result.AppendLine("内置 Agent 工具（注册项及默认风险，具体操作受当前模式和审批规则约束）");
            foreach (ToolSpec tool in ToolRegistry.All())
                result.AppendLine("- " + tool.Name + " [" + tool.RiskLevel + "] " + tool.Summary);

            List<string> names = new List<string>();
            if (mcp != null)
                foreach (McpTool tool in mcp.AllTools()) names.Add(tool.FullName);
            names.Sort(StringComparer.Ordinal);
            result.AppendLine();
            result.AppendLine("已发现的 MCP 工具：" + (names.Count == 0 ? "无" : string.Join("、", names.ToArray())));
            result.AppendLine("本地维护命令");
            result.AppendLine("- /capabilities 查看当前功能和工具");
            result.AppendLine("- /doctor 检查绿虎自身运行状态");
            result.AppendLine("- /repair [故障描述] 让绿虎诊断并修复自身故障");
            result.AppendLine("- /reflect 根据当前会话的实际执行记录回顾结果并判断下一步");
            result.Append("- /design 查看绿虎的界面设计取向与检查依据");
            return result.ToString();
        }
    }
}
