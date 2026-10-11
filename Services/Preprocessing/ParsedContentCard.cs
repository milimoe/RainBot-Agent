namespace RainBot.Services.Preprocessing;

/// <summary>面向用户的结构化资料卡片，与供 LLM 阅读的正文独立。</summary>
public sealed record ParsedContentCard(string Kind, string Title, string Description,
    IReadOnlyList<ParsedContentField> Fields, string ImageUrl = "");

public sealed record ParsedContentField(string Label, string Value);
