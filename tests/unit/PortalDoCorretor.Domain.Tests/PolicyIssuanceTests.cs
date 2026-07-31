using FluentAssertions;
using PortalDoCorretor.Policies.Domain;
using PortalDoCorretor.SharedKernel.Domain;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Domain.Tests;

public sealed class PolicyIssuanceTests
{
    private readonly FixedClock _clock = FixedClock.At(2026, 3, 10);
    private readonly TenantId _tenant = TenantId.FromTrustedSource(Guid.NewGuid());

    private static CoverageSelection Coverage(decimal limit, decimal premium, bool mandatory = true) =>
        new(CoverageId.New(), CoverageLimit.Of(limit), Deductible.Fixed(Money.Of(1500m)),
            Money.Of(premium), mandatory);

    private IssuanceRequest ValidRequest(
        bool approved = true, bool pendencies = false, bool favorable = true,
        IReadOnlyList<CoverageSelection>? coverages = null, decimal netPremium = 1000m) =>
        new(_tenant, ProposalId.New(), BrokerId.New(), CustomerId.New(), AssetId.New(),
            ProductVersionId.New(), approved, pendencies, favorable, Money.Of(netPremium),
            coverages ?? [Coverage(50_000m, 800m), Coverage(20_000m, 400m)],
            CorrelationId.New());

    private DateRange Period => DateRange.OfYear(_clock.Today);

    private Policy Issue(IssuanceRequest? request = null) =>
        Policy.Issue(request ?? ValidRequest(), PolicyNumber.Generate(2026, 1), Period, _clock);

    // ---------------------------------------------------------------- emissão

    [Fact]
    public void Emissao_valida_congela_coberturas_e_soma_o_premio()
    {
        var policy = Issue();

        policy.Status.Should().Be(PolicyStatus.Active);
        policy.Coverages.Should().HaveCount(2);
        policy.TotalPremium.Should().Be(Money.Of(1200m), "800 + 400");
        policy.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<PolicyIssued>();
    }

    [Fact]
    public void Evento_de_emissao_carrega_o_correlation_id_da_requisicao()
    {
        var request = ValidRequest();
        var policy = Policy.Issue(request, PolicyNumber.Generate(2026, 1), Period, _clock);

        policy.DomainEvents.OfType<PolicyIssued>().Single()
            .CorrelationId.Should().Be(request.CorrelationId);
    }

    // ---------------------------------------------------------------- invariantes

    [Fact]
    public void Nao_emite_proposta_nao_aprovada() =>
        AssertRejects(() => Issue(ValidRequest(approved: false)), PolicyErrors.ProposalNotApproved);

    [Fact]
    public void Nao_emite_proposta_com_pendencia_aberta() =>
        AssertRejects(() => Issue(ValidRequest(pendencies: true)), PolicyErrors.ProposalHasPendencies);

    [Fact]
    public void Nao_emite_com_decisao_desfavoravel() =>
        AssertRejects(() => Issue(ValidRequest(favorable: false)), PolicyErrors.UnfavorableDecision);

    [Fact]
    public void Nao_emite_sem_cobertura() =>
        AssertRejects(() => Issue(ValidRequest(coverages: [])), PolicyErrors.NoCoverages);

    /// <summary>
    /// Prêmio líquido maior que o total é incoerência contábil — o líquido é o total
    /// menos encargos, então nunca pode excedê-lo.
    /// </summary>
    [Fact]
    public void Nao_emite_com_premio_liquido_maior_que_o_total() =>
        AssertRejects(
            () => Issue(ValidRequest(netPremium: 5000m, coverages: [Coverage(10_000m, 100m)])),
            PolicyErrors.PremiumInvalid);

