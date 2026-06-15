using System;
using System.Globalization;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace BodyFlux;

/// <summary>
/// Owns the <c>/bodyflux</c> chat command: registers it on construction,
/// unregisters it on disposal, and handles all argument parsing and feedback.
/// </summary>
public sealed class ChatCommands : IDisposable
{
    private const string Command = "/bodyflux";

    private readonly Plugin          _plugin;
    private readonly IChatGui        _chat;
    private readonly ICommandManager _commands;

    public ChatCommands(Plugin plugin, IChatGui chat, ICommandManager commands)
    {
        _plugin   = plugin;
        _chat     = chat;
        _commands = commands;

        commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open the Body Flux window. Subcommands:\n" +
                "  /bodyflux preset <1-20> [speed]    — Apply a preset slot.\n" +
                "  /bodyflux sequence <name> [speed]  — Play a sequence by name.\n" +
                "  /bodyflux pause / resume / reverse / reset  — Control the active morph."
        });
    }

    public void Dispose() => _commands.RemoveHandler(Command);

    // ── Command entry point ───────────────────────────────────────────────────

    private void OnCommand(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            _plugin.ToggleMainUi();
            return;
        }

        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        switch (parts[0].ToLowerInvariant())
        {
            case "preset":   HandlePreset(parts);   break;
            case "sequence": HandleSequence(parts); break;
            case "pause":    HandlePause();         break;
            case "resume":   HandleResume();        break;
            case "reverse":  HandleReverse();       break;
            case "reset":    HandleReset();         break;
            default:         ShowUsage();           break;
        }
    }

    // ── preset ────────────────────────────────────────────────────────────────

    private void HandlePreset(string[] parts)
    {
        if (parts.Length < 2)
        {
            _chat.PrintError("[BodyFlux] Usage: /bodyflux preset <1-20> [speed]");
            return;
        }

        if (!int.TryParse(parts[1], out int presetNum) || presetNum < 1 || presetNum > Configuration.PresetSlots)
        {
            _chat.PrintError("[BodyFlux] Preset number must be between 1 and 20.");
            return;
        }

        float? speedOverride = null;
        if (parts.Length >= 3)
        {
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture,
                                out float parsed) || parsed < 0.01f || parsed > 1f)
            {
                _chat.PrintError("[BodyFlux] Speed must be a value between 0.01 and 1.0.");
                return;
            }
            speedOverride = parsed;
        }

        int slot   = presetNum - 1;
        var preset = slot < _plugin.Configuration.Presets.Count
            ? _plugin.Configuration.Presets[slot] : null;
        if (preset == null)
        {
            _chat.PrintError($"[BodyFlux] Preset {presetNum} is empty.");
            return;
        }

        _plugin.ApplyMorphPreset(preset, speedOverride);
        if (_plugin.IsMorphing)
        {
            string speedInfo = speedOverride.HasValue ? $" at speed {speedOverride.Value:F2}" : "";
            _chat.Print($"[BodyFlux] Applied preset {presetNum}: \"{preset.ProfileName}\"{speedInfo}.");
        }
    }

    // ── sequence ──────────────────────────────────────────────────────────────

    private void HandleSequence(string[] parts)
    {
        if (parts.Length < 2)
        {
            _chat.PrintError("[BodyFlux] Usage: /bodyflux sequence <name> [speed]");
            return;
        }

        // The last token is treated as a speed override if it parses as a float.
        // Everything before it (after "sequence") is the sequence name.
        float? speedOverride = null;
        int    nameEnd       = parts.Length;

        if (parts.Length >= 3 && float.TryParse(parts[^1], NumberStyles.Float,
                                                 CultureInfo.InvariantCulture, out float parsed))
        {
            if (parsed < 0.01f || parsed > 1f)
            {
                _chat.PrintError("[BodyFlux] Speed must be a value between 0.01 and 1.0.");
                return;
            }
            speedOverride = parsed;
            nameEnd       = parts.Length - 1;
        }

        var name = string.Join(' ', parts[1..nameEnd]);

        var seq = _plugin.Configuration.Sequences.Find(
            s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (seq == null)
        {
            _chat.PrintError($"[BodyFlux] Sequence \"{name}\" not found.");
            return;
        }

        bool started = _plugin.StartSequence(seq, speedOverride);
        if (started)
        {
            string speedInfo = speedOverride.HasValue ? $" at speed {speedOverride.Value:F2}" : "";
            _chat.Print($"[BodyFlux] Playing sequence \"{seq.Name}\"{speedInfo}.");
        }
        else
        {
            _chat.PrintError(
                $"[BodyFlux] Could not start sequence \"{seq.Name}\" " +
                "(already morphing, or one or more profiles are missing).");
        }
    }

    // ── morph controls ────────────────────────────────────────────────────────

    private void HandlePause()
    {
        if (!_plugin.IsMorphing)
        {
            _chat.PrintError("[BodyFlux] No active morph to pause.");
            return;
        }
        _plugin.PauseGrowth();
        _chat.Print("[BodyFlux] Morph paused.");
    }

    private void HandleResume()
    {
        if (!_plugin.IsPaused)
        {
            _chat.PrintError("[BodyFlux] No paused morph to resume.");
            return;
        }
        _plugin.ResumeGrowth();
        _chat.Print("[BodyFlux] Morph resumed.");
    }

    private void HandleReverse()
    {
        if (_plugin.BoneAnimCount == 0)
        {
            _chat.PrintError("[BodyFlux] No active morph to reverse.");
            return;
        }
        _plugin.ReverseGrowth();
        _chat.Print("[BodyFlux] Morph reversed.");
    }

    private void HandleReset()
    {
        if (_plugin.BoneAnimCount == 0)
        {
            _chat.PrintError("[BodyFlux] No active morph to reset.");
            return;
        }
        _plugin.ResetGrowth();
        _chat.Print("[BodyFlux] Morph reset.");
    }

    // ── fallback ──────────────────────────────────────────────────────────────

    private void ShowUsage() =>
        _chat.PrintError(
            "[BodyFlux] Unknown command. Available subcommands: " +
            "preset <1-20> [speed]  |  sequence <name> [speed]  |  pause  |  resume  |  reverse  |  reset");
}
