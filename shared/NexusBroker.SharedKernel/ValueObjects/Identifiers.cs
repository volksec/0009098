using NexusBroker.SharedKernel.Errors;

namespace NexusBroker.SharedKernel.ValueObjects;

/// <summary>
/// Identificador da corretora — o tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Este é o VO mais importante para a segurança do sistema.</b> Não existe construtor
/// público que aceite entrada de usuário: a criação passa por <see cref="FromTrustedSource"/>,
/// que só é chamada pelo resolvedor de claims (a partir do token assinado) e pelo
/// materializador do ORM (a partir do banco).
/// </para>
/// <para>
/// A consequência é que um DTO de requisição <b>não consegue</b> produzir um TenantId válido.
/// Manipulação de tenant via payload fica impedida pelo sistema de tipos, não por validação
/// em runtime que alguém pode esquecer de chamar. É a primeira das cinco camadas de
/// isolamento descritas no ADR-0004.
/// </para>
/// </remarks>
public readonly record struct TenantId
{
    public Guid Value { get; }

    private TenantId(Guid value) => Value = value;

    /// <summary>
    /// Construção permitida SOMENTE a partir de claim autenticado ou de leitura do banco.
    /// Deliberadamente sem sobrecarga pública que aceite string vinda de requisição.
    /// </summary>
    public static TenantId FromTrustedSource(Guid value) =>
        value == Guid.Empty
            ? throw new DomainException(ErrorCodes.TenantIdInvalid, "TenantId não pode ser vazio.")
            : new TenantId(value);

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Correlaciona uma requisição de ponta a ponta: frontend → API → domínio → banco → worker.
/// UUID v7 para ser ordenável no tempo, o que torna a leitura do Live Processing Console
/// cronológica sem depender de ordenação por timestamp separado.
/// </summary>
public readonly record struct CorrelationId
{
    public Guid Value { get; }

    private CorrelationId(Guid value) => Value = value;

    public static CorrelationId New() => new(Guid.CreateVersion7());

    public static CorrelationId From(Guid value) =>
        value == Guid.Empty ? New() : new CorrelationId(value);

    public static bool TryParse(string? input, out CorrelationId correlationId)
    {
        if (Guid.TryParse(input, out var guid) && guid != Guid.Empty)
        {
            correlationId = new CorrelationId(guid);
            return true;
        }

        correlationId = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Chave de idempotência fornecida pelo cliente em comandos que criam recurso.
/// Junto com o índice único, é uma das três camadas que impedem emissão duplicada de apólice.
/// </summary>
public readonly record struct IdempotencyKey
{
    private const int MinLength = 8;
    private const int MaxLength = 64;

    public string Value { get; }

    private IdempotencyKey(string value) => Value = value;

    public static IdempotencyKey Parse(string? input)
    {
        var trimmed = input?.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed.Length is < MinLength or > MaxLength)
            throw new DomainException("IDEMPOTENCY_KEY_INVALID",
                $"Chave de idempotência deve ter entre {MinLength} e {MaxLength} caracteres.");

        if (!trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new DomainException("IDEMPOTENCY_KEY_INVALID",
                "Chave de idempotência aceita apenas letras, dígitos, hífen e sublinhado.");

        return new IdempotencyKey(trimmed);
    }

    public override string ToString() => Value;
}
