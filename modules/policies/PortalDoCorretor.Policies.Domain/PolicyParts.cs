using PortalDoCorretor.SharedKernel.Domain;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Policies.Domain;

/// <summary>
/// Cobertura contratada, congelada no momento da emissão.
/// </summary>
/// <remarks>
/// Não expõe nenhum método de alteração: limite, franquia e prêmio ficam como estavam quando
/// a apólice foi emitida. Mudança só existe através de endosso, que é um registro novo — a
/// apólice original permanece consultável exatamente como foi contratada.
/// </remarks>
public sealed class PolicyCoverage : Entity<Guid>
{
    public PolicyId PolicyId { get; private set; }
    public CoverageId CoverageId { get; private set; }
    public CoverageLimit Limit { get; private set; }
    public Deductible Deductible { get; private set; }
    public Money Premium { get; private set; }
    public bool IsMandatory { get; private set; }

    private PolicyCoverage() { }

    private PolicyCoverage(Guid id, PolicyId policyId, CoverageSelection selection) : base(id)
    {
        PolicyId = policyId;
        CoverageId = selection.CoverageId;
        Limit = selection.Limit;
        Deductible = selection.Deductible;
        Premium = selection.Premium;
        IsMandatory = selection.IsMandatory;
    }

    internal static PolicyCoverage Freeze(PolicyId policyId, CoverageSelection selection) =>
        new(Guid.CreateVersion7(), policyId, selection);

    /// <summary>Valor efetivamente indenizável, já descontada a franquia.</summary>
    public Money NetIndemnityFor(Money lossAmount)
    {
        var deductibleAmount = Deductible.AppliedTo(lossAmount);
        var net = lossAmount.Subtract(deductibleAmount);

        if (net.IsNegative) return Money.Zero();
        return net > Limit.Value ? Limit.Value : net;
    }
}

/// <summary>Alteração formal da apólice. Sequencial e imutável após a criação.</summary>
public sealed class Endorsement : Entity<Guid>
{
    public PolicyId PolicyId { get; private set; }
    public int Sequence { get; private set; }
    public EndorsementKind Kind { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public Money PremiumDelta { get; private set; }
    public DateOnly EffectiveDate { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Endorsement() { }

    private Endorsement(Guid id, PolicyId policyId, int sequence, EndorsementKind kind,
                        string description, Money premiumDelta, DateOnly effectiveDate,
                        DateTimeOffset createdAt) : base(id)
    {
        PolicyId = policyId;
        Sequence = sequence;
        Kind = kind;
        Description = description;
        PremiumDelta = premiumDelta;
        EffectiveDate = effectiveDate;
        CreatedAt = createdAt;
    }

    internal static Endorsement Create(PolicyId policyId, int sequence, EndorsementKind kind,
                                       string description, Money premiumDelta,
                                       DateOnly effectiveDate, IClock clock) =>
        new(Guid.CreateVersion7(), policyId, sequence, kind, description,
            premiumDelta, effectiveDate, clock.UtcNow);

    /// <summary>Endosso que aumenta o prêmio gera comissão complementar; o que reduz, estorno.</summary>
    public bool IncreasesPremium => PremiumDelta.IsPositive;
}

// ---------------------------------------------------------------- eventos

public sealed record PolicyIssued(
    TenantId TenantId,
    PolicyId PolicyId,
    ProposalId ProposalId,
    PolicyNumber Number,
    Money TotalPremium,
    DateRange Period,
    BrokerId BrokerId,
    CorrelationId CorrelationId) : DomainEvent(TenantId);

public sealed record PolicyEndorsed(
    TenantId TenantId, PolicyId PolicyId, int Sequence,
    EndorsementKind Kind, Money PremiumDelta) : DomainEvent(TenantId);

public sealed record PolicyCancelled(
    TenantId TenantId, PolicyId PolicyId, CancellationReason Reason,
    DateOnly EffectiveDate, Percentage UnusedProportion) : DomainEvent(TenantId);

public sealed record PolicyExpired(TenantId TenantId, PolicyId PolicyId, DateOnly ExpiredOn)
    : DomainEvent(TenantId);
