using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using BodyFlux.Morph;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// Renders the "Player" tab and its sub-tabs (Single, Presets, Sequences): destination/speed/mode/
/// easing options, target selector, progress + controls, recent-morph history, and the preset grid.
/// </summary>
public sealed class PlayerTabView
{
    private readonly Plugin            plugin;
    private readonly SequenceListView  seqList;
    private readonly Action            onResetProfileCache; // ask MainWindow to refresh profiles next OnOpen

    private string _profileFilter   = "";
    private int    _seqPlayingIndex = -1; // sequence active/resting (-1 = none)
    private string _seqAddFilter    = ""; // filter for the "Add step" picker

    public PlayerTabView(Plugin plugin, SequenceListView seqList, Action onResetProfileCache)
    {
        this.plugin              = plugin;
        this.seqList             = seqList;
        this.onResetProfileCache = onResetProfileCache;
    }

    public void Draw()
    {
        using var subTabs = ImRaii.TabBar("##PlayerSubTabs");
        if (!subTabs) return;

        using (var tab = ImRaii.TabItem("Single"))
            if (tab) DrawMorphSubTab();

        using (var tab = ImRaii.TabItem("Presets"))
            if (tab) DrawPresetsTab();

        using (var tab = ImRaii.TabItem("Sequences"))
            if (tab) DrawSequencesSubTab();
    }

