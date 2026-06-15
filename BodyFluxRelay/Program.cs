// BodyFluxRelay — minimal WebSocket room relay for the BodyFlux Dalamud plugin.
//
// Every connection joins a "room" identified by the group code in the URL path.
// All messages received from one client are forwarded verbatim to every other
// client in the same room.  No state is stored; the relay is pure pass-through.
//
// Usage:
//   dotnet run [port]          (default port: 8080)
//
// Deploy anywhere that runs .NET 9 (Railway, Render, Fly.io free tier, VPS, etc.).
// Point the BodyFlux "Relay URL" setting to  ws://<host>:<port>

using System.Collections.Concurrent;
using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// ── Room registry ────────────────────────────────────────────────────────────
// groupCode (case-insensitive) → list of currently-connected WebSockets
var rooms     = new ConcurrentDictionary<string, List<WebSocket>>(StringComparer.OrdinalIgnoreCase);
var roomLocks = new ConcurrentDictionary<string, object>         (StringComparer.OrdinalIgnoreCase);

// ── Health check ──────────────────────────────────────────────────────────────
app.MapGet("/", () => $"BodyFluxRelay OK — {rooms.Values.Sum(r => r.Count)} connection(s)");

// ── WebSocket endpoint ────────────────────────────────────────────────────────
app.Map("/ws/{groupCode}", async (HttpContext ctx, string groupCode) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("WebSocket connections only.");
        return;
    }

    using var ws       = await ctx.WebSockets.AcceptWebSocketAsync();
    var       roomLock = roomLocks.GetOrAdd(groupCode, _ => new object());

    lock (roomLock)
        rooms.GetOrAdd(groupCode, _ => []).Add(ws);

    Console.WriteLine($"[+] room={groupCode}  peers={rooms[groupCode].Count}");

    var buf = new byte[512 * 1024]; // 512 KB — large enough for any C+ profile JSON

    try
    {
        while (ws.State == WebSocketState.Open)
        {
            // Accumulate a (potentially fragmented) WebSocket message
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buf, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) goto done;
                ms.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);

            var data = ms.ToArray();

            // Copy peer list while holding the lock, then broadcast outside the lock
            List<WebSocket> peers;
            lock (roomLock)
            {
                if (!rooms.TryGetValue(groupCode, out var room)) break;
                peers = room.Where(p => p != ws && p.State == WebSocketState.Open).ToList();
            }

            List<WebSocket> dead = [];
            foreach (var peer in peers)
            {
                try   { await peer.SendAsync(data, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None); }
                catch { dead.Add(peer); }
            }

            if (dead.Count > 0)
                lock (roomLock)
                {
                    if (rooms.TryGetValue(groupCode, out var room))
                        foreach (var d in dead) room.Remove(d);
                }
        }
        done:;
    }
    catch { /* abrupt disconnect — handled in finally */ }
    finally
    {
        lock (roomLock)
        {
            if (rooms.TryGetValue(groupCode, out var room)) room.Remove(ws);
        }
        Console.WriteLine($"[-] room={groupCode}  peers={rooms.GetValueOrDefault(groupCode)?.Count ?? 0}");
    }
});

// ── Start ─────────────────────────────────────────────────────────────────────
// Port resolution order: $PORT env var (Railway/Render/Fly) → CLI arg → 8080 default
var port = Environment.GetEnvironmentVariable("PORT") is string ep && int.TryParse(ep, out var envPort)
    ? envPort
    : args.FirstOrDefault() is string a && int.TryParse(a, out var n) ? n : 8080;
app.Run($"http://0.0.0.0:{port}");
