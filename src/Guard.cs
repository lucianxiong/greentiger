using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GreenTiger
{
    public class GuardVerdict
    {
        public bool Blocked { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }

        public GuardVerdict()
        {
            Blocked = false;
            Code = "";
            Message = "";
        }

        public static GuardVerdict Ok()
        {
            return new GuardVerdict();
        }

        public static GuardVerdict Block(string code, string message)
        {
            GuardVerdict v = new GuardVerdict();
            v.Blocked = true;
            v.Code = code;
            v.Message = message;
            return v;
        }
    }

    /// <summary>
    /// 执行前的最后一道闸门：拦住死循环与灾难性命令；执行后检查回答里有没有凭空编造的路径。
    /// 它只做「拦截 + 提示」，不改写模型输出，也不替代既有的风险分级与人工审批。
    /// </summary>
    public class Guard
    {
        /// <summary>同一工具 + 同一参数的重复判定窗口。</summary>
        public const int LoopWindowMs = 60000;

        /// <summary>窗口内重复到这个次数就认为卡住了。</summary>
        public const int LoopThreshold = 3;

        // 注意：行尾锚点对「解析后的命令本体」才有意义（见 CheckCall 里的 ExtractCommand），
        // 直接对 ArgsJson 匹配会因结尾的 \"} 永远不命中——rd/del/remove-item 三类曾经全部失效。
        // 目标形态不再要求命令结尾（\s*$）：Remove-Item C:\ ; echo done 这类「灭盘后接其他命令」
        // 也必须拦住，目标后允许跟分号/管道/行尾。
        private static readonly Regex Fatal = new Regex(
            @"(format-volume|diskpart|bcdedit|vssadmin\s+delete|wevtutil\s+cl|" +
            // 盘符根后允许 * / *.*：del /f /q C:\*.* 与 del /f /q C:\ 是同一类灭盘动作。
            @"remove-item\s+[^|;&]{0,120}[""']?[a-z]:[\\/]{0,2}(\*\.\*|\*)?[""']?\s*($|[;|&#])|" +
            @"del\s+/[fsq]\b[^|;&]{0,120}[""']?[a-z]:[\\/]{0,2}(\*\.\*|\*)?[""']?\s*($|[;|&#])|" +
            @"(rd|rmdir)\s+/s\b[^|;&]{0,120}[""']?[a-z]:[\\/]{0,2}(\*\.\*|\*)?[""']?\s*($|[;|&#])|" +
            @"cipher\s+/w|shutdown\s+[-/][rs]|stop-computer|" +
            // 编码执行无法静态审查，一律硬拦；带引号的 HKLM 注册表删除同样不能放过。
            @"powershell\s+[^\r\n]{0,120}-enc(odedcommand)?\b|" +
            @"(frombase64string|encodedcommand)[^\r\n]{0,160}invoke-expression|" +
            @"reg\s+delete\s+[""']?hklm|takeown\s+/f\s+c:\\windows)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 剥掉 PowerShell 反引号转义与零宽字符后再做高危匹配：
        /// Remove-Ite`m C:\ 在 PowerShell 里等价于 Remove-Item C:\，但对关键词正则完全隐身。
        /// 检测方向宁可误拦（走人工审批），不可漏放。
        /// </summary>
        private static string StripObfuscation(string command)
        {
            if (string.IsNullOrEmpty(command)) return command;
            StringBuilder sb = new StringBuilder(command.Length);
            for (int i = 0; i < command.Length; i++)
            {
                char c = command[i];
                if (c == '`') continue;   // PowerShell 转义符：剥掉后再匹配
                if (c == '\u200b' || c == '\u200c' || c == '\u200d' || c == '\ufeff') continue; // 零宽字符
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static readonly Regex WinPath = new Regex(
            @"[A-Za-z]:\\[^\s""'<>|，。；、）(]*",
            RegexOptions.Compiled);

        private static readonly Regex FileName = new Regex(
            @"[A-Za-z0-9_\-\.]+\.(exe|dll|txt|log|json|md|png|jpg|jpeg|gif|cs|ps1|bat|cmd|zip|7z|rar|pdf|docx|xlsx|pptx|msi|sys|ini|cfg|yaml|yml)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly object _gate = new object();
        private readonly Dictionary<string, List<long>> _recent = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>每轮对话开始时清空重复计数，避免上一轮的记录误伤。</summary>
        public void Reset()
        {
            lock (_gate) { _recent.Clear(); }
        }

        /// <summary>执行前检查：灾难性命令直接拦，重复调用则提醒。</summary>
        public GuardVerdict CheckCall(string tool, string argsJson)
        {
            if (string.IsNullOrEmpty(tool)) return GuardVerdict.Ok();
            string args = argsJson == null ? "" : argsJson;

            if (string.Equals(tool, "shell", StringComparison.OrdinalIgnoreCase))
            {
                // ArgsJson 是 JSON（{"command":"..."}），高危正则必须对解析出的命令本体匹配；
                // 同时兜底匹配原始参数，兼容非 JSON 的直接调用。
                // 匹配前先剥反引号/零宽字符：Remove-Ite`m 这类转义写法对正则隐身但对 PowerShell 等价。
                string command = StripObfuscation(ExtractCommand(args));
                string argsObfuscated = StripObfuscation(args);
                if (Fatal.IsMatch(command) || Fatal.IsMatch(argsObfuscated))
                {
                    return GuardVerdict.Block("FATAL_COMMAND",
                        "这条命令可能造成不可逆的系统损坏，已拦截：" + TextUtil.OneLine(command.Length > 0 ? command : args, 120));
                }
            }

            long now = Message.Now();
            string key = tool.ToLowerInvariant() + "|" + Normalize(args);
            int count = 0;
            lock (_gate)
            {
                List<long> stamps;
                if (!_recent.TryGetValue(key, out stamps))
                {
                    stamps = new List<long>();
                    _recent[key] = stamps;
                }
                for (int i = stamps.Count - 1; i >= 0; i--)
                {
                    if (now - stamps[i] > LoopWindowMs) stamps.RemoveAt(i);
                }
                stamps.Add(now);
                count = stamps.Count;
            }

            if (count >= LoopThreshold)
            {
                return GuardVerdict.Block("LOOP_DETECTED",
                    "最近 " + (LoopWindowMs / 1000) + " 秒内已经用完全相同的参数调用了 " + count + " 次「" + tool + "」，判断为原地打转。换个思路，或先把已有结果告诉用户。");
            }
            return GuardVerdict.Ok();
        }

        /// <summary>从工具参数 JSON 里取出 shell 命令本体；解析失败返回空串（调用方回退匹配原始参数）。</summary>
        private static string ExtractCommand(string argsJson)
        {
            if (string.IsNullOrEmpty(argsJson)) return "";
            try
            {
                Dictionary<string, object> obj = Json.ParseObject(argsJson);
                if (obj != null)
                {
                    string command = Json.Str(obj, "command");
                    if (!string.IsNullOrEmpty(command)) return command;
                }
            }
            catch (Exception ex)
            {
                // 护栏是最后一道防线：解析失败必须留痕，否则「危险命令没被拦」和
                // 「参数不是 JSON 所以没解析」在日志里长得一模一样，事后无从区分。
                Log.Warn("护栏提取命令失败（将按原参数匹配）: " + Log.Describe(ex));
            }
            return "";
        }

        /// <summary>
        /// 循环检测的语义归一：剥反引号与零宽字符、剥 # 注释尾巴、统一引号、折叠空白。
        /// 否则同义命令加个 `# 1` 注释或换种引号就生成新 key，60 秒循环拦截形同虚设。
        /// </summary>
        private static string Normalize(string args)
        {
            if (string.IsNullOrEmpty(args)) return "";
            string s = StripObfuscation(args);
            StringBuilder sb = new StringBuilder(s.Length);
            bool space = false;
            bool inQuote = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') { inQuote = !inQuote; c = '\''; }   // 统一引号风格
                if (!inQuote && c == '#' && (i == 0 || char.IsWhiteSpace(s[i - 1]))) break;  // 注释尾巴截断
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 执行后检查：回答里提到的路径/文件名，必须在工具输出里出现过或真实存在，
        /// 否则视为可能的幻觉，返回给用户提示（不拦截，只提示）。
        /// </summary>
        public List<string> CheckAnswer(string answer, IList<string> outputs)
        {
            List<string> warnings = new List<string>();
            if (string.IsNullOrEmpty(answer)) return warnings;

            StringBuilder all = new StringBuilder();
            if (outputs != null)
            {
                for (int i = 0; i < outputs.Count; i++)
                {
                    if (outputs[i] != null) all.Append(outputs[i]).Append('\n');
                }
            }
            string evidence = all.ToString();
            if (evidence.Length == 0) return warnings;   // 没有工具输出就无从比对

            List<string> seen = new List<string>();

            MatchCollection paths = WinPath.Matches(answer);
            for (int i = 0; i < paths.Count && warnings.Count < 4; i++)
            {
                string p = paths[i].Value.Trim().TrimEnd('.', ',', ';', ':');
                if (p.Length < 4) continue;
                if (seen.Contains(p.ToLowerInvariant())) continue;
                seen.Add(p.ToLowerInvariant());
                if (evidence.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (Exists(p)) continue;
                warnings.Add("回答里提到的路径在工具输出中找不到依据，也未在磁盘上存在：" + p);
            }

            // 只有回答声称「已经生成/保存/删除」了某个文件时，才去核对文件名。
            // 否则模型只是在建议一个未来要创建的文件名，属于正常表达。
            if (ClaimsFileAction(answer))
            {
                MatchCollection files = FileName.Matches(answer);
                for (int i = 0; i < files.Count && warnings.Count < 6; i++)
                {
                    string f = files[i].Value;
                    if (f.Length < 5) continue;
                    if (seen.Contains(f.ToLowerInvariant())) continue;
                    seen.Add(f.ToLowerInvariant());
                    if (evidence.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    warnings.Add("回答声称已处理 " + f + "，但工具输出里没有它的痕迹");
                }
            }

            return warnings;
        }

        private static readonly string[] ClaimWords = new string[]
        {
            "已保存", "已生成", "已创建", "已写入", "已删除", "已移动", "已复制",
            "保存在", "保存到", "输出到", "写入到", "生成在", "位于"
        };

        private static bool ClaimsFileAction(string answer)
        {
            for (int i = 0; i < ClaimWords.Length; i++)
            {
                if (answer.IndexOf(ClaimWords[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static bool Exists(string path)
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path)) return true;
                return false;
            }
            catch { return false; }
        }
    }
}
