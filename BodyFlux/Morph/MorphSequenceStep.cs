using System;

namespace BodyFlux.Morph;

/// <summary>
/// One step of a <see cref="MorphSequence"/>: a destination profile plus the speed and
/// easing used to morph into it. Steps always run in <see cref="MorphMode.Simple"/> —
/// loop modes don't compose cleanly with auto-chaining.
/// </summary>
public record MorphSequenceStep(
    Guid       ProfileId,
    string     ProfileName,
    float      Speed,
    EasingMode Easing);
