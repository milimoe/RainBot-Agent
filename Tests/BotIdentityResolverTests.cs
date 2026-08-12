using Microsoft.Extensions.DependencyInjection;
using RainBot.Services.QQ;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 机器人身份识别（BOT_OPENID）：全量消息精确判断是否 @ 机器人
/// </summary>
public class BotIdentityResolverTests
{
    private const string BotOpenId = "ABCDEF1234567890ABCDEF1234567890";

    [Fact]
    public async Task 从AT事件自动学习机器人openid()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var resolver = sp.GetRequiredService<BotIdentityResolver>();

        // openid 未知时不判定为 @
        Assert.False(await resolver.IsAtBotAsync($"<@!{BotOpenId}>在吗"));

        // 收到 GROUP_AT_MESSAGE_CREATE（content 含 <@!xxx> 标签）→ 自动学习
        await resolver.ResolveFromAtContentAsync($"<@!{BotOpenId}>在吗");
        Assert.Equal(BotOpenId, await resolver.GetBotOpenIdAsync());
        Assert.True(await resolver.IsAtBotAsync($"<@!{BotOpenId}>在吗"));
    }

    [Fact]
    public async Task 精确匹配_不误判艾特他人()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var resolver = sp.GetRequiredService<BotIdentityResolver>();
        await resolver.ResolveFromAtContentAsync($"<@!{BotOpenId}>在吗");

        // @ 别人：openid 不匹配 → 不算 @ 机器人
        Assert.False(await resolver.IsAtBotAsync("<@!FFFF0000FFFF0000FFFF0000FFFF0000>晚上好"));
        // 提到名字但无 @ 标签 → 不算
        Assert.False(await resolver.IsAtBotAsync("雨 今天天气怎么样"));
        // 无任何标签 → 不算
        Assert.False(await resolver.IsAtBotAsync("今天天气怎么样"));
    }

    [Fact]
    public async Task 学习值落库_重启后仍生效()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var resolver = sp.GetRequiredService<BotIdentityResolver>();
        await resolver.ResolveFromAtContentAsync($"<@!{BotOpenId}>在吗");

        // 新实例（模拟重启）：从数据库 settings 恢复
        var db = sp.GetRequiredService<Services.Storage.Database>();
        Assert.Equal(BotOpenId, await db.GetSettingAsync("Bot.OpenId"));
    }

    [Fact]
    public async Task 配置值优先于学习值()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var resolver = sp.GetRequiredService<BotIdentityResolver>();
        await resolver.ResolveFromAtContentAsync($"<@!{BotOpenId}>在吗");

        // 管理员配置了明确 openid（RAIN__BOTOPENID / /admin set Bot.OpenId）
        var config = sp.GetRequiredService<Services.Config.RuntimeConfig>();
        await config.SetAsync("Bot.OpenId", "CONFIG_OPEN_ID_123456");
        Assert.Equal("CONFIG_OPEN_ID_123456", await resolver.GetBotOpenIdAsync());
        Assert.True(await resolver.IsAtBotAsync("<@!CONFIG_OPEN_ID_123456>你好"));
        Assert.False(await resolver.IsAtBotAsync($"<@!{BotOpenId}>你好"));
    }
}
