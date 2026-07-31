using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PortalDoCorretor.SharedKernel.Domain;

namespace PortalDoCorretor.Persistence;

/// <summary>
/// Contexto base. Concentra as camadas 3 (filtro global) e 5 (RLS) da defesa em profundidade.
/// </summary>
public abstract class PortalDbContext(DbContextOptions options, ITenantContext tenantContext)
    : DbContext(options)
{
    protected ITenantContext TenantContext { get; } = tenantContext;

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureInfrastructureTables(modelBuilder);
        ApplyGlobalFilters(modelBuilder);
    }

    /// <summary>
    /// Camada 3: filtro global por tenant e por exclusão lógica, aplicado a <b>toda</b>
    /// entidade que implementa os marcadores.
    /// </summary>
    /// <remarks>
    /// A aplicação é automática e por convenção, e não entidade a entidade. É o que impede
    /// o modo de falha mais comum: alguém adiciona uma entidade nova e esquece o filtro,
    /// abrindo um buraco de isolamento que só aparece em produção.
    /// </remarks>
    private void ApplyGlobalFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            var parameter = Expression.Parameter(clrType, "e");
            Expression? filter = null;

            if (typeof(ITenantScoped).IsAssignableFrom(clrType))
            {
                // e.TenantId == tenantContext.Current
                var tenantProperty = Expression.Property(parameter, nameof(ITenantScoped.TenantId));
                var currentTenant = Expression.Property(
                    Expression.Property(Expression.Constant(this), nameof(TenantContext)),
                    nameof(ITenantContext.Current));

                filter = Expression.Equal(tenantProperty, currentTenant);
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                // e.DeletedAt == null
                var deletedProperty = Expression.Property(parameter, nameof(ISoftDeletable.DeletedAt));
                var notDeleted = Expression.Equal(
                    deletedProperty, Expression.Constant(null, typeof(DateTimeOffset?)));

                filter = filter is null ? notDeleted : Expression.AndAlso(filter, notDeleted);
            }

            if (filter is not null)
                modelBuilder.Entity(clrType).HasQueryFilter(Expression.Lambda(filter, parameter));
        }
    }

    private static void ConfigureInfrastructureTables(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable("outbox_messages");
            builder.HasKey(m => new { m.Id, m.OccurredAt });
            builder.Property(m => m.Payload).HasColumnType("jsonb");
            builder.Property(m => m.MessageType).HasMaxLength(120);
            builder.Property(m => m.AggregateType).HasMaxLength(60);
        });

        modelBuilder.Entity<AuditEvent>(builder =>
        {
            builder.ToTable("audit_events");
            builder.HasKey(a => new { a.Id, a.OccurredAt });
            builder.Property(a => a.BeforeState).HasColumnType("jsonb");
            builder.Property(a => a.AfterState).HasColumnType("jsonb");
            builder.Property(a => a.Action).HasMaxLength(60);
            builder.Property(a => a.ResourceType).HasMaxLength(60);
        });
    }

    /// <summary>
    /// Camada 5: define o contexto de tenant na conexão para que a Row-Level Security atue.
    /// </summary>
    /// <remarks>
    /// Usa <c>SET LOCAL</c>, e não <c>SET</c>: o valor morre no fim da transação, então uma
    /// conexão devolvida ao pool nunca carrega o tenant da requisição anterior. Com <c>SET</c>
    /// simples, a próxima requisição a pegar aquela conexão herdaria o contexto — um vazamento
    /// entre tenants silencioso e difícil de reproduzir.
    /// </remarks>
    public async Task ApplyDatabaseContextAsync(CancellationToken cancellationToken = default)
    {
        if (Database.GetDbConnection() is not NpgsqlConnection connection) return;

        if (connection.State is not System.Data.ConnectionState.Open)
            await Database.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT set_config('app.tenant_id',      @tenant,      true),
                   set_config('app.actor_id',       @actor,       true),
                   set_config('app.user_profile',   @profile,     true),
                   set_config('app.correlation_id', @correlation, true)
            """;

        // set_config com is_local = true equivale a SET LOCAL, e aceita parâmetro —
        // ao contrário de SET LOCAL, que exigiria interpolar a string e reabriria SQL injection
        command.Parameters.AddWithValue("tenant",
            TenantContext.Profile is UserProfile.Broker ? TenantContext.Current.ToString() : string.Empty);
        command.Parameters.AddWithValue("actor", TenantContext.ActorId.ToString());
        command.Parameters.AddWithValue("profile", TenantContext.Profile.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("correlation", TenantContext.CorrelationId.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
