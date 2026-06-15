using System.Collections.Generic;

namespace BodyFlux.Morph;

/// <summary>
/// A named, ordered chain of morph steps. Each step morphs from the previous step's
/// destination — the first step starts from the character's currently-active profile —
/// to its own destination, advancing automatically when the previous step completes.
/// Mutable: steps are added, removed, reordered and edited directly from the UI.
/// </summary>
public sealed class MorphSequence
{
    public string                  Name  { get; set; } = "New Sequence";
    public List<MorphSequenceStep> Steps { get; set; } = [];
}
