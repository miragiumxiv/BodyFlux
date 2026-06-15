using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;
using BodyFlux.Ipc;
using BodyFlux.Io;
using BodyFlux.Morph;

namespace BodyFlux.Services;

/// <summary>
/// Owns all morph session state and behaviour: starting, ticking, pausing, reversing, and
/// resetting player and Brio morphs; sequence playback; preset apply/save/clear; and MCDF
/// origin management. Depends on the IPC layers and SyncManager but not on Plugin, so it can
/// be created, ticked, and tested independently. Plugin calls <see cref="Tick"/> once per
/// frame after <c>SyncManager.ProcessIncomingFrames</c>.
/// </summary>
public sealed class MorphEngine
{
    private readonly CustomizePlusIpc                  _ipc;
    private readonly BrioIpc                           _brioIpc;
    private readonly SyncManager                       _sync;
    private readonly IObjectTable                      _objects;
    private readonly IClientState                      _clientState;
    private readonly Configuration                     _config;
    private readonly IChatGui                          _chat;
    private readonly IPluginLog                        _log;

    private readonly MorphSession                      _player = new();
    private readonly Dictionary<ushort, MorphSession>  _brio   = [];

    // True pre-morph model scale per externalised actor, kept across chained morphs (so starting a
    // new morph before resetting does not capture the previous morph's inflated scale as "original").
    // Cleared when the actor's model scale is restored on reset.
    private readonly Dictionary<ushort, Vector3>       _externalBaseScale = [];

    private string? _brioOriginJson;

    // ── Public state ──────────────────────────────────────────────────────────

    public string BrioOriginLabel  { get; private set; } = "";
    public bool   BrioOriginLoaded => _brioOriginJson != null;

    // UI-facing selection state
    public int     SelectedProfileIndex   { get; set; } = -1;
    public int     SelectedBrioActorIndex { get; set; } = -1;
    public int     SelectedBrioDestIndex  { get; set; } = -1;
    public string? TargetPlayerName       { get; set; }

    // Player session projections (drive the Player tab)
    public bool       IsMorphing        => _player.Controller.IsMorphing;
    public float      Progress          => _player.Controller.Progress;
    public int        BoneAnimCount     => _player.Controller.BoneCount;
    public bool       IsPaused          => _player.Controller.IsPaused;
    public MorphMode  CurrentMorphMode  => _player.Controller.Mode;
    public EasingMode CurrentEasingMode => _player.Controller.Easing;
    public ushort     MorphTargetIndex  => _player.Controller.TargetIndex;
    public bool       IsSequenceRunning => _player.SeqRunning;
    public int        SequenceStep      => _player.SeqRunning ? _player.SeqIndex + 1 : 0;
    public int        SequenceStepCount => _player.SeqSteps?.Count ?? 0;

    /// <summary>
    /// True when a targeted morph can correctly restore the target on Reset — i.e. the peer
    /// has already shared its base scaling. Always true for self-morphs (no target).
    /// </summary>
    public bool TargetBaseReady
    {
        get
        {
            if (string.IsNullOrEmpty(TargetPlayerName)) return true;
            return _sync.TryGetPeerBaseProfile(TargetPlayerName, out var j)
                && !string.IsNullOrEmpty(j);
        }
    }

    // ── Brio actor state ──────────────────────────────────────────────────────

    /// <summary>Snapshot of one active Brio actor morph for rendering the active-morphs list.</summary>
    public readonly record struct BrioMorphState(
        ushort Index, float Progress, int BoneCount,
        bool IsMorphing, bool IsPaused, bool IsFinished, bool SeqRunning, int SeqStep, int SeqStepCount);

    public bool  IsBrioActorActive(ushort index)   => _brio.ContainsKey(index);
    public float GetBrioActorProgress(ushort index) =>
        _brio.TryGetValue(index, out var s) ? s.Controller.Progress : 0f;

    public List<BrioMorphState> GetActiveBrioMorphs()
    {
        var list = new List<BrioMorphState>(_brio.Count);
        foreach (var (idx, s) in _brio)
            list.Add(new BrioMorphState(
                idx, s.Controller.Progress, s.Controller.BoneCount,
                s.Controller.IsMorphing, s.Controller.IsPaused, s.Controller.IsFinished,
                s.SeqRunning, s.SeqRunning ? s.SeqIndex + 1 : 0, s.SeqSteps?.Count ?? 0));
        list.Sort((a, b) => a.Index.CompareTo(b.Index));
        return list;
    }

    // ── Construction ──────────────────────────────────────────────────────────

