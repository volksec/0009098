using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Quotations.Domain;

/// <summary>Faixa de plano. O multiplicador é regra de negócio, não configuração.</summary>
public sealed record PlanTier(string Code, string Name, decimal Multiplier, decimal CoverageFactor)
{
    public static readonly PlanTier Essential = new("ESSENTIAL", "Essencial", 0.85m, 0.70m);
    public static readonly PlanTier Complete = new("COMPLETE", "Completo", 1.00m, 1.00m);
    public static readonly PlanTier Master = new("MASTER", "Master", 1.28m, 1.40m);

    public static IReadOnlyList<PlanTier> All => [Essential, Complete, Master];

    public static PlanTier Parse(string code) =>
        All.FirstOrDefault(p => p.Code == code)
        ?? throw new DomainException("PLAN_TIER_INVALID", $"Plano '{code}' não existe.");
}

/// <summary>Cobertura disponível na versão do produto.</summary>
public sealed record CoverageOption(
    Guid Id,
    string Code,
    string Name,
    bool IsMandatory,
    decimal MinLimit,
    decimal MaxLimit,
    decimal RateFactor,
    string DeductibleKind,
    decimal? DeductibleAmount,
    decimal? DeductiblePercent);

/// <summary>Respostas do questionário de risco.</summary>
public sealed record RiskAnswers(
    bool HasGarage,
    string Usage,
    int DriverAge,
    bool PreviousClaims,
    string PostalRegion)
{
    public static RiskAnswers Default => new(false, "PERSONAL", 35, false, "0");
}

/// <summary>Parâmetros da versão do produto que entram no cálculo.</summary>
public sealed record ProductParameters(
    Guid ProductVersionId,
    string Branch,
    decimal BaseRate,
    decimal RiskSensitivity,
    int MaxAcceptableRisk,
    decimal MinInsuredValue,
    decimal MaxInsuredValue);

/// <summary>Resultado do cálculo de um plano, com todos os fatores que o produziram.</summary>
public sealed record CalculationResult(
    PlanTier Plan,
    Money NetPremium,
    Money TotalPremium,
    RiskScore RiskScore,
    decimal RiskMultiplier,
    IReadOnlyList<CoveragePricing> Coverages,
    IReadOnlyDictionary<string, decimal> Factors);

/// <summary>Precificação de uma cobertura dentro de um plano.</summary>
public sealed record CoveragePricing(
    Guid CoverageId,
    string Code,
    string Name,
    bool IsMandatory,
    Money Limit,
    Money Premium,
    string DeductibleKind,
    decimal DeductibleValue);

/// <summary>
/// Cálculo de prêmio <b>simulado</b>, determinístico e puro.
/// </summary>
/// <remarks>
/// <para>
/// Sem I/O, sem relógio e sem aleatoriedade: as mesmas entradas produzem sempre o mesmo
/// resultado. É o que permite reproduzir, meses depois, exatamente o prêmio que foi ofertado
/// — e é por isso que o <c>CalculationSnapshot</c> guarda os fatores, e não apenas o valor.
/// </para>
/// <para>
/// <b>Não é cálculo atuarial.</b> A fórmula é uma simulação documentada, criada para o case;
/// nenhuma tabela de mortalidade, sinistralidade ou reserva técnica está envolvida.
/// </para>
/// </remarks>
public static class PremiumCalculator
{
    public const string EngineVersion = "1.0.0";

    /// <summary>Carregamento sobre o prêmio líquido (custos e tributos simulados).</summary>
    private const decimal LoadingRate = 0.22m;

    /// <summary>
    /// Deriva o escore de risco das respostas do questionário.
    /// </summary>
    /// <remarks>
    /// A faixa é <b>derivada</b> do escore, nunca armazenada como campo editável — não existe
    /// estado em que escore e faixa possam divergir. A mesma derivação é replicada como coluna
    /// gerada no banco.
    /// </remarks>
    public static RiskScore ComputeRiskScore(RiskAnswers answers)
    {
        var score = 300m;   // ponto de partida: risco moderado-baixo

        // Idade do condutor: curva em U, com risco maior nos extremos
        score += answers.DriverAge switch
        {
            < 21 => 260m,
            < 25 => 170m,
            < 30 => 70m,
            < 60 => 0m,
            < 70 => 60m,
            _ => 140m
        };

        score += answers.Usage switch
        {
            "RIDESHARE" => 190m,    // exposição muito acima da média
            "COMMERCIAL" => 120m,
            "COMMUTE" => 40m,
            _ => 0m
        };

        if (!answers.HasGarage) score += 90m;
        if (answers.PreviousClaims) score += 130m;

        // Região postal como proxy geográfico
        score += int.TryParse(answers.PostalRegion, out var region) && region <= 1 ? 60m : 0m;

        return RiskScore.Of((int)Math.Clamp(Math.Round(score), 0, 1000));
    }

