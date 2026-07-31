using PortalDoCorretor.SharedKernel.Domain;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Customers.Domain;

public enum ContactKind { Personal, Commercial, Emergency }
public enum AddressKind { Residential, Commercial, Billing }
public enum LegalBasis { Consent, Contract, LegalObligation, LegitimateInterest }

/// <summary>Contato do cliente. Exige ao menos um canal — contato sem canal é registro morto.</summary>
public sealed class Contact : Entity<ContactId>
{
    public CustomerId CustomerId { get; private set; }
    public ContactKind Kind { get; private set; }
    public EmailAddress? Email { get; private set; }
    public PhoneNumber? Phone { get; private set; }
    public bool IsPrimary { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    private Contact() { }

    private Contact(ContactId id, ContactKind kind, EmailAddress? email,
                    PhoneNumber? phone, bool isPrimary) : base(id)
    {
        Kind = kind;
        Email = email;
        Phone = phone;
        IsPrimary = isPrimary;
    }

    public static Contact Create(ContactKind kind, EmailAddress? email,
                                 PhoneNumber? phone, bool isPrimary = false)
    {
        if (email is null && phone is null)
            throw new DomainException(CustomerErrors.ContactWithoutChannel,
                "Contato exige ao menos e-mail ou telefone.");

        return new Contact(ContactId.New(), kind, email, phone, isPrimary);
    }

    public void SoftDelete(IClock clock) => DeletedAt ??= clock.UtcNow;

    /// <summary>Representação segura: os canais já vêm mascarados dos próprios VOs.</summary>
    public override string ToString() =>
        $"{Kind}: {Email?.Masked ?? Phone?.Masked ?? "-"}";
}

/// <summary>Endereço do cliente, com o Value Object <see cref="PostalAddress"/> encapsulado.</summary>
public sealed class Address : Entity<AddressId>
{
    public CustomerId CustomerId { get; private set; }
    public AddressKind Kind { get; private set; }
    public PostalAddress Value { get; private set; } = null!;
    public bool IsPrimary { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    private Address() { }

    private Address(AddressId id, AddressKind kind, PostalAddress value, bool isPrimary) : base(id)
    {
        Kind = kind;
        Value = value;
        IsPrimary = isPrimary;
    }

    public static Address Create(AddressKind kind, PostalAddress value, bool isPrimary = false) =>
        new(AddressId.New(), kind, value, isPrimary);

    public void SoftDelete(IClock clock) => DeletedAt ??= clock.UtcNow;
}

/// <summary>
/// Registro de consentimento LGPD. <b>Imutável</b>: não expõe nenhum método que altere o
/// estado existente. Revogar produz um novo registro via <see cref="RevokeAsNewRecord"/>.
/// </summary>
public sealed class Consent : Entity<ConsentId>
{
    public CustomerId CustomerId { get; private set; }
    public AccessPurpose Purpose { get; private set; }
    public LegalBasis Basis { get; private set; }
    public string TermsVersion { get; private set; } = string.Empty;
    public string Channel { get; private set; } = string.Empty;
    public DateTimeOffset GrantedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    private Consent() { }

    private Consent(ConsentId id, CustomerId customerId, AccessPurpose purpose, LegalBasis basis,
                    string termsVersion, string channel, DateTimeOffset grantedAt,
                    DateTimeOffset? revokedAt, DateTimeOffset recordedAt) : base(id)
    {
        CustomerId = customerId;
        Purpose = purpose;
        Basis = basis;
        TermsVersion = termsVersion;
        Channel = channel;
        GrantedAt = grantedAt;
        RevokedAt = revokedAt;
        RecordedAt = recordedAt;
    }

    public static Consent Grant(CustomerId customerId, AccessPurpose purpose, LegalBasis basis,
                                string? termsVersion, string? channel, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(termsVersion))
            throw new DomainException(CustomerErrors.ConsentTermsRequired,
                "Versão do termo é obrigatória.");

        if (string.IsNullOrWhiteSpace(channel))
            throw new DomainException(CustomerErrors.ConsentChannelRequired,
                "Canal de coleta é obrigatório.");

        var now = clock.UtcNow;
        return new Consent(ConsentId.New(), customerId, purpose, basis,
                           termsVersion.Trim(), channel.Trim(), now, null, now);
    }

    /// <summary>
    /// Produz um novo registro representando a revogação. O registro original permanece
    /// intacto — é o que permite responder "o que o titular consentiu em tal data".
    /// </summary>
    public Consent RevokeAsNewRecord(IClock clock)
    {
        if (RevokedAt is not null)
            throw new DomainException(CustomerErrors.ConsentAlreadyRevoked,
                "Consentimento já revogado.");

        var now = clock.UtcNow;
        return new Consent(ConsentId.New(), CustomerId, Purpose, Basis,
                           TermsVersion, Channel, GrantedAt, now, now);
    }

