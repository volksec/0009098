using PortalDoCorretor.SharedKernel.Domain;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Proposals.Domain;

/// <summary>Pendência documental ou informacional que bloqueia a aprovação.</summary>
public sealed class Pendency : Entity<PendencyId>
{
    public ProposalId ProposalId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public UserId? ResolvedBy { get; private set; }

    private Pendency() { }

    private Pendency(PendencyId id, ProposalId proposalId, string code,
                     string description, DateTimeOffset openedAt) : base(id)
    {
        ProposalId = proposalId;
        Code = code;
        Description = description;
        OpenedAt = openedAt;
    }

    internal static Pendency Open(ProposalId proposalId, string? code,
                                  string? description, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new DomainException(ProposalErrors.DuplicatePendency,
                "Código da pendência é obrigatório.");

        if (string.IsNullOrWhiteSpace(description))
            throw new DomainException(ProposalErrors.DuplicatePendency,
                "Descrição da pendência é obrigatória.");

        return new Pendency(PendencyId.New(), proposalId, code.Trim().ToUpperInvariant(),
                            description.Trim(), clock.UtcNow);
    }

    internal void Resolve(UserId resolvedBy, IClock clock)
    {
        if (IsResolved) return;   // idempotente
        ResolvedAt = clock.UtcNow;
        ResolvedBy = resolvedBy;
    }

    public bool IsResolved => ResolvedAt is not null;
}

/// <summary>
/// Decisão de aceitação. <b>Imutável e versionada</b> — nenhum método altera o estado após a
/// construção. Reanálise cria a versão seguinte, preservando o registro do que foi decidido antes.
/// </summary>
public sealed class UnderwritingDecision : Entity<Guid>
{
    private readonly List<string> _reasons = [];
    private readonly Dictionary<string, bool> _evaluatedRules = [];

    public ProposalId ProposalId { get; private set; }
    public int Version { get; private set; }
    public UnderwritingOutcome Outcome { get; private set; }
    public DateTimeOffset DecidedAt { get; private set; }
    public UserId DecidedBy { get; private set; }
    public CorrelationId CorrelationId { get; private set; }

    public IReadOnlyList<string> Reasons => _reasons.AsReadOnly();
    public IReadOnlyDictionary<string, bool> EvaluatedRules => _evaluatedRules;

    private UnderwritingDecision() { }

    private UnderwritingDecision(Guid id, ProposalId proposalId, int version,
                                 UnderwritingOutcome outcome, IEnumerable<string> reasons,
                                 IReadOnlyDictionary<string, bool> evaluatedRules,
                                 UserId decidedBy, CorrelationId correlationId, IClock clock)
        : base(id)
    {
        ProposalId = proposalId;
        Version = version;
        Outcome = outcome;
        DecidedBy = decidedBy;
        CorrelationId = correlationId;
        DecidedAt = clock.UtcNow;
        _reasons.AddRange(reasons);
        foreach (var (rule, passed) in evaluatedRules) _evaluatedRules[rule] = passed;
    }

    public static UnderwritingDecision Create(
        ProposalId proposalId, int version, UnderwritingOutcome outcome,
        IEnumerable<string> reasons, IReadOnlyDictionary<string, bool> evaluatedRules,
        UserId decidedBy, CorrelationId correlationId, IClock clock)
    {
        var reasonList = reasons?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? [];

        // Recusa e pendência SEM motivo são inauditáveis: quem receber a decisão não
        // consegue saber o que precisa mudar, e o supervisor não consegue avaliá-la.
        if (outcome is not UnderwritingOutcome.Approved && reasonList.Count == 0)
            throw new DomainException(ProposalErrors.DecisionReasonsRequired,
                "Decisão desfavorável exige ao menos um motivo.");

        if (version < 1)
            throw new DomainException(ProposalErrors.DecisionReasonsRequired,
                "Versão da decisão deve ser positiva.");

        return new UnderwritingDecision(Guid.CreateVersion7(), proposalId, version, outcome,
                                        reasonList, evaluatedRules, decidedBy, correlationId, clock);
    }

    public bool IsFavorable => Outcome is UnderwritingOutcome.Approved;

    public string PrimaryReason => _reasons.FirstOrDefault() ?? Outcome.ToString();
}

/// <summary>Registro append-only de mudança de status.</summary>
public sealed class ProposalStatusChange : Entity<Guid>
{
    public ProposalId ProposalId { get; private set; }
    public ProposalStatus? FromStatus { get; private set; }
    public ProposalStatus ToStatus { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public UserId ChangedBy { get; private set; }
    public DateTimeOffset ChangedAt { get; private set; }

    private ProposalStatusChange() { }

    private ProposalStatusChange(Guid id, ProposalId proposalId, ProposalStatus? from,
                                 ProposalStatus to, string reason, UserId changedBy,
                                 DateTimeOffset changedAt) : base(id)
    {
        ProposalId = proposalId;
        FromStatus = from;
        ToStatus = to;
        Reason = reason;
        ChangedBy = changedBy;
        ChangedAt = changedAt;
    }

    internal static ProposalStatusChange Record(ProposalId proposalId, ProposalStatus from,
                                                ProposalStatus to, string reason,
                                                UserId changedBy, IClock clock) =>
        new(Guid.CreateVersion7(), proposalId, from, to, reason, changedBy, clock.UtcNow);
}

// ---------------------------------------------------------------- eventos

public sealed record ProposalCreated(
    TenantId TenantId, ProposalId ProposalId, QuotationId QuotationId, ProposalNumber Number)
    : DomainEvent(TenantId);

public sealed record ProposalSubmitted(
    TenantId TenantId, ProposalId ProposalId, ProposalNumber Number) : DomainEvent(TenantId);

public sealed record ProposalApproved(
    TenantId TenantId, ProposalId ProposalId, ProposalNumber Number) : DomainEvent(TenantId);

public sealed record ProposalRejected(
    TenantId TenantId, ProposalId ProposalId, IReadOnlyList<string> Reasons) : DomainEvent(TenantId);

public sealed record ProposalPending(
    TenantId TenantId, ProposalId ProposalId, IReadOnlyList<string> Reasons) : DomainEvent(TenantId);

public sealed record ProposalIssued(
    TenantId TenantId, ProposalId ProposalId, PolicyId PolicyId) : DomainEvent(TenantId);

public sealed record ProposalExpired(TenantId TenantId, ProposalId ProposalId)
    : DomainEvent(TenantId);

public sealed record PendencyOpened(TenantId TenantId, ProposalId ProposalId, string Code)
    : DomainEvent(TenantId);

public sealed record PendencyResolved(TenantId TenantId, ProposalId ProposalId, string Code)
    : DomainEvent(TenantId);
