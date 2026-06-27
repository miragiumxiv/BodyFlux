using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using BodyFlux.Ipc;
using BodyFlux.Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BodyFlux.Services;

public enum ConsentStatus { Idle, Pending, Granted }

/// <summary>
/// Owns the <see cref="NetworkSync"/> connection and everything network-related: connecting,
/// broadcasting the local player's morph frames and base scaling, and applying frames received
/// from peers. The morph engine talks to peers only through this class — it never sees the raw
/// <see cref="NetworkSync"/>. Depends on <see cref="CustomizePlusIpc"/> (to apply incoming frames
/// and read our own base scaling) but not on the morph engine, so the coupling stays one-way.
/// </summary>
public sealed class SyncManager : IDisposable
{
    private const float SendInterval      = 0.05f;  // ~20 Hz broadcast cap
    private const int   PeerStaleSeconds  = 90;     // peers unseen this long are treated as gone
    private const float InactivityTimeout = 60f;    // 1 minute alone + idle → auto-disconnect
    private const float InterpInterval    = 0.05f;  // seconds between server batch ticks; interp window

    // Per-peer interpolation state: smoothly blends from the last displayed bone snapshot to the
    // latest received one, advancing each game tick instead of jumping at the 20 Hz batch rate.
    private sealed class PeerInterpState
    {
        public JObject? From;     // bones at start of current segment (last displayed position)
        public JObject? To;       // bones at end of current segment (latest received snapshot)
        public float    Elapsed;  // seconds elapsed since To was received
        public ushort?  Index;    // cached ObjectTable index (refreshed each time a new frame arrives)
        public bool     IsLocal;  // true when applying to index 0 (frame was targeted at this player)
    }

    private readonly CustomizePlusIpc _ipc;
    private readonly IObjectTable     _objects;
    private readonly IPluginLog       _log;
    private readonly Configuration    _config;
    private readonly IChatGui         _chat;

    private NetworkSync? _net;
    private float        _sendTimer;
    private string?      _lastSentJson;
    private string?      _lastSentTarget;
    private readonly Dictionary<string, PeerInterpState> _peerInterp = new();

    // ── Consent state (sender side) ────────────────────────────────────────────
    // Keyed by opaque peer id of the target we requested consent from.
    private readonly Dictionary<string, ConsentStatus> _consentStatus = new();
    // Called once when consent is granted so the morph can start automatically.
    private Action? _onConsentGranted;

    // ── Incoming consent requests (receiver side) ──────────────────────────────
    // Each entry is the opaque peer id + display name of the requester.
    private readonly List<(string PeerId, string DisplayName)> _incomingRequests = new();
    // Timestamp of the last time another live peer was present in the group (or the connect time).
    // While we are alone this stops updating, so (now - _lastPeerSeen) measures how long we have been
    // the only player in the Pair Key. Auto-disconnect fires after InactivityTimeout of being alone —
    // morph activity does NOT affect it, since the only point of the relay link is sharing with peers.
    private DateTime     _lastPeerSeen = DateTime.MinValue;
    private bool         _idleDisconnected;

    public SyncManager(CustomizePlusIpc ipc, IObjectTable objects, IPluginLog log, Configuration config, IChatGui chat)
    {
        _ipc     = ipc;
        _objects = objects;
        _log     = log;
        _config  = config;
        _chat    = chat;
    }

    // ── Status / read-only state (for the UI) ─────────────────────────────────

    public string Status           => _net?.Status ?? (_idleDisconnected ? "Disconnected (idle)" : "Disabled");
    public int?   MaxPeers        => _net?.MaxPeers;
    public bool   IsActive         => _net != null;
    public bool   IsIdleDisconnected => _idleDisconnected;
    public bool   IsConnected   => _net?.IsConnected ?? false;
    public string LocalPlayerName => _objects[0]?.Name.TextValue ?? string.Empty;

