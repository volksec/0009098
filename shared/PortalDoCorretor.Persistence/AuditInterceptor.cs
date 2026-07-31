using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PortalDoCorretor.SharedKernel.Domain;

namespace PortalDoCorretor.Persistence;

/// <summary>Registro imutável de auditoria.</summary>
public sealed class AuditEvent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt { get; init; }
    public Guid? TenantId { get; init; }
    public Guid CorrelationId { get; init; }
    public string? TraceId { get; init; }
    public Guid ActorId { get; init; }
    public string ActorProfile { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public Guid? ResourceId { get; init; }
    public string Outcome { get; init; } = "SUCCESS";
    public int? DurationMs { get; set; }
    public string? BeforeState { get; init; }
    public string? AfterState { get; init; }
}

/// <summary>
/// Gera <see cref="AuditEvent"/> para toda escrita, na <b>mesma transação</b> da operação.
/// </summary>
/// <remarks>
/// <para>
/// A consequência é deliberada: se a auditoria falhar, a operação de negócio falha junto.
/// Em contexto regulado, "operação confirmada sem auditoria" é um estado pior do que
/// "operação recusada" — o primeiro é invisível, o segundo o usuário percebe e repete.
/// </para>
/// <para>
/// Campos sensíveis são <b>excluídos</b> do estado gravado. Auditoria que copia o documento
/// do cliente para uma coluna JSON transforma a trilha, que é lida por perfil de supervisão,
/// em uma segunda cópia dos dados pessoais.
/// </para>
/// </remarks>
public sealed class AuditInterceptor(ITenantContext tenantContext, IClock clock) : SaveChangesInterceptor
{
    /// <summary>Nunca copiados para a trilha, mesmo que a entidade os exponha.</summary>
    private static readonly HashSet<string> SensitiveProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Document", "DocumentEncrypted", "DocumentHash", "PasswordHash", "TotpSecret",
        "RefreshTokenHash", "Email", "Phone", "SearchHash", "ContentHash"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) WriteAuditEvents(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) WriteAuditEvents(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void WriteAuditEvents(DbContext context)
    {
        var auditable = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => e.Entity is IAggregateRoot)
            .ToArray();

        if (auditable.Length == 0) return;

        var now = clock.UtcNow;

        foreach (var entry in auditable)
        {
            var action = entry.State switch
            {
                EntityState.Added => "CREATED",
                EntityState.Modified => IsSoftDelete(entry) ? "SOFT_DELETED" : "UPDATED",
                EntityState.Deleted => "DELETED",
                _ => "UNKNOWN"
            };

            context.Set<AuditEvent>().Add(new AuditEvent
            {
                OccurredAt = now,
                TenantId = tenantContext.IsResolved && tenantContext.Profile == UserProfile.Broker
                    ? tenantContext.Current.Value
                    : null,
                CorrelationId = tenantContext.CorrelationId.Value,
                TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
                ActorId = tenantContext.ActorId.Value,
                ActorProfile = tenantContext.Profile.ToString().ToUpperInvariant(),
                Action = $"{entry.Entity.GetType().Name.ToUpperInvariant()}_{action}",
                ResourceType = entry.Entity.GetType().Name,
                ResourceId = ExtractId(entry),
                BeforeState = entry.State is EntityState.Added ? null : Snapshot(entry, original: true),
                AfterState = entry.State is EntityState.Deleted ? null : Snapshot(entry, original: false)
            });
        }
    }

    /// <summary>Exclusão lógica é um UPDATE, mas semanticamente é remoção — a trilha reflete isso.</summary>
    private static bool IsSoftDelete(EntityEntry entry) =>
        entry.Entity is ISoftDeletable
        && entry.Properties.Any(p => p.Metadata.Name == nameof(ISoftDeletable.DeletedAt)
                                  && p.IsModified && p.CurrentValue is not null);

    private static string? Snapshot(EntityEntry entry, bool original)
    {
        var values = new Dictionary<string, object?>();

        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;
            if (SensitiveProperties.Contains(name)) continue;

            // Apenas o que mudou entra na trilha — copiar a linha inteira infla a auditoria
            // e dificulta ler o que de fato aconteceu
            if (entry.State is EntityState.Modified && !property.IsModified) continue;

            values[name] = original ? property.OriginalValue : property.CurrentValue;
        }

        return values.Count == 0 ? null : JsonSerializer.Serialize(values, SerializerOptions);
    }

    private static Guid? ExtractId(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey()?.Properties.FirstOrDefault();
        if (key is null) return null;

        var value = entry.Property(key.Name).CurrentValue;
        return value switch
        {
            Guid guid => guid,
            null => null,
            _ => value.GetType().GetProperty("Value")?.GetValue(value) as Guid?
        };
    }
}
