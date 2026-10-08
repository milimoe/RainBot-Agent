namespace RainBot.Services.Llm;

/// <summary>
/// 调试模式专用：按群记录最后一次模型思维链（reasoning_content）的内存快照。
/// 推理型模型的思考计入输出 token——content 为空而思维链写满预算时，
/// 用 /admin reasoning 查看最后的思考内容，即可定位「ReAct 循环无文本输出」的原因。
/// 仅存内存，重启即清空；不写库、不进上下文。
/// </summary>
public class ReasoningRecorder
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ReasoningSnapshot> _lastByGroup = [];

    /// <summary>记录一次思维链（同群覆盖上一次）</summary>
    public void Record(ReasoningSnapshot snapshot)
    {
        lock (_lock)
        {
            _lastByGroup[snapshot.GroupOpenId] = snapshot;
        }
    }

    /// <summary>读取某群最后一次记录；null = 尚无记录</summary>
    public ReasoningSnapshot? GetLast(string groupOpenId)
    {
        lock (_lock)
        {
            return _lastByGroup.TryGetValue(groupOpenId, out ReasoningSnapshot? snapshot) ? snapshot : null;
        }
    }
}

/// <summary>思维链快照</summary>
public class ReasoningSnapshot
{
    public required DateTimeOffset TimeUtc { get; init; }
    public required string GroupOpenId { get; init; }

    /// <summary>最后一次请求的结束原因（stop / length / tool_calls）</summary>
    public required string? FinishReason { get; init; }

    /// <summary>最后一次请求的输出 token 数（含思维链）</summary>
    public required int CompletionTokens { get; init; }
    public required string Text { get; init; }
}