    public MorphEngine(
        CustomizePlusIpc ipc, BrioIpc brioIpc, SyncManager sync,
        IObjectTable objects, IClientState clientState, Configuration config, IChatGui chat, IPluginLog log)
    {
        _ipc         = ipc;
        _brioIpc     = brioIpc;
        _sync        = sync;
        _objects     = objects;
        _clientState = clientState;
        _config      = config;
        _chat        = chat;
        _log         = log;
    }

    // ── Brio actor query ──────────────────────────────────────────────────────

    public List<(ushort Index, string Name)> GetGPoseActors() => _brioIpc.GetManagedActors();

    // ── Framework tick ────────────────────────────────────────────────────────

    /// <summary>
    /// Advances all active sessions by <paramref name="seconds"/>. Called once per framework
    /// update, after SyncManager has processed any incoming peer frames.
    /// </summary>
    /// <summary>Progress-per-second of the animated Reset sweep back to the origin (~0.5 s full range).</summary>
    private const float ResetSweepSpeed = 2.0f;

    public void Tick(float seconds)
    {
        if (_player.Controller.IsMorphing)
            TickSession(_player, seconds,
                _player.Resetting ? ResetSweepSpeed : _config.GrowthSpeed, broadcast: true);

        // The animated Reset sweep has reached the origin — finalise the teardown now (on the
        // framework thread). n_root has already been swept back through non-identity values, so the
        // DeleteTempProfile in ResetSession leaves it at the origin instead of stuck.
        if (_player.Resetting && !_player.Controller.IsMorphing)
        {
            _player.Resetting = false;
            ResetSession(_player, isPlayer: true);
        }

        if (_brio.Count > 0)
            foreach (var s in _brio.Values)
                if (s.Controller.IsMorphing)
                    TickSession(s, seconds, _config.BrioGrowthSpeed, broadcast: false);
    }

    private void TickSession(MorphSession s, float seconds, float baseSpeed, bool broadcast)
    {
        var json = s.Controller.Tick(seconds, s.SpeedOverride ?? baseSpeed);
        if (json == null) return;

        var (errorCode, _) = _ipc.SetTempProfile(s.Controller.TargetIndex, json);
        if (errorCode == 99)
        {
            _log.Error("[BodyFlux] Customize+ IPC lost during morph. Stopping.");
            s.Controller.Stop();
            return;
        }

        // GPose root externalisation: drive the root bone's interpolated scale through Brio's
        // model transform (instead of C+) so it does not fight Brio's pose-hold over n_root.
        if (s.RootExternalised && s.Controller.CurrentRootScale is { } rootScale)
            ApplyExternalRootScale(s, rootScale);

        if (broadcast && (s.Controller.TargetIndex == 0 || s.NetworkTargetName != null))
            _sync.TickBroadcast(json, s.NetworkTargetName, s.Controller.Progress, seconds);

        if (s.SeqRunning && !s.Controller.IsMorphing && s.Controller.Progress >= 1f)
            AdvanceSequence(s);
    }

    /// <summary>
    /// Drives the actor's whole-model scale through Brio to reproduce the morph's root-bone scale,
    /// leaving position/rotation untouched so the user's manual repositioning is preserved.
    /// </summary>
    private void ApplyExternalRootScale(MorphSession s, Vector3 rootScale)
    {
        if (s.ModelTransformIndex is not { } idx) return;
        var actor = _objects[idx];
        if (actor == null) return;

        var baseScale = s.OriginalModelScale ?? Vector3.One;
        var target    = baseScale * rootScale; // component-wise; base is normally (1,1,1)
        _brioIpc.SetModelTransform(actor, position: null, rotation: null, scale: target);
    }

    // ── Player morph controls ─────────────────────────────────────────────────

