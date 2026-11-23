using System;
using System.Text.RegularExpressions;

namespace GreenTiger
{
    /// <summary>
    /// 模型输出清洗器（稳定性中间件·模块一）：
    /// ① 剥离底层对话协议的特殊控制标记（GPT-OSS harmony 的 &lt;|channel|&gt;/&lt;|message|&gt;/&lt;|return|&gt;、
    ///    ChatML 的 &lt;|im_start|&gt;/&lt;|im_end|&gt; 等）——漏进正文会污染展示与协议解析，
    ///    漏进工具名会变成「不存在的工具」（实测案例：tool name "shell&lt;|channel|&gt;commentary"）；
    /// ② 剥离 &lt;thinking&gt; 思考块——推理静默，UI 与工具协议不消费模型内心独白；
    /// ③ 工具名清洗：取第一个控制标记之前的部分（"shell&lt;|channel|&gt;commentary" → "shell"）。
    /// 采用泛化 &lt;|...|&gt; 匹配（≤64 字符、不含换行）：底层协议标记种类随模型演进，逐个枚举追不上；
    /// 正文里讨论这类语法的误伤概率极低、后果只是少几个字符，两害相权取其轻。
    /// </summary>
    public static class Sanitizer
    {
        private static readonly Regex ControlMarker = new Regex("<\\|[^<>\\r\\n]{0,64}\\|>", RegexOptions.Compiled);
        private static readonly Regex ThinkingBlock = new Regex("<thinking>[\\s\\S]*?</thinking>\\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ThinkingTail = new Regex("<thinking>[\\s\\S]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>只剥底层控制标记（不碰 &lt;thinking&gt;）：用于工具参数等只需去协议标记的场合。</summary>
        public static string CleanControlMarkers(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            return ControlMarker.Replace(text, "");
        }

        /// <summary>模型正文清洗：控制标记 + 思考块（含只开了头没闭合的尾段）。</summary>
        public static string CleanModelOutput(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string t = ControlMarker.Replace(text, "");
            t = ThinkingBlock.Replace(t, "");
            t = ThinkingTail.Replace(t, "");
            return t;
        }

        /// <summary>工具名清洗：控制标记之前的才是真名（"shell&lt;|channel|&gt;commentary" → "shell"）。</summary>
        public static string SanitizeToolName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            int cut = name.IndexOf("<|", StringComparison.Ordinal);
            if (cut > 0) name = name.Substring(0, cut);
            return ControlMarker.Replace(name, "").Trim();
        }
    }
}
