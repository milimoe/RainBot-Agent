using System.Text.Json;
using System.Text.RegularExpressions;
using RainBot.Models;
using RainBot.Services.Config;
using RainBot.Services.QQ;
using RainBot.Services.Trigger;

namespace RainBot.Services.Fun;

/// <summary>
/// 随机互动（移植自原版 RainBOT，SC 系统不加入）：
/// 随机反驳是 / 随机反驳不 / 随机复读 / 随机OSM / 反向艾特 / 随机叫哥。
///
/// 优化点（对比原版）：
/// - 纯规则概率触发，不消耗 LLM Token；
/// - 一次消息最多响应一个 Fun（按优先级互斥），避免多重回复刷屏；
/// - 反驳不保留原版"烂梗 API"表现形式：命中时按概率拉一条弹幕烂梗代替词表，失败自动回退词表；
/// - 移除原版 SC 记分、控制台彩色日志；
/// - 概率/开关/延迟全部可热改（/admin set Fun.*），OSM 图片用 URL 列表配置。
/// </summary>
public class FunService(RuntimeConfig config, SendQueue sendQueue, QQBotService qqBotService, IHttpClientFactory httpClientFactory, ILogger<FunService> logger)
{
    private readonly RuntimeConfig _config = config;
    private readonly SendQueue _sendQueue = sendQueue;
    private readonly QQBotService _qqBotService = qqBotService;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<FunService> _logger = logger;

