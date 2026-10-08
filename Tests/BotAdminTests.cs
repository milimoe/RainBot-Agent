using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Commands;
using RainBot.Services.Storage;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 管理员按机器人实例维护：同一 QQ 用户在不同 QQ 官方机器人下的 openid 不同，
/// 管理员列表挂在实例上按实例匹配；OneBot11 实例用 QQ 号匹配。
/// 旧版全局 AdminOpenIds（appsettings/库表）保留兜底兼容。
/// </summary>
public class BotAdminTests
{
    [Fact]
    public async Task 实例管理员_按机器人隔离()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var store = sp.GetRequiredService<BotInstanceStore>();
        await store.UpsertAsync(new BotInstance
        {
            Id = "bot-b",
            Name = "B",
            Platform = BotPlatform.QqOfficial,
            Enabled = false,
            Qq = new QqOfficialConfig { AppId = "app-b" }
        });

        Assert.Null(await store.AddAdminAsync(Database.LegacyBotId, "openid_A"));
        Assert.Null(await store.AddAdminAsync("bot-b", "openid_B"));

        // 本实例命中
        Assert.True(await store.IsAdminAsync(Database.LegacyBotId, "openid_A"));
        Assert.True(await store.IsAdminAsync("bot-b", "openid_B"));

        // 跨实例不命中：同一 ID 在另一机器人下不是管理员
        Assert.False(await store.IsAdminAsync("bot-b", "openid_A"));
        Assert.False(await store.IsAdminAsync(Database.LegacyBotId, "openid_B"));

        // 幂等添加与移除
        Assert.Null(await store.AddAdminAsync(Database.LegacyBotId, "openid_A"));
        Assert.Null(await store.RemoveAdminAsync(Database.LegacyBotId, "openid_A"));
        Assert.False(await store.IsAdminAsync(Database.LegacyBotId, "openid_A"));

        // 实例不存在 → 报错
        Assert.NotNull(await store.AddAdminAsync("ghost", "x"));
        Assert.NotNull(await store.RemoveAdminAsync("ghost", "x"));
    }

    [Fact]
    public async Task OneBot实例_用QQ号匹配管理员()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var store = sp.GetRequiredService<BotInstanceStore>();
        await store.UpsertAsync(new BotInstance
        {
            Id = "ob-test",
            Name = "OB",
            Platform = BotPlatform.OneBot11,
            Enabled = false,
            OneBot = new OneBotConfig { Http = new OneBotHttpConfig { Enabled = true, ApiUrl = "http://x" } }
        });

        await store.AddAdminAsync("ob-test", "123456789");
        Assert.True(await store.IsAdminAsync("ob-test", "123456789"));
        Assert.False(await store.IsAdminAsync("ob-test", "987654321"));
    }

    [Fact]
    public async Task 旧版全局管理员_对所有实例兜底生效()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var store = sp.GetRequiredService<BotInstanceStore>();
        var db = sp.GetRequiredService<Database>();
        await db.AddAdminOpenIdAsync("legacy_admin");

        Assert.True(await store.IsAdminAsync(Database.LegacyBotId, "legacy_admin"));
    }

    [Fact]
    public async Task admin_admin指令_维护当前实例管理员()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var parser = sp.GetRequiredService<CommandParser>();
        var store = sp.GetRequiredService<BotInstanceStore>();
        string botId = Database.LegacyBotId;

        ParsedCommand? add = parser.Parse("/admin admin add openid_X");
        Assert.NotNull(add);

        // 非管理员被拒绝
        string? denied = await parser.ExecuteAsync(add!, "g", "u", isAdmin: false, botId);
        Assert.Contains("管理员", denied);

        // 添加到当前实例
        string? reply = await parser.ExecuteAsync(add!, "g", "u", isAdmin: true, botId);
        Assert.Contains("已添加", reply);
        Assert.True(await store.IsAdminAsync(botId, "openid_X"));

        // 移除
        ParsedCommand? remove = parser.Parse("/admin admin remove openid_X");
        Assert.NotNull(remove);
        string? removed = await parser.ExecuteAsync(remove!, "g", "u", isAdmin: true, botId);
        Assert.Contains("已移除", removed);
        Assert.False(await store.IsAdminAsync(botId, "openid_X"));
    }
}
