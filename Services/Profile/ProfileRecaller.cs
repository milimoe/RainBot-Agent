using RainBot.Services.Storage;

namespace RainBot.Services.Profile;

/// <summary>
/// L2 触发式召回：被 @ 时从 L0 拉取与该用户/话题相关的画像字段，
/// 置于 Block F（尾部，下轮丢弃），不写入常驻前缀。
/// 敏感字段（sensitive）绝不返回。
/// </summary>
public class ProfileRecaller(Database db, ILogger<ProfileRecaller> logger)
{
    private readonly Database _db = db;
    private readonly ILogger<ProfileRecaller> _logger = logger;

    private const int MaxRecalledChars = 200;

    /// <summary>召回指定用户的画像摘要（用于 Block F）</summary>
    public async Task<string> RecallAsync(string groupOpenId, string userOpenId)
    {
        UserProfile? profile = await _db.GetUserProfileAsync(groupOpenId, userOpenId);
        if (profile == null)
        {
            return "";
        }

        List<string> parts = [];
        if (profile.Tags.Count > 0)
        {
            parts.Add("标签: " + string.Join(", ", profile.Tags));
        }
        if (!string.IsNullOrWhiteSpace(profile.Interests))
        {
            parts.Add("兴趣: " + profile.Interests);
        }
        if (!string.IsNullOrWhiteSpace(profile.Habits))
        {
            parts.Add("习惯: " + profile.Habits);
        }
        if (!string.IsNullOrWhiteSpace(profile.Summary))
        {
            parts.Add("过往: " + profile.Summary);
        }

        string result = string.Join("；", parts);
        if (result.Length > MaxRecalledChars)
        {
            result = result[..MaxRecalledChars] + "…";
        }
        return result;
    }
}