    [Fact]
    public void Nao_emite_com_vigencia_iniciando_no_passado()
    {
        var pastPeriod = DateRange.OfYear(_clock.Today.AddDays(-30));

        FluentActions.Invoking(() =>
                Policy.Issue(ValidRequest(), PolicyNumber.Generate(2026, 1), pastPeriod, _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(PolicyErrors.PeriodInPast);
    }

    // ---------------------------------------------------------------- endosso

    [Fact]
    public void Endosso_ajusta_o_premio_e_e_sequencial()
    {
        var policy = Issue();

        policy.Endorse(EndorsementKind.CoverageChange, "Inclusão de vidros",
                       Money.Of(150m), _clock.Today.AddDays(30), _clock);
        policy.Endorse(EndorsementKind.DataCorrection, "Correção de endereço",
                       Money.Zero(), _clock.Today.AddDays(40), _clock);

        policy.TotalPremium.Should().Be(Money.Of(1350m));
        policy.Endorsements.Select(e => e.Sequence).Should().Equal(1, 2);
    }

    [Fact]
    public void Endosso_fora_da_vigencia_e_rejeitado()
    {
        var policy = Issue();

        FluentActions.Invoking(() => policy.Endorse(
                EndorsementKind.CoverageChange, "Fora do período",
                Money.Of(100m), _clock.Today.AddYears(2), _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(PolicyErrors.EndorsementOutsidePeriod);
    }

    [Fact]
    public void Apolice_cancelada_nao_aceita_endosso()
    {
        var policy = Issue();
        policy.Cancel(CancellationReason.CustomerRequest, _clock.Today.AddDays(10), _clock);

        FluentActions.Invoking(() => policy.Endorse(
                EndorsementKind.CoverageChange, "tentativa", Money.Of(50m),
                _clock.Today.AddDays(20), _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(PolicyErrors.PolicyNotActive);
    }

    // ---------------------------------------------------------------- cancelamento

    [Fact]
    public void Cancelamento_calcula_a_proporcao_nao_decorrida()
    {
        var policy = Issue();
        // Cancela na metade da vigência anual
        var halfway = _clock.Today.AddDays(policy.Period.DurationInDays / 2);

        policy.Cancel(CancellationReason.AssetSold, halfway, _clock);

        policy.Status.Should().Be(PolicyStatus.Cancelled);
        var unused = policy.DomainEvents.OfType<PolicyCancelled>().Single().UnusedProportion;
        unused.Value.Should().BeApproximately(0.5m, 0.01m,
            "metade da vigência não foi utilizada — base do estorno de comissão");
    }

    [Fact]
    public void Cancelamento_com_data_anterior_a_vigencia_e_rejeitado()
    {
        var policy = Issue();

        FluentActions.Invoking(() => policy.Cancel(
                CancellationReason.Fraud, policy.Period.Start.AddDays(-1), _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(PolicyErrors.InvalidCancellationDate);
    }

    [Fact]
    public void Apolice_cancelada_nao_cancela_novamente()
    {
        var policy = Issue();
        policy.Cancel(CancellationReason.NonPayment, _clock.Today.AddDays(5), _clock);

        FluentActions.Invoking(() => policy.Cancel(
                CancellationReason.Other, _clock.Today.AddDays(6), _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(PolicyErrors.PolicyNotActive);
    }

    // ---------------------------------------------------------------- cobertura e indenização

    [Fact]
    public void Indenizacao_desconta_franquia_e_respeita_o_limite()
    {
        // netPremium abaixo do total (800), senão a invariante de prêmio bloqueia a emissão
        var policy = Issue(ValidRequest(netPremium: 700m, coverages: [Coverage(50_000m, 800m)]));
        var coverage = policy.Coverages.Single();

        // Franquia fixa de R$ 1.500 sobre um sinistro de R$ 10.000
        coverage.NetIndemnityFor(Money.Of(10_000m)).Should().Be(Money.Of(8500m));

        // Prejuízo acima do limite é truncado no limite contratado
        coverage.NetIndemnityFor(Money.Of(80_000m)).Should().Be(Money.Of(50_000m));

        // Prejuízo abaixo da franquia não gera indenização
        coverage.NetIndemnityFor(Money.Of(1000m)).Should().Be(Money.Zero());
    }

    [Fact]
    public void Renovacao_vincula_a_apolice_anterior_uma_unica_vez()
    {
        var policy = Issue();
        var previous = PolicyId.New();

        policy.MarkAsRenewalOf(previous);
        policy.RenewedFromId.Should().Be(previous);

        FluentActions.Invoking(() => policy.MarkAsRenewalOf(PolicyId.New()))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(PolicyErrors.AlreadyLinked);
    }

    [Fact]
    public void Apolice_proxima_do_vencimento_e_detectada_pela_janela()
    {
        var policy = Issue();
        var thirtyDaysBeforeEnd = policy.Period.End.AddDays(-30);

        policy.IsExpiringWithin(thirtyDaysBeforeEnd, 45).Should().BeTrue();
        policy.IsExpiringWithin(thirtyDaysBeforeEnd, 15).Should().BeFalse();
    }

    [Fact]
    public void Colecao_de_coberturas_e_somente_leitura() =>
        policyCoveragesType().Should().NotBeAssignableTo<System.Collections.IList>();

    private static Type policyCoveragesType() =>
        typeof(Policy).GetProperty(nameof(Policy.Coverages))!.PropertyType;

    private static void AssertRejects(Func<Policy> action, string expectedCode) =>
        FluentActions.Invoking(() => action())
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(expectedCode);
}
