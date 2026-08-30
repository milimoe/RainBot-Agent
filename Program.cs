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
using RainBot.Services.WebUi;
using RainBot.Services.Mcp;
using RainBot.Services.Bots;
using RainBot.Services.OneBot;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
DateTimeOffset appStartTime = DateTimeOffset.UtcNow;

// ---------- 基础服务 ----------
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("duckduckgo"); // DuckDuckGo 搜索客户端
builder.Services.AddHttpClient("deepseek");   // DeepSeek LLM 客户端
builder.Services.Configure<RainConfig>(builder.Configuration.GetSection("Rain"));
builder.Services.Configure<WebUiOptions>(builder.Configuration.GetSection("Rain:WebUi"));

// ---------- 存储与配置 ----------
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<RuntimeConfig>();
builder.Services.AddSingleton<BotConfigService>(); // QQ 网关凭据（WebUI 维护，修改即断线重连）

// ---------- 机器人实例与平台抽象 ----------
builder.Services.AddSingleton<BotConnectionRegistry>();
builder.Services.AddSingleton<BotInstanceStore>();
builder.Services.AddSingleton<QqOfficialSender>();
builder.Services.AddSingleton<OneBotManager>();
builder.Services.AddSingleton<OneBotSender>();
builder.Services.AddSingleton<BotSenderRouter>();
builder.Services.AddSingleton<BotSendStats>(); // 发送成功/失败统计（内存计数 + 落库）

// ---------- QQ 接入 ----------
builder.Services.AddSingleton<BotStatus>();
builder.Services.AddSingleton<BotIdentityResolver>();
builder.Services.AddSingleton<QQBotService>(); // 无状态（HttpClient/MemoryCache 线程安全），被 singleton 的 SendQueue/FunService 消费，必须 singleton
builder.Services.AddHostedService<QqGatewayService>(); // 每个启用的 QqOfficial 实例一条 WS 连接
builder.Services.AddSingleton<MessageDispatcher>();
builder.Services.AddSingleton<MessageQueue>();
builder.Services.AddHostedService(static sp => sp.GetRequiredService<MessageQueue>()); // 消息队列消费者（必须宿主化才会消费）
builder.Services.AddSingleton<MessageProcessor>();

// ---------- OneBot11 接入 ----------
builder.Services.AddHostedService<OneBotWsForwardService>(); // WS 正向；HTTP 上报与 WS 反向端点在下方 Map 注册

// ---------- 触发系统 ----------
builder.Services.AddSingleton<GroupStateManager>();
builder.Services.AddSingleton<PassiveTrigger>();
builder.Services.AddSingleton<ActiveTrigger>();
builder.Services.AddSingleton<SendQueue>();
builder.Services.AddHostedService(static sp => sp.GetRequiredService<SendQueue>()); // 发送队列消费者（必须宿主化才会真正发送）
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
builder.Services.AddSingleton<SayNoWordsService>();
builder.Services.AddSingleton<OsmImageCatalog>(); // wwwroot/osm 目录扫描 + 公网域名拼接
builder.Services.AddSingleton<FunService>();

// ---------- MCP 工具（Model Context Protocol） ----------
builder.Services.AddSingleton<ToolCallRecorder>(); // 工具调用记录（WebUI 日志面板「工具调用」视图）
builder.Services.AddSingleton<McpClientManager>();

// ---------- WebUI 控制台 ----------
builder.Services.AddSingleton<WebUiLogProvider>(); // 捕获 ILogger 输出到日志页（与日志管线同源）
builder.Services.AddSingleton<ILoggerProvider>(static sp => sp.GetRequiredService<WebUiLogProvider>());
builder.Services.AddSingleton<WebUiEventBus>();
builder.Services.AddSingleton<WebUiBridge>();
builder.Services.AddSingleton<DeepSeekBalanceService>();

WebApplication app = builder.Build();

// ---------- 初始化（建库、加载配置覆盖、注册工具执行器） ----------
using (IServiceScope scope = app.Services.CreateScope())
{
    Database db = scope.ServiceProvider.GetRequiredService<Database>();
    await db.InitializeAsync();

    RuntimeConfig runtimeConfig = scope.ServiceProvider.GetRequiredService<RuntimeConfig>();
    await runtimeConfig.InitializeAsync();

    // 加载 QQ 网关凭据覆盖（WebUI 维护的 AppID/Secret 存于 settings 表）
    await scope.ServiceProvider.GetRequiredService<BotConfigService>().InitializeAsync();

    // 机器人实例注册表（DB 优先；首次运行从配置/旧凭据播种）
    await scope.ServiceProvider.GetRequiredService<BotInstanceStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<BotSendStats>().InitializeAsync();

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

    // 启动即加载 SayNo 词表（首次运行自动生成默认 sayno.json，便于用户直接编辑）
    _ = scope.ServiceProvider.GetRequiredService<SayNoWordsService>().Current;

    // MCP 工具：连接 server 并注册工具（单个 server 失败跳过，不阻塞启动）
    await scope.ServiceProvider.GetRequiredService<McpClientManager>().InitializeAsync();

    ILogger<Program> logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("RainBot Agent 初始化完成，模型：{Model}", runtimeConfig.Config.Llm.Model);
    logger.LogInformation("WebUI 控制台：http://localhost:8080/webui/（Rain:WebUi:Token 为空则内网免鉴权）");
}

// ---------- HTTP 端点（宝塔反代 / 健康检查 / OSM 图片静态托管 / WebUI 控制台） ----------
app.UseDefaultFiles(); // 目录请求默认返回 index.html（/webui/ → wwwroot/webui/index.html）
app.UseStaticFiles(); // wwwroot/ 下的图片（如 osm/osm.jpg）可通过 http://地址/osm/osm.jpg 访问

// WebUI 控制台（React + Tailwind 构建产物在 wwwroot/webui/）
app.MapWebUiEndpoints();

// OneBot11 接入端点：HTTP 上报 /onebot/v11/event/{实例Id} 与 WS 反向 /onebot/v11/ws/{实例Id}
OneBotGateway.MapEndpoints(app);

app.MapGet("/health", (BotStatus status, RuntimeConfig config, McpClientManager mcp, BotInstanceStore bots, BotConnectionRegistry registry, BotSendStats stats) => Results.Json(new
{
    status = status.WebSocketConnected ? "ok" : "ws_disconnected",
    bot = "雨",
    model = config.Config.Llm.Model,
    wsConnected = status.WebSocketConnected,
    lastConnectedAt = status.LastConnectedAt,
    uptime = DateTimeOffset.UtcNow - appStartTime,
    uptimeSeconds = (DateTimeOffset.UtcNow - appStartTime).TotalSeconds,
    mcp = new
    {
        enabled = config.Config.Mcp.Enabled,
        toolCount = mcp.ToolCount,
        servers = mcp.Status.Select(s => new { s.Name, s.Connected, s.ToolCount, s.Error })
    },
    bots = registry.Summarize(bots.All),
    sendStats = stats.Snapshot()
}));

await app.RunAsync();
