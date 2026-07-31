using PortalDoCorretor.SharedKernel.Domain;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Policies.Domain;

public enum PolicyStatus { Active, Cancelled, Expired, Renewed }

public enum EndorsementKind { CoverageChange, PeriodChange, DataCorrection, Cancellation }

public enum CancellationReason
{
    CustomerRequest, NonPayment, RiskAggravation, AssetSold, Fraud, Other
}

/// <summary>
/// Dados necessários para emitir, vindos do agregado Proposal.
/// </summary>
/// <remarks>
/// Existe para que Policies <b>não referencie</b> o assembly de Proposals: a emissão recebe um
/// contrato de dados, não o agregado alheio. É o que mantém a fronteira entre módulos honesta
/// e permite que o teste arquitetural a verifique.
/// </remarks>
public sealed record IssuanceRequest(
    TenantId TenantId,
    ProposalId ProposalId,
    BrokerId BrokerId,
    CustomerId CustomerId,
    AssetId AssetId,
    ProductVersionId ProductVersionId,
    bool ProposalApproved,
    bool HasOpenPendencies,
    bool DecisionFavorable,
    Money NetPremium,
    IReadOnlyList<CoverageSelection> Coverages,
    CorrelationId CorrelationId);

/// <summary>Cobertura escolhida, com limite, franquia e prêmio já calculados na cotação.</summary>
public sealed record CoverageSelection(
    CoverageId CoverageId,
    CoverageLimit Limit,
    Deductible Deductible,
    Money Premium,
    bool IsMandatory);

/// <summary>
/// Raiz do agregado Policy. Toda apólice do sistema nasce em <see cref="Issue"/> —
/// não existe outro caminho de criação, o que torna as invariantes de emissão
/// impossíveis de contornar e auditáveis num único ponto.
/// </summary>
public sealed class Policy : AggregateRoot<PolicyId>, ITenantScoped
{
    private readonly List<PolicyCoverage> _coverages = [];
    private readonly List<Endorsement> _endorsements = [];

