using Microsoft.Extensions.DependencyInjection;
using RainBot.Services.Commands;
using Xunit;

namespace RainBot.Tests;

public class CommandParserTests
{
    [Fact]
    public async Task 解析_忘掉我指令()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var parser = sp.GetRequiredService<CommandParser>();
        ParsedCommand? cmd = parser.Parse("/忘掉我");
        Assert.NotNull(cmd);
        Assert.Equal(CommandKind.ForgetMe, cmd!.Kind);

        // 清理自己的画像
        var db = sp.GetRequiredService<Services.Storage.Database>();
        await db.UpsertUserProfileAsync(new Services.Storage.UserProfile
        {
            GroupOpenId = "g",
            UserOpenId = "user_alpha_0123",
            InteractionCount = 10,
            LastActive = DateTimeOffset.UtcNow.ToString("o")
        });
        string? reply = await parser.ExecuteAsync(cmd, "g", "user_alpha_0123", isAdmin: false);
        Assert.NotNull(reply);
        Assert.Null(await db.GetUserProfileAsync("g", "user_alpha_0123"));
    }

    [Fact]
    public async Task 解析_admin_set_参数热改()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var parser = sp.GetRequiredService<CommandParser>();
        ParsedCommand? cmd = parser.Parse("/admin set Trigger.PassiveCooldownSeconds 60");
        Assert.NotNull(cmd);
        Assert.Equal(CommandKind.AdminSet, cmd!.Kind);
        Assert.Equal("Trigger.PassiveCooldownSeconds", cmd.Key);
        Assert.Equal("60", cmd.Value);

        // 非管理员被拒绝
        string? denied = await parser.ExecuteAsync(cmd, "g", "user_x", isAdmin: false);
        Assert.Contains("管理员", denied);

        // 管理员成功且参数即时生效（旧版全局管理员：库表兜底仍生效）
        var config = sp.GetRequiredService<Services.Config.RuntimeConfig>();
        await sp.GetRequiredService<Services.Storage.Database>().AddAdminOpenIdAsync("user_admin_0123");
        string? reply = await parser.ExecuteAsync(cmd, "g", "user_admin_0123", isAdmin: true);
        Assert.Contains("已更新", reply);
        Assert.Equal(60, config.Config.Trigger.PassiveCooldownSeconds);

        // 无效参数名
        ParsedCommand? bad = parser.Parse("/admin set NotExistKey 1");
        Assert.NotNull(bad);
        string? badReply = await parser.ExecuteAsync(bad!, "g", "user_admin_0123", isAdmin: true);
        Assert.Contains("未知参数", badReply);
    }

    [Fact]
    public async Task 解析_非指令消息返回null()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var parser = sp.GetRequiredService<CommandParser>();
        Assert.Null(parser.Parse("今天天气不错"));
        Assert.Null(parser.Parse("https://example.com"));
    }

    [Fact]
    public async Task 解析_管理员静默指令()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var parser = sp.GetRequiredService<CommandParser>();
        ParsedCommand? mute = parser.Parse("/admin mute");
        Assert.NotNull(mute);
        Assert.Equal(CommandKind.AdminMute, mute!.Kind);
        await parser.ExecuteAsync(mute, "g", "user_admin_0123", isAdmin: true);

        var states = sp.GetRequiredService<Services.Trigger.GroupStateManager>();
        Assert.True(states.GetOrCreate("g").Muted);

        ParsedCommand? unmute = parser.Parse("/admin unmute");
        await parser.ExecuteAsync(unmute!, "g", "user_admin_0123", isAdmin: true);
        Assert.False(states.GetOrCreate("g").Muted);
    }
}
