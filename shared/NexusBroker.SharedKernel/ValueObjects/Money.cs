using NexusBroker.SharedKernel.Errors;

namespace NexusBroker.SharedKernel.ValueObjects;

public enum Currency
{
    BRL = 986
}

/// <summary>
/// Valor monetário com moeda. Escala fixa de 2 casas, arredondamento bancário
/// (<see cref="MidpointRounding.ToEven"/>). Operações entre moedas distintas são
/// proibidas por construção, não por convenção.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    private const decimal MaxAbsolute = 999_999_999.99m;

    public decimal Amount { get; }
    public Currency Currency { get; }

    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, Currency currency = Currency.BRL)
    {
        if (decimal.Round(amount, 2, MidpointRounding.ToEven) != amount)
            throw new DomainException(ErrorCodes.MoneyScaleInvalid,
                "Valor monetário admite no máximo 2 casas decimais.");

        if (Math.Abs(amount) > MaxAbsolute)
            throw new DomainException(ErrorCodes.MoneyOutOfRange,
                "Valor monetário fora da faixa suportada.");

        return new Money(amount, currency);
    }

    public static Money Zero(Currency currency = Currency.BRL) => new(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return Of(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return Of(Amount - other.Amount, Currency);
    }

    public Money MultiplyBy(Percentage percentage) =>
        new(decimal.Round(Amount * percentage.Value, 2, MidpointRounding.ToEven), Currency);

    public Money Negate() => new(-Amount, Currency);

    /// <summary>
    /// Divide em N parcelas sem perder centavos: opera em centavos inteiros e distribui o
    /// resíduo da divisão um centavo por parcela, até esgotá-lo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Existe porque a invariante "Σ parcelas = prêmio total" (RF-064) é verificada ao
    /// centavo. Divisão ingênua de R$ 1.000,00 em 3 produz 333,33 × 3 = 999,99 — um centavo
    /// perdido que, em produção, vira divergência contábil.
    /// </para>
    /// <para>
    /// A distribuição é um centavo por parcela (método do maior resto), e não "todo o resíduo
    /// na primeira": jogar o resíduo inteiro na primeira parcela mantém a soma correta, mas
    /// produz distorção visível quando o valor é pequeno e o parcelamento é longo —
    /// R$ 0,05 em 12 vezes viraria uma parcela de R$ 0,05 e onze de R$ 0,00. Com a
    /// distribuição, nenhuma parcela difere de outra em mais de um centavo, o que é a
    /// propriedade verificada pelo teste.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Money> Allocate(int parts)
    {
        if (parts < 1)
            throw new DomainException(ErrorCodes.AllocationInvalid,
                "Número de parcelas deve ser positivo.");

        var totalCents = (long)decimal.Round(Amount * 100m, 0, MidpointRounding.ToEven);
        var baseCents = totalCents / parts;
        var remainder = totalCents - (baseCents * parts);

        // Preserva o sinal: um estorno parcelado distribui centavos negativos
        var extraParts = (int)Math.Abs(remainder);
        var step = Math.Sign(remainder);

        var result = new Money[parts];
        for (var i = 0; i < parts; i++)
        {
            var cents = baseCents + (i < extraParts ? step : 0);
            result[i] = new Money(cents / 100m, Currency);
        }

        return result;
    }

    public bool IsPositive => Amount > 0m;
    public bool IsNegative => Amount < 0m;
    public bool IsZero => Amount == 0m;

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
            throw new DomainException(ErrorCodes.CurrencyMismatch,
                $"Operação entre moedas distintas: {Currency} e {other.Currency}.");
    }

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    public static bool operator <(Money a, Money b) => a.CompareTo(b) < 0;
    public static bool operator >(Money a, Money b) => a.CompareTo(b) > 0;
    public static bool operator <=(Money a, Money b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Money a, Money b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Currency} {Amount:N2}";
}
