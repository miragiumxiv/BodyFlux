using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
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

    public static string TargetModeShort(MorphTargetMode m) => m switch
    {
        MorphTargetMode.TemplateOverlay => "Overlay",
        _                               => "Full"
    };

    /// <summary>
    /// Filterable, searchable combo box over a flat list of names — shared by every destination
    /// picker (Player/Brio Single, Group rows, Sequence add-step), which each switch between
    /// listing Profiles or Templates depending on the current <see cref="MorphTargetMode"/>.
    /// </summary>
    public static void DrawFilterableCombo(string comboId, string filterId, IReadOnlyList<string> names,
        ref int selectedIndex, ref string filter, string placeholder)
    {
        string preview = selectedIndex >= 0 && selectedIndex < names.Count ? names[selectedIndex] : placeholder;
        if (!ImGui.BeginCombo(comboId, preview)) return;

        if (ImGui.IsWindowAppearing())
        {
            filter = "";
            ImGui.SetKeyboardFocusHere();
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText(filterId, ref filter, 128);
        ImGui.Separator();

        bool any = false;
        for (int i = 0; i < names.Count; i++)
        {
            if (!string.IsNullOrEmpty(filter) && !names[i].Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            any = true;
            bool sel = selectedIndex == i;
            if (ImGui.Selectable(names[i], sel))
            {
                selectedIndex = i;
                filter        = "";
            }
            if (sel) ImGui.SetItemDefaultFocus();
        }

        if (!any) ImGui.TextDisabled("No matches.");
        ImGui.EndCombo();
    }
}
