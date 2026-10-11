using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Config;
using RainBot.Services.Persona;
using Xunit;

namespace RainBot.Tests;

public class LanguageStyleTests
{
    [Fact]
    public void 风格库初始为空且上传按名称保存原文()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rainbot-styles-" + Guid.NewGuid().ToString("N"));
        try
        {
            var styles = new LanguageStyleCatalog(directory);
            Assert.Empty(styles.List());
            string original = "\uFEFF---\r\nname: custom\r\n---\r\n# 风格\r\n测试风格正文";
            Assert.Equal("style_custom.md", styles.Import("custom", original).FileName);
            Assert.Equal(original.TrimStart('\uFEFF'), File.ReadAllText(Path.Combine(directory, "style_custom.md")));
            Assert.Single(new LanguageStyleCatalog(directory).List());
            Assert.Contains("测试风格正文", styles.Read("custom"));
            Assert.DoesNotContain("name: custom", styles.Read("custom"));
            Assert.Throws<ArgumentException>(() => styles.Import("CUSTOM", "替换正文"));
            Assert.Equal(original.TrimStart('\uFEFF'), File.ReadAllText(Path.Combine(directory, "style_custom.md")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task 开关持久化并控制固定模板和风格加载且可关闭恢复人设()
    {
        await using var provider = await TestHost.BuildReadyAsync();
        string directory = Path.Combine(Path.GetTempPath(), "rainbot-style-persona-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = provider.GetRequiredService<RuntimeConfig>();
            config.Config.PersonaPath = Path.Combine(directory, "persona.md");
            config.Config.PromptPath = Path.Combine(directory, "prompt.json");
            var prompts = provider.GetRequiredService<PromptSettingsService>();
            var bots = provider.GetRequiredService<BotInstanceStore>();
            var styles = new LanguageStyleCatalog(Path.Combine(directory, "skills", "styles"));
            styles.Import("custom", "# 风格\n唯一风格内容");
            var catalog = new PersonaCatalog(config, prompts, bots, styles);
            var loader = new PersonaLoader(prompts, catalog, bots, NullLogger<PersonaLoader>.Instance, styles);
            var enabled = PromptSettings.Default with { BotName = "小糖", LanguageStyleEnabled = true, LanguageStyleName = "custom" };
            Assert.Null(catalog.Save("styled", "自定义人设内容", enabled, true));
            Assert.NotNull(catalog.DeleteLanguageStyle("custom"));
            Assert.True(File.Exists(Path.Combine(styles.DirectoryPath, "style_custom.md")));
            Assert.Null(await bots.UpsertAsync(new BotInstance { Id = "styled", Enabled = false, PersonaName = "styled" }));
            string prompt = loader.GetSystemPrompt("styled");
            Assert.Contains(LanguageStyleCatalog.PersonaTemplate, prompt);
            Assert.Contains("唯一风格内容", prompt);
            Assert.DoesNotContain("自定义人设内容", prompt);
            Assert.Contains("名为「小糖」", prompt);
            Assert.Contains("隐私保护", prompt);
            Assert.DoesNotContain("最多 2 行", prompt);
            Assert.True(catalog.Read("styled").Settings.LanguageStyleEnabled);
            Assert.Equal("自定义人设内容", catalog.Read("styled").Content);
            File.WriteAllText(Path.Combine(styles.DirectoryPath, "style_custom.md"), "更新的风格");
            Assert.Contains("更新的风格", loader.GetSystemPrompt("styled"));
            Assert.Null(catalog.Save("styled", "自定义人设内容", enabled with { LanguageStyleEnabled = false }, false));
            prompt = loader.GetSystemPrompt("styled");
            Assert.Contains("自定义人设内容", prompt);
            Assert.DoesNotContain("更新的风格", prompt);
            Assert.Null(catalog.Save("empty", "", enabled, true));
            Assert.Equal(LanguageStyleCatalog.PersonaTemplate, catalog.Read("empty").Content);
            Assert.NotNull(catalog.DeleteLanguageStyle("custom"));
            Assert.Null(catalog.Save("empty", LanguageStyleCatalog.PersonaTemplate, enabled with { LanguageStyleEnabled = false }, false));
            Assert.Null(catalog.DeleteLanguageStyle("custom"));
            Assert.Empty(styles.List());
            Assert.NotNull(catalog.DeleteLanguageStyle("custom"));
            prompts.Save(catalog.PromptPath("empty"), enabled);
            Assert.Null(await bots.UpsertAsync(new BotInstance { Id = "empty", Enabled = false, PersonaName = "empty" }));
            Assert.Contains(LanguageStyleCatalog.PersonaTemplate, loader.GetSystemPrompt("empty"));
            Assert.NotNull(catalog.Save("missing", "", enabled, true));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void 不允许路径名称(string name)
    {
        Assert.NotNull(LanguageStyleCatalog.ValidateName(name));
        Assert.NotNull((PromptSettings.Default with { LanguageStyleEnabled = true, LanguageStyleName = name }).Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("---\nname: incomplete")]
    [InlineData("---\nname: empty\n---\n")]
    public void 无效文档不能上传(string? content) => Assert.Throws<ArgumentException>(() => LanguageStyleCatalog.Normalize(content));

    [Fact]
    public void 默认关闭且限制导入大小()
    {
        Assert.False(PromptSettings.Default.LanguageStyleEnabled);
        Assert.Throws<ArgumentException>(() => LanguageStyleCatalog.Normalize(new string('字', LanguageStyleCatalog.MaxSkillChars + 1)));
    }
}