    private void DrawMorphSubTab()
    {
        bool cpOk = plugin.IsCustomizePlusAvailable;

        // ── Requirements ──────────────────────────────────────────────────────
        ImGui.Spacing();
        var cpColor = cpOk
            ? new Vector4(0.3f, 0.9f, 0.4f, 1f)
            : new Vector4(0.9f, 0.3f, 0.3f, 1f);
        string cpLabel = cpOk
            ? "Customize+ enabled"
            : "Customize+ not detected — please install and enable it";
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(cpColor, cpLabel);
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Active profile ────────────────────────────────────────────────────
        ImGui.TextUnformatted("Active Profile:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.6f, 1f), plugin.ActiveProfileName);

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "This is the Customize+ Profile currently applied to your character. " +
            "It will be used as the starting point for the morph.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawOptionsSimple();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawOptionsAdvanced();

        // ── Footer: always visible ────────────────────────────────────────────
        ImGui.Separator();
        DrawProgress();
        ImGui.Spacing();
        DrawControls();

        DrawHistory();
    }

    private void DrawOptionsSimple()
    {
        float bw     = 90 * ImGuiHelpers.GlobalScale;
        bool  busy   = plugin.IsMorphing;
        var   config = plugin.Configuration;

        // ── Destination selector ──────────────────────────────────────────────
        ImGui.TextUnformatted("Destination Profile:");

        float comboWidth = ImGui.GetContentRegionAvail().X - (bw + 8 * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(comboWidth);

        string preview = plugin.SelectedProfileIndex >= 0 && plugin.SelectedProfileIndex < plugin.SavedProfiles.Count
            ? plugin.SavedProfiles[plugin.SelectedProfileIndex].Name
            : "— select a profile —";

        using (ImRaii.Disabled(busy))
        {
            if (ImGui.BeginCombo("##DestProfile", preview))
            {
                if (ImGui.IsWindowAppearing())
                {
                    _profileFilter = "";
                    ImGui.SetKeyboardFocusHere();
                }

                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##ProfileFilter", ref _profileFilter, 128);
                ImGui.Separator();

                bool anyVisible = false;
                for (int i = 0; i < plugin.SavedProfiles.Count; i++)
                {
                    if (!string.IsNullOrEmpty(_profileFilter) &&
                        !plugin.SavedProfiles[i].Name.Contains(_profileFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    anyVisible = true;
                    bool selected = plugin.SelectedProfileIndex == i;
                    if (ImGui.Selectable(plugin.SavedProfiles[i].Name, selected))
                    {
                        plugin.SelectedProfileIndex = i;
                        _profileFilter = "";
                    }
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }

                if (!anyVisible)
                    ImGui.TextDisabled("No matches.");

                ImGui.EndCombo();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button("Refresh", new Vector2(bw, 0)))
            {
                plugin.RefreshProfiles();
                plugin.SelectedProfileIndex = -1;
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Reload your Customize+ profile list\nfrom the latest saved profiles.");
        }

        ImGui.Spacing();

        // ── Growth speed ──────────────────────────────────────────────────────
        float speed = config.GrowthSpeed;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 180 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("Growth Speed (range/sec)", ref speed, 0.01f, 1f, "%.2f"))
        {
            config.GrowthSpeed = speed;
            config.Save();
        }

        ImGui.Spacing();
        float seconds = speed > 0f ? 1f / speed : float.PositiveInfinity;
        ImGui.TextDisabled($"Time to complete: {seconds:F1}s");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Mode selector ─────────────────────────────────────────────────────
        ImGui.TextUnformatted("Morph Mode:");
        ImGui.Spacing();

        using (ImRaii.Disabled(busy || plugin.IsPaused))
        {
            int  modeInt    = (int)config.MorphMode;
            bool modeChanged = false;

            if (ImGui.RadioButton("Simple",          ref modeInt, (int)MorphMode.Simple))       modeChanged = true;
            ImGui.SameLine();
            if (ImGui.RadioButton("Loop (Single)",   ref modeInt, (int)MorphMode.LoopSingle))   modeChanged = true;
            ImGui.SameLine();
            if (ImGui.RadioButton("Loop (Infinite)", ref modeInt, (int)MorphMode.LoopInfinite)) modeChanged = true;

            if (modeChanged)
            {
                config.MorphMode = (MorphMode)modeInt;
                config.Save();
            }
        }

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        string modeDesc = config.MorphMode switch
        {
            MorphMode.Simple       => "Morphs from your current profile to the destination once.",
            MorphMode.LoopSingle   => "Morphs to the destination, then reverses back to the starting profile once.",
            MorphMode.LoopInfinite => "Continuously ping-pongs between the starting and destination profiles until Reset is pressed.",
            _                      => ""
        };
        ImGui.TextDisabled(modeDesc);
        ImGui.PopTextWrapPos();
    }

    private void DrawOptionsAdvanced()
    {
        var  config = plugin.Configuration;
        bool busy   = plugin.IsMorphing;

        // ── Easing Curve ──────────────────────────────────────────────────────
        ImGui.TextUnformatted("Easing Curve:");
        ImGui.Spacing();

        using (ImRaii.Disabled(busy || plugin.IsPaused))
        {
            int  easingInt     = (int)config.EasingMode;
            bool easingChanged = false;

            if (ImGui.RadioButton("Linear",      ref easingInt, (int)EasingMode.Linear))    easingChanged = true;
            ImGui.SameLine();
            if (ImGui.RadioButton("Ease In",     ref easingInt, (int)EasingMode.EaseIn))    easingChanged = true;
            ImGui.SameLine();
            if (ImGui.RadioButton("Ease Out",    ref easingInt, (int)EasingMode.EaseOut))   easingChanged = true;
            ImGui.SameLine();
            if (ImGui.RadioButton("Ease In-Out", ref easingInt, (int)EasingMode.EaseInOut)) easingChanged = true;

            if (easingChanged)
            {
                config.EasingMode = (EasingMode)easingInt;
                config.Save();
            }
        }

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        string easingDesc = config.EasingMode switch
        {
            EasingMode.EaseIn    => "Starts slow and accelerates toward the destination.",
            EasingMode.EaseOut   => "Starts fast and decelerates as it approaches the destination.",
            EasingMode.EaseInOut => "Starts slow, accelerates through the middle, then decelerates at the end.",
            _                    => "Constant speed throughout the morph."
        };
        ImGui.TextDisabled(easingDesc);
        ImGui.PopTextWrapPos();
    }

    private void DrawProgress()
    {
        bool busy = plugin.IsMorphing;

        // ── Target selector ───────────────────────────────────────────────────
        var consentingPeers = plugin.ConsentingPeers;

        ImGui.TextUnformatted("Target:");
        ImGui.SameLine();

        string targetPreview = string.IsNullOrEmpty(plugin.TargetPlayerName)
            ? "Self"
            : plugin.TargetPlayerName;

        float targetComboWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(targetComboWidth);

        using (ImRaii.Disabled(busy || plugin.IsPaused))
        {
            if (ImGui.BeginCombo("##Target", targetPreview))
            {
                // Self option
                bool selfSelected = string.IsNullOrEmpty(plugin.TargetPlayerName);
                if (ImGui.Selectable("Self", selfSelected))
                    plugin.TargetPlayerName = null;
                if (selfSelected) ImGui.SetItemDefaultFocus();

                // Consenting peers
                if (consentingPeers.Count > 0)
                {
                    ImGui.Separator();
                    foreach (var peer in consentingPeers)
                    {
                        bool peerSelected = plugin.TargetPlayerName == peer;
                        if (ImGui.Selectable(peer, peerSelected))
                            plugin.TargetPlayerName = peer;
                        if (peerSelected) ImGui.SetItemDefaultFocus();
                    }
                }
                else if (plugin.IsNetworkActive)
                {
                    ImGui.Separator();
                    ImGui.TextDisabled("No consenting peers connected.");
                }

                ImGui.EndCombo();
            }
        }

        if (!string.IsNullOrEmpty(plugin.TargetPlayerName))
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(new Vector4(0.9f, 0.8f, 0.2f, 1f),
                $"Morphing will be applied to {plugin.TargetPlayerName}'s character.");

            // Tell the user whether we know the target's original scaling — without it,
            // Reset can't restore their appearance on our screen (it would snap to unscaled).
            if (plugin.TargetBaseReady)
                ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f),
                    "Their original scaling is known — Reset will restore it.");
            else
                ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.2f, 1f),
                    "Waiting for their scaling data… Reset may not restore it correctly yet.");
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Progress ──────────────────────────────────────────────────────────
        ImGui.TextUnformatted("Morph Progress:");
        ImGui.Spacing();

        if (plugin.BoneAnimCount > 0 || busy)
        {
            ImGui.TextUnformatted($"Bones morphing: {plugin.BoneAnimCount}");
            float prog = plugin.Progress;
            ImGui.ProgressBar(prog, new Vector2(-1, 18 * ImGuiHelpers.GlobalScale),
                              $"{prog * 100f:F1}%%");
        }
        else
        {
            ImGui.ProgressBar(0f, new Vector2(-1, 18 * ImGuiHelpers.GlobalScale), "0.0%");
        }
    }

