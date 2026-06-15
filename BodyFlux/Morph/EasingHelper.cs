using System;

namespace BodyFlux.Morph;

public static class EasingHelper
{
    public static float Apply(float t, EasingMode mode) => mode switch
    {
        EasingMode.EaseIn    => t * t,
        EasingMode.EaseOut   => 1f - (1f - t) * (1f - t),
        EasingMode.EaseInOut => t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2f) / 2f,
        _                    => t,
    };
}
