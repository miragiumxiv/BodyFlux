using System.Security.Cryptography;
using System.Text;

namespace BodyFluxRelay.Core;

/// <summary>
/// Privacy-preserving connection log. Real names are never written: a value is reduced to a salted
/// SHA-256 prefix (e.g. <c>user=a1b2c3d4</c>), enough to correlate "same id reconnected" or flag an
/// abuser without revealing who they are. Lines go to the console and to a dated file; files past
/// the retention window are purged on startup and once an hour thereafter.
/// </summary>
public sealed class RelayLog : IDisposable
{
    private readonly string   _salt;
    private readonly string   _dir;
    private readonly TimeSpan _retention;
    private readonly object   _fileLock = new();
    private readonly Timer    _purgeTimer;

    public RelayLog(RelayOptions options)
    {
        _salt      = options.LogSalt;
        _dir       = options.LogDir;
        _retention = options.LogRetention;

        Directory.CreateDirectory(_dir);
        Purge(); // clear anything already past retention before accepting connections
        _purgeTimer = new Timer(_ => Purge(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    /// <summary>Writes a UTC-timestamped line to the console and the current day's log file.</summary>
    public void Write(string line)
    {
        var stamped = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z  {line}";
        Console.WriteLine(stamped);
        try
        {
            lock (_fileLock)
                File.AppendAllText(
                    Path.Combine(_dir, $"relay-{DateTime.UtcNow:yyyyMMdd}.log"),
                    stamped + Environment.NewLine);
        }
        catch { /* logging must never take the relay down */ }
    }

    /// <summary>
    /// Salted SHA-256 of <paramref name="value"/> → first 8 hex chars. Not reversible; the salt
    /// stops it from being cracked with a precomputed table of known names.
    /// </summary>
    public string IdFor(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(_salt + "\0" + value));
        return Convert.ToHexString(bytes.AsSpan(0, 4)).ToLowerInvariant();
    }

    private void Purge()
    {
        try
        {
            var cutoff = DateTime.UtcNow - _retention;
            foreach (var file in Directory.EnumerateFiles(_dir, "relay-*.log"))
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
        }
        catch { /* best-effort cleanup */ }
    }

    public void Dispose() => _purgeTimer.Dispose();
}
