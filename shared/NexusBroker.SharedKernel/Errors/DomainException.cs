namespace NexusBroker.SharedKernel.Errors;

/// <summary>
/// Erro de regra de negócio. Carrega um código estável, adequado para tradução e para
/// correlação com testes, e uma mensagem já segura para exibição.
/// </summary>
/// <remarks>
/// A mensagem NUNCA deve conter o valor de entrada que causou a falha. Mensagens de erro
/// que ecoam o dado recebido são um vetor clássico de vazamento de dado pessoal em log
/// agregado — um <c>DocumentInvalid</c> que imprime o CPF recebido vaza o CPF.
/// </remarks>
public sealed class DomainException : Exception
{
    public string Code { get; }

    public DomainException(string code, string message) : base(message) => Code = code;
}

/// <summary>
/// Códigos de erro de domínio. Constantes em vez de strings livres para que o teste
/// verifique o código, não a redação da mensagem — a mensagem pode mudar, o contrato não.
/// </summary>
public static class ErrorCodes
{
    // Money
    public const string MoneyScaleInvalid = "MONEY_SCALE_INVALID";
    public const string MoneyOutOfRange = "MONEY_OUT_OF_RANGE";
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";
    public const string AllocationInvalid = "ALLOCATION_INVALID";

    // Percentage / taxas
    public const string PercentageOutOfRange = "PERCENTAGE_OUT_OF_RANGE";
    public const string CommissionRateOutOfRange = "COMMISSION_RATE_OUT_OF_RANGE";

    // Identificação
    public const string DocumentInvalid = "DOCUMENT_INVALID";
    public const string EmailInvalid = "EMAIL_INVALID";
    public const string PhoneInvalid = "PHONE_INVALID";
    public const string TenantIdInvalid = "TENANT_ID_INVALID";

    // Numeração de negócio
    public const string PolicyNumberInvalid = "POLICY_NUMBER_INVALID";
    public const string PolicyNumberCheckDigit = "POLICY_NUMBER_CHECK_DIGIT";
    public const string ProposalNumberInvalid = "PROPOSAL_NUMBER_INVALID";
    public const string QuotationNumberInvalid = "QUOTATION_NUMBER_INVALID";

    // Vigência e risco
    public const string DateRangeInvalid = "DATE_RANGE_INVALID";
    public const string RiskScoreOutOfRange = "RISK_SCORE_OUT_OF_RANGE";
    public const string CoverageLimitInvalid = "COVERAGE_LIMIT_INVALID";
    public const string DeductibleInvalid = "DEDUCTIBLE_INVALID";

    // Endereço
    public const string PostalCodeInvalid = "POSTAL_CODE_INVALID";
    public const string StateCodeInvalid = "STATE_CODE_INVALID";
    public const string AddressIncomplete = "ADDRESS_INCOMPLETE";
}
