using NexusBroker.SharedKernel.Errors;

namespace NexusBroker.SharedKernel.ValueObjects;

/// <summary>
/// Fração entre 0 e 1 (0,15 = 15%). Escala de 6 casas — suficiente para fatores de
/// precificação sem acumular erro de arredondamento em multiplicações encadeadas.
/// </summary>
public readonly record struct Percentage : IComparable<Percentage>
{
    public decimal Value { get; }

    private Percentage(decimal value) => Value = value;

    /// <summary>Constrói a partir da fração: <c>Of(0.15m)</c> = 15%.</summary>
    public static Percentage Of(decimal fraction)
    {
        if (fraction is < 0m or > 1m)
            throw new DomainException(ErrorCodes.PercentageOutOfRange,
                "Percentual deve estar entre 0 e 1.");

        return new Percentage(decimal.Round(fraction, 6, MidpointRounding.ToEven));
    }

    /// <summary>Constrói a partir do valor percentual: <c>FromPercent(15m)</c> = 15%.</summary>
    public static Percentage FromPercent(decimal percent) => Of(percent / 100m);

    public static Percentage Zero => new(0m);

    public decimal AsPercent => Value * 100m;

    public int CompareTo(Percentage other) => Value.CompareTo(other.Value);

    public override string ToString() => $"{AsPercent:0.####}%";
}

/// <summary>
/// Percentual de comissão, com teto de negócio de 35%. Distinto de <see cref="Percentage"/>
/// justamente para que uma taxa de comissão fora da faixa não seja construível — evitar
/// primitive obsession significa também não reusar o VO genérico onde há regra específica.
/// </summary>
public readonly record struct CommissionRate
{
    public const decimal MaxRate = 0.35m;

    public decimal Value { get; }

    private CommissionRate(decimal value) => Value = value;

    public static CommissionRate Of(decimal rate)
    {
        if (rate is <= 0m || rate > MaxRate)
            throw new DomainException(ErrorCodes.CommissionRateOutOfRange,
                $"Taxa de comissão deve ser maior que zero e no máximo {MaxRate:P0}.");

        return new CommissionRate(decimal.Round(rate, 4, MidpointRounding.ToEven));
    }

    public Percentage AsPercentage() => Percentage.Of(Value);

    public Money AppliedTo(Money baseAmount) => baseAmount.MultiplyBy(AsPercentage());

    public override string ToString() => $"{Value * 100m:0.##}%";
}