    /// <summary>
    /// Reads the active C+ profile (origin) and the selected profile (destination), then
    /// hands them to <see cref="MorphController"/> to start the transition. Optional overrides
    /// let callers (presets, history) supply per-morph values without touching saved config.
    /// </summary>
    public void StartGrowth(float? speedOverride = null, MorphMode? modeOverride = null, EasingMode? easingOverride = null)
    {
        if (_player.Controller.IsMorphing) return;
        _player.SpeedOverride = speedOverride;

        var profiles = _ipc.Profiles;
        if (SelectedProfileIndex < 0 || SelectedProfileIndex >= profiles.Count)
        {
            _log.Warning("[BodyFlux] No destination profile selected.");
            return;
        }

        // ── Resolve target index ──────────────────────────────────────────────
        ushort targetIndex = 0;
        if (!string.IsNullOrEmpty(TargetPlayerName))
        {
            if (!_sync.IsConnected)
            {
                _chat.PrintError("[BodyFlux] Network Sync must be active to morph other players.");
                return;
            }
            if (!_sync.IsConsentingPeer(TargetPlayerName))
            {
                _chat.PrintError(
                    $"[BodyFlux] '{TargetPlayerName}' has not enabled remote morphing " +
                    "or is not connected with the same Sync Key.");
                return;
            }
            var found = FindCharacterIndex(TargetPlayerName);
            if (found == null)
            {
                _chat.PrintError($"[BodyFlux] Cannot find '{TargetPlayerName}' nearby.");
                return;
            }
            targetIndex = found.Value;
        }

        // ── Fetch origin (the target's current scaling) ───────────────────────
        string? originJson = null;
        if (!string.IsNullOrEmpty(TargetPlayerName))
        {
            if (_sync.TryGetPeerBaseProfile(TargetPlayerName, out var peerBase)
                && !string.IsNullOrEmpty(peerBase))
                originJson = peerBase;
            else
                _log.Warning($"[BodyFlux] '{TargetPlayerName}' hasn't shared their base scaling yet; " +
                             "morphing from identity. Their appearance may not restore correctly on Reset.");
        }
        else
        {
            var (ec1, activeId) = _ipc.GetActiveProfileId(0);
            if (ec1 == 0 && activeId.HasValue)
            {
                var (ec2, json) = _ipc.GetProfile(activeId.Value);
                if (ec2 == 0) originJson = json;
            }

            // Fallback: a temp profile already applied (e.g. another plugin).
            if (originJson == null)
            {
                var (ecT, tempJson) = _ipc.GetTempProfile(0);
                if (ecT == 0 && tempJson != null) originJson = tempJson;
            }

            if (originJson == null)
            {
                _log.Error("[BodyFlux] No active C+ profile on the local player. " +
                           "Make sure a Customize+ profile is active.");
                return;
            }
        }

        // ── Fetch destination ─────────────────────────────────────────────────
        var (ec3, destJson) = _ipc.GetProfile(profiles[SelectedProfileIndex].Id);
        if (ec3 != 0 || destJson == null)
        {
            _log.Error($"[BodyFlux] GetByUniqueId (destination) failed (ec={ec3}).");
            return;
        }

        // ── Configure the player session ──────────────────────────────────────
        _player.OriginProfileJson = originJson;
        _player.NetworkTargetName = targetIndex == 0 ? null : TargetPlayerName;
        _player.TargetIndex       = targetIndex;
        _player.SeqSteps          = null;
        _player.SeqRunning        = false;

        var originJObj  = originJson != null ? JObject.Parse(originJson) : new JObject();
        var destJObj    = JObject.Parse(destJson);
        var originBones = originJObj["Bones"] as JObject ?? new JObject();
        var destBones   = destJObj  ["Bones"] as JObject ?? new JObject();

        var resolvedMode   = modeOverride   ?? _config.MorphMode;
        var resolvedEasing = easingOverride ?? _config.EasingMode;

        // In GPose, drive the root bone through Brio's model transform instead of C+ so it does
        // not fight Brio's pose-hold over n_root (the cause of the height flicker).
        TrySetupRootExternalisation(_player, targetIndex);
        _player.Controller.Start(targetIndex, originJObj, originBones, destBones,
                                 resolvedMode, resolvedEasing, _player.RootExternalised);

        _sync.ResetSendThrottle();

        // Record in history (dedup by profile, newest first, cap at 5)
        var (destId, destName) = profiles[SelectedProfileIndex];
        var historyEntry = new MorphPreset(destId, destName,
            speedOverride ?? _config.GrowthSpeed, resolvedMode, resolvedEasing);
        _config.RecentMorphs.RemoveAll(h => h.ProfileId == destId);
        _config.RecentMorphs.Insert(0, historyEntry);
        if (_config.RecentMorphs.Count > 5)
            _config.RecentMorphs.RemoveAt(5);
        _config.Save();

        string targetLabel = string.IsNullOrEmpty(TargetPlayerName) ? "self" : $"'{TargetPlayerName}'";
        _log.Information($"[BodyFlux] Morphing {_player.Controller.BoneCount} bones on {targetLabel}: " +
                         $"→ '{profiles[SelectedProfileIndex].Name}'");
    }

    public void PauseGrowth()   => _player.Controller.Pause();
    public void ResumeGrowth()  => _player.Controller.Resume();
    public void ReverseGrowth() => _player.Controller.Reverse();
    /// <summary>
    /// Resets the local player's morph. Outside GPose the root bone runs through Customize+, and a
    /// one-shot reset to identity is skipped by C+ (it ignores identity transforms), leaving n_root
    /// stuck. So we animate a quick reverse back to the origin — the path Reverse uses, which C+
    /// applies frame by frame — and finalise once it arrives. In GPose the root is handled by Brio's
    /// model transform, so the instant teardown already restores correctly.
    /// </summary>
    public void ResetGrowth()
    {
        if (!_player.RootExternalised
            && !_player.Resetting
            && _player.Controller.BoneCount > 0
            && _player.Controller.Progress > 0.001f)
        {
            _player.SpeedOverride = null; // sweep uses ResetSweepSpeed, not any per-morph override
            _player.Resetting     = true;
            _player.Controller.BeginReset();
        }
        else
        {
            ResetSession(_player, isPlayer: true);
        }
    }

