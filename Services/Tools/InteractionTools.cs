using RainBot.Services.Config;
using RainBot.Services.Profile;
using RainBot.Services.Storage;

namespace RainBot.Services.Tools;

public sealed class InteractionTools(UserIdentityResolver identities, Database db, RuntimeConfig config)
{
    public void Register(ToolRegistry registry)
    {
        registry.RegisterExecutor("at_user", async (args, ctx) =>
        {
            if (ctx.IsPrivate) return "私聊无需艾特。";
            string? input = ToolRegistry.ParseArguments(args)?["user"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(input)) return "参数 user 不能为空。";
            ResolutionResult result = await identities.ResolveAsync(ctx.GroupOpenId, input);
            if (result.User == null) return result.Message ?? "无法解析目标群友。";
            UserProfile? profile = await db.GetUserProfileAsync(ctx.GroupOpenId, result.User.OpenId);
            if (profile == null || !DateTimeOffset.TryParse(profile.LastActive, out DateTimeOffset lastActive) ||
                lastActive < DateTimeOffset.UtcNow.AddHours(-Math.Max(1, config.Config.Profile.ActiveWindowHours)))
                return "该群友不在当前群的近期活跃成员名单中，不能艾特。";
            ctx.RequestedAtUserId = result.User.OpenId;
            return $"本轮回复将艾特 {result.User.Nickname}({result.User.ShortId})。请直接写自然回复，勿在正文输出完整标识。";
        });
    }
}
