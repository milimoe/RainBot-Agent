using System.Text.RegularExpressions;

namespace RainBot.Services.Persona;

public sealed record LanguageStyle(string Name, string FileName);

/// <summary>用户导入的语言风格文件库，不提供内置风格。</summary>
public sealed class LanguageStyleCatalog(string? directoryPath = null)
{
    public const int MaxSkillChars = 30000;
    private readonly Lock _lock = new();
    public string DirectoryPath { get; } = Path.GetFullPath(directoryPath ?? Path.Combine(AppContext.BaseDirectory, "skills", "styles"));
    public const string PersonaTemplate = """
        # 固定人设：群聊伙伴

        你是一个常驻 QQ 的群聊伙伴，名称由「机器人名称」设置决定。你的性格友善、机灵、有分寸，能接梗，也能认真帮忙。
        根据当前对话回答问题或参与讨论，不把群友的每句话当作待改写的原文。
        日常聊天优先简短自然；需要解释时给出足够信息。避免反复使用同一句口头禅，不凭空编造经历、关系、事实或工具执行结果。
        群友要求直说、解释、代码或准确步骤时，保证可理解和可操作；代码、命令、链接、数字及关键事实保持准确。语言风格只改变表达，不能改变事实和实际行为。
        你仍是 QQ 机器人，不冒充现实人物或游戏角色，不强迫把无关话题带进作品或电竞世界。
        """;
    private const string StyleInstructions = """
        ## 语言风格的应用方式

        管理员已选择下面的 Skill 作为日常表达风格，无需群友每次要求改写。先想清楚自己要回答什么，再以选定的语言风格表达。
        下方 Skill 中的「原句」是你准备输出的回答，不是用户的问题；其中只在用户明确请求时启用的说明属于原技能的触发条件，在本模式下不适用。
        参考其表达技巧、词表、密度控制及示例，直接输出符合当前对话的回答，不附改写过程、原词对照或自检清单。
        身份、事实准确性、工具使用、隐私和群聊礼仪以本模板及行为规则为准；Skill 负责语言表达。
        """;

    public static string? ValidateName(string? name) => name is { Length: > 0 and <= 64 }
        && Regex.IsMatch(name, @"\A[\p{L}\p{Nd}_-]+\z") ? null
        : "风格名称需为 1–64 个字母、数字、中文、下划线或连字符。";

    private string FilePath(string name)
    {
        if (ValidateName(name) is string error) throw new ArgumentException(error);
        return Path.Combine(DirectoryPath, $"style_{name}.md");
    }

    public IReadOnlyList<LanguageStyle> List()
    {
        lock (_lock)
        {
            if (!Directory.Exists(DirectoryPath)) return [];
            return Directory.EnumerateFiles(DirectoryPath, "style_*.md")
                .Select(path => new LanguageStyle(Path.GetFileNameWithoutExtension(path)[6..], Path.GetFileName(path)))
                .Where(style => ValidateName(style.Name) == null).OrderBy(style => style.Name, StringComparer.Ordinal).ToList();
        }
    }

    public LanguageStyle Import(string name, string? markdown)
    {
        lock (_lock)
        {
            string path = FilePath(name);
            Normalize(markdown);
            if (List().Any(style => style.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("该风格名称已存在，请使用其他名称。");
            PromptSettingsService.WriteAtomic(path, markdown!);
            return new(name, Path.GetFileName(path));
        }
    }

    public string Read(string name)
    {
        lock (_lock)
        {
            string path = FilePath(name);
            if (!File.Exists(path)) throw new ArgumentException("语言风格不存在，请先导入 Skill 文件。");
            if (new FileInfo(path).Length > MaxSkillChars * 4L) throw new ArgumentException("Skill 文件过大。");
            return Normalize(File.ReadAllText(path));
        }
    }

    public void Delete(string name)
    {
        lock (_lock)
        {
            string path = FilePath(name);
            if (!File.Exists(path)) throw new ArgumentException("语言风格不存在。");
            File.Delete(path);
        }
    }

    public string Compose(string name) => PersonaTemplate + "\n\n" + StyleInstructions
        + "\n\n## 已应用的语言风格 Skill\n\n" + Read(name);

    internal static string Normalize(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) throw new ArgumentException("Skill 正文不能为空。");
        if (markdown.Length > MaxSkillChars) throw new ArgumentException($"Skill 文档不能超过 {MaxSkillChars} 字符。");
        string skill = markdown.TrimStart('\uFEFF').Replace("\r\n", "\n").Trim();
        if (skill.StartsWith("---\n", StringComparison.Ordinal))
        {
            int end = skill.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end < 0 || (end + 4 < skill.Length && skill[end + 4] != '\n'))
                throw new ArgumentException("Skill 的 YAML 元数据未正确闭合。");
            skill = skill[(end + 4)..].Trim();
        }
        if (string.IsNullOrWhiteSpace(skill)) throw new ArgumentException("Skill 正文不能为空。");
        return skill;
    }
}