    // ── Brio morph controls ───────────────────────────────────────────────────

    /// <summary>
    /// Starts a morph on the selected GPose actor. The origin is resolved in priority order:
    /// loaded MCDF → permanent C+ profile → identity (unscaled). Records the morph in Brio history.
    /// </summary>
    public void StartBrioMorph(float? speedOverride = null, MorphMode? modeOverride = null, EasingMode? easingOverride = null)
    {
        if (SelectedBrioActorIndex < 0)
        {
            _log.Warning("[BodyFlux] No GPose actor selected.");
            return;
        }

        var profiles = _ipc.Profiles;
        if (SelectedBrioDestIndex < 0 || SelectedBrioDestIndex >= profiles.Count)
        {
            _log.Warning("[BodyFlux] No destination profile selected.");
            return;
        }

        var (destId, destName) = profiles[SelectedBrioDestIndex];
        var resolvedMode   = modeOverride   ?? _config.MorphMode;
        var resolvedEasing = easingOverride ?? _config.BrioEasingMode;

        if (!StartBrioMorphFor((ushort)SelectedBrioActorIndex, destId, _brioOriginJson,
                               speedOverride, resolvedMode, resolvedEasing))
            return;

        // Record in Brio history (dedup by profile, newest first, cap at 5)
        var historyEntry = new MorphPreset(destId, destName,
            speedOverride ?? _config.BrioGrowthSpeed, resolvedMode, resolvedEasing);
        _config.BrioRecentMorphs.RemoveAll(h => h.ProfileId == destId);
        _config.BrioRecentMorphs.Insert(0, historyEntry);
        if (_config.BrioRecentMorphs.Count > 5)
            _config.BrioRecentMorphs.RemoveAt(5);
        _config.Save();
    }

    /// <summary>
    /// Starts (or replaces) a morph on one Brio actor with fully explicit config — the engine
    /// behind both the single Apply and the Group tab's "Apply All". Origin resolves
    /// MCDF → permanent C+ profile → identity. <paramref name="speedOverride"/> null means
    /// "follow the live BrioGrowthSpeed slider". Returns false if the destination couldn't be read.
    /// </summary>
    public bool StartBrioMorphFor(ushort actorIndex, Guid destProfileId, string? mcdfOriginJson,
                                  float? speedOverride, MorphMode mode, EasingMode easing)
    {
        var originJson = ResolveBrioOrigin(actorIndex, mcdfOriginJson, out string originSource);

        var (ec, destJson) = _ipc.GetProfile(destProfileId);
        if (ec != 0 || destJson == null)
        {
            _log.Error($"[BodyFlux/Brio] GetProfile (destination) failed (ec={ec}).");
            return false;
        }

        var originJObj  = JObject.Parse(originJson);
        var destJObj    = JObject.Parse(destJson);
        var originBones = originJObj["Bones"] as JObject ?? new JObject();
        var destBones   = destJObj  ["Bones"] as JObject ?? new JObject();

        var session = new MorphSession
        {
            TargetIndex       = actorIndex,
            OriginProfileJson = originJson,
            NetworkTargetName = null,
            SpeedOverride     = speedOverride,
        };
        TrySetupRootExternalisation(session, actorIndex);
        session.Controller.Start(actorIndex, originJObj, originBones, destBones,
                                 mode, easing, session.RootExternalised);
        _brio[actorIndex] = session;

        _log.Information($"[BodyFlux/Brio] actor={actorIndex} bones={session.Controller.BoneCount} origin={originSource}");
        return true;
    }

    public void PauseBrioActor(ushort index)   { if (_brio.TryGetValue(index, out var s)) s.Controller.Pause(); }
    public void ResumeBrioActor(ushort index)  { if (_brio.TryGetValue(index, out var s)) s.Controller.Resume(); }
    public void ReverseBrioActor(ushort index) { if (_brio.TryGetValue(index, out var s)) s.Controller.Reverse(); }

    public void PauseAllBrio()   { foreach (var s in _brio.Values) s.Controller.Pause(); }
    public void ResumeAllBrio()  { foreach (var s in _brio.Values) s.Controller.Resume(); }
    public void ReverseAllBrio() { foreach (var s in _brio.Values) s.Controller.Reverse(); }

