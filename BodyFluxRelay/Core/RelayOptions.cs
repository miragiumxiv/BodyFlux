using System.Security.Cryptography;

namespace BodyFluxRelay.Core;

/// <summary>
/// All runtime configuration, resolved once from environment variables (and the CLI port arg) so
/// the rest of the relay just reads typed properties.
///
///   PORT                          HTTP listen port (Railway/Render/Fly set this). CLI arg, then 8080.
///   BODYFLUX_LOG_SALT             Fixed secret → log hashes stay stable across restarts (lets an id
///                                 be recognised / banned over time). Unset → random per-process salt:
///                                 maximally private, but a player hashes differently after a restart.
///   BODYFLUX_LOG_DIR              Directory for the dated log files (default: &lt;app&gt;/logs).
///   BODYFLUX_LOG_RETENTION_HOURS  Delete log files older than this (default: 48).
/// </summary>
public sealed record RelayOptions
{
    public required int      Port         { get; init; }
    public required string   LogSalt      { get; init; }
    public required string   LogDir       { get; init; }
    public required TimeSpan LogRetention { get; init; }

    public static RelayOptions FromEnvironment(string[] args) => new()
    {
        Port    = ResolvePort(args),
        LogSalt = Environment.GetEnvironmentVariable("BODYFLUX_LOG_SALT") is { Length: > 0 } salt
            ? salt
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
        LogDir  = Environment.GetEnvironmentVariable("BODYFLUX_LOG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "logs"),
        LogRetention = TimeSpan.FromHours(
            Environment.GetEnvironmentVariable("BODYFLUX_LOG_RETENTION_HOURS") is string rh
            && int.TryParse(rh, out var hours) && hours > 0 ? hours : 48),
    };

    // Resolution order: $PORT (cloud hosts) → CLI arg → 8080 default.
    private static int ResolvePort(string[] args)
        => Environment.GetEnvironmentVariable("PORT") is string ep && int.TryParse(ep, out var envPort)
            ? envPort
            : args.FirstOrDefault() is string a && int.TryParse(a, out var argPort) ? argPort : 8080;
}
