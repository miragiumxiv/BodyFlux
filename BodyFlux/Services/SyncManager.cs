using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using BodyFlux.Ipc;
using BodyFlux.Network;

namespace BodyFlux.Services;

/// <summary>
/// Owns the <see cref="NetworkSync"/> connection and everything network-related: connecting,
/// broadcasting the local player's morph frames and base scaling, and applying frames received
/// from peers. The morph engine talks to peers only through this class — it never sees the raw
/// <see cref="NetworkSync"/>. Depends on <see cref="CustomizePlusIpc"/> (to apply incoming frames
/// and read our own base scaling) but not on the morph engine, so the coupling stays one-way.
/// </summary>
public sealed class SyncManager : IDisposable
{
    private const float SendInterval      = 0.033f; // ~30 Hz broadcast cap
    private const int   PeerStaleSeconds  = 90;     // peers unseen this long are treated as gone
    private const float InactivityTimeout = 300f;   // 5 minutes alone + idle → auto-disconnect

    private readonly CustomizePlusIpc _ipc;
    private readonly IObjectTable     _objects;
    private readonly IPluginLog       _log;
    private readonly Configuration    _config;

    private NetworkSync? _net;
    private float        _sendTimer;
    // Timestamp of the last time another live peer was present in the group (or the connect time).
    // While we are alone this stops updating, so (now - _lastPeerSeen) measures how long we have been
    // the only player in the Pair Key. Auto-disconnect fires after InactivityTimeout of being alone —
    // morph activity does NOT affect it, since the only point of the relay link is sharing with peers.
    private DateTime     _lastPeerSeen = DateTime.MinValue;
    private bool         _idleDisconnected;

    public SyncManager(CustomizePlusIpc ipc, IObjectTable objects, IPluginLog log, Configuration config)
    {
        _ipc     = ipc;
        _objects = objects;
        _log     = log;
        _config  = config;
    }

    // ── Status / read-only state (for the UI) ─────────────────────────────────

    public string Status           => _net?.Status ?? (_idleDisconnected ? "Disconnected (idle)" : "Disabled");
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

    public IReadOnlyDictionary<string, DateTime>? Peers => _net?.Peers;

    /// <summary>Names of connected peers who allow remote morphing and were seen within ~90 s.</summary>
    public IReadOnlyList<string> ConsentingPeers
    {
        get
        {
            if (_net == null) return [];
            var cutoff = DateTime.UtcNow.AddSeconds(-PeerStaleSeconds);
            var list   = new List<string>();
            foreach (var kv in _net.PeerConsent)
                if (kv.Value && _net.Peers.TryGetValue(kv.Key, out var seen) && seen >= cutoff)
                    list.Add(kv.Key);
            return list;
        }
    }

    /// <summary>True when <paramref name="name"/> is a peer who consents and is currently present.</summary>
    public bool IsConsentingPeer(string name)
        => _net != null
        && _net.PeerConsent.TryGetValue(name, out bool consent) && consent
        && _net.Peers.TryGetValue(name, out var seen)
        && seen >= DateTime.UtcNow.AddSeconds(-PeerStaleSeconds);

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