    /// <summary>
    /// True while connected but alone in the group (no live peers): the auto-disconnect countdown is
    /// running. The UI shows a notice in this state.
    /// </summary>
    public bool IsAwaitingIdleDisconnect => (_net?.IsConnected ?? false) && !HasLivePeers();

    /// <summary>
    /// Whole seconds remaining before a lone, peerless connection is auto-disconnected. 0 when not
    /// applicable (not connected, peers present, or the timer hasn't been initialised yet).
    /// </summary>
    public int IdleDisconnectSecondsRemaining
    {
        get
        {
            if (!IsAwaitingIdleDisconnect || _lastPeerSeen == DateTime.MinValue) return 0;
            var remaining = InactivityTimeout - (float)(DateTime.UtcNow - _lastPeerSeen).TotalSeconds;
            return remaining <= 0f ? 0 : (int)MathF.Ceiling(remaining);
        }
    }

    /// <summary>
    /// Presence list for the UI, keyed by a display label: the peer's real name when we can resolve
    /// their id against a nearby character, otherwise a short form of the opaque id (distant peers
    /// stay anonymous). Built fresh from the raw id-keyed map on each access.
    /// </summary>
    public IReadOnlyDictionary<string, DateTime>? Peers
    {
        get
        {
            if (_net == null) return null;
            var names  = BuildIdToNameMap();
            var result = new Dictionary<string, DateTime>();
            foreach (var kv in _net.Peers)
                result[names.GetValueOrDefault(kv.Key) ?? PeerIdentity.Short(kv.Key)] = kv.Value;
            return result;
        }
    }

    /// <summary>
    /// Real names of connected peers who allow remote morphing and were seen within ~90 s. Only
    /// peers we can resolve to a nearby character are returned — which is exactly the set we are
    /// able to morph anyway (a far, unresolvable peer cannot be targeted).
    /// </summary>
    public IReadOnlyList<string> ConsentingPeers
    {
        get
        {
            if (_net == null) return [];
            var cutoff = DateTime.UtcNow.AddSeconds(-PeerStaleSeconds);
            var names  = BuildIdToNameMap();
            var list   = new List<string>();
            foreach (var kv in _net.PeerConsent)
                if (kv.Value && _net.Peers.TryGetValue(kv.Key, out var seen) && seen >= cutoff
                    && names.TryGetValue(kv.Key, out var name))
                    list.Add(name);
            return list;
        }
    }

    /// <summary>True when <paramref name="name"/> is a peer who consents and is currently present.</summary>
    public bool IsConsentingPeer(string name)
    {
        if (_net == null) return false;
        var id = PeerIdentity.Of(name, _config.PairKey);
        return _net.PeerConsent.TryGetValue(id, out bool consent) && consent
            && _net.Peers.TryGetValue(id, out var seen)
            && seen >= DateTime.UtcNow.AddSeconds(-PeerStaleSeconds);
    }

    // ── Consent API (sender side) ─────────────────────────────────────────────

    /// <summary>Returns the current consent status for a named target.</summary>
    public ConsentStatus GetConsentStatus(string targetName)
    {
        if (_net == null) return ConsentStatus.Granted;
        var id = PeerIdentity.Of(targetName, _config.PairKey);
        return _consentStatus.GetValueOrDefault(id, ConsentStatus.Idle);
    }

    /// <summary>
    /// Sends a morph_request to the named target and registers an optional callback to run
    /// automatically when consent is granted.
    /// </summary>
    public void RequestConsent(string targetName, Action? onGranted = null)
    {
        if (_net == null) return;
        var id = PeerIdentity.Of(targetName, _config.PairKey);
        _consentStatus[id] = ConsentStatus.Pending;
        _onConsentGranted  = onGranted;
        _net.SendMorphRequest(targetName);
    }

    // ── Consent API (receiver side) ───────────────────────────────────────────

    /// <summary>True when at least one peer is waiting for a consent decision.</summary>
    public bool HasIncomingRequest => _incomingRequests.Count > 0;