    public TenantId TenantId { get; private set; }
    public ProposalId ProposalId { get; private set; }
    public BrokerId BrokerId { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public AssetId AssetId { get; private set; }
    public ProductVersionId ProductVersionId { get; private set; }
    public PolicyNumber Number { get; private set; }
    public PolicyStatus Status { get; private set; }
    public DateRange Period { get; private set; }
    public Money NetPremium { get; private set; }
    public Money TotalPremium { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public CorrelationId CorrelationId { get; private set; }
    public PolicyId? RenewedFromId { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public CancellationReason? CancellationReason { get; private set; }
    public DateOnly? CancellationEffectiveDate { get; private set; }

    public IReadOnlyCollection<PolicyCoverage> Coverages => _coverages.AsReadOnly();
    public IReadOnlyCollection<Endorsement> Endorsements => _endorsements.AsReadOnly();

    private Policy() { }

    private Policy(PolicyId id, IssuanceRequest request, PolicyNumber number,
                   DateRange period, IClock clock) : base(id)
    {
        TenantId = request.TenantId;
        ProposalId = request.ProposalId;
        BrokerId = request.BrokerId;
        CustomerId = request.CustomerId;
        AssetId = request.AssetId;
        ProductVersionId = request.ProductVersionId;
        Number = number;
        Period = period;
        NetPremium = request.NetPremium;
        Status = PolicyStatus.Active;
        IssuedAt = clock.UtcNow;
        CorrelationId = request.CorrelationId;
        TotalPremium = Money.Zero();
    }

    /// <summary>
    /// Único caminho de emissão. Verifica, nesta ordem, as invariantes que impedem uma
    /// apólice inválida de existir.
    /// </summary>
    public static Policy Issue(IssuanceRequest request, PolicyNumber number,
                               DateRange period, IClock clock)
    {
        if (!request.ProposalApproved)
            throw new DomainException(PolicyErrors.ProposalNotApproved,
                "Proposta não está aprovada.");

        if (request.HasOpenPendencies)
            throw new DomainException(PolicyErrors.ProposalHasPendencies,
                "Proposta possui pendências em aberto.");

        if (!request.DecisionFavorable)
            throw new DomainException(PolicyErrors.UnfavorableDecision,
                "Decisão de aceitação não permite emissão.");

        if (request.Coverages.Count == 0)
            throw new DomainException(PolicyErrors.NoCoverages,
                "Apólice exige ao menos uma cobertura.");

        if (period.Start < clock.Today.AddDays(-1))
            throw new DomainException(PolicyErrors.PeriodInPast,
                "Vigência não pode iniciar no passado.");

        var policy = new Policy(PolicyId.New(), request, number, period, clock);

        // Coberturas são CONGELADAS na emissão: alteração posterior só via endosso
        foreach (var selection in request.Coverages)
            policy._coverages.Add(PolicyCoverage.Freeze(policy.Id, selection));

        policy.TotalPremium = policy._coverages
            .Select(c => c.Premium)
            .Aggregate(Money.Zero(), (acc, premium) => acc.Add(premium));

        if (!policy.TotalPremium.IsPositive)
            throw new DomainException(PolicyErrors.PremiumInvalid,
                "Prêmio total deve ser positivo.");

        // Invariante financeira: o líquido nunca excede o total
        if (policy.NetPremium > policy.TotalPremium)
            throw new DomainException(PolicyErrors.PremiumInvalid,
                "Prêmio líquido não pode exceder o total.");

        policy.Raise(new PolicyIssued(
            request.TenantId, policy.Id, request.ProposalId, number,
            policy.TotalPremium, period, request.BrokerId, request.CorrelationId));

        return policy;
    }

    /// <summary>Vincula a apólice à anterior no fluxo de renovação, preservando o histórico.</summary>
    public void MarkAsRenewalOf(PolicyId previousPolicyId)
    {
        if (RenewedFromId is not null)
            throw new DomainException(PolicyErrors.AlreadyLinked,
                "Apólice já vinculada a uma renovação.");

        RenewedFromId = previousPolicyId;
    }

    // ---------------------------------------------------------------- endosso

    public Endorsement Endorse(EndorsementKind kind, string description,
                               Money premiumDelta, DateOnly effectiveDate, IClock clock)
    {
        if (Status is not PolicyStatus.Active)
            throw new DomainException(PolicyErrors.PolicyNotActive,
                $"Apólice em {Status} não aceita endosso.");

        if (!Period.Contains(effectiveDate))
            throw new DomainException(PolicyErrors.EndorsementOutsidePeriod,
                "Data de efeito fora da vigência.");

        var newTotal = TotalPremium.Add(premiumDelta);
        if (!newTotal.IsPositive)
            throw new DomainException(PolicyErrors.PremiumInvalid,
                "Endosso deixaria o prêmio total não positivo.");

        var sequence = _endorsements.Count + 1;
        var endorsement = Endorsement.Create(Id, sequence, kind, description,
                                             premiumDelta, effectiveDate, clock);
        _endorsements.Add(endorsement);
        TotalPremium = newTotal;

        Raise(new PolicyEndorsed(TenantId, Id, sequence, kind, premiumDelta));
        return endorsement;
    }

    // ---------------------------------------------------------------- cancelamento

    public void Cancel(CancellationReason reason, DateOnly effectiveDate, IClock clock)
    {
        if (Status is not PolicyStatus.Active)
            throw new DomainException(PolicyErrors.PolicyNotActive,
                $"Apólice em {Status} não pode ser cancelada.");

        if (effectiveDate < Period.Start)
            throw new DomainException(PolicyErrors.InvalidCancellationDate,
                "Data de efeito anterior ao início da vigência.");

        if (effectiveDate > Period.End)
            throw new DomainException(PolicyErrors.InvalidCancellationDate,
                "Data de efeito posterior ao fim da vigência.");

        Status = PolicyStatus.Cancelled;
        CancelledAt = clock.UtcNow;
        CancellationReason = reason;
        CancellationEffectiveDate = effectiveDate;

        Raise(new PolicyCancelled(TenantId, Id, reason, effectiveDate, UnusedProportion(effectiveDate)));
    }

    /// <summary>
    /// Fração não decorrida da vigência, usada pelo módulo de comissões para calcular o
    /// estorno proporcional. Cálculo puro — o valor em si é responsabilidade de Commissions.
    /// </summary>
    public Percentage UnusedProportion(DateOnly effectiveDate)
    {
        var total = Period.DurationInDays;
        if (total <= 0) return Percentage.Zero;

        var remaining = Period.End.DayNumber - effectiveDate.DayNumber;
        if (remaining <= 0) return Percentage.Zero;

        return Percentage.Of(Math.Min(1m, remaining / (decimal)total));
    }

    public void MarkExpired(IClock clock)
    {
        if (Status is not PolicyStatus.Active) return;
        if (!Period.HasExpiredBy(clock.Today)) return;

        Status = PolicyStatus.Expired;
        Raise(new PolicyExpired(TenantId, Id, Period.End));
    }

    public void MarkRenewed()
    {
        if (Status is not (PolicyStatus.Active or PolicyStatus.Expired))
            throw new DomainException(PolicyErrors.PolicyNotActive,
                $"Apólice em {Status} não pode ser marcada como renovada.");

        Status = PolicyStatus.Renewed;
    }

    /// <summary>Cobertura vigente na data — usada na validação de aviso de sinistro.</summary>
    public bool CoversDate(DateOnly date) =>
        Period.Contains(date)
        && Status is not PolicyStatus.Cancelled
        || (Status is PolicyStatus.Cancelled
            && CancellationEffectiveDate is { } cancelDate
            && date < cancelDate
            && Period.Contains(date));

    public bool IsExpiringWithin(DateOnly reference, int days) =>
        Status is PolicyStatus.Active && Period.IsExpiringWithin(reference, days);
}

public static class PolicyErrors
{
    public const string ProposalNotApproved = "POLICY_PROPOSAL_NOT_APPROVED";
    public const string ProposalHasPendencies = "POLICY_PROPOSAL_HAS_PENDENCIES";
    public const string UnfavorableDecision = "POLICY_UNFAVORABLE_DECISION";
    public const string NoCoverages = "POLICY_NO_COVERAGES";
    public const string PremiumInvalid = "POLICY_PREMIUM_INVALID";
    public const string PeriodInPast = "POLICY_PERIOD_IN_PAST";
    public const string PolicyNotActive = "POLICY_NOT_ACTIVE";
    public const string InvalidCancellationDate = "POLICY_INVALID_CANCELLATION_DATE";
    public const string EndorsementOutsidePeriod = "POLICY_ENDORSEMENT_OUTSIDE_PERIOD";
    public const string DuplicateCoverage = "POLICY_DUPLICATE_COVERAGE";
    public const string AlreadyLinked = "POLICY_ALREADY_LINKED";
}
