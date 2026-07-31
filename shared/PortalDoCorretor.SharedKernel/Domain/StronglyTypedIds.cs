namespace PortalDoCorretor.SharedKernel.Domain;

/// <summary>
/// Identificadores tipados por entidade.
/// </summary>
/// <remarks>
/// Existem para eliminar uma classe inteira de bug: com <c>Guid</c> em toda parte,
/// <c>FindPolicy(customerId)</c> compila. Com tipos distintos, não compila.
/// Também é o que torna honesta a regra de que agregados referenciam uns aos outros
/// por identidade, e nunca por navegação de objeto.
/// </remarks>
public readonly record struct CustomerId(Guid Value)
{
    public static CustomerId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct BrokerId(Guid Value)
{
    public static BrokerId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct BrokerageId(Guid Value)
{
    public static BrokerageId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct AssetId(Guid Value)
{
    public static AssetId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct ContactId(Guid Value)
{
    public static ContactId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct AddressId(Guid Value)
{
    public static AddressId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct ConsentId(Guid Value)
{
    public static ConsentId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct QuotationId(Guid Value)
{
    public static QuotationId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct ProposalId(Guid Value)
{
    public static ProposalId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct PolicyId(Guid Value)
{
    public static PolicyId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct CoverageId(Guid Value)
{
    public static CoverageId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct ProductVersionId(Guid Value)
{
    public static ProductVersionId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct PendencyId(Guid Value)
{
    public static PendencyId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}
