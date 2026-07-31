using FluentAssertions;
using PortalDoCorretor.Proposals.Domain;
using PortalDoCorretor.SharedKernel.Domain;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Domain.Tests;

/// <summary>Relógio determinístico — o domínio não consulta DateTime.UtcNow.</summary>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;
    public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);
    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);

    public static FixedClock At(int year, int month, int day) =>
        new(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));
}

public sealed class ProposalStateMachineTests
{
    private readonly FixedClock _clock = FixedClock.At(2026, 3, 10);
    private readonly TenantId _tenant = TenantId.FromTrustedSource(Guid.NewGuid());
    private readonly UserId _actor = UserId.New();

    private Proposal NewProposal() => Proposal.FromQuotation(
        _tenant, QuotationId.New(), BrokerId.New(), CustomerId.New(),
        ProposalNumber.Generate(2026, 1), PlanTier.Complete,
        Money.Of(1000m), Money.Of(1200m), 6, _clock);

    private Proposal SubmittedProposal()
    {
        var proposal = NewProposal();
        proposal.AttachDocument(Guid.NewGuid());
        proposal.Submit(_actor, _clock);
        return proposal;
    }

    // ---------------------------------------------------------------- tabela de transições

    /// <summary>
    /// Verificação EXAUSTIVA: todos os 64 pares (origem, destino) são exercitados.
    /// Um teste por caminho feliz deixaria passar a transição inválida que ninguém lembrou.
    /// </summary>
    [Fact]
    public void Tabela_de_transicoes_cobre_todos_os_pares_de_status()
    {
        var statuses = Proposal.AllStatuses.ToArray();
        var pairs = statuses.SelectMany(_ => statuses).Count();

        pairs.Should().Be(statuses.Length * statuses.Length);

        foreach (var from in statuses)
            foreach (var to in statuses)
            {
                // A consulta não pode lançar para nenhum par — a tabela precisa ser total
                var act = () => Proposal.IsTransitionAllowed(from, to);
                act.Should().NotThrow($"a tabela deve responder para {from} → {to}");
            }
    }

    [Theory]
    [InlineData(ProposalStatus.Draft, ProposalStatus.Submitted)]
    [InlineData(ProposalStatus.Submitted, ProposalStatus.UnderAnalysis)]
    [InlineData(ProposalStatus.UnderAnalysis, ProposalStatus.Approved)]
    [InlineData(ProposalStatus.UnderAnalysis, ProposalStatus.Rejected)]
    [InlineData(ProposalStatus.UnderAnalysis, ProposalStatus.Pending)]
    [InlineData(ProposalStatus.Pending, ProposalStatus.UnderAnalysis)]
    [InlineData(ProposalStatus.Approved, ProposalStatus.Issued)]
    public void Transicoes_validas_sao_permitidas(ProposalStatus from, ProposalStatus to) =>
        Proposal.IsTransitionAllowed(from, to).Should().BeTrue();

    [Theory]
    [InlineData(ProposalStatus.Draft, ProposalStatus.Approved)]      // pula a análise
    [InlineData(ProposalStatus.Draft, ProposalStatus.Issued)]        // emite sem aprovar
    [InlineData(ProposalStatus.Rejected, ProposalStatus.Approved)]   // ressuscita recusada
    [InlineData(ProposalStatus.Rejected, ProposalStatus.Issued)]     // emite recusada
    [InlineData(ProposalStatus.Issued, ProposalStatus.Draft)]        // volta atrás na emissão
    [InlineData(ProposalStatus.Expired, ProposalStatus.Approved)]    // aprova expirada
    [InlineData(ProposalStatus.Submitted, ProposalStatus.Issued)]    // emite sem análise
    public void Transicoes_invalidas_sao_bloqueadas(ProposalStatus from, ProposalStatus to) =>
        Proposal.IsTransitionAllowed(from, to).Should().BeFalse();

    /// <summary>Estados finais não têm saída — proposta recusada, emitida ou expirada não volta.</summary>
    [Theory]
    [InlineData(ProposalStatus.Rejected)]
    [InlineData(ProposalStatus.Issued)]
    [InlineData(ProposalStatus.Expired)]
    public void Estados_finais_nao_possuem_transicao_de_saida(ProposalStatus terminal) =>
        Proposal.AllStatuses
            .Where(target => Proposal.IsTransitionAllowed(terminal, target))
            .Should().BeEmpty($"{terminal} é estado final");

    // ---------------------------------------------------------------- comportamento

    [Fact]
    public void Proposta_nasce_em_draft_e_emite_evento()
    {
        var proposal = NewProposal();

        proposal.Status.Should().Be(ProposalStatus.Draft);
        proposal.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProposalCreated>();
    }

