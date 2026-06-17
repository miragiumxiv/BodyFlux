using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace BodyFlux.Input;

/// <summary>
/// Polls <see cref="IKeyState"/> once per framework tick and fires the configured morph operation
/// when its key combination transitions from up→down (edge-triggered, so it fires once per press).
/// Each operation is context-sensitive: in GPose it targets all Brio actors, otherwise the player.
///
/// Combos require an exact modifier match (Ctrl/Alt/Shift) so they never overlap, and are suppressed
/// while an ImGui text field has keyboard focus (e.g. the capture box or any plugin input).
/// </summary>
public sealed class KeybindHandler
{
    private readonly Plugin     _plugin;
    private readonly IKeyState  _keyState;

    // Per-action / per-preset "was the combo down last frame" — the basis for edge detection.
    private readonly Dictionary<MorphAction, bool> _wasDown       = new();
    private readonly Dictionary<int, bool>         _wasDownPreset = new();

    /// <summary>
    /// Invoked when the "Apply Multi" keybind fires in GPose. Wired by the UI layer to the Brio
    /// tab's group "Apply All", whose configuration lives in the view (not the config), so the
    /// handler can't build it itself. Null until wired, or outside GPose where it's a no-op.
    /// </summary>
    public Action? ApplyMultiHandler { get; set; }

    // Virtual keys that are modifiers (or otherwise unsuitable as a combo's main key); excluded
    // from capture and never treated as the main key.
    private static readonly HashSet<VirtualKey> ModifierKeys =
    [
        VirtualKey.CONTROL, VirtualKey.MENU, VirtualKey.SHIFT,
        VirtualKey.LCONTROL, VirtualKey.RCONTROL,
        VirtualKey.LMENU, VirtualKey.RMENU,
        VirtualKey.LSHIFT, VirtualKey.RSHIFT,
        VirtualKey.LWIN, VirtualKey.RWIN,
        VirtualKey.LBUTTON, VirtualKey.RBUTTON, VirtualKey.MBUTTON,
    ];

    public KeybindHandler(Plugin plugin, IKeyState keyState)
    {
        _plugin   = plugin;
        _keyState = keyState;
    }

    // ── Per-frame polling ─────────────────────────────────────────────────────

    /// <summary>Checks every configured keybind and dispatches any that were just pressed.</summary>
    public void Tick()
    {
        var config = _plugin.Configuration;

        // Master switch off: fire nothing, and forget prior state so re-enabling while a key is
        // still held doesn't immediately trigger a stale edge.
        if (!config.KeybindsEnabled)
        {
            if (_wasDown.Count       > 0) _wasDown.Clear();
            if (_wasDownPreset.Count > 0) _wasDownPreset.Clear();
            return;
        }

        // Never fire while typing into a text field (our capture box, chat-link inputs, etc.).
        bool textActive = ImGui.GetIO().WantTextInput;

        foreach (var (action, bind) in config.Keybinds)
        {
            bool down = !textActive && IsComboDown(bind);
            bool was  = _wasDown.TryGetValue(action, out var w) && w;
            _wasDown[action] = down;

            if (down && !was)
                Dispatch(action);
        }

        foreach (var (slot, bind) in config.PresetKeybinds)
        {
            bool down = !textActive && IsComboDown(bind);
            bool was  = _wasDownPreset.TryGetValue(slot, out var w) && w;
            _wasDownPreset[slot] = down;

            if (down && !was)
                ApplyPreset(slot);
        }
    }

    private bool IsComboDown(Keybind bind)
    {
        if (!bind.IsBound || !_keyState[bind.Key])
            return false;

        // Exact modifier match — a combo with no modifiers must not fire while one is held.
        bool ctrl  = _keyState[VirtualKey.CONTROL];
        bool alt   = _keyState[VirtualKey.MENU];
        bool shift = _keyState[VirtualKey.SHIFT];
        return ctrl == bind.Ctrl && alt == bind.Alt && shift == bind.Shift;
    }

    // Routes one action to the player session or, in GPose, to all Brio actors. Every target
    // method guards its own preconditions (no active morph, nothing selected, …) and no-ops safely.
    private void Dispatch(MorphAction action)
    {
        bool gpose = _plugin.IsInGPose;
        switch (action)
        {
            case MorphAction.Apply:      if (gpose) _plugin.StartBrioMorph(); else _plugin.StartGrowth();   break;
            case MorphAction.ApplyMulti: if (gpose) ApplyMultiHandler?.Invoke();                            break; // GPose-only
            case MorphAction.Pause:      if (gpose) _plugin.PauseAllBrio();   else _plugin.PauseGrowth();    break;
            case MorphAction.Resume:     if (gpose) _plugin.ResumeAllBrio();  else _plugin.ResumeGrowth();   break;
            case MorphAction.Reset:      if (gpose) _plugin.ResetAllBrio();   else _plugin.ResetGrowth();    break;
            case MorphAction.Reverse:    if (gpose) _plugin.ReverseAllBrio(); else _plugin.ReverseGrowth();  break;
        }
    }

    // Applies a preset slot, context-sensitive: the Brio preset in GPose, otherwise the player
    // preset. Empty/missing slots no-op; the underlying apply methods guard the rest.
    private void ApplyPreset(int slot)
    {
        var config = _plugin.Configuration;
        if (_plugin.IsInGPose)
        {
            var preset = slot < config.BrioPresets.Count ? config.BrioPresets[slot] : null;
            if (preset != null) _plugin.ApplyBrioMorphPreset(preset);
        }
        else
        {
            var preset = slot < config.Presets.Count ? config.Presets[slot] : null;
            if (preset != null) _plugin.ApplyMorphPreset(preset);
        }
    }

    // ── Capture (used by the Keybinds config UI) ───────────────────────────────

    /// <summary>
    /// If a non-modifier key is currently held, returns a <see cref="Keybind"/> capturing it plus the
    /// live modifier state. Returns false when only modifiers (or nothing) are down. The caller drives
    /// this each frame while in "press a key" mode and stops once it returns true.
    /// </summary>
    public bool TryCaptureCombo(out Keybind combo)
    {
        foreach (var vk in _keyState.GetValidVirtualKeys())
        {
            if (ModifierKeys.Contains(vk) || vk == VirtualKey.ESCAPE)
                continue;
            if (!_keyState[vk])
                continue;

            combo = new Keybind
            {
                Key   = vk,
                Ctrl  = _keyState[VirtualKey.CONTROL],
                Alt   = _keyState[VirtualKey.MENU],
                Shift = _keyState[VirtualKey.SHIFT],
            };
            return true;
        }

        combo = new Keybind();
        return false;
    }

    /// <summary>True while Escape is held — lets the capture UI cancel cleanly.</summary>
    public bool IsCancelPressed() => _keyState[VirtualKey.ESCAPE];
}
