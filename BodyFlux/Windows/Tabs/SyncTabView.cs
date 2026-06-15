using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace BodyFlux.Windows.Tabs;

/// <summary>
/// Renders the "Sync" tab: the group Sync Key, the remote-morph consent toggle,
/// connection controls, status, and the list of connected peers.
/// </summary>
public sealed class SyncTabView
{
    private readonly Plugin plugin;

    public SyncTabView(Plugin plugin) => this.plugin = plugin;

    private static string GeneratePairKey()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var buf = new char[9];
        for (int i = 0; i < 4; i++) buf[i] = chars[Random.Shared.Next(chars.Length)];
        buf[4] = '-';
        for (int i = 5; i < 9; i++) buf[i] = chars[Random.Shared.Next(chars.Length)];
        return new string(buf);
    }

    public void Draw()
    {
        float bw     = 90 * ImGuiHelpers.GlobalScale;
        float labelW = 90 * ImGuiHelpers.GlobalScale;
        var   config = plugin.Configuration;

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(
            "Generate a Sync Key and share it with your partner(s) to sync together " +
            "and see each other's morphs in real time.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Enable toggle ─────────────────────────────────────────────────────
        bool syncEnabled = config.NetworkSyncEnabled;
        if (ImGui.Checkbox("Enable Group Sync", ref syncEnabled))
        {
            config.NetworkSyncEnabled = syncEnabled;
            config.Save();
            if (!syncEnabled)
                plugin.DisconnectNetworkSync();
        }

        ImGui.Spacing();

        bool allowRemote = plugin.AllowRemoteMorph;
        if (ImGui.Checkbox("Allow others to morph my character", ref allowRemote))
            plugin.AllowRemoteMorph = allowRemote;

        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled("When enabled, connected players with your Sync Key can apply morph profiles to your character.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();

        // ── Sync Key row ──────────────────────────────────────────────────────
        float inputW = ImGui.GetContentRegionAvail().X - labelW - 8 * ImGuiHelpers.GlobalScale;

        ImGui.TextUnformatted("Sync Key:");
        ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(inputW - bw * 2 - 8 * ImGuiHelpers.GlobalScale);
        string pairKey = config.PairKey;
        if (ImGui.InputText("##SyncKey", ref pairKey, 64))
        {
            config.PairKey = pairKey;
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Generate", new Vector2(bw, 0)))
        {
            config.PairKey = GeneratePairKey();
            config.Save();
        }

        ImGui.SameLine();
        if (plugin.IsNetworkActive)
        {
            if (ImGui.Button("Disconnect", new Vector2(bw, 0)))
                plugin.DisconnectNetworkSync();
        }
        else if (plugin.IsNetworkIdleDisconnected)
        {
            if (ImGui.Button("Reconnect", new Vector2(bw, 0)))
                plugin.ApplyNetworkSyncConfig();
        }
        else
        {
            if (ImGui.Button("Connect", new Vector2(bw, 0)))
                plugin.ApplyNetworkSyncConfig();
        }

        // ── Connection status ─────────────────────────────────────────────────
        ImGui.Spacing();
        var status = plugin.NetworkSyncStatus;
        var statusColor = status == "Connected"
            ? new Vector4(0.3f, 0.9f, 0.4f, 1f)
            : status.StartsWith("Connecting")
                ? new Vector4(0.9f, 0.8f, 0.2f, 1f)
                : status == "Disconnected (idle)"
                    ? new Vector4(0.9f, 0.6f, 0.2f, 1f)  // amber — auto-disconnected by inactivity
                    : new Vector4(0.7f, 0.7f, 0.7f, 1f);
        ImGui.TextColored(statusColor, $"Status: {status}");

        // ── Lone-connection auto-disconnect notice ────────────────────────────
        // The relay only needs to carry frames between connected players, so a lone connection is
        // auto-disconnected to save bandwidth. Show the running countdown while alone.
        if (plugin.IsNetworkAwaitingIdleDisconnect)
        {
            int secs = plugin.NetworkIdleDisconnectSecondsRemaining;
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(new Vector4(0.9f, 0.6f, 0.2f, 1f),
                $"No peers connected to Sync Key... Auto-disconnecting in {secs / 60}:{secs % 60:D2}");
            ImGui.PopTextWrapPos();
        }

        // ── Connected users ───────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Connected users:");

        var peers     = plugin.ConnectedPeers;
        var localName = plugin.LocalPlayerName;

        if (peers == null)
        {
            ImGui.TextDisabled("  (not connected)");
        }
        else
        {
            if (!string.IsNullOrEmpty(localName))
                ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.6f, 1f), $"  {localName} (you)");

            var  cutoff  = DateTime.UtcNow.AddSeconds(-90);
            bool anyPeer = false;
            foreach (var kv in peers)
            {
                if (kv.Value < cutoff) continue;
                ImGui.TextUnformatted($"  {kv.Key}");
                anyPeer = true;
            }
            if (!anyPeer)
                ImGui.TextDisabled("  No other users connected.");
        }
    }
}