    public void ResetBrioActor(ushort index)
    {
        if (!_brio.TryGetValue(index, out var s)) return;
        ResetSession(s, isPlayer: false);
        _brio.Remove(index);
    }

    public void ResetAllBrio()
    {
        if (_brio.Count == 0) return;
        var indices = new ushort[_brio.Count];
        _brio.Keys.CopyTo(indices, 0);
        foreach (var idx in indices)
            ResetBrioActor(idx);
    }

    // ── Sequence playback ─────────────────────────────────────────────────────

    /// <summary>
    /// Plays a named <see cref="MorphSequence"/> on the local player. All step destination
    /// profiles are resolved up front (so a missing profile aborts before anything starts),
    /// the first step morphs from the currently-active profile, and each subsequent step
    /// morphs from the previous step's destination. Returns false if it could not start.
    /// </summary>
    public bool StartSequence(MorphSequence sequence, float? speedOverride = null)
    {
        if (_player.Controller.IsMorphing) return false;
        if (sequence.Steps.Count == 0)
        {
            _log.Warning("[BodyFlux/Seq] Sequence has no steps.");
            return false;
        }

        var (ec1, activeId) = _ipc.GetActiveProfileId(0);
        if (ec1 != 0 || !activeId.HasValue)
        {
            _log.Error($"[BodyFlux/Seq] GetActiveProfileId failed (ec={ec1}). " +
                       "Make sure a Customize+ profile is active.");
            return false;
        }
        var (ec2, originJson) = _ipc.GetProfile(activeId.Value);
        if (ec2 != 0 || originJson == null)
        {
            _log.Error($"[BodyFlux/Seq] GetProfile (origin) failed (ec={ec2}).");
            return false;
        }

        var steps = new List<SeqStep>(sequence.Steps.Count);
        foreach (var step in sequence.Steps)
        {
            var (ec, destJson) = _ipc.GetProfile(step.ProfileId);
            if (ec != 0 || destJson == null)
            {
                _log.Error($"[BodyFlux/Seq] Profile '{step.ProfileName}' not found (ec={ec}); aborting sequence.");
                return false;
            }
            steps.Add(new SeqStep(destJson, speedOverride ?? step.Speed, step.Easing));
        }

        _player.SeqSteps          = steps;
        _player.SeqIndex          = 0;
        _player.SeqRunning        = true;
        _player.TargetIndex       = 0;
        _player.OriginProfileJson = originJson;
        _player.NetworkTargetName = null;

        TrySetupRootExternalisation(_player, 0);
        StartSequenceStep(_player, originJson, steps[0]);
        _log.Information($"[BodyFlux/Seq] Playing '{sequence.Name}' ({steps.Count} steps).");
        return true;
    }

    /// <summary>
    /// Plays a named <see cref="MorphSequence"/> on the currently-selected GPose actor. The
    /// first step's origin is resolved like <see cref="StartBrioMorph"/> (MCDF → permanent →
    /// identity); subsequent steps morph from the previous step's destination. Returns false
    /// if it could not start.
    /// </summary>
    public bool StartBrioSequence(MorphSequence sequence)
    {
        if (sequence.Steps.Count == 0)
        {
            _log.Warning("[BodyFlux/Seq] Brio sequence has no steps.");
            return false;
        }
        if (SelectedBrioActorIndex < 0)
        {
            _log.Warning("[BodyFlux/Seq] No GPose actor selected.");
            return false;
        }

        var actorIndex = (ushort)SelectedBrioActorIndex;

        string? originJson = null;
        string  originSource;
        if (_brioOriginJson != null)
        {
            originJson   = _brioOriginJson;
            originSource = "MCDF";
        }
        else
        {
            var (ec1, activeId) = _ipc.GetActiveProfileId(actorIndex);
            if (ec1 == 0 && activeId.HasValue)
            {
                var (ec2, j) = _ipc.GetProfile(activeId.Value);
                if (ec2 == 0 && j != null) originJson = j;
            }
            originSource = "permanent";
        }

        if (originJson == null)
        {
            _log.Warning($"[BodyFlux/Seq] Actor {actorIndex} has no readable origin; " +
                         "morphing from identity. Load an MCDF for correct scaling.");
            originJson   = """{"ID":"00000000-0000-0000-0000-000000000000","Name":"BodyFlux Origin","Bones":{}}""";
            originSource = "identity";
        }

        var steps = new List<SeqStep>(sequence.Steps.Count);
        foreach (var step in sequence.Steps)
        {
            var (ec, destJson) = _ipc.GetProfile(step.ProfileId);
            if (ec != 0 || destJson == null)
            {
                _log.Error($"[BodyFlux/Seq] Profile '{step.ProfileName}' not found (ec={ec}); aborting sequence.");
                return false;
            }
            steps.Add(new SeqStep(destJson, step.Speed, step.Easing));
        }

        var session = new MorphSession
        {
            TargetIndex       = actorIndex,
            SeqSteps          = steps,
            SeqIndex          = 0,
            SeqRunning        = true,
            OriginProfileJson = originJson,
            NetworkTargetName = null,
        };
        TrySetupRootExternalisation(session, actorIndex);
        _brio[actorIndex] = session;

        StartSequenceStep(session, originJson, steps[0]);

        _log.Information($"[BodyFlux/Seq] Playing Brio '{sequence.Name}' on actor {actorIndex} " +
                         $"({steps.Count} steps, origin={originSource}).");
        return true;
    }

