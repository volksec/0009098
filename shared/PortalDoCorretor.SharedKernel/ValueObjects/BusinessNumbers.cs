using System.Text.RegularExpressions;
using PortalDoCorretor.SharedKernel.Errors;

namespace PortalDoCorretor.SharedKernel.ValueObjects;

/// <summary>Dígito verificador módulo 11, compartilhado pela numeração de negócio.</summary>
internal static class CheckDigit
{
    public static int Mod11(string payload)
    {
        var sum = 0;
        var weight = 2;

        for (var i = payload.Length - 1; i >= 0; i--)
        {
            sum += (payload[i] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }

        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }
}

/// <summary>
/// Número de apólice no formato <c>PC-AAAA-NNNNNNNN-D</c>.
/// </summary>
/// <remarks>
/// O dígito verificador não é enfeite: torna inválido um número adivinhado por incremento,
/// então tentativas de enumeração falham na validação <b>antes</b> de tocar o banco — e o
/// erro é registrado como evento de segurança de enumeração, em vez de virar uma consulta
/// que retorna vazio silenciosamente.
/// </remarks>
public readonly partial record struct PolicyNumber
{
    [GeneratedRegex(@"^PC-(?<year>\d{4})-(?<seq>\d{8})-(?<dv>\d)$")]
    private static partial Regex Pattern();

    public string Value { get; }

    private PolicyNumber(string value) => Value = value;

    public static PolicyNumber Parse(string? input)
    {
        var normalized = input?.Trim().ToUpperInvariant() ?? string.Empty;
        var match = Pattern().Match(normalized);

        if (!match.Success)
            throw new DomainException(ErrorCodes.PolicyNumberInvalid,
                "Número de apólice em formato inválido.");

        var payload = match.Groups["year"].Value + match.Groups["seq"].Value;

        if (CheckDigit.Mod11(payload) != match.Groups["dv"].Value[0] - '0')
            throw new DomainException(ErrorCodes.PolicyNumberCheckDigit,
                "Dígito verificador do número de apólice inválido.");

        return new PolicyNumber(normalized);
    }

    /// <summary>
    /// Gerado a partir de sequence do banco — a unicidade sob concorrência é garantida
    /// pelo PostgreSQL, não por contador em memória da aplicação.
    /// </summary>
    public static PolicyNumber Generate(int year, long sequence)
    {
        var payload = $"{year:D4}{sequence:D8}";
        return new PolicyNumber($"PC-{year:D4}-{sequence:D8}-{CheckDigit.Mod11(payload)}");
    }

    public int Year => int.Parse(Value[3..7]);

    public override string ToString() => Value;
}

/// <summary>Número de proposta no formato <c>PR-AAAA-NNNNNNNN-D</c>.</summary>
public readonly partial record struct ProposalNumber
{
    [GeneratedRegex(@"^PR-(?<year>\d{4})-(?<seq>\d{8})-(?<dv>\d)$")]
    private static partial Regex Pattern();

    public string Value { get; }

    private ProposalNumber(string value) => Value = value;

    public static ProposalNumber Parse(string? input)
    {
        var normalized = input?.Trim().ToUpperInvariant() ?? string.Empty;
        var match = Pattern().Match(normalized);

        if (!match.Success)
            throw new DomainException(ErrorCodes.ProposalNumberInvalid,
                "Número de proposta em formato inválido.");

        var payload = match.Groups["year"].Value + match.Groups["seq"].Value;

        if (CheckDigit.Mod11(payload) != match.Groups["dv"].Value[0] - '0')
            throw new DomainException(ErrorCodes.ProposalNumberInvalid,
                "Dígito verificador do número de proposta inválido.");

        return new ProposalNumber(normalized);
    }

    public static ProposalNumber Generate(int year, long sequence)
    {
        var payload = $"{year:D4}{sequence:D8}";
        return new ProposalNumber($"PR-{year:D4}-{sequence:D8}-{CheckDigit.Mod11(payload)}");
    }

    public override string ToString() => Value;
}

/// <summary>Número de cotação no formato <c>CT-AAAA-NNNNNNNN-D</c>.</summary>
public readonly partial record struct QuotationNumber
{
    [GeneratedRegex(@"^CT-(?<year>\d{4})-(?<seq>\d{8})-(?<dv>\d)$")]
    private static partial Regex Pattern();

    public string Value { get; }

    private QuotationNumber(string value) => Value = value;

    public static QuotationNumber Parse(string? input)
    {
        var normalized = input?.Trim().ToUpperInvariant() ?? string.Empty;
        var match = Pattern().Match(normalized);

        if (!match.Success)
            throw new DomainException(ErrorCodes.QuotationNumberInvalid,
                "Número de cotação em formato inválido.");

        var payload = match.Groups["year"].Value + match.Groups["seq"].Value;

        if (CheckDigit.Mod11(payload) != match.Groups["dv"].Value[0] - '0')
            throw new DomainException(ErrorCodes.QuotationNumberInvalid,
                "Dígito verificador do número de cotação inválido.");

        return new QuotationNumber(normalized);
    }

    public static QuotationNumber Generate(int year, long sequence)
    {
        var payload = $"{year:D4}{sequence:D8}";
        return new QuotationNumber($"CT-{year:D4}-{sequence:D8}-{CheckDigit.Mod11(payload)}");
    }

    public override string ToString() => Value;
}