    private void DrawControls()
    {
        float bw   = 90 * ImGuiHelpers.GlobalScale;
        bool  busy = plugin.IsMorphing;

        if (busy)
        {
            if (ImGui.Button("Pause", new Vector2(bw, 0)))
                plugin.PauseGrowth();
        }
        else if (plugin.IsPaused)
        {
            if (ImGui.Button("Resume", new Vector2(bw, 0)))
                plugin.ResumeGrowth();
        }
        else
        {
            bool canStart = plugin.SelectedProfileIndex >= 0
                         && plugin.SelectedProfileIndex < plugin.SavedProfiles.Count
                         && plugin.Progress < 1f;

            using (ImRaii.Disabled(!canStart))
            {
                if (ImGui.Button("Apply", new Vector2(bw, 0)))
                    plugin.StartGrowth();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset", new Vector2(bw, 0)))
        {
            plugin.ResetGrowth();
            onResetProfileCache();
        }

        bool done = !busy && !plugin.IsPaused && plugin.BoneAnimCount > 0;

        ImGui.SameLine();
        if ((busy || plugin.IsPaused || done) && ImGui.Button("Reverse", new Vector2(bw, 0)))
            plugin.ReverseGrowth();

        // ── Completion banner ─────────────────────────────────────────────────
        if (done)
        {
            string? banner = plugin.CurrentMorphMode switch
            {
                MorphMode.Simple     when plugin.Progress >= 1f => "Morph complete! Press Reset to restore the original Profile.",
                MorphMode.LoopSingle when plugin.Progress <= 0f => "Loop complete! You are back at the starting Profile. Press Reset to clean up.",
                _                                               => null
            };

            if (banner != null)
            {
                ImGui.Spacing();
                ImGui.PushTextWrapPos(0f);
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), banner);
                ImGui.PopTextWrapPos();
            }
        }
    }

