using System;

namespace BodyFlux.Morph;

/// <summary>
/// One step of a <see cref="MorphSequence"/>: a destination profile plus the speed and
/// easing used to morph into it. Steps always run in <see cref="MorphMode.Simple"/> —
/// loop modes don't compose cleanly with auto-chaining.
///
/// When <see cref="TargetMode"/> is <see cref="MorphTargetMode.TemplateOverlay"/>, <see cref="ProfileId"/>
/// and <see cref="ProfileName"/> are reused to hold the selected Template's ID and name, and
/// <see cref="TemplateOwnerProfileId"/> carries the profile that owns it (see <see cref="MorphPreset"/>).
/// </summary>
public record MorphSequenceStep(
    Guid       ProfileId,
    string     ProfileName,
    float      Speed,
    EasingMode Easing,
    MorphTargetMode TargetMode = MorphTargetMode.FullProfile,
    Guid?      TemplateOwnerProfileId = null);