    private static readonly Regex AtTagRegex = new(@"<@![^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// 尝试响应随机互动。
    /// </summary>
    public async Task<FunResult> TryRespondAsync(IncomingMessage msg)
    {
        FunConfig fun = _config.Config.Fun;
        string clean = AtTagRegex.Replace(msg.Content, "").Trim();

        // 优先级互斥：一次消息最多命中一个（防刷屏，优于原版可能的多重回复）
        if (fun.EnableReplyYes && clean == "是" && Chance(fun.ReplyYesProbability))
        {
            return await ReplyTextAsync(msg, "是你的头");
        }

        if (fun.EnableReplyNo && TryReplyNo(clean, out string? noReply) && Chance(fun.ReplyNoProbability))
        {
            // 烂梗 API：反驳不的另一种随机表现形式（原版 30% 概率，失败自动回退词表）
            if (!string.IsNullOrWhiteSpace(fun.ReplyNoMemeUrl) && Chance(fun.ReplyNoMemeProbability))
            {
                string? meme = await FetchMemeAsync(fun.ReplyNoMemeUrl);
                if (!string.IsNullOrWhiteSpace(meme))
                {
                    return await ReplyTextAsync(msg, meme);
                }
            }
            return await ReplyTextAsync(msg, noReply);
        }

        if (fun.EnableReverseAt && msg.IsAtRobot
            && !IsIgnored(msg.SenderOpenId, fun.ReverseAtIgnoreOpenIds)
            && Chance(fun.ReverseAtProbability))
        {
            return await ReverseAtAsync(msg, clean);
        }

        if (fun.EnableOsm && fun.OsmImages.Count > 0 && Chance(fun.OsmProbability))
        {
            return await ReplyOsmAsync(msg);
        }

        if (fun.EnableRepeat && !fun.RepeatIgnoreWords.Any(clean.Contains) && Chance(fun.RepeatProbability))
        {
            return await ReplyRepeatAsync(msg, clean);
        }

        if (fun.EnableCallBrother && !IsIgnored(msg.SenderOpenId, fun.CallBrotherIgnoreOpenIds)
            && Chance(fun.CallBrotherProbability))
        {
            return await ReplyCallBrotherAsync(msg);
        }

        return FunResult.NotHandled;
    }

    // ---------- 各功能实现 ----------

    /// <summary>随机反驳是：消息恰好是「是」→ 概率反驳</summary>
    private async Task<FunResult> ReplyTextAsync(IncomingMessage msg, string content)
    {
        await _sendQueue.EnqueueAsync(new SendTask { GroupOpenId = msg.GroupOpenId, Content = content });
        LogHit(msg, content);
        return FunResult.HandledAndBlock;
    }

    /// <summary>随机反驳不：词表抬杠（不/没/是/别/太/可以/能/可能/要/想）</summary>
    private bool TryReplyNo(string text, out string? reply)
    {
        reply = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        int p;

        // 1. "不"后第一个字（后跟忽略词则不反驳）
        p = text.IndexOf("不", StringComparison.Ordinal);
        if (p >= 0 && p + 1 < text.Length && !SayNoWords.IgnoreTriggerAfterNo.Any(text[(p + 1)..].Contains))
        {
            reply = Format(SayNoWords.ReplyNoWords, text[p + 1]);
            return true;
        }
        // 2. "没"后第一个字
        p = text.IndexOf("没", StringComparison.Ordinal);
        if (p >= 0 && p + 1 < text.Length)
        {
            reply = Format(SayNoWords.SayDontHaveWords, text[p + 1]);
            return true;
        }
        // 3. "是"后跟 TriggerAfterYes 词才反驳
        p = text.IndexOf("是", StringComparison.Ordinal);
        if (p >= 0 && p + 1 < text.Length && SayNoWords.TriggerAfterYes.Any(text[(p + 1)..].Contains))
        {
            reply = Pick(SayNoWords.SayNotYesWords);
            return true;
        }
        // 4. "别"后第一个字（后跟 WillNotSayNo 不反驳）
        p = text.IndexOf("别", StringComparison.Ordinal);
        if (p >= 0 && p + 1 < text.Length && !SayNoWords.WillNotSayNo.Any(text[(p + 1)..].Contains))
        {
            reply = Format(SayNoWords.SayDontWords, text[p + 1]);
            return true;
        }
        // 5. "太X了"
        p = text.IndexOf("太", StringComparison.Ordinal);
        if (p >= 0 && p + 2 < text.Length && text[p + 2] == '了')
        {
            reply = Pick(SayNoWords.SaySpecialNoWords) + text[p + 1];
            return true;
        }
        // 6. 可以 / 可能 / 能（半对半反驳）
        if (text.Contains("可以", StringComparison.Ordinal) && !text.Contains('不'))
        {
            reply = Random.Shared.Next(2) == 0 ? "可以" : "不可以";
            return true;
        }
        if (text.Contains("可能", StringComparison.Ordinal) && !text.Contains('不'))
        {
            reply = Random.Shared.Next(2) == 0 ? "可能" : "不可能";
            return true;
        }
        if (text.Contains("能", StringComparison.Ordinal) && !text.Contains('不'))
        {
            reply = Random.Shared.Next(2) == 0 ? "能" : "不能";
            return true;
        }
        // 7. 要 / 想
        if (text.Contains("要", StringComparison.Ordinal) && !text.Contains('不'))
        {
            reply = Pick(SayNoWords.SayWantWords);
            return true;
        }
        if (text.Contains("想", StringComparison.Ordinal) && !text.Contains('不'))
        {
            reply = Pick(SayNoWords.SayThinkWords);
            return true;
        }
        return false;
    }

    /// <summary>反向艾特：@ 机器人 → 把 @ 弹回发送者（不阻断后续 AI 回复）</summary>
    private async Task<FunResult> ReverseAtAsync(IncomingMessage msg, string clean)
    {
        string content = string.IsNullOrEmpty(clean) ? "👀" : $"{clean}";
        await _sendQueue.EnqueueAsync(new SendTask
        {
            GroupOpenId = msg.GroupOpenId,
            Content = $"<@!{msg.SenderOpenId}> {content}"
        });
        LogHit(msg, "反向艾特");
        return FunResult.HandledContinue; // 继续被动触发（LLM 回复）
    }

    /// <summary>随机 OSM：上传图片并发送（图片 URL 空则该功能已禁用）</summary>
    private async Task<FunResult> ReplyOsmAsync(IncomingMessage msg)
    {
        FunConfig fun = _config.Config.Fun;
        string url = fun.OsmImages[Random.Shared.Next(fun.OsmImages.Count)];
        try
        {
            UploadMediaResult upload = await _qqBotService.UploadGroupMediaAsync(msg.GroupOpenId, 1, url);
            if (upload.IsSuccess && !string.IsNullOrEmpty(upload.FileInfo))
            {
                await _qqBotService.SendGroupImageAsync(msg.GroupOpenId, upload.FileInfo);
                LogHit(msg, "随机OSM");
                return FunResult.HandledAndBlock;
            }
            logger.LogWarning("OSM 图片上传失败：{Error}", upload.Error);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OSM 图片发送异常");
        }
        return FunResult.NotHandled;
    }

    /// <summary>随机复读：延迟后原样复读（50% 概率加 desuwa～）</summary>
    private async Task<FunResult> ReplyRepeatAsync(IncomingMessage msg, string clean)
    {
        FunConfig fun = _config.Config.Fun;
        if (string.IsNullOrEmpty(clean))
        {
            return FunResult.NotHandled;
        }
        int delay = fun.RepeatDelayMinSeconds + Random.Shared.Next(fun.RepeatDelayMaxSeconds - fun.RepeatDelayMinSeconds + 1);
        string content = Random.Shared.Next(2) == 0 ? clean : clean + "desuwa～";
        await _sendQueue.EnqueueAsync(new SendTask
        {
            GroupOpenId = msg.GroupOpenId,
            Content = content,
            DelaySeconds = delay
        });
        LogHit(msg, $"随机复读（延迟 {delay}s）");
        return FunResult.HandledAndBlock;
    }

    /// <summary>随机叫哥：@ 发送者 + 名字随机截取 + 随机后缀</summary>
    private async Task<FunResult> ReplyCallBrotherAsync(IncomingMessage msg)
    {
        FunConfig fun = _config.Config.Fun;
        string name = (msg.Username ?? "").Trim();
        if (name.Length == 0)
        {
            name = "友"; // 官方 API 拿不到昵称时的兜底
        }
        int pos = name.Length > 1 ? Random.Shared.Next(name.Length - 1) : 0;
        string suffix = Random.Shared.Next(6) switch
        {
            0 => name[pos..Math.Min(name.Length, pos + 2)] + "哥",
            1 => name[pos..Math.Min(name.Length, pos + 2)] + "姐",
            2 => name[pos..Math.Min(name.Length, pos + 1)] + "圣",
            3 => name[pos..Math.Min(name.Length, pos + 1)] + "亲",
            4 => name[pos..Math.Min(name.Length, pos + 2)] + "亲",
            _ => "哈基" + name[pos..Math.Min(name.Length, pos + 1)],
        };
        await _sendQueue.EnqueueAsync(new SendTask
        {
            GroupOpenId = msg.GroupOpenId,
            Content = $"<@!{msg.SenderOpenId}> {suffix}",
            DelaySeconds = fun.CallBrotherDelaySeconds
        });
        LogHit(msg, $"随机叫哥（{suffix}）");
        return FunResult.HandledAndBlock;
    }

    // ---------- 工具 ----------

    /// <summary>
    /// 拉取一条弹幕烂梗（原版 OpenGetRequest.GetMeme：响应 JSON 的 data.barrage）。
    /// 网络异常/解析失败返回 null（调用方回退词表），绝不抛出。
    /// </summary>
    private async Task<string?> FetchMemeAsync(string url)
    {
        try
        {
            using HttpClient client = _httpClientFactory.CreateClient("meme");
            using HttpResponseMessage response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("烂梗 API 返回 {Status}", response.StatusCode);
                return null;
            }
            string body = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out JsonElement data)
                && data.TryGetProperty("barrage", out JsonElement barrage))
            {
                string? meme = barrage.GetString();
                if (!string.IsNullOrWhiteSpace(meme))
                {
                    return meme.Trim();
                }
            }
            _logger.LogWarning("烂梗 API 响应格式异常：{Body}", body.Length > 200 ? body[..200] + "…" : body);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "烂梗 API 请求失败，回退词表反驳");
            return null;
        }
    }

    /// <summary>概率判定：percent=100 必中，0 永不</summary>
    private static bool Chance(int percent) => percent > 0 && Random.Shared.Next(100) < percent;

    private static bool IsIgnored(string openId, List<string> ignoreList)
    {
        if (ignoreList.Count == 0)
        {
            return false;
        }
        string shortId = openId.Length > 8 ? openId[..8] : openId;
        return ignoreList.Any(i => i == openId || i == shortId);
    }

    private static string Pick(string[] words) => words[Random.Shared.Next(words.Length)];

    private static string Format(string[] templates, char arg) => string.Format(Pick(templates), arg);

    private void LogHit(IncomingMessage msg, string feature)
    {
        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("群 {Group} 触发「{Feature}」（user={User}）", msg.GroupOpenId, feature, msg.SenderOpenId);
    }
}

/// <summary>Fun 响应结果</summary>
public class FunResult
{
    /// <summary>是否已响应（已发送消息）</summary>
    public bool Handled { get; init; }

    /// <summary>是否阻断后续流程（true=不再走被动触发/LLM）</summary>
    public bool Blocked { get; init; }

    public static FunResult NotHandled => new();
    public static FunResult HandledAndBlock => new() { Handled = true, Blocked = true };
    public static FunResult HandledContinue => new() { Handled = true, Blocked = false };
}
