using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Diagnostics;

namespace TicketNow.BuildingBlocks.Messaging;

public sealed class OutboxPublisher<TDbContext>(
    IServiceScopeFactory scopes,
    IBus bus,
    OutboxTypeRegistry registry,
    ILogger<OutboxPublisher<TDbContext>> log) : BackgroundService
    where TDbContext : DbContext
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                // Keep draining while full batches come back instead of waiting for the next tick
                while (await PublishBatchAsync(ct) == BatchSize) { }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Outbox {Context}: publish failed, will retry", typeof(TDbContext).Name);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var npgsqlTx = (NpgsqlTransaction)tx.GetDbTransaction();

        // SKIP LOCKED: parallel instances each grab a different batch without blocking each other
        var batch = new List<(Guid Id, string Type, string Payload, string? TraceParent)>();
        await using (var select = new NpgsqlCommand("""
            SELECT id, type, payload::text, trace_parent FROM outbox_messages
            WHERE processed_at IS NULL
            ORDER BY occurred_at
            LIMIT @limit
            FOR UPDATE SKIP LOCKED
            """, conn, npgsqlTx))
        {
            select.Parameters.AddWithValue("limit", BatchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                batch.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                           reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        if (batch.Count == 0) return 0;

        var published = new List<Guid>();
        foreach (var m in batch)
        {
            if (!registry.TryResolve(m.Type, out var type))
            {
                log.LogError("Outbox: unknown message type {Type} (id {Id}), skipping", m.Type, m.Id);
                continue;
            }

            var message = JsonSerializer.Deserialize(m.Payload, type)!;

            using var activity = OutboxActivity.Source.StartActivity(
                $"outbox publish {m.Type}", ActivityKind.Producer, m.TraceParent);
            activity?.SetTag("outbox.message_id", m.Id);

            await bus.Publish(message, type, Pipe.Execute<PublishContext>(ctx => ctx.MessageId = m.Id), ct);
            published.Add(m.Id);
        }

        if (published.Count > 0)
        {
            await using var update = new NpgsqlCommand(
                "UPDATE outbox_messages SET processed_at = now() WHERE id = ANY(@ids)", conn, npgsqlTx);
            update.Parameters.AddWithValue("ids", published.ToArray());
            await update.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return published.Count;
    }
}