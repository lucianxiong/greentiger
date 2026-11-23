using System;
using System.IO;
using System.Text.RegularExpressions;

namespace GreenTiger
{
    internal static class SelfMaintenance
    {
        internal const string Design = "我的界面取向是克制、清晰的专业工作台。任务、进度和下一步操作优先；同类控件用相同的颜色、字号和间距。文字要读得清，小窗口也要够得着主要操作，审批和错误必须容易辨认。正式版显示随程序发布的界面，--dev 可以预览本地改稿。修改界面后，用浏览器检查桌面与窄屏的聊天和工具箱，再看实际截图决定是否采用。";

        internal static string Command(string input)
        {
            string text = (input ?? "").Trim();
            if (text.StartsWith("/")) return text;
            if (text.Length > 160) return "";
            text = text.TrimEnd('？', '?', '。', '！', '!').Trim();
            if (Regex.IsMatch(text, @"^(?:请问|请|告诉我)?(?:绿虎|你的|你)?(?:的)?工具箱(?:里|里面|中)?(?:都有|有|包含|包括)(?:些什么|什么|哪些)(?:东西|工具|内容|功能)?$"))
                return "/toolbox";
            if (Regex.IsMatch(text, @"^(?:绿虎|你)(?:自己)?(?:都会什么|能做什么|会做什么|有哪些功能|有什么功能|有哪些工具|有什么工具)$"))
                return "/capabilities";
            if (Regex.IsMatch(text, @"^(?:绿虎|你)?(?:刚才|刚刚|上次|最近)(?:都)?(?:做了什么|做过什么|干了什么|做了哪些事)$"))
                return "/reflect";
            if (Regex.IsMatch(text, @"^(?:绿虎|你)(?:的)?(?:审美是什么|喜欢什么界面风格|如何判断界面好不好看)$"))
                return "/design";
            if (Regex.IsMatch(text, @"^(?:请)?(?:绿虎自检|检查绿虎自身状态|检查你自己的运行状态)$"))
                return "/doctor";
            if (Regex.IsMatch(text, @"^(?:请)?(?:绿虎自修复|修复绿虎的界面资源|修复你自己的界面资源)$"))
                return "/repair";
            Match repair = Regex.Match(text, @"^(?:请)?(?:绿虎自己修复|修复绿虎自身的|修复你自己的|修一下你自己的)(?:故障|问题|[bB][uU][gG])?[：:，, ]+(.+)$");
            return repair.Success ? "/repair " + repair.Groups[1].Value : "";
        }

        internal static bool IsSourceRepair(string input)
        {
            return (input ?? "").TrimStart().StartsWith("/repair ", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsSourceTree(string dir)
        {
            return !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "src", "Agent.cs"))
                && File.Exists(Path.Combine(dir, "src", "Guard.cs")) && File.Exists(Path.Combine(dir, "tools", "project-manifest.js"))
                && File.Exists(Path.Combine(dir, "build.ps1"));
        }

        internal static string Diagnostics(Config config)
        {
            Uri endpoint;
            bool configured = Uri.TryCreate(config.ProviderEndpoint, UriKind.Absolute, out endpoint);
            string model = configured ? (endpoint.IsLoopback ? "已设置本机模型端点" : "已设置网络模型端点") : "未设置主模型端点";
            return AppInfo.Name + " " + AppInfo.Version + "。模型配置：" + model + "（未发网络请求）。\n"
                + "工作目录：" + (IsSourceTree(config.WorkDir) ? "已识别绿虎源码目录。" : "当前未选中绿虎源码目录。")
                + "\n" + App.CheckUiAssets(false);
        }

        internal const string SourceGuide = "本次是绿虎自身的源码修复。你作为主 Agent 亲自处理。先用当前源码、已有测试和复现结果定位故障，用 edit/multiedit 做最小修复。用 build.ps1 的 -Out dist\\GreenTiger.candidate.exe 构建候选版本，并运行候选程序的 --self-test；涉及界面时对同一候选运行 node tools/ui-e2e.js --candidate。再以原症状验证，区分已修源码、候选版和正在运行的版本。直接调用明确的构建或测试命令，每条单独执行。缺证据或不能复现就如实报告，保留用户的现有改动。不要改写验证脚本来制造通过。";
    }
}
