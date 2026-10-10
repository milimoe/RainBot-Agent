using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RainBot.Services.QQ;
using Xunit;

namespace RainBot.Tests;

public class QqEventCaptureTests
{
    [Fact]
    public async Task 完整保留长卡片未知字段和并发消息()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rainbot-capture-" + Guid.NewGuid().ToString("N"));
        try
        {
            using QqEventCapture capture = new(true, directory, NullLogger<QqEventCapture>.Instance);
            string text = new('长', 12000);
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new
            { content = text, ark_data = new { unknown_field = new { jump_url = "https://b23.tv/example", value = "完整卡片" } } }));
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => capture.RecordAsync("bot", "GROUP_MESSAGE_CREATE", document.RootElement)));
            string[] lines = await File.ReadAllLinesAsync(Assert.Single(Directory.GetFiles(directory)));
            Assert.Equal(20, lines.Length);
            foreach (string line in lines)
            {
                using JsonDocument saved = JsonDocument.Parse(line);
                JsonElement data = saved.RootElement.GetProperty("data");
                Assert.Equal(text, data.GetProperty("content").GetString());
                Assert.Equal("完整卡片", data.GetProperty("ark_data").GetProperty("unknown_field").GetProperty("value").GetString());
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false, "GROUP_MESSAGE_CREATE")]
    [InlineData(true, "READY")]
    [InlineData(true, "RESUMED")]
    public async Task 默认关闭且不会记录会话鉴权事件(bool enabled, string eventType)
    {
        string directory = Path.Combine(Path.GetTempPath(), "rainbot-capture-" + Guid.NewGuid().ToString("N"));
        using QqEventCapture capture = new(enabled, directory, NullLogger<QqEventCapture>.Instance);
        using JsonDocument data = JsonDocument.Parse("{\"session_id\":\"secret\"}");
        await capture.RecordAsync("bot", eventType, data.RootElement);
        Assert.False(Directory.Exists(directory));
    }
}
