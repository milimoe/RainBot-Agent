using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.Tools;

namespace RainBot.Services.Mcp;

/// <summary>
/// MCP 工具管理器：启动时连接各 MCP server（官方 ModelContextProtocol.Core SDK），
/// tools/list 后经 McpSchemaConverter 转换为 OpenAI function 格式注册进 ToolRegistry，
/// 工具名统一为 mcp__{server}__{tool}。
///
/// 关键约束（保 Block B 缓存前缀稳定）：
/// - 启动时一次性加载，工具按（server 名, 工具名）确定性排序注册；
/// - 单个 server 连接失败只告警跳过，不阻塞启动；
/// - 改配置需重启生效，不支持运行中热更新。
/// </summary>
public class McpClientManager : IAsyncDisposable
{
    private readonly RuntimeConfig _config;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpClientManager> _logger;
    private readonly List<McpServerSession> _sessions = [];
    private readonly ConcurrentDictionary<string, McpClientTool> _toolsByFullName = new();

    private const int DefaultTimeoutSeconds = 60;

    public McpClientManager(RuntimeConfig config, ToolRegistry toolRegistry, ILoggerFactory loggerFactory, ILogger<McpClientManager> logger)
    {
        _config = config;
        _toolRegistry = toolRegistry;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>各 server 连接状态（供 /health 与 WebUI 透出）</summary>
    public IReadOnlyList<McpServerStatus> Status { get; private set; } = [];

    /// <summary>已注册的 MCP 工具总数</summary>
    public int ToolCount => _toolsByFullName.Count;

    /// <summary>启动时连接全部 server 并注册工具（失败跳过不阻塞）</summary>
    public async Task InitializeAsync()
    {
        McpConfig cfg = _config.Config.Mcp;
        if (!cfg.Enabled)
        {
            _logger.LogInformation("MCP 工具已禁用（Rain:Mcp:Enabled=false）");
            return;
        }
        if (cfg.Servers.Count == 0)
        {
            _logger.LogInformation("MCP 未配置任何 server，跳过工具注册");
            return;
        }

        List<McpServerStatus> statuses = [];
        foreach (McpServerConfig server in cfg.Servers)
        {
            try
            {
                int count = await ConnectServerAsync(server);
                statuses.Add(new McpServerStatus(server.Name, true, count, null));
                _logger.LogInformation("MCP server {Server} 连接成功，注册 {Count} 个工具（前缀 mcp__{Server}__）", server.Name, count, server.Name);
            }
            catch (Exception ex)
            {
                statuses.Add(new McpServerStatus(server.Name, false, 0, ex.Message));
                _logger.LogWarning(ex, "MCP server {Server} 连接失败，跳过（不影响启动）：{Error}", server.Name, ex.Message);
            }
        }
        Status = statuses;
        _logger.LogInformation("MCP 工具初始化完成：{Total} 个工具来自 {Servers} 个 server", _toolsByFullName.Count, Status.Count(s => s.Connected));
    }

    private async Task<int> ConnectServerAsync(McpServerConfig server)
    {
        IClientTransport transport = CreateTransport(server);
        McpClient client = await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory);
        IList<McpClientTool> tools = await client.ListToolsAsync();

        McpServerSession session = new(server.Name, client);
        _sessions.Add(session);

        // 确定性排序：按工具名排序后注册（server 顺序 = 配置顺序）
        int registered = 0;
        foreach (McpClientTool tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            string fullName = McpSchemaConverter.MakeToolName(server.Name, tool.Name);
            if (!_toolsByFullName.TryAdd(fullName, tool))
            {
                _logger.LogWarning("MCP 工具名冲突已跳过：{FullName}（检查 server 名称是否重复）", fullName);
                continue;
            }
            JsonObject? parameters = McpSchemaConverter.ToOpenAiParameters(tool.JsonSchema);
            _toolRegistry.RegisterExternal(fullName, tool.Description, parameters ?? new JsonObject());
            string capturedName = fullName;
            int timeout = server.TimeoutSeconds;
            _toolRegistry.RegisterExecutor(capturedName, (argsJson, _) => CallToolAsync(capturedName, argsJson, timeout));
            registered++;
        }
        return registered;
    }

