using System.Diagnostics;

namespace TicketNow.BuildingBlocks.Messaging;

public static class OutboxActivity
{
    public const string SourceName = "TicketNow.Outbox";
    public static readonly ActivitySource Source = new(SourceName);
}