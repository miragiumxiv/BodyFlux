using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace BodyFluxRelay.Core;

/// <summary>
/// Manages room membership (WebSocket connections) and the live morph state of every peer, and
/// drives the batch broadcast loop that delivers state snapshots to all room members at ≤10 Hz.
///
/// Message routing (decided by <see cref="RelayConnection"/>):
///   - Non-targeted frames/stops/hellos → <see cref="UpdatePeerState"/> → batched delivery
///   - Targeted morphs → direct forward via <see cref="PeersExcept"/> (unchanged behaviour)
///
/// This replaces the former RoomRegistry.
/// </summary>
public sealed class RoomStateStore : IAsyncDisposable
{
    private const int StaleSeconds    = 90;
    private const int BatchIntervalMs = 50;  // 20 Hz max outbound rate per room

    // ── Per-peer state ────────────────────────────────────────────────────────
    private sealed class PeerState
    {
        public string?  Profile;    // packed+encoded profile from last "frame"; null = no active morph
        public bool     IsStopped;  // true when the last action was a "stop"
        public string?  Base;       // packed+encoded base profile from last "hello"
        public bool     Consent;
        public DateTime LastSeen;
    }

    private sealed class Room
    {
        public readonly List<WebSocket>               Sockets   = [];
        public readonly Dictionary<string, PeerState> States    = new(StringComparer.Ordinal);
        public bool                                   IsDirty;
        public readonly DateTime                      CreatedAt = DateTime.UtcNow;
        public long                                   TotalMsgs;
        public long                                   TotalInBytes;
        public long                                   TotalOutBytes;
    }

    private readonly Dictionary<string, Room>              _rooms     = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<WebSocket, long> _bytesSent = new();
    private readonly object                                _gate      = new();
    private readonly CancellationTokenSource               _cts       = new();
    private readonly Task                                  _batchTask;
    private readonly RelayLog                              _log;

    /// <summary>Total open WebSocket connections across all rooms.</summary>
    public int TotalConnections { get { lock (_gate) return _rooms.Values.Sum(r => r.Sockets.Count); } }

    /// <summary>Number of active rooms.</summary>
    public int RoomCount { get { lock (_gate) return _rooms.Count; } }

    public RoomStateStore(RelayLog log)
    {
        _log       = log;
        _batchTask = Task.Run(RunBatchLoopAsync);
    }

    // ── Room membership ───────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to add <paramref name="ws"/> to the room. Returns the new peer count (or -1 if the
    /// room is full) and whether the room was freshly created by this call.
    /// </summary>
    public (int PeerCount, bool IsNew) Join(string groupCode, WebSocket ws, int maxPeers)
    {
        lock (_gate)
        {
            var isNew = !_rooms.ContainsKey(groupCode);
            var room  = EnsureRoom(groupCode);
            if (room.Sockets.Count >= maxPeers) return (-1, false);
            room.Sockets.Add(ws);
            return (room.Sockets.Count, isNew);
        }
    }

