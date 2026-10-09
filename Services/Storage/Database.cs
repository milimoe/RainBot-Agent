using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RainBot.Models;

namespace RainBot.Services.Storage;

/// <summary>
/// SQLite 存储层：L0 画像、消息历史、群画像、动态配置、管理员、统计。
/// 每次操作独立连接（WAL 模式），无需 ORM。
/// </summary>
public class Database
{
    private readonly string _connectionString;
    private readonly ILogger<Database> _logger;

    public Database(IConfiguration configuration, ILogger<Database> logger)
    {
        _logger = logger;
        string path = configuration.GetSection("Rain:Storage:SqlitePath").Value ?? "Data/rainbot.db";
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, path);
        }
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        _logger.LogInformation("SQLite 数据库路径：{Path}", path);
    }

    /// <summary>初始化：建表 + WAL 模式</summary>
    public async Task InitializeAsync()
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                msg_id TEXT NOT NULL UNIQUE,
                group_openid TEXT NOT NULL,
                user_openid TEXT NOT NULL,
                content TEXT NOT NULL DEFAULT '',
                is_at INTEGER NOT NULL DEFAULT 0,
                msg_time TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_messages_group_time ON messages (group_openid, id);

            CREATE TABLE IF NOT EXISTS users (
                group_openid TEXT NOT NULL,
                user_openid TEXT NOT NULL,
                tags TEXT NOT NULL DEFAULT '[]',
                interests TEXT NOT NULL DEFAULT '',
                habits TEXT NOT NULL DEFAULT '',
                sensitive TEXT NOT NULL DEFAULT '',
                summary TEXT NOT NULL DEFAULT '',
                last_active TEXT NOT NULL,
                interaction_count INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (group_openid, user_openid)
            );
            CREATE INDEX IF NOT EXISTS idx_users_group_count ON users (group_openid, interaction_count DESC);

            CREATE TABLE IF NOT EXISTS group_profiles (
                group_openid TEXT PRIMARY KEY,
                type TEXT NOT NULL DEFAULT '',
                taboos TEXT NOT NULL DEFAULT '',
                summary TEXT NOT NULL DEFAULT '',
                muted INTEGER NOT NULL DEFAULT 0,
                degraded INTEGER NOT NULL DEFAULT 0,
                last_reset TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS admins (
                openid TEXT PRIMARY KEY,
                added_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS stats (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                group_openid TEXT NOT NULL,
                trigger_type TEXT NOT NULL,
                hit_tokens INTEGER NOT NULL DEFAULT 0,
                miss_tokens INTEGER NOT NULL DEFAULT 0,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_stats_ts ON stats (ts);

            CREATE TABLE IF NOT EXISTS distill_summaries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                group_openid TEXT NOT NULL,
                summary TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS bot_instances (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL DEFAULT '',
                platform TEXT NOT NULL DEFAULT 'QqOfficial',
                enabled INTEGER NOT NULL DEFAULT 1,
                config_json TEXT NOT NULL DEFAULT '{}',
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS bot_send_stats (
                bot_id TEXT PRIMARY KEY,
                sent INTEGER NOT NULL DEFAULT 0,
                failed INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS search_usage (
                provider TEXT NOT NULL,
                day TEXT NOT NULL,
                count INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (provider, day)
            );
            """;
        await cmd.ExecuteNonQueryAsync();
        await EnsureUserColumnsAsync(conn);
        await EnsureMessageNicknameAsync(conn);
        await MigrateToBotNamespaceAsync(conn);
        _logger.LogInformation("SQLite 数据库初始化完成");
    }

    private static async Task EnsureMessageNicknameAsync(SqliteConnection conn)
    {
        bool exists = false;
        await using (SqliteCommand info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(messages);";
            await using SqliteDataReader reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync()) if (reader.GetString(1) == "nickname") exists = true;
        }
        if (!exists)
        {
            await using SqliteCommand alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE messages ADD COLUMN nickname TEXT NOT NULL DEFAULT '';";
            await alter.ExecuteNonQueryAsync();
        }
    }

    private static async Task EnsureUserColumnsAsync(SqliteConnection conn)
    {
        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        await using (SqliteCommand info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(users);";
            await using SqliteDataReader reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        }
        foreach (string column in new[] { "nickname", "profile_updated_at", "profile_update_day", "profile_update_count" })
        {
            if (columns.Contains(column)) continue;
            await using SqliteCommand alter = conn.CreateCommand();
            alter.CommandText = column == "profile_update_count"
                ? "ALTER TABLE users ADD COLUMN profile_update_count INTEGER NOT NULL DEFAULT 0;"
                : $"ALTER TABLE users ADD COLUMN {column} TEXT NOT NULL DEFAULT '';";
            await alter.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// 多机器人命名空间迁移（一次性）：历史数据的 group_openid 均为裸 openid，
    /// 统一加默认实例前缀 "qq:"，使其与新的 {实例Id}:{群号} 键规则一致。
    /// 用 settings 表记录标记，重复执行安全；失败只告警不阻塞启动。
    /// </summary>
    private async Task MigrateToBotNamespaceAsync(SqliteConnection conn)
    {
        const string MarkerKey = "_migration.botNamespace.v1";
        if (LegacyBotId.Length == 0)
        {
            return;
        }
        try
        {
            await using (SqliteCommand check = conn.CreateCommand())
            {
                check.CommandText = "SELECT value FROM settings WHERE key = $key LIMIT 1;";
                check.Parameters.AddWithValue("$key", MarkerKey);
                if (await check.ExecuteScalarAsync() is string existing && existing == "1")
                {
                    return;
                }
            }

            string prefix = LegacyBotId + ":";
            (string table, string column)[] targets =
            [
                ("messages", "group_openid"),
                ("users", "group_openid"),
                ("group_profiles", "group_openid"),
                ("stats", "group_openid"),
                ("distill_summaries", "group_openid")
            ];
            int total = 0;
            foreach ((string table, string column) in targets)
            {
                await using SqliteCommand update = conn.CreateCommand();
                // group_openid 中不含 ':' 的即为老数据（新键一定带实例前缀）
                update.CommandText = $"UPDATE {table} SET {column} = $prefix || {column} WHERE {column} NOT LIKE '%:%';";
                update.Parameters.AddWithValue("$prefix", prefix);
                total += await update.ExecuteNonQueryAsync();
            }
            if (total > 0)
            {
                _logger.LogInformation("多机器人命名空间迁移：{Count} 条历史数据已归入默认实例 {BotId}", total, LegacyBotId);
            }

            await using SqliteCommand mark = conn.CreateCommand();
            mark.CommandText = "INSERT INTO settings (key, value) VALUES ($key, '1') ON CONFLICT(key) DO UPDATE SET value = '1';";
            mark.Parameters.AddWithValue("$key", MarkerKey);
            await mark.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "多机器人命名空间迁移失败（不影响启动，历史数据仍以裸 ID 存在）");
        }
    }

    /// <summary>迁移与种子使用的默认实例 Id（对应 QQ 官方网关）</summary>
    public const string LegacyBotId = "qq";

    // ---------- 动态配置 ----------

    public async Task<Dictionary<string, string>> GetAllSettingsAsync()
    {
        Dictionary<string, string> result = [];
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings;";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    public async Task<string?> GetSettingAsync(string key)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        object? result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    public async Task UpsertSettingAsync(string key, string value)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>删除配置覆盖项（恢复 appsettings 默认值）</summary>
    public async Task DeleteSettingAsync(string key)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------- 管理员 ----------

    public async Task<List<string>> GetAdminOpenIdsAsync()
    {
        List<string> result = [];
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT openid FROM admins ORDER BY added_at;";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    public async Task AddAdminOpenIdAsync(string openId)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO admins (openid, added_at) VALUES ($openid, $ts) ON CONFLICT(openid) DO NOTHING;";
        cmd.Parameters.AddWithValue("$openid", openId);
        cmd.Parameters.AddWithValue("$ts", Now());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveAdminOpenIdAsync(string openId)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM admins WHERE openid = $openid;";
        cmd.Parameters.AddWithValue("$openid", openId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------- 群画像 ----------

    public async Task<GroupProfile?> GetGroupProfileAsync(string groupOpenId)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT group_openid, type, taboos, summary, muted, degraded, last_reset FROM group_profiles WHERE group_openid = $g;";
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }
        return new GroupProfile
        {
            GroupOpenId = reader.GetString(0),
            Type = reader.GetString(1),
            Taboos = reader.GetString(2),
            Summary = reader.GetString(3),
            Muted = reader.GetInt32(4) != 0,
            Degraded = reader.GetInt32(5) != 0,
            LastReset = reader.GetString(6)
        };
    }

    public async Task UpsertGroupProfileAsync(GroupProfile profile)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO group_profiles (group_openid, type, taboos, summary, muted, degraded, last_reset, updated_at)
            VALUES ($g, $type, $taboos, $summary, $muted, $degraded, $last_reset, $ts)
            ON CONFLICT(group_openid) DO UPDATE SET
                type = excluded.type, taboos = excluded.taboos, summary = excluded.summary,
                muted = excluded.muted, degraded = excluded.degraded, last_reset = excluded.last_reset,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$g", profile.GroupOpenId);
        cmd.Parameters.AddWithValue("$type", profile.Type);
        cmd.Parameters.AddWithValue("$taboos", profile.Taboos);
        cmd.Parameters.AddWithValue("$summary", profile.Summary);
        cmd.Parameters.AddWithValue("$muted", profile.Muted ? 1 : 0);
        cmd.Parameters.AddWithValue("$degraded", profile.Degraded ? 1 : 0);
        cmd.Parameters.AddWithValue("$last_reset", profile.LastReset);
        cmd.Parameters.AddWithValue("$ts", Now());
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------- 用户画像（L0） ----------

    public async Task<UserProfile?> GetUserProfileAsync(string groupOpenId, string userOpenId)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT group_openid, user_openid, tags, interests, habits, sensitive, summary, last_active, interaction_count, nickname, profile_updated_at
            FROM users WHERE group_openid = $g AND user_openid = $u;
            """;
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$u", userOpenId);
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }
        return new UserProfile
        {
            GroupOpenId = reader.GetString(0),
            UserOpenId = reader.GetString(1),
            Tags = DeserializeTags(reader.GetString(2)),
            Interests = reader.GetString(3),
            Habits = reader.GetString(4),
            Sensitive = reader.GetString(5),
            Summary = reader.GetString(6),
            LastActive = reader.GetString(7),
            InteractionCount = reader.GetInt32(8),
            Nickname = reader.GetString(9),
            ProfileUpdatedAt = reader.GetString(10)
        };
    }

    public async Task UpsertUserProfileAsync(UserProfile profile)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (group_openid, user_openid, tags, interests, habits, sensitive, summary, last_active, interaction_count, updated_at, nickname, profile_updated_at)
            VALUES ($g, $u, $tags, $interests, $habits, $sensitive, $summary, $last_active, $count, $ts, $nickname, $profile_updated)
            ON CONFLICT(group_openid, user_openid) DO UPDATE SET
                tags = excluded.tags, interests = excluded.interests, habits = excluded.habits,
                sensitive = excluded.sensitive, summary = excluded.summary,
                last_active = excluded.last_active, interaction_count = excluded.interaction_count,
                updated_at = excluded.updated_at,
                nickname = CASE WHEN excluded.nickname <> '' THEN excluded.nickname ELSE nickname END,
                profile_updated_at = CASE WHEN excluded.profile_updated_at <> '' THEN excluded.profile_updated_at ELSE profile_updated_at END;
            """;
        cmd.Parameters.AddWithValue("$g", profile.GroupOpenId);
        cmd.Parameters.AddWithValue("$u", profile.UserOpenId);
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(profile.Tags));
        cmd.Parameters.AddWithValue("$interests", profile.Interests);
        cmd.Parameters.AddWithValue("$habits", profile.Habits);
        cmd.Parameters.AddWithValue("$sensitive", profile.Sensitive);
        cmd.Parameters.AddWithValue("$summary", profile.Summary);
        cmd.Parameters.AddWithValue("$last_active", profile.LastActive);
        cmd.Parameters.AddWithValue("$count", profile.InteractionCount);
        cmd.Parameters.AddWithValue("$nickname", profile.Nickname);
        cmd.Parameters.AddWithValue("$profile_updated", profile.ProfileUpdatedAt);
        cmd.Parameters.AddWithValue("$ts", Now());
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>消息统计：互动次数 +1、更新最后活跃时间</summary>
    public async Task BumpUserActivityAsync(string groupOpenId, string userOpenId, string? nickname = null, DateTimeOffset? receivedAt = null)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (group_openid, user_openid, tags, last_active, interaction_count, updated_at, nickname)
            VALUES ($g, $u, '[]', $ts, 1, $ts, $nickname)
            ON CONFLICT(group_openid, user_openid) DO UPDATE SET
                last_active = MAX(last_active, excluded.last_active), interaction_count = interaction_count + 1, updated_at = excluded.updated_at,
                nickname = CASE WHEN excluded.nickname <> '' THEN excluded.nickname ELSE nickname END;
            """;
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$u", userOpenId);
        cmd.Parameters.AddWithValue("$ts", (receivedAt ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("$nickname", nickname?.Trim() ?? "");
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteUserProfileAsync(string groupOpenId, string userOpenId)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM users WHERE group_openid = $g AND user_openid = $u;";
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$u", userOpenId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>按统一短标识（u + openid 前 8 位）查找，歧义时返回空。</summary>
    public async Task<UserProfile?> FindUserByShortIdAsync(string groupOpenId, string shortId)
    {
        List<UserProfile> matches = await FindUsersAsync(groupOpenId, shortId);
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>按互动次数取 Top N 用户（L1 锚点候选）</summary>
    public async Task<List<UserProfile>> GetTopUsersAsync(string groupOpenId, int limit)
    {
        List<UserProfile> result = [];
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT group_openid, user_openid, tags, interests, habits, sensitive, summary, last_active, interaction_count, nickname, profile_updated_at
            FROM users WHERE group_openid = $g ORDER BY interaction_count DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$limit", limit);
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new UserProfile
            {
                GroupOpenId = reader.GetString(0),
                UserOpenId = reader.GetString(1),
                Tags = DeserializeTags(reader.GetString(2)),
                Interests = reader.GetString(3),
                Habits = reader.GetString(4),
                Sensitive = reader.GetString(5),
                Summary = reader.GetString(6),
                LastActive = reader.GetString(7),
                InteractionCount = reader.GetInt32(8),
                Nickname = reader.GetString(9),
                ProfileUpdatedAt = reader.GetString(10)
            });
        }
        return result;
    }

    // ---------- 消息历史 ----------
    public async Task<List<UserProfile>> FindUsersAsync(string groupOpenId, string input)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT group_openid, user_openid, tags, interests, habits, sensitive, summary, last_active, interaction_count, nickname, profile_updated_at
            FROM users WHERE group_openid = $g AND
                (user_openid = $input OR nickname = $input OR ('u' || substr(user_openid, 1, 8)) = $input)
            ORDER BY user_openid;
            """;
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$input", input.Trim());
        List<UserProfile> users = [];
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) users.Add(new UserProfile
        {
            GroupOpenId = reader.GetString(0), UserOpenId = reader.GetString(1), Tags = DeserializeTags(reader.GetString(2)),
            Interests = reader.GetString(3), Habits = reader.GetString(4), Sensitive = reader.GetString(5), Summary = reader.GetString(6),
            LastActive = reader.GetString(7), InteractionCount = reader.GetInt32(8), Nickname = reader.GetString(9), ProfileUpdatedAt = reader.GetString(10)
        });
        // Exact openid has precedence over nicknames that happen to equal an identifier.
        UserProfile? exact = users.FirstOrDefault(u => u.UserOpenId == input.Trim());
        return exact == null ? users : [exact];
    }

    /// <summary>原子检查每日配额并保存画像，避免并发写入绕过配额或覆盖互动计数。</summary>
    public async Task<bool> TryUpdateUserProfileAsync(UserProfile profile, int dailyLimit, bool bypassQuota)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET tags = $tags, interests = $interests, habits = $habits, summary = $summary,
                profile_updated_at = $ts, updated_at = $ts, profile_update_day = $day,
                profile_update_count = CASE WHEN profile_update_day = $day THEN profile_update_count + 1 ELSE 1 END
            WHERE group_openid = $g AND user_openid = $u AND
                ($bypass = 1 OR (CASE WHEN profile_update_day = $day THEN profile_update_count ELSE 0 END) < $limit);
            """;
        cmd.Parameters.AddWithValue("$g", profile.GroupOpenId);
        cmd.Parameters.AddWithValue("$u", profile.UserOpenId);
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(profile.Tags));
        cmd.Parameters.AddWithValue("$interests", profile.Interests);
        cmd.Parameters.AddWithValue("$habits", profile.Habits);
        cmd.Parameters.AddWithValue("$summary", profile.Summary);
        cmd.Parameters.AddWithValue("$ts", Now());
        cmd.Parameters.AddWithValue("$day", DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$bypass", bypassQuota ? 1 : 0);
        cmd.Parameters.AddWithValue("$limit", Math.Max(0, dailyLimit));
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    public async Task InsertMessageAsync(string msgId, string groupOpenId, string userOpenId, string content, bool isAt, DateTimeOffset time, string? nickname = null)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO messages (msg_id, group_openid, user_openid, content, is_at, msg_time, nickname) VALUES ($id, $g, $u, $c, $at, $t, $nickname);";
        cmd.Parameters.AddWithValue("$nickname", nickname?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$id", msgId);
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$u", userOpenId);
        cmd.Parameters.AddWithValue("$c", content);
        cmd.Parameters.AddWithValue("$at", isAt ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", time.ToString("o", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<StoredMessage>> GetRecentMessagesAsync(string groupOpenId, int limit)
    {
        List<StoredMessage> result = [];
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT msg_id, user_openid, content, is_at, msg_time, nickname FROM messages WHERE group_openid = $g ORDER BY id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$limit", limit);
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new StoredMessage
            {
                MsgId = reader.GetString(0),
                Nickname = reader.GetString(5),
                UserOpenId = reader.GetString(1),
                Content = reader.GetString(2),
                IsAt = reader.GetInt32(3) != 0,
                Time = ParseTime(reader.GetString(4))
            });
        }
        result.Reverse(); // 按时间正序返回
        return result;
    }

    /// <summary>
    /// 记录机器人回复（WebUI 聊天回看用；user_openid = WebUiBridge.BotMarker）。
    /// 不进入 HistoryStore 内存上下文，不影响模型缓存前缀。
    /// </summary>
    public async Task InsertBotMessageAsync(string groupOpenId, string content, DateTimeOffset time)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO messages (msg_id, group_openid, user_openid, content, is_at, msg_time) VALUES ($id, $g, '$bot', $c, 0, $t);";
        cmd.Parameters.AddWithValue("$id", "bot-" + Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$c", content);
        cmd.Parameters.AddWithValue("$t", time.ToString("o", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>出现过消息或画像的所有群 OpenID（含仅存于群画像表的群）</summary>
    public async Task<List<string>> GetDistinctGroupsAsync()
    {
        List<string> result = [];
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT group_openid FROM messages
            UNION
            SELECT group_openid FROM group_profiles
            UNION
            SELECT group_openid FROM users
            ORDER BY group_openid;
            """;
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    /// <summary>按群聚合统计（消息/画像/LLM 用量），供 WebUI 状态页</summary>
    public async Task<List<GroupWebStats>> GetGroupStatsAsync()
    {
        Dictionary<string, GroupWebStatsAcc> map = new(StringComparer.Ordinal);

        await using (SqliteConnection conn = await OpenAsync())
        await using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT group_openid, COUNT(*), SUM(CASE WHEN user_openid = '$bot' THEN 1 ELSE 0 END) FROM messages GROUP BY group_openid;";
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                map[reader.GetString(0)] = new GroupWebStatsAcc
                {
                    GroupOpenId = reader.GetString(0),
                    Messages = reader.GetInt64(1),
                    BotMessages = reader.GetInt64(2)
                };
            }
        }

        await using (SqliteConnection conn = await OpenAsync())
        await using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT group_openid, COUNT(*) FROM users GROUP BY group_openid;";
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string group = reader.GetString(0);
                if (!map.TryGetValue(group, out GroupWebStatsAcc? acc))
                {
                    map[group] = acc = new GroupWebStatsAcc { GroupOpenId = group };
                }
                acc.Users = reader.GetInt64(1);
            }
        }

        await using (SqliteConnection conn = await OpenAsync())
        await using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT group_openid, COUNT(*), SUM(hit_tokens), SUM(miss_tokens), SUM(input_tokens), SUM(output_tokens) FROM stats GROUP BY group_openid;";
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string group = reader.GetString(0);
                if (!map.TryGetValue(group, out GroupWebStatsAcc? acc))
                {
                    map[group] = acc = new GroupWebStatsAcc { GroupOpenId = group };
                }
                acc.Calls = reader.GetInt64(1);
                acc.HitTokens = reader.GetInt64(2);
                acc.MissTokens = reader.GetInt64(3);
                acc.InputTokens = reader.GetInt64(4);
                acc.OutputTokens = reader.GetInt64(5);
            }
        }

        return map.Values
            .OrderBy(a => a.GroupOpenId)
            .Select(a => new GroupWebStats(
                a.GroupOpenId, a.Messages, a.BotMessages, a.Users,
                a.Calls, a.HitTokens, a.MissTokens, a.InputTokens, a.OutputTokens))
            .ToList();
    }

    /// <summary>清理每个群超出保留上限的历史（头部整条丢弃）</summary>
    public async Task TrimHistoryAsync(string groupOpenId, int keepCount)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM messages WHERE group_openid = $g AND id NOT IN (
                SELECT id FROM messages WHERE group_openid = $g ORDER BY id DESC LIMIT $keep
            );
            """;
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$keep", keepCount);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------- 统计 ----------

    public async Task InsertStatAsync(string groupOpenId, string triggerType, int hitTokens, int missTokens, int inputTokens, int outputTokens)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO stats (ts, group_openid, trigger_type, hit_tokens, miss_tokens, input_tokens, output_tokens) VALUES ($ts, $g, $t, $hit, $miss, $in, $out);";
        cmd.Parameters.AddWithValue("$ts", Now());
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$t", triggerType);
        cmd.Parameters.AddWithValue("$hit", hitTokens);
        cmd.Parameters.AddWithValue("$miss", missTokens);
        cmd.Parameters.AddWithValue("$in", inputTokens);
        cmd.Parameters.AddWithValue("$out", outputTokens);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>某群最近 N 条的缓存命中率（返回 hit / (hit+miss)）</summary>
    public async Task<double> GetCacheHitRateAsync(string groupOpenId, int count = 50)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hit_tokens, miss_tokens FROM stats WHERE group_openid = $g ORDER BY id DESC LIMIT $count;";
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$count", count);
        long hit = 0, miss = 0;
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            hit += reader.GetInt64(0);
            miss += reader.GetInt64(1);
        }
        return hit + miss > 0 ? (double)hit / (hit + miss) : 0;
    }

    // ---------- 蒸馏摘要 ----------

    public async Task SaveDistillSummaryAsync(string groupOpenId, string summary)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO distill_summaries (group_openid, summary, created_at) VALUES ($g, $s, $ts);";
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$s", summary);
        cmd.Parameters.AddWithValue("$ts", Now());
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------- 机器人实例 ----------

    /// <summary>读取全部机器人实例（按 Id 排序，保证顺序确定）</summary>
    public async Task<List<BotInstanceRow>> GetBotInstancesAsync()
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, platform, enabled, config_json FROM bot_instances ORDER BY id;";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        List<BotInstanceRow> result = [];
        while (await reader.ReadAsync())
        {
            result.Add(new BotInstanceRow
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Platform = reader.GetString(2),
                Enabled = reader.GetInt32(3) == 1,
                ConfigJson = reader.GetString(4)
            });
        }
        return result;
    }

    /// <summary>新增或更新机器人实例</summary>
    public async Task UpsertBotInstanceAsync(string id, string name, string platform, bool enabled, string configJson)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO bot_instances (id, name, platform, enabled, config_json, updated_at)
            VALUES ($id, $name, $platform, $enabled, $json, $ts)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                platform = excluded.platform,
                enabled = excluded.enabled,
                config_json = excluded.config_json,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$platform", platform);
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$json", configJson);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>删除机器人实例（其历史数据保留，只是不再有实例归属）</summary>
    public async Task DeleteBotInstanceAsync(string id)    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bot_instances WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------- 发送统计 ----------

    public async Task<List<BotSendStatRow>> GetBotSendStatsAsync()
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT bot_id, sent, failed, last_error FROM bot_send_stats ORDER BY bot_id;";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        List<BotSendStatRow> result = [];
        while (await reader.ReadAsync())
        {
            result.Add(new BotSendStatRow
            {
                BotId = reader.GetString(0),
                Sent = reader.GetInt64(1),
                Failed = reader.GetInt64(2),
                LastError = reader.GetString(3)
            });
        }
        return result;
    }

    public async Task UpsertBotSendStatsAsync(string botId, long sent, long failed, string? lastError)
    {        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO bot_send_stats (bot_id, sent, failed, last_error, updated_at)
            VALUES ($id, $sent, $failed, $err, $ts)
            ON CONFLICT(bot_id) DO UPDATE SET
                sent = excluded.sent,
                failed = excluded.failed,
                last_error = excluded.last_error,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", botId);
        cmd.Parameters.AddWithValue("$sent", sent);
        cmd.Parameters.AddWithValue("$failed", failed);
        cmd.Parameters.AddWithValue("$err", lastError ?? "");
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------- 搜索额度（按后端 + 本地日期计数，跨天自动重置） ----------

    /// <summary>取某后端当天的调用次数</summary>
    public async Task<int> GetSearchUsageAsync(string provider, string day)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count FROM search_usage WHERE provider = $p AND day = $d;";
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$d", day);
        object? value = await cmd.ExecuteScalarAsync();
        return value == null || value is DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>调用计数 +1，返回累加后的值</summary>
    public async Task<int> IncrementSearchUsageAsync(string provider, string day)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO search_usage (provider, day, count, updated_at)
            VALUES ($p, $d, 1, $ts)
            ON CONFLICT(provider, day) DO UPDATE SET
                count = count + 1,
                updated_at = excluded.updated_at
            RETURNING count;
            """;
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
        object? value = await cmd.ExecuteScalarAsync();
        return value == null || value is DBNull ? 1 : Convert.ToInt32(value);
    }

    /// <summary>把当天计数直接置为指定值（后端自报额度用尽时标记，避免继续无效调用）</summary>
    public async Task SetSearchUsageAsync(string provider, string day, int count)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO search_usage (provider, day, count, updated_at)
            VALUES ($p, $d, $count, $ts)
            ON CONFLICT(provider, day) DO UPDATE SET
                count = excluded.count,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$count", count);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<string>> GetRecentDistillSummariesAsync(string groupOpenId, int limit = 3)
    {
        List<string> result = [];
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT summary FROM distill_summaries WHERE group_openid = $g ORDER BY id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$limit", limit);
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }
        result.Reverse();
        return result;
    }

    // ---------- 辅助 ----------

    private async Task<SqliteConnection> OpenAsync()
    {
        SqliteConnection conn = new(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string iso)
    {
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset t)
            ? t
            : DateTimeOffset.UnixEpoch;
    }

    private static List<string> DeserializeTags(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>群静态画像（Block C 内容来源）</summary>
/// <summary>bot_send_stats 表的一行</summary>
public class BotSendStatRow
{
    public string BotId { get; set; } = "";
    public long Sent { get; set; }
    public long Failed { get; set; }
    public string LastError { get; set; } = "";
}

/// <summary>bot_instances 表的一行（配置以 JSON 存储，由 BotInstanceStore 反序列化）</summary>
public class BotInstanceRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "QqOfficial";
    public bool Enabled { get; set; } = true;
    public string ConfigJson { get; set; } = "{}";
}

public class GroupProfile
{
    public required string GroupOpenId { get; set; }
    public string Type { get; set; } = "";
    public string Taboos { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Muted { get; set; }
    public bool Degraded { get; set; }
    public string LastReset { get; set; } = "";
}

/// <summary>用户画像（L0 完整画像，不直接进上下文）</summary>
public class UserProfile
{
    public string Nickname { get; set; } = "";
    public string ProfileUpdatedAt { get; set; } = "";
    public required string GroupOpenId { get; set; }
    public required string UserOpenId { get; set; }
    public List<string> Tags { get; set; } = [];
    public string Interests { get; set; } = "";
    public string Habits { get; set; } = "";
    public string Sensitive { get; set; } = "";
    public string Summary { get; set; } = "";
    public string LastActive { get; set; } = "";
    public int InteractionCount { get; set; }
}

/// <summary>存储的历史消息</summary>
public class StoredMessage
{
    public string Nickname { get; set; } = "";
    public required string MsgId { get; set; }
    public required string UserOpenId { get; set; }
    public required string Content { get; set; }
    public bool IsAt { get; set; }
    public DateTimeOffset Time { get; set; }
}

/// <summary>按群聚合的 WebUI 统计</summary>
public sealed record GroupWebStats(
    string GroupOpenId,
    long Messages,
    long BotMessages,
    long Users,
    long Calls,
    long HitTokens,
    long MissTokens,
    long InputTokens,
    long OutputTokens);

/// <summary>聚合累加器（GetGroupStatsAsync 内部用）</summary>
internal sealed class GroupWebStatsAcc
{
    public required string GroupOpenId { get; init; }
    public long Messages { get; set; }
    public long BotMessages { get; set; }
    public long Users { get; set; }
    public long Calls { get; set; }
    public long HitTokens { get; set; }
    public long MissTokens { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}
