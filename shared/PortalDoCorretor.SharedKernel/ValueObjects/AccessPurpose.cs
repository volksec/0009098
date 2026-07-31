namespace PortalDoCorretor.SharedKernel.ValueObjects;

/// <summary>
/// Finalidade de acesso ou de tratamento de dado pessoal.
/// </summary>
/// <remarks>
/// Conjunto <b>fechado</b> por decisão: finalidade em texto livre é o mesmo que não ter
/// finalidade, porque não é auditável nem comparável. Acrescentar um valor exige mudança
/// de código e migration, o que é exatamente a barreira desejada.
/// </remarks>
public enum AccessPurpose
{
    /// <summary>Contato comercial com o titular.</summary>
    CommercialContact,

    /// <summary>Elaboração de cotação.</summary>
    Quotation,

    /// <summary>Emissão e gestão de apólice.</summary>
    PolicyIssuance,

    /// <summary>Renovação de apólice.</summary>
    Renewal,

    /// <summary>Regulação de sinistro.</summary>
    ClaimHandling,

    /// <summary>Supervisão regulatória.</summary>
    RegulatorySupervision,

    /// <summary>Verificação de conformidade.</summary>
    ComplianceVerification,

    /// <summary>Investigação de inconsistência.</summary>
    InconsistencyInvestigation,

    /// <summary>Análise de indicador consolidado.</summary>
    IndicatorAnalysis
}

public static class AccessPurposeExtensions
{
    /// <summary>
    /// Finalidades disponíveis ao perfil de supervisão. As demais são operacionais e não
    /// podem ser declaradas por um supervisor — a separação impede que uma consulta de
    /// supervisão seja registrada sob uma finalidade de negócio.
    /// </summary>
    public static bool IsRegulatory(this AccessPurpose purpose) => purpose is
        AccessPurpose.RegulatorySupervision or
        AccessPurpose.ComplianceVerification or
        AccessPurpose.InconsistencyInvestigation or
        AccessPurpose.IndicatorAnalysis;

    public static bool IsOperational(this AccessPurpose purpose) => !purpose.IsRegulatory();
}