    public bool IsActive => RevokedAt is null;
}

/// <summary>
/// Bem segurável. Abstrata e polimórfica: o motor de precificação consome
/// <see cref="RiskFactors"/> sem conhecer o tipo concreto, então adicionar um novo tipo
/// de bem não exige alterar nenhum <c>switch</c> existente.
/// </summary>
public abstract class InsurableAsset : Entity<AssetId>, ITenantScoped, ISoftDeletable
{
    public TenantId TenantId { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public Money DeclaredValue { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
    public string? DeletionReason { get; private set; }
    public Guid? DeletionBatchId { get; private set; }

    public abstract AssetKind Kind { get; }
    public abstract bool IsCompatibleWith(InsuranceBranch branch);

    /// <summary>Fatores consumidos pelo cálculo de prêmio, sem acoplamento ao tipo concreto.</summary>
    public abstract IReadOnlyDictionary<string, decimal> RiskFactors(DateOnly reference);

    /// <summary>Identidade de negócio do bem — placa/chassi ou endereço, conforme o tipo.</summary>
    public abstract bool IsSameAs(InsurableAsset other);

    protected InsurableAsset() { }

    protected InsurableAsset(AssetId id, TenantId tenantId, CustomerId customerId,
                             Money declaredValue) : base(id)
    {
        if (!declaredValue.IsPositive)
            throw new DomainException(CustomerErrors.AssetValueInvalid,
                "Valor declarado deve ser positivo.");

        TenantId = tenantId;
        CustomerId = customerId;
        DeclaredValue = declaredValue;
    }

    public void SoftDelete(Guid deletedBy, string reason, Guid batchId, IClock clock)
    {
        if (DeletedAt is not null) return;
        DeletedAt = clock.UtcNow;
        DeletedBy = deletedBy;
        DeletionReason = reason;
        DeletionBatchId = batchId;
    }

    public void Restore()
    {
        DeletedAt = null;
        DeletedBy = null;
        DeletionReason = null;
        DeletionBatchId = null;
    }
}

public enum AssetKind { Vehicle, Property }
public enum InsuranceBranch { Auto, Residential }
public enum VehicleUsage { Personal, Commute, Commercial, Rideshare }
public enum ConstructionType { Masonry, Wood, Mixed, Steel }
public enum PropertyUsage { Residential, Commercial, Vacation }

public sealed class Vehicle : InsurableAsset
{
    public string Plate { get; private set; } = string.Empty;
    public string Chassis { get; private set; } = string.Empty;
    public int ModelYear { get; private set; }
    public int ManufactureYear { get; private set; }
    public string Brand { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public VehicleUsage Usage { get; private set; }
    public PostalCode OvernightLocation { get; private set; }
    public bool HasGarage { get; private set; }

    private Vehicle() { }

    private Vehicle(AssetId id, TenantId tenantId, CustomerId customerId, Money declaredValue,
                    string plate, string chassis, int modelYear, int manufactureYear,
                    string brand, string model, VehicleUsage usage,
                    PostalCode overnight, bool hasGarage)
        : base(id, tenantId, customerId, declaredValue)
    {
        Plate = plate;
        Chassis = chassis;
        ModelYear = modelYear;
        ManufactureYear = manufactureYear;
        Brand = brand;
        Model = model;
        Usage = usage;
        OvernightLocation = overnight;
        HasGarage = hasGarage;
    }

    public static Vehicle Register(
        TenantId tenantId, CustomerId customerId, Money declaredValue,
        string? plate, string? chassis, int modelYear, int manufactureYear,
        string? brand, string? model, VehicleUsage usage,
        PostalCode overnight, bool hasGarage, IClock clock)
    {
        var normalizedPlate = (plate ?? string.Empty).Trim().ToUpperInvariant().Replace("-", "");

        // Aceita o formato antigo (AAA1234) e o Mercosul (AAA1A23)
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                normalizedPlate, "^([A-Z]{3}[0-9]{4}|[A-Z]{3}[0-9][A-Z][0-9]{2})$"))
            throw new DomainException(CustomerErrors.PlateInvalid, "Placa inválida.");

        var normalizedChassis = (chassis ?? string.Empty).Trim().ToUpperInvariant();

        // O padrão VIN exclui I, O e Q para evitar confusão com 1 e 0
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedChassis, "^[A-HJ-NPR-Z0-9]{17}$"))
            throw new DomainException(CustomerErrors.ChassisInvalid, "Chassi inválido.");