    private void StartSequenceStep(MorphSession s, string originJson, SeqStep step)
    {
        var originJObj  = JObject.Parse(originJson);
        var destJObj    = JObject.Parse(step.DestJson);
        var originBones = originJObj["Bones"] as JObject ?? new JObject();
        var destBones   = destJObj  ["Bones"] as JObject ?? new JObject();

        s.SpeedOverride = step.Speed;
        s.Controller.Start(s.TargetIndex, originJObj, originBones, destBones,
                           MorphMode.Simple, step.Easing, s.RootExternalised);
        if (s == _player)
            _sync.ResetSendThrottle();
    }

    private void AdvanceSequence(MorphSession s)
    {
        int prev = s.SeqIndex;
        s.SeqIndex++;

        if (s.SeqSteps == null || s.SeqIndex >= s.SeqSteps.Count)
        {
            s.SeqRunning    = false;
            s.SeqSteps      = null;
            s.SeqIndex      = 0;
            s.SpeedOverride = null;
            _log.Information("[BodyFlux/Seq] Sequence complete.");
            return;
        }

        StartSequenceStep(s, s.SeqSteps[prev].DestJson, s.SeqSteps[s.SeqIndex]);
    }

    // ── Preset controls ───────────────────────────────────────────────────────

    /// <summary>Applies a saved preset or history entry, overriding current UI selections.</summary>
    public void ApplyMorphPreset(MorphPreset preset, float? speedOverride = null)
    {
        var profiles = _ipc.Profiles;
        int idx = -1;
        for (int i = 0; i < profiles.Count; i++)
            if (profiles[i].Id == preset.ProfileId) { idx = i; break; }
        if (idx < 0)
        {
            _log.Warning($"[BodyFlux] Preset profile '{preset.ProfileName}' not found in Customize+.");
            return;
        }
        SelectedProfileIndex = idx;
        StartGrowth(speedOverride ?? preset.Speed, preset.Mode, preset.Easing);
    }

    /// <summary>
    /// Applies a Brio preset or history entry. Besides the destination profile, a preset may
    /// also carry its own target actor and MCDF origin; when present, those are restored so the
    /// morph affects the originally-saved actor with the scaling it was saved against.
    /// </summary>
    public void ApplyBrioMorphPreset(MorphPreset preset, float? speedOverride = null)
    {
        var profiles = _ipc.Profiles;
        int idx = -1;
        for (int i = 0; i < profiles.Count; i++)
            if (profiles[i].Id == preset.ProfileId) { idx = i; break; }
        if (idx < 0)
        {
            _log.Warning($"[BodyFlux/Brio] Preset profile '{preset.ProfileName}' not found in Customize+.");
            return;
        }
        SelectedBrioDestIndex = idx;

        if (preset.TargetActorName != null)
        {
            int resolved = -1;
            foreach (var (aIdx, aName) in GetGPoseActors())
            {
                if (aName != preset.TargetActorName) continue;
                if (aIdx == preset.TargetActorIndex) { resolved = aIdx; break; }
                if (resolved < 0) resolved = aIdx;
            }
            if (resolved >= 0)
                SelectedBrioActorIndex = resolved;
            else
                _log.Warning($"[BodyFlux/Brio] Preset target actor '{preset.TargetActorName}' " +
                             "is not in the current scene; using the selected actor instead.");
        }

        if (preset.OriginMcdfJson != null)
        {
            _brioOriginJson = preset.OriginMcdfJson;
            BrioOriginLabel = preset.OriginMcdfLabel ?? "";
        }

        StartBrioMorph(speedOverride ?? preset.Speed, preset.Mode, preset.Easing);
    }

    /// <summary>Saves the current UI selection into preset slot <paramref name="slot"/> (0-based).</summary>
    public void SavePreset(int slot)
    {
        if (slot < 0 || slot >= Configuration.PresetSlots) return;
        var profiles = _ipc.Profiles;
        if (SelectedProfileIndex < 0 || SelectedProfileIndex >= profiles.Count) return;
        var (id, name) = profiles[SelectedProfileIndex];
        while (_config.Presets.Count <= slot)
            _config.Presets.Add(null);
        _config.Presets[slot] = new MorphPreset(id, name,
            _config.GrowthSpeed, _config.MorphMode, _config.EasingMode);
        _config.Save();
    }

