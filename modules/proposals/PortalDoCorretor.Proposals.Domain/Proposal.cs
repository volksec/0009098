using System.Diagnostics;
using PortalDoCorretor.SharedKernel.Domain;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Proposals.Domain;

public enum ProposalStatus
{
    Draft, Submitted, UnderAnalysis, Pending, Approved, Rejected, Issued, Expired
}

public enum PlanTier { Essential, Complete, Master }

public enum UnderwritingOutcome { Approved, Rejected, Pending }

/// <summary>
/// Raiz do agregado Proposal. Concentra a máquina de estados do processo de aceitação.
/// </summary>
/// <remarks>
/// As transições válidas são declaradas <b>uma única vez</b> em <see cref="AllowedTransitions"/>
/// e verificadas em um único ponto. A alternativa — espalhar <c>if (status == ...)</c> por cada
/// método — permite que dois caminhos discordem sobre o que é válido, e é assim que uma proposta
/// recusada acaba emitida.
/// </remarks>
public sealed class Proposal : AggregateRoot<ProposalId>, ITenantScoped, ISoftDeletable
{
    private static readonly Dictionary<ProposalStatus, ProposalStatus[]> AllowedTransitions = new()
    {
        [ProposalStatus.Draft]         = [ProposalStatus.Submitted, ProposalStatus.Expired],
        [ProposalStatus.Submitted]     = [ProposalStatus.UnderAnalysis, ProposalStatus.Expired],
        [ProposalStatus.UnderAnalysis] = [ProposalStatus.Pending, ProposalStatus.Approved, ProposalStatus.Rejected],
        [ProposalStatus.Pending]       = [ProposalStatus.UnderAnalysis, ProposalStatus.Expired],
        [ProposalStatus.Approved]      = [ProposalStatus.Issued, ProposalStatus.Expired],
        [ProposalStatus.Rejected]      = [],
        [ProposalStatus.Issued]        = [],
        [ProposalStatus.Expired]       = []
    };

    private readonly List<Pendency> _pendencies = [];
    private readonly List<UnderwritingDecision> _decisions = [];
    private readonly List<ProposalStatusChange> _history = [];
    private readonly List<Guid> _documentIds = [];

