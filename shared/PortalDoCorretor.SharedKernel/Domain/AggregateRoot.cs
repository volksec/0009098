using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.SharedKernel.Domain;

/// <summary>Entidade: identidade estável, igualdade por identidade (não por valor).</summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : struct, IEquatable<TId>
{
    public TId Id { get; protected set; }

    protected Entity(TId id) => Id = id;

    /// <summary>Construtor para o ORM materializar a entidade sem passar pelo factory.</summary>
    protected Entity() { }

    public bool Equals(Entity<TId>? other) =>
        other is not null && GetType() == other.GetType() && Id.Equals(other.Id);

    public override bool Equals(object? obj) => obj is Entity<TId> e && Equals(e);
    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}

/// <summary>Marcador de raiz de agregado — usado pelos testes arquiteturais e pelos repositórios.</summary>
public interface IAggregateRoot;

/// <summary>
/// Permite que a infraestrutura drene os eventos acumulados sem conhecer o tipo concreto
/// do agregado. Fica no SharedKernel, e não na camada de persistência, para que a regra de
/// dependência continue apontando para dentro.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}

/// <summary>Entidade pertencente a um tenant. Base do filtro global e da RLS.</summary>
public interface ITenantScoped
{
    TenantId TenantId { get; }
}

/// <summary>
/// Exclusão lógica como contrato do sistema, não como campo avulso (RF-131).
/// O motivo é obrigatório para que a auditoria responda "por que isso foi apagado".
/// </summary>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; }
    Guid? DeletedBy { get; }
    string? DeletionReason { get; }

    /// <summary>Correlaciona exclusões em cascata para permitir restauração do mesmo lote (RF-132).</summary>
    Guid? DeletionBatchId { get; }

    bool IsDeleted => DeletedAt is not null;
}

/// <summary>Rastro de criação e alteração, preenchido por interceptor do ORM.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; }
    Guid CreatedBy { get; }
    DateTimeOffset? UpdatedAt { get; }
    Guid? UpdatedBy { get; }
}

/// <summary>Evento de domínio: fato consumado, no passado, com contexto de tenant.</summary>
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
    TenantId TenantId { get; }
}

/// <summary>Base de evento de domínio com identidade ordenável no tempo (UUID v7).</summary>
public abstract record DomainEvent(TenantId TenantId) : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Raiz de agregado: fronteira de consistência transacional.
/// Acumula eventos em memória; o interceptor do ORM os drena antes do commit e grava
/// as mensagens de Outbox na MESMA transação, eliminando o problema de dual write.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot, IHasDomainEvents
    where TId : struct, IEquatable<TId>
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id) : base(id) { }
    protected AggregateRoot() { }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>Abstração de tempo: mantém o domínio determinístico e testável sem relógio real.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateOnly Today { get; }
}

/// <summary>Relógio de produção.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
}
