using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Commands;
using RainBot.Services.Config;
using RainBot.Services.Context;
using RainBot.Services.Fun;
using RainBot.Services.Llm;
using RainBot.Services.OneBot;
using RainBot.Services.Profile;
using RainBot.Services.QQ;
using RainBot.Services.Safety;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;
using RainBot.Services.Workflow;
using Xunit;

namespace RainBot.Tests;

/// <summary>
/// 图片消息识图链路：
/// 附件解析（QQ 官方 attachments / OneBot image 段）→ 纯图片消息可触发插嘴 →
/// 图片下载到内存并 base64 内联（不落盘、无需清理）→ DeepSeek 视觉格式 content 块数组 →
/// 历史里以 [图片] 占位（图片本体不进上下文）。
/// </summary>
public class VisionImageTests
{
    private const string Group = "group_vision";
    private const string Sender = "member_vision";
    private const string ImageUrl = "https://multimedia.nt.qq.com.cn/download?appid=1407&fileid=abc";

    /// <summary>最小合法 JPEG 头（FFD8FF）+ 尾（FFD9），仅用于格式嗅探</summary>
    private static readonly byte[] FakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0xFF, 0xD9];

    private static string ExpectedDataUrl => "data:image/jpeg;base64," + Convert.ToBase64String(FakeJpeg);

    private static string GroupImagePayload(string msgId, string contentType = "image/jpeg", string? content = "")
        => $$"""{"id":"{{msgId}}","author":{"member_openid":"{{Sender}}","username":"小明"},"content":"{{content}}","group_openid":"{{Group}}","timestamp":"2026-01-01T00:00:00+00:00","msg_seq":1,"attachments":[{"content_type":"{{contentType}}","filename":"a.jpg","url":"{{ImageUrl}}","width":100,"height":100,"size":1234}]}""";

    // ---------- 附件解析 ----------

    [Fact]
    public async Task QQ官方_图片附件_解析进ImageUrls()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await BuildDispatcherAsync();

        await dispatcher.HandleDispatchAsync(Database.LegacyBotId, "GROUP_MESSAGE_CREATE",
            JsonDocument.Parse(GroupImagePayload("msg_img_full")).RootElement);
        await dispatcher.HandleDispatchAsync(Database.LegacyBotId, "GROUP_AT_MESSAGE_CREATE",
            JsonDocument.Parse(GroupImagePayload("msg_img_at")).RootElement);

        List<IncomingMessage> messages = queue.DrainForTest();
        Assert.Equal(2, messages.Count);
        Assert.Equal([ImageUrl], messages[0].ImageUrls);
        Assert.Equal([ImageUrl], messages[1].ImageUrls);
        Assert.Equal("[图片]", messages[0].DisplayContent); // 无文字 → 占位
    }

    [Fact]
    public async Task QQ官方_视频附件_不进ImageUrls()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await BuildDispatcherAsync();

        await dispatcher.HandleDispatchAsync(Database.LegacyBotId, "GROUP_MESSAGE_CREATE",
            JsonDocument.Parse(GroupImagePayload("msg_video", contentType: "video/mp4")).RootElement);

        List<IncomingMessage> messages = queue.DrainForTest();
        Assert.Empty(messages[0].ImageUrls);
    }

    [Fact]
    public void OneBot_image消息段_解析出直链()
    {
        JsonElement message = JsonDocument.Parse("""
        [{"type":"image","data":{"file":"abc.jpg","url":"https://cdn.example.com/a.jpg"}},
         {"type":"text","data":{"text":"看这个"}},
         {"type":"image","data":{"file":"local-only.jpg"}}]
        """).RootElement;

        Assert.Equal(["https://cdn.example.com/a.jpg"], OneBotMessage.ExtractImageUrls(message));
        Assert.Equal("看这个", OneBotMessage.ExtractText(message));
    }

    // ---------- 触发判定 ----------

    [Fact]
    public async Task 纯图片消息_可触发随机插嘴()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("Trigger.RandomChatProbability", "100");
        var trigger = sp.GetRequiredService<PassiveTrigger>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        IncomingMessage imageOnly = TestHelpers.Msg(Group, Sender, "");
        imageOnly = WithImages(imageOnly, ImageUrl);

        Assert.True(trigger.ShouldRandomChat(imageOnly, now));
        // 无文字也无图片的空消息仍然不接话
        Assert.False(trigger.ShouldRandomChat(TestHelpers.Msg(Group, Sender, ""), now));
    }

    // ---------- 识图请求与历史占位 ----------

    [Fact]
    public async Task 带图消息_LLM请求内联图片_历史显示占位()
    {
        (ServiceProvider sp, List<string> llmBodies) = await BuildLlmCaptureAsync(reply: "这张图里好像是一只在睡觉的猫 🌧️");

        var runner = sp.GetRequiredService<WorkflowRunner>();
        bool sent = await runner.RunAsync(new TriggerContext
        {
            BotId = Database.LegacyBotId,
            GroupOpenId = Group,
            Type = TriggerType.Passive,
            Reason = "被群友 @ 互动",
            SenderOpenId = Sender,
            ImageUrls = [ImageUrl]
        }, "msg_id_img");

        Assert.True(sent);
        Assert.Single(llmBodies);

        // 图片以块数组内联在最后一条消息（触发消息），文本块 + image_url 块
        using JsonDocument doc = JsonDocument.Parse(llmBodies[0]);
        JsonElement messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(JsonValueKind.String, messages[0].GetProperty("content").ValueKind); // 前缀消息仍是纯字符串
        JsonElement content = messages[messages.GetArrayLength() - 1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Contains("被群友 @ 互动", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal(ExpectedDataUrl, content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task 历史入库_纯图片消息显示为占位()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var history = sp.GetRequiredService<HistoryStore>();

        await history.AppendAsync(Group, WithImages(TestHelpers.Msg(Group, Sender, "", msgId: "msg_hist_img"), ImageUrl));

        List<HistoryEntry> recent = history.GetRecent(Group, 10000, out _);
        Assert.Equal("[图片]", recent[^1].Content);
    }

    [Fact]
    public async Task 关闭视觉识图_不发图片块()
    {
        (ServiceProvider sp, List<string> llmBodies) = await BuildLlmCaptureAsync(reply: "哦哦");
        var config = sp.GetRequiredService<RuntimeConfig>();
        await config.SetAsync("Llm.EnableVision", "false");

        bool sent = await sp.GetRequiredService<WorkflowRunner>().RunAsync(new TriggerContext
        {
            BotId = Database.LegacyBotId,
            GroupOpenId = Group,
            Type = TriggerType.Passive,
            Reason = "被群友 @ 互动",
            SenderOpenId = Sender,
            ImageUrls = [ImageUrl]
        }, "msg_id_novision");

        Assert.True(sent);
        using JsonDocument doc = JsonDocument.Parse(Assert.Single(llmBodies));
        // 关闭后走纯文本路径：触发消息 content 仍为字符串
        Assert.Equal(JsonValueKind.String, LastMessage(doc).GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task 图片下载失败_降级为纯文本不中断回复()
    {
        (ServiceProvider sp, List<string> llmBodies) = await BuildLlmCaptureAsync(HttpStatusCode.NotFound, reply: "看不到图也没关系");

        bool sent = await sp.GetRequiredService<WorkflowRunner>().RunAsync(new TriggerContext
        {
            BotId = Database.LegacyBotId,
            GroupOpenId = Group,
            Type = TriggerType.Passive,
            Reason = "被群友 @ 互动",
            SenderOpenId = Sender,
            ImageUrls = [ImageUrl]
        }, "msg_id_fail");

        Assert.True(sent); // 下载失败不影响回复
        using JsonDocument doc = JsonDocument.Parse(Assert.Single(llmBodies));
        Assert.Equal(JsonValueKind.String, LastMessage(doc).GetProperty("content").ValueKind);
    }

    // ---------- 图片回溯（先发图、再 @ 机器人分析） ----------

    [Fact]
    public async Task 回溯取图_只认触发者本人_且限时间窗()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        var history = sp.GetRequiredService<HistoryStore>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // 别人刚发的图（更近）+ 本人 1 分钟前发的图
        await history.AppendAsync(Group, WithImages(TestHelpers.Msg(Group, "other_user", "", msgId: "m_other", msgTime: now.AddSeconds(-10)), "https://cdn.example.com/other.jpg"));
        await history.AppendAsync(Group, WithImages(TestHelpers.Msg(Group, Sender, "", msgId: "m_mine", msgTime: now.AddMinutes(-1)), ImageUrl));

        // 只回溯本人：不跨人取图（避免把别人发的图误当成本次提问对象）
        Assert.Equal([ImageUrl], history.FindRecentImageUrls(Group, Sender, TimeSpan.FromMinutes(3), now));

        // 窗口外不取
        Assert.Empty(history.FindRecentImageUrls(Group, Sender, TimeSpan.FromSeconds(30), now));

        // 关闭（0 秒窗口）不取
        Assert.Empty(history.FindRecentImageUrls(Group, Sender, TimeSpan.FromSeconds(0), now));
    }

    [Fact]
    public async Task 先发图再艾特_回溯取图进识图请求()
    {
        (ServiceProvider sp, List<string> llmBodies) = await BuildLlmCaptureAsync();
        await DisableFunAsync(sp);
        var processor = BuildProcessor(sp);

        // 第一条：本人发图（未 @ 机器人）→ 只入历史，不触发
        await processor.ProcessAsync(WithImages(TestHelpers.Msg(Group, Sender, "", msgId: "m_img"), ImageUrl), CancellationToken.None);
        Assert.Empty(llmBodies);

        // 第二条：@ 机器人求分析（本条没带图）→ 从历史回溯到上一条的图，内联识图
        await processor.ProcessAsync(TestHelpers.Msg(Group, Sender, "帮我看看这张图", isAt: true, msgId: "m_ask"), CancellationToken.None);

        using JsonDocument doc = JsonDocument.Parse(Assert.Single(llmBodies));
        JsonElement content = LastMessage(doc).GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal(ExpectedDataUrl, content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task 先发图再艾特_别人发图不回溯()
    {
        (ServiceProvider sp, List<string> llmBodies) = await BuildLlmCaptureAsync();
        await DisableFunAsync(sp);
        var processor = BuildProcessor(sp);

        await processor.ProcessAsync(WithImages(TestHelpers.Msg(Group, "other_user", "", msgId: "m_img2"), ImageUrl), CancellationToken.None);
        await processor.ProcessAsync(TestHelpers.Msg(Group, Sender, "帮我看看这张图", isAt: true, msgId: "m_ask2"), CancellationToken.None);

        // 没带图也没回溯到 -> 纯文本请求，不影响回复
        using JsonDocument doc = JsonDocument.Parse(Assert.Single(llmBodies));
        Assert.Equal(JsonValueKind.String, LastMessage(doc).GetProperty("content").ValueKind);
    }

    // ---------- 引用（回复）消息：官方事件直接下发被引用内容 ----------

    [Fact]
    public void 引用解析_索引与消息元素()
    {
        JsonElement payload = JsonDocument.Parse("""
        {"message_type":103,
         "message_scene":{"source":"default","ext":["msg_idx=REFIDX_AAA==","auth_token=tok","ref_msg_idx=TMP_bbb"]},
         "msg_elements":[{"msg_idx":"REFIDX_BBB==","message_type":0,"content":"今晚一起打球吗",
                          "attachments":[{"content_type":"image/png","url":"https://multimedia.nt.qq.com.cn/q.png"}]}]}
        """).RootElement;

        GroupMessage message = JsonSerializer.Deserialize<GroupMessage>(payload.GetRawText())!;
        QuoteParser.Result quote = QuoteParser.Parse(message.Scene, message.MsgElements);

        Assert.Equal("REFIDX_AAA==", quote.MsgIdx);
        Assert.Equal("TMP_bbb", quote.RefMsgIdx);
        Assert.Equal("今晚一起打球吗", quote.QuotedText);
        Assert.Equal(["https://multimedia.nt.qq.com.cn/q.png"], quote.QuotedImageUrls);
    }

    [Fact]
    public void 引用解析_无引用时为空()
    {
        var quote = QuoteParser.Parse(null, null);
        Assert.Equal("", quote.MsgIdx);
        Assert.Equal("", quote.RefMsgIdx);
        Assert.Equal("", quote.QuotedText);
        Assert.Empty(quote.QuotedImageUrls);
    }

    [Fact]
    public async Task 引用消息_被引用内容与图片随事件下发()
    {
        (MessageDispatcher dispatcher, MessageQueue queue) = await BuildDispatcherAsync();
        string payload = $$"""
        {"id":"msg_quote_1","author":{"member_openid":"{{Sender}}","username":"小明"},"content":" <@!bot> 这是什么",
         "group_openid":"{{Group}}","timestamp":"2026-01-01T00:00:00+00:00","message_type":103,
         "message_scene":{"source":"default","ext":["msg_idx=REFIDX_NEW==","ref_msg_idx=REFIDX_OLD=="]},
         "msg_elements":[{"message_type":0,"content":"今天中午吃了火锅",
           "attachments":[{"content_type":"image/jpeg","url":"{{ImageUrl}}"}]}]}
        """;

        await dispatcher.HandleDispatchAsync(Database.LegacyBotId, "GROUP_MESSAGE_CREATE", JsonDocument.Parse(payload).RootElement);

        IncomingMessage message = Assert.Single(queue.DrainForTest());
        Assert.Equal("今天中午吃了火锅", message.QuotedContent);
        Assert.Equal([ImageUrl], message.ImageUrls);      // 引用消息里的图片被合并
        Assert.Equal("REFIDX_NEW==", message.MsgIdx);
        Assert.Equal("REFIDX_OLD==", message.RefMsgIdx);
    }

    [Fact]
    public async Task 引用消息_本地无内容时按索引回溯()
    {
        (ServiceProvider sp, List<string> llmBodies) = await BuildLlmCaptureAsync();
        await DisableFunAsync(sp);
        var processor = BuildProcessor(sp);
        var history = sp.GetRequiredService<HistoryStore>();

        // 群里先前有人发过图（含 msg_idx），随后有人引用它（事件只带 ref_msg_idx、无引用内容）
        IncomingMessage imageMsg = WithImages(TestHelpers.Msg(Group, "other_user", "", msgId: "m_orig"), ImageUrl);
        await history.AppendAsync(Group, WithMsgIdx(imageMsg, "REFIDX_ORIG=="));

        await processor.ProcessAsync(WithQuote(TestHelpers.Msg(Group, Sender, "这是啥", isAt: true, msgId: "m_quote"), "REFIDX_ORIG=="), CancellationToken.None);

        using JsonDocument doc = JsonDocument.Parse(Assert.Single(llmBodies));
        JsonElement content = LastMessage(doc).GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);           // 回溯到图片并内联
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal(ExpectedDataUrl, content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task OneBot_引用消息_解析reply段并按索引回溯()
    {
        (ServiceProvider sp, List<string> llmBodies) = await BuildLlmCaptureAsync();
        await DisableFunAsync(sp);
        var store = sp.GetRequiredService<Services.Bots.BotInstanceStore>();
        await store.UpsertAsync(new BotInstance
        {
            Id = "ob-quote",
            Name = "OB引用",
            Platform = BotPlatform.OneBot11,
            Enabled = true,
            OneBot = new OneBotConfig { Http = new OneBotHttpConfig { Enabled = true, ApiUrl = "http://127.0.0.1:3000" } }
        });
        var manager = sp.GetRequiredService<OneBotManager>();
        var queue = sp.GetRequiredService<MessageQueue>();
        var history = sp.GetRequiredService<HistoryStore>();
        var processor = BuildProcessor(sp);
        string groupKey = BotKeys.Group("ob-quote", "10001");

        // 群友先发图（OneBot 群聊）
        await manager.HandleEventAsync("ob-quote", $$$"""
        {"post_type":"message","message_type":"group","self_id":999,"user_id":10086,"group_id":10001,
         "message_id":20001,"raw_message":"[CQ:image,file=a.jpg,url={{{ImageUrl}}}]",
         "sender":{"nickname":"小明"},
         "message":[{"type":"image","data":{"url":"{{{ImageUrl}}}"}}]}
        """);
        IncomingMessage imageMsg = Assert.Single(queue.DrainForTest());
        Assert.Equal([ImageUrl], imageMsg.ImageUrls);
        Assert.Equal("20001", imageMsg.MsgIdx);
        await history.AppendAsync(groupKey, imageMsg);

        // 随后引用那条图 + @ 机器人提问（reply 段只带被引用消息 id）
        await manager.HandleEventAsync("ob-quote", """
        {"post_type":"message","message_type":"group","self_id":999,"user_id":10086,"group_id":10001,
         "message_id":20002,"raw_message":"[CQ:reply,id=20001]这是什么",
         "sender":{"nickname":"小明"},
         "message":[{"type":"reply","data":{"id":"20001"}},{"type":"at","data":{"qq":"999"}},{"type":"text","data":{"text":"这是什么"}}]}
        """);
        IncomingMessage quoteMsg = Assert.Single(queue.DrainForTest());
        Assert.Equal("20001", quoteMsg.RefMsgIdx);
        Assert.Equal("20002", quoteMsg.MsgIdx);

        // 经消息管线 → 按 ref_msg_idx 回溯到原图并内联
        await processor.ProcessAsync(quoteMsg, CancellationToken.None);
        using JsonDocument doc = JsonDocument.Parse(Assert.Single(llmBodies));
        JsonElement content = LastMessage(doc).GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal(ExpectedDataUrl, content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    // ---------- 序列化兼容（前缀缓存保护） ----------

    [Fact]
    public void 纯文本消息序列化_与改造前逐字节一致()
    {
        // 旧格式 = 不加转换器时按属性序列化；新格式 = 转换器输出。两者必须完全一致，
        // 否则历史请求体的前缀变化会让 DeepSeek 缓存命中率崩掉。
        JsonSerializerOptions old = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        JsonSerializerOptions now = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new ChatMessageConverter() }
        };
        List<ChatMessage> samples =
        [
            ChatMessage.System("测试人设"),
            ChatMessage.User("今天天气怎么样？"),
            new ChatMessage
            {
                Role = "assistant",
                Content = null,
                ToolCalls = [new ChatToolCall { Id = "call_1", Function = new ChatToolCallFunction { Name = "web_search", Arguments = "{\"query\":\"天气\"}" } }]
            },
            ChatMessage.ToolResult("call_1", "结果：晴 22°C")
        ];
        foreach (ChatMessage message in samples)
        {
            Assert.Equal(JsonSerializer.Serialize(message, old), JsonSerializer.Serialize(message, now));
        }
    }

    // ---------- 辅助 ----------

    /// <summary>宿主 + 捕获 LLM 请求体；图片 URL 返回假 JPEG（imageStatus 非 200 时模拟下载失败）</summary>
    private static async Task<(ServiceProvider Sp, List<string> LlmBodies)> BuildLlmCaptureAsync(
        HttpStatusCode imageStatus = HttpStatusCode.OK, string reply = "收到啦 🌧️")
    {
        List<string> llmBodies = [];
        ServiceProvider sp = await TestHost.BuildReadyAsync(req =>
        {
            string host = req.RequestUri!.Host;
            if (host.Contains("deepseek", StringComparison.Ordinal))
            {
                llmBodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TestHelpers.LlmTextResponse(reply), Encoding.UTF8, "application/json")
                };
            }
            if (host.Contains("multimedia", StringComparison.Ordinal))
            {
                return imageStatus == HttpStatusCode.OK
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeJpeg) }
                    : new HttpResponseMessage(imageStatus) { Content = new StringContent("expired") };
            }
            // 其余（腾讯取 token / 发送等）返回成功空体
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });
        return (sp, llmBodies);
    }

    /// <summary>
    /// 关闭随机互动与随机插嘴，保证测试确定性：
    /// 复读/叫哥/OSM/反驳不命中时会 HandledAndBlock 直接阻断工作流（不调 LLM），
    /// 注意布尔开关必须用 "false"（ParseBool 不接受 "0"），概率项才用 "0"。
    /// </summary>
    private static async Task DisableFunAsync(ServiceProvider sp)
    {
        var config = sp.GetRequiredService<RuntimeConfig>();
        foreach (string key in new[]
        {
            "Fun.EnableReplyYes", "Fun.EnableReplyNo", "Fun.EnableRepeat",
            "Fun.EnableOsm", "Fun.EnableReverseAt", "Fun.EnableCallBrother"
        })
        {
            string? error = await config.SetAsync(key, "false");
            Assert.Null(error);
        }
        Assert.Null(await config.SetAsync("Trigger.RandomChatProbability", "0"));
    }

    /// <summary>按真实消息管线的同一装配方式构造处理器</summary>
    private static MessageProcessor BuildProcessor(ServiceProvider sp) => new(
        sp.GetRequiredService<GroupStateManager>(),
        sp.GetRequiredService<InputFilter>(),
        sp.GetRequiredService<HistoryStore>(),
        sp.GetRequiredService<CommandParser>(),
        sp.GetRequiredService<PassiveTrigger>(),
        sp.GetRequiredService<ProfileRecaller>(),
        sp.GetRequiredService<WorkflowRunner>(),
        sp.GetRequiredService<OutputFilter>(),
        sp.GetRequiredService<SendQueue>(),
        sp.GetRequiredService<FunService>(),
        sp.GetRequiredService<RuntimeConfig>(),
        sp.GetRequiredService<ILogger<MessageProcessor>>());

    /// <summary>取 messages 数组最后一条（触发消息）</summary>
    private static JsonElement LastMessage(JsonDocument doc)
    {
        JsonElement messages = doc.RootElement.GetProperty("messages");
        return messages[messages.GetArrayLength() - 1];
    }

    private static IncomingMessage WithImages(IncomingMessage message, params string[] urls) => new()
    {
        BotId = message.BotId,
        MsgId = message.MsgId,
        GroupOpenId = message.GroupOpenId,
        SenderOpenId = message.SenderOpenId,
        Username = message.Username,
        Content = message.Content,
        ImageUrls = [.. urls],
        QuotedContent = message.QuotedContent,
        MsgIdx = message.MsgIdx,
        RefMsgIdx = message.RefMsgIdx,
        IsAtRobot = message.IsAtRobot,
        IsAdmin = message.IsAdmin,
        ReceivedAt = message.ReceivedAt
    };

    private static IncomingMessage WithMsgIdx(IncomingMessage message, string msgIdx) => new()
    {
        BotId = message.BotId,
        MsgId = message.MsgId,
        GroupOpenId = message.GroupOpenId,
        SenderOpenId = message.SenderOpenId,
        Username = message.Username,
        Content = message.Content,
        ImageUrls = message.ImageUrls,
        MsgIdx = msgIdx,
        IsAtRobot = message.IsAtRobot,
        IsAdmin = message.IsAdmin,
        ReceivedAt = message.ReceivedAt
    };

    private static IncomingMessage WithQuote(IncomingMessage message, string refMsgIdx) => new()
    {
        BotId = message.BotId,
        MsgId = message.MsgId,
        GroupOpenId = message.GroupOpenId,
        SenderOpenId = message.SenderOpenId,
        Username = message.Username,
        Content = message.Content,
        RefMsgIdx = refMsgIdx,
        IsAtRobot = message.IsAtRobot,
        IsAdmin = message.IsAdmin,
        ReceivedAt = message.ReceivedAt
    };

    private static async Task<(MessageDispatcher Dispatcher, MessageQueue Queue)> BuildDispatcherAsync()
    {
        ServiceProvider sp = await TestHost.BuildReadyAsync();
        ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        MessageQueue queue = new(sp, loggerFactory.CreateLogger<MessageQueue>());
        MessageDispatcher dispatcher = new(
            queue,
            sp.GetRequiredService<RuntimeConfig>(),
            sp.GetRequiredService<BotIdentityResolver>(),
            sp.GetRequiredService<Services.Bots.BotInstanceStore>(),
            loggerFactory.CreateLogger<MessageDispatcher>());
        return (dispatcher, queue);
    }
}
