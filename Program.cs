using System.Text.Json.Nodes;
using RainBot.Models;
using RainBot.Services.Commands;using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Llm;
using RainBot.Services.Persona;
using RainBot.Services.Profile;
using RainBot.Services.QQ;
using RainBot.Services.Safety;
using RainBot.Services.Scheduler;
using RainBot.Services.Search;
using RainBot.Services.Storage;
using RainBot.Services.Tools;
using RainBot.Services.Topic;
using RainBot.Services.Trigger;
using RainBot.Services.Workflow;
using RainBot.Services.Fun;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
DateTimeOffset appStartTime = DateTimeOffset.UtcNow;

// ---------- 基础服务 ----------
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("duckduckgo"); // DuckDuckGo 搜索客户端
builder.Services.AddHttpClient("deepseek");   // DeepSeek LLM 客户端
builder.Services.Configure<BotConfig>(builder.Configuration.GetSection("Bot"));
builder.Services.Configure<RainConfig>(builder.Configuration.GetSection("Rain"));

// ---------- 存储与配置 ----------
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<RuntimeConfig>();

// ---------- QQ 接入 ----------
builder.Services.AddSingleton<BotStatus>();
builder.Services.AddSingleton<BotIdentityResolver>();
builder.Services.AddScoped<QQBotService>();
builder.Services.AddHostedService<QQBotWebSocketService>();
builder.Services.AddSingleton<MessageDispatcher>();
builder.Services.AddSingleton<MessageQueue>();
builder.Services.AddSingleton<MessageProcessor>();

// ---------- 触发系统 ----------
builder.Services.AddSingleton<GroupStateManager>();
builder.Services.AddSingleton<PassiveTrigger>();
builder.Services.AddSingleton<ActiveTrigger>();
builder.Services.AddSingleton<SendQueue>();
builder.Services.AddHostedService<WarmupScheduler>();

// ---------- LLM 与上下文 ----------
builder.Services.AddSingleton<DeepSeekClient>();
builder.Services.AddSingleton<ReActLoop>();
builder.Services.AddSingleton<CacheMonitor>();
builder.Services.AddSingleton<PersonaLoader>();
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddSingleton<BlockComposer>();
builder.Services.AddSingleton<WatermarkManager>();
builder.Services.AddSingleton<Distiller>();
builder.Services.AddSingleton<WorkflowRunner>();

// ---------- 画像与工具 ----------
builder.Services.AddSingleton<AnchorManager>();
builder.Services.AddSingleton<ProfileRecaller>();
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<ISearchProvider, DuckDuckGoSearchProvider>();
builder.Services.AddSingleton<WebSearchTool>();
builder.Services.AddSingleton<ProfileTools>();
builder.Services.AddSingleton<AdminTools>();

// ---------- 风控与命令 ----------
builder.Services.AddSingleton<InputFilter>();
builder.Services.AddSingleton<OutputFilter>();
builder.Services.AddSingleton<CommandParser>();
builder.Services.AddSingleton<TopicAnalyzer>();

// ---------- 随机互动（原版 RainBOT 娱乐功能） ----------
builder.Services.AddSingleton<FunService>();

WebApplication app = builder.Build();

// ---------- 初始化（建库、加载配置覆盖、注册工具执行器） ----------
using (IServiceScope scope = app.Services.CreateScope())
{
    Database db = scope.ServiceProvider.GetRequiredService<Database>();
    await db.InitializeAsync();

    RuntimeConfig runtimeConfig = scope.ServiceProvider.GetRequiredService<RuntimeConfig>();
    await runtimeConfig.InitializeAsync();

    ToolRegistry registry = scope.ServiceProvider.GetRequiredService<ToolRegistry>();
    WebSearchTool searchTool = scope.ServiceProvider.GetRequiredService<WebSearchTool>();
    registry.RegisterExecutor("web_search", (args, _) =>
    {
        JsonObject? param = ToolRegistry.ParseArguments(args);
        string? query = param?["query"]?.GetValue<string>();
        string? topic = param?["topic"]?.GetValue<string>();
        return searchTool.SearchAsync(query ?? "", topic);
    });
    scope.ServiceProvider.GetRequiredService<ProfileTools>().Register(registry);
    scope.ServiceProvider.GetRequiredService<AdminTools>().Register(registry);

    ILogger<Program> logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("RainBot Agent 初始化完成，模型：{Model}", runtimeConfig.Config.Llm.Model);
}

// ---------- HTTP 端点（宝塔反代 / 健康检查 / OSM 图片静态托管） ----------
app.UseStaticFiles(); // wwwroot/ 下的图片（如 osm/osm.jpg）可通过 http://地址/osm/osm.jpg 访问

app.MapGet("/health", (BotStatus status, RuntimeConfig config) => Results.Json(new
{
    status = status.WebSocketConnected ? "ok" : "ws_disconnected",
    bot = "雨",
    model = config.Config.Llm.Model,
    wsConnected = status.WebSocketConnected,
    lastConnectedAt = status.LastConnectedAt,
    uptime = DateTimeOffset.UtcNow - appStartTime
}));

await app.RunAsync();
