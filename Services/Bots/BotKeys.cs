namespace RainBot.Services.Bots;

/// <summary>
/// 多机器人实例的键规则：内部群键 = {实例Id}:{原始群号}。
/// 因为画像/历史/统计全部以群键为维度，带上实例 Id 后各机器人的数据天然隔离，
/// 无需改动 Database 的任何方法签名（它们只把 ID 当不透明字符串使用）。
/// </summary>
public static class BotKeys
{
    public const char Separator = ':';

    /// <summary>拼接内部群键</summary>
    public static string Group(string botId, string rawGroupId) => $"{botId}{Separator}{rawGroupId}";

    /// <summary>
    /// 拼接内部私聊键：{实例Id}:p{用户号}。
    /// 加 p 标记是为了与群号区分——同一实例下群号与用户号都是数字，不带标记可能撞键，
    /// 而该键就是存储身份（历史/画像/统计都按它隔离）。
    /// </summary>
    public static string Private(string botId, string rawUserId) => $"{botId}{Separator}{PrivateMarker}{rawUserId}";

    /// <summary>私聊键标记（紧跟分隔符）</summary>
    public const char PrivateMarker = 'p';

    /// <summary>该内部键是否为私聊会话</summary>
    public static bool IsPrivateKey(string? key)
    {
        if (!TrySplit(key, out _, out string rawId))
        {
            return false;
        }
        return rawId.Length > 0 && rawId[0] == PrivateMarker;
    }

    /// <summary>
    /// 取对端原始 ID：群号直接返回；私聊去掉 p 标记后即用户号。
    /// </summary>
    public static string GetRawPeerId(string? key)
    {
        if (!TrySplit(key, out _, out string rawId))
        {
            return key ?? "";
        }
        return rawId.Length > 0 && rawId[0] == PrivateMarker ? rawId[1..] : rawId;
    }

    /// <summary>
    /// 拆分内部群键。返回是否成功（老数据无前缀时返回 false，rawId 原样返回）。
    /// </summary>
    public static bool TrySplit(string? groupKey, out string botId, out string rawGroupId)
    {
        botId = "";
        rawGroupId = groupKey ?? "";
        if (string.IsNullOrEmpty(groupKey))
        {
            return false;
        }
        int idx = groupKey.IndexOf(Separator);
        if (idx <= 0 || idx == groupKey.Length - 1)
        {
            return false;
        }
        botId = groupKey[..idx];
        rawGroupId = groupKey[(idx + 1)..];
        return true;
    }

    /// <summary>取实例 Id（无前缀返回空）</summary>
    public static string GetBotId(string? groupKey) => TrySplit(groupKey, out string botId, out _) ? botId : "";

    /// <summary>取原始群号/群 openid</summary>
    public static string GetRawGroupId(string? groupKey) => TrySplit(groupKey, out _, out string raw) ? raw : groupKey ?? "";
}
