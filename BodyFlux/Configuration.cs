using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using BodyFlux.Input;
using BodyFlux.Morph;

namespace BodyFlux;

[Serializable]
public class Configuration : IPluginConfiguration
{
    // Config schema version (IPluginConfiguration), used for migrations — NOT the plugin version.
    public int Version { get; set; } = 0;

    /// <summary>Latest schema version. Bump when adding a migration step in <see cref="Migrate"/>.</summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// Stable anonymous identifier for this installation. Generated once on first load and persisted.
    /// Sent to the relay (as a query param) so the server can derive a consistent global id for
    /// quota tracking and moderation — the raw UUID never leaves the plugin process in readable form.
    /// </summary>
    public Guid InstallId { get; set; } = Guid.NewGuid();

    /// <summary>Number of quick-access preset slots per set (Player and Brio).</summary>
    public const int PresetSlots = 50;

    // Progress fraction covered per second  (0.5 = 2 s to complete)
    public float GrowthSpeed     { get; set; } = 0.5f;
    public float BrioGrowthSpeed { get; set; } = 0.5f;

    // How the morph plays back — once, one full loop, or continuously.
    public MorphMode   MorphMode      { get; set; } = MorphMode.Simple;

    // Whether a morph replaces the whole profile or only overlays the destination's bones.
    public MorphTargetMode MorphTargetMode { get; set; } = MorphTargetMode.FullProfile;

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

    // ── Keybinds ─────────────────────────────────────────────────────────────
    // Master switch: when false, no keybind fires regardless of its binding.
    public bool KeybindsEnabled { get; set; } = true;

    // Hotkey per morph operation. Each is context-sensitive when fired: outside GPose it controls
    // the player's morph, inside GPose it controls all Brio actors. All start unbound.
    public Dictionary<MorphAction, Keybind> Keybinds { get; set; } = new();

    // Hotkey per preset slot (0-based, < PresetSlots). Context-sensitive: applies the matching
    // player preset outside GPose, or the matching Brio preset inside GPose.
    public Dictionary<int, Keybind> PresetKeybinds { get; set; } = new();

    /// <summary>Returns the keybind for an action, creating an unbound entry if none exists.</summary>
    public Keybind GetKeybind(MorphAction action)
    {
        if (!Keybinds.TryGetValue(action, out var bind))
            Keybinds[action] = bind = new Keybind();
        return bind;
    }

    /// <summary>Returns the keybind for a preset slot, creating an unbound entry if none exists.</summary>
    public Keybind GetPresetKeybind(int slot)
    {
        if (!PresetKeybinds.TryGetValue(slot, out var bind))
            PresetKeybinds[slot] = bind = new Keybind();
        return bind;
    }

    // ── Network sync (peer sharing via BodyFluxRelay) ────────────────────────
    // When enabled, the plugin connects to the relay and broadcasts each morph
    // frame to all peers in the same group code.  Peers who also have BodyFlux
    // installed will receive the frames and apply SetTemporaryProfileOnCharacter
    // to your character slot locally, giving them smooth real-time animation.
    /// <summary>Current relay endpoint. Also the value retired URLs are migrated to (see <see cref="Migrate"/>).</summary>
    public const string DefaultRelayUrl = "wss://bodyflux-relay.fly.dev";

    // Relay URLs from earlier builds that no longer point anywhere. A config still on one of these
    // (i.e. the user never set a custom URL) is repointed to DefaultRelayUrl on load.
    private static readonly string[] DeadRelayUrls =
    [
        "wss://bodyfluxrelay.onrender.com",
        "wss://bodyfluxsync.onrender.com",
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

        // v2: stable install identity. Configs without one get a fresh UUID on first load.
        if (Version < 2 && InstallId == Guid.Empty)
        {
            InstallId = Guid.NewGuid();
            changed   = true;
        }

        // v3: relay moved from Render to Fly.io. Repoint any config still on a retired URL.
        if (Version < 3 && Array.IndexOf(DeadRelayUrls, RelayUrl?.TrimEnd('/')) >= 0)
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
