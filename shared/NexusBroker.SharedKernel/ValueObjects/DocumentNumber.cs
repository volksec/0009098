using System.Security.Cryptography;
using System.Text;
using NexusBroker.SharedKernel.Errors;

namespace NexusBroker.SharedKernel.ValueObjects;

public enum DocumentKind
{
    Cpf,
    Cnpj
}

/// <summary>
/// CPF ou CNPJ com validação real de dígito verificador.
/// </summary>
/// <remarks>
/// Três decisões deliberadas de segurança:
/// 1. A exceção nunca inclui o valor recebido — mensagens que ecoam o dado são vetor de
///    vazamento de dado pessoal em log agregado.
/// 2. <see cref="ToString"/> retorna a versão MASCARADA. Se alguém interpolar o objeto em
///    um log por descuido, o mascaramento é o comportamento padrão, não a exceção.
/// 3. A busca e a unicidade usam <see cref="SearchHash"/> com pepper mantido fora do banco:
///    o espaço de CPFs é pequeno o bastante para força bruta, então um hash sem pepper
///    vazado junto com o dump permitiria reverter os documentos.
/// </remarks>
public readonly record struct DocumentNumber
{
    public string Value { get; }
    public DocumentKind Kind { get; }

    private DocumentNumber(string value, DocumentKind kind)
    {
        Value = value;
        Kind = kind;
    }

    public static DocumentNumber Parse(string? input)
    {
        var digits = OnlyDigits(input);

        return digits.Length switch
        {
            11 when IsValidCpf(digits) => new DocumentNumber(digits, DocumentKind.Cpf),
            14 when IsValidCnpj(digits) => new DocumentNumber(digits, DocumentKind.Cnpj),
            _ => throw new DomainException(ErrorCodes.DocumentInvalid, "Documento inválido.")
        };
    }

    public static bool TryParse(string? input, out DocumentNumber document)
    {
        try
        {
            document = Parse(input);
            return true;
        }
        catch (DomainException)
        {
            document = default;
            return false;
        }
    }

    /// <summary>Versão mascarada, usada para exibição ao perfil regulatório e em logs.</summary>
    public string Masked => Kind == DocumentKind.Cpf
        ? $"***.***.{Value[6..9]}-**"
        : $"**.***.{Value[5..8]}/****-**";

    /// <summary>Versão formatada. Exige decisão explícita de exibir o dado completo.</summary>
    public string Formatted => Kind == DocumentKind.Cpf
        ? $"{Value[..3]}.{Value[3..6]}.{Value[6..9]}-{Value[9..]}"
        : $"{Value[..2]}.{Value[2..5]}.{Value[5..8]}/{Value[8..12]}-{Value[12..]}";

    /// <summary>
    /// Hash determinístico com pepper, usado como chave de busca e de unicidade.
    /// Permite localizar por documento sem manter o valor em claro em índice.
    /// </summary>
    public byte[] SearchHash(ReadOnlySpan<byte> pepper) =>
        HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(Value));

    private static string OnlyDigits(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var buffer = new StringBuilder(input.Length);
        foreach (var c in input)
            if (char.IsAsciiDigit(c))
                buffer.Append(c);

        return buffer.ToString();
    }

    private static bool IsValidCpf(string d)
    {
        // Sequências repetidas (000..., 111...) passam no cálculo do DV mas não são CPFs válidos
        if (d.All(c => c == d[0])) return false;

        var first = CheckDigit(d, 9, 10);
        var second = CheckDigit(d, 10, 11);

        return d[9] == first && d[10] == second;

        static char CheckDigit(string digits, int length, int startWeight)
        {
            var sum = 0;
            for (var i = 0; i < length; i++)
                sum += (digits[i] - '0') * (startWeight - i);

            var remainder = sum % 11;
            return (char)('0' + (remainder < 2 ? 0 : 11 - remainder));
        }
    }

    private static bool IsValidCnpj(string d)
    {
        if (d.All(c => c == d[0])) return false;

        int[] firstWeights = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        int[] secondWeights = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

        var first = CheckDigit(d, firstWeights);
        var second = CheckDigit(d, secondWeights);

        return d[12] == first && d[13] == second;

        static char CheckDigit(string digits, int[] weights)
        {
            var sum = 0;
            for (var i = 0; i < weights.Length; i++)
                sum += (digits[i] - '0') * weights[i];

            var remainder = sum % 11;
            return (char)('0' + (remainder < 2 ? 0 : 11 - remainder));
        }
    }

    /// <summary>Retorna a forma MASCARADA — segurança por padrão em interpolação acidental.</summary>
    public override string ToString() => Masked;
}