    public TenantId TenantId { get; private set; }
    public QuotationId QuotationId { get; private set; }
    public BrokerId BrokerId { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public ProposalNumber Number { get; private set; }
    public ProposalStatus Status { get; private set; }
    public PlanTier ChosenPlan { get; private set; }
    public Money NetPremium { get; private set; }
    public Money TotalPremium { get; private set; }
    public int InstallmentCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public DateTimeOffset? IssuedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
    public string? DeletionReason { get; private set; }
    public Guid? DeletionBatchId { get; private set; }

    public IReadOnlyCollection<Pendency> Pendencies => _pendencies.AsReadOnly();
    public IReadOnlyCollection<UnderwritingDecision> Decisions => _decisions.AsReadOnly();
    public IReadOnlyCollection<ProposalStatusChange> History => _history.AsReadOnly();
    public IReadOnlyCollection<Guid> DocumentIds => _documentIds.AsReadOnly();

    /// <summary>Decisão vigente é a de maior versão — as anteriores permanecem consultáveis.</summary>
    public UnderwritingDecision? CurrentDecision =>
        _decisions.OrderByDescending(d => d.Version).FirstOrDefault();

    public bool HasOpenPendencies => _pendencies.Any(p => !p.IsResolved);

    private Proposal() { }

    private Proposal(ProposalId id, TenantId tenantId, QuotationId quotationId, BrokerId brokerId,
                     CustomerId customerId, ProposalNumber number, PlanTier plan,
                     Money netPremium, Money totalPremium, int installmentCount, IClock clock)
        : base(id)
    {
        TenantId = tenantId;
        QuotationId = quotationId;
        BrokerId = brokerId;
        CustomerId = customerId;
        Number = number;
        ChosenPlan = plan;
        NetPremium = netPremium;
        TotalPremium = totalPremium;
        InstallmentCount = installmentCount;
        Status = ProposalStatus.Draft;
        CreatedAt = clock.UtcNow;
    }

    public static Proposal FromQuotation(
        TenantId tenantId, QuotationId quotationId, BrokerId brokerId, CustomerId customerId,
        ProposalNumber number, PlanTier plan, Money netPremium, Money totalPremium,
        int installmentCount, IClock clock)
    {
        if (!netPremium.IsPositive)
            throw new DomainException(ProposalErrors.PremiumInvalid,
                "Prêmio líquido deve ser positivo.");

        if (totalPremium < netPremium)
            throw new DomainException(ProposalErrors.PremiumInvalid,
                "Prêmio total não pode ser menor que o líquido.");

        if (installmentCount is < 1 or > 12)
            throw new DomainException(ProposalErrors.InstallmentCountInvalid,
                "Número de parcelas deve estar entre 1 e 12.");

        var proposal = new Proposal(ProposalId.New(), tenantId, quotationId, brokerId, customerId,
                                    number, plan, netPremium, totalPremium, installmentCount, clock);

        proposal.Raise(new ProposalCreated(tenantId, proposal.Id, quotationId, number));
        return proposal;
    }

    // ---------------------------------------------------------------- transições

    public void Submit(UserId actor, IClock clock)
    {
        if (_documentIds.Count == 0)
            throw new DomainException(ProposalErrors.DocumentsRequired,
                "Proposta exige ao menos um documento anexado.");

        TransitionTo(ProposalStatus.Submitted, actor, "Submissão pelo corretor", clock);
        SubmittedAt = clock.UtcNow;
        Raise(new ProposalSubmitted(TenantId, Id, Number));
    }

    public void StartAnalysis(UserId actor, IClock clock) =>
        TransitionTo(ProposalStatus.UnderAnalysis, actor, "Início da análise", clock);

    /// <summary>
    /// Aplica a decisão de aceitação. A decisão é imutável e versionada: uma reanálise
    /// acrescenta uma nova versão em vez de sobrescrever a anterior.
    /// </summary>
    public void ApplyDecision(UnderwritingDecision decision, UserId actor, IClock clock)
    {
        if (Status is not ProposalStatus.UnderAnalysis)
            throw new DomainException(ProposalErrors.NotUnderAnalysis,
                $"Proposta em {Status} não aceita decisão de aceitação.");

        if (decision.Outcome is UnderwritingOutcome.Approved && HasOpenPendencies)
            throw new DomainException(ProposalErrors.CannotApproveWithPendencies,
                "Proposta com pendência em aberto não pode ser aprovada.");

        _decisions.Add(decision);
        DecidedAt = clock.UtcNow;

        var target = decision.Outcome switch
        {
            UnderwritingOutcome.Approved => ProposalStatus.Approved,
            UnderwritingOutcome.Rejected => ProposalStatus.Rejected,
            UnderwritingOutcome.Pending => ProposalStatus.Pending,
            _ => throw new UnreachableException()
        };

        TransitionTo(target, actor, decision.PrimaryReason, clock);

        Raise(decision.Outcome switch
        {
            UnderwritingOutcome.Approved => new ProposalApproved(TenantId, Id, Number),
            UnderwritingOutcome.Rejected => new ProposalRejected(TenantId, Id, decision.Reasons),
            _ => (IDomainEvent)new ProposalPending(TenantId, Id, decision.Reasons)
        });
    }

    /// <summary>Chamado pelo agregado Policy ao concluir a emissão, na mesma transação.</summary>
    public void MarkIssued(PolicyId policyId, UserId actor, IClock clock)
    {
        TransitionTo(ProposalStatus.Issued, actor, $"Apólice {policyId} emitida", clock);
        IssuedAt = clock.UtcNow;
        Raise(new ProposalIssued(TenantId, Id, policyId));
    }

    public void Expire(IClock clock)
    {
        TransitionTo(ProposalStatus.Expired, UserId.New(), "Prazo esgotado", clock);
        Raise(new ProposalExpired(TenantId, Id));
    }

    // ---------------------------------------------------------------- pendências e documentos

    public Pendency OpenPendency(string code, string description, IClock clock)
    {
        if (Status is ProposalStatus.Issued or ProposalStatus.Rejected or ProposalStatus.Expired)
            throw new DomainException(ProposalErrors.ProposalClosed,
                $"Proposta em {Status} não aceita novas pendências.");

        if (_pendencies.Any(p => p.Code == code && !p.IsResolved))
            throw new DomainException(ProposalErrors.DuplicatePendency,
                $"Já existe pendência aberta com o código {code}.");

        var pendency = Pendency.Open(Id, code, description, clock);
        _pendencies.Add(pendency);
        Raise(new PendencyOpened(TenantId, Id, code));
        return pendency;
    }

    public void ResolvePendency(PendencyId pendencyId, UserId resolvedBy, IClock clock)
    {
        var pendency = _pendencies.SingleOrDefault(p => p.Id == pendencyId)
            ?? throw new DomainException(ProposalErrors.PendencyNotFound, "Pendência não encontrada.");

        pendency.Resolve(resolvedBy, clock);
        Raise(new PendencyResolved(TenantId, Id, pendency.Code));
    }

    public void AttachDocument(Guid documentId)
    {
        if (Status is ProposalStatus.Issued or ProposalStatus.Rejected or ProposalStatus.Expired)
            throw new DomainException(ProposalErrors.ProposalClosed,
                $"Proposta em {Status} não aceita novos documentos.");

        if (_documentIds.Contains(documentId)) return;   // idempotente por natureza
        _documentIds.Add(documentId);
    }

    // ---------------------------------------------------------------- exclusão lógica

    public void SoftDelete(Guid deletedBy, string reason, Guid batchId, IClock clock)
    {
        if (Status is ProposalStatus.Issued)
            throw new DomainException(ProposalErrors.CannotDeleteIssued,
                "Proposta emitida não pode ser excluída — há apólice vinculada.");

        DeletedAt = clock.UtcNow;
        DeletedBy = deletedBy;
        DeletionReason = reason;
        DeletionBatchId = batchId;
    }

    public bool IsDeleted => DeletedAt is not null;

    // ---------------------------------------------------------------- máquina de estados

    /// <summary>Ponto ÚNICO de mudança de status. Toda transição passa por aqui e é historiada.</summary>
    private void TransitionTo(ProposalStatus target, UserId actor, string reason, IClock clock)
    {
        if (IsDeleted)
            throw new DomainException(ProposalErrors.ProposalDeleted,
                "Proposta excluída não pode transicionar.");

        if (!AllowedTransitions[Status].Contains(target))
            throw new DomainException(ProposalErrors.InvalidTransition,
                $"Transição inválida: {Status} → {target}.");

        var from = Status;
        Status = target;
        _history.Add(ProposalStatusChange.Record(Id, from, target, reason, actor, clock));
    }

    /// <summary>Exposto para teste: permite verificar a tabela de transições exaustivamente.</summary>
    public static bool IsTransitionAllowed(ProposalStatus from, ProposalStatus to) =>
        AllowedTransitions[from].Contains(to);

    public static IReadOnlyCollection<ProposalStatus> AllStatuses =>
        Enum.GetValues<ProposalStatus>();
}

public static class ProposalErrors
{
    public const string PremiumInvalid = "PROPOSAL_PREMIUM_INVALID";
    public const string InstallmentCountInvalid = "PROPOSAL_INSTALLMENT_COUNT_INVALID";
    public const string InvalidTransition = "PROPOSAL_INVALID_TRANSITION";
    public const string NotUnderAnalysis = "PROPOSAL_NOT_UNDER_ANALYSIS";
    public const string CannotApproveWithPendencies = "PROPOSAL_HAS_OPEN_PENDENCIES";
    public const string DocumentsRequired = "PROPOSAL_DOCUMENTS_REQUIRED";
    public const string DuplicatePendency = "PROPOSAL_DUPLICATE_PENDENCY";
    public const string PendencyNotFound = "PROPOSAL_PENDENCY_NOT_FOUND";
    public const string ProposalClosed = "PROPOSAL_CLOSED";
    public const string ProposalDeleted = "PROPOSAL_DELETED";
    public const string CannotDeleteIssued = "PROPOSAL_CANNOT_DELETE_ISSUED";
    public const string DecisionReasonsRequired = "UNDERWRITING_REASONS_REQUIRED";
}
