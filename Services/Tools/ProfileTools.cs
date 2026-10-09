using System.Text.Json.Nodes;
using RainBot.Services.Profile;
using RainBot.Services.Storage;
using RainBot.Services.Config;

namespace RainBot.Services.Tools;

/// <summary>
/// 画像工具：L2 召回与按每日配额沉淀画像；暖群可绕过配额。
/// </summary>
public class ProfileTools(Database db, AnchorManager anchorManager, ILogger<ProfileTools> logger, UserIdentityResolver identities, RuntimeConfig config)
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
            ResolutionResult resolution = await identities.ResolveAsync(ctx.GroupOpenId, user);
            if (resolution.User == null) return resolution.Message ?? "暂未建档；等待该群友发言后再查询。";
            UserProfile? profile = await _db.GetUserProfileAsync(ctx.GroupOpenId, resolution.User.OpenId);
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
            return parts.Count > 0 ? string.Join("；", parts) : $"群友 {user} 已建档，暂无画像内容；可根据真实对话用 update_user_profile 沉淀兴趣和习惯。";
        });

        registry.RegisterExecutor("update_user_profile", async (args, ctx) =>
        {
            JsonObject? param = ToolRegistry.ParseArguments(args);
            string? user = param?["user"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(user))
            {
                return "参数 user 不能为空。";
            }
            ResolutionResult resolution = await identities.ResolveAsync(ctx.GroupOpenId, user);
            if (resolution.User == null) return resolution.Message ?? "无法解析群友。";
            UserProfile? profile = await _db.GetUserProfileAsync(ctx.GroupOpenId, resolution.User.OpenId);
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

            if (!await _db.TryUpdateUserProfileAsync(profile, config.Config.Profile.DailyUpdateLimit, ctx.AllowProfileUpdate))
            {
                _logger.LogDebug("群友画像更新已达到每日配额");
                return "该群友今日画像更新配额已用完，请明天再沉淀。";
            }
            _anchorManager.Invalidate(ctx.GroupOpenId);
            _logger.LogInformation("用户画像已更新：{Group} / {User}", ctx.GroupOpenId, UserIdentityResolver.ShortId(profile.UserOpenId));
            return $"已更新用户 {user} 的画像。";
        });
    }

}
