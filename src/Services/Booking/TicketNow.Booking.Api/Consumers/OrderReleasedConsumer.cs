using MassTransit;
using TicketNow.Booking.Infrastructure.Redis;
using TicketNow.Contracts.Booking;

namespace TicketNow.Booking.Api.Consumers;

public sealed class OrderReleasedConsumer(InventoryStore inventory, ILogger<OrderReleasedConsumer> log)
    : IConsumer<OrderReleased>
{
    public async Task Consume(ConsumeContext<OrderReleased> context)
    {
        var m = context.Message;
        var released = await inventory.ReleaseOnceAsync(
            m.OrderId, m.UserId, m.Lines.Select(l => (l.TicketTypeId, l.Quantity)));

        if (!released)
            log.LogInformation("Order {OrderId} tickets were already released, skipping duplicate message", m.OrderId);
    }
}