    /// <summary>Display name of the peer whose request is currently shown, or null.</summary>
    public string? IncomingRequestSenderName
        => _incomingRequests.Count > 0 ? _incomingRequests[0].DisplayName : null;

    /// <summary>Accepts the current incoming request and starts the morph on our character.</summary>
    public void AcceptIncomingRequest()
    {
        if (_net == null || _incomingRequests.Count == 0) return;
        var (peerId, _) = _incomingRequests[0];
        _incomingRequests.RemoveAt(0);
        _net.SendMorphConsent(peerId, accepted: true);
    }

    /// <summary>Denies the current incoming request.</summary>
    public void DenyIncomingRequest()
    {
        if (_net == null || _incomingRequests.Count == 0) return;
        var (peerId, _) = _incomingRequests[0];
        _incomingRequests.RemoveAt(0);
        _net.SendMorphConsent(peerId, accepted: false);
    }

    /// <summary>Returns the base scaling JSON a peer broadcast, or null if not received yet.</summary>
    public bool TryGetPeerBaseProfile(string name, out string? json)
    {
        if (_net != null) return _net.TryGetPeerBaseProfile(name, out json);
        json = null;
        return false;
    }

    // ── Local consent ─────────────────────────────────────────────────────────

    /// <summary>Pushes the local "allow remote morph" flag to peers immediately.</summary>
    public void SetLocalConsent(bool value)
    {
        if (_net == null) return;
        _net.LocalConsent = value;
        _net.SendHello();
    }

    // ── Connection lifecycle ──────────────────────────────────────────────────

    /// <summary>Creates or recreates the connection from current config. Safe to call repeatedly.</summary>
    public void ApplyConfig()
    {
        _net?.Dispose();
        _net               = null;
        _idleDisconnected  = false;
        _lastPeerSeen = DateTime.MinValue;

        if (!_config.NetworkSyncEnabled) return;
        if (string.IsNullOrWhiteSpace(_config.RelayUrl)) return;
        if (string.IsNullOrWhiteSpace(_config.PairKey)) return;

        var playerName = _objects[0]?.Name.TextValue;
        if (playerName == null) return; // not logged in yet

        _net = new NetworkSync(playerName, _config.PairKey, _config.RelayUrl, _config.InstallId, _log)
        {
            LocalConsent = _config.AllowRemoteMorph
        };

        // Start the inactivity timer from the moment the connection is established.
        _lastPeerSeen = DateTime.UtcNow;

        // Capture our base scaling now so the connection's first hello already carries it.
        RefreshLocalBaseProfile(selfMorphing: false);
    }

    /// <summary>Tears down the active connection without touching config.</summary>
    public void Disconnect()
    {
        _net?.Dispose();
        _net               = null;
        _lastPeerSeen      = DateTime.MinValue;
        _idleDisconnected  = false;
        _lastSentJson      = null;
        _lastSentTarget    = null;
        _peerInterp.Clear();
        _consentStatus.Clear();
        _incomingRequests.Clear();
        _onConsentGranted = null;
    }

    public void Dispose()
    {
        _net?.SendStop();
        _net?.Dispose();
        _net = null;
        _peerInterp.Clear();
    }

    // ── Local base-scaling broadcast ──────────────────────────────────────────

    /// <summary>
    /// Reads the local player's permanent Customize+ scaling and shares it with peers so they can
    /// use it as the origin (and restore point) when morphing this character. Only re-broadcasts
    /// when the scaling actually changed. Pass <paramref name="selfMorphing"/> true to skip while a
    /// self-morph is ticking (those repeated C+ updates aren't real base-profile changes).
    /// </summary>
    public void RefreshLocalBaseProfile(bool selfMorphing)
    {
        if (_net == null) return;
        if (selfMorphing) return;

        // GetActiveProfileId + GetProfile always return the PERMANENT profile (C+ filters temp),
        // so this captures our true base scaling even while a temp morph is active.
        var (ec1, activeId) = _ipc.GetActiveProfileId(0);
        if (ec1 != 0 || !activeId.HasValue) return;

        var (ec2, json) = _ipc.GetProfile(activeId.Value);
        if (ec2 != 0 || json == null) return;

        if (json == _net.LocalBaseProfile) return; // unchanged — skip re-broadcast
        _net.LocalBaseProfile = json;
        _net.SendHello();
    }