    /// <summary>Clears preset slot <paramref name="slot"/> (0-based).</summary>
    public void ClearPreset(int slot)
    {
        if (slot >= 0 && slot < _config.Presets.Count)
        {
            _config.Presets[slot] = null;
            _config.Save();
        }
    }

    /// <summary>
    /// Saves the current Brio selection into preset slot <paramref name="slot"/> (0-based).
    /// Captures the destination profile plus the currently-selected actor and loaded MCDF
    /// origin, so the preset can re-target that actor with the same starting scaling.
    /// </summary>
    public void SaveBrioPreset(int slot)
    {
        if (slot < 0 || slot >= Configuration.PresetSlots) return;
        var profiles = _ipc.Profiles;
        if (SelectedBrioDestIndex < 0 || SelectedBrioDestIndex >= profiles.Count) return;
        var (id, name) = profiles[SelectedBrioDestIndex];

        string? actorName = null;
        int     actorIdx  = -1;
        if (SelectedBrioActorIndex >= 0)
        {
            var found = GetGPoseActors().Find(a => a.Index == (ushort)SelectedBrioActorIndex);
            if (found.Name != null) { actorName = found.Name; actorIdx = found.Index; }
        }

        while (_config.BrioPresets.Count <= slot)
            _config.BrioPresets.Add(null);
        _config.BrioPresets[slot] = new MorphPreset(id, name,
            _config.BrioGrowthSpeed, _config.MorphMode, _config.BrioEasingMode,
            _brioOriginJson, _brioOriginJson != null ? BrioOriginLabel : null,
            actorName, actorIdx);
        _config.Save();
    }

    /// <summary>Clears Brio preset slot <paramref name="slot"/> (0-based).</summary>
    public void ClearBrioPreset(int slot)
    {
        if (slot >= 0 && slot < _config.BrioPresets.Count)
        {
            _config.BrioPresets[slot] = null;
            _config.Save();
        }
    }

    // ── MCDF helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a Mare/Lightless MCDF file and extracts its Customize+ profile to use as the
    /// Brio clone's morph origin. Returns true on success; on failure <see cref="BrioOriginLabel"/>
    /// is set to the error reason.
    /// </summary>
    public bool LoadBrioOriginMcdf(string path)
    {
        var result = McdfReader.TryReadCustomizePlusProfile(path);
        if (!result.Ok)
        {
            _brioOriginJson = null;
            BrioOriginLabel = $"Error: {result.Error}";
            _log.Error($"[BodyFlux/Brio] Failed to read MCDF '{path}': {result.Error}");
            return false;
        }
        _brioOriginJson = result.CustomizePlusJson;
        BrioOriginLabel = System.IO.Path.GetFileName(path);
        _log.Information($"[BodyFlux/Brio] Loaded MCDF origin '{BrioOriginLabel}'.");
        return true;
    }

    /// <summary>Clears the currently loaded MCDF origin.</summary>
    public void ClearBrioOrigin()
    {
        _brioOriginJson = null;
        BrioOriginLabel = "";
    }

    /// <summary>
    /// Reads an MCDF's Customize+ profile without touching the shared MCDF staging — used by
    /// the Group tab, where each actor row keeps its own origin. Returns (ok, json, label-or-error).
    /// </summary>
    public (bool Ok, string? Json, string Label) ReadMcdfProfile(string path)
    {
        var result = McdfReader.TryReadCustomizePlusProfile(path);
        if (!result.Ok)
        {
            _log.Error($"[BodyFlux/Brio] Failed to read MCDF '{path}': {result.Error}");
            return (false, null, $"Error: {result.Error}");
        }
        return (true, result.CustomizePlusJson, System.IO.Path.GetFileName(path));
    }

    // ── GPose root externalisation ───────────────────────────────────────────

