using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace BodyFlux.Ipc;

/// <summary>
/// Minimal gateway to Brio's IPC. Used to enumerate the actors Brio manages —
/// the local player and Brio-spawned clones — so the Brio tab only offers those
/// as morph targets, never other players, NPCs, or world entities in the scene.
/// </summary>
public sealed class BrioIpc
{
    private const string BrioInternalName = "Brio";

    // Brio's public API endpoints (v0.7.x).
    private const string LabelGetAllActors      = "Brio.GetAllActors.V3";
    private const string LabelGetModelTransform = "Brio.GetModelTransform.V3";
    private const string LabelSetModelTransform = "Brio.SetModelTransform.V3";

    private readonly IDalamudPluginInterface _pi;
    private readonly ICallGateSubscriber<IGameObject[]?> _getAllActors;

    // GetModelTransform: actor -> (position?, rotation?, scale?)
    private readonly ICallGateSubscriber<IGameObject, ValueTuple<Vector3?, Quaternion?, Vector3?>> _getModelTransform;
    // SetModelTransform: (actor, position?, rotation?, scale?, relativeMode) -> bool
    private readonly ICallGateSubscriber<IGameObject, Vector3?, Quaternion?, Vector3?, bool, bool> _setModelTransform;

    private readonly IPluginLog _log;

    public BrioIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        _pi           = pi;
        _log          = log;
        _getAllActors = pi.GetIpcSubscriber<IGameObject[]?>(LabelGetAllActors);
        _getModelTransform = pi.GetIpcSubscriber<IGameObject, ValueTuple<Vector3?, Quaternion?, Vector3?>>(LabelGetModelTransform);
        _setModelTransform = pi.GetIpcSubscriber<IGameObject, Vector3?, Quaternion?, Vector3?, bool, bool>(LabelSetModelTransform);
    }

    /// <summary>True when the Brio plugin is installed and currently loaded.</summary>
    public bool IsAvailable =>
        _pi.InstalledPlugins.Any(p => p.InternalName == BrioInternalName && p.IsLoaded);

    /// <summary>
    /// Returns the ObjectTable indices and names of every Brio-managed actor.
    /// Returns an empty list when Brio is not installed or has no actors.
    /// </summary>
    public List<(ushort Index, string Name)> GetManagedActors()
    {
        var result = new List<(ushort, string)>();
        try
        {
            var actors = _getAllActors.InvokeFunc();
            if (actors == null) return result;

            foreach (var obj in actors)
            {
                if (obj == null || string.IsNullOrEmpty(obj.Name.TextValue)) continue;
                result.Add((obj.ObjectIndex, obj.Name.TextValue));
            }
        }
        catch (IpcNotReadyError)
        {
            // Brio not installed / not ready — caller treats the empty list as "no actors".
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BodyFlux] Brio GetAllActors IPC threw.");
        }
        return result;
    }

    /// <summary>
    /// Reads the actor's Brio model transform (whole-model position/rotation/scale in world space).
    /// Any component may be <c>null</c> if Brio does not currently track it. Returns all-null on failure.
    /// </summary>
    public (Vector3? Position, Quaternion? Rotation, Vector3? Scale) GetModelTransform(IGameObject actor)
    {
        try
        {
            var (pos, rot, scale) = _getModelTransform.InvokeFunc(actor);
            return (pos, rot, scale);
        }
        catch (IpcNotReadyError) { return (null, null, null); }
        catch (Exception ex)
        {
            _log.Error(ex, "[BodyFlux] Brio GetModelTransform IPC threw.");
            return (null, null, null);
        }
    }

    /// <summary>
    /// Sets the actor's Brio model transform. Pass <c>null</c> for any component to leave it unchanged
    /// (e.g. position/rotation null to preserve the user's manual repositioning while driving scale).
    /// Uses absolute mode. Returns false on failure.
    /// </summary>
    public bool SetModelTransform(IGameObject actor, Vector3? position, Quaternion? rotation, Vector3? scale)
    {
        try { return _setModelTransform.InvokeFunc(actor, position, rotation, scale, false); }
        catch (IpcNotReadyError) { return false; }
        catch (Exception ex)
        {
            _log.Error(ex, "[BodyFlux] Brio SetModelTransform IPC threw.");
            return false;
        }
    }
}
