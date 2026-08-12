using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Services.Config;
using RainBot.Services.Fun;
using RainBot.Services.Trigger;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 随机互动（移植自原版 RainBOT）：反驳是/反驳不/复读/OSM/反向艾特/叫哥
/// </summary>
public class FunServiceTests
{
    private const string Group = "group_fun";
    private const string Sender = "sender_alpha_12345678";
    private const string SenderShort = "sender_a"; // openid 前 8 位

    private static async Task<ServiceProvider> BuildAsync(Action<RuntimeConfig>? configure = null, Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync(responder);
        RuntimeConfig config = sp.GetRequiredService<RuntimeConfig>();
        ZeroAll(config.Config.Fun);
        configure?.Invoke(config);
        return sp;
    }

    /// <summary>关闭全部随机互动概率（避免测试间功能互相干扰）</summary>
    private static void ZeroAll(RainBot.Models.FunConfig fun)
    {
        fun.ReplyYesProbability = 0;
        fun.ReplyNoProbability = 0;
        fun.RepeatProbability = 0;
        fun.OsmProbability = 0;
        fun.ReverseAtProbability = 0;
        fun.CallBrotherProbability = 0;
    }

    [Fact]
    public async Task 反驳是_消息为是_概率100必触发()
    {
        ServiceProvider sp = await BuildAsync(c => c.Config.Fun.ReplyYesProbability = 100);
        var fun = sp.GetRequiredService<FunService>();
        FunResult result = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "是"));
        Assert.True(result.Handled);
        Assert.True(result.Blocked);
    }

    [Fact]
    public async Task 反驳是_概率0不触发()
    {
        ServiceProvider sp = await BuildAsync(c => c.Config.Fun.ReplyYesProbability = 0);
        var fun = sp.GetRequiredService<FunService>();
        Assert.True((await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "是"))).Handled == false);
    }

    [Fact]
    public async Task 反驳是_非纯是消息不触发()
    {
        ServiceProvider sp = await BuildAsync(c => c.Config.Fun.ReplyYesProbability = 100);
        var fun = sp.GetRequiredService<FunService>();
        Assert.False((await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "是的呀"))).Handled);
        Assert.False((await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "是是是"))).Handled);
    }

    [Fact]
    public async Task 反驳不_词表命中()
    {
        ServiceProvider sp = await BuildAsync(c => c.Config.Fun.ReplyNoProbability = 100);
        var fun = sp.GetRequiredService<FunService>();
        // "不"后是"玩"（非忽略词）→ 反驳
        FunResult r1 = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "不玩了"));
        Assert.True(r1.Handled && r1.Blocked);
        // "太X了" → 特殊反驳
        FunResult r2 = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "太好了"));
        Assert.True(r2.Handled);
        // "没" 后字 → 反驳
        FunResult r3 = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "没钱"));
        Assert.True(r3.Handled);
    }

    [Fact]
    public async Task 反驳不_忽略词不触发()
    {
        ServiceProvider sp = await BuildAsync(c => c.Config.Fun.ReplyNoProbability = 100);
        var fun = sp.GetRequiredService<FunService>();
        // "不"后是忽略词"要/能" → 不反驳（"不是吧"会命中"是+吧"反驳分支，属设计行为）
        Assert.False((await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "不要了"))).Handled);
        Assert.False((await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "不能去"))).Handled);
    }

    [Fact]
    public async Task 复读_概率100触发且延迟在区间内()
    {
        ServiceProvider sp = await BuildAsync(c => c.Config.Fun.RepeatProbability = 100);
        var fun = sp.GetRequiredService<FunService>();
        var queue = sp.GetRequiredService<SendQueue>();
        FunResult result = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "今天天气不错"));
        Assert.True(result.Handled && result.Blocked);

        IReadOnlyList<SendTask> pending = queue.PeekPendingForTest();
        SendTask task = Assert.Single(pending);
        Assert.True(task.Content.Contains("今天天气不错") || task.Content.Contains("desuwa～"));
        Assert.InRange(task.DelaySeconds, 30, 80);
    }

    [Fact]
    public async Task 复读_忽略词不触发()
    {
        ServiceProvider sp = await BuildAsync(c =>
        {
            c.Config.Fun.RepeatProbability = 100;
            c.Config.Fun.RepeatIgnoreWords.Add("别复读");
        });
        var fun = sp.GetRequiredService<FunService>();
        Assert.False((await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "别复读了"))).Handled);
    }

    [Fact]
    public async Task 反向艾特_概率100触发且不阻断AI()
    {
        ServiceProvider sp = await BuildAsync(c => c.Config.Fun.ReverseAtProbability = 100);
        var fun = sp.GetRequiredService<FunService>();
        var queue = sp.GetRequiredService<SendQueue>();
        FunResult result = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "在吗", isAt: true));
        Assert.True(result.Handled);
        Assert.False(result.Blocked); // 反向艾特后继续 AI 回复

        SendTask task = Assert.Single(queue.PeekPendingForTest());
        Assert.Contains($"<@!{Sender}>", task.Content);
    }

    [Fact]
    public async Task 反向艾特_非艾特消息不触发()
    {
        ServiceProvider sp = await BuildAsync(c => c.Config.Fun.ReverseAtProbability = 100);
        var fun = sp.GetRequiredService<FunService>();
        Assert.False((await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "在吗"))).Handled);
    }

    [Fact]
    public async Task 叫哥_概率100触发()
    {
        ServiceProvider sp = await BuildAsync(c => c.Config.Fun.CallBrotherProbability = 100);
        var fun = sp.GetRequiredService<FunService>();
        var queue = sp.GetRequiredService<SendQueue>();
        FunResult result = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "随便聊聊", username: "米莉"));
        Assert.True(result.Handled && result.Blocked);

        SendTask task = Assert.Single(queue.PeekPendingForTest());
        Assert.Contains($"<@!{Sender}>", task.Content);
        // 名字随机截取 1-2 字 + 随机后缀（哥/姐/圣/亲/哈基）
        Assert.Contains(new[] { "哥", "姐", "圣", "亲", "哈基" }, suffix => task.Content.Contains(suffix, StringComparison.Ordinal));
        Assert.True(task.DelaySeconds > 0);
    }

    [Fact]
    public async Task 叫哥_忽略用户不触发()
    {
        ServiceProvider sp = await BuildAsync(c =>
        {
            c.Config.Fun.CallBrotherProbability = 100;
            c.Config.Fun.CallBrotherIgnoreOpenIds.Add(SenderShort); // 短 ID 匹配
        });
        var fun = sp.GetRequiredService<FunService>();
        Assert.False((await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "hi"))).Handled);
    }

    [Fact]
    public async Task 互斥_一次消息最多命中一个功能()
    {
        ServiceProvider sp = await BuildAsync(c =>
        {
            c.Config.Fun.ReplyYesProbability = 100;
            c.Config.Fun.ReplyNoProbability = 100;
            c.Config.Fun.RepeatProbability = 100;
            c.Config.Fun.CallBrotherProbability = 100;
        });
        var fun = sp.GetRequiredService<FunService>();
        var queue = sp.GetRequiredService<SendQueue>();
        // "是"会先命中反驳是（优先级最高），复读/叫哥不再触发
        FunResult result = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "是"));
        Assert.True(result.Handled);
        Assert.Single(queue.PeekPendingForTest()); // 只入队一条
    }

    [Fact]
    public async Task OSM_未配置图片自动禁用()
    {
        ServiceProvider sp = await BuildAsync(c => c.Config.Fun.OsmProbability = 100);
        var fun = sp.GetRequiredService<FunService>();
        Assert.False((await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "随便"))).Handled);
    }

    [Fact]
    public async Task 功能开关关闭不触发()
    {
        ServiceProvider sp = await BuildAsync(c =>
        {
            c.Config.Fun.EnableReplyYes = false;
            c.Config.Fun.ReplyYesProbability = 100;
            c.Config.Fun.EnableCallBrother = false;
            c.Config.Fun.CallBrotherProbability = 100;
        });
        var fun = sp.GetRequiredService<FunService>();
        Assert.False((await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "是"))).Handled);
        Assert.False((await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "hi"))).Handled);
    }

    [Fact]
    public async Task 反驳不_烂梗API命中优先于词表()
    {
        ServiceProvider sp = await BuildAsync(c =>
        {
            c.Config.Fun.ReplyNoProbability = 100;
            c.Config.Fun.ReplyNoMemeProbability = 100; // 必走烂梗
        }, _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":{\"barrage\":\"哈哈哈哈笑死我了\"}}", Encoding.UTF8, "application/json")
        });
        var fun = sp.GetRequiredService<FunService>();
        var queue = sp.GetRequiredService<SendQueue>();
        FunResult result = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "不玩了"));
        Assert.True(result.Handled);
        SendTask task = Assert.Single(queue.PeekPendingForTest());
        Assert.Equal("哈哈哈哈笑死我了", task.Content); // 烂梗替换词表
    }

    [Fact]
    public async Task 反驳不_烂梗API失败回退词表()
    {
        ServiceProvider sp = await BuildAsync(c =>
        {
            c.Config.Fun.ReplyNoProbability = 100;
            c.Config.Fun.ReplyNoMemeProbability = 100;
        }, _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });
        var fun = sp.GetRequiredService<FunService>();
        var queue = sp.GetRequiredService<SendQueue>();
        FunResult result = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "不玩了"));
        Assert.True(result.Handled);
        // 回退到词表反驳（含"玩"）
        Assert.Contains("玩", Assert.Single(queue.PeekPendingForTest()).Content);
    }

    [Fact]
    public async Task 反驳不_烂梗响应格式异常回退词表()
    {
        ServiceProvider sp = await BuildAsync(c =>
        {
            c.Config.Fun.ReplyNoProbability = 100;
            c.Config.Fun.ReplyNoMemeProbability = 100;
        }, _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"unexpected\":true}")
        });
        var fun = sp.GetRequiredService<FunService>();
        var queue = sp.GetRequiredService<SendQueue>();
        FunResult result = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "不玩了"));
        Assert.True(result.Handled);
        Assert.Contains("玩", Assert.Single(queue.PeekPendingForTest()).Content);
    }

    [Fact]
    public async Task 反驳不_烂梗概率0不请求API()
    {
        int apiCalls = 0;
        ServiceProvider sp = await BuildAsync(c =>
        {
            c.Config.Fun.ReplyNoProbability = 100;
            c.Config.Fun.ReplyNoMemeProbability = 0; // 不走烂梗
        }, _ =>
        {
            Interlocked.Increment(ref apiCalls);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"barrage\":\"不该出现\"}}", Encoding.UTF8, "application/json")
            };
        });
        var fun = sp.GetRequiredService<FunService>();
        FunResult result = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "不玩了"));
        Assert.True(result.Handled);
        Assert.Equal(0, apiCalls); // 未请求烂梗 API，用词表
    }

    [Fact]
    public async Task 反驳不_未配置烂梗URL不请求API()
    {
        int apiCalls = 0;
        ServiceProvider sp = await BuildAsync(c =>
        {
            c.Config.Fun.ReplyNoProbability = 100;
            c.Config.Fun.ReplyNoMemeProbability = 100;
            c.Config.Fun.ReplyNoMemeUrl = ""; // URL 为空
        }, _ =>
        {
            Interlocked.Increment(ref apiCalls);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        var fun = sp.GetRequiredService<FunService>();
        FunResult result = await fun.TryRespondAsync(TestHelpers.Msg(Group, Sender, "不玩了"));
        Assert.True(result.Handled);
        Assert.Equal(0, apiCalls);
    }
}
