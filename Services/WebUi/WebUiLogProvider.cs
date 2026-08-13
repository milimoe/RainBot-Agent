using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RainBot.Services.WebUi;

/// <summary>一条被 WebUI 日志页捕获的日志</summary>
public sealed record WebUiLogEntry(long Seq, DateTimeOffset Time, string Level, string Category, string Message, string? Exception);

/// <summary>
/// WebUI 日志捕获器：以 ILoggerProvider 注册进日志管线（与 ILogger 输出同源），
/// 全部类别/级别的日志写入内存环形缓冲（2000 条），供「日志」页轮询查看。
/// 捕获范围遵循应用自身的日志级别规则（appsettings Logging 段）。
/// </summary>
public class WebUiLogProvider : ILoggerProvider
{
    private const int Capacity = 2000;
    private readonly ConcurrentQueue<WebUiLogEntry> _buffer = new();
    private long _seq;

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    /// <summary>当前最大序号（前端轮询游标）</summary>
    public long LastSeq => Interlocked.Read(ref _seq);

    /// <summary>取 seq 大于 after 的日志（最多 limit 条，时间正序）</summary>
    public IReadOnlyList<WebUiLogEntry> GetEntries(long after, int limit)
    {
        List<WebUiLogEntry> result = [];
        foreach (WebUiLogEntry entry in _buffer)
        {
            if (entry.Seq > after)
            {
                result.Add(entry);
            }
        }
        return result.TakeLast(limit).ToList();
    }

    public void Dispose()
    {
        // 进程级单例，无需释放
    }

    private void Add(LogLevel level, string category, string message, Exception? exception)
    {
        WebUiLogEntry entry = new(
            Interlocked.Increment(ref _seq),
            DateTimeOffset.UtcNow,
            LevelName(level),
            category,
            message,
            exception?.ToString());
        _buffer.Enqueue(entry);
        while (_buffer.Count > Capacity)
        {
            _buffer.TryDequeue(out _);
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Info",
        LogLevel.Warning => "Warn",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Critical",
        _ => "None"
    };

    /// <summary>捕获器：把每条日志写入环形缓冲（不调用任何日志 API，避免递归）</summary>
    private sealed class Sink(WebUiLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Add(logLevel, category, formatter(state, exception), exception);
    }
}
