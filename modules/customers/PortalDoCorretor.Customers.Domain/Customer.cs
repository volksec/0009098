using PortalDoCorretor.SharedKernel.Domain;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Customers.Domain;

public enum CustomerStatus { Active, Inactive, Blocked }
public enum RiskCategory { Standard, Preferred, Elevated }

/// <summary>
/// Raiz do agregado Customer. Abstrata: um cliente é sempre pessoa física ou jurídica,
/// nunca "cliente genérico" — e o sistema de tipos impede a terceira possibilidade.
/// </summary>
/// <remarks>
/// Composição: contatos, endereços, consentimentos e bens têm o ciclo de vida atrelado
/// ao cliente. Todas as coleções são privadas e expostas como somente-leitura; a mutação
/// passa obrigatoriamente por métodos de intenção que verificam as invariantes.
/// </remarks>
public abstract class Customer : AggregateRoot<CustomerId>, ITenantScoped, ISoftDeletable
{
    private readonly List<Contact> _contacts = [];
    private readonly List<Address> _addresses = [];
    private readonly List<Consent> _consents = [];
    private readonly List<InsurableAsset> _assets = [];

    public TenantId TenantId { get; private set; }
    public BrokerId BrokerId { get; private set; }
    public DocumentNumber Document { get; private set; }
    public CustomerStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
    public string? DeletionReason { get; private set; }
    public Guid? DeletionBatchId { get; private set; }

    public IReadOnlyCollection<Contact> Contacts => _contacts.AsReadOnly();
    public IReadOnlyCollection<Address> Addresses => _addresses.AsReadOnly();
    public IReadOnlyCollection<Consent> Consents => _consents.AsReadOnly();
    public IReadOnlyCollection<InsurableAsset> Assets => _assets.AsReadOnly();

    /// <summary>Nome de exibição — resolvido polimorficamente por tipo de cliente.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Categoria de risco do titular, usada como fator na precificação.</summary>
    public abstract RiskCategory RiskCategory { get; }

    protected Customer() { }   // materialização pelo ORM

    protected Customer(CustomerId id, TenantId tenantId, BrokerId brokerId,
                       DocumentNumber document, IClock clock) : base(id)
    {
        TenantId = tenantId;
        BrokerId = brokerId;
        Document = document;
        Status = CustomerStatus.Active;
        CreatedAt = clock.UtcNow;
    }

    // ---------------------------------------------------------------- contatos

    public void AddContact(Contact contact)
    {
        EnsureActive();

        if (contact.IsPrimary && _contacts.Any(c => c.IsPrimary && c.Kind == contact.Kind))
            throw new DomainException(CustomerErrors.DuplicatePrimaryContact,
                $"Já existe contato principal do tipo {contact.Kind}.");

        _contacts.Add(contact);
        Raise(new CustomerUpdated(TenantId, Id, nameof(Contacts)));
    }

    public void RemoveContact(ContactId contactId)
    {
        var contact = _contacts.SingleOrDefault(c => c.Id == contactId)
            ?? throw new DomainException(CustomerErrors.ContactNotFound, "Contato não encontrado.");

        // Invariante: cliente ativo mantém ao menos um contato — senão fica inalcançável
        if (_contacts.Count == 1 && Status == CustomerStatus.Active)
            throw new DomainException(CustomerErrors.LastContactRemoval,
                "Cliente ativo deve manter ao menos um contato.");

        _contacts.Remove(contact);
        Raise(new CustomerUpdated(TenantId, Id, nameof(Contacts)));
    }

    // ---------------------------------------------------------------- endereços

    public void AddAddress(Address address)
    {
        EnsureActive();

        if (address.IsPrimary && _addresses.Any(a => a.IsPrimary && a.Kind == address.Kind))
            throw new DomainException(CustomerErrors.DuplicatePrimaryAddress,
                $"Já existe endereço principal do tipo {address.Kind}.");

        _addresses.Add(address);
        Raise(new CustomerUpdated(TenantId, Id, nameof(Addresses)));
    }

    // ---------------------------------------------------------------- consentimentos

