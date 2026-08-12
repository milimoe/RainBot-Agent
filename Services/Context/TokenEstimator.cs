namespace RainBot.Services.Context;

/// <summary>
/// 轻量 token 估算（仅用于水位预算，不追求精确；估算值偏保守）。
/// 中文按 ~0.8 token/字，ASCII 按 ~4 字符/token，每条消息加固定开销。
/// </summary>
public static class TokenEstimator
{
    /// <summary>单条消息固定开销（角色标记、换行等）</summary>
    public const int PerMessageOverhead = 8;

    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        int cjk = text.Count(c => c > 0x2E80);
        int ascii = text.Length - cjk;
        return (int)Math.Ceiling(cjk * 0.8 + ascii / 4.0) + 4;
    }

    public static int EstimateMessage(string content) => Estimate(content) + PerMessageOverhead;
}
