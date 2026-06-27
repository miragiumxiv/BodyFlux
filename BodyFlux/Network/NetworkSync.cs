using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BodyFlux.Network;

/// <summary>
/// Manages a persistent WebSocket connection to the BodyFluxRelay server.
/// Sends the local player's working profile JSON to all peers in the same group,
/// and exposes incoming peer frames for the game main thread to apply.
///
/// Thread-safety:
///   SendFrame / SendStop / SendHello  — safe to call from any thread (enqueues, non-blocking)
///   TryDequeue                        — safe to call from the game main thread
///   All WebSocket I/O                 — runs entirely on background tasks
/// </summary>
public sealed class NetworkSync : IDisposable
{
    // ── Inbound frame from a peer ─────────────────────────────────────────────
    /// <param name="SenderId">Opaque peer id (salted hash) of the player who sent this frame.</param>
    /// <param name="ProfileJson">
    ///   Working-profile JSON to apply via SetTemporaryProfileOnCharacter,
    ///   or <see cref="string.Empty"/> as a "stop / delete temp profile" signal.
    /// </param>
    /// <param name="TargetId">
    ///   When non-null the frame is a targeted morph directed at a specific player (by peer id).
    ///   Null means the sender is morphing themselves.
    /// </param>
    public readonly record struct PeerFrame(string SenderId, string ProfileJson, string? TargetId);

    // ── Inbound consent message ───────────────────────────────────────────────
    /// <param name="SenderId">Opaque peer id of the sender.</param>
    /// <param name="IsRequest">True = morph_request; false = morph_consent (reply).</param>
    /// <param name="Accepted">For consent replies only: whether the target accepted.</param>
    public readonly record struct ConsentMsg(string SenderId, bool IsRequest, bool Accepted);

    // ── State ─────────────────────────────────────────────────────────────────
    private volatile string _status = "Connecting…";
    private volatile bool   _connected;
    private volatile bool   _quotaExceeded;

    public string Status        => _status;
    public bool   IsConnected   => _connected;
    public bool   QuotaExceeded => _quotaExceeded;

    // ── Queues ────────────────────────────────────────────────────────────────
    private readonly ConcurrentQueue<PeerFrame>  _inbound        = new();
    private readonly ConcurrentQueue<string>     _outbound       = new();
    private readonly ConcurrentQueue<ConsentMsg> _inboundConsent = new();

    // ── Peer presence ─────────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, DateTime> _peers = new();

    /// <summary>
    /// Live map of peer ids → UTC timestamp of their last received message. Keyed by the opaque
    /// <see cref="PeerIdentity"/> hash, never a real name. Entries older than ~90 s should be
    /// considered gone (missed 3 heartbeats).
    /// </summary>
    public IReadOnlyDictionary<string, DateTime> Peers => _peers;

    // ── Peer consent ──────────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, bool> _peerConsent = new();

    /// <summary>
    /// Map of peer ids → whether they have enabled "allow remote morph".
    /// Updated whenever a hello or heartbeat is received from that peer.
    /// </summary>
    public IReadOnlyDictionary<string, bool> PeerConsent => _peerConsent;

    // ── Peer base profiles ────────────────────────────────────────────────────
    // Each peer broadcasts its own *base* Customize+ scaling (read from its permanent
    // profile, which only that peer can read locally) inside hello messages. We cache it
    // so a targeted morph can use it as the origin and restore it on Reset — solving the
    // "their scaling is unreadable because Lightless applied it as a temp profile" problem.
    private readonly ConcurrentDictionary<string, string> _peerBaseProfile = new();

    /// <summary>
    /// Returns the base Customize+ profile JSON a peer broadcast, or null if not received yet.
    /// Takes the peer's real (local) name and hashes it to the peer id used as the storage key.
    /// </summary>
    public bool TryGetPeerBaseProfile(string peerName, out string? json)
    {
        var ok = _peerBaseProfile.TryGetValue(PeerIdentity.Of(peerName, _groupCode), out var j);
        json = j;
        return ok;
    }

    // ── Room config (received from server on connect) ─────────────────────────
    /// <summary>Maximum peers allowed in this room, as reported by the server. Null until the
    /// welcome message arrives.</summary>
    public int? MaxPeers { get; private set; }