        if (manufactureYear < 1950 || manufactureYear > clock.Today.Year + 1)
            throw new DomainException(CustomerErrors.VehicleYearInvalid,
                "Ano de fabricação implausível.");

        if (modelYear < manufactureYear || modelYear > manufactureYear + 1)
            throw new DomainException(CustomerErrors.VehicleYearInvalid,
                "Ano do modelo incompatível com o de fabricação.");

        if (string.IsNullOrWhiteSpace(brand) || string.IsNullOrWhiteSpace(model))
            throw new DomainException(CustomerErrors.VehicleDescriptionRequired,
                "Marca e modelo são obrigatórios.");

        return new Vehicle(AssetId.New(), tenantId, customerId, declaredValue,
                           normalizedPlate, normalizedChassis, modelYear, manufactureYear,
                           brand.Trim(), model.Trim(), usage, overnight, hasGarage);
    }

    public override AssetKind Kind => AssetKind.Vehicle;

    public override bool IsCompatibleWith(InsuranceBranch branch) => branch == InsuranceBranch.Auto;

    public override IReadOnlyDictionary<string, decimal> RiskFactors(DateOnly reference) =>
        new Dictionary<string, decimal>
        {
            ["vehicleAge"] = reference.Year - ModelYear,
            ["usage"] = Usage switch
            {
                VehicleUsage.Personal => 1.00m,
                VehicleUsage.Commute => 1.10m,
                VehicleUsage.Commercial => 1.35m,
                VehicleUsage.Rideshare => 1.60m,
                _ => 1.00m
            },
            ["region"] = OvernightLocation.Region,
            ["garage"] = HasGarage ? 0.90m : 1.00m,
            ["declaredValue"] = DeclaredValue.Amount
        };

    /// <summary>Chassi é o identificador único do veículo; placa muda, chassi não.</summary>
    public override bool IsSameAs(InsurableAsset other) =>
        other is Vehicle v && (v.Chassis == Chassis || v.Plate == Plate);
}

public sealed class Property : InsurableAsset
{
    public PostalAddress Location { get; private set; } = null!;
    public decimal AreaSqm { get; private set; }
    public int BuiltYear { get; private set; }
    public ConstructionType Construction { get; private set; }
    public PropertyUsage Usage { get; private set; }
    public bool HasAlarm { get; private set; }

    private Property() { }

    private Property(AssetId id, TenantId tenantId, CustomerId customerId, Money declaredValue,
                     PostalAddress location, decimal areaSqm, int builtYear,
                     ConstructionType construction, PropertyUsage usage, bool hasAlarm)
        : base(id, tenantId, customerId, declaredValue)
    {
        Location = location;
        AreaSqm = areaSqm;
        BuiltYear = builtYear;
        Construction = construction;
        Usage = usage;
        HasAlarm = hasAlarm;
    }

    public static Property Register(
        TenantId tenantId, CustomerId customerId, Money declaredValue,
        PostalAddress location, decimal areaSqm, int builtYear,
        ConstructionType construction, PropertyUsage usage, bool hasAlarm, IClock clock)
    {
        if (areaSqm is <= 0 or > 100_000)
            throw new DomainException(CustomerErrors.PropertyAreaInvalid, "Área implausível.");

        if (builtYear < 1900 || builtYear > clock.Today.Year + 1)
            throw new DomainException(CustomerErrors.PropertyYearInvalid,
                "Ano de construção implausível.");

        return new Property(AssetId.New(), tenantId, customerId, declaredValue,
                            location, areaSqm, builtYear, construction, usage, hasAlarm);
    }

    public override AssetKind Kind => AssetKind.Property;

    public override bool IsCompatibleWith(InsuranceBranch branch) =>
        branch == InsuranceBranch.Residential;

    public override IReadOnlyDictionary<string, decimal> RiskFactors(DateOnly reference) =>
        new Dictionary<string, decimal>
        {
            ["buildingAge"] = reference.Year - BuiltYear,
            ["construction"] = Construction switch
            {
                ConstructionType.Masonry => 1.00m,
                ConstructionType.Steel => 1.05m,
                ConstructionType.Mixed => 1.20m,
                ConstructionType.Wood => 1.45m,
                _ => 1.00m
            },
            ["area"] = AreaSqm,
            ["region"] = Location.RegionCode,
            ["alarm"] = HasAlarm ? 0.92m : 1.00m,
            ["declaredValue"] = DeclaredValue.Amount
        };

    /// <summary>Imóvel é identificado pelo endereço completo.</summary>
    public override bool IsSameAs(InsurableAsset other) =>
        other is Property p && p.Location == Location;
}
