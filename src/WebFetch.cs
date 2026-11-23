using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GreenTiger
{
    /// <summary>
    /// web_fetch 的正文提取与短期缓存。
    ///
    /// 为什么要它：http_get 把整页 HTML 原样丢给模型，一个普通资讯页 200KB 里
    /// 真正有用的正文往往不到 10KB——剩下全是脚本、样式、导航和埋点。
    /// 这里把 HTML 降维成可读文本，token 直接省一个数量级；
    /// 15 分钟缓存让「同一轮任务反复读同一页」不再重复打网络。
    /// </summary>
    public static class WebFetchCache
    {
        private sealed class Entry
        {
            public string Text;
            public long At;
        }

        private const int TtlMs = 15 * 60 * 1000;
        private const int MaxEntries = 60;

        private static readonly Dictionary<string, Entry> Map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly object Gate = new object();
        private static readonly Regex DropBlocks = new Regex(
            @"<(script|style|noscript|template|svg|iframe)\b[^>]*>[\s\S]*?</\1\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Comments = new Regex(@"<!--[\s\S]*?-->", RegexOptions.Compiled);
        private static readonly Regex LineBreaks = new Regex(
            @"<\s*(br|/p|/div|/li|/tr|/h[1-6]|/section|/article|/header|/footer|/blockquote)\b[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Bullets = new Regex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Tags = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex Entity = new Regex(@"&(#x?[0-9a-fA-F]+|[a-zA-Z]+);", RegexOptions.Compiled);
        private static readonly Regex ManyNewlines = new Regex(@"[ \t]+\n", RegexOptions.Compiled);
        private static readonly Regex BlankRuns = new Regex(@"\n{3,}", RegexOptions.Compiled);

        public static bool TryGet(string url, out string text)
        {
            text = null;
            if (string.IsNullOrEmpty(url)) return false;
            lock (Gate)
            {
                Entry e;
                if (!Map.TryGetValue(url, out e) || e == null) return false;
                if (Message.Now() - e.At > TtlMs) { Map.Remove(url); return false; }
                text = e.Text;
                return true;
            }
        }

        public static void Put(string url, string text)
        {
            if (string.IsNullOrEmpty(url) || text == null) return;
            lock (Gate)
            {
                if (Map.Count >= MaxEntries)
                {
                    // 简单淘汰：先清过期，还超就整体清空（缓存只是加速，丢了不影响正确性）
                    long now = Message.Now();
                    List<string> stale = new List<string>();
                    foreach (KeyValuePair<string, Entry> kv in Map)
                        if (now - kv.Value.At > TtlMs) stale.Add(kv.Key);
                    for (int i = 0; i < stale.Count; i++) Map.Remove(stale[i]);
                    if (Map.Count >= MaxEntries) Map.Clear();
                }
                Entry e = new Entry();
                e.Text = text;
                e.At = Message.Now();
                Map[url] = e;
            }
        }

        /// <summary>HTML → 可读文本。不像 HTML 的内容（JSON / 纯文本 / Markdown）原样返回。</summary>
        public static string ToReadable(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            string text = content;
            if (text.IndexOf('<') < 0) return Normalize(text);

            text = Comments.Replace(text, "");
            text = DropBlocks.Replace(text, "");
            text = LineBreaks.Replace(text, "\n");
            text = Bullets.Replace(text, "\n· ");
            text = Tags.Replace(text, "");
            text = DecodeEntities(text);
            return Normalize(text);
        }

        private static string DecodeEntities(string text)
        {
            return Entity.Replace(text, delegate(Match m)
            {
                string body = m.Groups[1].Value;
                if (body.Length > 1 && (body[0] == '#'))
                {
                    bool hex = body.Length > 2 && (body[1] == 'x' || body[1] == 'X');
                    string digits = hex ? body.Substring(2) : body.Substring(1);
                    int code;
                    if (int.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out code)
                        && code > 0 && code <= 0x10FFFF)
                    {
                        try { return char.ConvertFromUtf32(code); } catch { return m.Value; }
                    }
                    return m.Value;
                }
                switch (body.ToLowerInvariant())
                {
                    case "amp": return "&";
                    case "lt": return "<";
                    case "gt": return ">";
                    case "quot": return "\"";
                    case "apos": return "'";
                    case "nbsp": return " ";
                    case "mdash": return "—";
                    case "ndash": return "–";
                    case "hellip": return "…";
                    case "ldquo": return "“";
                    case "rdquo": return "”";
                    case "lsquo": return "‘";
                    case "rsquo": return "’";
                    case "middot": return "·";
                    default: return m.Value;
                }
            });
        }

        private static string Normalize(string text)
        {
            StringBuilder sb = new StringBuilder(text.Length);
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = ManyNewlines.Replace(lines[i], "").TrimEnd();
                sb.AppendLine(line);
            }
            string result = BlankRuns.Replace(sb.ToString(), "\n\n");
            return result.Trim();
        }
    }
}
