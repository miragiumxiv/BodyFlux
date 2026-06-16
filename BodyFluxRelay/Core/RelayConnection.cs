using System.Net.WebSockets;
using System.Text.Json;

namespace BodyFluxRelay.Core;

/// <summary>
/// Handles the lifetime of a single WebSocket connection: it joins the socket to its room, forwards
/// every received message verbatim to the other room members, and identifies the connection once
/// (hashed) for the log. The relay never interprets payloads beyond reading the "sender" field for
/// that one log line — everything else is opaque pass-through.
/// </summary>
public sealed class RelayConnection
{
    // 512 KB — large enough for any (compressed) Customize+ profile message.
    private const int BufferSize = 512 * 1024;

    private readonly RoomRegistry _rooms;
    private readonly RelayLog     _log;

    public RelayConnection(RoomRegistry rooms, RelayLog log)
    {
        _rooms = rooms;
        _log   = log;
    }

    public async Task HandleAsync(WebSocket ws, string groupCode)
    {
        var peerCount = _rooms.Join(groupCode, ws);
        _log.Write($"[+] room={groupCode}  peers={peerCount}");

        // Hashed identity, resolved from the first message that names a sender. Stays null
        // (logged as "?") if the client never identifies itself.
        string? userId = null;
        var buffer = new byte[BufferSize];

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(ws, buffer);
                if (message == null) break; // close frame

                userId ??= TryIdentify(message, groupCode);
                await BroadcastAsync(groupCode, ws, message);
            }
        }
        catch { /* abrupt disconnect — handled in finally */ }
        finally
        {
            var remaining = _rooms.Leave(groupCode, ws);
            _log.Write($"[-] room={groupCode}  user={userId ?? "?"}  peers={remaining}");
        }
    }

    /// <summary>Accumulates one (possibly fragmented) message. Returns null on a close frame.</summary>
    private static async Task<byte[]?> ReceiveMessageAsync(WebSocket ws, byte[] buffer)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return ms.ToArray();
    }

    /// <summary>Forwards the message to every other open socket, pruning any that fail to send.</summary>
    private async Task BroadcastAsync(string groupCode, WebSocket self, byte[] message)
    {
        List<WebSocket> dead = [];
        foreach (var peer in _rooms.PeersExcept(groupCode, self))
        {
            try   { await peer.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None); }
            catch { dead.Add(peer); }
        }
        _rooms.Remove(groupCode, dead);
    }

    /// <summary>
    /// Reads the "sender" field once and returns its hashed log id, or null if the payload is not
    /// JSON or carries no sender. Called only until the connection is identified, so the broadcast
    /// hot path is untouched afterwards.
    /// </summary>
    private string? TryIdentify(byte[] message, string groupCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("sender", out var se)
                && se.ValueKind == JsonValueKind.String
                && se.GetString() is { Length: > 0 } sender)
            {
                var id = _log.IdFor(sender);
                _log.Write($"[id] room={groupCode}  user={id}");
                return id;
            }
        }
        catch { /* not JSON or no sender — stay anonymous */ }
        return null;
    }
}
