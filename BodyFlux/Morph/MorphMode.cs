namespace BodyFlux.Morph;

/// <summary>Controls the repetition behaviour of a morph transition.</summary>
public enum MorphMode
{
    /// <summary>Animates from the origin profile to the destination once, then stops.</summary>
    Simple,

    /// <summary>
    /// Animates forward to the destination, then reverses back to the origin.
    /// Stops once the origin is reached again (one full round-trip).
    /// </summary>
    LoopSingle,

    /// <summary>
    /// Continuously ping-pongs between the origin and destination profiles
    /// until <c>Reset</c> is pressed.
    /// </summary>
    LoopInfinite,
}
