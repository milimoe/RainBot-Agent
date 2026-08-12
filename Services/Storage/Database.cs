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
            """;
        await cmd.ExecuteNonQueryAsync();
        _logger.LogInformation("SQLite 数据库初始化完成");
    }

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
            SELECT group_openid, user_openid, tags, interests, habits, sensitive, summary, last_active, interaction_count
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
            InteractionCount = reader.GetInt32(8)
        };
    }

    public async Task UpsertUserProfileAsync(UserProfile profile)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (group_openid, user_openid, tags, interests, habits, sensitive, summary, last_active, interaction_count, updated_at)
            VALUES ($g, $u, $tags, $interests, $habits, $sensitive, $summary, $last_active, $count, $ts)
            ON CONFLICT(group_openid, user_openid) DO UPDATE SET
                tags = excluded.tags, interests = excluded.interests, habits = excluded.habits,
                sensitive = excluded.sensitive, summary = excluded.summary,
                last_active = excluded.last_active, interaction_count = excluded.interaction_count,
                updated_at = excluded.updated_at;
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
        cmd.Parameters.AddWithValue("$ts", Now());
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>消息统计：互动次数 +1、更新最后活跃时间</summary>
    public async Task BumpUserActivityAsync(string groupOpenId, string userOpenId)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (group_openid, user_openid, tags, last_active, interaction_count, updated_at)
            VALUES ($g, $u, '[]', $ts, 1, $ts)
            ON CONFLICT(group_openid, user_openid) DO UPDATE SET
                last_active = excluded.last_active, interaction_count = interaction_count + 1, updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$u", userOpenId);
        cmd.Parameters.AddWithValue("$ts", Now());
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

    /// <summary>按短 ID（openid 前 6 位，形如 u123456）查找用户</summary>
    public async Task<UserProfile?> FindUserByShortIdAsync(string groupOpenId, string shortId)
    {
        string prefix = shortId.TrimStart('u', 'U');
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT group_openid, user_openid, tags, interests, habits, sensitive, summary, last_active, interaction_count
            FROM users WHERE group_openid = $g AND user_openid LIKE $p ESCAPE '\'
            ORDER BY interaction_count DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$p", $"{prefix}%");
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
            InteractionCount = reader.GetInt32(8)
        };
    }

    /// <summary>按互动次数取 Top N 用户（L1 锚点候选）</summary>
    public async Task<List<UserProfile>> GetTopUsersAsync(string groupOpenId, int limit)
    {
        List<UserProfile> result = [];
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT group_openid, user_openid, tags, interests, habits, sensitive, summary, last_active, interaction_count
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
                InteractionCount = reader.GetInt32(8)
            });
        }
        return result;
    }

    // ---------- 消息历史 ----------

    public async Task InsertMessageAsync(string msgId, string groupOpenId, string userOpenId, string content, bool isAt, DateTimeOffset time)
    {
        await using SqliteConnection conn = await OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO messages (msg_id, group_openid, user_openid, content, is_at, msg_time) VALUES ($id, $g, $u, $c, $at, $t);";
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
        cmd.CommandText = "SELECT msg_id, user_openid, content, is_at, msg_time FROM messages WHERE group_openid = $g ORDER BY id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$g", groupOpenId);
        cmd.Parameters.AddWithValue("$limit", limit);
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new StoredMessage
            {
                MsgId = reader.GetString(0),
                UserOpenId = reader.GetString(1),
                Content = reader.GetString(2),
                IsAt = reader.GetInt32(3) != 0,
                Time = ParseTime(reader.GetString(4))
            });
        }
        result.Reverse(); // 按时间正序返回
        return result;
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
    public required string MsgId { get; set; }
    public required string UserOpenId { get; set; }
    public required string Content { get; set; }
    public bool IsAt { get; set; }
    public DateTimeOffset Time { get; set; }
}