    // ── Local consent ─────────────────────────────────────────────────────────
    /// <summary>
    /// Whether this player allows others to morph their character.
    /// Set this before or after construction; it is included in every hello message.
    /// Call <see cref="SendHello"/> after changing to propagate immediately.
    /// </summary>
    public bool LocalConsent { get; set; }

    /// <summary>
    /// This player's base Customize+ scaling (permanent profile JSON), shared with peers in
    /// every hello so they can use it as the origin when morphing this character.
    /// Call <see cref="SendHello"/> after changing to propagate immediately.
    /// </summary>
    public string? LocalBaseProfile { get; set; }

    // ── Config ────────────────────────────────────────────────────────────────
    private readonly string     _localId;
    private readonly string     _groupCode;
    private readonly string     _relayBase;
    private readonly string     _installIdParam; // "N" format (no hyphens), appended as ?iid=
    private readonly IPluginLog _log;

    /// <summary>This client's own opaque peer id (salted hash of the local name). Never a real name.</summary>
    public string LocalId => _localId;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _runTask;

    // ── Constructor ───────────────────────────────────────────────────────────
    public NetworkSync(string localName, string groupCode, string relayUrl, Guid installId, IPluginLog log)
    {
        _groupCode      = groupCode.Trim();
        _relayBase      = relayUrl.TrimEnd('/');
        _installIdParam = installId.ToString("N"); // 32 hex chars, no hyphens
        _log            = log;

        // The real character name never leaves this process: it is hashed (salted with the Sync
        // Key) into an opaque id, and only the id is ever put on the wire.
        _localId = PeerIdentity.Of(localName, _groupCode);

        _runTask = Task.Run(RunLoopAsync);
    }

    // ── Background connection loop ────────────────────────────────────────────

    private async Task RunLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            _connected = false;
            _peers.Clear();
            _peerConsent.Clear();
            _peerBaseProfile.Clear();
            using var ws = new ClientWebSocket();

            try
            {
                _status = "Connecting…";
                var uri = new Uri($"{_relayBase}/ws/{Uri.EscapeDataString(_groupCode)}?iid={_installIdParam}");
                await ws.ConnectAsync(uri, _cts.Token).ConfigureAwait(false);

                _connected = true;
                _status    = "Connected";
                _log.Debug($"[BodyFlux/Net] connected  room={_groupCode}");

                // Announce presence and consent status immediately
                _outbound.Enqueue(BuildMessage("hello", null));

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                var sendTask      = SendLoopAsync(ws,    linked.Token);
                var receiveTask   = ReceiveLoopAsync(ws, linked.Token);
                var heartbeatTask = HeartbeatLoopAsync(linked.Token);
                await Task.WhenAny(sendTask, receiveTask).ConfigureAwait(false);
                linked.Cancel();
                await Task.WhenAll(sendTask, receiveTask, heartbeatTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _status = "Disconnected — retrying…";
                _log.Warning($"[BodyFlux/Net] {ex.GetType().Name}: {ex.Message}");
            }

            if (_quotaExceeded)
            {
                _status = "Daily quota exceeded — try again tomorrow";
                break;
            }

            if (!_cts.IsCancellationRequested)
                await Task.Delay(5_000, _cts.Token).ConfigureAwait(false);
        }

