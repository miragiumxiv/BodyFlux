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
                    ref int playingIndex, ref string addFilter,
                    float defaultSpeed, bool busy, bool seqActive,
                    bool playAllowed, string? playBlockedTooltip,
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
                           defaultSpeed, ref addFilter, bw, scale, ref deleteIdx, ref playRequest);
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
                            float defaultSpeed, ref string addFilter,
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

            ImGui.SetNextItemWidth(110 * scale);
            float sp = step.Speed;
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.DragFloat("##spd", ref sp, 0.005f, 0.01f, 1f, "%.2f/s"))
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
            using (ImRaii.Disabled(busy))
                if (ImGui.Button("Remove")) removeIdx = i;

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

        // ── Add step (selecting a profile appends it immediately) ─────────────
        ImGui.TextUnformatted("Add step:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.BeginCombo("##SeqAddProfile", "— add a profile —"))
            {
                if (ImGui.IsWindowAppearing()) { addFilter = ""; ImGui.SetKeyboardFocusHere(); }
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##SeqAddFilter", ref addFilter, 128);
                ImGui.Separator();

                bool any = false;
                for (int i = 0; i < plugin.SavedProfiles.Count; i++)
                {
                    if (!string.IsNullOrEmpty(addFilter) &&
                        !plugin.SavedProfiles[i].Name.Contains(addFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    any = true;
                    if (ImGui.Selectable(plugin.SavedProfiles[i].Name))
                    {
                        var (id, pname) = plugin.SavedProfiles[i];
                        seq.Steps.Add(new MorphSequenceStep(id, pname, defaultSpeed, EasingMode.Linear));
                        needSave  = true;
                        addFilter = "";
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
