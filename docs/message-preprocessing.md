# 自动消息预处理扩展

`IMessagePreprocessor` 用于回复前自动把链接、附件、音频等消息来源解析成文本。有效聊天消息完成批次及引用解析后，`MessageProcessor` 调用 `MessagePreprocessorPipeline`；所有注册的解析器独立并发执行。冷却中、非 @ 且未命中插嘴概率的消息也执行解析并按开关直接发送资料，不调用 LLM，也不更新回复触发时间。通过回复触发判定时，结果按注册顺序放进 `TriggerContext.PreprocessedResults`，由 `BlockComposer` 注入本轮 Block F。LLM 直接读取，不需要发起工具调用；解析结果不写入聊天历史，也不加入工具清单。命令、机器人自身消息、敏感或过期消息和暖群不执行这条管线。

实现接口并在 `Program.cs` 注册即可，不需要修改消息处理器或上下文组装器：

```csharp
using RainBot.Services.Preprocessing;

public sealed class AttachmentTextPreprocessor : IMessagePreprocessor
{
    public string Name => "附件文本";
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<PreprocessedText>> ProcessAsync(
        MessagePreprocessContext context, CancellationToken cancellationToken)
    {
        // 按实际协议的附件字段识别适用消息；也可遍历 context.Batch。
        // 无匹配来源时直接 return []，不要做无关网络请求。
        // 调用自己的解析服务，传递 cancellationToken。
        // 成功：new PreprocessedText(来源, Text: 实际解析文本)
        // 失败：new PreprocessedText(来源, "unavailable", Error: 可公开的失败说明)
        await Task.CompletedTask;
        return [];
    }
}

builder.Services.AddSingleton<IMessagePreprocessor, AttachmentTextPreprocessor>();
```

上下文包含 `Current`（当前原始消息）、`Batch`（本批新消息）、`QuotedText`（已解析的引用正文）、`ImageUrls`（本轮图片）、`QuotedVideoUrls`（本地引用中的视频地址）和 `Now`（本轮统一时间）。具体消息字段见 `IncomingMessage`；接口不负责增加平台尚未提供的附件信息。多个解析器均会收到同一上下文；不要修改消息或这些集合。Singleton 解析器需要支持多个会话并发调用。

`PreprocessedText` 的 `Source` 是 URL、文件名或其他来源标识，`Status` 默认 `ok`，`Text` 是解析正文，`Error` 是失败说明。空集合表示不适用。成功文本每条最多注入 4000 字符，来源最多 240 字符；解析器应自行限制来源数量、输出长度、网络请求及资源消耗。所有输出视为外部非可信资料，不能包含“已经观看/听取”的虚假声明。

默认单个解析器等待预算 10 秒，可通过 `Timeout` 覆盖。超时产生 `timeout`，异常产生 `unavailable`，不会影响其他解析器及正常回复；内部异常只写日志，不进入 LLM。管线停止等待不响应取消的解析任务，但无法强制结束它，所以实现必须传递并尊重取消令牌；外部取消会向上传播。

解析器也可通过 `SendParsedContent` 属性启用直接发送资料（接口默认 `false`）。管线在该解析器完成后调用 `ParsedContentSender`，无需等待其他解析器或 LLM：将该解析器本轮成功结果去重合并为一条发送任务，由现有 `SendQueue` → 平台发送器调用 QQBot 接口。Markdown 模式下包含标题、引用正文和原链接，普通模式发送纯文本；总发送正文最多 1800 字符，超出标记截断，不受 LLM 行数/字符限制。任务固定本条 Markdown 格式，保留频控、重试、多实例、私聊及试聊拦截规则。全失败、空文本和 `SkipSideEffects` 回放不额外发送；发送入队异常不影响资料注入和正常回复。直接资料不追加到 LLM 内存历史，WebUI 仍可回看发送消息。

现有 `LinkPrefetcher` 已实现该接口，仍保留 `PrefetchAsync` 及提取方法，继续复用 `Rain.Page` 开关、链接数量、缓存、安全读取边界、引用与最近链接回溯、读取预算。视频资料明确标注仅为元信息。`TriggerContext.LinkResults` 保留用于兼容既有直接组装调用，正常消息处理走通用结果，不重复注入。

新增自动解析能力用 `IMessagePreprocessor`；新增网页站点读取器仍用 `ISiteReader`。后者只决定页面怎么读，不会自动新增消息预取来源。自动预处理和模型主动调用工具是两条独立入口，可以复用同一底层解析服务。

已有扩展：[GitHub 链接解析](github-reader.md)，支持公开仓库、Issue、PR、Commit、Release 和文本文件。

`PreprocessedText.DisplayCard` 可提供结构化 `ParsedContentCard`，让直接发送使用专用布局而不展示全部模型正文。卡片包含类型、标题、简介、标签和值、可选封面；B 站使用视频图文布局，GitHub 使用无图元信息布局。未提供卡片的扩展仍沿用通用文本布局。`DisplayCard` 不参与 JSON 序列化，不改变 LLM 的 `Text` 输入；合法的公开 B 站封面和来源 URL 会保留完整哈希，正文仍经过输出过滤。
