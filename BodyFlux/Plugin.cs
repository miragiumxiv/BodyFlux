using System;
using System.Collections.Generic;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using BodyFlux.Input;
using BodyFlux.Ipc;
using BodyFlux.Io;
using BodyFlux.Morph;
using BodyFlux.Services;
using BodyFlux.Windows;

namespace BodyFlux;

/// <summary>
/// Plugin entry point and thin Dalamud adapter. Owns service lifetimes, wires up Dalamud
/// events, and exposes a unified facade to the UI layer. All morph session logic lives in
/// <see cref="MorphEngine"/>; all network logic lives in <see cref="SyncManager"/>.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    // ── Dalamud services ──────────────────────────────────────────────────────
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager        CommandManager  { get; private set; } = null!;
    [PluginService] internal static IPluginLog             Log             { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework       { get; private set; } = null!;
    [PluginService] internal static IObjectTable           ObjectTable     { get; private set; } = null!;
    [PluginService] internal static IChatGui               ChatGui         { get; private set; } = null!;
    [PluginService] internal static IClientState           ClientState     { get; private set; } = null!;
    [PluginService] internal static IKeyState              KeyState        { get; private set; } = null!;

    // ── Core services ─────────────────────────────────────────────────────────
    internal CustomizePlusIpc Ipc   { get; private init; }
    internal BrioIpc          Brio  { get; private init; }
    internal SyncManager      Sync  { get; private init; }
    internal MorphEngine      Morph { get; private init; }
    internal KeybindHandler   Keybinds { get; private init; }
    private  ChatCommands     _chatCommands = null!;

    // ── Configuration & UI ────────────────────────────────────────────────────
    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("BodyFluxPlugin");
    private MainWindow MainWindow { get; init; }
    private MorphConsentWindow _consentWindow = null!;

    // ── Properties forwarded from Customize+ IPC ──────────────────────────────
    public IReadOnlyList<(Guid Id, string Name)> SavedProfiles            => Ipc.Profiles;
    public string                                ActiveProfileName         => Ipc.ActiveProfileName;
    public bool                                  IsCustomizePlusAvailable  => Ipc.IsAvailable;

    // ── Properties forwarded from Brio IPC ───────────────────────────────────
    public bool IsBrioAvailable => Brio.IsAvailable;

    // ── Properties forwarded from SyncManager ─────────────────────────────────
    public string                                 NetworkSyncStatus    => Sync.Status;
    public bool                                   IsNetworkActive      => Sync.IsActive;
    public int?                                   NetworkMaxPeers      => Sync.MaxPeers;
    public bool                                   IsNetworkIdleDisconnected => Sync.IsIdleDisconnected;
    public bool                                   IsNetworkQuotaExhausted  => Sync.IsQuotaExhausted;
    public bool                                   IsNetworkAwaitingIdleDisconnect => Sync.IsAwaitingIdleDisconnect;
    public int                                    NetworkIdleDisconnectSecondsRemaining => Sync.IdleDisconnectSecondsRemaining;
    public string                                 LocalPlayerName   => Sync.LocalPlayerName;
    public IReadOnlyDictionary<string, DateTime>? ConnectedPeers    => Sync.Peers;
    public IReadOnlyList<string>                  ConsentingPeers   => Sync.ConsentingPeers;

    // ── Properties forwarded from MorphEngine ─────────────────────────────────

    // Selection state
    public int     SelectedProfileIndex   { get => Morph.SelectedProfileIndex;   set => Morph.SelectedProfileIndex   = value; }
    public int     SelectedBrioActorIndex { get => Morph.SelectedBrioActorIndex; set => Morph.SelectedBrioActorIndex = value; }
    public int     SelectedBrioDestIndex  { get => Morph.SelectedBrioDestIndex;  set => Morph.SelectedBrioDestIndex  = value; }
    public string? TargetPlayerName       { get => Morph.TargetPlayerName;       set => Morph.TargetPlayerName       = value; }

    // Brio origin
    public bool   BrioOriginLoaded => Morph.BrioOriginLoaded;
    public string BrioOriginLabel  => Morph.BrioOriginLabel;

    // Player session state
    public bool       IsMorphing        => Morph.IsMorphing;
    public float      Progress          => Morph.Progress;
    public int        BoneAnimCount     => Morph.BoneAnimCount;
    public bool       IsPaused          => Morph.IsPaused;
    public MorphMode  CurrentMorphMode  => Morph.CurrentMorphMode;
    public EasingMode CurrentEasingMode => Morph.CurrentEasingMode;
    public ushort     MorphTargetIndex  => Morph.MorphTargetIndex;
    public bool       TargetBaseReady   => Morph.TargetBaseReady;

    // Sequence state
    public bool IsSequenceRunning => Morph.IsSequenceRunning;
    public int  SequenceStep      => Morph.SequenceStep;
    public int  SequenceStepCount => Morph.SequenceStepCount;

    // Brio actor state
    public bool  IsBrioActorActive(ushort index)   => Morph.IsBrioActorActive(index);
    public float GetBrioActorProgress(ushort index) => Morph.GetBrioActorProgress(index);

    public List<MorphEngine.BrioMorphState> GetActiveBrioMorphs() => Morph.GetActiveBrioMorphs();

    // ── Dalamud-only properties ───────────────────────────────────────────────

    /// <summary>True while the game's Group Pose (GPose) mode is active.</summary>
    public bool IsInGPose => ClientState.IsGPosing;

    /// <summary>
    /// Whether this player allows connected peers to morph their character.
    /// Persists to config and immediately notifies peers via a hello message.
    /// </summary>
    public bool AllowRemoteMorph
    {
        get => Configuration.AllowRemoteMorph;
        set
        {
            Configuration.AllowRemoteMorph = value;
            Configuration.Save();
            Sync.SetLocalConsent(value);
        }
    }

    // ── Consent request API ───────────────────────────────────────────────────

    public Services.ConsentStatus GetConsentForTarget(string targetName) => Sync.GetConsentStatus(targetName);
    public void RequestMorphConsent(string targetName) => Sync.RequestConsent(targetName, onGranted: () => StartGrowth());
    public bool   HasIncomingMorphRequest      => Sync.HasIncomingRequest;
    public string? IncomingMorphRequestSender  => Sync.IncomingRequestSenderName;
    public void AcceptIncomingMorphRequest()   => Sync.AcceptIncomingRequest();
    public void DenyIncomingMorphRequest()     => Sync.DenyIncomingRequest();

    // ── Construction / disposal ───────────────────────────────────────────────

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Apply pending schema migrations (e.g. retired relay URLs) before anything reads the config,
        // and persist once if anything changed.
        if (Configuration.Migrate())
            Configuration.Save();

        Ipc   = new CustomizePlusIpc(PluginInterface, Log);
        Brio  = new BrioIpc(PluginInterface, Log);
        Sync  = new SyncManager(Ipc, ObjectTable, Log, Configuration, ChatGui);
        Morph = new MorphEngine(Ipc, Brio, Sync, ObjectTable, ClientState, Configuration, ChatGui, Log);
        Keybinds = new KeybindHandler(this, KeyState);

        Ipc.ProfileUpdated += OnLocalProfileUpdated;

        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);
        _consentWindow = new MorphConsentWindow(this);
        WindowSystem.AddWindow(_consentWindow);

        _chatCommands = new ChatCommands(this, ChatGui, CommandManager);

        PluginInterface.UiBuilder.DisableGposeUiHide = true;
        PluginInterface.UiBuilder.Draw         += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi   += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        // NOTE: do NOT start Network Sync here. ApplyConfig() reads the ObjectTable (the local
        // player), which is not safe to touch during plugin construction at game boot (before login
        // / off the framework thread) and would throw, failing the whole plugin load when sync is
        // enabled with a Pair Key. The first framework tick after login starts it instead.
    }

    // True once the post-login Network Sync startup has run (one-shot, on the framework thread).
    private bool _syncStartupDone;
    // Tracks login state across frames so we can disconnect Network Sync the moment the player logs
    // out (returns to title/character select), not just when the whole plugin is disposed.
    private bool _wasLoggedIn;

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw         -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUi;

        _chatCommands.Dispose();

        Ipc.ProfileUpdated -= OnLocalProfileUpdated;
        Ipc.Dispose();

        // Persist "sync off" so the next launch doesn't auto-connect. Covers normal game close
        // where the player didn't explicitly log out first. Hard crashes may skip this.
        Configuration.NetworkSyncEnabled = false;
        Configuration.Save();

        Sync.Dispose();

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
    }

    // ── Framework tick ────────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        bool loggedIn = ClientState.IsLoggedIn;

        // Logged out (returned to title / character select) — drop the relay connection so we never
        // linger connected to a Pair Key while not in-game. Re-armed so the next login reconnects.
        // (A full game close is covered separately by Dispose.)
        if (_wasLoggedIn && !loggedIn)
        {
            Sync.Disconnect();
            _syncStartupDone = false;
            // Require explicit manual reconnect after every logout — never auto-reconnect on login.
            Configuration.NetworkSyncEnabled = false;
            Configuration.Save();
        }
        _wasLoggedIn = loggedIn;

        // Start Network Sync once, after login, on the framework thread — where reading the
        // ObjectTable (the local player) is safe. Doing this in the constructor crashed plugin load.
        if (!_syncStartupDone && loggedIn)
        {
            _syncStartupDone = true;
            try   { Sync.ApplyConfig(); }
            catch (Exception ex) { Log.Error(ex, "[BodyFlux] Network Sync startup failed."); }
        }

        Sync.Tick((float)framework.UpdateDelta.TotalSeconds);
        Morph.Tick((float)framework.UpdateDelta.TotalSeconds);

        // Drive the consent popup's visibility from the live request state. WindowSystem skips a
        // closed window's PreDraw/Draw entirely, so the window cannot open itself — we toggle it
        // here each frame instead.
        _consentWindow.IsOpen = HasIncomingMorphRequest;

        // Poll hotkeys only while logged in (no point reading keys at title/character select).
        if (loggedIn)
            Keybinds.Tick();
    }

    // ── Network sync facade ───────────────────────────────────────────────────

    /// <summary>(Re)creates the network connection from current config.</summary>
    public void ApplyNetworkSyncConfig() => Sync.ApplyConfig();

    /// <summary>Disconnects and clears any pending targeted morph.</summary>
    public void DisconnectNetworkSync()
    {
        Sync.Disconnect();
        TargetPlayerName = null;
    }

    // ── Morph engine facade ───────────────────────────────────────────────────

    /// <inheritdoc cref="CustomizePlusIpc.RefreshProfiles"/>
    public void RefreshProfiles() => Ipc.RefreshProfiles();

    /// <summary>Returns Brio's last-used MCDF import folder (to pre-fill the file picker), or null.</summary>
    public string? GetBrioLastMcdfFolder()
        => BrioConfigReader.GetLastMcdfFolder(PluginInterface.ConfigFile.DirectoryName);

    public List<(ushort Index, string Name)> GetGPoseActors()        => Morph.GetGPoseActors();

    public void StartGrowth(float? speedOverride = null, MorphMode? modeOverride = null, EasingMode? easingOverride = null)
        => Morph.StartGrowth(speedOverride, modeOverride, easingOverride);

    public void PauseGrowth()   => Morph.PauseGrowth();
    public void ResumeGrowth()  => Morph.ResumeGrowth();
    public void ReverseGrowth() => Morph.ReverseGrowth();
    public void ResetGrowth()   => Morph.ResetGrowth();

    public void StartBrioMorph(float? speedOverride = null, MorphMode? modeOverride = null, EasingMode? easingOverride = null)
        => Morph.StartBrioMorph(speedOverride, modeOverride, easingOverride);

    public bool StartBrioMorphFor(ushort actorIndex, Guid destProfileId, string? mcdfOriginJson,
                                  float? speedOverride, MorphMode mode, EasingMode easing)
        => Morph.StartBrioMorphFor(actorIndex, destProfileId, mcdfOriginJson, speedOverride, mode, easing);

    public void PauseBrioActor(ushort index)   => Morph.PauseBrioActor(index);
    public void ResumeBrioActor(ushort index)  => Morph.ResumeBrioActor(index);
    public void ReverseBrioActor(ushort index) => Morph.ReverseBrioActor(index);
    public void ResetBrioActor(ushort index)   => Morph.ResetBrioActor(index);
    public void PauseAllBrio()                 => Morph.PauseAllBrio();
    public void ResumeAllBrio()                => Morph.ResumeAllBrio();
    public void ReverseAllBrio()               => Morph.ReverseAllBrio();
    public void ResetAllBrio()                 => Morph.ResetAllBrio();

    public bool StartSequence(MorphSequence sequence, float? speedOverride = null) => Morph.StartSequence(sequence, speedOverride);
    public bool StartBrioSequence(MorphSequence sequence)  => Morph.StartBrioSequence(sequence);

    public void ApplyMorphPreset(MorphPreset preset, float? speedOverride = null)
        => Morph.ApplyMorphPreset(preset, speedOverride);

    public void ApplyBrioMorphPreset(MorphPreset preset, float? speedOverride = null)
        => Morph.ApplyBrioMorphPreset(preset, speedOverride);

    public void SavePreset(int slot)       => Morph.SavePreset(slot);
    public void ClearPreset(int slot)      => Morph.ClearPreset(slot);
    public void SaveBrioPreset(int slot)   => Morph.SaveBrioPreset(slot);
    public void ClearBrioPreset(int slot)  => Morph.ClearBrioPreset(slot);

    public bool   LoadBrioOriginMcdf(string path)              => Morph.LoadBrioOriginMcdf(path);
    public void   ClearBrioOrigin()                            => Morph.ClearBrioOrigin();
    public (bool Ok, string? Json, string Label) ReadMcdfProfile(string path)
        => Morph.ReadMcdfProfile(path);

    // ── Commands / UI wiring ──────────────────────────────────────────────────

    public void ToggleMainUi() => MainWindow.Toggle();

    // ── Internal event handlers ───────────────────────────────────────────────

    // Fired by Customize+ when any character's profile changes; only our own (index 0) matters,
    // and we skip self-morph ticks so we re-share our true base scaling, not the in-progress morph.
    private void OnLocalProfileUpdated(ushort gameObjectIndex)
    {
        if (gameObjectIndex != 0) return;
        Sync.RefreshLocalBaseProfile(
            selfMorphing: Morph.IsMorphing && Morph.MorphTargetIndex == 0);
    }
}