    [Fact]
    public void Submissao_exige_ao_menos_um_documento()
    {
        var proposal = NewProposal();

        FluentActions.Invoking(() => proposal.Submit(_actor, _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ProposalErrors.DocumentsRequired);

        proposal.Status.Should().Be(ProposalStatus.Draft, "a transição não pode ter ocorrido");
    }

    [Fact]
    public void Toda_transicao_e_registrada_no_historico()
    {
        var proposal = SubmittedProposal();
        proposal.StartAnalysis(_actor, _clock);

        proposal.History.Should().HaveCount(2);
        proposal.History.Last().FromStatus.Should().Be(ProposalStatus.Submitted);
        proposal.History.Last().ToStatus.Should().Be(ProposalStatus.UnderAnalysis);
        proposal.History.Last().ChangedBy.Should().Be(_actor);
    }

    // ---------------------------------------------------------------- invariante central

    /// <summary>
    /// A invariante que impede o pior desfecho: aprovar uma proposta com pendência aberta,
    /// o que permitiria emitir apólice sem a documentação exigida.
    /// </summary>
    [Fact]
    public void Nao_aprova_proposta_com_pendencia_aberta()
    {
        var proposal = SubmittedProposal();
        proposal.StartAnalysis(_actor, _clock);
        proposal.OpenPendency("DOC_CNH", "CNH ilegível", _clock);

        var approval = UnderwritingDecision.Create(
            proposal.Id, 1, UnderwritingOutcome.Approved, [], new Dictionary<string, bool>(),
            _actor, CorrelationId.New(), _clock);

        FluentActions.Invoking(() => proposal.ApplyDecision(approval, _actor, _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ProposalErrors.CannotApproveWithPendencies);

        proposal.Status.Should().Be(ProposalStatus.UnderAnalysis);
    }

    [Fact]
    public void Aprova_depois_que_a_pendencia_e_resolvida()
    {
        var proposal = SubmittedProposal();
        proposal.StartAnalysis(_actor, _clock);
        var pendency = proposal.OpenPendency("DOC_CNH", "CNH ilegível", _clock);

        proposal.ResolvePendency(pendency.Id, _actor, _clock);
        proposal.HasOpenPendencies.Should().BeFalse();

        var approval = UnderwritingDecision.Create(
            proposal.Id, 1, UnderwritingOutcome.Approved, [], new Dictionary<string, bool>(),
            _actor, CorrelationId.New(), _clock);

        proposal.ApplyDecision(approval, _actor, _clock);
        proposal.Status.Should().Be(ProposalStatus.Approved);
    }

    [Fact]
    public void Pendencia_duplicada_e_rejeitada()
    {
        var proposal = SubmittedProposal();
        proposal.StartAnalysis(_actor, _clock);
        proposal.OpenPendency("DOC_CNH", "CNH ilegível", _clock);

        FluentActions.Invoking(() => proposal.OpenPendency("DOC_CNH", "outra descrição", _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ProposalErrors.DuplicatePendency);
    }

    // ---------------------------------------------------------------- decisão imutável

    [Fact]
    public void Decisao_desfavoravel_exige_motivo()
    {
        FluentActions.Invoking(() => UnderwritingDecision.Create(
                ProposalId.New(), 1, UnderwritingOutcome.Rejected, [],
                new Dictionary<string, bool>(), _actor, CorrelationId.New(), _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ProposalErrors.DecisionReasonsRequired);
    }

    [Fact]
    public void Reanalise_versiona_a_decisao_sem_sobrescrever_a_anterior()
    {
        var proposal = SubmittedProposal();
        proposal.StartAnalysis(_actor, _clock);

        var pending = UnderwritingDecision.Create(
            proposal.Id, 1, UnderwritingOutcome.Pending, ["Documentação incompleta"],
            new Dictionary<string, bool> { ["DOC_COMPLETE"] = false },
            _actor, CorrelationId.New(), _clock);
        proposal.ApplyDecision(pending, _actor, _clock);

        proposal.StartAnalysis(_actor, _clock);
        var approved = UnderwritingDecision.Create(
            proposal.Id, 2, UnderwritingOutcome.Approved, [],
            new Dictionary<string, bool> { ["DOC_COMPLETE"] = true },
            _actor, CorrelationId.New(), _clock);
        proposal.ApplyDecision(approved, _actor, _clock);

        proposal.Decisions.Should().HaveCount(2, "a decisão anterior é preservada");
        proposal.CurrentDecision!.Version.Should().Be(2);
        proposal.Decisions.Single(d => d.Version == 1).Outcome
            .Should().Be(UnderwritingOutcome.Pending);
    }

    [Fact]
    public void Proposta_emitida_nao_aceita_exclusao_logica()
    {
        var proposal = SubmittedProposal();
        proposal.StartAnalysis(_actor, _clock);
        proposal.ApplyDecision(UnderwritingDecision.Create(
            proposal.Id, 1, UnderwritingOutcome.Approved, [], new Dictionary<string, bool>(),
            _actor, CorrelationId.New(), _clock), _actor, _clock);
        proposal.MarkIssued(PolicyId.New(), _actor, _clock);

        FluentActions.Invoking(() =>
                proposal.SoftDelete(Guid.NewGuid(), "engano operacional", Guid.NewGuid(), _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ProposalErrors.CannotDeleteIssued);
    }

    [Fact]
    public void Anexar_o_mesmo_documento_duas_vezes_e_idempotente()
    {
        var proposal = NewProposal();
        var documentId = Guid.NewGuid();

        proposal.AttachDocument(documentId);
        proposal.AttachDocument(documentId);

        proposal.DocumentIds.Should().ContainSingle();
    }
}
