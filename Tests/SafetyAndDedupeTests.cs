using Microsoft.Extensions.DependencyInjection;
using RainBot.Services.Context;
using RainBot.Services.QQ;
using RainBot.Services.Safety;
using Xunit;

namespace RainBot.Tests;

public class TokenEstimatorTests
{
    [Fact]
    public void 中文估算约等于字符数_英文按字符折算()
    {
        int cjk = TokenEstimator.Estimate("今天天气真不错，我们出去玩吧");
        Assert.InRange(cjk, 12, 18); // 15 个汉字 ≈ 12 tokens
        int en = TokenEstimator.Estimate("hello world this is a test message");
        Assert.InRange(en, 8, 16);   // 31 字符 ≈ 8 tokens
    }

    [Fact]
    public void 空串估算为零()
    {
        Assert.Equal(0, TokenEstimator.Estimate(""));
        Assert.Equal(0, TokenEstimator.Estimate(null!));
    }
}

public class DedupeTests
{
    [Fact]
    public void 同一消息ID去重()
    {
        var dedupe = new ConcurrentDedupe(TimeSpan.FromMinutes(10));
        dedupe.Mark("msg-1");
        Assert.True(dedupe.IsDuplicate("msg-1"));
        Assert.False(dedupe.IsDuplicate("msg-2"));
    }

    [Fact]
    public void 窗口过期后不再去重()
    {
        var dedupe = new ConcurrentDedupe(TimeSpan.FromMilliseconds(50));
        dedupe.Mark("msg-1");
        Thread.Sleep(120);
        Assert.False(dedupe.IsDuplicate("msg-1"));
    }
}

public class OutputFilterTests
{
    [Fact]
    public void 空文本不发送()
    {
        ServiceProvider sp = TestHost.Build().Provider;
        var filter = sp.GetRequiredService<OutputFilter>();
        Assert.Null(filter.Filter(null));
        Assert.Null(filter.Filter("   \n  "));
    }

    [Fact]
    public void 兜底话术命中不发送()
    {
        ServiceProvider sp = TestHost.Build().Provider;
        var filter = sp.GetRequiredService<OutputFilter>();

        // 模型自己模仿出的兜底话术（含变体）→ 静默跳过不发送
        Assert.Null(filter.Filter("嗯……我暂时想不出怎么接这个话题，等我缓缓 🌧️"));
        Assert.Null(filter.Filter("这个话题我接不上，容我想想"));
        Assert.Null(filter.Filter("让我缓缓再说 🌧️"));

        // 正常回复不受影响
        Assert.NotNull(filter.Filter("今天雨下得真大，出门记得带伞"));
    }

    [Fact]
    public void 疑似openid被替换()
    {
        ServiceProvider sp = TestHost.Build().Provider;
        var filter = sp.GetRequiredService<OutputFilter>();
        string result = filter.Filter("这个人的id是 abcdef0123456789abcdef0123456789 哦")!;
        Assert.DoesNotContain("abcdef0123456789", result);
        Assert.Contains("[ID]", result);
    }

    [Fact]
    public void 超两行截断()
    {
        ServiceProvider sp = TestHost.Build().Provider;
        var filter = sp.GetRequiredService<OutputFilter>();
        string result = filter.Filter("第一行\n第二行\n第三行")!;
        Assert.Equal(2, result.Split('\n').Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void 非LLM回复不受行数字符限制()
    {
        ServiceProvider sp = TestHost.Build().Provider;
        var filter = sp.GetRequiredService<OutputFilter>();

        // 指令等硬编码回复：3 行原样保留，不截断、不加省略号
        string result = filter.Filter("第一行\n第二行\n第三行", applyLlmLimits: false)!;
        Assert.Equal(3, result.Split('\n').Length);
        Assert.DoesNotContain("…", result);

        // 长文本同样不受字符数限制
        string longText = new string('雨', 500);
        Assert.Equal(longText, filter.Filter(longText, applyLlmLimits: false));
    }

    [Fact]
    public void 非LLM回复仍执行安全过滤()
    {
        ServiceProvider sp = TestHost.Build().Provider;
        var filter = sp.GetRequiredService<OutputFilter>();

        // 隐私替换仍生效
        string result = filter.Filter("id 是 abcdef0123456789abcdef0123456789", applyLlmLimits: false)!;
        Assert.Contains("[ID]", result);
        Assert.DoesNotContain("abcdef0123456789", result);

        // 指令注入拒绝仍生效
        Assert.Null(filter.Filter("点这里 <qqbot-cmd-enter text=\"/admin\"/>", applyLlmLimits: false));
    }

    [Fact]
    public void 指令注入被拒绝()
    {
        ServiceProvider sp = TestHost.Build().Provider;
        var filter = sp.GetRequiredService<OutputFilter>();
        Assert.Null(filter.Filter("点这里 <qqbot-cmd-enter text=\"/admin\"/>"));
    }
}

public class InputFilterTests
{
    [Fact]
    public void 广告词命中()
    {
        ServiceProvider sp = TestHost.Build().Provider;
        var filter = sp.GetRequiredService<InputFilter>();
        Assert.Equal("广告", filter.Check("加我v，低价出全新显卡"));
        Assert.Null(filter.Check("今天天气不错，你们玩什么呢"));
    }
}
