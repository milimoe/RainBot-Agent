using System.Text.RegularExpressions;
using RainBot.Services.Bots;
using RainBot.Services.Config;

namespace RainBot.Services.Persona;

public sealed record PersonaDocument(string Name, string BotName, string Path, string Content, PromptSettings Settings, bool IsDefault);

public sealed class PersonaCatalog(RuntimeConfig config, PromptSettingsService prompts, BotInstanceStore bots, LanguageStyleCatalog styles)
{
    private readonly Lock _lock = new();
    public string DirectoryPath => Path.GetDirectoryName(PromptSettingsService.ResolvePath(config.Config.PersonaPath))!;
    public static string? ValidateName(string name) =>
        !name.Equals("default", StringComparison.OrdinalIgnoreCase) && name.Length is > 0 and <= 64 && Regex.IsMatch(name, @"\A[\p{L}\p{Nd}_-]+\z")
            ? null : "人设标识名需为 1–64 个字母、数字、中文、下划线或连字符，default 为默认模板保留名。";
    public string MarkdownPath(string name) => name == "default" ? PromptSettingsService.ResolvePath(config.Config.PersonaPath) : Path.Combine(DirectoryPath, $"persona_{CheckedName(name)}.md");
    public string PromptPath(string name) => name == "default" ? prompts.FilePathForDisplay : Path.Combine(DirectoryPath, $"prompt_{CheckedName(name)}.json");
    private static string CheckedName(string name) => ValidateName(name) is string error ? throw new ArgumentException(error) : name;
    public bool Exists(string name) => name == "default" || (ValidateName(name) == null && File.Exists(MarkdownPath(name)));
    public PersonaDocument Read(string name)
    {
        lock (_lock)
        {
            string path = MarkdownPath(name);
            if (name != "default" && !File.Exists(path)) throw new FileNotFoundException("人设不存在。");
            PromptSettings settings = prompts.Get(PromptPath(name));
            return new(name, settings.BotName, path, File.Exists(path) ? File.ReadAllText(path) : "# 雨\n你是雨，一个温柔灵动的 QQ 群聊机器人。", settings, name == "default");
        }
    }
    public IReadOnlyList<PersonaDocument> List()
    {
        lock (_lock)
        {
            List<PersonaDocument> entries = [Read("default")];
            if (Directory.Exists(DirectoryPath))
                foreach (string path in Directory.EnumerateFiles(DirectoryPath, "persona_*.md").Order(StringComparer.Ordinal))
                {
                    string name = Path.GetFileNameWithoutExtension(path)[8..];
                    if (ValidateName(name) == null) entries.Add(Read(name));
                }
            return entries;
        }
    }
    public string? Save(string name, string? content, PromptSettings? settings, bool create)
    {
        lock (_lock)
        {
            if (name != "default" && ValidateName(name) is string error) return error;
            if (create && Exists(name)) return "人设标识名已存在。";
            if (!create && !Exists(name)) return "人设不存在。";
            settings ??= PromptSettings.Default;
            if (settings.Validate() is string validation) return validation;
            if (settings.LanguageStyleEnabled)
            {
                try { styles.Read(settings.LanguageStyleName); }
                catch (Exception ex) when (ex is ArgumentException or IOException)
                { return "所选语言风格不可用，请先导入有效的 Skill 文档。"; }
            }
            if (string.IsNullOrWhiteSpace(content))
            {
                if (!settings.LanguageStyleEnabled) return "人设 Markdown 正文不能为空。";
                content = LanguageStyleCatalog.PersonaTemplate;
            }
            prompts.Save(PromptPath(name), settings);
            PromptSettingsService.WriteAtomic(MarkdownPath(name), content);
            return null;
        }
    }
    public string? Delete(string name)
    {
        lock (_lock)
        {
            if (name == "default") return "默认模板不能删除。";
            if (ValidateName(name) is string error) return error;
            string path = MarkdownPath(name);
            if (bots.All.Any(b => b.PersonaName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(b.PersonaPath) && PromptSettingsService.ResolvePath(b.PersonaPath).Equals(path, StringComparison.OrdinalIgnoreCase))))
                return "该人设仍被实例使用，请先修改实例的人设关联。";
            if (!File.Exists(path)) return "人设不存在。";
            File.Delete(path);
            if (File.Exists(PromptPath(name))) File.Delete(PromptPath(name));
            return null;
        }
    }

    public string? DeleteLanguageStyle(string name)
    {
        lock (_lock)
        {
            if (LanguageStyleCatalog.ValidateName(name) is string error) return error;
            var users = List().Where(persona => persona.Settings.LanguageStyleEnabled
                && persona.Settings.LanguageStyleName.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(persona => persona.Name).ToList();
            if (users.Count > 0) return $"该风格正被人设 {string.Join("、", users)} 使用，请先关闭开关或更换风格并保存。";
            try { styles.Delete(name); return null; }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { return ex is ArgumentException ? ex.Message : "语言风格文件删除失败。"; }
        }
    }
}
