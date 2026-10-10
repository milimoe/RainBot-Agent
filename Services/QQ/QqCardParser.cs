using System.Text.Json;
using RainBot.Services.Page;

namespace RainBot.Services.QQ;

/// <summary>只接收官 Q 图文卡片的显式目标字段，不猜测封面、小程序或其他字段。</summary>
public static class QqCardParser
{
    public static List<string> ExtractVideoLinks(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("ark_data", out JsonElement ark)
            || ark.ValueKind != JsonValueKind.Object || !ark.TryGetProperty("ark_type", out JsonElement type)
            || type.ValueKind != JsonValueKind.String || type.GetString() != "tuwen"
            || !ark.TryGetProperty("fields", out JsonElement fields) || fields.ValueKind != JsonValueKind.Object
            || !fields.TryGetProperty("jump_url", out JsonElement target) || target.ValueKind != JsonValueKind.String)
            return [];
        string? normalized = LinkPrefetcher.NormalizeVideoUrl(target.GetString() ?? "");
        return normalized == null ? [] : [normalized];
    }
}