    private IClientTransport CreateTransport(McpServerConfig server)
    {
        string transport = server.Transport.Trim().ToLowerInvariant();
        switch (transport)
        {
            case "http":
            case "streamable-http":
            case "sse":
                if (string.IsNullOrWhiteSpace(server.Url))
                {
                    throw new InvalidOperationException("http 传输需要配置 Url");
                }
                HttpClientTransportOptions httpOptions = new() { Endpoint = new Uri(server.Url) };
                if (server.Headers.Count > 0)
                {
                    httpOptions.AdditionalHeaders = server.Headers;
                }
                return new HttpClientTransport(httpOptions);
            case "stdio":
            default:
                if (string.IsNullOrWhiteSpace(server.Command))
                {
                    throw new InvalidOperationException("stdio 传输需要配置 Command");
                }
                return new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = server.Name,
                    Command = server.Command,
                    Arguments = server.Arguments.ToArray()
                });
        }
    }

    /// <summary>执行 MCP 工具调用（由 ToolRegistry 执行器委托）</summary>
    private async Task<string> CallToolAsync(string fullName, string argumentsJson, int timeoutSeconds)
    {
        if (!_toolsByFullName.TryGetValue(fullName, out McpClientTool? tool))
        {
            return $"未知 MCP 工具：{fullName}";
        }

        Dictionary<string, object?> args = ParseArguments(argumentsJson);
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds));
        try
        {
            CallToolResult result = await tool.CallAsync(args, cancellationToken: cts.Token);
            return FormatToolResult(result);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("MCP 工具 {Tool} 调用超时（{Timeout}s）", fullName, timeoutSeconds);
            return $"工具 {fullName} 调用超时（{timeoutSeconds} 秒）";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP 工具 {Tool} 调用异常", fullName);
            return $"工具 {fullName} 调用失败：{ex.Message}";
        }
    }

    /// <summary>解析模型传入的 JSON 参数为字典（JsonElement 克隆保证生命周期安全）</summary>
    private static Dictionary<string, object?> ParseArguments(string argumentsJson)
    {
        Dictionary<string, object?> result = [];
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return result;
        }
        using JsonDocument doc = JsonDocument.Parse(argumentsJson);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.Clone();
            }
        }
        return result;
    }

    /// <summary>把 CallToolResult 内容块序列化为给模型看的一段文本</summary>
    internal static string FormatToolResult(CallToolResult result)
    {
        StringBuilder sb = new();
        if (result.IsError == true)
        {
            sb.AppendLine("[工具执行错误]");
        }
        foreach (ContentBlock block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock textBlock:
                    sb.AppendLine(textBlock.Text);
                    break;
                case ImageContentBlock img:
                    sb.AppendLine($"[图片 {img.MimeType} base64 {img.Data.Length} 字节]");
                    break;
                case AudioContentBlock audio:
                    sb.AppendLine($"[音频 {audio.MimeType}]");
                    break;
                case EmbeddedResourceBlock res:
                    sb.AppendLine(JsonSerializer.Serialize(res.Resource));
                    break;
                default:
                    sb.AppendLine(JsonSerializer.Serialize(block, block.GetType()));
                    break;
            }
        }
        if (result.StructuredContent is { ValueKind: not JsonValueKind.Undefined } structured)
        {
            sb.AppendLine(JsonSerializer.Serialize(structured));
        }
        string text = sb.ToString().Trim();
        return text.Length == 0 ? "(工具无返回内容)" : text;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (McpServerSession session in _sessions)
        {
            try
            {
                await session.Client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "释放 MCP server {Server} 连接异常", session.ServerName);
            }
        }
        _sessions.Clear();
    }

    private sealed record McpServerSession(string ServerName, McpClient Client);
}

/// <summary>MCP server 连接状态（供 /health、日志透出）</summary>
public record McpServerStatus(string Name, bool Connected, int ToolCount, string? Error);
