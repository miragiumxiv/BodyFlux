using System.Security.Cryptography;
using System.Text;

namespace BodyFluxRelay.Core;

/// <summary>
/// All runtime configuration, resolved once from environment variables (and the CLI port arg) so
/// the rest of the relay just reads typed properties.
///
///   PORT                          HTTP listen port (Railway/Render/Fly set this). CLI arg, then 8080.
///   BODYFLUX_SERVER_SECRET        Required. HMAC key used to derive stable global user ids and room
///                                 hashes stored in the DB. Must never change — changing it invalidates
///                                 all existing global ids (bans stop working). The log-display salt
///                                 is also derived from this secret, so log IDs stay stable for free.
///   BODYFLUX_LOG_DIR              Directory for the dated log files (default: &lt;app&gt;/logs).
///   BODYFLUX_LOG_RETENTION_HOURS  Delete log files older than this (default: 168h aka 7 days).
///   BODYFLUX_DB_PATH              Path to the SQLite database file (default: &lt;app&gt;/bodyflux.db).
///   BODYFLUX_DAILY_QUOTA          Max messages a single user may send per day (default: 200000).
///   BODYFLUX_MAX_PEERS            Max peers allowed per room (default: 5).
///   BODYFLUX_MAX_CONNECTIONS      Max total concurrent WebSocket connections (default: 100).
///                                 New connections are rejected with 503 when this limit is reached.
///   BODYFLUX_MAX_CONNECTIONS_PER_IP  Max concurrent WebSocket connections from a single IP (default: 10).
///                                    Excess connections are rejected with 429.
///   BODYFLUX_MAINTENANCE          Set to "true" or "1" to reject all new WebSocket connections with
///                                 503. Flip at runtime with: fly secrets set BODYFLUX_MAINTENANCE=true
///   BODYFLUX_ADMIN_KEY            Bearer token required for admin endpoints. Unset → endpoints
///                                 return 404 so their existence is not discoverable.
/// </summary>
public sealed record RelayOptions
{
    public required int      Port             { get; init; }
    public required string   ServerSecret     { get; init; }
    public required string   LogSalt          { get; init; }
    public required string   LogDir           { get; init; }
    public required TimeSpan LogRetention     { get; init; }
    public required string   DbPath           { get; init; }
    public required int      DailyQuota       { get; init; }
    public required int      MaxPeersPerRoom       { get; init; }
    public required int      MaxConnections        { get; init; }
    public required int      MaxConnectionsPerIp   { get; init; }
    public required bool     MaintenanceMode       { get; init; }
    public          string?  AdminKey              { get; init; }

    public static RelayOptions FromEnvironment(string[] args)
    {
        var serverSecret = Environment.GetEnvironmentVariable("BODYFLUX_SERVER_SECRET") is { Length: > 0 } sec
            ? sec
            : throw new InvalidOperationException(
                "BODYFLUX_SERVER_SECRET is required but not set. " +
                "Set it to a long random secret and keep it stable — changing it invalidates all stored user ids.");

        // Derived from ServerSecret — stable across restarts without a separate env var.
        var logSalt = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(serverSecret), "log-salt-v1"u8.ToArray()));

        return new()
        {
            Port            = ResolvePort(args),
            ServerSecret    = serverSecret,
            LogSalt         = logSalt,
            LogDir          = Environment.GetEnvironmentVariable("BODYFLUX_LOG_DIR") is { Length: > 0 } dir
                ? dir
                : Path.Combine(AppContext.BaseDirectory, "logs"),
            LogRetention    = TimeSpan.FromHours(
                Environment.GetEnvironmentVariable("BODYFLUX_LOG_RETENTION_HOURS") is string rh
                && int.TryParse(rh, out var hours) && hours > 0 ? hours : 168),
            DbPath          = Environment.GetEnvironmentVariable("BODYFLUX_DB_PATH") is { Length: > 0 } db
                ? db
                : Path.Combine(AppContext.BaseDirectory, "bodyflux.db"),
            DailyQuota      = Environment.GetEnvironmentVariable("BODYFLUX_DAILY_QUOTA") is string dq
                && int.TryParse(dq, out var quota) && quota > 0 ? quota : 200_000,
            MaxPeersPerRoom = Environment.GetEnvironmentVariable("BODYFLUX_MAX_PEERS") is string mp
                && int.TryParse(mp, out var peers) && peers > 0 ? peers : 5,
            MaxConnections  = Environment.GetEnvironmentVariable("BODYFLUX_MAX_CONNECTIONS") is string mc
                && int.TryParse(mc, out var maxConn) && maxConn > 0 ? maxConn : 100,
            MaxConnectionsPerIp = Environment.GetEnvironmentVariable("BODYFLUX_MAX_CONNECTIONS_PER_IP") is string mcip
                && int.TryParse(mcip, out var maxIp) && maxIp > 0 ? maxIp : 10,
            MaintenanceMode = Environment.GetEnvironmentVariable("BODYFLUX_MAINTENANCE") is "true" or "1",
            AdminKey        = Environment.GetEnvironmentVariable("BODYFLUX_ADMIN_KEY") is { Length: > 0 } ak ? ak : null,
        };
    }

    // Resolution order: $PORT (cloud hosts) → CLI arg → 8080 default.
    private static int ResolvePort(string[] args)
        => Environment.GetEnvironmentVariable("PORT") is string ep && int.TryParse(ep, out var envPort)
            ? envPort
            : args.FirstOrDefault() is string a && int.TryParse(a, out var argPort) ? argPort : 8080;
}
