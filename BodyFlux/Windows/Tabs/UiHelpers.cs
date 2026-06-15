using BodyFlux.Morph;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// Small label/formatting helpers shared by the tab views (Player and Brio history,
/// presets, and easing combos). Pure presentation — no state.
/// </summary>
internal static class UiHelpers
{
    /// <summary>Display names indexed by <c>(int)</c><see cref="EasingMode"/> — used by easing combos.</summary>
    public static readonly string[] EasingNames = ["Linear", "Ease In", "Ease Out", "Ease In-Out"];

    public static string ModeShort(MorphMode m) => m switch
    {
        MorphMode.LoopSingle   => "Loop×1",
        MorphMode.LoopInfinite => "Loop∞",
        _                      => "Simple"
    };

    public static string EasingShort(EasingMode e) => e switch
    {
        EasingMode.EaseIn    => "Ease In",
        EasingMode.EaseOut   => "Ease Out",
        EasingMode.EaseInOut => "In-Out",
        _                    => "Linear"
    };
}
