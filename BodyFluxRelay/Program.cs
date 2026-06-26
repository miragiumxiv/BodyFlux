// BodyFluxRelay — WebSocket room relay for the BodyFlux Dalamud plugin.
//
// Usage:
//   dotnet run [port]          (default port: 8080)
//
// Required environment variables:
//   BODYFLUX_SERVER_SECRET    Long random secret. MUST stay fixed — changing it invalidates all
//                             stored user global ids (bans and quotas would stop working).
//
// Optional environment variables: see RelayOptions for the full list.

using System.Collections.Concurrent;
using System.Text.Json;
using BodyFluxRelay.Core;

var options = RelayOptions.FromEnvironment(args);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

using var log          = new RelayLog(options);
await using var rooms  = new RoomStateStore(log);
await using var users  = new UserStore(options.DbPath, options.ServerSecret, options.DailyQuota);
await users.InitAsync();

log.Write($"[start] port={options.Port}  quota={options.DailyQuota}  max_peers={options.MaxPeersPerRoom}  max_conn={options.MaxConnections}  max_conn_ip={options.MaxConnectionsPerIp}  maintenance={options.MaintenanceMode}  admin={options.AdminKey is not null}");

var connections    = new RelayConnection(rooms, log, users, options);
var ipConnections  = new ConcurrentDictionary<string, int>();

// Resolves the real client IP, accounting for Fly.io's reverse proxy headers.
static string GetClientIp(HttpContext ctx)
{
    if (ctx.Request.Headers.TryGetValue("Fly-Client-IP", out var flyIp) && flyIp.Count > 0)
        return flyIp[0]!;
    if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var xff) && xff.Count > 0)
        return xff[0]!.Split(',')[0].Trim();
    return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

// ── Health check ──────────────────────────────────────────────────────────────
app.MapGet("/", () => options.MaintenanceMode
    ? $"BodyFluxRelay MAINTENANCE — {rooms.TotalConnections} connection(s) still active, not accepting new"
    : $"BodyFluxRelay OK — {rooms.TotalConnections} connection(s) in {rooms.RoomCount} room(s)");

// ── WebSocket endpoint ────────────────────────────────────────────────────────
app.Map("/ws/{groupCode}", async (HttpContext ctx, string groupCode) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("WebSocket connections only.");
        return;
    }

    // ── Kill switch ───────────────────────────────────────────────────────────
    if (options.MaintenanceMode)
    {
        ctx.Response.StatusCode = 503;
        await ctx.Response.WriteAsync("Service temporarily unavailable — maintenance mode active.");
        return;
    }

    // ── Global connection cap ─────────────────────────────────────────────────
    if (rooms.TotalConnections >= options.MaxConnections)
    {
        log.Write($"[!] room={groupCode}  reason=global cap ({options.MaxConnections} max)");
        ctx.Response.StatusCode = 503;
        await ctx.Response.WriteAsync("Service temporarily unavailable — connection limit reached.");
        return;
    }

    // ── Per-IP connection cap ─────────────────────────────────────────────────
    var ip      = GetClientIp(ctx);
    var ipCount = ipConnections.AddOrUpdate(ip, 1, (_, n) => n + 1);
    if (ipCount > options.MaxConnectionsPerIp)
    {
        ipConnections.AddOrUpdate(ip, 0, (_, n) => Math.Max(0, n - 1));
        log.Write($"[!] room={groupCode}  ip={ip}  count={ipCount}  reason=per-IP cap ({options.MaxConnectionsPerIp} max)");
        ctx.Response.StatusCode = 429;
        await ctx.Response.WriteAsync("Too many connections from your address.");
        return;
    }

    try
    {
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await connections.HandleAsync(ctx, ws, groupCode);
    }
    finally
    {
        ipConnections.AddOrUpdate(ip, 0, (_, n) => Math.Max(0, n - 1));
    }
});

// ── Admin endpoints ───────────────────────────────────────────────────────────
// All admin routes require Authorization: Bearer <BODYFLUX_ADMIN_KEY>.
// If the key is not configured the routes return 404 so their existence is not discoverable.

bool AdminAuth(HttpContext ctx)
    => options.AdminKey != null
    && ctx.Request.Headers.Authorization.ToString() == $"Bearer {options.AdminKey}";

// POST /admin/ban  { "globalId": "...", "reason": "...", "expiresAt": "2026-07-01T00:00:00Z" }
app.MapPost("/admin/ban", async (HttpContext ctx) =>
{
    if (!AdminAuth(ctx)) return Results.NotFound();

    BanRequest? body;
    try   { body = await ctx.Request.ReadFromJsonAsync<BanRequest>(); }
    catch { return Results.BadRequest("Invalid JSON body."); }

    if (body?.GlobalId is not { Length: > 0 } gid)
        return Results.BadRequest("globalId is required.");

    await users.BanAsync(gid, body.Reason, body.ExpiresAt);
    log.Write($"[admin] ban  globalId={gid}  reason={body.Reason ?? "—"}");
    return Results.Ok(new { banned = gid, reason = body.Reason, expiresAt = body.ExpiresAt });
});

// DELETE /admin/ban/{globalId}
app.MapDelete("/admin/ban/{globalId}", async (HttpContext ctx, string globalId) =>
{
    if (!AdminAuth(ctx)) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(globalId)) return Results.BadRequest("globalId is required.");

    await users.UnbanAsync(globalId);
    log.Write($"[admin] unban  globalId={globalId}");
    return Results.Ok(new { unbanned = globalId });
});

// GET /admin/stats
app.MapGet("/admin/stats", async (HttpContext ctx) =>
{
    if (!AdminAuth(ctx)) return Results.NotFound();

    var top = await users.GetTopQuotaUsersAsync(20);
    return Results.Ok(new
    {
        maintenance    = options.MaintenanceMode,
        connections    = rooms.TotalConnections,
        maxConnections = options.MaxConnections,
        rooms          = rooms.RoomCount,
        topUsers       = top,
    });
});

app.Run($"http://0.0.0.0:{options.Port}");

// ── DTOs ──────────────────────────────────────────────────────────────────────
record BanRequest(string? GlobalId, string? Reason, DateTime? ExpiresAt);
