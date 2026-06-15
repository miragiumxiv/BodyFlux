using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace BodyFlux.Ipc;

/// <summary>
/// Typed gateway for all Customize+ IPC interactions.
/// Owns all subscriber registrations and exposes clean method calls with
/// consistent error handling — callers never see raw IPC exceptions.
/// Also caches the profile list and active profile name for the UI.
/// </summary>
public sealed class CustomizePlusIpc : IDisposable
{
    private readonly IPluginLog _log;

    // ── IPC labels ────────────────────────────────────────────────────────────
    private const string LabelGetList          = "CustomizePlus.Profile.GetList";
    private const string LabelGetActiveProfile = "CustomizePlus.Profile.GetActiveProfileIdOnCharacter";
    private const string LabelGetProfile       = "CustomizePlus.Profile.GetByUniqueId";
    private const string LabelGetTempProfile   = "CustomizePlus.Profile.GetTemporaryProfileOnCharacter";
    private const string LabelSetTempProfile   = "CustomizePlus.Profile.SetTemporaryProfileOnCharacter";
    private const string LabelDeleteTempProfile = "CustomizePlus.Profile.DeleteTemporaryProfileOnCharacter";
    private const string LabelOnProfileUpdate  = "CustomizePlus.Profile.OnUpdate";

    // ── Subscribers ───────────────────────────────────────────────────────────
    // IPCProfileDataTuple = (Guid, string Name, string VirtualPath, List<(string, ushort, byte, ushort)>, int Priority, bool IsEnabled)
    private readonly ICallGateSubscriber<IList<ValueTuple<Guid, string, string,
        List<ValueTuple<string, ushort, byte, ushort>>, int, bool>>>  _getList;
    private readonly ICallGateSubscriber<ushort, ValueTuple<int, Guid?>>         _getActiveProfile;
    private readonly ICallGateSubscriber<Guid,   ValueTuple<int, string?>>        _getProfile;
    private readonly ICallGateSubscriber<ushort, string, ValueTuple<int, Guid?>> _setTempProfile;
    private readonly ICallGateSubscriber<ushort, int>                            _deleteTempProfile;

    /// <summary>
    /// Optional — only present in Customize+ v4.3+.
    /// Reads the temporary (IPC-applied) profile currently active on a character slot.
    /// Used to capture Brio MCDF body shapes as the morph origin.
    /// </summary>
    private readonly ICallGateSubscriber<ushort, ValueTuple<int, string?>>? _getTempProfile;

    /// <summary>
    /// Fired by Customize+ whenever a character's active profile changes.
    /// Carries the ObjectTable index of the affected character; we use index 0 to
    /// detect changes to the local player's own scaling so we can re-broadcast it to peers.
    /// </summary>
    private readonly ICallGateSubscriber<ushort, Guid, object?> _onProfileUpdate;

    /// <summary>Raised on the framework thread with the ObjectTable index of the updated character.</summary>
    public event Action<ushort>? ProfileUpdated;

    // ── Cached UI state ───────────────────────────────────────────────────────
    /// <summary>True when Customize+ is installed and its IPC is responsive.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>All saved profiles returned by the last <see cref="RefreshProfiles"/> call.</summary>
    public IReadOnlyList<(Guid Id, string Name)> Profiles { get; private set; } = [];

    /// <summary>Display name of the profile currently active on the local player (index 0).</summary>
    public string ActiveProfileName { get; private set; } = "(none)";

    // ── Construction ──────────────────────────────────────────────────────────

    public CustomizePlusIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;

        _getList = pi.GetIpcSubscriber<IList<ValueTuple<Guid, string, string,
            List<ValueTuple<string, ushort, byte, ushort>>, int, bool>>>(LabelGetList);
        _getActiveProfile  = pi.GetIpcSubscriber<ushort, ValueTuple<int, Guid?>>(LabelGetActiveProfile);
        _getProfile        = pi.GetIpcSubscriber<Guid,   ValueTuple<int, string?>>(LabelGetProfile);
        _setTempProfile    = pi.GetIpcSubscriber<ushort, string, ValueTuple<int, Guid?>>(LabelSetTempProfile);
        _deleteTempProfile = pi.GetIpcSubscriber<ushort, int>(LabelDeleteTempProfile);

        // GetTemporaryProfileOnCharacter is optional — register and silently null it if absent.
        try { _getTempProfile = pi.GetIpcSubscriber<ushort, ValueTuple<int, string?>>(LabelGetTempProfile); }
        catch { _getTempProfile = null; }

