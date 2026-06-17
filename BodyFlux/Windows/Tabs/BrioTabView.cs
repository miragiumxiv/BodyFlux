using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using BodyFlux.Morph;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// Renders the "Brio" tab and its sub-tabs (Single, Presets, Sequences, Multi): per-actor morph
/// config, the active-morphs list, the multi-actor group editor, Brio presets and history.
/// Only meaningful in GPose; the parent gates interactivity.
/// </summary>
public sealed class BrioTabView
{
    private readonly Plugin            plugin;
    private readonly SequenceListView  seqList;
    private readonly FileDialogManager fileDialog; // shared with MainWindow, drawn at the top level

    private string _profileFilter   = ""; // Single-tab destination picker filter
    private string _seqAddFilter     = ""; // Sequences "Add step" picker filter

    // ── Group (Multi) sub-tab state ───────────────────────────────────────────
    // One configurable entry per actor; "Apply All" morphs them simultaneously. In-memory only.
    private sealed class GroupEntry
    {
        public ushort     ActorIndex;
        public int        DestIndex = -1;      // index into SavedProfiles
        public string?    OriginMcdfJson;      // captured MCDF JSON (null = permanent/identity)
        public string     OriginMcdfLabel = "";
        public float      Speed  = 0.5f;
        public MorphMode  Mode   = MorphMode.Simple;
        public EasingMode Easing = EasingMode.Linear;
    }
    private readonly List<GroupEntry> _group = [];
    private string _groupProfileFilter = ""; // shared filter for the per-row destination pickers

    public BrioTabView(Plugin plugin, SequenceListView seqList, FileDialogManager fileDialog)
    {
        this.plugin     = plugin;
        this.seqList    = seqList;
        this.fileDialog = fileDialog;
    }

    public void Draw()
    {
        using var subTabs = ImRaii.TabBar("##BrioSubTabs");
        if (!subTabs) return;

        using (var tab = ImRaii.TabItem("Single##BrioSub"))
            if (tab) DrawMorphSubTab();

        using (var tab = ImRaii.TabItem("Presets##BrioSub"))
            if (tab) DrawPresetsTab();

        using (var tab = ImRaii.TabItem("Sequences##BrioSub"))
            if (tab) DrawSequencesSubTab();

        using (var tab = ImRaii.TabItem("Multi##BrioSub"))
            if (tab) DrawGroupSubTab();
    }

