using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using BodyFlux.Morph;

namespace BodyFlux;

[Serializable]
public class Configuration : IPluginConfiguration
{
    // Config schema version (IPluginConfiguration), used for migrations — NOT the plugin version.
    public int Version { get; set; } = 0;

    /// <summary>Latest schema version. Bump when adding a migration step in <see cref="Migrate"/>.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Number of quick-access preset slots per set (Player and Brio).</summary>
    public const int PresetSlots = 20;

    // Progress fraction covered per second  (0.5 = 2 s to complete)
    public float GrowthSpeed     { get; set; } = 0.5f;
    public float BrioGrowthSpeed { get; set; } = 0.5f;

    // How the morph plays back — once, one full loop, or continuously.
    public MorphMode   MorphMode      { get; set; } = MorphMode.Simple;

    // Easing curve applied to the interpolation progress each frame.
    public EasingMode EasingMode     { get; set; } = EasingMode.Linear;
    public EasingMode BrioEasingMode { get; set; } = EasingMode.Linear;

    // ── Presets & History ────────────────────────────────────────────────────
    public List<MorphPreset?> Presets           { get; set; } = [];
    public List<MorphPreset?> BrioPresets       { get; set; } = [];
    public List<MorphPreset>  RecentMorphs      { get; set; } = [];
    public List<MorphPreset>  BrioRecentMorphs  { get; set; } = [];

    // ── Morph Sequences ──────────────────────────────────────────────────────
    // Named A→B→C chains, each step morphing from the previous step's destination.
    public List<MorphSequence> Sequences        { get; set; } = [];
    public List<MorphSequence> BrioSequences    { get; set; } = [];

    // When true, connected peers are allowed to apply morph profiles to this character.
    public bool AllowRemoteMorph { get; set; } = false;

    // ── Network sync (peer sharing via BodyFluxRelay) ────────────────────────
    // When enabled, the plugin connects to the relay and broadcasts each morph
    // frame to all peers in the same group code.  Peers who also have BodyFlux
    // installed will receive the frames and apply SetTemporaryProfileOnCharacter
    // to your character slot locally, giving them smooth real-time animation.
    /// <summary>Current relay endpoint. Also the value retired URLs are migrated to (see <see cref="Migrate"/>).</summary>
    public const string DefaultRelayUrl = "wss://bodyfluxsync.onrender.com";

    // Relay URLs from earlier builds that no longer point anywhere. A config still on one of these
    // (i.e. the user never set a custom URL) is repointed to DefaultRelayUrl on load.
    private static readonly string[] DeadRelayUrls =
    [
        "wss://bodyfluxrelay.onrender.com",
    ];

    public bool   NetworkSyncEnabled { get; set; } = false;
    public string RelayUrl           { get; set; } = DefaultRelayUrl;
    public string PairKey            { get; set; } = "";

    /// <summary>
    /// Applies one-time, version-gated migrations to a freshly loaded config and returns true if
    /// anything changed (so the caller can persist once). Safe to run on every startup — each step
    /// is gated on <see cref="Version"/>, and the relay-URL rewrite only touches retired defaults,
    /// never a URL the user set themselves.
    /// </summary>
    public bool Migrate()
    {
        var changed = false;

        // v1: the relay moved hosts. Repoint configs still on a retired default URL.
        if (Version < 1 && Array.IndexOf(DeadRelayUrls, RelayUrl?.TrimEnd('/')) >= 0)
        {
            RelayUrl = DefaultRelayUrl;
            changed  = true;
        }

        if (Version != CurrentVersion)
        {
            Version = CurrentVersion;
            changed = true;
        }

        return changed;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
