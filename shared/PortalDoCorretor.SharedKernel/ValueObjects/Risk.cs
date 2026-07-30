using PortalDoCorretor.SharedKernel.Errors;

namespace PortalDoCorretor.SharedKernel.ValueObjects;

public enum RiskBand
{
    Low,
    Moderate,
    High,
    Severe
}

/// <summary>
/// Escore de risco de 0 a 1000.
/// </summary>
/// <remarks>
/// A faixa (<see cref="Band"/>) é <b>derivada</b>, nunca armazenada como campo editável —
/// não existe estado em que escore e faixa possam divergir. No banco, a mesma derivação é
/// replicada como coluna gerada e indexada, permitindo filtrar por faixa sem recalcular.
/// </remarks>
public readonly record struct RiskScore : IComparable<RiskScore>
{
    public const int MinValue = 0;
    public const int MaxValue = 1000;

    public int Value { get; }

    private RiskScore(int value) => Value = value;

    public static RiskScore Of(int value) =>
        value is >= MinValue and <= MaxValue
            ? new RiskScore(value)
            : throw new DomainException(ErrorCodes.RiskScoreOutOfRange,
                $"Escore de risco deve estar entre {MinValue} e {MaxValue}.");

    public RiskBand Band => Value switch
    {
        <= 250 => RiskBand.Low,
        <= 550 => RiskBand.Moderate,
        <= 800 => RiskBand.High,
        _ => RiskBand.Severe
    };

    public bool IsAcceptableUpTo(int maxAcceptable) => Value <= maxAcceptable;

    /// <summary>Fração 0..1, usada como multiplicador na precificação simulada.</summary>
    public decimal AsFactor => Value / (decimal)MaxValue;

    public int CompareTo(RiskScore other) => Value.CompareTo(other.Value);

    public override string ToString() => $"{Value} ({Band})";
}

/// <summary>
/// Limite máximo de indenização de uma cobertura. Distinto de <see cref="Money"/> porque
/// carrega a regra de negócio "sempre positivo" — um limite de cobertura zero ou negativo
/// não é um valor monetário incomum, é um estado impossível.
/// </summary>
public readonly record struct CoverageLimit
{
    public Money Value { get; }

    private CoverageLimit(Money value) => Value = value;

    public static CoverageLimit Of(Money amount)
    {
        if (!amount.IsPositive)
            throw new DomainException(ErrorCodes.CoverageLimitInvalid,
                "Limite de cobertura deve ser positivo.");

        return new CoverageLimit(amount);
    }

    public static CoverageLimit Of(decimal amount) => Of(Money.Of(amount));

    public bool IsWithin(CoverageLimit cap) => Value <= cap.Value;

    public override string ToString() => Value.ToString();
}

public enum DeductibleKind
{
    Fixed,
    Percentage
}

/// <summary>
/// Franquia: valor fixo ou percentual sobre o valor do bem. Modelada como um único VO
/// com duas formas em vez de duas classes, porque o consumidor sempre pergunta a mesma
/// coisa — "quanto o segurado paga?" — e <see cref="AppliedTo"/> resolve polimorficamente.
/// </summary>
public readonly record struct Deductible
{
    public DeductibleKind Kind { get; }
    public Money? FixedAmount { get; }
    public Percentage? Rate { get; }

    private Deductible(DeductibleKind kind, Money? fixedAmount, Percentage? rate)
    {
        Kind = kind;
        FixedAmount = fixedAmount;
        Rate = rate;
    }

    public static Deductible Fixed(Money amount)
    {
        if (amount.IsNegative)
            throw new DomainException(ErrorCodes.DeductibleInvalid,
                "Franquia fixa não pode ser negativa.");

        return new Deductible(DeductibleKind.Fixed, amount, null);
    }

    public static Deductible Proportional(Percentage rate) =>
        new(DeductibleKind.Percentage, null, rate);

    public static Deductible None() => Fixed(Money.Zero());

    /// <summary>Valor efetivo da franquia sobre um valor segurado.</summary>
    public Money AppliedTo(Money insuredValue) => Kind switch
    {
        DeductibleKind.Fixed => FixedAmount!.Value,
        DeductibleKind.Percentage => insuredValue.MultiplyBy(Rate!.Value),
        _ => throw new InvalidOperationException("Tipo de franquia desconhecido.")
    };

    public override string ToString() => Kind == DeductibleKind.Fixed
        ? $"Fixa {FixedAmount}"
        : $"Proporcional {Rate}";
}
