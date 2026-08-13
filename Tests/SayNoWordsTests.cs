using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Services.Config;
using RainBot.Services.Fun;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// SayNo 词表 JSON 配置 + 热更新（对齐原版 PluginConfig 用法）
/// </summary>
public class SayNoWordsTests
{
    private static async Task<(ServiceProvider Sp, string SayNoFile)> BuildWithSayNoAsync()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        RuntimeConfig config = sp.GetRequiredService<RuntimeConfig>();
        string file = Path.Combine(Path.GetTempPath(), $"sayno-test-{Guid.NewGuid():N}.json");
        config.Config.SayNoPath = file; // SayNoWordsService 懒加载，首次 Current 时生效
        return (sp, file);
    }

    /// <summary>强制修改文件时间戳，确保热重载检测生效</summary>
    private static void TouchFile(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
    }

    [Fact]
    public async Task 首次访问自动生成默认词表文件()
    {
        (ServiceProvider sp, string file) = await BuildWithSayNoAsync();
        var sayNo = sp.GetRequiredService<SayNoWordsService>();

        Assert.False(File.Exists(file));
        SayNoWordSet current = sayNo.Current;
        Assert.True(File.Exists(file)); // 已生成

        // 生成的文件是合法 JSON 且包含默认词表
        SayNoWordSet? fromFile = JsonSerializer.Deserialize<SayNoWordSet>(await File.ReadAllTextAsync(file));
        Assert.NotNull(fromFile);
        Assert.Contains("不{0}", fromFile!.SayNoWords);
        Assert.Contains("太", fromFile.TriggerBeforeNo);
    }

    [Fact]
    public async Task 编辑JSON文件后自动热重载()
    {
        (ServiceProvider sp, string file) = await BuildWithSayNoAsync();
        var sayNo = sp.GetRequiredService<SayNoWordsService>();

        Assert.Contains("不{0}", sayNo.Current.SayNoWords); // 触发默认文件生成

        // 用户编辑文件：给 SayNoWords 加自定义词，清空 IgnoreTriggerAfterNo
        SayNoWordSet edited = sayNo.Current with
        {
            SayNoWords = ["自定义反驳{0}", "不{0}"],
            IgnoreTriggerAfterNo = []
        };
        TouchFile(file, JsonSerializer.Serialize(edited, new JsonSerializerOptions { WriteIndented = true }));

        // 无需重启/手动刷新：下一次访问即新词表
        SayNoWordSet reloaded = sayNo.Current;
        Assert.Equal("自定义反驳{0}", reloaded.SayNoWords[0]);
        Assert.Empty(reloaded.IgnoreTriggerAfterNo);
    }

    [Fact]
    public async Task 指令add写回JSON并即时生效()
    {
        (ServiceProvider sp, string file) = await BuildWithSayNoAsync();
        var sayNo = sp.GetRequiredService<SayNoWordsService>();
        _ = sayNo.Current; // 生成文件

        string? error = await sayNo.UpdateAsync("SayNoWords", add: true, "杠一下{0}");
        Assert.Null(error);
        Assert.Contains("杠一下{0}", sayNo.Current.SayNoWords);

        // 写回的文件也包含新词（持久化）
        SayNoWordSet? onDisk = JsonSerializer.Deserialize<SayNoWordSet>(await File.ReadAllTextAsync(file));
        Assert.Contains("杠一下{0}", onDisk!.SayNoWords);
    }

    [Fact]
    public async Task 指令remove删除词并写回()
    {
        (ServiceProvider sp, string file) = await BuildWithSayNoAsync();
        var sayNo = sp.GetRequiredService<SayNoWordsService>();
        _ = sayNo.Current;

        string? error = await sayNo.UpdateAsync("Trigger", add: false, "别");
        Assert.Null(error);
        Assert.DoesNotContain("别", sayNo.Current.Trigger);
        SayNoWordSet? onDisk = JsonSerializer.Deserialize<SayNoWordSet>(await File.ReadAllTextAsync(file));
        Assert.DoesNotContain("别", onDisk!.Trigger);
    }

    [Fact]
    public async Task 未知表名返回错误()
    {
        (ServiceProvider sp, _) = await BuildWithSayNoAsync();
        var sayNo = sp.GetRequiredService<SayNoWordsService>();
        string? error = await sayNo.UpdateAsync("NotExistTable", add: true, "x");
        Assert.NotNull(error);
        Assert.Contains("未知词表", error);
    }

    [Fact]
    public async Task 词表热更新影响FunService反驳行为()
    {
        (ServiceProvider sp, string file) = await BuildWithSayNoAsync();
        var sayNo = sp.GetRequiredService<SayNoWordsService>();
        var fun = sp.GetRequiredService<FunService>();
        RuntimeConfig config = sp.GetRequiredService<RuntimeConfig>();
        // 关闭其余随机互动，只保留反驳不（避免复读/叫哥等随机命中干扰断言）
        config.Config.Fun.ReplyYesProbability = 0;
        config.Config.Fun.ReplyNoProbability = 100;
        config.Config.Fun.RepeatProbability = 0;
        config.Config.Fun.CallBrotherProbability = 0;
        config.Config.Fun.OsmProbability = 0;
        config.Config.Fun.ReverseAtProbability = 0;

        // 默认："不要了"因忽略词"要"不触发反驳
        Assert.False((await fun.TryRespondAsync(TestHelpers.Msg("g", "u1", "不要了"))).Handled);

        // 热更新：清空 IgnoreTriggerAfterNo → "不要了"开始被反驳
        SayNoWordSet edited = sayNo.Current with { IgnoreTriggerAfterNo = [] };
        TouchFile(file, JsonSerializer.Serialize(edited, new JsonSerializerOptions { WriteIndented = true }));

        FunResult result = await fun.TryRespondAsync(TestHelpers.Msg("g", "u1", "不要了"));
        Assert.True(result.Handled);
    }
}
