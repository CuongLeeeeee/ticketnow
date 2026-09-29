using System.Security.Claims;
using TicketNow.Booking.Infrastructure.Redis;

namespace TicketNow.Booking.Api.Endpoints;

public static class WaitingRoomEndpoints
{
    public static IEndpointRouteBuilder MapWaitingRoomEndpoints(this IEndpointRouteBuilder app)
    {
        var waitingRoom = app.MapGroup("/api/waiting-room").RequireAuthorization("authenticated");
        waitingRoom.MapPost("/{eventId:guid}/join", Join);
        waitingRoom.MapGet("/{eventId:guid}/status", GetStatus);

        var admin = app.MapGroup("/api/admin/waiting-room").RequireAuthorization("admin");
        admin.MapPost("/{eventId:guid}", Configure);

        return app;
    }

    private static async Task<IResult> Join(Guid eventId, ClaimsPrincipal principal, WaitingRoomStore store)
    {
        var cfg = await store.GetConfigAsync(eventId);
        if (cfg is not { Enabled: true })
            return Results.Ok(new { state = "Disabled", position = 0, queueLength = 0 });

        var s = await store.JoinAsync(eventId, principal.GetUserId());
        return Results.Ok(new { state = s.State.ToString(), position = s.Position, queueLength = s.QueueLength });
    }

    private static async Task<IResult> GetStatus(Guid eventId, ClaimsPrincipal principal, WaitingRoomStore store)
    {
        var cfg = await store.GetConfigAsync(eventId);
        if (cfg is not { Enabled: true })
            return Results.Ok(new { state = "Disabled", position = 0, queueLength = 0, etaSeconds = 0 });

        var s = await store.StatusAsync(eventId, principal.GetUserId());
        var eta = s.State == WaitingRoomState.Waiting && cfg.AdmitPerTick > 0
            ? (long)Math.Ceiling((double)s.Position / cfg.AdmitPerTick)
            : 0;

        return Results.Ok(new { state = s.State.ToString(), position = s.Position, queueLength = s.QueueLength, etaSeconds = eta });
    }

    private static async Task<IResult> Configure(Guid eventId, WaitingRoomSettings req, WaitingRoomStore store)
    {
        if (req.Enabled) await store.EnableAsync(eventId, Math.Max(1, req.AdmitPerTick), Math.Max(30, req.AdmitTtlSeconds));
        else await store.DisableAsync(eventId);
        return Results.Ok(await store.GetConfigAsync(eventId));
    }
}
