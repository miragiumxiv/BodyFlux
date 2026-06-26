using System.Net.WebSockets;
using System.Text.Json;

namespace BodyFluxRelay.Core;

/// <summary>
/// Handles the lifetime of a single WebSocket connection: identity + ban check, room join,
/// message routing, and session logging.
///
/// Phase 4 protections (per connection):
///   - Max peers per room: reject with 1008 PolicyViolation before joining.
///   - Token-bucket rate limiter: 20 msg/s sustained, 30-token burst; excess messages are
///     silently dropped (no disconnect) so a single fast client can't starve others.
///   - Mid-session quota: checked every 500 messages against the daily limit; excess → 1008.
///
/// Message routing:
///   - Messages with a "target" field → forwarded immediately to all peers.
///   - Non-targeted "frame" / "stop" / "hello" → written to the state store; the batch timer
///     in <see cref="RoomStateStore"/> delivers them to peers at ≤10 Hz.
/// </summary>
public sealed class RelayConnection
{
    private const int    BufferSize         = 512 * 1024;
    private const int    QuotaCheckInterval = 500;   // messages between mid-session quota checks
    private const double RateBurst          = 30.0;  // max token burst
    private const double RatePerSecond      = 20.0;  // sustained rate tokens/s

    private readonly RoomStateStore _rooms;
    private readonly RelayLog       _log;
    private readonly UserStore      _users;
    private readonly RelayOptions   _options;

    public RelayConnection(RoomStateStore rooms, RelayLog log, UserStore users, RelayOptions options)
    {
        _rooms   = rooms;
        _log     = log;
        _users   = users;
        _options = options;
    }

    public async Task HandleAsync(HttpContext ctx, WebSocket ws, string groupCode)
    {
        // ── Identity & access check ───────────────────────────────────────────
        var globalId  = ResolveGlobalId(ctx);
        var sessionId = -1L;

        if (globalId != null)
        {
            var (banned, reason) = await _users.CheckAndUpsertAsync(globalId);
            if (banned)
            {
                _log.Write($"[X] room={groupCode}  reason={reason}");
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                    reason ?? "Access denied", CancellationToken.None);
                return;
            }

            var roomHash = _users.DeriveRoomHash(groupCode);
            sessionId    = await _users.OpenSessionAsync(globalId, roomHash);
        }

        // ── Max peers check (atomic join) ─────────────────────────────────────
        var (peerCount, isNew) = _rooms.Join(groupCode, ws, _options.MaxPeersPerRoom);
        if (peerCount < 0)
        {
            _log.Write($"[!] room={groupCode}  reason=full ({_options.MaxPeersPerRoom} max)");
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Room is full", CancellationToken.None);
            if (globalId != null && sessionId >= 0) await _users.CloseSessionAsync(sessionId);
            return;
        }
        var gidLabel  = globalId ?? "anon";
        var startTime = DateTime.UtcNow;
        if (isNew) _log.Write($"[room+] room={groupCode}");
        _log.Write($"[+] room={groupCode}  peers={peerCount}  gid={gidLabel}  total={_rooms.TotalConnections}");

        // Send room config to the newly connected client.
        var welcome = JsonSerializer.SerializeToUtf8Bytes(new { type = "welcome", maxPeers = _options.MaxPeersPerRoom });
        await ws.SendAsync(welcome, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        // ── Per-connection state ──────────────────────────────────────────────
        string? rawPeerId    = null;
        var     bucket       = new TokenBucket(RateBurst, RatePerSecond);
        var     msgCount     = 0;
        var     droppedCount = 0;
        var     recvBytes    = 0L;
        var     buffer       = new byte[BufferSize];

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(ws, buffer);
                if (message == null) break;

                // ── Rate limit: drop excess messages silently ─────────────────
                if (!bucket.TryConsume()) { droppedCount++; continue; }

                msgCount++;
                recvBytes += message.Length;
                var parsed = Parse(message);

                // Capture peer id once we see a "sender" field.
                if (rawPeerId == null && parsed.Sender != null)
                {
                    rawPeerId = parsed.Sender;
                    if (globalId != null && sessionId >= 0)
                        _users.LinkSessionPeer(sessionId, _log.IdFor(rawPeerId));
                }

                if (globalId != null)
                {
                    _users.TrackMessage(globalId);

                    // ── Mid-session quota check ───────────────────────────────
                    if (msgCount % QuotaCheckInterval == 0 && await _users.IsOverQuotaAsync(globalId))
                    {
                        _log.Write($"[X] room={groupCode}  gid={gidLabel}  reason=quota exceeded mid-session");
                        await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                            "Daily message quota exceeded — try again tomorrow", CancellationToken.None);
                        break;
                    }
                }

