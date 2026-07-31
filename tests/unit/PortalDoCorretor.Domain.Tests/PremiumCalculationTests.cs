using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using PortalDoCorretor.Quotations.Domain;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Domain.Tests;

/// <summary>
/// O cálculo de prêmio é a única parte do domínio que o corretor vê como número, e é
/// também a que precisa ser reproduzível meses depois. Estes testes fixam as duas coisas:
/// o que a fórmula garante, e o que ela recusa.
/// </summary>
public sealed class PremiumCalculationTests
{
    private static readonly ProductParameters Auto = new(
        ProductVersionId: Guid.NewGuid(),
        Branch: "AUTO",
        BaseRate: 0.0180m,
        RiskSensitivity: 0.35m,
        MaxAcceptableRisk: 800,
        MinInsuredValue: 10_000m,
        MaxInsuredValue: 500_000m);

    private static readonly CoverageOption Collision = new(
        Guid.NewGuid(), "COLLISION", "Colisão e capotagem", true,
        10_000m, 500_000m, 0.028m, "PERCENTAGE", null, 0.05m);

    private static readonly CoverageOption Theft = new(
        Guid.NewGuid(), "THEFT", "Roubo e furto", true,
        10_000m, 500_000m, 0.022m, "FIXED", 0m, null);

    private static readonly CoverageOption Glass = new(
        Guid.NewGuid(), "GLASS", "Vidros e faróis", false,
        1_000m, 15_000m, 0.004m, "FIXED", 250m, null);

    private static readonly CoverageOption[] Catalog = [Collision, Theft, Glass];

    private static readonly Guid[] Mandatory = [Collision.Id, Theft.Id];

    private static IReadOnlyList<CalculationResult> Calculate(
        decimal insuredValue = 80_000m,
        RiskAnswers? answers = null,
        Guid[]? selection = null) =>
        PremiumCalculator.CalculateAllPlans(
            Auto, Money.Of(insuredValue), answers ?? RiskAnswers.Default,
            Catalog, selection ?? Mandatory);

    // ------------------------------------------------------------ escore de risco

    [Theory]
    [InlineData(19, 560)]   // condutor muito jovem
    [InlineData(23, 470)]
    [InlineData(27, 370)]
    [InlineData(40, 300)]   // faixa sem agravo
    [InlineData(65, 360)]
    [InlineData(75, 440)]   // extremo superior da curva em U
    public void EscoreSegueCurvaEmUPorIdade(int age, int expected)
    {
        var answers = new RiskAnswers(HasGarage: true, "PERSONAL", age, false, "5");

        PremiumCalculator.ComputeRiskScore(answers).Value.Should().Be(expected);
    }

    [Fact]
    public void CadaAgravoSomaSeuPesoAoEscore()
    {
        var baseline = new RiskAnswers(HasGarage: true, "PERSONAL", 40, false, "5");

        var semGaragem = baseline with { HasGarage = false };
        var comSinistros = baseline with { PreviousClaims = true };
        var aplicativo = baseline with { Usage = "RIDESHARE" };

        var b = PremiumCalculator.ComputeRiskScore(baseline).Value;

        PremiumCalculator.ComputeRiskScore(semGaragem).Value.Should().Be(b + 90);
        PremiumCalculator.ComputeRiskScore(comSinistros).Value.Should().Be(b + 130);
        PremiumCalculator.ComputeRiskScore(aplicativo).Value.Should().Be(b + 190);
    }

    [Fact]
    public void FaixaEDerivadaDoEscoreNuncaArmazenada()
    {
        var baixo = new RiskAnswers(true, "PERSONAL", 40, false, "5");
        var severo = new RiskAnswers(false, "RIDESHARE", 19, true, "0");

        PremiumCalculator.ComputeRiskScore(baixo).Band.Should().Be(RiskBand.Moderate);
        PremiumCalculator.ComputeRiskScore(severo).Band.Should().Be(RiskBand.Severe);
    }

    [Fact]
    public void EscoreNuncaEstouraOsLimitesDaEscala()
    {
        // Todos os agravos simultâneos somam mais de 1000; o clamp precisa segurar
        var pior = new RiskAnswers(HasGarage: false, "RIDESHARE", 18, true, "0");

        PremiumCalculator.ComputeRiskScore(pior).Value.Should().Be(1000);
    }

    // ------------------------------------------------------------ determinismo

    [Fact]
    public void MesmaEntradaProduzExatamenteOMesmoPremio()
    {
        var answers = new RiskAnswers(false, "COMMUTE", 31, true, "3");

        var primeira = Calculate(answers: answers);
        var segunda = Calculate(answers: answers);

        primeira.Select(r => r.TotalPremium.Amount)
            .Should().Equal(segunda.Select(r => r.TotalPremium.Amount));
    }

    [Fact]
    public void FatoresDeEntradaSaoDevolvidosParaOSnapshot()
    {
        // Sem os fatores, o valor não é reproduzível — só reconferível por sorte
        var result = Calculate().First();

        result.Factors.Should().ContainKeys(
            "baseRate", "riskScore", "riskSensitivity", "riskMultiplier",
            "insuredValue", "loadingRate", "driverAge", "hasGarage",
            "previousClaims", "planMultiplier");

        result.Factors["planMultiplier"].Should().Be(result.Plan.Multiplier);
    }

    // ------------------------------------------------------------ ordenação dos planos

