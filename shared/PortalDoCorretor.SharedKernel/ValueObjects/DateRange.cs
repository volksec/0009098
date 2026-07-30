using PortalDoCorretor.SharedKernel.Errors;

namespace PortalDoCorretor.SharedKernel.ValueObjects;

/// <summary>
/// Intervalo de datas semiaberto <c>[Start, End)</c> — o dia final não está contido.
/// </summary>
/// <remarks>
/// A escolha do intervalo semiaberto não é estilística: é o que permite mapear direto para
/// <c>daterange</c> do PostgreSQL e usar a constraint de exclusão
/// <c>EXCLUDE USING gist (... coverage_period WITH &amp;&amp;)</c>, que impede sobreposição de
/// vigências para o mesmo bem. A mesma regra de <see cref="Overlaps"/> passa a ser garantida
/// também pelo banco — defesa em profundidade aplicada a integridade.
/// </remarks>
public readonly record struct DateRange
{
    public DateOnly Start { get; }
    public DateOnly End { get; }

    private DateRange(DateOnly start, DateOnly end)
    {
        Start = start;
        End = end;
    }

    public static DateRange Of(DateOnly start, DateOnly end)
    {
        if (end <= start)
            throw new DomainException(ErrorCodes.DateRangeInvalid,
                "A data final deve ser posterior à data inicial.");

        return new DateRange(start, end);
    }

    public static DateRange OfMonths(DateOnly start, int months)
    {
        if (months < 1)
            throw new DomainException(ErrorCodes.DateRangeInvalid,
                "A quantidade de meses deve ser positiva.");

        return Of(start, start.AddMonths(months));
    }

    public static DateRange OfYear(DateOnly start) => OfMonths(start, 12);

    public bool Contains(DateOnly date) => date >= Start && date < End;

    public bool Overlaps(DateRange other) => Start < other.End && other.Start < End;

    public int DurationInDays => End.DayNumber - Start.DayNumber;

    /// <summary>Apólice vencendo dentro da janela — usado pelo Renewal Scanner.</summary>
    public bool IsExpiringWithin(DateOnly reference, int days) =>
        End > reference && End <= reference.AddDays(days);

    public bool HasExpiredBy(DateOnly reference) => End <= reference;

    public override string ToString() => $"[{Start:yyyy-MM-dd}, {End:yyyy-MM-dd})";
}
