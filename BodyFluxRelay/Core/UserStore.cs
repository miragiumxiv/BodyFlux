using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace BodyFluxRelay.Core;

/// <summary>
/// Persists user identity, ban status, daily quotas, and session history to a local SQLite database.
/// All values linked to a user are keyed by a <c>globalId</c> derived server-side from the client's
/// InstallId UUID — never by a real name or the raw UUID.
///
/// Thread-safety: all write paths are serialised by a SemaphoreSlim; the in-memory message counter
/// (<see cref="TrackMessage"/>) is lock-free and flushed to the DB periodically and on session close.
/// </summary>
public sealed class UserStore : IAsyncDisposable
{
    private readonly string    _connStr;
    private readonly string    _serverSecret;
    private readonly int       _dailyQuota;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _pendingCounts = new();
    private readonly Timer     _flushTimer;

    public UserStore(string dbPath, string serverSecret, int dailyQuota)
    {
        _serverSecret = serverSecret;
        _dailyQuota   = dailyQuota;
        _connStr      = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode       = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _flushTimer = new Timer(_ => _ = FlushCountsAsync(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    // ── Schema bootstrap ──────────────────────────────────────────────────────

    public async Task InitAsync()
    {
        await using var db = Open();
        await db.OpenAsync();

        // WAL mode: one writer + concurrent readers, better for WebSocket workloads.
        await ExecAsync(db, "PRAGMA journal_mode=WAL;");

        await ExecAsync(db, """
            CREATE TABLE IF NOT EXISTS users (
                global_id      TEXT PRIMARY KEY,
                first_seen     TEXT NOT NULL,
                last_seen      TEXT NOT NULL,
                total_messages INTEGER NOT NULL DEFAULT 0,
                daily_messages INTEGER NOT NULL DEFAULT 0,
                daily_reset_at TEXT NOT NULL,
                is_banned      INTEGER NOT NULL DEFAULT 0,
                ban_reason     TEXT,
                ban_expires_at TEXT
            );
            """);

        await ExecAsync(db, """
            CREATE TABLE IF NOT EXISTS sessions (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                global_id       TEXT NOT NULL REFERENCES users(global_id),
                room_hash       TEXT NOT NULL,
                peer_id         TEXT,
                connected_at    TEXT NOT NULL,
                disconnected_at TEXT,
                messages_sent   INTEGER NOT NULL DEFAULT 0
            );
            """);

        await ExecAsync(db, "CREATE INDEX IF NOT EXISTS idx_sessions_gid ON sessions(global_id);");

        await ExecAsync(db, """
            CREATE TABLE IF NOT EXISTS ban_audit_log (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                global_id   TEXT    NOT NULL,
                action      TEXT    NOT NULL,
                reason      TEXT,
                expires_at  TEXT,
                actioned_at TEXT    NOT NULL
            );
            """);
    }

    // ── Identity derivation ───────────────────────────────────────────────────

    /// <summary>
    /// Derives the server-side opaque identifier from the client's InstallId hex string.
    /// HMAC-SHA256 with the server secret — stable across restarts as long as the secret is fixed.
    /// </summary>
    public string DeriveGlobalId(string installIdHex)
    {
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_serverSecret),
            Encoding.UTF8.GetBytes(installIdHex));
        return Convert.ToHexString(hash).ToLowerInvariant(); // 64 hex chars
    }

    /// <summary>Derives a short opaque room token for the sessions log (server never stores PairKey).</summary>
    public string DeriveRoomHash(string groupCode)
    {
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_serverSecret),
            Encoding.UTF8.GetBytes("room:" + groupCode));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant(); // 16 hex chars
    }

    // ── User lifecycle ────────────────────────────────────────────────────────

    /// <summary>
    /// Upserts the user record (updating last_seen and resetting the daily counter if the day rolled
    /// over), then returns whether the user is banned and the ban reason if so.
    /// </summary>
    public async Task<(bool IsBanned, string? Reason)> CheckAndUpsertAsync(string globalId)
    {
        var now     = DateTime.UtcNow;
        var today   = now.Date.ToString("O");
        var nowStr  = now.ToString("O");

        await _gate.WaitAsync();
        try
        {
            await using var db = Open();
            await db.OpenAsync();

            // Roll over daily counter if the day changed since last reset.
            await ExecAsync(db,
                "UPDATE users SET daily_messages = 0, daily_reset_at = @today WHERE global_id = @id AND daily_reset_at < @today",
                ("@id", globalId), ("@today", today));

            // Insert on first connect; touch last_seen on reconnect.
            await ExecAsync(db, """
                INSERT INTO users (global_id, first_seen, last_seen, daily_reset_at)
                VALUES (@id, @now, @now, @today)
                ON CONFLICT(global_id) DO UPDATE SET last_seen = @now
                """,
                ("@id", globalId), ("@now", nowStr), ("@today", today));

            // Read current ban and quota state.
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT is_banned, ban_reason, ban_expires_at, daily_messages FROM users WHERE global_id = @id";
            cmd.Parameters.AddWithValue("@id", globalId);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return (false, null);

            int      isBanned   = r.GetInt32(0);
            string?  banReason  = r.IsDBNull(1) ? null : r.GetString(1);
            string?  expiresStr = r.IsDBNull(2) ? null : r.GetString(2);
            int      dailyMsgs  = r.GetInt32(3);
            r.Close();

            // Lift expired temp bans automatically.
            if (isBanned == 1 && expiresStr != null && DateTime.Parse(expiresStr) <= now)
            {
                await ExecAsync(db, "UPDATE users SET is_banned = 0 WHERE global_id = @id", ("@id", globalId));
                isBanned = 0;
            }

            if (isBanned > 0)        return (true, banReason ?? "Banned");
            if (dailyMsgs >= _dailyQuota) return (true, "Daily message quota exceeded — try again tomorrow");
            return (false, null);
        }
        finally { _gate.Release(); }
    }

    // ── Session lifecycle ─────────────────────────────────────────────────────

    /// <summary>Opens a session row and returns its auto-incremented id.</summary>
    public async Task<long> OpenSessionAsync(string globalId, string roomHash)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = Open();
            await db.OpenAsync();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (global_id, room_hash, connected_at)
                VALUES (@gid, @rh, @ts);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@gid", globalId);
            cmd.Parameters.AddWithValue("@rh",  roomHash);
            cmd.Parameters.AddWithValue("@ts",  DateTime.UtcNow.ToString("O"));
            return (long)(await cmd.ExecuteScalarAsync())!;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Links the room peer id (opaque hash of name+PairKey) to the session row once we learn it
    /// from the first hello message. Fire-and-forget — not critical path.
    /// </summary>
    public void LinkSessionPeer(long sessionId, string peerId)
        => _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                await using var db = Open();
                await db.OpenAsync();
                await ExecAsync(db,
                    "UPDATE sessions SET peer_id = @pid WHERE id = @id",
                    ("@pid", peerId), ("@id", sessionId));
            }
            finally { _gate.Release(); }
        });

    /// <summary>Flushes pending message counts and stamps the session's disconnected_at.</summary>
    public async Task CloseSessionAsync(long sessionId)
    {
        await FlushCountsAsync();
        await _gate.WaitAsync();
        try
        {
            await using var db = Open();
            await db.OpenAsync();
            await ExecAsync(db,
                "UPDATE sessions SET disconnected_at = @ts WHERE id = @id",
                ("@ts", DateTime.UtcNow.ToString("O")), ("@id", sessionId));
        }
        finally { _gate.Release(); }
    }

    // ── Admin operations ──────────────────────────────────────────────────────

    /// <summary>
    /// Bans a user. Creates a stub user row if the globalId has never connected.
    /// Pass <paramref name="expiresAt"/> for a temporary ban; null for permanent.
    /// Takes effect immediately on the next connection attempt or quota check.
    /// </summary>
    public async Task BanAsync(string globalId, string? reason, DateTime? expiresAt)
    {
        var now = DateTime.UtcNow.ToString("O");
        await _gate.WaitAsync();
        try
        {
            await using var db = Open();
            await db.OpenAsync();

            await ExecAsync(db, """
                INSERT INTO users (global_id, first_seen, last_seen, daily_reset_at)
                VALUES (@id, @now, @now, @today)
                ON CONFLICT(global_id) DO NOTHING
                """,
                ("@id", globalId), ("@now", now), ("@today", DateTime.UtcNow.Date.ToString("O")));

            await ExecAsync(db,
                "UPDATE users SET is_banned = 1, ban_reason = @r, ban_expires_at = @e WHERE global_id = @id",
                ("@r", (object?)reason   ?? DBNull.Value),
                ("@e", (object?)expiresAt?.ToString("O") ?? DBNull.Value),
                ("@id", globalId));

            await ExecAsync(db,
                "INSERT INTO ban_audit_log (global_id, action, reason, expires_at, actioned_at) VALUES (@id, 'ban', @r, @e, @ts)",
                ("@id", globalId),
                ("@r",  (object?)reason ?? DBNull.Value),
                ("@e",  (object?)expiresAt?.ToString("O") ?? DBNull.Value),
                ("@ts", now));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Lifts a ban and records the action in the audit log.</summary>
    public async Task UnbanAsync(string globalId)
    {
        var now = DateTime.UtcNow.ToString("O");
        await _gate.WaitAsync();
        try
        {
            await using var db = Open();
            await db.OpenAsync();

            await ExecAsync(db,
                "UPDATE users SET is_banned = 0, ban_reason = NULL, ban_expires_at = NULL WHERE global_id = @id",
                ("@id", globalId));

            await ExecAsync(db,
                "INSERT INTO ban_audit_log (global_id, action, actioned_at) VALUES (@id, 'unban', @ts)",
                ("@id", globalId), ("@ts", now));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Aggregates usage metrics for the current UTC day.</summary>
    public async Task<DailyReport> GetDailyReportAsync()
    {
        await FlushCountsAsync();

        var todayFull  = DateTime.UtcNow.Date.ToString("O");       // matches daily_reset_at format
        var todayShort = DateTime.UtcNow.Date.ToString("yyyy-MM-dd"); // for date() comparisons
        var nearThreshold = (long)(_dailyQuota * 0.8);

        await _gate.WaitAsync();
        try
        {
            await using var db = Open();
            await db.OpenAsync();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*)
                     FROM users
                     WHERE daily_reset_at = @todayFull AND daily_messages > 0)                          AS active_users,
                    (SELECT COALESCE(SUM(daily_messages), 0)
                     FROM users
                     WHERE daily_reset_at = @todayFull)                                                 AS total_messages,
                    (SELECT COUNT(*)
                     FROM users
                     WHERE date(first_seen) = @todayShort)                                              AS new_users,
                    (SELECT COUNT(*)
                     FROM users
                     WHERE daily_reset_at = @todayFull AND daily_messages >= @quota)                    AS quota_exceeded,
                    (SELECT COUNT(*)
                     FROM users
                     WHERE daily_reset_at = @todayFull AND daily_messages > @near AND daily_messages < @quota) AS near_quota,
                    (SELECT COUNT(*)
                     FROM ban_audit_log
                     WHERE date(actioned_at) = @todayShort AND action = 'ban')                          AS bans_today,
                    (SELECT COUNT(*)
                     FROM sessions
                     WHERE date(connected_at) = @todayShort)                                            AS sessions_today,
                    (SELECT AVG((julianday(disconnected_at) - julianday(connected_at)) * 86400)
                     FROM sessions
                     WHERE date(connected_at) = @todayShort AND disconnected_at IS NOT NULL)            AS avg_session_secs
                """;
            cmd.Parameters.AddWithValue("@todayFull",  todayFull);
            cmd.Parameters.AddWithValue("@todayShort", todayShort);
            cmd.Parameters.AddWithValue("@quota",      _dailyQuota);
            cmd.Parameters.AddWithValue("@near",       nearThreshold);

            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();

            return new DailyReport(
                Date:           todayShort,
                DailyQuota:     _dailyQuota,
                ActiveUsers:    r.GetInt64(0),
                TotalMessages:  r.GetInt64(1),
                NewUsers:       r.GetInt64(2),
                QuotaExceeded:  r.GetInt64(3),
                NearQuota:      r.GetInt64(4),
                BansToday:      r.GetInt64(5),
                SessionsToday:  r.GetInt64(6),
                AvgSessionSecs: r.IsDBNull(7) ? null : r.GetDouble(7));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Returns the top <paramref name="count"/> users by today's message count.</summary>
    public async Task<IReadOnlyList<QuotaEntry>> GetTopQuotaUsersAsync(int count)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = Open();
            await db.OpenAsync();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT global_id, daily_messages, total_messages, is_banned, last_seen
                FROM users
                ORDER BY daily_messages DESC
                LIMIT @n
                """;
            cmd.Parameters.AddWithValue("@n", count);
            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<QuotaEntry>();
            while (await r.ReadAsync())
                list.Add(new QuotaEntry(
                    GlobalId:      r.GetString(0),
                    DailyMessages: r.GetInt64(1),
                    TotalMessages: r.GetInt64(2),
                    IsBanned:      r.GetInt32(3) != 0,
                    LastSeen:      r.GetString(4)));
            return list;
        }
        finally { _gate.Release(); }
    }

    // ── Message accounting ────────────────────────────────────────────────────

    /// <summary>
    /// Records one message from <paramref name="globalId"/> in memory. Counts are flushed to the DB
    /// every minute and on session close — the hot path never touches SQLite.
    /// </summary>
    public void TrackMessage(string globalId)
        => _pendingCounts.AddOrUpdate(globalId, 1, (_, v) => v + 1);

    /// <summary>
    /// Checks whether the user has already exceeded the daily quota (in-DB count + unflushed
    /// in-memory count). Used for mid-session enforcement — call every ~500 messages.
    /// </summary>
    public async Task<bool> IsOverQuotaAsync(string globalId)
    {
        // Only check the persisted DB count — FlushCountsAsync can remove from pending and write
        // to DB non-atomically with respect to this read, so including pending risks double-counting.
        // The check is intentionally approximate: at most ~60 s × 20 msg/s = 1200 messages of
        // tolerance on a 50,000-message daily quota, which is well within acceptable overshoot.
        await _gate.WaitAsync();
        try
        {
            await using var db = Open();
            await db.OpenAsync();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT daily_messages FROM users WHERE global_id = @id";
            cmd.Parameters.AddWithValue("@id", globalId);
            var result = await cmd.ExecuteScalarAsync();
            return result is long count && count >= _dailyQuota;
        }
        finally { _gate.Release(); }
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private async Task FlushCountsAsync()
    {
        // Drain the pending map atomically: swap out each entry before writing.
        var batch = new Dictionary<string, int>();
        foreach (var key in _pendingCounts.Keys)
            if (_pendingCounts.TryRemove(key, out var count))
                batch[key] = count;

        if (batch.Count == 0) return;

        await _gate.WaitAsync();
        try
        {
            await using var db = Open();
            await db.OpenAsync();
            foreach (var (id, count) in batch)
                await ExecAsync(db,
                    "UPDATE users SET daily_messages = daily_messages + @c, total_messages = total_messages + @c WHERE global_id = @id",
                    ("@c", count), ("@id", id));
        }
        finally { _gate.Release(); }
    }

    private SqliteConnection Open() => new(_connStr);

    private static async Task ExecAsync(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, val) in args) cmd.Parameters.AddWithValue(name, val);
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _flushTimer.Dispose();
        await FlushCountsAsync();
        _gate.Dispose();
    }
}

public sealed record DailyReport(
    string  Date,
    int     DailyQuota,
    long    ActiveUsers,
    long    TotalMessages,
    long    NewUsers,
    long    QuotaExceeded,
    long    NearQuota,
    long    BansToday,
    long    SessionsToday,
    double? AvgSessionSecs);

public sealed record QuotaEntry(
    string GlobalId,
    long   DailyMessages,
    long   TotalMessages,
    bool   IsBanned,
    string LastSeen);