    /// <summary>
    /// Registra consentimento. A coleção é <b>append-only</b>: conceder novamente uma
    /// finalidade já vigente não sobrescreve nada, apenas acrescenta uma versão.
    /// </summary>
    public Consent GrantConsent(AccessPurpose purpose, LegalBasis basis,
                                string termsVersion, string channel, IClock clock)
    {
        EnsureActive();

        var consent = Consent.Grant(Id, purpose, basis, termsVersion, channel, clock);
        _consents.Add(consent);
        Raise(new ConsentGranted(TenantId, Id, purpose));
        return consent;
    }

    /// <summary>
    /// Revoga o consentimento vigente criando um <b>novo registro</b> com data de revogação.
    /// O registro original nunca é alterado nem removido — sem isso, seria impossível provar
    /// o que o titular consentiu numa data passada.
    /// </summary>
    public void RevokeConsent(AccessPurpose purpose, IClock clock)
    {
        var current = CurrentConsentFor(purpose)
            ?? throw new DomainException(CustomerErrors.ConsentNotFound,
                   "Não há consentimento vigente para esta finalidade.");

        _consents.Add(current.RevokeAsNewRecord(clock));
        Raise(new ConsentRevoked(TenantId, Id, purpose));
    }

    /// <summary>Consentimento vigente é a versão mais recente daquela finalidade.</summary>
    public Consent? CurrentConsentFor(AccessPurpose purpose)
    {
        var latest = _consents
            .Where(c => c.Purpose == purpose)
            .OrderByDescending(c => c.RecordedAt)
            .FirstOrDefault();

        return latest is { IsActive: true } ? latest : null;
    }

    public bool HasActiveConsentFor(AccessPurpose purpose) => CurrentConsentFor(purpose) is not null;

    // ---------------------------------------------------------------- bens seguráveis

    public void AddAsset(InsurableAsset asset)
    {
        EnsureActive();

        if (asset.TenantId != TenantId)
            throw new DomainException(CustomerErrors.TenantMismatch,
                "Bem segurável pertence a outro tenant.");

        if (_assets.Any(a => a.IsSameAs(asset)))
            throw new DomainException(CustomerErrors.DuplicateAsset,
                "Bem segurável já cadastrado para este cliente.");

        _assets.Add(asset);
        Raise(new AssetRegistered(TenantId, Id, asset.Id, asset.Kind));
    }

    // ---------------------------------------------------------------- ciclo de vida

    /// <summary>
    /// Exclusão lógica. O chamador é responsável por verificar, via serviço de domínio,
    /// que não existe apólice vigente — essa checagem cruza a fronteira com o agregado
    /// Policy e por isso não cabe aqui dentro.
    /// </summary>
    public void SoftDelete(Guid deletedBy, string reason, Guid batchId, IClock clock)
    {
        if (IsDeleted)
            throw new DomainException(CustomerErrors.AlreadyDeleted, "Cliente já excluído.");

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
            throw new DomainException(CustomerErrors.DeletionReasonRequired,
                "Motivo da exclusão é obrigatório.");

        DeletedAt = clock.UtcNow;
        DeletedBy = deletedBy;
        DeletionReason = reason.Trim();
        DeletionBatchId = batchId;
        Status = CustomerStatus.Inactive;

        // Cascata LÓGICA aplicada pelo agregado — não ON DELETE CASCADE físico
        foreach (var contact in _contacts) contact.SoftDelete(clock);
        foreach (var address in _addresses) address.SoftDelete(clock);
        foreach (var asset in _assets) asset.SoftDelete(deletedBy, reason, batchId, clock);

        Raise(new CustomerDeleted(TenantId, Id, reason, batchId));
    }

    public void Restore()
    {
        if (!IsDeleted)
            throw new DomainException(CustomerErrors.NotDeleted, "Cliente não está excluído.");

        DeletedAt = null;
        DeletedBy = null;
        DeletionReason = null;
        Status = CustomerStatus.Active;

        // Restaura apenas o que foi excluído no MESMO lote: filhos apagados antes,
        // por decisão independente, continuam apagados
        foreach (var asset in _assets.Where(a => a.DeletionBatchId == DeletionBatchId))
            asset.Restore();

        DeletionBatchId = null;
        Raise(new CustomerRestored(TenantId, Id));
    }

