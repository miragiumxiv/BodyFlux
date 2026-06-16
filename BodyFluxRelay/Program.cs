// BodyFluxRelay — minimal WebSocket room relay for the BodyFlux Dalamud plugin.
//
// Every connection joins a "room" identified by the group code in the URL path. All messages from
// one client are forwarded verbatim to every other client in the same room. No payloads are stored
// or interpreted (beyond a hashed "sender" id for the log); the relay is pure pass-through.
//
// Usage:
//   dotnet run [port]          (default port: 8080)
//
// Deploy anywhere that runs .NET 9 (Railway, Render, Fly.io free tier, VPS, etc.).
// Point the BodyFlux "Relay URL" setting to  ws://<host>:<port>

using BodyFluxRelay.Core;

var options = RelayOptions.FromEnvironment(args);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

using var log   = new RelayLog(options);
var rooms       = new RoomRegistry();
var connections = new RelayConnection(rooms, log);

// ── Health check ────────────────────────────────────────────────────────────────
app.MapGet("/", () => $"BodyFluxRelay OK — {rooms.TotalConnections} connection(s)");

// ── WebSocket endpoint ────────────────────────────────────────────────────────────
app.Map("/ws/{groupCode}", async (HttpContext ctx, string groupCode) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("WebSocket connections only.");
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await connections.HandleAsync(ws, groupCode);
});

app.Run($"http://0.0.0.0:{options.Port}");