    // ── Outbound frames (called by the morph engine) ──────────────────────────

    /// <summary>Primes the throttle so the next <see cref="TickBroadcast"/> sends immediately.</summary>
    public void ResetSendThrottle()
    {
        _sendTimer      = SendInterval;
        _lastSentJson   = null;
        _lastSentTarget = null;
    }

    /// <summary>
    /// Broadcasts a morph frame at ~30 Hz (and always on the final frame). <paramref name="target"/>
    /// is the peer being morphed, or null for a self-morph. No-op when not connected or no key set.
    /// </summary>
    public void TickBroadcast(string json, string? target, float progress, float seconds)
    {
        if (_net == null) return;
        if (string.IsNullOrWhiteSpace(_config.PairKey)) return;

        _sendTimer += seconds;
        if (_sendTimer < SendInterval && progress < 1f) return;
        _sendTimer = 0f;

        // Skip redundant frames: if the profile and target haven't changed and this isn't the
        // final frame, the relay would just forward the same bytes again for no visual benefit.
        if (progress < 1f && json == _lastSentJson && target == _lastSentTarget) return;

        _lastSentJson   = json;
        _lastSentTarget = target;
        _net.SendFrame(json, target);
    }

    /// <summary>Sends a single frame immediately (e.g. restoring origin on Reset).</summary>
    public void SendFrame(string json, string? target) => _net?.SendFrame(json, target);

    /// <summary>Tells peers to stop/clear the morph on the given target (or self when null).</summary>
    public void SendStop(string? target = null) => _net?.SendStop(target);

    // ── Inbound frames + interpolation (called once per framework tick) ──────────────────────