    public bool IsDeleted => DeletedAt is not null;

    public void Block(string reason)
    {
        if (Status == CustomerStatus.Blocked) return;
        Status = CustomerStatus.Blocked;
        Raise(new CustomerBlocked(TenantId, Id, reason));
    }

    private void EnsureActive()
    {
        if (IsDeleted)
            throw new DomainException(CustomerErrors.CustomerDeleted,
                "Cliente excluído não pode ser alterado.");

        if (Status == CustomerStatus.Blocked)
            throw new DomainException(CustomerErrors.CustomerBlocked,
                "Cliente bloqueado não pode ser alterado.");
    }
}

/// <summary>Pessoa física.</summary>
public sealed class IndividualCustomer : Customer
{
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public DateOnly BirthDate { get; private set; }
    public string? Occupation { get; private set; }

    private IndividualCustomer() { }

    private IndividualCustomer(CustomerId id, TenantId tenantId, BrokerId brokerId,
                               DocumentNumber document, string firstName, string lastName,
                               DateOnly birthDate, string? occupation, IClock clock)
        : base(id, tenantId, brokerId, document, clock)
    {
        FirstName = firstName;
        LastName = lastName;
        BirthDate = birthDate;
        Occupation = occupation;
    }

    public static IndividualCustomer Register(
        TenantId tenantId, BrokerId brokerId, DocumentNumber document,
        string? firstName, string? lastName, DateOnly birthDate,
        string? occupation, Contact primaryContact, IClock clock)
    {
        if (document.Kind is not DocumentKind.Cpf)
            throw new DomainException(CustomerErrors.DocumentKindMismatch,
                "Pessoa física exige CPF.");

        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
            throw new DomainException(CustomerErrors.NameRequired,
                "Nome e sobrenome são obrigatórios.");

        if (birthDate >= clock.Today)
            throw new DomainException(CustomerErrors.BirthDateInvalid,
                "Data de nascimento deve ser passada.");

        var age = AgeAt(birthDate, clock.Today);
        if (age < 18)
            throw new DomainException(CustomerErrors.CustomerUnderage,
                "Titular deve ser maior de 18 anos.");

        if (age > 120)
            throw new DomainException(CustomerErrors.BirthDateInvalid,
                "Data de nascimento implausível.");

        var customer = new IndividualCustomer(
            CustomerId.New(), tenantId, brokerId, document,
            firstName.Trim(), lastName.Trim(), birthDate, occupation?.Trim(), clock);

        customer.AddContact(primaryContact);
        customer.Raise(new CustomerRegistered(tenantId, customer.Id, CustomerKind.Individual));
        return customer;
    }

    public override string DisplayName => $"{FirstName} {LastName}";

    /// <summary>Faixa etária como proxy de risco — regra do domínio, não do banco.</summary>
    public override RiskCategory RiskCategory => AgeAt(BirthDate, DateOnly.FromDateTime(DateTime.UtcNow)) switch
    {
        < 25 => RiskCategory.Elevated,
        >= 25 and < 60 => RiskCategory.Standard,
        _ => RiskCategory.Preferred
    };

    public int AgeOn(DateOnly reference) => AgeAt(BirthDate, reference);

    private static int AgeAt(DateOnly birthDate, DateOnly reference)
    {
        var age = reference.Year - birthDate.Year;
        if (birthDate.AddYears(age) > reference) age--;
        return age;
    }
}

/// <summary>Pessoa jurídica.</summary>
public sealed class BusinessCustomer : Customer
{
    public string LegalName { get; private set; } = string.Empty;
    public string? TradeName { get; private set; }
    public string CnaeCode { get; private set; } = string.Empty;
    public CompanySize Size { get; private set; }

    private BusinessCustomer() { }

    private BusinessCustomer(CustomerId id, TenantId tenantId, BrokerId brokerId,
                             DocumentNumber document, string legalName, string? tradeName,
                             string cnaeCode, CompanySize size, IClock clock)
        : base(id, tenantId, brokerId, document, clock)
    {
        LegalName = legalName;
        TradeName = tradeName;
        CnaeCode = cnaeCode;
        Size = size;
    }

