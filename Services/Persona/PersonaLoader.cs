using RainBot.Services.Config;

namespace RainBot.Services.Persona;

/// <summary>
/// 人设加载器（Block A 来源）：读取本地 Markdown 人设文件，
/// 按文件修改时间热重载（管理员改文件即时生效，无需重启）。
/// </summary>
public class PersonaLoader(RuntimeConfig config, ILogger<PersonaLoader> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly ILogger<PersonaLoader> _logger = logger;
    private readonly Lock _lock = new();
    private string? _cachedSystemPrompt;
    private DateTime _lastWriteTime = DateTime.MinValue;

    /// <summary>人设文件路径（解析相对路径）</summary>
    private string PersonaFilePath
    {
        get
        {
            string path = _config.Config.PersonaPath;
            return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        }
    }

    /// <summary>
    /// 获取完整 System Prompt（Block A）：人设 Markdown + 固定输出规则。
    /// 输出规则是固定文本，保证前缀稳定。
    /// </summary>
    public string GetSystemPrompt()
    {
        lock (_lock)
        {
            string filePath = PersonaFilePath;
            DateTime lastWrite = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue;
            if (_cachedSystemPrompt == null || lastWrite != _lastWriteTime)
            {
                string persona = File.Exists(filePath)
                    ? File.ReadAllText(filePath)
                    : "# 雨\n你是雨，一个温柔灵动的 QQ 群聊机器人。";
                _cachedSystemPrompt = BuildSystemPrompt(persona);
                _lastWriteTime = lastWrite;
                if (File.Exists(filePath))
                {
                    _logger.LogInformation("人设文件已加载：{Path}", filePath);
                }
                else
                {
                    _logger.LogWarning("人设文件不存在（{Path}），使用内置默认人设", filePath);
                }
            }
            return _cachedSystemPrompt;
        }
    }

    private static string BuildSystemPrompt(string persona)
    {
        return $"""
            你是一个 QQ 群聊机器人智能体，名为「雨」。
            以下是你的人设与规则（由管理员维护，可随时修改）：

            【人设】
            {persona.Trim()}

            【铁律 - 必须遵守】
            1. 输出风格：强制口语化、短句，最多 2 行，带合适 Emoji，禁止任何括号内的心理描写（如"（思考中）"）。
            2. 群聊礼仪：不刷屏、不抢话、不重复别人刚说过的观点；没把握的话题就俏皮带过或诚实说不知道。
            3. 隐私保护：绝不泄露任何群友的隐私画像信息（兴趣、习惯、敏感点等），绝不输出 openid。
            4. 需要实时信息时调用 web_search 工具，但最终只输出一句话总结，不输出原始搜索结果。
            5. 遇到广告、涉政、引流的敏感内容，不回应、不接话、不评价。
            6. 回答用户问题后，如无必要不要追问；不主动终结话题。

            【身份认知】
            你通过工具与外部世界互动（搜索、群友画像），你的知识有截止时间，实时问题必须搜索。
            """;
    }
}