    /// <summary>
    /// Decides whether the morph's root bone should be driven through Brio's model transform
    /// instead of Customize+ (only in GPose, where Brio's pose-hold over n_root would otherwise
    /// fight C+ and cause a height flicker). When applicable, resolves the Brio actor that carries
    /// the rendered skeleton (for self-morphs that is the player's GPose counterpart, matched by
    /// name) and captures its current model scale so it can be restored on reset. Sets
    /// <see cref="MorphSession.RootExternalised"/>, <see cref="MorphSession.ModelTransformIndex"/>,
    /// and <see cref="MorphSession.OriginalModelScale"/> on <paramref name="s"/>.
    /// </summary>
    private void TrySetupRootExternalisation(MorphSession s, ushort cPlusTargetIndex)
    {
        s.RootExternalised   = false;
        s.OriginalModelScale = null;
        s.ModelTransformIndex = null;

        if (!_clientState.IsGPosing || !_brioIpc.IsAvailable) return;

        // Resolve which Brio-managed actor carries the rendered skeleton.
        ushort? brioIndex = null;
        var managed = _brioIpc.GetManagedActors();
        if (cPlusTargetIndex == 0)
        {
            // Self-morph: C+ targets index 0, but Brio poses the player's GPose counterpart.
            var name = _objects[0]?.Name.TextValue;
            if (!string.IsNullOrEmpty(name))
                foreach (var (idx, n) in managed)
                    if (n == name) { brioIndex = idx; break; }
        }
        else
        {
            foreach (var (idx, _) in managed)
                if (idx == cPlusTargetIndex) { brioIndex = idx; break; }
        }
        if (brioIndex == null) return;

        var actor = _objects[brioIndex.Value];
        if (actor == null) return;

        // Reuse the remembered pre-morph scale if a previous (un-reset) morph is still driving this
        // actor; only read the live value when we are the first to externalise it. This prevents a
        // chained morph from capturing the previous morph's inflated scale as the "original".
        if (_externalBaseScale.TryGetValue(brioIndex.Value, out var known))
        {
            s.OriginalModelScale = known;
        }
        else
        {
            var (_, _, scale) = _brioIpc.GetModelTransform(actor);
            s.OriginalModelScale = scale ?? Vector3.One;
            _externalBaseScale[brioIndex.Value] = s.OriginalModelScale.Value;
        }

        s.ModelTransformIndex = brioIndex.Value;
        s.RootExternalised    = true;
        _log.Information($"[BodyFlux/Brio] Root externalised via Brio model transform " +
                         $"(C+ target={cPlusTargetIndex}, Brio actor={brioIndex.Value}, base={s.OriginalModelScale}).");
    }

    /// <summary>Restores the actor's Brio model scale captured at morph start (if externalised).</summary>
    private void RestoreExternalRootScale(MorphSession s)
    {
        if (!s.RootExternalised || s.ModelTransformIndex is not { } idx) return;

        var actor = _objects[idx];
        if (actor != null)
            _brioIpc.SetModelTransform(actor, position: null, rotation: null,
                                       scale: s.OriginalModelScale ?? Vector3.One);

        // The true original has now been put back — forget it so the next morph re-reads fresh.
        _externalBaseScale.Remove(idx);
        _log.Information($"[BodyFlux/Brio] Root scale restored on actor {idx} " +
                         $"(scale={s.OriginalModelScale ?? Vector3.One}).");
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private string ResolveBrioOrigin(ushort actorIndex, string? mcdfJson, out string source)
    {
        if (mcdfJson != null)
        {
            source = "MCDF";
            return mcdfJson;
        }

        var (ec1, activeId) = _ipc.GetActiveProfileId(actorIndex);
        if (ec1 == 0 && activeId.HasValue)
        {
            var (ec2, j) = _ipc.GetProfile(activeId.Value);
            if (ec2 == 0 && j != null) { source = "permanent"; return j; }
        }

        _log.Warning($"[BodyFlux/Brio] Actor {actorIndex} has no readable origin; " +
                     "morphing from identity (unscaled). Load an MCDF for correct scaling.");
        source = "identity";
        return """{"ID":"00000000-0000-0000-0000-000000000000","Name":"BodyFlux Origin","Bones":{}}""";
    }

    private void ResetSession(MorphSession s, bool isPlayer)
    {
        var targetIndex = s.Controller.TargetIndex;

        if (isPlayer)
        {
            if (s.NetworkTargetName != null)
                _sync.SendStop(s.NetworkTargetName);
            else if (s.OriginProfileJson != null)
                _sync.SendFrame(s.OriginProfileJson, null);
            else
                _sync.SendStop(null);
        }

        // Restore the Brio model scale we drove during the morph (GPose root externalisation)
        // before tearing down the session.
        RestoreExternalRootScale(s);

        s.Controller.Stop();
        s.SpeedOverride = null;
        s.SeqRunning    = false;
        s.SeqSteps      = null;
        s.SeqIndex      = 0;
        s.Resetting           = false;
        s.RootExternalised    = false;
        s.ModelTransformIndex = null;
        s.OriginalModelScale  = null;

        if (targetIndex != 0 && s.OriginProfileJson != null)
            _ipc.SetTempProfile(targetIndex, s.OriginProfileJson);
        else
            _ipc.DeleteTempProfile(targetIndex);

        s.OriginProfileJson = null;
        s.NetworkTargetName = null;
    }

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
