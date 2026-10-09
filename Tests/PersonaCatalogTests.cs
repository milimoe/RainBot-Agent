using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Config;
using RainBot.Services.Persona;
using Xunit;

namespace RainBot.Tests;

public class PersonaCatalogTests
{
    private static async Task<(ServiceProvider Provider, string Directory)> BuildAsync()
    {
        ServiceProvider provider = await TestHost.BuildReadyAsync();
        string directory = Path.Combine(Path.GetTempPath(), "rainbot-persona-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        RuntimeConfig config = provider.GetRequiredService<RuntimeConfig>();
        config.Config.PersonaPath = Path.Combine(directory, "persona.md");
        config.Config.PromptPath = Path.Combine(directory, "prompt.json");
        File.WriteAllText(config.Config.PersonaPath, "# 默认雨");
        return (provider, directory);
    }

    [Fact]
    public async Task 标识与称呼独立_实例持久化并选择人设()
    {
        var (provider, directory) = await BuildAsync();
        using (provider)
        try
        {
            var catalog = provider.GetRequiredService<PersonaCatalog>();
            var bots = provider.GetRequiredService<BotInstanceStore>();
            var loader = provider.GetRequiredService<PersonaLoader>();
            Assert.Null(catalog.Save("gentle", "# 温柔角色", PromptSettings.Default with { BotName = "小糖" }, true));
            Assert.True(File.Exists(Path.Combine(directory, "persona_gentle.md")));
            Assert.Null(await bots.UpsertAsync(new BotInstance { Id = "custom", Enabled = false, PersonaName = "gentle" }));
            Assert.Equal("gentle", bots.Get("custom")!.PersonaName);
            Assert.Contains("名为「小糖」", loader.GetSystemPrompt("custom"));
            Assert.Contains("温柔角色", loader.GetSystemPrompt("custom"));
            Assert.Contains("默认雨", loader.GetSystemPrompt("qq"));
            Assert.DoesNotContain("温柔角色", loader.GetSystemPrompt("qq"));
            Assert.NotNull(catalog.Delete("gentle"));
            Assert.Null(catalog.Save("gentle", "# 更新角色", PromptSettings.Default with { BotName = "新糖" }, false));
            Assert.Contains("新糖", loader.GetSystemPrompt("custom"));
            Assert.Contains("更新角色", loader.GetSystemPrompt("custom"));
            Assert.Null(await bots.UpsertAsync(new BotInstance { Id = "custom", Enabled = false }));
            Assert.Null(catalog.Delete("gentle"));
            Assert.NotNull(catalog.Delete("default"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task 旧路径仍加载_缺失人设回退默认()
    {
        var (provider, directory) = await BuildAsync();
        using (provider)
        try
        {
            var bots = provider.GetRequiredService<BotInstanceStore>();
            var loader = provider.GetRequiredService<PersonaLoader>();
            string oldPath = Path.Combine(directory, "old.md");
            File.WriteAllText(oldPath, "旧版独立人设");
            Assert.Null(await bots.UpsertAsync(new BotInstance { Id = "legacy", Enabled = false, PersonaPath = oldPath }));
            Assert.Contains("旧版独立人设", loader.GetSystemPrompt("legacy"));
            Assert.Null(await bots.UpsertAsync(new BotInstance { Id = "missing", Enabled = false, PersonaName = "gone" }));
            Assert.Contains("默认雨", loader.GetSystemPrompt("missing"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("default")]
    [InlineData("")]
    public void 不接受非法名称(string name) => Assert.NotNull(PersonaCatalog.ValidateName(name));

    [Fact]
    public async Task 配置默认生成_热重载_损坏保留_校验空条目()
    {
        var (provider, directory) = await BuildAsync();
        using (provider)
        try
        {
            var prompts = provider.GetRequiredService<PromptSettingsService>();
            Assert.Equal("雨", prompts.Current.BotName);
            Assert.True(File.Exists(prompts.FilePathForDisplay));
            File.WriteAllText(prompts.FilePathForDisplay, "{\"botName\":\"甜雨\"}");
            File.SetLastWriteTimeUtc(prompts.FilePathForDisplay, DateTime.UtcNow.AddSeconds(1));
            Assert.Equal("甜雨", prompts.Current.BotName);
            Assert.NotEmpty(prompts.Current.Rules);
            File.WriteAllText(prompts.FilePathForDisplay, "{bad json}");
            Assert.Equal("甜雨", prompts.Current.BotName);
            Assert.NotNull((PromptSettings.Default with { Rules = [""] }).Validate());
            var catalog = provider.GetRequiredService<PersonaCatalog>();
            Assert.NotNull(catalog.Save("bad", "  ", PromptSettings.Default, true));
            Assert.Null(catalog.Save("糖_1", "# 糖", PromptSettings.Default, true));
            Assert.NotNull(catalog.Save("糖_1", "# 糖", PromptSettings.Default, true));
            Assert.Contains(catalog.List(), p => p.Name == "糖_1");
        }
        finally { Directory.Delete(directory, true); }
    }
}