    /// <summary>
    /// Calcula os três planos para o mesmo conjunto de coberturas selecionadas.
    /// </summary>
    /// <exception cref="DomainException">
    /// Quando o valor do bem está fora da faixa do produto, quando uma cobertura obrigatória
    /// não foi selecionada, ou quando o risco excede o aceitável para a versão do produto.
    /// </exception>
    public static IReadOnlyList<CalculationResult> CalculateAllPlans(
        ProductParameters product,
        Money insuredValue,
        RiskAnswers answers,
        IReadOnlyList<CoverageOption> available,
        IReadOnlyCollection<Guid> selectedCoverageIds)
    {
        if (insuredValue.Amount < product.MinInsuredValue || insuredValue.Amount > product.MaxInsuredValue)
            throw new DomainException("ASSET_VALUE_OUT_OF_RANGE",
                $"O valor do bem deve estar entre {product.MinInsuredValue:N2} e "
              + $"{product.MaxInsuredValue:N2} para este produto.");

        // Invariante: cobertura obrigatória não pode ser desmarcada
        var missing = available
            .Where(c => c.IsMandatory && !selectedCoverageIds.Contains(c.Id))
            .Select(c => c.Name)
            .ToList();

        if (missing.Count > 0)
            throw new DomainException("MANDATORY_COVERAGE_MISSING",
                $"Cobertura obrigatória não selecionada: {string.Join(", ", missing)}.");

        var selected = available.Where(c => selectedCoverageIds.Contains(c.Id)).ToList();

        if (selected.Count == 0)
            throw new DomainException("NO_COVERAGE_SELECTED",
                "Selecione ao menos uma cobertura.");

        var riskScore = ComputeRiskScore(answers);

        if (!riskScore.IsAcceptableUpTo(product.MaxAcceptableRisk))
            throw new DomainException("RISK_NOT_ACCEPTABLE",
                $"Escore de risco {riskScore.Value} excede o máximo aceitável "
              + $"({product.MaxAcceptableRisk}) para este produto.");

        // O multiplicador cresce com o escore, ponderado pela sensibilidade do produto
        var riskMultiplier = 1m + (riskScore.AsFactor * product.RiskSensitivity);

        var factors = new Dictionary<string, decimal>
        {
            ["baseRate"] = product.BaseRate,
            ["riskScore"] = riskScore.Value,
            ["riskSensitivity"] = product.RiskSensitivity,
            ["riskMultiplier"] = Math.Round(riskMultiplier, 6),
            ["insuredValue"] = insuredValue.Amount,
            ["loadingRate"] = LoadingRate,
            ["driverAge"] = answers.DriverAge,
            ["hasGarage"] = answers.HasGarage ? 1m : 0m,
            ["previousClaims"] = answers.PreviousClaims ? 1m : 0m
        };

        return [.. PlanTier.All.Select(plan =>
            CalculatePlan(plan, product, insuredValue, riskScore, riskMultiplier, selected, factors))];
    }

    private static CalculationResult CalculatePlan(
        PlanTier plan,
        ProductParameters product,
        Money insuredValue,
        RiskScore riskScore,
        decimal riskMultiplier,
        IReadOnlyList<CoverageOption> selected,
        IReadOnlyDictionary<string, decimal> factors)
    {
        var pricings = new List<CoveragePricing>(selected.Count);
        var net = Money.Zero();

        foreach (var coverage in selected)
        {
            // O limite acompanha o plano, respeitando o teto da cobertura
            var rawLimit = insuredValue.Amount * plan.CoverageFactor;
            var limit = Math.Clamp(rawLimit, coverage.MinLimit, coverage.MaxLimit);

            var premium = Money.Of(Math.Round(
                limit * product.BaseRate * coverage.RateFactor * riskMultiplier * plan.Multiplier,
                2, MidpointRounding.ToEven));

            net = net.Add(premium);

            pricings.Add(new CoveragePricing(
                coverage.Id, coverage.Code, coverage.Name, coverage.IsMandatory,
                Money.Of(Math.Round(limit, 2)), premium,
                coverage.DeductibleKind,
                coverage.DeductibleKind == "PERCENTAGE"
                    ? coverage.DeductiblePercent ?? 0m
                    : coverage.DeductibleAmount ?? 0m));
        }

        var total = Money.Of(Math.Round(net.Amount * (1m + LoadingRate), 2, MidpointRounding.ToEven));

        return new CalculationResult(plan, net, total, riskScore,
            Math.Round(riskMultiplier, 6), pricings,
            new Dictionary<string, decimal>(factors) { ["planMultiplier"] = plan.Multiplier });
    }
}
