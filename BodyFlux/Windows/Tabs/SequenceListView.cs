using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using BodyFlux.Morph;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// Shared renderer for a list of morph sequences: the New button, one collapsible editor per
/// sequence (name, steps, add/remove/reorder, speed + easing), and Play/Delete. Stateless beyond
/// the <see cref="Plugin"/> reference — all per-tab state (playing index, add-step filter) is
/// passed in by reference so the same instance serves both the Player and Brio Sequences tabs.
/// </summary>
public sealed class SequenceListView
{
    private readonly Plugin plugin;

    public SequenceListView(Plugin plugin) => this.plugin = plugin;

    /// <summary>
    /// Draws the New button and one editor per sequence. Returns the index of a sequence whose Play
    /// was pressed this frame (-1 if none) so the caller can start it with the right engine entry point.
    /// </summary>
    public int Draw(List<MorphSequence> sequences,
                    ref int playingIndex, ref string addFilter, ref MorphTargetMode addMode,
                    float defaultSpeed, bool busy, bool seqActive,
                    bool playAllowed, string? playBlockedTooltip,
                    Action onReset,
                    float bw, float scale)
    {
        var config = plugin.Configuration;

        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button("New Sequence", new Vector2(bw + 40 * scale, 0)))
            {
                sequences.Add(new MorphSequence { Name = $"Sequence {sequences.Count + 1}" });
                config.Save();
            }
        }

        ImGui.Spacing();
        if (sequences.Count == 0)
            ImGui.TextDisabled("No sequences yet — press New Sequence.");

        int deleteIdx   = -1;
        int playRequest = -1;
        for (int s = 0; s < sequences.Count; s++)
        {
            var  seq       = sequences[s];
            bool isPlaying = playingIndex == s;

            ImGui.PushID(s);

            string header = isPlaying ? $"{seq.Name}  (playing)" : seq.Name;
            if (ImGui.CollapsingHeader($"{header}###seqHeader"))
            {
                ImGui.Indent(12 * scale);
                DrawEditor(seq, s, busy, seqActive, isPlaying, playAllowed, playBlockedTooltip,
                           defaultSpeed, onReset, ref addFilter, ref addMode, bw, scale, ref deleteIdx, ref playRequest);
                ImGui.Unindent(12 * scale);
            }

            ImGui.PopID();
        }

        if (deleteIdx >= 0)
        {
            sequences.RemoveAt(deleteIdx);
            if      (playingIndex == deleteIdx) playingIndex = -1;
            else if (playingIndex >  deleteIdx) playingIndex--;
            config.Save();
        }

        return playRequest;
    }

    /// <summary>Draws the editor body for a single sequence (name, steps, add, play/delete).</summary>
    private void DrawEditor(MorphSequence seq, int seqIndex,
                            bool busy, bool seqActive, bool isPlaying,
                            bool playAllowed, string? playBlockedTooltip,
                            float defaultSpeed, Action onReset, ref string addFilter, ref MorphTargetMode addMode,
                            float bw, float scale, ref int deleteIdx, ref int playRequest)
    {
        var  config   = plugin.Configuration;
        bool needSave = false;

        // ── Name ──────────────────────────────────────────────────────────────
        ImGui.TextUnformatted("Name:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        string name = seq.Name;
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.InputText("##SeqName", ref name, 64))
                seq.Name = name;
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
        }

        // ── Steps ─────────────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.TextUnformatted("Steps:");
        ImGui.Spacing();

        if (seq.Steps.Count == 0)
            ImGui.TextDisabled("   No steps yet — add one below.");

        int removeIdx = -1;
        int swapA = -1, swapB = -1;

        for (int i = 0; i < seq.Steps.Count; i++)
        {
            var step = seq.Steps[i];
            ImGui.PushID(i);

            using (ImRaii.Disabled(busy || i == 0))
                if (ImGui.ArrowButton("up", ImGuiDir.Up)) { swapA = i; swapB = i - 1; }
            ImGui.SameLine(0, 2 * scale);
            using (ImRaii.Disabled(busy || i == seq.Steps.Count - 1))
                if (ImGui.ArrowButton("down", ImGuiDir.Down)) { swapA = i; swapB = i + 1; }

            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}.");
            ImGui.SameLine();
            ImGui.TextUnformatted(step.ProfileName);

            // Speed + easing + remove on an indented second line so long names don't misalign.
            ImGui.Indent(28 * scale);

            ImGui.SetNextItemWidth(170 * scale);
            float sp = step.Speed;
            using (ImRaii.Disabled(busy))
            {
                // A labelled slider (not a drag field) so it's obvious this is the morph speed.
                if (ImGui.SliderFloat("##spd", ref sp, 0.01f, 1f, "Speed: %.2f/s"))
                    seq.Steps[i] = step with { Speed = Math.Clamp(sp, 0.01f, 1f) };
                if (ImGui.IsItemDeactivatedAfterEdit()) needSave = true;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(120 * scale);
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.BeginCombo("##eas", UiHelpers.EasingNames[(int)seq.Steps[i].Easing]))
                {
                    for (int e = 0; e < UiHelpers.EasingNames.Length; e++)
                    {
                        bool sel = (int)seq.Steps[i].Easing == e;
                        if (ImGui.Selectable(UiHelpers.EasingNames[e], sel))
                        {
                            seq.Steps[i] = seq.Steps[i] with { Easing = (EasingMode)e };
                            needSave = true;
                        }
                        if (sel) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
            }

            ImGui.SameLine();
            ImGui.TextDisabled(seq.Steps[i].TargetMode == MorphTargetMode.TemplateOverlay
                ? "(Template Overlay)" : "(Full Profile)");

            ImGui.SameLine();
            using (ImRaii.Disabled(busy))
                if (ImGui.Button("Remove")) removeIdx = i;

            // Time to complete this step at its current speed (mirrors the Single tab).
            float stepSecs = seq.Steps[i].Speed > 0f ? 1f / seq.Steps[i].Speed : float.PositiveInfinity;
            ImGui.TextDisabled($"Time to complete: {stepSecs:F1}s");

            ImGui.Unindent(28 * scale);
            ImGui.PopID();
            ImGui.Spacing();
        }

        // Apply deferred structural edits (outside the loop to avoid mutating mid-iteration).
        if (swapA >= 0)
        {
            (seq.Steps[swapA], seq.Steps[swapB]) = (seq.Steps[swapB], seq.Steps[swapA]);
            needSave = true;
        }
        if (removeIdx >= 0)
        {
            seq.Steps.RemoveAt(removeIdx);
            needSave = true;
        }

        // ── Add step (selecting a profile/template appends it immediately) ─────
        ImGui.TextUnformatted("Add step target:");
        ImGui.SameLine();
        int addModeInt = (int)addMode;
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.RadioButton("Full Profile##addmode", ref addModeInt, (int)MorphTargetMode.FullProfile))
                addMode = MorphTargetMode.FullProfile;
            ImGui.SameLine();
            if (ImGui.RadioButton("Template Overlay##addmode", ref addModeInt, (int)MorphTargetMode.TemplateOverlay))
                addMode = MorphTargetMode.TemplateOverlay;
        }

        bool addOverlay = addMode == MorphTargetMode.TemplateOverlay;
        ImGui.TextUnformatted("Add step:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        using (ImRaii.Disabled(busy))
        {
            string comboPreview = addOverlay ? "— add a template —" : "— add a profile —";
            if (ImGui.BeginCombo("##SeqAddProfile", comboPreview))
            {
                if (ImGui.IsWindowAppearing()) { addFilter = ""; ImGui.SetKeyboardFocusHere(); }
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##SeqAddFilter", ref addFilter, 128);
                ImGui.Separator();

                bool any = false;
                if (addOverlay)
                {
                    var templates = plugin.SavedTemplates;
                    for (int i = 0; i < templates.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(addFilter) &&
                            !templates[i].Name.Contains(addFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        any = true;
                        if (ImGui.Selectable(templates[i].Name))
                        {
                            var (id, tname, ownerId) = templates[i];
                            seq.Steps.Add(new MorphSequenceStep(id, tname, defaultSpeed, EasingMode.Linear,
                                MorphTargetMode.TemplateOverlay, ownerId));
                            needSave  = true;
                            addFilter = "";
                        }
                    }
                }
                else
                {
                    var profiles = plugin.SavedProfiles;
                    for (int i = 0; i < profiles.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(addFilter) &&
                            !profiles[i].Name.Contains(addFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        any = true;
                        if (ImGui.Selectable(profiles[i].Name))
                        {
                            var (id, pname) = profiles[i];
                            seq.Steps.Add(new MorphSequenceStep(id, pname, defaultSpeed, EasingMode.Linear,
                                MorphTargetMode.FullProfile));
                            needSave  = true;
                            addFilter = "";
                        }
                    }
                }
                if (!any) ImGui.TextDisabled("No matches.");
                ImGui.EndCombo();
            }
        }

        // ── Play / Delete ─────────────────────────────────────────────────────
        ImGui.Spacing();
        bool canPlay = !seqActive && !busy && seq.Steps.Count > 0 && playAllowed;
        using (ImRaii.Disabled(!canPlay))
        {
            if (ImGui.Button("Play", new Vector2(bw, 0)))
                playRequest = seqIndex; // caller starts it with the right engine entry point
        }
        if (!busy && !seqActive && !playAllowed && playBlockedTooltip != null
            && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(playBlockedTooltip);

        // Reset the morph back to the character's original profile. Always available — it's also the
        // way to stop a running sequence and clean up.
        ImGui.SameLine();
        if (ImGui.Button("Reset", new Vector2(bw, 0)))
            onReset();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reset to the original profile (also stops a running sequence).");

        ImGui.SameLine();
        using (ImRaii.Disabled(busy || isPlaying)) // can't delete the active sequence — Stop it first
        {
            if (ImGui.Button("Delete", new Vector2(bw, 0)))
                deleteIdx = seqIndex;
        }

        if (needSave) config.Save();
        ImGui.Spacing();
    }
}
