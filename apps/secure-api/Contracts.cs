using System.ComponentModel.DataAnnotations;

namespace PortalDoCorretor.SecureApi;

/// <summary>
/// Página de resultados com cursor implícito por offset.
/// </summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int PageNumber, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
    public bool HasNext => PageNumber < TotalPages;
    public bool HasPrevious => PageNumber > 1;
}

/// <summary>
/// Entrada de cadastro/edição de cliente.
/// </summary>
/// <remarks>
/// <b>Não existe propriedade TenantId aqui, e isso é deliberado.</b> O tenant vem do contexto
/// da requisição, nunca do corpo. Um DTO que aceitasse tenant seria a porta de entrada para
/// mass assignment: bastaria o cliente enviar o identificador de outra corretora.
/// O mesmo vale para <c>Id</c>, <c>CreatedAt</c> e os campos de exclusão lógica.
/// </remarks>
public sealed class CustomerInput
{
    [Required(ErrorMessage = "Tipo de cliente é obrigatório.")]
    [RegularExpression("^(INDIVIDUAL|BUSINESS)$", ErrorMessage = "Tipo deve ser INDIVIDUAL ou BUSINESS.")]
    public string Kind { get; init; } = "INDIVIDUAL";

    [Required(ErrorMessage = "Corretor responsável é obrigatório.")]
    public Guid BrokerId { get; init; }

    [Required(ErrorMessage = "Documento é obrigatório.")]
    public string Document { get; init; } = string.Empty;

    // Pessoa física
    [StringLength(80, MinimumLength = 2, ErrorMessage = "Nome deve ter entre 2 e 80 caracteres.")]
    public string? FirstName { get; init; }

    [StringLength(120, MinimumLength = 2, ErrorMessage = "Sobrenome deve ter entre 2 e 120 caracteres.")]
    public string? LastName { get; init; }

    public DateOnly? BirthDate { get; init; }

    [StringLength(120)]
    public string? Occupation { get; init; }

    // Pessoa jurídica
    [StringLength(180, MinimumLength = 2, ErrorMessage = "Razão social deve ter entre 2 e 180 caracteres.")]
    public string? LegalName { get; init; }

    [StringLength(180)]
    public string? TradeName { get; init; }

    [StringLength(10)]
    public string? CnaeCode { get; init; }

    [RegularExpression("^(MICRO|SMALL|MEDIUM|LARGE)$", ErrorMessage = "Porte inválido.")]
    public string? CompanySize { get; init; }

    // Contato principal — o agregado exige ao menos um contato ativo
    [EmailAddress(ErrorMessage = "E-mail inválido.")]
    public string? Email { get; init; }

    public string? Phone { get; init; }
}

/// <summary>
/// Entrada de edição. Contrato separado do cadastro, e não o mesmo com campos opcionais.
/// </summary>
/// <remarks>
/// <b>Não há Document nem Kind aqui, e isso é uma decisão de domínio.</b> Alterar o documento
/// ou o tipo mudaria a identidade do cliente e invalidaria o histórico de apólices já emitidas
/// em seu nome. Reaproveitar o contrato de cadastro obrigaria a marcar o documento como
/// opcional, o que enfraqueceria a validação na criação — onde ele é obrigatório de verdade.
/// </remarks>
public sealed class CustomerUpdateInput
{
    [Required(ErrorMessage = "Corretor responsável é obrigatório.")]
    public Guid BrokerId { get; init; }

    [StringLength(80, MinimumLength = 2, ErrorMessage = "Nome deve ter entre 2 e 80 caracteres.")]
    public string? FirstName { get; init; }

    [StringLength(120, MinimumLength = 2, ErrorMessage = "Sobrenome deve ter entre 2 e 120 caracteres.")]
    public string? LastName { get; init; }

    public DateOnly? BirthDate { get; init; }

    [StringLength(120)]
    public string? Occupation { get; init; }

    [StringLength(180, MinimumLength = 2, ErrorMessage = "Razão social deve ter entre 2 e 180 caracteres.")]
    public string? LegalName { get; init; }

    [StringLength(180)]
    public string? TradeName { get; init; }

    [StringLength(10)]
    public string? CnaeCode { get; init; }

    [RegularExpression("^(MICRO|SMALL|MEDIUM|LARGE)$", ErrorMessage = "Porte inválido.")]
    public string? CompanySize { get; init; }

    [EmailAddress(ErrorMessage = "E-mail inválido.")]
    public string? Email { get; init; }

    public string? Phone { get; init; }
}

/// <summary>Entrada de exclusão lógica — o motivo é obrigatório e vai para a auditoria.</summary>
public sealed class DeletionInput
{
    [Required(ErrorMessage = "Motivo da exclusão é obrigatório.")]
    [StringLength(300, MinimumLength = 5, ErrorMessage = "Motivo deve ter ao menos 5 caracteres.")]
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Resposta de erro no formato Problem Details, com erros por campo.</summary>
public sealed record ValidationProblem(string Title, IDictionary<string, string[]> Errors)
{
    public string Type => "https://tools.ietf.org/html/rfc9110#section-15.5.1";
    public int Status => StatusCodes.Status422UnprocessableEntity;
}
