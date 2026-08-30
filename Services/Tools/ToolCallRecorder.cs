using System.Collections.Concurrent;

namespace RainBot.Services.Tools;

/// <summary>一条工具调用记录（内置与 MCP 工具统一）</summary>
public class ToolCallRecord
{
    /// <summary>自增序号（前端增量拉取与排序用）</summary>
    public long Seq { get; init; }

    /// <summary>调用时间（本地）</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>工具名（MCP 工具为 mcp__{server}__{tool}）</summary>
    public required string Tool { get; init; }

    /// <summary>模型传入的参数 JSON（截断）</summary>
    public string? Arguments { get; init; }

    /// <summary>工具返回内容（截断）</summary>
    public string? Result { get; init; }

    /// <summary>耗时（毫秒）</summary>
    public long ElapsedMs { get; init; }

    /// <summary>是否执行成功（按已知错误标记启发式判定）</summary>
    public bool Success { get; init; }
}

/// <summary>
/// 工具调用记录器：内存环形缓冲（最近 MaxRecords 条），
/// 供 WebUI 日志面板「工具调用」视图展示（MCP 工具调用结果进统计与日志）。
/// 纯内存、不落库，重启清零。
/// </summary>
public class ToolCallRecorder
{
    /// <summary>最多保留的记录条数</summary>
    internal const int MaxRecords = 200;

    /// <summary>结果/参数截断长度（防大结果撑爆内存与前端）</summary>
    internal const int MaxTextLength = 600;

    private readonly ConcurrentQueue<ToolCallRecord> _records = new();
    private long _seq;

    /// <summary>当前记录条数</summary>
    public int Count => _records.Count;

    /// <summary>记录一次工具调用（供 ToolRegistry.ExecuteAsync 埋点）</summary>
    public void Record(string tool, string? argumentsJson, string result, long elapsedMs)
    {
        if (string.IsNullOrEmpty(tool))
        {
            return;
        }
        long seq = Interlocked.Increment(ref _seq);
        ToolCallRecord record = new()
        {
            Seq = seq,
            Time = DateTimeOffset.Now,
            Tool = tool,
            Arguments = Truncate(argumentsJson),
            Result = Truncate(result),
            ElapsedMs = elapsedMs,
            Success = !LooksLikeError(result)
        };
        _records.Enqueue(record);
        while (_records.Count > MaxRecords && _records.TryDequeue(out _))
        {
        }
    }

    /// <summary>按序号倒序取最近记录（limit 限制条数）</summary>
    public IReadOnlyList<ToolCallRecord> Latest(int limit = 100)
    {
        IEnumerable<ToolCallRecord> query = _records.OrderByDescending(r => r.Seq);
        return (limit > 0 ? query.Take(limit) : query).ToList();
    }

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        return text.Length <= MaxTextLength ? text : text[..MaxTextLength] + "…";
    }

    /// <summary>
    /// 启发式判定工具返回是否为错误：与 ToolRegistry / McpClientManager 的已知错误文案对齐。
    /// </summary>
    internal static bool LooksLikeError(string? result)
    {
        if (string.IsNullOrEmpty(result))
        {
            return false;
        }
        return result.StartsWith("[工具执行错误]", StringComparison.Ordinal)
            || result.StartsWith("未知工具：", StringComparison.Ordinal)
            || result.StartsWith("未知 MCP 工具：", StringComparison.Ordinal)
            || result.StartsWith("工具参数解析失败：", StringComparison.Ordinal)
            || result.StartsWith("工具执行异常：", StringComparison.Ordinal)
            || result.Contains("调用超时（", StringComparison.Ordinal)
            || result.Contains("调用失败：", StringComparison.Ordinal);
    }
}