    [Fact]
    public void OsTresPlanosSaoCalculadosEmOrdemCrescente()
    {
        var results = Calculate();

        results.Should().HaveCount(3);
        results.Select(r => r.Plan.Code).Should().Equal("ESSENTIAL", "COMPLETE", "MASTER");
        results.Select(r => r.TotalPremium.Amount).Should().BeInAscendingOrder();
    }

    [Fact]
    public void PremioTotalEOLiquidoMaisCarregamento()
    {
        foreach (var result in Calculate())
        {
            var esperado = Math.Round(result.NetPremium.Amount * 1.22m, 2, MidpointRounding.ToEven);
            result.TotalPremium.Amount.Should().Be(esperado);
        }
    }

    [Fact]
    public void LimiteRespeitaOTetoDaCoberturaMesmoNoPlanoMaisAlto()
    {
        // Vidros tem teto de R$ 15.000; no plano Master o fator seria 1,40 × 80.000
        var results = Calculate(selection: [Collision.Id, Theft.Id, Glass.Id]);
        var master = results.Single(r => r.Plan.Code == "MASTER");

        master.Coverages.Single(c => c.Code == "GLASS").Limit.Amount.Should().Be(15_000m);
        master.Coverages.Single(c => c.Code == "COLLISION").Limit.Amount.Should().Be(112_000m);
    }

    [Fact]
    public void RiscoMaiorProduzPremioMaior()
    {
        var tranquilo = new RiskAnswers(true, "PERSONAL", 45, false, "5");
        // Agravado, mas ainda dentro do apetite: somar todos os agravos estouraria o teto
        // do produto e a cotação seria recusada em vez de precificada
        var agravado = new RiskAnswers(false, "RIDESHARE", 45, false, "5");

        var barato = Calculate(answers: tranquilo).First().TotalPremium.Amount;
        var caro = Calculate(answers: agravado).First().TotalPremium.Amount;

        caro.Should().BeGreaterThan(barato);
    }

    // ------------------------------------------------------------ recusas

    [Fact]
    public void CoberturaObrigatoriaNaoSelecionadaRecusaACotacao()
    {
        var act = () => Calculate(selection: [Glass.Id]);

        act.Should().Throw<DomainException>()
            .Where(e => e.Code == "MANDATORY_COVERAGE_MISSING")
            .Which.Message.Should().Contain("Colisão").And.Contain("Roubo");
    }

    [Theory]
    [InlineData(9_999)]
    [InlineData(500_001)]
    public void ValorDoBemForaDaFaixaDoProdutoRecusaACotacao(decimal insuredValue)
    {
        var act = () => Calculate(insuredValue);

        act.Should().Throw<DomainException>().Where(e => e.Code == "ASSET_VALUE_OUT_OF_RANGE");
    }

    [Fact]
    public void RiscoAcimaDoApetiteDoProdutoRecusaACotacao()
    {
        var inaceitavel = new RiskAnswers(HasGarage: false, "RIDESHARE", 19, true, "0");

        var act = () => Calculate(answers: inaceitavel);

        act.Should().Throw<DomainException>().Where(e => e.Code == "RISK_NOT_ACCEPTABLE");
    }

    [Fact]
    public void SelecaoVaziaRecusaACotacao()
    {
        // Produto sem coberturas obrigatórias: a recusa precisa vir da seleção vazia
        var opcionais = new[] { Glass };

        var act = () => PremiumCalculator.CalculateAllPlans(
            Auto, Money.Of(80_000m), RiskAnswers.Default, opcionais, []);

        act.Should().Throw<DomainException>().Where(e => e.Code == "NO_COVERAGE_SELECTED");
    }

    [Fact]
    public void PlanoInexistenteNaoEAceito()
    {
        var act = () => PlanTier.Parse("PLATINUM");

        act.Should().Throw<DomainException>().Where(e => e.Code == "PLAN_TIER_INVALID");
    }

    // ------------------------------------------------------------ propriedades

    [Property]
    public Property PremioNuncaENegativoNemPerdeCentavosNaSoma()
    {
        return Prop.ForAll(
            Gen.Choose(10_000, 500_000).Select(v => (decimal)v).ToArbitrary(),
            Gen.Choose(18, 99).ToArbitrary(),
            (insuredValue, age) =>
            {
                var answers = new RiskAnswers(true, "PERSONAL", age, false, "5");

                // Idades extremas podem estourar o apetite do produto — não é o que se testa aqui
                if (!PremiumCalculator.ComputeRiskScore(answers)
                        .IsAcceptableUpTo(Auto.MaxAcceptableRisk))
                    return true;

                return Calculate(insuredValue, answers).All(result =>
                    result.NetPremium.Amount > 0
                    && result.TotalPremium.Amount >= result.NetPremium.Amount
                    // O prêmio líquido é exatamente a soma das coberturas: nada se perde
                    && result.Coverages.Sum(c => c.Premium.Amount) == result.NetPremium.Amount);
            });
    }

    [Property]
    public Property PlanoSuperiorNuncaCustaMenosQueOInferior()
    {
        return Prop.ForAll(
            Gen.Choose(10_000, 500_000).Select(v => (decimal)v).ToArbitrary(),
            insuredValue =>
            {
                var results = Calculate(insuredValue);

                return results[0].TotalPremium.Amount <= results[1].TotalPremium.Amount
                    && results[1].TotalPremium.Amount <= results[2].TotalPremium.Amount;
            });
    }
}
