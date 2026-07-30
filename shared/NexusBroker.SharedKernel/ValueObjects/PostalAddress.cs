using System.Text;
using NexusBroker.SharedKernel.Errors;

namespace NexusBroker.SharedKernel.ValueObjects;

/// <summary>CEP brasileiro: 8 dígitos.</summary>
public readonly record struct PostalCode
{
    public string Value { get; }

    private PostalCode(string value) => Value = value;

    public static PostalCode Parse(string? input)
    {
        var digits = OnlyDigits(input);

        if (digits.Length != 8)
            throw new DomainException(ErrorCodes.PostalCodeInvalid, "CEP inválido.");

        return new PostalCode(digits);
    }

    public string Formatted => $"{Value[..5]}-{Value[5..]}";

    /// <summary>
    /// Primeiro dígito do CEP: identifica a região postal, usada como fator de risco
    /// geográfico na precificação simulada.
    /// </summary>
    public int Region => Value[0] - '0';

    private static string OnlyDigits(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var buffer = new StringBuilder(input.Length);
        foreach (var c in input)
            if (char.IsAsciiDigit(c))
                buffer.Append(c);

        return buffer.ToString();
    }

    public override string ToString() => Formatted;
}

/// <summary>Unidade federativa. Conjunto fechado — validado contra a lista das 27 UFs.</summary>
public readonly record struct StateCode
{
    private static readonly string[] Valid =
    [
        "AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS", "MG",
        "PA", "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC", "SP", "SE", "TO"
    ];

    public string Value { get; }

    private StateCode(string value) => Value = value;

    public static StateCode Parse(string? input)
    {
        var normalized = input?.Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(normalized) || !Valid.Contains(normalized))
            throw new DomainException(ErrorCodes.StateCodeInvalid, "UF inválida.");

        return new StateCode(normalized);
    }

    public override string ToString() => Value;
}

/// <summary>
/// Endereço postal completo. Value Object multi-campo, persistido como o tipo composto
/// <c>postal_address</c> do PostgreSQL — a coesão do objeto sobrevive à persistência,
/// em vez de virar sete colunas soltas repetidas em cada tabela que precisa de endereço.
/// </summary>
public sealed record PostalAddress
{
    public string Street { get; }
    public string Number { get; }
    public string? Complement { get; }
    public string District { get; }
    public string City { get; }
    public StateCode State { get; }
    public PostalCode PostalCode { get; }

    private PostalAddress(
        string street, string number, string? complement,
        string district, string city, StateCode state, PostalCode postalCode)
    {
        Street = street;
        Number = number;
        Complement = complement;
        District = district;
        City = city;
        State = state;
        PostalCode = postalCode;
    }

    public static PostalAddress Of(
        string? street, string? number, string? complement,
        string? district, string? city, string? state, string? postalCode)
    {
        if (string.IsNullOrWhiteSpace(street) || string.IsNullOrWhiteSpace(number)
            || string.IsNullOrWhiteSpace(district) || string.IsNullOrWhiteSpace(city))
            throw new DomainException(ErrorCodes.AddressIncomplete,
                "Logradouro, número, bairro e cidade são obrigatórios.");

        return new PostalAddress(
            street.Trim(),
            number.Trim(),
            string.IsNullOrWhiteSpace(complement) ? null : complement.Trim(),
            district.Trim(),
            city.Trim(),
            StateCode.Parse(state),
            ValueObjects.PostalCode.Parse(postalCode));
    }

    /// <summary>Fator de risco geográfico derivado da região postal, usado na precificação.</summary>
    public int RegionCode => PostalCode.Region;

    public string SingleLine =>
        $"{Street}, {Number}{(Complement is null ? "" : $" - {Complement}")}, "
        + $"{District}, {City}/{State} - {PostalCode.Formatted}";

    /// <summary>Minimização para o perfil regulatório: só cidade, UF e região postal.</summary>
    public string Minimized => $"{City}/{State} - {PostalCode.Value[..5]}***";

    public override string ToString() => Minimized;
}
