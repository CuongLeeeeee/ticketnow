using TicketNow.Booking.Infrastructure.Redis;

namespace TicketNow.Booking.Api.BackgroundServices;

public sealed class WaitingRoomAdmitter(WaitingRoomStore store, ILogger<WaitingRoomAdmitter> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                foreach (var eventId in await store.GetEnabledEventsAsync())
                {
                    var cfg = await store.GetConfigAsync(eventId);
                    if (cfg is not { Enabled: true }) continue;

                    var admitted = await store.AdmitAsync(eventId, cfg.AdmitPerTick, cfg.AdmitTtlSeconds);
                    if (admitted > 0)
                        log.LogInformation("Waiting room {EventId}: admitted {Count} users", eventId, admitted);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Waiting room admitter failed");
            }
        }
    }
}