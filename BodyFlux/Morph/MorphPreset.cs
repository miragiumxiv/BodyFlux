using System;

namespace BodyFlux.Morph;

/// <summary>
/// Snapshot of a morph configuration — used for both preset slots and recent-morph history.
/// The trailing fields are Brio-only and optional: they let a Brio preset remember the
/// MCDF origin and the GPose actor it was saved against, so Apply can restore the exact
/// starting scaling and re-target the original actor. Player presets and history leave
/// them at their defaults.
///
/// When <see cref="TargetMode"/> is <see cref="MorphTargetMode.TemplateOverlay"/>, <see cref="ProfileId"/>
/// and <see cref="ProfileName"/> are reused to hold the selected Template's ID and name (not a
/// Profile), and <see cref="TemplateOwnerProfileId"/> carries the profile that owns it — Customize+
/// only exposes template bone data per-owning-profile, so that ID is needed to re-fetch it later.
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
    int        TargetActorIndex = -1,
    MorphTargetMode TargetMode  = MorphTargetMode.FullProfile,
    Guid?      TemplateOwnerProfileId = null);
