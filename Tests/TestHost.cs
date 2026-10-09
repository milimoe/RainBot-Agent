using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RainBot.Models;
using RainBot.Services.Commands;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Llm;
using RainBot.Services.Persona;
using RainBot.Services.Profile;
using RainBot.Services.Safety;
using RainBot.Services.Storage;
using RainBot.Services.Tools;
using RainBot.Services.Trigger;
using RainBot.Services.Workflow;

namespace RainBot.Tests;

/// <summary>
/// 测试宿主：用 ServiceCollection 组装真实服务（数据库用临时文件、LLM 用假 HTTP 处理器），
/// 便于对核心逻辑做黑盒断言。
/// </summary>
public static class TestHost
{
    public static (ServiceProvider Provider, string DbPath) Build(Func<HttpRequestMessage, HttpResponseMessage>? llmResponder = null)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"rainbot-test-{Guid.NewGuid():N}.db");
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Rain:Storage:SqlitePath"] = dbPath
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(configuration);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(Options.Create(new RainConfig()));
        services.AddSingleton<Database>();
        services.AddSingleton<RuntimeConfig>();
        services.AddSingleton<PromptSettingsService>();
        services.AddSingleton<PersonaCatalog>();
        services.AddSingleton<UserIdentityResolver>();
        services.AddSingleton<InteractionTools>();
        services.AddSingleton<PersonaLoader>();
        services.AddSingleton<GroupStateManager>();
        services.AddSingleton<Services.QQ.BotIdentityResolver>();
        services.AddSingleton<HistoryStore>();
        services.AddSingleton<AnchorManager>();
        services.AddSingleton<ProfileRecaller>();
        services.AddSingleton<RainBot.Services.Tools.ToolCallRecorder>();
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<DeepSeekClient>();
        services.AddSingleton<ReasoningRecorder>();
        services.AddSingleton<VisionImageLoader>();
        services.AddSingleton<ReActLoop>();
        services.AddSingleton<InputFilter>();
        services.AddSingleton<OutputFilter>();
        services.AddSingleton<PassiveTrigger>();
        services.AddSingleton<ActiveTrigger>();
        services.AddSingleton<Distiller>();
        services.AddHostedService(sp => sp.GetRequiredService<Distiller>());
        services.AddSingleton<BlockComposer>();
        services.AddSingleton<WatermarkManager>();
        services.AddSingleton<Services.Llm.CacheMonitor>();
        services.AddSingleton<WorkflowRunner>();
        services.AddSingleton<CommandParser>();
        services.AddMemoryCache();
        services.AddSingleton<BotConfigService>();
        services.AddSingleton<Services.QQ.QQBotService>();

        // 多机器人实例与平台抽象（SendQueue / FunService 依赖）
        services.AddSingleton<Services.Bots.BotConnectionRegistry>();
        services.AddSingleton<Services.Bots.BotInstanceStore>();
        services.AddSingleton<Services.Bots.QqOfficialSender>();
        services.AddSingleton<Services.OneBot.OneBotManager>();
        services.AddSingleton<Services.OneBot.OneBotSender>();
        services.AddSingleton<Services.Bots.BotSenderRouter>();
        services.AddSingleton<Services.Bots.BotSendStats>();
        services.AddSingleton<Services.Bots.QqMenuPanelService>();

        services.AddSingleton<Services.QQ.BotStatus>();
        services.AddSingleton<Services.QQ.MessageQueue>();
        services.AddSingleton<Services.QQ.MessageProcessor>();
        services.AddSingleton<SendQueue>();
        services.AddSingleton<Services.Fun.SayNoWordsService>();
        services.AddSingleton<Services.Fun.OsmImageCatalog>();
        services.AddSingleton<Services.Fun.FunService>();
        services.AddSingleton<IHttpClientFactory>(_ => new FakeHttpClientFactory(llmResponder));

        ServiceProvider provider = services.BuildServiceProvider();
        return (provider, dbPath);
    }

    public static async Task<ServiceProvider> BuildReadyAsync(Func<HttpRequestMessage, HttpResponseMessage>? llmResponder = null)
    {
        (ServiceProvider provider, _) = Build(llmResponder);
        await provider.GetRequiredService<Database>().InitializeAsync();
        await provider.GetRequiredService<RuntimeConfig>().InitializeAsync();

        // 多机器人：测试用默认实例（启用，凭据占位；发送走 FakeHttpClientFactory）
        Services.Bots.BotInstanceStore store = provider.GetRequiredService<Services.Bots.BotInstanceStore>();
        await store.InitializeAsync();
        await store.UpsertAsync(new Models.BotInstance
        {
            Id = Database.LegacyBotId,
            Name = "测试实例",
            Platform = Models.BotPlatform.QqOfficial,
            Enabled = true,
            Qq = new Models.QqOfficialConfig { AppId = "test-appid", Secret = "test-secret" }
        });
        return provider;
    }
}

/// <summary>假 HTTP 客户端工厂：所有请求走同一应答器（用于 DeepSeek 模拟）</summary>
public class FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage>? responder) : IHttpClientFactory
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder ?? DefaultResponder;

    public HttpClient CreateClient(string name) => new(new FakeHttpMessageHandler(_responder))
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static HttpResponseMessage DefaultResponder(HttpRequestMessage request)
    {
        // 默认返回一个简单的 LLM 文本回复（含用量统计）
        string json = """{"choices":[{"message":{"role":"assistant","content":"你好呀 🌧️"}}],"usage":{"prompt_tokens":100,"completion_tokens":8,"prompt_cache_hit_tokens":90,"prompt_cache_miss_tokens":10}}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

public class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(responder(request));
}

public static class TestHelpers
{
    /// <summary>构造一条入站群消息</summary>
    public static IncomingMessage Msg(string group, string sender, string content, bool isAt = false, string? msgId = null, bool isAdmin = false, string? username = null, DateTimeOffset? msgTime = null) => new()
    {
        MsgId = msgId ?? Guid.NewGuid().ToString("N"),
        GroupOpenId = group,
        SenderOpenId = sender,
        Username = username,
        Content = content,
        IsAtRobot = isAt,
        IsAdmin = isAdmin,
        ReceivedAt = msgTime ?? DateTimeOffset.UtcNow
    };

    public static string LlmTextResponse(string content, int hit = 90, int miss = 10) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content } } },
        usage = new { prompt_tokens = hit + miss, completion_tokens = 8, prompt_cache_hit_tokens = hit, prompt_cache_miss_tokens = miss }
    });
}
