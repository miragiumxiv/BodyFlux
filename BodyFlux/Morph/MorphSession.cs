using System.Collections.Generic;
using System.Numerics;

namespace BodyFlux.Morph;

/// <summary>One resolved step of a running sequence (destination JSON + speed + easing).</summary>
public readonly record struct SeqStep(string DestJson, float Speed, EasingMode Easing);

/// <summary>
/// All the state of a single in-flight morph, bundled so the plugin can run many at once
/// (the local player plus any number of Brio GPose actors, each fully independent).
/// A <c>null</c> <see cref="SeqSteps"/> means a one-shot single morph; otherwise the session
/// is playing a sequence and advances step-by-step.
/// </summary>
public sealed class MorphSession
{
    /// <summary>The interpolation state machine for this session (self-contained per target).</summary>
    public MorphController Controller { get; } = new();

    /// <summary>ObjectTable index this session morphs (0 = local player).</summary>
    public ushort TargetIndex;

    /// <summary>Origin profile JSON, re-applied/broadcast on Reset to restore the pre-morph look.</summary>
    public string? OriginProfileJson;

    /// <summary>Peer name for a targeted-player morph; <c>null</c> for self and Brio morphs.</summary>
    public string? NetworkTargetName;

    /// <summary>Per-morph speed override (sequence step / preset / chat); <c>null</c> = use the tab default.</summary>
    public float? SpeedOverride;

    // ── Sequence playback (null SeqSteps = single morph) ──────────────────────
    public List<SeqStep>? SeqSteps;
    public int            SeqIndex;
    public bool           SeqRunning;

    /// <summary>
    /// True while a Reset is animating this session back to the origin (see
    /// <see cref="MorphController.BeginReset"/>). The engine finalises the reset once the sweep
    /// reaches the origin and the controller stops ticking.
    /// </summary>
    public bool Resetting;

    // ── GPose root externalisation ────────────────────────────────────────────
    /// <summary>
    /// True when the root bone is being driven through Brio's model transform instead of the
    /// Customize+ profile (set while morphing a posed actor in GPose, to avoid the C+/Brio fight
    /// over n_root). When set, <see cref="OriginalModelScale"/> holds the model scale to restore on reset.
    /// </summary>
    public bool     RootExternalised;
    public Vector3? OriginalModelScale;

    /// <summary>
    /// ObjectTable index of the Brio-managed actor whose model transform carries the root scale.
    /// For a Brio-tab morph this equals <see cref="TargetIndex"/>; for a self-morph in GPose it is
    /// the player's GPose counterpart (which differs from the C+ target index 0).
    /// </summary>
    public ushort? ModelTransformIndex;
}
