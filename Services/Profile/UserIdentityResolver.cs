using System.Collections.Concurrent;
using RainBot.Services.Storage;

namespace RainBot.Services.Profile;

public sealed record ResolvedUser(string OpenId, string ShortId, string? Nickname);
public sealed record ResolutionResult(ResolvedUser? User, IReadOnlyList<ResolvedUser> Candidates, string? Message);

/// <summary>群隔离的昵称缓存与完整标识、短标识、昵称解析。歧义必须由调用者消解。</summary>
public sealed class UserIdentityResolver(Database db)
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _nicknames = new();

    public static string ShortId(string openId) => "u" + (openId.Length > 8 ? openId[..8] : openId);

    public void LearnNickname(string groupOpenId, string openId, string? nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname) || string.IsNullOrWhiteSpace(openId)) return;
        _nicknames.GetOrAdd(groupOpenId, _ => new())[openId] = nickname.Trim();
    }

    public async Task<string?> GetNicknameAsync(string groupOpenId, string openId)
    {
        if (_nicknames.TryGetValue(groupOpenId, out var members) && members.TryGetValue(openId, out string? nickname)) return nickname.Length == 0 ? null : nickname;
        UserProfile? profile = await db.GetUserProfileAsync(groupOpenId, openId);
        LearnNickname(groupOpenId, openId, profile?.Nickname);
        if (string.IsNullOrWhiteSpace(profile?.Nickname)) _nicknames.GetOrAdd(groupOpenId, _ => new()).TryAdd(openId, "");
        return string.IsNullOrWhiteSpace(profile?.Nickname) ? null : profile.Nickname;
    }

    public async Task<ResolutionResult> ResolveAsync(string groupOpenId, string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new(null, [], "请提供群友昵称或标识。");
        List<UserProfile> profiles = await db.FindUsersAsync(groupOpenId, input);
        List<ResolvedUser> candidates = profiles.Select(p => new ResolvedUser(p.UserOpenId, ShortId(p.UserOpenId), p.Nickname)).ToList();
        foreach (UserProfile profile in profiles) LearnNickname(groupOpenId, profile.UserOpenId, profile.Nickname);
        if (candidates.Count == 1) return new(candidates[0], candidates, null);
        return new(null, candidates, candidates.Count == 0 ? "未找到当前群成员；请使用上下文中的群友标识。" :
            "匹配到多位群友，请用更明确的完整标识重新选择：" + string.Join("、", candidates.Select(c => $"{c.Nickname}({c.ShortId}) 完整标识={c.OpenId}")));
    }
}
