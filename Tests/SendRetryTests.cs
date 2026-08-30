using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Services.Bots;
using RainBot.Services.Config;
using RainBot.Services.Trigger;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 发送失败重试策略测试：
/// - SendResult.FromHttp 分类：429/4xx 不可重试（频控/参数类），5xx 与网络异常可重试；
/// - SendQueue 集成：5xx 失败后指数退避重试并最终成功；429 不重试直接丢弃。
/// </summary>
public class SendRetryTests
{
    private const string Group = "group_retry_test";
    private const string BotId = "qq";

    // ---------- SendResult 分类（纯单元） ----------

    [Fact]
    public void FromHttp_按状态码分类可重试性()
    {
        Assert.True(SendResult.FromHttp(true, null, null).Success);

        // 429 频控 / 4xx 参数类：不重试
        Assert.False(SendResult.FromHttp(false, 429, "rate limited").Retryable);
        Assert.False(SendResult.FromHttp(false, 400, "bad request").Retryable);
        Assert.False(SendResult.FromHttp(false, 404, "not found").Retryable);

        // 5xx 临时故障 / 无状态码（网络异常、超时）：可重试
        Assert.True(SendResult.FromHttp(false, 500, "boom").Retryable);
        Assert.True(SendResult.FromHttp(false, 503, "unavailable").Retryable);
        Assert.True(SendResult.FromHttp(false, null, "timeout").Retryable);
    }

    // ---------- SendQueue 集成 ----------

    [Fact]
    public async Task 发送5xx失败_退避重试后成功()
    {
        List<HttpResponseMessage> responses =
        [
            new(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") },
            new(HttpStatusCode.OK) { Content = new StringContent("{}") }
        ];
        List<string> bodies = [];
        (ServiceProvider sp, _) = await BuildAsync(responses, bodies);
        try
        {
            SendQueue queue = sp.GetRequiredService<SendQueue>();
            await queue.EnqueueAsync(new SendTask { BotId = BotId, GroupOpenId = Group, Content = "重试我" });

            // 第一次 500 失败 → 1.5s 退避 → 第二次成功
            Assert.True(await WaitForAsync(() => bodies.Count >= 2, 15000), "失败后未发生重试");
            Assert.True(await WaitForAsync(() =>
                sp.GetRequiredService<BotSendStats>().Get(BotId).Sent >= 1, 5000), "重试成功后应记一次成功");
        }
        finally
        {
            await sp.GetRequiredService<SendQueue>().StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 发送429频控_不重试直接失败()
    {
        List<HttpResponseMessage> responses =
        [
            new(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limited") }
        ];
        List<string> bodies = [];
        (ServiceProvider sp, _) = await BuildAsync(responses, bodies);
        try
        {
            SendQueue queue = sp.GetRequiredService<SendQueue>();
            await queue.EnqueueAsync(new SendTask { BotId = BotId, GroupOpenId = Group, Content = "频控" });

            Assert.True(await WaitForAsync(() => bodies.Count >= 1, 5000), "未捕获到发送请求");
            BotSendStatSnapshot stats = sp.GetRequiredService<BotSendStats>().Get(BotId);
            Assert.True(await WaitForAsync(() => stats.Failed >= 1, 5000), "频控失败应计入统计");

            // 频控类不重试：多等一会不会出现第二次请求
            await Task.Delay(2000);
            Assert.Equal(1, bodies.Count);
        }
        finally
        {
            await sp.GetRequiredService<SendQueue>().StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 重试超过上限_丢弃不再发送()
    {
        // 4 次全 500：首次 + 3 次重试后放弃
        List<HttpResponseMessage> responses =
        [
            new(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") },
            new(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") },
            new(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") },
            new(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }
        ];
        List<string> bodies = [];
        (ServiceProvider sp, _) = await BuildAsync(responses, bodies);
        try
        {
            SendQueue queue = sp.GetRequiredService<SendQueue>();
            await queue.EnqueueAsync(new SendTask { BotId = BotId, GroupOpenId = Group, Content = "撑不住" });

            // 1.5s + 3s + 6s = 10.5s 内完成全部 4 次尝试
            Assert.True(await WaitForAsync(() => bodies.Count >= 4, 20000), "未完成首次与全部重试");
            await Task.Delay(2000);
            Assert.Equal(4, bodies.Count); // 超限后不再发送
        }
        finally
        {
            await sp.GetRequiredService<SendQueue>().StopAsync(CancellationToken.None);
        }
    }

    // ---------- 辅助 ----------

    /// <summary>构建宿主：消息接口按预置响应队列依次返回（耗尽后返回 500）</summary>
    private static async Task<(ServiceProvider Sp, List<string> Bodies)> BuildAsync(
        List<HttpResponseMessage> responses, List<string> bodies)
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("getAppAccessToken", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"fake-token\",\"expires_in\":\"7200\"}", Encoding.UTF8, "application/json")
                };
            }
            bodies.Add(req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            lock (responses)
            {
                if (responses.Count > 0)
                {
                    HttpResponseMessage next = responses[0];
                    responses.RemoveAt(0);
                    return next;
                }
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("exhausted") };
        });
        SendQueue queue = sp.GetRequiredService<SendQueue>();
        await queue.StartAsync(CancellationToken.None);
        return (sp, bodies);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return condition();
    }
}
