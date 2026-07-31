using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PortalDoCorretor.SharedKernel.Domain;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Persistence;

/// <summary>Linha da Outbox. Persistida na MESMA transação que altera o estado.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt { get; init; }
    public Guid TenantId { get; init; }
    public string MessageType { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public Guid CorrelationId { get; init; }
    public string AggregateType { get; init; } = string.Empty;
    public Guid AggregateId { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public short Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
}

/// <summary>
/// Drena os eventos dos agregados rastreados e grava as mensagens de Outbox <b>antes</b> do
/// commit, dentro da mesma transação.
/// </summary>
/// <remarks>
/// <para>
/// É a solução para o problema de <i>dual write</i>: escrever no banco e publicar em um broker
/// são operações que não compartilham transação. Se o commit passa e a publicação falha, o
/// evento some; se publica antes e o commit falha, o evento é uma mentira.
/// </para>
/// <para>
/// Com a Outbox, ambos são a mesma escrita. Um worker separado lê com
/// <c>FOR UPDATE SKIP LOCKED</c> e publica depois. A entrega é <b>ao menos uma vez</b> —
/// exatamente-uma-vez é inalcançável sem coordenação distribuída, então o consumidor precisa
/// ser idempotente por construção.
/// </para>
/// </remarks>
public sealed class OutboxInterceptor(IClock clock) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) DrainDomainEvents(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) DrainDomainEvents(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void DrainDomainEvents(DbContext context)
    {
        var entries = context.ChangeTracker
            .Entries()
            .Where(entry => entry.Entity is IHasDomainEvents { DomainEvents.Count: > 0 })
            .ToArray();

        if (entries.Length == 0) return;

        var now = clock.UtcNow;

        foreach (var entry in entries)
        {
            var root = (IHasDomainEvents)entry.Entity;

            // A identidade vem da chave primária rastreada pelo ORM — evita exigir que
            // todo agregado exponha o Id como Guid, o que anularia os identificadores tipados
            var aggregateId = ExtractAggregateId(entry);

            foreach (var domainEvent in root.DomainEvents)
            {
                context.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    OccurredAt = domainEvent.OccurredAt,
                    TenantId = domainEvent.TenantId.Value,
                    MessageType = domainEvent.GetType().FullName ?? domainEvent.GetType().Name,
                    Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), SerializerOptions),
                    CorrelationId = domainEvent.EventId,
                    AggregateType = entry.Entity.GetType().Name,
                    AggregateId = aggregateId,
                    NextAttemptAt = now
                });
            }

            // Limpa após enfileirar: um SaveChanges subsequente não reemite o mesmo evento
            root.ClearDomainEvents();
        }
    }

    private static Guid ExtractAggregateId(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey()?.Properties.FirstOrDefault();
        if (key is null) return Guid.Empty;

        var value = entry.Property(key.Name).CurrentValue;
        return value switch
        {
            Guid guid => guid,
            null => Guid.Empty,
            _ => value.GetType().GetProperty("Value")?.GetValue(value) as Guid? ?? Guid.Empty
        };
    }
}
