using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using BodyFlux.Input;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// Renders the "Keybinds" tab: a master enable toggle, one row per morph operation, and a
/// collapsible section of per-preset hotkeys. Each row has a capture button and a clear button.
/// Bindings are context-sensitive at runtime (player vs. all Brio actors in GPose); this view only
/// edits <see cref="Configuration.Keybinds"/> / <see cref="Configuration.PresetKeybinds"/>.
/// </summary>
public sealed class KeybindsTabView
{
    private readonly Plugin plugin;

    // Exactly one capture target may be active at a time: an action, or a preset slot (>= 0).
    private MorphAction? _capturingAction;
    private int          _capturingPreset = -1;

    private static readonly (MorphAction Action, string Label, string? Tooltip)[] Rows =
    [
        (MorphAction.Apply,      "Apply Single", "Applies the morph set up in the Single tab (player), or the selected Brio actor in GPose."),
        (MorphAction.ApplyMulti, "Apply Multi",  "GPose only: applies the group set up in the Multi tab (same as its 'Apply All' button)."),
        (MorphAction.Pause,      "Pause",   null),
        (MorphAction.Resume,     "Resume",  null),
        (MorphAction.Reset,      "Reset",   null),
        (MorphAction.Reverse,    "Reverse", null),
    ];

    public KeybindsTabView(Plugin plugin) => this.plugin = plugin;

    public void Draw()
    {
        var   config = plugin.Configuration;
        float scale  = ImGuiHelpers.GlobalScale;
        float bw     = 90 * scale;

        ImGui.Spacing();

        // ── Master switch ──────────────────────────────────────────────────────
        bool enabled = config.KeybindsEnabled;
        if (ImGui.Checkbox("Enable keybinds", ref enabled))
        {
            config.KeybindsEnabled = enabled;
            config.Save();
        }
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "When off, no hotkey fires (bindings are kept). Outside GPose a key controls your own " +
            "morph; in GPose it controls all Brio actors. Keys are ignored while typing in a text field.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // While capturing, watch for either a pressed combo or an Escape cancel, then write it to
        // whichever target (action or preset) is active.
        HandleCapture();

        // Rows stay editable even when the master switch is off, so bindings can be prepared ahead
        // of time; the switch only gates whether they fire (enforced in KeybindHandler).
        foreach (var (action, label, tooltip) in Rows)
            DrawRow(config.GetKeybind(action), label, tooltip,
                    capturing: _capturingAction == action,
                    onSet:   () => BeginCapture(action: action),
                    onClear: () => { if (_capturingAction == action) CancelCapture(); },
                    bw, scale);

        DrawPresetSection(bw, scale);
    }

    // ── Preset hotkeys ─────────────────────────────────────────────────────────

    private void DrawPresetSection(float bw, float scale)
    {
        var config = plugin.Configuration;

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Preset hotkeys")) return;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Bind a key to a preset slot. It applies the matching player preset, or the matching " +
            "Brio preset while in GPose. Empty slots do nothing.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        using var scroll = ImRaii.Child("##KeybindPresetScroll", new Vector2(0, 0), false);
        if (!scroll) return;

        for (int slot = 0; slot < Configuration.PresetSlots; slot++)
        {
            var    preset   = slot < config.Presets.Count ? config.Presets[slot] : null;
            string name     = preset?.ProfileName ?? "empty";
            string label    = $"Preset {slot + 1}";
            int    captured = slot; // capture loop variable for the closures

            DrawRow(config.GetPresetKeybind(slot), label, $"Player slot: {name}",
                    capturing: _capturingPreset == slot,
                    onSet:   () => BeginCapture(preset: captured),
                    onClear: () => { if (_capturingPreset == captured) CancelCapture(); },
                    bw, scale);
        }
    }

    // ── Shared row renderer ──────────────────────────────────────────────────────

    private void DrawRow(Keybind bind, string label, string? tooltip, bool capturing,
                         Action onSet, Action onClear, float bw, float scale)
    {
        float labelW = 110 * scale;

        ImGui.TextUnformatted(label);
        if (tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        ImGui.SameLine(labelW);

        string display = capturing ? "Press a key…  (Esc to cancel)" : bind.Describe();
        var    color   = capturing
            ? new Vector4(0.9f, 0.8f, 0.2f, 1f)
            : bind.IsBound
                ? new Vector4(0.4f, 0.9f, 0.6f, 1f)
                : new Vector4(0.6f, 0.6f, 0.6f, 1f);

        float fieldW = ImGui.GetContentRegionAvail().X - (bw * 2 + 16 * scale);
        ImGui.TextColored(color, display);

        ImGui.SameLine(labelW + Math.Max(fieldW, 150 * scale));

        // Capturing this row → the Set button becomes Cancel.
        string setLabel = capturing ? "Cancel" : "Set";
        if (ImGui.Button($"{setLabel}##set{label}", new Vector2(bw, 0)))
        {
            if (capturing) CancelCapture();
            else           onSet();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!bind.IsBound))
        {
            if (ImGui.Button($"Clear##clr{label}", new Vector2(bw, 0)))
            {
                bind.Key  = VirtualKey.NO_KEY;
                bind.Ctrl = bind.Alt = bind.Shift = false;
                plugin.Configuration.Save();
                onClear();
            }
        }

        ImGui.Spacing();
    }

    // ── Capture state machine ────────────────────────────────────────────────────

    private void BeginCapture(MorphAction? action = null, int preset = -1)
    {
        _capturingAction = action;
        _capturingPreset = preset;
    }

    private void CancelCapture()
    {
        _capturingAction = null;
        _capturingPreset = -1;
    }

    private void HandleCapture()
    {
        bool active = _capturingAction.HasValue || _capturingPreset >= 0;
        if (!active) return;

        if (plugin.Keybinds.IsCancelPressed())
        {
            CancelCapture();
            return;
        }

        if (!plugin.Keybinds.TryCaptureCombo(out var combo))
            return;

        var target = _capturingAction is { } a
            ? plugin.Configuration.GetKeybind(a)
            : plugin.Configuration.GetPresetKeybind(_capturingPreset);

        target.Key   = combo.Key;
        target.Ctrl  = combo.Ctrl;
        target.Alt   = combo.Alt;
        target.Shift = combo.Shift;
        plugin.Configuration.Save();
        CancelCapture();
    }
}