    private void DrawMorphSubTab()
    {
        float scale  = ImGuiHelpers.GlobalScale;
        float bw     = 90 * scale;
        var   config = plugin.Configuration;

        // ── Requirements (Customize+ and Brio must both be active) ─────────────
        var okColor  = new Vector4(0.3f, 0.9f, 0.4f, 1f);
        var badColor = new Vector4(0.9f, 0.3f, 0.3f, 1f);

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        bool cpOk = plugin.IsCustomizePlusAvailable;
        ImGui.TextColored(cpOk ? okColor : badColor,
            cpOk ? "Customize+ enabled"
                 : "Customize+ not detected — please install and enable it");

        bool brioOk = plugin.IsBrioAvailable;
        ImGui.TextColored(brioOk ? okColor : badColor,
            brioOk ? "Brio enabled"
                   : "Brio not detected — please install and enable it");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Configure an actor + destination below and press Apply to morph it. Multiple actors " +
            "can morph at once — each appears in the Active Morphs list with its own controls. " +
            "All actors reset automatically when you exit GPose.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Actor selector ────────────────────────────────────────────────────
        ImGui.TextUnformatted("GPose Actor:");
        ImGui.Spacing();

        var actors = plugin.GetGPoseActors();

        string actorPreview = "— select an actor —";
        if (plugin.SelectedBrioActorIndex >= 0)
        {
            var found = actors.Find(a => a.Index == (ushort)plugin.SelectedBrioActorIndex);
            actorPreview = found.Name != null
                ? $"[{found.Index}] {found.Name}"
                : $"Actor {plugin.SelectedBrioActorIndex} (not in scene)";
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo("##ActorSelect", actorPreview))
        {
            if (actors.Count == 0)
            {
                ImGui.TextDisabled("No GPose actors found. Create clones using Brio.");
            }
            else
            {
                foreach (var (idx, name) in actors)
                {
                    bool sel = plugin.SelectedBrioActorIndex == idx;
                    string tag = plugin.IsBrioActorActive(idx) ? "  (morphing)" : "";
                    if (ImGui.Selectable($"[{idx}] {name}{tag}##actor{idx}", sel))
                        plugin.SelectedBrioActorIndex = idx;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Origin scaling (MCDF) ─────────────────────────────────────────────
        ImGui.TextUnformatted("Origin Scaling (MCDF):");
        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Customize+ can't read a clone's applied scaling, so load the MCDF that was " +
            "applied to it. It becomes the starting point for the next Apply and is restored " +
            "on that actor's Reset. Without it, the morph starts from an unscaled body.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        if (ImGui.Button("Load MCDF…", new Vector2(bw + 30 * scale, 0)))
        {
            var startPath = plugin.GetBrioLastMcdfFolder() ?? "";
            fileDialog.OpenFileDialog(
                "Select the origin MCDF", "MCDF files{.mcdf}",
                (ok, paths) =>
                {
                    if (ok && paths.Count > 0)
                        plugin.LoadBrioOriginMcdf(paths[0]);
                },
                1, startPath, false);
        }

        if (plugin.BrioOriginLoaded)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear##BrioOrigin", new Vector2(bw, 0)))
                plugin.ClearBrioOrigin();
        }

        ImGui.Spacing();
        if (plugin.BrioOriginLoaded)
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f), $"Origin: {plugin.BrioOriginLabel}");
        else if (plugin.BrioOriginLabel.Length > 0) // an error was reported
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), plugin.BrioOriginLabel);
        else
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "No MCDF loaded — morph will start unscaled.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Destination profile selector ──────────────────────────────────────
        ImGui.TextUnformatted("Destination Profile:");

        float destW = ImGui.GetContentRegionAvail().X - (bw + 8 * scale);
        ImGui.SetNextItemWidth(destW);

        string destPreview = plugin.SelectedBrioDestIndex >= 0
                          && plugin.SelectedBrioDestIndex < plugin.SavedProfiles.Count
            ? plugin.SavedProfiles[plugin.SelectedBrioDestIndex].Name
            : "— select a profile —";

        if (ImGui.BeginCombo("##BrioDestProfile", destPreview))
        {
            if (ImGui.IsWindowAppearing())
            {
                _profileFilter = "";
                ImGui.SetKeyboardFocusHere();
            }

            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##BrioProfileFilter", ref _profileFilter, 128);
            ImGui.Separator();

            bool anyVisible = false;
            for (int i = 0; i < plugin.SavedProfiles.Count; i++)
            {
                if (!string.IsNullOrEmpty(_profileFilter) &&
                    !plugin.SavedProfiles[i].Name.Contains(_profileFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                anyVisible = true;
                bool sel = plugin.SelectedBrioDestIndex == i;
                if (ImGui.Selectable(plugin.SavedProfiles[i].Name, sel))
                {
                    plugin.SelectedBrioDestIndex = i;
                    _profileFilter               = "";
                }
                if (sel) ImGui.SetItemDefaultFocus();
            }

            if (!anyVisible)
                ImGui.TextDisabled("No matches.");

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh##BrioRefresh", new Vector2(bw, 0)))
        {
            plugin.RefreshProfiles();
            plugin.SelectedBrioDestIndex = -1;
        }

        ImGui.Spacing();

        // ── Speed ─────────────────────────────────────────────────────────────
        float speed = config.BrioGrowthSpeed;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 180 * scale);
        if (ImGui.SliderFloat("Growth Speed (range/sec)##Brio", ref speed, 0.01f, 1f, "%.2f"))
        {
            config.BrioGrowthSpeed = speed;
            config.Save();
        }

        ImGui.Spacing();
        float secs = speed > 0f ? 1f / speed : float.PositiveInfinity;
        ImGui.TextDisabled($"Time to complete: {secs:F1}s");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Mode selector ─────────────────────────────────────────────────────
        ImGui.TextUnformatted("Morph Mode:");
        ImGui.Spacing();

        int  modeInt    = (int)config.MorphMode;
        bool modeChanged = false;
        if (ImGui.RadioButton("Simple##Brio",          ref modeInt, (int)MorphMode.Simple))       modeChanged = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("Loop (Single)##Brio",   ref modeInt, (int)MorphMode.LoopSingle))   modeChanged = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("Loop (Infinite)##Brio", ref modeInt, (int)MorphMode.LoopInfinite)) modeChanged = true;
        if (modeChanged) { config.MorphMode = (MorphMode)modeInt; config.Save(); }

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        string modeDesc = config.MorphMode switch
        {
            MorphMode.Simple       => "Morphs from the actor's current profile to the destination once.",
            MorphMode.LoopSingle   => "Morphs to the destination, then reverses back to the starting profile once.",
            MorphMode.LoopInfinite => "Continuously ping-pongs between the starting and destination profiles until Reset is pressed.",
            _                      => ""
        };
        ImGui.TextDisabled(modeDesc);
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Easing Curve ──────────────────────────────────────────────────────
        ImGui.TextUnformatted("Easing Curve:");
        ImGui.Spacing();

        int  easingInt     = (int)config.BrioEasingMode;
        bool easingChanged = false;
        if (ImGui.RadioButton("Linear##BrioEasing",      ref easingInt, (int)EasingMode.Linear))    easingChanged = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("Ease In##BrioEasing",     ref easingInt, (int)EasingMode.EaseIn))    easingChanged = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("Ease Out##BrioEasing",    ref easingInt, (int)EasingMode.EaseOut))   easingChanged = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("Ease In-Out##BrioEasing", ref easingInt, (int)EasingMode.EaseInOut)) easingChanged = true;
        if (easingChanged) { config.BrioEasingMode = (EasingMode)easingInt; config.Save(); }

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        string easingDesc = config.BrioEasingMode switch
        {
            EasingMode.EaseIn    => "Starts slow and accelerates toward the destination.",
            EasingMode.EaseOut   => "Starts fast and decelerates as it approaches the destination.",
            EasingMode.EaseInOut => "Starts slow, accelerates through the middle, then decelerates at the end.",
            _                    => "Constant speed throughout the morph."
        };
        ImGui.TextDisabled(easingDesc);
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Apply (adds/updates the selected actor's morph) ───────────────────
        bool canApply = plugin.SelectedBrioActorIndex >= 0
                     && plugin.SelectedBrioDestIndex  >= 0
                     && plugin.SelectedBrioDestIndex  <  plugin.SavedProfiles.Count;
        using (ImRaii.Disabled(!canApply))
        {
            if (ImGui.Button("Apply##Brio", new Vector2(bw + 30 * scale, 0)))
                plugin.StartBrioMorph();
        }
        if (canApply && plugin.IsBrioActorActive((ushort)plugin.SelectedBrioActorIndex)
            && ImGui.IsItemHovered())
            ImGui.SetTooltip("This actor already has an active morph — Apply replaces it.");

        // ── Active morphs list ────────────────────────────────────────────────
        DrawActiveMorphs(actors, bw, scale);

        DrawHistory();
    }

    /// <summary>Renders one row per active Brio actor morph, each with its own controls.</summary>
    private void DrawActiveMorphs(List<(ushort Index, string Name)> actors, float bw, float scale)
    {
        var active = plugin.GetActiveBrioMorphs();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted($"Active Morphs ({active.Count}):");
        ImGui.Spacing();

        if (active.Count == 0)
        {
            ImGui.TextDisabled("   No actors are being morphed. Configure above and press Apply.");
            return;
        }

        foreach (var m in active)
        {
            ImGui.PushID(m.Index);

            var found = actors.Find(a => a.Index == m.Index);
            string name = found.Name ?? "(not in scene)";
            ImGui.TextUnformatted($"[{m.Index}] {name}");
            if (m.SeqRunning)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"— sequence step {m.SeqStep}/{m.SeqStepCount}");
            }

            ImGui.ProgressBar(m.Progress, new Vector2(-1, 16 * scale),
                              $"{m.Progress * 100f:F0}%%  ({m.BoneCount} bones)");

            bool drew = false;
            if (m.IsMorphing)
            {
                if (ImGui.Button("Pause", new Vector2(bw, 0))) plugin.PauseBrioActor(m.Index);
                drew = true;
            }
            else if (m.IsPaused)
            {
                if (ImGui.Button("Resume", new Vector2(bw, 0))) plugin.ResumeBrioActor(m.Index);
                drew = true;
            }

            // Reverse only applies to single morphs (sequences chain forward only).
            if (!m.SeqRunning && (m.IsMorphing || m.IsPaused || m.IsFinished))
            {
                if (drew) ImGui.SameLine();
                if (ImGui.Button("Reverse", new Vector2(bw, 0))) plugin.ReverseBrioActor(m.Index);
                drew = true;
            }

            if (drew) ImGui.SameLine();
            if (ImGui.Button("Reset", new Vector2(bw, 0))) plugin.ResetBrioActor(m.Index);

            ImGui.PopID();
            ImGui.Spacing();
        }
    }

    private void DrawSequencesSubTab()
    {
        var   config = plugin.Configuration;
        float scale  = ImGuiHelpers.GlobalScale;
        float bw     = 90 * scale;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Chain several morphs into an automatic A→B→C sequence and Play it on a GPose actor. " +
            "The target actor and MCDF origin come from your selection in the Single tab; live " +
            "playback and Stop appear there in the Active Morphs list. Several actors can run " +
            "sequences at once.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Target info (from the Single tab selection) ────────────────────────
        bool   actorSelected = plugin.SelectedBrioActorIndex >= 0;
        string actorName     = "— none —";
        if (actorSelected)
        {
            var found = plugin.GetGPoseActors().Find(a => a.Index == (ushort)plugin.SelectedBrioActorIndex);
            actorName = found.Name ?? "(not in scene)";
        }

        ImGui.TextUnformatted("Target actor:");
        ImGui.SameLine();
        ImGui.TextColored(actorSelected ? new Vector4(0.4f, 0.9f, 0.6f, 1f) : new Vector4(0.9f, 0.7f, 0.2f, 1f),
            actorName);

        ImGui.TextUnformatted("Origin MCDF:");
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.BrioOriginLoaded ? plugin.BrioOriginLabel : "none — starts from permanent/identity");

        ImGui.TextDisabled("Choose the actor and load an MCDF in the Single tab before playing.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Library + Play. Playback is per-actor, so this tab keeps no single playing-index or
        // footer — Play just launches the sequence onto the selected actor.
        int dummyPlaying = -1;
        int playRequest = seqList.Draw(
            config.BrioSequences, ref dummyPlaying, ref _seqAddFilter,
            config.BrioGrowthSpeed, busy: false, seqActive: false,
            playAllowed: actorSelected, "Select an actor in the Single tab first.",
            onReset: ResetSequenceTarget, bw, scale);

        if (playRequest >= 0)
            plugin.StartBrioSequence(config.BrioSequences[playRequest]);
    }

    /// <summary>Reset target for a Brio sequence's Reset button: the selected actor, or all actors
    /// if none is selected.</summary>
    private void ResetSequenceTarget()
    {
        if (plugin.SelectedBrioActorIndex >= 0)
            plugin.ResetBrioActor((ushort)plugin.SelectedBrioActorIndex);
        else
            plugin.ResetAllBrio();
    }

    private void DrawGroupSubTab()
    {
        var   config = plugin.Configuration;
        float scale  = ImGuiHelpers.GlobalScale;
        float bw     = 90 * scale;
        var   actors = plugin.GetGPoseActors();

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Configure several GPose actors — each with its own MCDF origin, destination, speed, " +
            "mode and easing — then press Apply All to morph them at the same time. The group " +
            "controls act on every active Brio morph.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Add actor ─────────────────────────────────────────────────────────
        ImGui.TextUnformatted("Add actor:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo("##GroupAddActor", "— select an actor to add —"))
        {
            bool any = false;
            foreach (var (idx, name) in actors)
            {
                if (_group.Exists(g => g.ActorIndex == idx)) continue; // already in the group
                any = true;
                if (ImGui.Selectable($"[{idx}] {name}##gadd{idx}"))
                    _group.Add(new GroupEntry
                    {
                        ActorIndex = idx,
                        Speed      = config.BrioGrowthSpeed,
                        Mode       = config.MorphMode,
                        Easing     = config.BrioEasingMode,
                    });
            }
            if (!any)
                ImGui.TextDisabled(actors.Count == 0 ? "No GPose actors found." : "All actors already added.");
            ImGui.EndCombo();
        }

        ImGui.Spacing();
        if (_group.Count == 0)
            ImGui.TextDisabled("No actors configured yet — add one above.");

        // ── Per-actor config rows ─────────────────────────────────────────────
        int removeIdx = -1;
        for (int i = 0; i < _group.Count; i++)
        {
            var g = _group[i];
            ImGui.PushID(i);

            var    found   = actors.Find(a => a.Index == g.ActorIndex);
            string name    = found.Name ?? "(not in scene)";
            bool   active  = plugin.IsBrioActorActive(g.ActorIndex);

            string header = $"[{g.ActorIndex}] {name}" + (active ? "  (morphing)" : "");
            if (ImGui.CollapsingHeader($"{header}##grphdr{i}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent(12 * scale);
                DrawGroupEntryEditor(g, i, bw, scale, ref removeIdx);
                ImGui.Unindent(12 * scale);
            }

            ImGui.PopID();
        }
        if (removeIdx >= 0)
        {
            plugin.ResetBrioActor(_group[removeIdx].ActorIndex); // stop it if currently morphing
            _group.RemoveAt(removeIdx);
        }

        // ── Group controls ────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        int applyable = _group.FindAll(g =>
            g.DestIndex >= 0 && g.DestIndex < plugin.SavedProfiles.Count
            && actors.Exists(a => a.Index == g.ActorIndex)).Count;

        using (ImRaii.Disabled(applyable == 0))
        {
            if (ImGui.Button($"Apply All ({applyable})", new Vector2(bw + 40 * scale, 0)))
                ApplyGroup();
        }

        ImGui.Spacing();
        if (ImGui.Button("Pause All",   new Vector2(bw, 0))) plugin.PauseAllBrio();
        ImGui.SameLine();
        if (ImGui.Button("Resume All",  new Vector2(bw, 0))) plugin.ResumeAllBrio();
        ImGui.SameLine();
        if (ImGui.Button("Reverse All", new Vector2(bw, 0))) plugin.ReverseAllBrio();
        ImGui.SameLine();
        if (ImGui.Button("Reset All",   new Vector2(bw, 0))) plugin.ResetAllBrio();
    }

    /// <summary>
    /// Morphs every configured group entry at once (the Multi tab's "Apply All"). Skips entries with
    /// no destination or whose actor has left the scene. Also invoked by the "Apply Multi" keybind.
    /// </summary>
    public void ApplyGroup()
    {
        var actors = plugin.GetGPoseActors();
        foreach (var g in _group)
        {
            if (g.DestIndex < 0 || g.DestIndex >= plugin.SavedProfiles.Count) continue;
            if (!actors.Exists(a => a.Index == g.ActorIndex)) continue; // gone from scene
            var destId = plugin.SavedProfiles[g.DestIndex].Id;
            plugin.StartBrioMorphFor(g.ActorIndex, destId, g.OriginMcdfJson, g.Speed, g.Mode, g.Easing);
        }
    }

    /// <summary>Draws the config editor for one group entry (destination, MCDF, speed, mode, easing).</summary>
    private void DrawGroupEntryEditor(GroupEntry g, int index, float bw, float scale, ref int removeIdx)
    {
        // ── Destination profile ───────────────────────────────────────────────
        ImGui.TextUnformatted("Destination:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        string destPreview = g.DestIndex >= 0 && g.DestIndex < plugin.SavedProfiles.Count
            ? plugin.SavedProfiles[g.DestIndex].Name
            : "— select a profile —";
        if (ImGui.BeginCombo("##grpdest", destPreview))
        {
            if (ImGui.IsWindowAppearing()) { _groupProfileFilter = ""; ImGui.SetKeyboardFocusHere(); }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##grpdestfilter", ref _groupProfileFilter, 128);
            ImGui.Separator();

            bool any = false;
            for (int i = 0; i < plugin.SavedProfiles.Count; i++)
            {
                if (!string.IsNullOrEmpty(_groupProfileFilter) &&
                    !plugin.SavedProfiles[i].Name.Contains(_groupProfileFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                any = true;
                bool sel = g.DestIndex == i;
                if (ImGui.Selectable(plugin.SavedProfiles[i].Name, sel))
                {
                    g.DestIndex         = i;
                    _groupProfileFilter = "";
                }
                if (sel) ImGui.SetItemDefaultFocus();
            }
            if (!any) ImGui.TextDisabled("No matches.");
            ImGui.EndCombo();
        }

        // ── MCDF origin ───────────────────────────────────────────────────────
        if (ImGui.Button("Load MCDF…##grp"))
        {
            var startPath = plugin.GetBrioLastMcdfFolder() ?? "";
            fileDialog.OpenFileDialog(
                "Select the origin MCDF", "MCDF files{.mcdf}",
                (ok, paths) =>
                {
                    if (ok && paths.Count > 0)
                    {
                        var (rok, json, label) = plugin.ReadMcdfProfile(paths[0]);
                        g.OriginMcdfJson  = rok ? json : null;
                        g.OriginMcdfLabel = label;
                    }
                },
                1, startPath, false);
        }
        if (g.OriginMcdfJson != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear##grpmcdf")) { g.OriginMcdfJson = null; g.OriginMcdfLabel = ""; }
        }
        ImGui.SameLine();
        if (g.OriginMcdfJson != null)
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f), g.OriginMcdfLabel);
        else if (g.OriginMcdfLabel.Length > 0) // an error was reported
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), g.OriginMcdfLabel);
        else
            ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "no MCDF — starts unscaled/permanent");

        // ── Speed ─────────────────────────────────────────────────────────────
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60 * scale);
        float sp = g.Speed;
        if (ImGui.SliderFloat("Speed##grp", ref sp, 0.01f, 1f, "%.2f")) g.Speed = sp;

        float grpSecs = g.Speed > 0f ? 1f / g.Speed : float.PositiveInfinity;
        ImGui.TextDisabled($"Time to complete: {grpSecs:F1}s");

        // ── Mode ──────────────────────────────────────────────────────────────
        ImGui.TextUnformatted("Mode:");
        ImGui.SameLine();
        int modeInt = (int)g.Mode;
        if (ImGui.RadioButton("Simple##grpm",  ref modeInt, (int)MorphMode.Simple))       g.Mode = (MorphMode)modeInt;
        ImGui.SameLine();
        if (ImGui.RadioButton("Loop×1##grpm",  ref modeInt, (int)MorphMode.LoopSingle))   g.Mode = (MorphMode)modeInt;
        ImGui.SameLine();
        if (ImGui.RadioButton("Loop∞##grpm",   ref modeInt, (int)MorphMode.LoopInfinite)) g.Mode = (MorphMode)modeInt;

        // ── Easing ────────────────────────────────────────────────────────────
        ImGui.TextUnformatted("Easing:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140 * scale);
        if (ImGui.BeginCombo("##grpeas", UiHelpers.EasingNames[(int)g.Easing]))
        {
            for (int e = 0; e < UiHelpers.EasingNames.Length; e++)
            {
                bool sel = (int)g.Easing == e;
                if (ImGui.Selectable(UiHelpers.EasingNames[e], sel)) g.Easing = (EasingMode)e;
                if (sel) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        // ── Live progress (when this actor is morphing) + remove ──────────────
        if (plugin.IsBrioActorActive(g.ActorIndex))
        {
            float prog = plugin.GetBrioActorProgress(g.ActorIndex);
            ImGui.ProgressBar(prog, new Vector2(-1, 14 * scale), $"{prog * 100f:F0}%%");
        }

        if (ImGui.Button("Remove##grp", new Vector2(bw, 0))) removeIdx = index;
        ImGui.Spacing();
    }

    private void DrawPresetsTab()
    {
        var   config = plugin.Configuration;
        float bw     = 90 * ImGuiHelpers.GlobalScale;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Save up to 20 Brio morph configurations for quick access. Each preset remembers " +
            "the actor and MCDF origin it was saved with, so Apply re-targets that actor — " +
            "it only needs to be present in the current GPose scene.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool canSave  = plugin.SelectedBrioDestIndex >= 0
                     && plugin.SelectedBrioDestIndex  < plugin.SavedProfiles.Count;
        bool hasActor = plugin.SelectedBrioActorIndex >= 0;
        var  actors   = plugin.GetGPoseActors();

        for (int i = 0; i < Configuration.PresetSlots; i++)
        {
            var  preset    = i < config.BrioPresets.Count ? config.BrioPresets[i] : null;
            bool hasPreset = preset != null;

            // A preset can be applied when it can resolve a target: either its saved actor is
            // present in the scene, or — for legacy presets without a saved actor — an actor is
            // selected in the Single tab.
            bool presetHasTarget = hasPreset && preset!.TargetActorName != null;
            bool targetPresent   = presetHasTarget
                                 && actors.Exists(a => a.Name == preset!.TargetActorName);
            bool canApply        = hasPreset
                                && (targetPresent || (!presetHasTarget && hasActor));

            using (ImRaii.Disabled(!canApply))
            {
                if (ImGui.Button($"Apply##BrioPreset{i}", new Vector2(bw, 0)))
                    plugin.ApplyBrioMorphPreset(preset!);
            }
            if (hasPreset && !canApply && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(presetHasTarget
                    ? $"Target actor '{preset!.TargetActorName}' is not in the current scene."
                    : "Select an actor in the Single tab first.");

            ImGui.SameLine();
            using (ImRaii.Disabled(!canSave))
            {
                if (ImGui.Button($"Save##BrioPreset{i}", new Vector2(bw, 0)))
                    plugin.SaveBrioPreset(i);
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(!hasPreset))
            {
                if (ImGui.Button($"Clear##BrioPreset{i}", new Vector2(bw, 0)))
                    plugin.ClearBrioPreset(i);
            }

            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}.");
            ImGui.SameLine();

            if (hasPreset)
            {
                ImGui.TextUnformatted(preset!.ProfileName);
                ImGui.SameLine();
                ImGui.TextDisabled($"({preset.Speed:F2}/s  {UiHelpers.ModeShort(preset.Mode)}  {UiHelpers.EasingShort(preset.Easing)})");

                // Second line: the captured target actor and MCDF origin.
                if (preset.TargetActorName != null || preset.OriginMcdfLabel != null)
                {
                    string target = preset.TargetActorName == null
                        ? "any selected actor"
                        : targetPresent
                            ? preset.TargetActorName
                            : $"{preset.TargetActorName} (not in scene)";
                    string mcdf = preset.OriginMcdfLabel ?? "no MCDF";

                    float indent = 3 * bw + 3 * ImGui.GetStyle().ItemSpacing.X;
                    ImGui.Indent(indent);
                    ImGui.TextDisabled($"→ {target}   •   MCDF: {mcdf}");
                    ImGui.Unindent(indent);
                }
            }
            else
            {
                ImGui.TextDisabled("— empty —");
            }
        }
    }

    private void DrawHistory()
    {
        var   config   = plugin.Configuration;
        bool  hasActor = plugin.SelectedBrioActorIndex >= 0;
        float bw       = 90 * ImGuiHelpers.GlobalScale;

        if (config.BrioRecentMorphs.Count == 0) return;

        // Section divider between the morph controls and the history list.
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Recent Morphs##Brio")) return;
        ImGui.Spacing();

        // Applying an entry rewrites BrioRecentMorphs (dedup + reinsert), so defer the call
        // until after the loop to avoid mutating the list we're enumerating.
        MorphPreset? toApply = null;
        foreach (var entry in config.BrioRecentMorphs)
        {
            using (ImRaii.Disabled(!hasActor))
            {
                if (ImGui.Button($"Apply##BrioHist{entry.ProfileId}", new Vector2(bw, 0)))
                    toApply = entry;
            }
            if (!hasActor && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Select an actor in the Single tab first.");
            ImGui.SameLine();
            ImGui.TextUnformatted(entry.ProfileName);
            ImGui.SameLine();
            ImGui.TextDisabled($"({entry.Speed:F2}/s  {UiHelpers.ModeShort(entry.Mode)}  {UiHelpers.EasingShort(entry.Easing)})");
        }

        if (toApply != null)
            plugin.ApplyBrioMorphPreset(toApply);
    }
}
