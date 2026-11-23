using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GreenTiger
{
    /// <summary>一个用户技能包（Skills，v3.6 蒸馏注入）。</summary>
    public class SkillPack
    {
        public string Name { get; set; }        /// <summary>斜杠调用名（/名称）。</summary>
        public string Description { get; set; } /// <summary>一句话描述（进系统提示清单）。</summary>
        public string Body { get; set; }        /// <summary>技能正文（调用时作为任务提示注入）。</summary>
        public string File { get; set; }

        public SkillPack()
        {
            Name = "";
            Description = "";
            Body = "";
            File = "";
        }
    }

    /// <summary>
    /// 用户技能包：工作目录 <c>.greentiger\skills\*.md</c>，frontmatter 声明 name/description：
    /// <code>
    /// ---
    /// name: release
    /// description: 按仓库规范走一遍发布流程
    /// ---
    /// 技能正文：给模型的完整指令……
    /// </code>
    /// 每次任务开始重新扫描（改完即生效）；系统提示注入名称+描述清单；
    /// 用户输入 /name 调用时，技能正文作为任务提示（可附加用户参数）。
    /// </summary>
    public static class AgentSkills
    {
        public static string SkillsDir(string workDir)
        {
            if (string.IsNullOrEmpty(workDir)) return "";
            return Path.Combine(workDir, ".greentiger", "skills");
        }

        /// <summary>扫描技能目录。读失败的单个文件跳过；name 缺省用文件名。</summary>
        public static List<SkillPack> Scan(string workDir)
        {
            List<SkillPack> skills = new List<SkillPack>();
            string dir = SkillsDir(workDir);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return skills;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.md"); } catch { return skills; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    string text = File.ReadAllText(files[i], Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    SkillPack skill = Parse(files[i], text);
                    if (skill != null && skill.Name.Length > 0) skills.Add(skill);
                }
                catch { /* 单个技能坏了不影响其它 */ }
            }
            return skills;
        }

        /// <summary>解析 frontmatter（--- 包围的 name/description）+ 正文。</summary>
        public static SkillPack Parse(string file, string text)
        {
            SkillPack skill = new SkillPack();
            skill.File = file;
            skill.Name = Path.GetFileNameWithoutExtension(file);
            string body = text == null ? "" : text.Trim();
            if (body.StartsWith("---", StringComparison.Ordinal))
            {
                int end = body.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (end > 0)
                {
                    string fm = body.Substring(3, end - 3);
                    for (int i = 0; i < fm.Split('\n').Length; i++)
                    {
                        string line = fm.Split('\n')[i].Trim();
                        int colon = line.IndexOf(':');
                        if (colon <= 0) continue;
                        string key = line.Substring(0, colon).Trim().ToLowerInvariant();
                        string val = line.Substring(colon + 1).Trim();
                        if (key == "name" && val.Length > 0) skill.Name = val;
                        else if (key == "description" && val.Length > 0) skill.Description = val;
                    }
                    body = body.Substring(end + 4).Trim();
                }
            }
            skill.Body = body;
            skill.Description = skill.Description.Length > 0 ? skill.Description : TextUtil.OneLine(body, 80);
            return skill;
        }

        /// <summary>按名查找（大小写不敏感）。</summary>
        public static SkillPack Find(string workDir, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            List<SkillPack> skills = Scan(workDir);
            for (int i = 0; i < skills.Count; i++)
            {
                if (string.Equals(skills[i].Name, name, StringComparison.OrdinalIgnoreCase)) return skills[i];
            }
            return null;
        }

        /// <summary>系统提示里的技能清单（无技能返回 ""，零上下文开销）。</summary>
        public static string PromptSection(string workDir)
        {
            List<SkillPack> skills = Scan(workDir);
            if (skills.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            sb.Append("## 用户技能包（输入 /名称 调用，正文会作为任务指令注入）\n");
            for (int i = 0; i < skills.Count && i < 20; i++)
            {
                sb.Append("- /").Append(skills[i].Name)
                  .Append(" — ").Append(TextUtil.OneLine(skills[i].Description, 100)).Append('\n');
            }
            if (skills.Count > 20)
                sb.Append("（菜单只列前 20 个，另有 ").Append(skills.Count - 20)
                  .Append(" 个未列出；/技能名 调用不受此限制）\n");
            return sb.ToString();
        }
    }
}
