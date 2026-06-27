using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace BodyFlux.Windows;

/// <summary>
/// A small always-on-top popup that appears when a sync peer requests permission to apply a morph
/// to the local player. Independent of the main window — visible even when the plugin UI is closed.
/// </summary>
public sealed class MorphConsentWindow : Window
{
    private readonly Plugin _plugin;

    public MorphConsentWindow(Plugin plugin)
        : base("Morph Request##ConsentPopup",
               ImGuiWindowFlags.NoResize     |
               ImGuiWindowFlags.NoScrollbar  |
               ImGuiWindowFlags.AlwaysAutoResize |
               ImGuiWindowFlags.NoCollapse)
    {
        _plugin = plugin;

        // Force a decision: no title-bar close button and no Esc-to-close. The window is opened
        // and closed solely by Plugin tracking HasIncomingMorphRequest each frame; the user must
        // Accept or Deny. (IsOpen is driven externally because WindowSystem skips PreDraw/Draw
        // entirely while a window is closed, so the window can't open itself.)
        ShowCloseButton    = false;
        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        var center = ImGui.GetIO().DisplaySize / 2f;
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));
    }

    public override void Draw()
    {
        var sender = _plugin.IncomingMorphRequestSender;
        if (sender == null) return;

        float scale = ImGuiHelpers.GlobalScale;

        ImGui.PushTextWrapPos(290 * scale);
        ImGui.TextUnformatted($"{sender} wants to apply a morph to your character.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();

        float bw = 80 * scale;
        if (ImGui.Button("Accept", new Vector2(bw, 0)))
            _plugin.AcceptIncomingMorphRequest();
        ImGui.SameLine();
        if (ImGui.Button("Deny", new Vector2(bw, 0)))
            _plugin.DenyIncomingMorphRequest();
    }
}