        _connected = false;
        _status    = "Disconnected";
        _log.Debug("[BodyFlux/Net] disconnected");
    }

    // ── Send loop (background) ────────────────────────────────────────────────

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            while (_outbound.TryDequeue(out var msg))
            {
                var bytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg));
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
                        .ConfigureAwait(false);
            }
            await Task.Delay(8, ct).ConfigureAwait(false);
        }
    }

    // ── Receive loop (background) ─────────────────────────────────────────────

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[512 * 1024];

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (ws.CloseStatusDescription?.Contains("quota", StringComparison.OrdinalIgnoreCase) == true)
                        _quotaExceeded = true;
                    return;
                }
                ms.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);

            try
            {
                var raw     = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                var msg     = JObject.Parse(raw);
                var sender  = msg["sender"]? .Value<string>();  // opaque peer id, not a name
                var type    = msg["type"]?.   Value<string>();
                var profile = msg["profile"]?.Value<string>();
                var target  = msg["target"]?. Value<string>();  // opaque peer id, not a name
                var consent = msg["consent"]?.Value<bool?>();
                var baseP   = msg["base"]?.   Value<string>();

                // Server-originated messages (no "sender") — handle before the sender check.
                if (type == "welcome")
                {
                    MaxPeers = msg["maxPeers"]?.Value<int?>();
                    continue;
                }

                // Batch messages are sent by the server and carry no "sender" field — handle them
                // before the sender check so they are not silently discarded.
                if (type == "batch" && msg["peers"] is JObject batchPeers)
                {
                    var activePids = new HashSet<string>();

                    foreach (var prop in batchPeers.Properties())
                    {
                        var pid  = prop.Name;
                        if (pid == _localId) continue;
                        if (prop.Value is not JObject pData) continue;

                        activePids.Add(pid);

                        var pProfile  = pData["profile"]?.Value<string>();
                        var pConsent  = pData["consent"]?.Value<bool?>();
                        var pBase     = pData["base"]?.   Value<string>();
                        var pStop     = pData["stop"]?.   Value<bool?>() ?? false;

                        if (pData["enc"]?.Value<string>() == "d")
                        {
                            if (!string.IsNullOrEmpty(pProfile)) pProfile = ProfileWire.Unpack(pProfile);
                            if (!string.IsNullOrEmpty(pBase))    pBase    = ProfileWire.Unpack(pBase);
                        }

                        bool isNew = !_peers.ContainsKey(pid);
                        _peers[pid] = DateTime.UtcNow;
                        if (pConsent.HasValue)            _peerConsent[pid]     = pConsent.Value;
                        if (!string.IsNullOrEmpty(pBase)) _peerBaseProfile[pid] = pBase;

                        if (isNew) _outbound.Enqueue(BuildMessage("hello", null, includeBase: true));

                        if (pStop)
                            _inbound.Enqueue(new PeerFrame(pid, string.Empty, null));
                        else if (!string.IsNullOrEmpty(pProfile))
                            _inbound.Enqueue(new PeerFrame(pid, pProfile, null));
                    }

                    // Peers absent from this batch have disconnected — remove them immediately.
                    foreach (var knownPid in _peers.Keys.ToList())
                    {
                        if (activePids.Contains(knownPid)) continue;
                        _peers.TryRemove(knownPid, out _);
                        _peerConsent.TryRemove(knownPid, out _);
                        _peerBaseProfile.TryRemove(knownPid, out _);
                        _inbound.Enqueue(new PeerFrame(knownPid, string.Empty, null));
                    }

                    continue;
                }

                // All other message types are sent directly by a peer and must have a sender.
                if (sender == null || sender == _localId) continue;

                // Unpack DEFLATE+base64 profile payloads back into JSON (see ProfileWire).
                if (msg["enc"]?.Value<string>() == "d")
                {
                    if (!string.IsNullOrEmpty(profile)) profile = ProfileWire.Unpack(profile);
                    if (!string.IsNullOrEmpty(baseP))   baseP   = ProfileWire.Unpack(baseP);
                }

                // Update peer presence, consent, and base scaling for every direct message received.
                bool isNewPeer = !_peers.ContainsKey(sender);
                _peers[sender] = DateTime.UtcNow;
                if (consent.HasValue)
                    _peerConsent[sender] = consent.Value;
                if (!string.IsNullOrEmpty(baseP))
                    _peerBaseProfile[sender] = baseP;

                // First time we hear from this peer — reply with a full hello so they discover us
                // immediately instead of waiting for the next heartbeat.
                if (isNewPeer)
                    _outbound.Enqueue(BuildMessage("hello", null, includeBase: true));

                if (type == "frame" && !string.IsNullOrEmpty(profile))
                    _inbound.Enqueue(new PeerFrame(sender, profile, target));
                else if (type == "stop")
                    _inbound.Enqueue(new PeerFrame(sender, string.Empty, target));
                else if (type == "morph_request" && target == _localId)
                    _inboundConsent.Enqueue(new ConsentMsg(sender, IsRequest: true,  Accepted: false));
                else if (type == "morph_consent" && target == _localId)
                    _inboundConsent.Enqueue(new ConsentMsg(sender, IsRequest: false, Accepted: msg["accepted"]?.Value<bool?>() ?? false));
                // "hello" — presence/consent already updated above
            }
            catch { /* malformed message — skip silently */ }
        }
    }

    // ── Heartbeat loop (background) ───────────────────────────────────────────

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(30_000, ct).ConfigureAwait(false);
                // Periodic presence heartbeat so peers keep seeing us in the group (and the
                // lone-connection auto-disconnect can tell when the group is truly empty). Kept lean
                // — no base profile; that is sent on connect, on change, and in new-peer replies.
                _outbound.Enqueue(BuildMessage("hello", null, includeBase: false));
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueue a working-profile frame to be broadcast to peers.
    /// Pass <paramref name="target"/> to direct the frame at a specific player;
    /// leave null to broadcast a self-morph frame (existing behaviour).
    /// </summary>
    public void SendFrame(string profileJson, string? target = null)
    {
        if (!_connected) return;
        _outbound.Enqueue(BuildMessage("frame", profileJson, target));
    }

    /// <summary>
    /// Notify peers that a morph has been reset.
    /// Pass <paramref name="target"/> when resetting a targeted morph.
    /// </summary>
    public void SendStop(string? target = null)
        => _outbound.Enqueue(BuildMessage("stop", null, target));

    /// <summary>
    /// Immediately broadcasts a hello message with the current <see cref="LocalConsent"/>
    /// value so peers learn about consent changes without waiting for the next heartbeat.
    /// </summary>
    public void SendHello()
    {
        if (_connected)
            _outbound.Enqueue(BuildMessage("hello", null));
    }

    /// <summary>Dequeue one incoming peer frame. Call in a loop until it returns false.</summary>
    public bool TryDequeue(out PeerFrame frame) => _inbound.TryDequeue(out frame);

    /// <summary>Dequeue one incoming consent message. Call in a loop until it returns false.</summary>
    public bool TryDequeueConsent(out ConsentMsg msg) => _inboundConsent.TryDequeue(out msg);

    /// <summary>Sends a morph_request to a target specified by real character name.</summary>
    public void SendMorphRequest(string targetName)
    {
        if (!_connected) return;
        _outbound.Enqueue(BuildMessage("morph_request", null, targetName));
    }

    /// <summary>
    /// Sends a morph_consent reply. <paramref name="targetPeerId"/> is the opaque peer id of the
    /// requester (already hashed — no second hash applied).
    /// </summary>
    public void SendMorphConsent(string targetPeerId, bool accepted)
    {
        if (!_connected) return;
        var obj = new JObject
        {
            ["sender"]   = _localId,
            ["type"]     = "morph_consent",
            ["target"]   = targetPeerId,
            ["accepted"] = accepted
        };
        _outbound.Enqueue(obj.ToString(Formatting.None));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string BuildMessage(string type, string? profile, string? target = null, bool includeBase = true)
    {
        var obj = new JObject { ["sender"] = _localId, ["type"] = type, ["consent"] = LocalConsent };
        // Profile payloads are stripped to {ID,Name,Bones} and DEFLATE+base64-packed; "enc" marks
        // the packed fields so the receiver knows to unpack them. See ProfileWire.
        if (profile != null) { obj["profile"] = ProfileWire.Pack(ProfileWire.Minimize(profile)); obj["enc"] = "d"; }
        // The target is supplied as a real (local) character name; hash it so only the opaque id
        // crosses the relay, mirroring how the sender id is derived.
        if (target  != null) obj["target"]  = PeerIdentity.Of(target, _groupCode);
        // Attach our base scaling only on hello, and only when asked — frames carry their own
        // profile, and presence heartbeats stay lean (peers keep the base cached from earlier).
        if (type == "hello" && includeBase && !string.IsNullOrEmpty(LocalBaseProfile))
        {
            obj["base"] = ProfileWire.Pack(ProfileWire.Minimize(LocalBaseProfile!));
            obj["enc"]  = "d";
        }
        return obj.ToString(Formatting.None);
    }

    public void Dispose()
    {
        _cts.Cancel();

        // Wait for the background loop to actually stop before returning. Without this, the still-
        // running task keeps this assembly's load context alive, so Dalamud can't unload it and the
        // next version of the plugin fails to load on reload/update. The timeout guards against a
        // stuck socket operation. Wait before disposing the CTS so the loop never sees it disposed.
        try { _runTask.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* loop ended via cancellation — expected */ }

        _cts.Dispose();
    }
}
