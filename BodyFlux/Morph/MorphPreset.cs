using System;

namespace BodyFlux.Morph;

/// <summary>
/// Snapshot of a morph configuration — used for both preset slots and recent-morph history.
/// The trailing fields are Brio-only and optional: they let a Brio preset remember the
/// MCDF origin and the GPose actor it was saved against, so Apply can restore the exact
/// starting scaling and re-target the original actor. Player presets and history leave
/// them at their defaults.
/// </summary>
public record MorphPreset(
    Guid       ProfileId,
    string     ProfileName,
    float      Speed,
    MorphMode  Mode,
    EasingMode Easing,
    string?    OriginMcdfJson   = null,
    string?    OriginMcdfLabel  = null,
    string?    TargetActorName  = null,
    int        TargetActorIndex = -1);