        _net = new NetworkSync(playerName, _config.PairKey, _config.RelayUrl, _log)
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
        _lastPeerSeen = DateTime.MinValue;
        _idleDisconnected  = false;
    }

    public void Dispose()
    {
        _net?.SendStop();
        _net?.Dispose();
        _net = null;
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
    public void ResetSendThrottle() => _sendTimer = SendInterval;

    /// <summary>
    /// Broadcasts a morph frame at ~30 Hz (and always on the final frame). <paramref name="target"/>
    /// is the peer being morphed, or null for a self-morph. No-op when not connected or no key set.
    /// </summary>
    public void TickBroadcast(string json, string? target, float progress, float seconds)
    {
        if (_net == null) return;
        if (string.IsNullOrWhiteSpace(_config.PairKey)) return;

        // Signal to NetworkSync that a morph is active (enables heartbeat). Note: morph activity does
        // NOT touch the auto-disconnect timer — that tracks peer presence only (see _lastPeerSeen).
        _net.MorphActive   = true;

        _sendTimer += seconds;
        if (_sendTimer < SendInterval && progress < 1f) return;
        _sendTimer = 0f;
        _net.SendFrame(json, target);
    }

    /// <summary>Sends a single frame immediately (e.g. restoring origin on Reset).</summary>
    public void SendFrame(string json, string? target) => _net?.SendFrame(json, target);

    /// <summary>Tells peers to stop/clear the morph on the given target (or self when null).</summary>
    public void SendStop(string? target = null)
    {
        _net?.SendStop(target);
        if (_net != null) _net.MorphActive = false;
    }

    // ── Inbound frames (called once per framework tick) ───────────────────────

    public void ProcessIncomingFrames()
    {
        // While at least one other peer is present, keep resetting the timer: a group with multiple
        // connected players is never auto-disconnected. Morph activity is irrelevant — the timer
        // tracks peer presence only.
        if (_net != null && HasLivePeers())
            _lastPeerSeen = DateTime.UtcNow;

        // Auto-disconnect once we have been alone (no other peers) for the full window, regardless
        // of morph activity — a lone connection only wastes relay bandwidth.
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
            if (frame.Target == LocalPlayerName)
            {
                // A stop (empty profile) only ever REMOVES the external temp profile, so always
                // honor it — even after we've disabled remote morphing — otherwise a lingering
                // temp profile could leave us unable to edit our own Customize+. Applying a morph
                // still requires consent.
                if (string.IsNullOrEmpty(frame.ProfileJson))
                    _ipc.DeleteTempProfile(0);
                else if (_config.AllowRemoteMorph)
                    _ipc.SetTempProfile(0, frame.ProfileJson);
                continue;
            }

            // ── Self-morph or targeted-at-someone-else: apply to their slot ───
            // For targeted frames, affect the named target; for self-morphs, affect the sender.
            string characterName = !string.IsNullOrEmpty(frame.Target) ? frame.Target : frame.SenderName;

            // Only apply visually to characters that are confirmed members of our sync group.
            // This prevents accidentally morphing a same-named stranger in the local area.
            if (!_net.Peers.ContainsKey(characterName)) continue;

            var index = FindCharacterIndex(characterName);
            if (index == null) continue;

            if (string.IsNullOrEmpty(frame.ProfileJson))
                _ipc.DeleteTempProfile(index.Value);
            else
                _ipc.SetTempProfile(index.Value, frame.ProfileJson);
        }
    }

    /// <summary>
    /// True when at least one other player in the group has been seen within the staleness window.
    /// Used to keep the connection alive while multiple users share a Pair Key — only a lone, idle
    /// player is ever auto-disconnected.
    /// </summary>
    private bool HasLivePeers()
    {
        if (_net == null) return false;
        var cutoff   = DateTime.UtcNow.AddSeconds(-PeerStaleSeconds);
        var selfName = LocalPlayerName;
        foreach (var kv in _net.Peers)
            if (kv.Value >= cutoff && kv.Key != selfName)
                return true;
        return false;
    }

    private void AutoDisconnect()
    {
        _log.Information("[BodyFlux/Sync] Auto-disconnected: alone in the group and idle for 5 minutes.");
        _idleDisconnected  = true;
        _net?.Dispose();
        _net               = null;
        _lastPeerSeen = DateTime.MinValue;
    }

    /// <summary>Returns the ObjectTable index of a character by name, or null if not visible.</summary>
    private ushort? FindCharacterIndex(string characterName)
    {
        for (ushort i = 1; i < _objects.Length; i++)
        {
            var obj = _objects[i];
            if (obj != null && obj.Name.TextValue == characterName)
                return i;
        }
        return null;
    }
}
