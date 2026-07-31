using PortalDoCorretor.SharedKernel.Domain;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Persistence;

/// <summary>Contexto da requisição corrente. Somente leitura para quem consome.</summary>
public interface ITenantContext
{
    TenantId Current { get; }
    UserId ActorId { get; }
    UserProfile Profile { get; }
    CorrelationId CorrelationId { get; }
    bool IsResolved { get; }
}

public enum UserProfile { Broker, Regulator }

/// <summary>
/// Camada 2 da defesa em profundidade: o tenant é fixado <b>uma única vez</b> por requisição
/// e não pode ser alterado depois.
/// </summary>
/// <remarks>
/// <para>
/// A resolução acontece a partir do claim do token assinado, no middleware, antes de qualquer
/// código de negócio rodar. <see cref="Resolve"/> lança se chamado duas vezes — não existe
/// caminho para "trocar de tenant no meio da requisição", que é exatamente o que um atacante
/// tentaria depois de passar pela autenticação.
/// </para>
/// <para>
/// O serviço é registrado com tempo de vida <c>Scoped</c>: uma instância por requisição, sem
/// vazamento entre requisições que compartilham o pool de conexões.
/// </para>
/// </remarks>
public sealed class TenantContext : ITenantContext
{
    private TenantId? _tenantId;
    private UserId? _actorId;
    private UserProfile? _profile;
    private CorrelationId? _correlationId;

    public bool IsResolved => _tenantId is not null || _profile is UserProfile.Regulator;

    public TenantId Current => _tenantId
        ?? throw new DomainException(PersistenceErrors.TenantNotResolved,
               "Contexto de tenant não resolvido para esta requisição.");

    public UserId ActorId => _actorId
        ?? throw new DomainException(PersistenceErrors.ActorNotResolved,
               "Ator não resolvido para esta requisição.");

    public UserProfile Profile => _profile
        ?? throw new DomainException(PersistenceErrors.ProfileNotResolved,
               "Perfil não resolvido para esta requisição.");

    public CorrelationId CorrelationId => _correlationId ??= SharedKernel.ValueObjects.CorrelationId.New();

    /// <summary>
    /// Fixa o contexto a partir de origem confiável. Chamado exclusivamente pelo middleware
    /// de autenticação, com valores extraídos do token já validado.
    /// </summary>
    public void Resolve(Guid tenantId, Guid actorId, UserProfile profile, CorrelationId correlationId)
    {
        if (_actorId is not null)
            throw new DomainException(PersistenceErrors.TenantAlreadyResolved,
                "Contexto já resolvido — não pode ser alterado durante a requisição.");

        // O perfil de supervisão é multi-tenant por escopo, então não tem tenant fixo
        _tenantId = profile is UserProfile.Broker
            ? TenantId.FromTrustedSource(tenantId)
            : null;

        _actorId = new UserId(actorId);
        _profile = profile;
        _correlationId = correlationId;
    }
}

internal static class PersistenceErrors
{
    public const string TenantNotResolved = "TENANT_NOT_RESOLVED";
    public const string TenantAlreadyResolved = "TENANT_ALREADY_RESOLVED";
    public const string ActorNotResolved = "ACTOR_NOT_RESOLVED";
    public const string ProfileNotResolved = "PROFILE_NOT_RESOLVED";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string AuditMissing = "AUDIT_MISSING";
}
