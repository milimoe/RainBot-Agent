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
