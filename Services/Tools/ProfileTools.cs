using System.Text.Json.Nodes;
using RainBot.Services.Profile;
using RainBot.Services.Storage;
using RainBot.Services.Tools;

namespace RainBot.Services.Tools;

/// <summary>
/// 画像工具：get_user_profile（L2 召回）/ update_user_profile（仅主动暖群写入 L0）。
/// </summary>
public class ProfileTools(Database db, AnchorManager anchorManager, ILogger<ProfileTools> logger)
{
    private readonly Database _db = db;
    private readonly AnchorManager _anchorManager = anchorManager;
    private readonly ILogger<ProfileTools> _logger = logger;

    /// <summary>注册工具执行器（由 Program.cs 装配时调用）</summary>
    public void Register(ToolRegistry registry)
    {
        registry.RegisterExecutor("get_user_profile", async (args, ctx) =>
        {
            JsonObject? param = ToolRegistry.ParseArguments(args);
            string? user = param?["user"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(user))
            {
                return "参数 user 不能为空。";
            }
            UserProfile? profile = await ResolveUserAsync(ctx.GroupOpenId, user);
            if (profile == null)
            {
                return $"没有找到用户 {user} 的画像。";
            }
            // L2 召回：不含敏感字段
            List<string> parts = [];
            if (profile.Tags.Count > 0) parts.Add("标签: " + string.Join(", ", profile.Tags));
            if (!string.IsNullOrWhiteSpace(profile.Interests)) parts.Add("兴趣: " + profile.Interests);
            if (!string.IsNullOrWhiteSpace(profile.Habits)) parts.Add("习惯: " + profile.Habits);
            if (!string.IsNullOrWhiteSpace(profile.Summary)) parts.Add("过往: " + profile.Summary);
            return parts.Count > 0 ? string.Join("；", parts) : $"用户 {user} 暂无画像信息。";
        });

        registry.RegisterExecutor("update_user_profile", async (args, ctx) =>
        {
            if (!ctx.AllowProfileUpdate)
            {
                return "画像是沉淀在主动暖群过程中的，当前场景不允许修改，请直接与群友交流。";
            }
            JsonObject? param = ToolRegistry.ParseArguments(args);
            string? user = param?["user"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(user))
            {
                return "参数 user 不能为空。";
            }
            UserProfile? profile = await ResolveUserAsync(ctx.GroupOpenId, user);
            if (profile == null)
            {
                return $"没有找到用户 {user} 的画像，无法更新。";
            }

            if (param!["tags"] is JsonArray tags)
            {
                List<string> tagList = tags.Select(t => t?.GetValue<string>()?.Trim()).Where(t => !string.IsNullOrWhiteSpace(t)).Take(8).ToList()!;
                if (tagList.Count > 0) profile.Tags = tagList;
            }
            if (param["interests"] is JsonValue iv) profile.Interests = iv.GetValue<string>().Trim();
            if (param["habits"] is JsonValue hv) profile.Habits = hv.GetValue<string>().Trim();
            if (param["summary"] is JsonValue sv) profile.Summary = sv.GetValue<string>().Trim();

            await _db.UpsertUserProfileAsync(profile);
            _anchorManager.Invalidate(ctx.GroupOpenId);
            _logger.LogInformation("用户画像已更新：{Group} / {User}", ctx.GroupOpenId, profile.UserOpenId);
            return $"已更新用户 {user} 的画像。";
        });
    }

    /// <summary>按短 ID 或完整 openid 解析用户</summary>
    private async Task<UserProfile?> ResolveUserAsync(string groupOpenId, string user)
    {
        string trimmed = user.Trim();
        if (trimmed.StartsWith("u", StringComparison.OrdinalIgnoreCase) && trimmed.Length <= 12)
        {
            return await _db.FindUserByShortIdAsync(groupOpenId, trimmed);
        }
        return await _db.GetUserProfileAsync(groupOpenId, trimmed);
    }
}
