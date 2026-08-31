using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RainBot.Models;
using RainBot.Services.Bots;
using RainBot.Services.Config;
using RainBot.Services.Fun;
using RainBot.Services.OneBot;
using RainBot.Services.QQ;
using RainBot.Services.Storage;
using RainBot.Services.Trigger;

namespace RainBot.Services.WebUi;

/// <summary>
/// WebUI 控制台 HTTP API（/api/webui/*）。
/// 鉴权：Rain:WebUi:Token 非空时要求请求头 X-WebUi-Token（SSE 支持 ?token= 查询参数）。
/// </summary>
public static class WebUiApi
{
    public static void MapWebUiEndpoints(this WebApplication app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/webui");
        api.AddEndpointFilter(async (context, next) =>
        {
            WebUiOptions options = context.HttpContext.RequestServices.GetRequiredService<IOptions<WebUiOptions>>().Value;
            if (!options.Enabled)
            {
                return Results.Json(new { error = "WebUI 未启用（Rain:WebUi:Enabled=false）" }, statusCode: 404);
            }
            if (!string.IsNullOrEmpty(options.Token))
            {
                string? header = context.HttpContext.Request.Headers["X-WebUi-Token"].FirstOrDefault();
                string? query = context.HttpContext.Request.Query["token"].FirstOrDefault();
                bool ok = string.Equals(header, options.Token, StringComparison.Ordinal)
                    || string.Equals(query, options.Token, StringComparison.Ordinal);
                if (!ok)
                {
                    return Results.Json(new { error = "token_required" }, statusCode: 401);
                }
            }
            return await next(context);
        });

        // ---------- 状态 ----------
        api.MapGet("/status", async (BotStatus status, RuntimeConfig config, Database db, MessageQueue queue, WebUiBridge bridge, BotInstanceStore bots, IOptions<WebUiOptions> options) =>
        {
            List<GroupWebStats> stats = await db.GetGroupStatsAsync();
            return Results.Json(new
            {
                bot = new
                {
                    wsConnected = status.WebSocketConnected,
                    lastConnectedAt = status.LastConnectedAt,
                    receivedMessages = status.ReceivedMessages,
                    model = config.Config.Llm.Model,
                    botInstances = bots.EnabledInstances.Count,
                    botTotal = bots.All.Count
                },
                queuePending = queue.PendingCount,
                sseSubscribers = bridge.SubscriberCount,
                simulationGroups = bridge.SimulationGroups(),
                authRequired = !string.IsNullOrEmpty(options.Value.Token),
                totals = new
                {
                    groups = stats.Count,
                    messages = stats.Sum(s => s.Messages),
                    botMessages = stats.Sum(s => s.BotMessages),
                    users = stats.Sum(s => s.Users),
                    llmCalls = stats.Sum(s => s.Calls)
                }
            });
        });

        // ---------- 工具调用记录（内置与 MCP 工具统一，内存环形缓冲） ----------
        api.MapGet("/tool-calls", (RainBot.Services.Tools.ToolCallRecorder recorder, int? limit) =>
        {
            int take = Math.Clamp(limit ?? 100, 1, 200);
            return Results.Json(new
            {
                calls = recorder.Latest(take).Select(c => new
                {
                    seq = c.Seq,
                    time = c.Time,
                    tool = c.Tool,
                    arguments = c.Arguments,
                    result = c.Result,
                    elapsedMs = c.ElapsedMs,
                    success = c.Success
                })
            });
        });

        // ---------- DeepSeek 余额 ----------
        api.MapGet("/deepseek/balance", async (DeepSeekBalanceService balanceService, bool? refresh) =>
        {
            BalanceResult result = await balanceService.GetAsync(refresh == true);
            return Results.Json(new
            {
                available = result.Balance?.Available,
                currency = result.Balance?.Currency,
                totalBalance = result.Balance?.TotalBalance,
                grantedBalance = result.Balance?.GrantedBalance,
                toppedUpBalance = result.Balance?.ToppedUpBalance,
                fetchedAt = result.FetchedAt,
                error = result.Error
            });
        });

        // ---------- OSM 梗图目录（域名 + 自动扫描，无路径配置） ----------
        // 注：旧版单实例「QQ 网关凭据」维护（/settings/bot）已移除，
        // 凭据请在「机器人」页按实例维护（多实例架构）。
        api.MapGet("/settings/osm", (OsmImageCatalog catalog, RuntimeConfig config) => Results.Json(new
        {
            baseUrl = catalog.BaseUrl ?? "",
            enabled = config.Config.Fun.EnableOsm,
            probability = config.Config.Fun.OsmProbability,
            directory = catalog.OsmDirectory,
            files = catalog.Files,
            urls = catalog.Images.Select(i => i.Url)
        }));

        // ---------- 配置（热改参数） ----------
        api.MapGet("/config", async (RuntimeConfig config) =>
        {
            List<(string Key, string Value, bool Overridden)> all = await config.ListAllAsync();
            return Results.Json(new
            {
                items = all.Select(t => new
                {
                    key = t.Key,
                    value = t.Value,
                    overridden = t.Overridden,
                    meta = ConfigMetadata.Find(t.Key)
                })
            });
        });

        api.MapPut("/config/{key}", async (string key, RuntimeConfig config, HttpRequest request) =>
        {
            JsonNode? body = await ReadBodyAsync(request);
            string? value = body?["value"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return Results.Json(new { error = "value 不能为空" }, statusCode: 400);
            }
            string? error = await config.SetAsync(key, value);
            return error == null
                ? Results.Json(new { ok = true, key })
                : Results.Json(new { error }, statusCode: 400);
        });

        api.MapDelete("/config/{key}", async (string key, RuntimeConfig config) =>
        {
            string? error = await config.ResetAsync(key);
            return error == null
                ? Results.Json(new { ok = true, key, reset = true })
                : Results.Json(new { error }, statusCode: 400);
        });

        // ---------- 设置：人设（热重载） ----------
        api.MapGet("/settings/persona", async (RuntimeConfig config) =>
        {
            string path = ResolvePath(config.Config.PersonaPath);
            string content = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
            return Results.Json(new { path, content });
        });

        api.MapPut("/settings/persona", async (RuntimeConfig config, HttpRequest request) =>
        {
            JsonNode? body = await ReadBodyAsync(request);
            string? content = body?["content"]?.GetValue<string>();
            if (content == null)
            {
                return Results.Json(new { error = "content 不能为空" }, statusCode: 400);
            }
            string path = ResolvePath(config.Config.PersonaPath);
            await WriteFileAtomicAsync(path, content);
            return Results.Json(new { ok = true, path });
        });

        // ---------- 设置：SayNo 词表 ----------
        api.MapGet("/settings/sayno", (SayNoWordsService words) =>
            Results.Json(new { path = words.FilePathForDisplay, tables = words.AllTables }));

        api.MapPost("/settings/sayno", async (SayNoWordsService words, HttpRequest request) =>
        {
            JsonNode? body = await ReadBodyAsync(request);
            string? table = body?["table"]?.GetValue<string>();
            string? word = body?["word"]?.GetValue<string>();
            bool add = body?["add"]?.GetValue<bool>() ?? true;
            if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(word))
            {
                return Results.Json(new { error = "table 与 word 不能为空" }, statusCode: 400);
            }
            string? error = await words.UpdateAsync(table, add, word);
            return error == null
                ? Results.Json(new { ok = true })
                : Results.Json(new { error }, statusCode: 400);
        });

