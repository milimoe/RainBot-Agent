using System.Text;
using System.Text.Json;

namespace RainBot.Services.QQ;

/// <summary>临时协议诊断：完整记录入站消息 data，不记录鉴权、心跳或 READY 会话信息。</summary>
public sealed class QqEventCapture : IDisposable
{
    private readonly bool _enabled;
    private readonly string _directory;
    private readonly ILogger<QqEventCapture> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _session = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
    private int _part;
    private long _bytes;
    private const long MaxFileBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public QqEventCapture(IConfiguration configuration, IHostEnvironment environment, ILogger<QqEventCapture> logger)
        : this(configuration.GetValue<bool>("Rain:Diagnostics:CaptureQqMessages"),
            Path.Combine(environment.ContentRootPath, "Logs", "qq-events"), logger) { }

    internal QqEventCapture(bool enabled, string directory, ILogger<QqEventCapture> logger)
    {
        _enabled = enabled;
        _directory = directory;
        _logger = logger;
        if (enabled) logger.LogInformation("官 Q 完整消息采集已启用，日志目录：{Directory}", directory);
    }

    public async Task RecordAsync(string botId, string eventType, JsonElement data)
    {
        if (!_enabled || eventType is not ("GROUP_MESSAGE_CREATE" or "GROUP_AT_MESSAGE_CREATE" or "C2C_MESSAGE_CREATE")) return;
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_directory);
            string line = JsonSerializer.Serialize(new { capturedAt = DateTimeOffset.UtcNow, botId, eventType, data }, JsonOptions) + "\n";
            long size = Encoding.UTF8.GetByteCount(line);
            if (_bytes > 0 && _bytes + size > MaxFileBytes) { _part++; _bytes = 0; }
            string path = Path.Combine(_directory, $"qq-{_session}-{_part:D3}.jsonl");
            // 每条完整写入并关闭文件，即使意外停止，也能读取已采集的消息。
            await File.AppendAllTextAsync(path, line, new UTF8Encoding(false));
            _bytes += size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("官 Q 消息采集写入失败：{ErrorType}", ex.GetType().Name);
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
