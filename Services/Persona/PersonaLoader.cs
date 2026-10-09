using RainBot.Services.Bots;

namespace RainBot.Services.Persona;

/// <summary>按实例选用人设，保存后下一轮对话即时生效。</summary>
public class PersonaLoader(PromptSettingsService prompts, PersonaCatalog catalog, BotInstanceStore bots, ILogger<PersonaLoader> logger)
{
    public string GetSystemPrompt(string? botId = null)
    {
        var bot = botId == null ? null : bots.Get(botId);
        string name = string.IsNullOrWhiteSpace(bot?.PersonaName) ? "default" : bot.PersonaName;
        string persona;
        PromptSettings settings;
        if (name == "default" && !string.IsNullOrWhiteSpace(bot?.PersonaPath))
        {
            string path = PromptSettingsService.ResolvePath(bot.PersonaPath);
            persona = File.Exists(path) ? File.ReadAllText(path) : catalog.Read("default").Content;
            settings = prompts.Current;
        }
        else
        {
            if (!catalog.Exists(name))
            {
                logger.LogWarning("实例 {BotId} 的人设 {Name} 不存在，使用默认模板", botId, name);
                name = "default";
            }
            PersonaDocument document = catalog.Read(name);
            persona = document.Content;
            settings = document.Settings;
        }
        return BuildSystemPrompt(persona, settings);
    }
    public static string BuildSystemPrompt(string persona, PromptSettings settings) =>
        $"{settings.Intro.Replace("{name}", settings.BotName)}\n\n【{settings.PersonaSectionTitle}】\n{persona.Trim()}\n\n【{settings.RulesSectionTitle}】\n{string.Join("\n", settings.Rules.Select((r, i) => $"{i + 1}. {r}"))}\n\n【{settings.IdentitySectionTitle}】\n{string.Join("\n", settings.IdentityNotes)}";
}