                // ── Message routing ───────────────────────────────────────────
                // Targeted messages (private morphs) → immediate forward.
                // Non-targeted state messages → state store → batch delivery.
                if (parsed.Target != null || parsed.Type == null)
                    await ForwardAsync(groupCode, ws, message);
                else if (parsed.Sender != null)
                    _rooms.UpdatePeerState(groupCode, parsed.Sender, parsed.Type,
                                           parsed.Profile, parsed.Base, parsed.Consent);
            }
        }
        catch { /* abrupt disconnect — handled in finally */ }
        finally
        {
            var sentBytes                                           = _rooms.TakeBytesSent(ws);
            var (remaining, totalMsgs, totalIn, totalOut, roomDur) = _rooms.Leave(groupCode, ws, rawPeerId, msgCount, recvBytes, sentBytes);
            var duration                                           = DateTime.UtcNow - startTime;
            _log.Write($"[-] room={groupCode}  peers={remaining}  gid={gidLabel}  msgs={msgCount}  dropped={droppedCount}  in={FormatBytes(recvBytes)}  out={FormatBytes(sentBytes)}  dur={duration:h\\:mm\\:ss}  total={_rooms.TotalConnections}");
            if (remaining == 0) _log.Write($"[room-] room={groupCode}  msgs={totalMsgs}  in={FormatBytes(totalIn)}  out={FormatBytes(totalOut)}  dur={roomDur:h\\:mm\\:ss}");

            if (globalId != null && sessionId >= 0)
                await _users.CloseSessionAsync(sessionId);
        }
    }

    // ── Token-bucket rate limiter ─────────────────────────────────────────────

    private struct TokenBucket
    {
        private double _tokens;
        private long   _lastTick; // Environment.TickCount64 (ms)
        private readonly double _capacity;
        private readonly double _perMs;   // tokens per millisecond

        public TokenBucket(double capacity, double tokensPerSecond)
        {
            _capacity = capacity;
            _perMs    = tokensPerSecond / 1000.0;
            _tokens   = capacity;
            _lastTick = Environment.TickCount64;
        }

        public bool TryConsume()
        {
            var now = Environment.TickCount64;
            _tokens   = Math.Min(_capacity, _tokens + (now - _lastTick) * _perMs);
            _lastTick = now;
            if (_tokens < 1.0) return false;
            _tokens -= 1.0;
            return true;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string? ResolveGlobalId(HttpContext ctx)
    {
        var iid = ctx.Request.Query["iid"].ToString();
        if (iid.Length != 32 || !IsHex(iid)) return null;
        return _users.DeriveGlobalId(iid);
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    private static async Task<byte[]?> ReceiveMessageAsync(WebSocket ws, byte[] buffer)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return ms.ToArray();
    }

    private async Task ForwardAsync(string groupCode, WebSocket self, byte[] message)
    {
        List<WebSocket> dead = [];
        var seg = new ArraySegment<byte>(message);
        foreach (var peer in _rooms.PeersExcept(groupCode, self))
        {
            try
            {
                await peer.SendAsync(seg, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                _rooms.TrackBytesSent(peer, message.Length);
            }
            catch { dead.Add(peer); }
        }
        _rooms.RemoveDead(groupCode, dead);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024             => $"{bytes}B",
        < 1024 * 1024      => $"{bytes / 1024.0:F1}KB",
        _                  => $"{bytes / (1024.0 * 1024):F2}MB",
    };

    // ── Message parser ────────────────────────────────────────────────────────

    private readonly record struct ParsedMsg(
        string? Type, string? Sender, string? Target,
        string? Profile, string? Base, bool Consent);

    private static ParsedMsg Parse(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var r = doc.RootElement;
            return new ParsedMsg(
                Type:    Str(r, "type"),
                Sender:  Str(r, "sender"),
                Target:  Str(r, "target"),
                Profile: Str(r, "profile"),
                Base:    Str(r, "base"),
                Consent: r.TryGetProperty("consent", out var cv) && cv.ValueKind == JsonValueKind.True);
        }
        catch { return default; }
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