    public static BusinessCustomer Register(
        TenantId tenantId, BrokerId brokerId, DocumentNumber document,
        string? legalName, string? tradeName, string? cnaeCode,
        CompanySize size, Contact primaryContact, IClock clock)
    {
        if (document.Kind is not DocumentKind.Cnpj)
            throw new DomainException(CustomerErrors.DocumentKindMismatch,
                "Pessoa jurídica exige CNPJ.");

        if (string.IsNullOrWhiteSpace(legalName))
            throw new DomainException(CustomerErrors.NameRequired,
                "Razão social é obrigatória.");

        if (string.IsNullOrWhiteSpace(cnaeCode))
            throw new DomainException(CustomerErrors.CnaeRequired,
                "CNAE é obrigatório para pessoa jurídica.");

        var customer = new BusinessCustomer(
            CustomerId.New(), tenantId, brokerId, document,
            legalName.Trim(), tradeName?.Trim(), cnaeCode.Trim(), size, clock);

        customer.AddContact(primaryContact);
        customer.Raise(new CustomerRegistered(tenantId, customer.Id, CustomerKind.Business));
        return customer;
    }

    public override string DisplayName => TradeName ?? LegalName;

    public override RiskCategory RiskCategory => Size switch
    {
        CompanySize.Micro or CompanySize.Small => RiskCategory.Elevated,
        CompanySize.Medium => RiskCategory.Standard,
        _ => RiskCategory.Preferred
    };
}

public enum CustomerKind { Individual, Business }
public enum CompanySize { Micro, Small, Medium, Large }

public static class CustomerErrors
{
    public const string DocumentKindMismatch = "CUSTOMER_DOCUMENT_KIND_MISMATCH";
    public const string NameRequired = "CUSTOMER_NAME_REQUIRED";
    public const string CnaeRequired = "CUSTOMER_CNAE_REQUIRED";
    public const string BirthDateInvalid = "CUSTOMER_BIRTH_DATE_INVALID";
    public const string CustomerUnderage = "CUSTOMER_UNDERAGE";
    public const string DuplicatePrimaryContact = "CUSTOMER_DUPLICATE_PRIMARY_CONTACT";
    public const string DuplicatePrimaryAddress = "CUSTOMER_DUPLICATE_PRIMARY_ADDRESS";
    public const string ContactNotFound = "CUSTOMER_CONTACT_NOT_FOUND";
    public const string LastContactRemoval = "CUSTOMER_LAST_CONTACT_REMOVAL";
    public const string ConsentNotFound = "CUSTOMER_CONSENT_NOT_FOUND";
    public const string DuplicateAsset = "CUSTOMER_DUPLICATE_ASSET";
    public const string TenantMismatch = "CUSTOMER_TENANT_MISMATCH";
    public const string CustomerDeleted = "CUSTOMER_DELETED";
    public const string CustomerBlocked = "CUSTOMER_BLOCKED";
    public const string AlreadyDeleted = "CUSTOMER_ALREADY_DELETED";
    public const string NotDeleted = "CUSTOMER_NOT_DELETED";
    public const string DeletionReasonRequired = "CUSTOMER_DELETION_REASON_REQUIRED";

    // Contato e consentimento
    public const string ContactWithoutChannel = "CONTACT_WITHOUT_CHANNEL";
    public const string ConsentTermsRequired = "CONSENT_TERMS_VERSION_REQUIRED";
    public const string ConsentChannelRequired = "CONSENT_CHANNEL_REQUIRED";
    public const string ConsentAlreadyRevoked = "CONSENT_ALREADY_REVOKED";

    // Bens seguráveis
    public const string AssetValueInvalid = "ASSET_VALUE_INVALID";
    public const string PlateInvalid = "VEHICLE_PLATE_INVALID";
    public const string ChassisInvalid = "VEHICLE_CHASSIS_INVALID";
    public const string VehicleYearInvalid = "VEHICLE_YEAR_INVALID";
    public const string VehicleDescriptionRequired = "VEHICLE_DESCRIPTION_REQUIRED";
    public const string PropertyAreaInvalid = "PROPERTY_AREA_INVALID";
    public const string PropertyYearInvalid = "PROPERTY_YEAR_INVALID";
}