        _onProfileUpdate = pi.GetIpcSubscriber<ushort, Guid, object?>(LabelOnProfileUpdate);
        _onProfileUpdate.Subscribe(OnProfileUpdateReceived);
    }

    private void OnProfileUpdateReceived(ushort gameObjectIndex, Guid _) => ProfileUpdated?.Invoke(gameObjectIndex);

    public void Dispose()
    {
        try { _onProfileUpdate.Unsubscribe(OnProfileUpdateReceived); }
        catch { /* C+ already unloaded — nothing to detach */ }
    }

    // ── Profile list (cached for the UI) ──────────────────────────────────────

    /// <summary>
    /// Queries Customize+ for the full profile list and the currently active profile
    /// on the local player, then updates <see cref="Profiles"/> and <see cref="ActiveProfileName"/>.
    /// </summary>
    public void RefreshProfiles()
    {
        try
        {
            var raw = _getList.InvokeFunc();
            IsAvailable = true;
            // Sorted alphabetically by name (case-insensitive) for the selection dropdowns. Every
            // consumer reads this list by index and resolves IDs from it, and presets match by Id,
            // so a stable alphabetical order keeps selections and lookups consistent.
            Profiles    = raw.Select(p => (Id: p.Item1, Name: p.Item2))
                             .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                             .ToList();

            var (ec, activeId) = _getActiveProfile.InvokeFunc(0);
            ActiveProfileName  = (ec == 0 && activeId.HasValue)
                ? Profiles.FirstOrDefault(p => p.Id == activeId.Value).Name ?? "(unknown)"
                : "(none)";
        }
        catch (IpcNotReadyError)
        {
            IsAvailable = false;
            _log.Error("[BodyFlux] Customize+ IPC not available.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BodyFlux] Error refreshing C+ profile list.");
        }
    }

    // ── Profile read ──────────────────────────────────────────────────────────

    /// <summary>Fetches the serialized JSON for a profile by its unique ID.</summary>
    public (int ec, string? json) GetProfile(Guid id)
    {
        try   { return _getProfile.InvokeFunc(id); }
        catch (IpcNotReadyError)     { return (99, null); }
        catch (Exception ex)         { _log.Error(ex, "[BodyFlux] GetProfile threw."); return (98, null); }
    }

    /// <summary>
    /// Returns the GUID of the named (permanent) profile active on the given object slot.
    /// Returns ec=3 when only a temporary IPC profile is active (e.g. a Brio MCDF clone).
    /// </summary>
    public (int ec, Guid? id) GetActiveProfileId(ushort index = 0)
    {
        try   { return _getActiveProfile.InvokeFunc(index); }
        catch (IpcNotReadyError)     { return (99, null); }
        catch (Exception ex)         { _log.Error(ex, "[BodyFlux] GetActiveProfileId threw."); return (98, null); }
    }

    /// <summary>
    /// Returns the JSON of the temporary (IPC-applied) profile on the given slot.
    /// Returns ec=99 when the endpoint is absent from the installed C+ version.
    /// </summary>
    public (int ec, string? json) GetTempProfile(ushort index)
    {
        if (_getTempProfile == null) return (99, null);
        try   { return _getTempProfile.InvokeFunc(index); }
        catch (IpcNotReadyError)     { return (99, null); }
        catch (Exception ex)         { _log.Error(ex, "[BodyFlux] GetTempProfile threw."); return (98, null); }
    }

    // ── Profile application ───────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="json"/> as a temporary profile on the given object slot.
    /// Returns the C+ error code and the GUID of the applied profile.
    /// </summary>
    public (int ec, Guid? appliedId) SetTempProfile(ushort index, string json)
    {
        try   { return _setTempProfile.InvokeFunc(index, json); }
        catch (IpcNotReadyError)     { return (99, null); }
        catch (Exception ex)         { _log.Error(ex, "[BodyFlux] SetTempProfile threw."); return (98, null); }
    }

    /// <summary>Removes the temporary profile from the given object slot.</summary>
    public int DeleteTempProfile(ushort index)
    {
        try   { return _deleteTempProfile.InvokeFunc(index); }
        catch (IpcNotReadyError)     { return 99; }
        catch (Exception ex)         { _log.Error(ex, "[BodyFlux] DeleteTempProfile threw."); return 98; }
    }
}
