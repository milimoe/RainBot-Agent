using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RainBot.Services.Config;
using RainBot.Services.Trigger;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 发送队列集成测试：验证 Markdown 回复模式（msg_type=2 + markdown 包体）
/// 与纯文本模式（msg_type=0 + content 字段）的实际 HTTP 请求体。
/// </summary>
public class SendQueueTests
{
    private const string Group = "group_send_test";

    private static async Task<(ServiceProvider Sp, List<string> Bodies)> BuildWithCaptureAsync(Action<RuntimeConfig>? configure = null)
    {
        List<string> bodies = [];
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            // 打令牌接口：返回假 token
            if (req.RequestUri!.AbsolutePath.Contains("getAppAccessToken", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"fake-token\",\"expires_in\":\"7200\"}", Encoding.UTF8, "application/json")
                };
            }
            // 其余请求（群消息发送）：捕获请求体
            bodies.Add(req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        RuntimeConfig config = sp.GetRequiredService<RuntimeConfig>();
        configure?.Invoke(config);
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

    [Fact]
    public async Task Markdown回复开启_以msg_type2发送markdown包体()
    {
        (ServiceProvider sp, List<string> bodies) = await BuildWithCaptureAsync(c => c.Config.MarkdownReply = true);
        try
        {
            SendQueue queue = sp.GetRequiredService<SendQueue>();
            await queue.EnqueueAsync(new SendTask { GroupOpenId = Group, Content = "**加粗**测试" });

            Assert.True(await WaitForAsync(() => bodies.Count > 0), "未捕获到发送请求");

            using JsonDocument doc = JsonDocument.Parse(bodies[0]);
            JsonElement root = doc.RootElement;
            Assert.Equal(2, root.GetProperty("msg_type").GetInt32());
            Assert.Equal("**加粗**测试", root.GetProperty("markdown").GetProperty("content").GetString());
            Assert.False(root.TryGetProperty("content", out _), "Markdown 包体不应包含纯文本 content 字段");
        }
        finally
        {
            await sp.GetRequiredService<SendQueue>().StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Markdown回复关闭_以msg_type0发送纯文本()
    {
        (ServiceProvider sp, List<string> bodies) = await BuildWithCaptureAsync(c => c.Config.MarkdownReply = false);
        try
        {
            SendQueue queue = sp.GetRequiredService<SendQueue>();
            await queue.EnqueueAsync(new SendTask { GroupOpenId = Group, Content = "普通文本" });

            Assert.True(await WaitForAsync(() => bodies.Count > 0), "未捕获到发送请求");

            using JsonDocument doc = JsonDocument.Parse(bodies[0]);
            JsonElement root = doc.RootElement;
            Assert.Equal(0, root.GetProperty("msg_type").GetInt32());
            Assert.Equal("\r\n普通文本", root.GetProperty("content").GetString());
            Assert.False(root.TryGetProperty("markdown", out _), "纯文本包体不应包含 markdown 字段");
        }
        finally
        {
            await sp.GetRequiredService<SendQueue>().StopAsync(CancellationToken.None);
        }
    }
}
