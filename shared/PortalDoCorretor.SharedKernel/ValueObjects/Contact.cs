using System.Text;
using PortalDoCorretor.SharedKernel.Errors;

namespace PortalDoCorretor.SharedKernel.ValueObjects;

/// <summary>
/// Endereço de e-mail normalizado para minúsculas — a normalização acontece na construção,
/// então a igualdade por valor funciona sem comparação case-insensitive espalhada pelo código.
/// Persistido como <c>citext</c>, o que replica a mesma semântica no banco.
/// </summary>
public readonly record struct EmailAddress
{
    private const int MaxLength = 254; // RFC 5321

    public string Value { get; }

    private EmailAddress(string value) => Value = value;

    public static EmailAddress Parse(string? input)
    {
        var normalized = input?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(normalized) || normalized.Length > MaxLength)
            throw new DomainException(ErrorCodes.EmailInvalid, "Endereço de e-mail inválido.");

        var at = normalized.IndexOf('@', StringComparison.Ordinal);

        // Exatamente um '@', com parte local e domínio não vazios
        if (at <= 0 || at != normalized.LastIndexOf('@') || at == normalized.Length - 1)
            throw new DomainException(ErrorCodes.EmailInvalid, "Endereço de e-mail inválido.");

        var domain = normalized[(at + 1)..];

        // Domínio precisa de ao menos um ponto, sem pontos nas pontas nem consecutivos
        if (!domain.Contains('.', StringComparison.Ordinal)
            || domain.StartsWith('.') || domain.EndsWith('.')
            || domain.Contains("..", StringComparison.Ordinal)
            || normalized.Contains(' ', StringComparison.Ordinal))
            throw new DomainException(ErrorCodes.EmailInvalid, "Endereço de e-mail inválido.");

        return new EmailAddress(normalized);
    }

    public static bool TryParse(string? input, out EmailAddress email)
    {
        try
        {
            email = Parse(input);
            return true;
        }
        catch (DomainException)
        {
            email = default;
            return false;
        }
    }

    public string Domain => Value[(Value.IndexOf('@', StringComparison.Ordinal) + 1)..];

    /// <summary>Mascarado para exibição ao perfil regulatório e para logs.</summary>
    public string Masked
    {
        get
        {
            var at = Value.IndexOf('@', StringComparison.Ordinal);
            var local = Value[..at];
            var visible = local.Length <= 2 ? local[..1] : local[..2];
            return $"{visible}{new string('*', Math.Max(3, local.Length - visible.Length))}@{Domain}";
        }
    }

    public override string ToString() => Masked;
}

/// <summary>
/// Telefone brasileiro: DDD válido (11–99) + 8 ou 9 dígitos. Celular de 9 dígitos precisa
/// começar com 9, que é a regra vigente da numeração móvel nacional.
/// </summary>
public readonly record struct PhoneNumber
{
    public string Value { get; }

    private PhoneNumber(string value) => Value = value;

    public static PhoneNumber Parse(string? input)
    {
        var digits = OnlyDigits(input);

        // Tolera o prefixo internacional do Brasil
        if (digits.Length is 12 or 13 && digits.StartsWith("55", StringComparison.Ordinal))
            digits = digits[2..];

        if (digits.Length is not (10 or 11))
            throw new DomainException(ErrorCodes.PhoneInvalid, "Número de telefone inválido.");

        var areaCode = int.Parse(digits[..2]);
        if (areaCode is < 11 or > 99)
            throw new DomainException(ErrorCodes.PhoneInvalid, "DDD inválido.");

        if (digits.Length == 11 && digits[2] != '9')
            throw new DomainException(ErrorCodes.PhoneInvalid,
                "Número móvel de 9 dígitos deve iniciar com 9.");

        return new PhoneNumber(digits);
    }

    public bool IsMobile => Value.Length == 11;

    public string AreaCode => Value[..2];

    public string Formatted => IsMobile
        ? $"({AreaCode}) {Value[2..7]}-{Value[7..]}"
        : $"({AreaCode}) {Value[2..6]}-{Value[6..]}";

    public string Masked => $"({AreaCode}) *****-{Value[^4..]}";

    private static string OnlyDigits(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var buffer = new StringBuilder(input.Length);
        foreach (var c in input)
            if (char.IsAsciiDigit(c))
                buffer.Append(c);

        return buffer.ToString();
    }

    public override string ToString() => Masked;
}