    /// <summary>
    /// Removes <paramref name="ws"/> from the room, accumulates its session bytes into the room
    /// totals, and evicts the peer state if <paramref name="peerId"/> is provided.
    /// Returns the remaining peer count and the room's cumulative byte totals (useful when
    /// the room is destroyed, i.e. remaining == 0).
    /// </summary>
    public (int Remaining, long TotalMsgs, long TotalIn, long TotalOut, TimeSpan Duration) Leave(
        string groupCode, WebSocket ws, string? peerId, long msgs, long inBytes, long outBytes)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(groupCode, out var room)) return (0, 0, 0, 0, TimeSpan.Zero);
            room.Sockets.Remove(ws);
            room.TotalMsgs     += msgs;
            room.TotalInBytes  += inBytes;
            room.TotalOutBytes += outBytes;
            if (peerId != null) { room.States.Remove(peerId); room.IsDirty = true; }
            var duration = DateTime.UtcNow - room.CreatedAt;
            Prune(groupCode, room);
            return (room.Sockets.Count, room.TotalMsgs, room.TotalInBytes, room.TotalOutBytes, duration);
        }
    }

    /// <summary>Snapshot of open sockets in the room, excluding <paramref name="self"/>.</summary>
    public List<WebSocket> PeersExcept(string groupCode, WebSocket self)
    {
        lock (_gate)
            return _rooms.TryGetValue(groupCode, out var room)
                ? room.Sockets.Where(s => s != self && s.State == WebSocketState.Open).ToList()
                : [];
    }

    /// <summary>Prunes sockets that failed to receive a message.</summary>
    public void RemoveDead(string groupCode, IReadOnlyCollection<WebSocket> dead)
    {
        if (dead.Count == 0) return;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(groupCode, out var room)) return;
            foreach (var d in dead)
            {
                room.Sockets.Remove(d);
                if (_bytesSent.TryRemove(d, out var b)) room.TotalOutBytes += b;
            }
            Prune(groupCode, room);
        }
    }

    // ── Bandwidth tracking ────────────────────────────────────────────────────

    /// <summary>Records bytes successfully sent to a specific socket (called after each send).</summary>
    public void TrackBytesSent(WebSocket ws, int bytes) =>
        _bytesSent.AddOrUpdate(ws, bytes, (_, n) => n + bytes);

    /// <summary>Returns total bytes sent to the socket and removes its entry.</summary>
    public long TakeBytesSent(WebSocket ws)
    {
        _bytesSent.TryRemove(ws, out var bytes);
        return bytes;
    }

    // ── State store ───────────────────────────────────────────────────────────

    /// <summary>
    /// Applies one non-targeted message to the peer's state entry and marks the room dirty so the
    /// next batch tick delivers the updated snapshot to all members.
    /// </summary>
    public void UpdatePeerState(string groupCode, string peerId,
                                string msgType, string? profile, string? baseProfile, bool consent)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(groupCode, out var room)) return;
            if (!room.States.TryGetValue(peerId, out var st))
                room.States[peerId] = st = new PeerState();

            st.LastSeen = DateTime.UtcNow;
            st.Consent  = consent;

            switch (msgType)
            {
                case "frame":
                    st.Profile   = profile;
                    st.IsStopped = false;
                    break;
                case "stop":
                    st.Profile   = null;
                    st.IsStopped = true;
                    break;
                // "hello": update consent/base without touching the last morph state.
            }

            if (baseProfile != null) st.Base = baseProfile;
            room.IsDirty = true;
        }
    }

    // ── Batch broadcast loop ──────────────────────────────────────────────────

    private async Task RunBatchLoopAsync()
    {
        // Use a Stopwatch-based scheduler to compensate for Task.Delay imprecision (~15 ms
        // Windows timer resolution). Without compensation each tick drifts independently,
        // producing variable delivery intervals that manifest as animation jitter on receivers.
        var nextTick = Environment.TickCount64 + BatchIntervalMs;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var delay = (int)(nextTick - Environment.TickCount64);
                if (delay > 0) await Task.Delay(delay, _cts.Token);
                nextTick += BatchIntervalMs;

                // Collect dirty rooms and build payloads while holding the lock (no async I/O here).
                // Carry the Room reference so dead-socket cleanup stays O(dead), not O(rooms × dead).
                List<(List<WebSocket> Sockets, byte[] Payload, Room RoomRef)> work           = [];
                List<(string Code, int Count)>                                work_evictions = [];
                lock (_gate)
                {
                    var cutoff = DateTime.UtcNow.AddSeconds(-StaleSeconds);
                    foreach (var (code, room) in _rooms)
                    {
                        if (!room.IsDirty || room.Sockets.Count == 0) continue;
                        room.IsDirty = false;

                        // Evict peers whose connection silently died without a clean disconnect.
                        var staleKeys = room.States
                            .Where(kv => kv.Value.LastSeen < cutoff)
                            .Select(kv => kv.Key).ToList();
                        foreach (var stale in staleKeys)
                            room.States.Remove(stale);
                        if (staleKeys.Count > 0)
                            work_evictions.Add((code, staleKeys.Count));

                        var payload = BuildBatch(room.States);
                        if (payload != null)
                            work.Add((room.Sockets.Where(s => s.State == WebSocketState.Open).ToList(), payload, room));
                    }
                }

                // Log evictions outside the lock.
                foreach (var (code, count) in work_evictions)
                    _log.Write($"[evict] room={code}  count={count}  reason=stale ({StaleSeconds}s)");

                // Send outside the lock; remove failed sockets from their own room only.
                foreach (var (sockets, payload, roomRef) in work)
                {
                    var seg  = new ArraySegment<byte>(payload);
                    List<WebSocket> dead = [];
                    foreach (var ws in sockets)
                    {
                        try
                        {
                            await ws.SendAsync(seg, WebSocketMessageType.Text, true, CancellationToken.None);
                            TrackBytesSent(ws, payload.Length);
                        }
                        catch { dead.Add(ws); }
                    }

                    if (dead.Count > 0)
                        lock (_gate)
                            foreach (var d in dead) roomRef.Sockets.Remove(d);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Batch builder ─────────────────────────────────────────────────────────

    private static byte[]? BuildBatch(Dictionary<string, PeerState> states)
    {
        if (states.Count == 0) return null;

        using var ms     = new MemoryStream(capacity: 512);
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { SkipValidation = true });

        writer.WriteStartObject();
        writer.WriteString("type", "batch");
        writer.WriteStartObject("peers");

        foreach (var (peerId, st) in states)
        {
            writer.WriteStartObject(peerId);
            writer.WriteBoolean("consent", st.Consent);

            // Write enc once if any packed field is present.
            bool hasPackedData = !st.IsStopped && (st.Profile != null || st.Base != null);
            if (hasPackedData) writer.WriteString("enc", "d");

            if (st.IsStopped)
                writer.WriteBoolean("stop", true);
            else if (st.Profile != null)
                writer.WriteString("profile", st.Profile);

            if (st.Base != null)
                writer.WriteString("base", st.Base);

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Room EnsureRoom(string groupCode)
    {
        if (!_rooms.TryGetValue(groupCode, out var room))
            _rooms[groupCode] = room = new Room();
        return room;
    }

    private int Prune(string groupCode, Room room)
    {
        if (room.Sockets.Count > 0) return room.Sockets.Count;
        _rooms.Remove(groupCode);
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _batchTask; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