    private void DrawHistory()
    {
        var  config = plugin.Configuration;
        bool busy   = plugin.IsMorphing;
        float bw    = 90 * ImGuiHelpers.GlobalScale;

        if (config.RecentMorphs.Count == 0) return;

        // Section divider between the morph controls and the history list.
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Recent Morphs")) return;
        ImGui.Spacing();

        // Applying an entry rewrites RecentMorphs (dedup + reinsert), so defer the call
        // until after the loop to avoid mutating the list we're enumerating.
        MorphPreset? toApply = null;
        foreach (var entry in config.RecentMorphs)
        {
            using (ImRaii.Disabled(busy || plugin.IsPaused))
            {
                if (ImGui.Button($"Apply##{entry.ProfileId}", new Vector2(bw, 0)))
                    toApply = entry;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(entry.ProfileName);
            ImGui.SameLine();
            ImGui.TextDisabled($"({entry.Speed:F2}/s  {UiHelpers.ModeShort(entry.Mode)}  {UiHelpers.EasingShort(entry.Easing)})");
        }

        if (toApply != null)
            plugin.ApplyMorphPreset(toApply);
    }

    private void DrawPresetsTab()
    {
        var   config = plugin.Configuration;
        bool  busy   = plugin.IsMorphing;
        float bw     = 90 * ImGuiHelpers.GlobalScale;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            $"Save up to {Configuration.PresetSlots} morph configurations for quick access. " +
            $"Use /bodyflux 1–{Configuration.PresetSlots} to apply from chat.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool canSave = plugin.SelectedProfileIndex >= 0
                    && plugin.SelectedProfileIndex < plugin.SavedProfiles.Count;

        for (int i = 0; i < Configuration.PresetSlots; i++)
        {
            var  preset    = i < config.Presets.Count ? config.Presets[i] : null;
            bool hasPreset = preset != null;

            using (ImRaii.Disabled(busy || !hasPreset))
            {
                if (ImGui.Button($"Apply##{i}", new Vector2(bw, 0)))
                    plugin.ApplyMorphPreset(preset!);
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(!canSave))
            {
                if (ImGui.Button($"Save##{i}", new Vector2(bw, 0)))
                    plugin.SavePreset(i);
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(!hasPreset))
            {
                if (ImGui.Button($"Clear##{i}", new Vector2(bw, 0)))
                    plugin.ClearPreset(i);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}.");
            ImGui.SameLine();

            if (hasPreset)
            {
                ImGui.TextUnformatted(preset!.ProfileName);
                ImGui.SameLine();
                ImGui.TextDisabled($"({preset.Speed:F2}/s  {UiHelpers.ModeShort(preset.Mode)}  {UiHelpers.EasingShort(preset.Easing)})");
            }
            else
            {
                ImGui.TextDisabled("— empty —");
            }
        }
    }

    private void DrawSequencesSubTab()
    {
        var   config = plugin.Configuration;
        float scale  = ImGuiHelpers.GlobalScale;
        float bw     = 90 * scale;
        bool  busy   = plugin.IsMorphing || plugin.IsPaused; // a morph is running/paused → lock editing

        // Keep _seqPlayingIndex pointing at the sequence that is playing, paused, or resting at
        // its final destination. Clear it once nothing is active so the footer disappears.
        bool restingMorph = plugin.BoneAnimCount > 0 && plugin.MorphTargetIndex == 0;
        if (!plugin.IsSequenceRunning && !plugin.IsPaused && !restingMorph)
            _seqPlayingIndex = -1;
        bool seqActive = _seqPlayingIndex >= 0;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Chain several morphs into an automatic A→B→C sequence. Each step morphs from the " +
            "previous step's result, with its own speed and easing. Steps always run once (Simple).");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        int playRequest = seqList.Draw(
            config.Sequences, ref _seqPlayingIndex, ref _seqAddFilter,
            config.GrowthSpeed, busy, seqActive, playAllowed: true, null,
            onReset: plugin.ResetGrowth, bw, scale);

        if (playRequest >= 0 && plugin.StartSequence(config.Sequences[playRequest]))
            _seqPlayingIndex = playRequest;

        if (!seqActive) return;
        DrawSequenceFooter(bw, scale, "Sequence complete — press Stop to restore your original profile.");
    }

    /// <summary>Playback footer for the player sequence: status line, progress bar, and Pause/Resume/Stop.</summary>
    private void DrawSequenceFooter(float bw, float scale, string completeText)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool running = plugin.IsSequenceRunning && plugin.IsMorphing;
        bool paused  = plugin.IsPaused;

        if (plugin.IsSequenceRunning)
            ImGui.TextUnformatted($"Playing step {plugin.SequenceStep} / {plugin.SequenceStepCount}");
        else
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), completeText);

        float prog = plugin.Progress;
        ImGui.ProgressBar(prog, new Vector2(-1, 18 * scale), $"{prog * 100f:F0}%%");

        ImGui.Spacing();

        if (running)
        {
            if (ImGui.Button("Pause##Seq", new Vector2(bw, 0)))
                plugin.PauseGrowth();
        }
        else if (paused)
        {
            if (ImGui.Button("Resume##Seq", new Vector2(bw, 0)))
                plugin.ResumeGrowth();
        }

        if (running || paused) ImGui.SameLine();
        if (ImGui.Button("Stop##Seq", new Vector2(bw, 0)))
            plugin.ResetGrowth();
    }
}
