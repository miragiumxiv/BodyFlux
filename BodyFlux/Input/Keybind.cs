using System;
using Dalamud.Game.ClientState.Keys;

namespace BodyFlux.Input;

/// <summary>
/// The morph operations a keybind can trigger. Each one is context-sensitive at dispatch time:
/// outside GPose it drives the player's own morph; inside GPose it drives every Brio actor.
/// </summary>
/// <remarks>
/// Member names are persisted as dictionary keys in <see cref="Configuration.Keybinds"/>, so they
/// must not be renamed once shipped. <see cref="Apply"/> is the single-morph Apply (labelled
/// "Apply Single" in the UI); <see cref="ApplyMulti"/> applies the Brio "Multi" group and is only
/// meaningful in GPose.
/// </remarks>
public enum MorphAction
{
    Apply,
    ApplyMulti,
    Pause,
    Resume,
    Reset,
    Reverse,
}

/// <summary>
/// A single configurable hotkey: one main <see cref="VirtualKey"/> plus optional modifier flags.
/// Stored in <see cref="Configuration.Keybinds"/> and matched each frame by <see cref="KeybindHandler"/>.
/// A <see cref="VirtualKey.NO_KEY"/> main key means "unbound".
/// </summary>
[Serializable]
public sealed class Keybind
{
    public VirtualKey Key   { get; set; } = VirtualKey.NO_KEY;
    public bool       Ctrl  { get; set; }
    public bool       Alt   { get; set; }
    public bool       Shift { get; set; }

    public bool IsBound => Key != VirtualKey.NO_KEY;

    /// <summary>Human-readable form, e.g. "Ctrl+Shift+R" or "Unbound".</summary>
    public string Describe()
    {
        if (!IsBound) return "Unbound";

        var prefix = "";
        if (Ctrl)  prefix += "Ctrl+";
        if (Alt)   prefix += "Alt+";
        if (Shift) prefix += "Shift+";
        return prefix + Key.ToString();
    }
}