    /// <summary>
    /// Drains the inbound frame queue and advances per-peer bone interpolation.
    /// Call this exactly once per framework tick in place of the old ProcessIncomingFrames.
    /// </summary>
    public void Tick(float delta)
    {
        // While at least one other peer is present, keep resetting the timer.
        if (_net != null && HasLivePeers())
            _lastPeerSeen = DateTime.UtcNow;

        if (_net != null && _lastPeerSeen != DateTime.MinValue
            && (DateTime.UtcNow - _lastPeerSeen).TotalSeconds >= InactivityTimeout)
        {
            AutoDisconnect();
            return;
        }

        if (_net == null) return;

        while (_net.TryDequeue(out var frame))
        {
            // ── Frames directed at the local player ───────────────────────────
            if (frame.TargetId == _net.LocalId)
            {
                // Stops are always honored so a lingering temp profile never blocks C+ editing.
                if (string.IsNullOrEmpty(frame.ProfileJson))
                {
                    _ipc.DeleteTempProfile(0);
                    _peerInterp.Remove("_local_");
                }
                else if (_config.AllowRemoteMorph)
                {
                    UpdateInterp("_local_", frame.ProfileJson, 0, isLocal: true);
                }
                continue;
            }

            // ── Self-morph or targeted-at-someone-else ────────────────────────
            string targetId = !string.IsNullOrEmpty(frame.TargetId) ? frame.TargetId! : frame.SenderId;
            if (!_net.Peers.ContainsKey(targetId)) continue;

            if (string.IsNullOrEmpty(frame.ProfileJson))
            {
                var idx = FindCharacterIndexById(targetId);
                if (idx != null) _ipc.DeleteTempProfile(idx.Value);
                _peerInterp.Remove(targetId);
                // Peer disconnected — clear sender-side consent so we request again on reconnect.
                _consentStatus.Remove(targetId);
                if (_onConsentGranted != null && _consentStatus.Count == 0)
                    _onConsentGranted = null;
                // Clear any queued incoming request from this peer.
                _incomingRequests.RemoveAll(r => r.PeerId == targetId);
            }
            else
            {
                UpdateInterp(targetId, frame.ProfileJson, FindCharacterIndexById(targetId), isLocal: false);
            }
        }

        // ── Process consent messages ──────────────────────────────────────────
        while (_net.TryDequeueConsent(out var consent))
        {
            if (consent.IsRequest)
            {
                // Incoming morph request — add to queue for the popup window.
                var names = BuildIdToNameMap();
                string displayName = names.GetValueOrDefault(consent.SenderId)
                                  ?? PeerIdentity.Short(consent.SenderId);
                // Skip duplicate requests from the same peer.
                if (_incomingRequests.FindIndex(r => r.PeerId == consent.SenderId) < 0)
                    _incomingRequests.Add((consent.SenderId, displayName));
            }
            else if (consent.Accepted)
            {
                _consentStatus[consent.SenderId] = ConsentStatus.Granted;
                var names = BuildIdToNameMap();
                string who = names.GetValueOrDefault(consent.SenderId)
                          ?? PeerIdentity.Short(consent.SenderId);
                _chat.Print($"[BodyFlux] {who} accepted your morph request.");
                // Fire the auto-start callback if one was registered.
                var cb = _onConsentGranted;
                _onConsentGranted = null;
                cb?.Invoke();
            }
            else
            {
                _consentStatus[consent.SenderId] = ConsentStatus.Idle;
                _onConsentGranted = null;
                var names = BuildIdToNameMap();
                string who = names.GetValueOrDefault(consent.SenderId)
                          ?? PeerIdentity.Short(consent.SenderId);
                _chat.Print($"[BodyFlux] {who} declined your morph request.");
            }
        }

        // Apply interpolated morph states at game-tick rate (≈60 Hz) so receivers see smooth
        // animation even though the relay only delivers snapshots at 20 Hz.
        if (_peerInterp.Count == 0) return;
        foreach (var state in _peerInterp.Values)
        {
            if (state.From == null || state.To == null) continue;

            state.Elapsed += delta;
            float t    = Math.Min(state.Elapsed / InterpInterval, 1f);
            var   json = BuildProfileJson(LerpBones(state.From, state.To, t));

            if (state.IsLocal)
            {
                if (_config.AllowRemoteMorph) _ipc.SetTempProfile(0, json);
            }
            else if (state.Index.HasValue)
            {
                _ipc.SetTempProfile(state.Index.Value, json);
            }
        }
    }

    // Registers (or updates) an interpolation segment for a peer character.
    // From = where we currently are; To = newly received target; Elapsed resets to 0.
    private void UpdateInterp(string key, string profileJson, ushort? index, bool isLocal)
    {
        var newBones = ParseBones(profileJson);
        if (newBones == null) return;

        if (!_peerInterp.TryGetValue(key, out var state))
            _peerInterp[key] = state = new PeerInterpState();

        state.Index   = index;
        state.IsLocal = isLocal;

        if (state.From == null)
        {
            // First frame for this peer — snap to it immediately (no From to blend from).
            state.From    = newBones;
            state.To      = newBones;
            state.Elapsed = InterpInterval; // mark segment as complete so tick shows To
        }
        else
        {
            // New frame arrived: freeze From at the current interpolated position so the
            // transition continues smoothly from wherever we are to the new target.
            float prevT = Math.Min(state.Elapsed / InterpInterval, 1f);
            state.From    = LerpBones(state.From, state.To!, prevT);
            state.To      = newBones;
            state.Elapsed = 0f;
        }
    }

    // ── Bone interpolation helpers ─────────────────────────────────────────────

    private static JObject? ParseBones(string profileJson)
    {
        try   { return JObject.Parse(profileJson)["Bones"] as JObject; }
        catch { return null; }
    }

