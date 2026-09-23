using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

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
                // Còn dồn nhiều thì xả liên tục, không chờ tick sau
                while (await PublishBatchAsync(ct) == BatchSize) { }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Outbox {Context}: publish thất bại, sẽ thử lại", typeof(TDbContext).Name);
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

        // SKIP LOCKED: nhiều instance chạy song song, mỗi instance lấy một lô khác nhau, không ai chờ ai
        var batch = new List<(Guid Id, string Type, string Payload)>();
        await using (var select = new NpgsqlCommand("""
            SELECT id, type, payload::text FROM outbox_messages
            WHERE processed_at IS NULL
            ORDER BY occurred_at
            LIMIT @limit
            FOR UPDATE SKIP LOCKED
            """, conn, npgsqlTx))
        {
            select.Parameters.AddWithValue("limit", BatchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                batch.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }

        if (batch.Count == 0) return 0;

        var published = new List<Guid>();
        foreach (var m in batch)
        {
            if (!registry.TryResolve(m.Type, out var type))
            {
                log.LogError("Outbox: không nhận ra message type {Type} (id {Id}), bỏ qua", m.Type, m.Id);
                continue;
            }

            var message = JsonSerializer.Deserialize(m.Payload, type)!;

            // MessageId = id của dòng outbox. Nếu publish xong mà chết trước khi commit,
            // lần sau message được gửi lại với CÙNG MessageId => inbox bên nhận nhận ra và bỏ qua
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