        // ---------- 设置：管理员 ----------
        api.MapGet("/settings/admins", async (RuntimeConfig config) =>
            Results.Json(new { openIds = await config.GetAdminOpenIdsAsync() }));

        api.MapPost("/settings/admins", async (RuntimeConfig config, HttpRequest request) =>
        {
            JsonNode? body = await ReadBodyAsync(request);
            string? openId = body?["openId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(openId))
            {
                return Results.Json(new { error = "openId 不能为空" }, statusCode: 400);
            }
            await config.AddAdminAsync(openId.Trim());
            return Results.Json(new { ok = true });
        });

        api.MapDelete("/settings/admins/{openId}", async (string openId, RuntimeConfig config) =>
        {
            await config.RemoveAdminAsync(openId);
            return Results.Json(new { ok = true });
        });

        // ---------- 群列表（聊天页左侧栏） ----------
        api.MapGet("/groups", async (Database db, GroupStateManager states, WebUiBridge bridge, BotInstanceStore store) =>
        {
            HashSet<string> all = new(await db.GetDistinctGroupsAsync(), StringComparer.Ordinal);
            foreach (GroupState state in states.AllStates())
            {
                all.Add(state.GroupOpenId);
            }

            List<GroupInfo> result = [];
            foreach (string group in all)
            {
                GroupState state = states.GetOrCreate(group);
                GroupProfile? profile = await db.GetGroupProfileAsync(group);
                List<StoredMessage> last = await db.GetRecentMessagesAsync(group, 1);
                StoredMessage? preview = last.Count > 0 ? last[0] : null;
                bool isBot = preview?.UserOpenId == WebUiBridge.BotMarker;
                // 会话归属：群键 = {实例Id}:{群号}，私聊 = {实例Id}:p{用户号}
                string botId = BotKeys.GetBotId(group);
                result.Add(new GroupInfo(
                    Group: group,
                    BotId: botId,
                    BotName: store.Get(botId)?.Name ?? botId,
                    IsPrivate: BotKeys.IsPrivateKey(group),
                    IsSim: group == WebUiBridge.SimGroupId,
                    SimEnabled: bridge.IsSimulationEnabled(group),
                    Muted: profile?.Muted ?? state.Muted ?? false,
                    Degraded: profile?.Degraded ?? state.Degraded,
                    TotalMessages: state.TotalMessages,
                    LastMessageUtc: state.LastMessageUtc == DateTimeOffset.MinValue ? null : state.LastMessageUtc,
                    Preview: preview?.Content,
                    PreviewTime: preview?.Time,
                    PreviewIsBot: isBot,
                    PreviewSender: preview?.UserOpenId,
                    LastResetUtc: state.LastResetUtc == DateTimeOffset.MinValue ? null : state.LastResetUtc
                ));
            }

            // 试聊群置顶，其余按最近活跃倒序
            List<GroupInfo> ordered = result
                .OrderByDescending(g => g.IsSim)
                .ThenByDescending(g => g.LastMessageUtc ?? g.PreviewTime ?? DateTimeOffset.MinValue)
                .ToList();
            return Results.Json(new { groups = ordered });
        });

        // ---------- 聊天记录 ----------
        api.MapGet("/groups/{groupId}/messages", async (string groupId, Database db, int? limit) =>
        {
            int take = Math.Clamp(limit ?? 200, 1, 1000);
            List<StoredMessage> messages = await db.GetRecentMessagesAsync(groupId, take);
            return Results.Json(new
            {
                group = groupId,
                messages = messages.Select(m => new
                {
                    id = m.MsgId,
                    sender = m.UserOpenId,
                    isBot = m.UserOpenId == WebUiBridge.BotMarker,
                    content = m.Content,
                    time = m.Time,
                    isAt = m.IsAt
                })
            });
        });

        // ---------- 群操作：静默 / 重置上下文 ----------
        api.MapPost("/groups/{groupId}/mute", async (string groupId, GroupStateManager states, HttpRequest request) =>
        {
            JsonNode? body = await ReadBodyAsync(request);
            bool muted = body?["muted"]?.GetValue<bool>() ?? false;
            await states.SetMutedAsync(groupId, muted);
            return Results.Json(new { ok = true, muted });
        });

        api.MapPost("/groups/{groupId}/reset-context", async (string groupId, GroupStateManager states) =>
        {
            await states.ResetContextAsync(groupId);
            return Results.Json(new { ok = true });
        });

        // ---------- 试聊（仿真） ----------
        api.MapPost("/sim-mode", async (WebUiBridge bridge, HttpRequest request) =>
        {
            JsonNode? body = await ReadBodyAsync(request);
            string? group = body?["group"]?.GetValue<string>();
            bool enabled = body?["enabled"]?.GetValue<bool>() ?? true;
            if (string.IsNullOrWhiteSpace(group))
            {
                return Results.Json(new { error = "group 不能为空" }, statusCode: 400);
            }
            if (group == WebUiBridge.SimGroupId)
            {
                return Results.Json(new { error = "内置试聊群恒为试聊模式" }, statusCode: 400);
            }
            bridge.SetSimulation(group, enabled);
            return Results.Json(new { ok = true, group, enabled });
        });

        api.MapPost("/simulate", async (MessageQueue queue, WebUiBridge bridge, RuntimeConfig config, HttpRequest request) =>
        {
            JsonNode? body = await ReadBodyAsync(request);
            string? group = body?["group"]?.GetValue<string>();
            string? sender = body?["sender"]?.GetValue<string>();
            string? username = body?["username"]?.GetValue<string>();
            string? content = body?["content"]?.GetValue<string>();
            bool isAt = body?["isAt"]?.GetValue<bool>() ?? true;
            bool isAdmin = body?["isAdmin"]?.GetValue<bool>() ?? true;

            if (string.IsNullOrWhiteSpace(content))
            {
                return Results.Json(new { error = "content 不能为空" }, statusCode: 400);
            }
            group = string.IsNullOrWhiteSpace(group) ? WebUiBridge.SimGroupId : group.Trim();
            if (!bridge.IsSimulationEnabled(group))
            {
                return Results.Json(new { error = "该群未开启试聊拦截，拒绝注入（请在群菜单中先开启）" }, statusCode: 400);
            }

            IncomingMessage message = new()
            {
                MsgId = "webui-" + Guid.NewGuid().ToString("N"),
                GroupOpenId = group,
                SenderOpenId = string.IsNullOrWhiteSpace(sender) ? "u_webui_sim_01" : sender.Trim(),
                Username = string.IsNullOrWhiteSpace(username) ? "WebUI 群友" : username.Trim(),
                Content = content.Trim(),
                IsAtRobot = isAt,
                IsAdmin = isAdmin,
                IsFullMessage = true,
                ReceivedAt = DateTimeOffset.UtcNow
            };
            await queue.EnqueueAsync(message);
            return Results.Json(new { ok = true, group, msgId = message.MsgId });
        });

        // ---------- 统计 ----------
        api.MapGet("/stats", async (Database db) =>
        {
            List<GroupWebStats> stats = await db.GetGroupStatsAsync();
            return Results.Json(new
            {
                groups = stats.Select(s => new
                {
                    group = s.GroupOpenId,
                    messages = s.Messages,
                    botMessages = s.BotMessages,
                    users = s.Users,
                    calls = s.Calls,
                    hitTokens = s.HitTokens,
                    missTokens = s.MissTokens,
                    inputTokens = s.InputTokens,
                    outputTokens = s.OutputTokens,
                    hitRate = s.HitTokens + s.MissTokens > 0 ? (double)s.HitTokens / (s.HitTokens + s.MissTokens) : 0
                }),
                totals = new
                {
                    messages = stats.Sum(s => s.Messages),
                    botMessages = stats.Sum(s => s.BotMessages),
                    users = stats.Sum(s => s.Users),
                    calls = stats.Sum(s => s.Calls),
                    hitTokens = stats.Sum(s => s.HitTokens),
                    missTokens = stats.Sum(s => s.MissTokens),
                    hitRate = stats.Sum(s => s.HitTokens) + stats.Sum(s => s.MissTokens) > 0
                        ? (double)stats.Sum(s => s.HitTokens) / stats.Sum(s => s.HitTokens + s.MissTokens)
                        : 0
                }
            });
        });

        // ---------- 日志（ILogger 输出，与日志管线同源） ----------
        api.MapGet("/logs", (WebUiLogProvider logProvider, long? after, int? limit) =>
        {
            long cursor = after ?? 0;
            int take = Math.Clamp(limit ?? 500, 1, 2000);
            IReadOnlyList<WebUiLogEntry> entries = logProvider.GetEntries(cursor, take);
            return Results.Json(new
            {
                seq = logProvider.LastSeq,
                entries = entries.Select(e => new
                {
                    seq = e.Seq,
                    time = e.Time,
                    level = e.Level,
                    category = e.Category,
                    message = e.Message,
                    exception = e.Exception
                })
            });
        });

        // ---------- 实时事件流（SSE） ----------
        api.MapGet("/events", async (HttpContext context, WebUiEventBus bus) =>
        {
            HttpResponse response = context.Response;
            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no"; // 关闭 nginx 缓冲
            CancellationToken ct = context.RequestAborted;

            try
            {
                // 先回放最近事件（断线重连补缺口）
                foreach (WebUiEvent ev in bus.Recent(300))
                {
                    await WriteEventAsync(response, ev, ct);
                }

                ChannelReader<WebUiEvent> reader = bus.Subscribe();
                while (!ct.IsCancellationRequested)
                {
                    if (reader.TryRead(out WebUiEvent? ev))
                    {
                        await WriteEventAsync(response, ev, ct);
                        continue;
                    }
                    // 15 秒心跳，保持反代连接存活
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
                    Task<bool> wait = reader.WaitToReadAsync(ct).AsTask();
                    Task completed = await Task.WhenAny(wait, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
                    timeout.Cancel();
                    if (completed == wait)
                    {
                        if (!await wait)
                        {
                            break; // 订阅已关闭
                        }
                    }
                    else
                    {
                        await response.WriteAsync(": ping\n\n", ct);
                        await response.Body.FlushAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        });

        // ---------- 机器人实例管理 ----------
        api.MapGet("/bots", (BotInstanceStore store, BotConnectionRegistry registry, RuntimeConfig config, BotSendStats stats) =>
        {
            List<BotInstanceStatus> statuses = registry.Summarize(store.All);
            string baseUrl = config.Config.PublicBaseUrl.TrimEnd('/');
            return Results.Json(new
            {
                bots = store.All.Select(b => new
                {
                    b.Id,
                    b.Name,
                    platform = b.Platform.ToString(),
                    b.Enabled,
                    b.PersonaPath,
                    qq = b.Qq,
                    oneBot = new
                    {
                        b.OneBot.SelfQq,
                        http = b.OneBot.Http,
                        wsForward = b.OneBot.WsForward,
                        wsReverse = b.OneBot.WsReverse
                    },
                    // OneBot 接入地址（可直接复制到 go-cqhttp / NapCat 配置）
                    endpoints = b.Platform == BotPlatform.OneBot11
                        ? new
                        {
                            httpReport = baseUrl.Length > 0 ? baseUrl + OneBotRoutes.Report(b.Id) : OneBotRoutes.Report(b.Id),
                            reverseWs = (baseUrl.Length > 0
                                ? baseUrl.Replace("http", "ws", StringComparison.OrdinalIgnoreCase)
                                : "") + OneBotRoutes.ReverseWs(b.Id)
                        }
                        : null
                }),
                status = statuses,
                sendStats = stats.Snapshot().Select(s => new
                {
                    botId = s.BotId,
                    sent = s.Sent,
                    failed = s.Failed,
                    lastError = s.LastError
                })
            });
        });

        api.MapPost("/bots", async (BotInstanceStore store, HttpRequest request) =>
        {
            JsonNode? body = await ReadBodyAsync(request);
            if (body is not JsonObject obj)
            {
                return Results.Json(new { error = "请求体格式错误" }, statusCode: 400);
            }
            BotInstance instance = ParseBotInstance(obj);
            string? error = await store.UpsertAsync(instance);
            return error != null ? Results.Json(new { error }, statusCode: 400) : Results.Json(new { ok = true, id = instance.Id });
        });

        api.MapDelete("/bots/{id}", async (string id, BotInstanceStore store) =>
        {
            string? error = await store.DeleteAsync(id);
            return error != null ? Results.Json(new { error }, statusCode: 404) : Results.Json(new { ok = true });
        });

        api.MapPost("/bots/{id}/enabled", async (string id, BotInstanceStore store, HttpRequest request) =>
        {
            JsonNode? body = await ReadBodyAsync(request);
            bool enabled = body?["enabled"]?.GetValue<bool>() ?? true;
            string? error = await store.SetEnabledAsync(id, enabled);
            return error != null ? Results.Json(new { error }, statusCode: 400) : Results.Json(new { ok = true, enabled });
        });

        // ---------- 入口跳转 ----------
        // 注意：不要映射 "/webui" 路由（路由匹配忽略尾斜杠，会与静态文件形成重定向环）。
        // /webui → /webui/ 由 StaticFileMiddleware 自动处理，/webui/ 由 UseDefaultFiles 提供 index.html。
        app.MapGet("/", (IOptions<WebUiOptions> options) =>
            options.Value.Enabled ? Results.Redirect("/webui/") : Results.NotFound());
    }

    // ---------- 辅助 ----------

    /// <summary>把 WebUI 提交的 JSON 解析为机器人实例（缺字段用默认值，避免前端漏传报错）</summary>
    private static BotInstance ParseBotInstance(JsonObject obj)
    {
        BotInstance instance = new()
        {
            Id = obj["id"]?.GetValue<string>()?.Trim() ?? "",
            Name = obj["name"]?.GetValue<string>()?.Trim() ?? "",
            Platform = string.Equals(obj["platform"]?.GetValue<string>(), "OneBot11", StringComparison.OrdinalIgnoreCase)
                ? BotPlatform.OneBot11
                : BotPlatform.QqOfficial,
            Enabled = obj["enabled"]?.GetValue<bool>() ?? true,
            PersonaPath = obj["personaPath"]?.GetValue<string>() ?? ""
        };

        if (obj["qq"] is JsonObject qq)
        {
            instance.Qq = new QqOfficialConfig
            {
                AppId = qq["appId"]?.GetValue<string>() ?? "",
                Secret = qq["secret"]?.GetValue<string>() ?? "",
                UseSandbox = qq["useSandbox"]?.GetValue<bool>() ?? false,
                SelfOpenId = qq["selfOpenId"]?.GetValue<string>() ?? ""
            };
        }

        if (obj["oneBot"] is JsonObject ob)
        {
            instance.OneBot = new OneBotConfig
            {
                SelfQq = ob["selfQq"]?.GetValue<string>() ?? ""
            };
            if (ob["http"] is JsonObject http)
            {
                instance.OneBot.Http = new OneBotHttpConfig
                {
                    Enabled = http["enabled"]?.GetValue<bool>() ?? false,
                    ApiUrl = http["apiUrl"]?.GetValue<string>() ?? "",
                    Token = http["token"]?.GetValue<string>() ?? "",
                    ReportPath = http["reportPath"]?.GetValue<string>() ?? OneBotRoutes.DefaultReportPath
                };
            }
            if (ob["wsForward"] is JsonObject wsf)
            {
                instance.OneBot.WsForward = new OneBotWsConfig
                {
                    Enabled = wsf["enabled"]?.GetValue<bool>() ?? false,
                    Url = wsf["url"]?.GetValue<string>() ?? "",
                    Token = wsf["token"]?.GetValue<string>() ?? ""
                };
            }
            if (ob["wsReverse"] is JsonObject wsr)
            {
                instance.OneBot.WsReverse = new OneBotWsConfig
                {
                    Enabled = wsr["enabled"]?.GetValue<bool>() ?? false,
                    Token = wsr["token"]?.GetValue<string>() ?? "",
                    Path = wsr["path"]?.GetValue<string>() ?? OneBotRoutes.DefaultReverseWsPath
                };
            }
        }
        return instance;
    }

    private static async Task WriteEventAsync(HttpResponse response, WebUiEvent ev, CancellationToken ct)
    {
        string payload = JsonSerializer.Serialize(new { type = ev.Type, time = ev.Time, data = ev.Data });
        await response.WriteAsync($"data: {payload}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task<JsonNode?> ReadBodyAsync(HttpRequest request)
    {
        try
        {
            return await request.ReadFromJsonAsync<JsonNode>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>相对路径解析到运行目录（与人设/SayNo 加载器一致）</summary>
    private static string ResolvePath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    /// <summary>原子写：临时文件 + 重命名，避免热重载读到半截内容</summary>
    private static async Task WriteFileAtomicAsync(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>群列表条目（聊天页左侧栏）</summary>
    private sealed record GroupInfo(
        string Group,
        string BotId,
        string BotName,
        bool IsPrivate,
        bool IsSim,
        bool SimEnabled,
        bool Muted,
        bool Degraded,
        long TotalMessages,
        DateTimeOffset? LastMessageUtc,
        string? Preview,
        DateTimeOffset? PreviewTime,
        bool PreviewIsBot,
        string? PreviewSender,
        DateTimeOffset? LastResetUtc);
}