    private static JObject LerpBones(JObject from, JObject to, float t)
    {
        if (t <= 0f) return from;
        if (t >= 1f) return to;

        var result = new JObject();
        foreach (var bone in to.Properties())
        {
            result[bone.Name] = from[bone.Name] is JObject fb && bone.Value is JObject tb
                ? LerpBoneData(fb, tb, t)
                : bone.Value;
        }
        return result;
    }

    private static JObject LerpBoneData(JObject from, JObject to, float t)
    {
        var result = new JObject();
        foreach (var field in to.Properties())
        {
            var fv = from[field.Name];
            result[field.Name] = fv != null ? LerpVec(fv, field.Value, t) : field.Value.DeepClone();
        }
        return result;
    }

    private static JToken LerpVec(JToken from, JToken to, float t)
    {
        if (from is JArray fa && to is JArray ta && fa.Count == ta.Count)
        {
            var r = new JArray();
            for (int i = 0; i < ta.Count; i++)
                r.Add(fa[i].Value<float>() + (ta[i].Value<float>() - fa[i].Value<float>()) * t);
            return r;
        }
        if (from is JObject fo && to is JObject to2)
        {
            var r = new JObject();
            foreach (var p in to2.Properties())
            {
                float fv = fo[p.Name]?.Value<float>() ?? p.Value.Value<float>();
                r[p.Name] = fv + (p.Value.Value<float>() - fv) * t;
            }
            return r;
        }
        return to.DeepClone();
    }

    private static string BuildProfileJson(JObject bones)
        => new JObject { ["ID"] = "00000000-0000-0000-0000-000000000000", ["Name"] = "BodyFlux", ["Bones"] = bones }
               .ToString(Formatting.None);

    /// <summary>
    /// True when at least one other player in the group has been seen within the staleness window.
    /// Used to keep the connection alive while multiple users share a Pair Key — only a lone, idle
    /// player is ever auto-disconnected.
    /// </summary>
    private bool HasLivePeers()
    {
        if (_net == null) return false;
        var cutoff = DateTime.UtcNow.AddSeconds(-PeerStaleSeconds);
        var selfId = _net.LocalId;
        foreach (var kv in _net.Peers)
            if (kv.Value >= cutoff && kv.Key != selfId)
                return true;
        return false;
    }

    private void AutoDisconnect()
    {
        _log.Information("[BodyFlux/Sync] Auto-disconnected: alone in the group and idle for 1 minute.");
        _idleDisconnected  = true;
        _net?.Dispose();
        _net               = null;
        _lastPeerSeen      = DateTime.MinValue;
        _peerInterp.Clear();
        _consentStatus.Clear();
        _incomingRequests.Clear();
        _onConsentGranted = null;
    }

    /// <summary>
    /// Returns the ObjectTable index of the character whose name hashes to <paramref name="peerId"/>,
    /// or null if no visible character matches (peer out of range). Resolves peer ids to local slots
    /// without the real name ever having crossed the network.
    /// </summary>
    private ushort? FindCharacterIndexById(string peerId)
    {
        var key = _config.PairKey;
        for (ushort i = 1; i < _objects.Length; i++)
        {
            var obj = _objects[i];
            if (obj != null && PeerIdentity.Of(obj.Name.TextValue, key) == peerId)
                return i;
        }
        return null;
    }

    /// <summary>
    /// Builds a map of peer id → real name for every named character currently visible in the
    /// ObjectTable. Used to resolve incoming ids back to names for display and target selection;
    /// ids with no visible match simply stay unresolved (the peer is out of range).
    /// </summary>
    private Dictionary<string, string> BuildIdToNameMap()
    {
        var key = _config.PairKey;
        var map = new Dictionary<string, string>();
        for (ushort i = 0; i < _objects.Length; i++)
        {
            var name = _objects[i]?.Name.TextValue;
            if (!string.IsNullOrEmpty(name))
                map[PeerIdentity.Of(name, key)] = name;
        }
        return map;
    }
}
