namespace BodyFlux.Morph;

/// <summary>Controls how a morph's destination bones are combined with the origin profile.</summary>
public enum MorphTargetMode
{
    /// <summary>
    /// Morphs into the full destination profile. Bones absent from it animate back to
    /// their default (identity) transform.
    /// </summary>
    FullProfile,

    /// <summary>
    /// Blends only the bones defined in a single Customize+ Template onto the currently-applied
    /// profile; every other bone is left completely untouched.
    /// </summary>
    TemplateOverlay,
}
