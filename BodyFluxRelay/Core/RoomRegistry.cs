using System.Net.WebSockets;

namespace BodyFluxRelay.Core;

/// <summary>
/// Tracks which WebSockets belong to which room (group code, case-insensitive). All access is
/// serialised by a single gate lock — mutations are tiny (list add/remove/snapshot) and never held
/// across an <c>await</c>, so contention is negligible for this workload while keeping the cleanup
/// of empty rooms race-free. A room exists only while at least one socket is in it: the moment it
/// empties, its key is removed so group codes never accumulate.
/// </summary>
public sealed class RoomRegistry
{
    private readonly Dictionary<string, List<WebSocket>> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Total live connections across all rooms (for the health check).</summary>
    public int TotalConnections
    {
        get { lock (_gate) return _rooms.Values.Sum(room => room.Count); }
    }

    /// <summary>Adds <paramref name="ws"/> to the room (creating it if needed) and returns its size.</summary>
    public int Join(string groupCode, WebSocket ws)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(groupCode, out var room))
                _rooms[groupCode] = room = [];
            room.Add(ws);
            return room.Count;
        }
    }

    /// <summary>
    /// Removes <paramref name="ws"/> and returns the room's resulting size. Deletes the room key
    /// entirely once it becomes empty so unused group codes are freed.
    /// </summary>
    public int Leave(string groupCode, WebSocket ws)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(groupCode, out var room)) return 0;
            room.Remove(ws);
            return PruneIfEmpty(groupCode, room);
        }
    }

    /// <summary>Snapshot of the other open sockets in the room (empty if the room is gone).</summary>
    public List<WebSocket> PeersExcept(string groupCode, WebSocket self)
    {
        lock (_gate)
            return _rooms.TryGetValue(groupCode, out var room)
                ? room.Where(p => p != self && p.State == WebSocketState.Open).ToList()
                : [];
    }

    /// <summary>Drops sockets that failed to receive a broadcast, freeing the room if it empties.</summary>
    public void Remove(string groupCode, IReadOnlyCollection<WebSocket> dead)
    {
        if (dead.Count == 0) return;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(groupCode, out var room)) return;
            foreach (var d in dead)
                room.Remove(d);
            PruneIfEmpty(groupCode, room);
        }
    }

    // Must be called while holding _gate. Removes the room key when empty; returns the room size.
    private int PruneIfEmpty(string groupCode, List<WebSocket> room)
    {
        if (room.Count > 0) return room.Count;
        _rooms.Remove(groupCode);
        return 0;
    }
}
