using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BodyFlux.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private bool _refreshedOnOpen;

    // ── GPose tab-management state ────────────────────────────────────────────
    private bool _wasInGpose;
    private bool _requestPlayerTabFocus;

    // Owned here (not by a tab view) because it must be drawn at the top level so an open
    // dialog survives tab switches; shared into the Brio tab for the MCDF pickers.
    private readonly FileDialogManager _fileDialog = new();

    // ── Tab views (extracted renderers) ───────────────────────────────────────
    private readonly Tabs.SyncTabView      _syncTab;
    private readonly Tabs.SequenceListView _seqList;
    private readonly Tabs.PlayerTabView    _playerTab;
    private readonly Tabs.BrioTabView      _brioTab;
    private readonly Tabs.KeybindsTabView  _keybindsTab;

    public MainWindow(Plugin plugin)
        : base("Body Flux##BodyFluxMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        Size          = new Vector2(500, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;

        _syncTab   = new Tabs.SyncTabView(plugin);
        _seqList   = new Tabs.SequenceListView(plugin);
        _playerTab = new Tabs.PlayerTabView(plugin, _seqList, () => _refreshedOnOpen = false);
        _brioTab   = new Tabs.BrioTabView(plugin, _seqList, _fileDialog);
        _keybindsTab = new Tabs.KeybindsTabView(plugin);

        // The Multi "Apply All" config lives in the Brio tab view, so let the keybind handler
        // trigger it through here.
        plugin.Keybinds.ApplyMultiHandler = _brioTab.ApplyGroup;
    }

    public void Dispose() { }

    public override void OnOpen()
    {
        if (!_refreshedOnOpen)
        {
            plugin.RefreshProfiles();
            _refreshedOnOpen = true;
        }
    }

    // ── Top-level draw ────────────────────────────────────────────────────────

    public override void Draw()
    {
        // ── GPose state-change detection ──────────────────────────────────────
        bool inGpose = plugin.IsInGPose;

        if (inGpose && !_wasInGpose)
        {
            // Just entered GPose: enable the Brio tab and reset its selections,
            // but do NOT steal focus — the user stays on whatever tab they were on.
            plugin.SelectedBrioActorIndex = -1;
            plugin.SelectedBrioDestIndex  = -1;
        }
        else if (!inGpose && _wasInGpose)
        {
            // Just exited GPose: tear down every Brio actor morph so nothing lingers, then
            // return to the Player tab. The player session is left untouched.
            plugin.ResetAllBrio();
            _requestPlayerTabFocus = true;
        }

        _wasInGpose = inGpose;

        // ── Tab bar ───────────────────────────────────────────────────────────
        using var tabBar = ImRaii.TabBar("##MainTabs");
        if (!tabBar) return;

        // Player tab — gains focus when returning from GPose
        var playerFlags = _requestPlayerTabFocus
            ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        _requestPlayerTabFocus = false;
        using (var tab = ImRaii.TabItem("Player", playerFlags))
            if (tab) _playerTab.Draw();

        // Sync tab — label turns green while actively connected.
        // NOTE: must use a scoped `using (...)` block so EndTabItem fires before the next
        // tab is declared. A bare `using var` defers EndTabItem to end of Draw(), leaving the
        // Sync item "open" and causing the following Brio tab to nest under it (and stop
        // responding to clicks) whenever Sync is the active tab.
        bool syncLive = plugin.Configuration.NetworkSyncEnabled
                     && plugin.NetworkSyncStatus == "Connected";
        if (syncLive)
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 0.4f, 1f));
        using (var syncTab = ImRaii.TabItem("Sync"))
        {
            if (syncLive)
                ImGui.PopStyleColor(); // pop after BeginTabItem; tab content draws in normal colour
            if (syncTab) _syncTab.Draw();
        }

        // Brio tab — always visible; disabled (grey, non-clickable) outside GPose.
        // Using explicit Begin/EndDisabled rather than ImRaii.Disabled(!inGpose) because
        // the RAII wrapper always calls EndDisabled() even when disabled=false, which
        // unbalances the ImGui disabled stack and keeps the tab grey while in GPose.
        if (!inGpose) ImGui.BeginDisabled();
        using (var tab = ImRaii.TabItem("Gpose"))
            if (tab) _brioTab.Draw();
        if (!inGpose) ImGui.EndDisabled();

        // Keybinds tab — configure hotkeys for the morph operations.
        using (var tab = ImRaii.TabItem("Keybinds"))
            if (tab) _keybindsTab.Draw();

        // Render any open file dialog (MCDF origin picker) on top of the window.
        _fileDialog.Draw();
    }

}
