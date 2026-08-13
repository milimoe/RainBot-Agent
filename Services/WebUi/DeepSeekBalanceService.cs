using RainBot.Services.Llm;

namespace RainBot.Services.WebUi;

/// <summary>一次余额查询结果（含缓存时间与错误信息）</summary>
public sealed record BalanceResult(DeepSeekBalance? Balance, DateTimeOffset? FetchedAt, string? Error);

/// <summary>
/// DeepSeek 余额缓存服务：状态页首次打开时查询一次并缓存，
/// 之后复用缓存；刷新按钮（forceRefresh）强制重新查询。
/// 查询失败时返回错误信息（附上次成功结果，如有）。
/// </summary>
public class DeepSeekBalanceService(DeepSeekClient client, ILogger<DeepSeekBalanceService> logger)
{
    private readonly DeepSeekClient _client = client;
    private readonly ILogger<DeepSeekBalanceService> _logger = logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DeepSeekBalance? _cached;
    private DateTimeOffset? _fetchedAt;
    private string? _lastError;

    /// <summary>获取余额；forceRefresh=true 时忽略缓存强制查询</summary>
    public async Task<BalanceResult> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cached != null)
        {
            return new BalanceResult(_cached, _fetchedAt, null);
        }

        await _lock.WaitAsync(ct);
        try
        {
            // 双检：等待锁期间可能已被其它请求填充
            if (!forceRefresh && _cached != null)
            {
                return new BalanceResult(_cached, _fetchedAt, null);
            }
            try
            {
                DeepSeekBalance balance = await _client.GetBalanceAsync(ct);
                _cached = balance;
                _fetchedAt = DateTimeOffset.UtcNow;
                _lastError = null;
                _logger.LogInformation("DeepSeek 余额查询成功：{Currency} {Total}（可用：{Available}）",
                    balance.Currency, balance.TotalBalance, balance.Available);
                return new BalanceResult(balance, _fetchedAt, null);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning("DeepSeek 余额查询失败：{Error}", ex.Message);
                return new BalanceResult(_cached, _fetchedAt, ex.Message);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
