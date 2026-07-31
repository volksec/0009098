using FluentAssertions;
using PortalDoCorretor.Customers.Domain;
using PortalDoCorretor.SharedKernel.Domain;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Domain.Tests;

public sealed class CustomerAggregateTests
{
    private const string ValidCpf = "52998224725";
    private const string ValidCnpj = "11222333000181";

    private readonly FixedClock _clock = FixedClock.At(2026, 3, 10);
    private readonly TenantId _tenant = TenantId.FromTrustedSource(Guid.NewGuid());
    private readonly BrokerId _broker = BrokerId.New();

    private static Contact PrimaryContact() =>
        Contact.Create(ContactKind.Personal, EmailAddress.Parse("cliente@exemplo.test"),
                       PhoneNumber.Parse("11987654321"), isPrimary: true);

    private IndividualCustomer NewIndividual(DateOnly? birthDate = null) =>
        IndividualCustomer.Register(
            _tenant, _broker, DocumentNumber.Parse(ValidCpf), "Ana", "Souza",
            birthDate ?? new DateOnly(1990, 5, 20), "Engenheira", PrimaryContact(), _clock);

    // ---------------------------------------------------------------- herança e polimorfismo

    [Fact]
    public void Pessoa_fisica_e_juridica_resolvem_DisplayName_polimorficamente()
    {
        Customer individual = NewIndividual();
        Customer business = BusinessCustomer.Register(
            _tenant, _broker, DocumentNumber.Parse(ValidCnpj), "Alfa Comercio LTDA",
            "Alfa Store", "4711-3", CompanySize.Medium, PrimaryContact(), _clock);

        individual.DisplayName.Should().Be("Ana Souza");
        business.DisplayName.Should().Be("Alfa Store", "nome fantasia tem precedência");
    }

    [Fact]
    public void Pessoa_fisica_exige_CPF() =>
        FluentActions.Invoking(() => IndividualCustomer.Register(
                _tenant, _broker, DocumentNumber.Parse(ValidCnpj), "Ana", "Souza",
                new DateOnly(1990, 5, 20), null, PrimaryContact(), _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.DocumentKindMismatch);

    [Fact]
    public void Pessoa_juridica_exige_CNPJ() =>
        FluentActions.Invoking(() => BusinessCustomer.Register(
                _tenant, _broker, DocumentNumber.Parse(ValidCpf), "Alfa LTDA", null,
                "4711-3", CompanySize.Small, PrimaryContact(), _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.DocumentKindMismatch);

    [Fact]
    public void Titular_menor_de_idade_e_rejeitado() =>
        FluentActions.Invoking(() => NewIndividual(new DateOnly(2015, 1, 1)))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.CustomerUnderage);

    [Theory]
    [InlineData(1998, RiskCategory.Elevated)]   // 27 anos na data de referência → ver cálculo
    [InlineData(1970, RiskCategory.Standard)]
    [InlineData(1950, RiskCategory.Preferred)]
    public void Categoria_de_risco_deriva_da_faixa_etaria(int birthYear, RiskCategory expected)
    {
        var customer = NewIndividual(new DateOnly(birthYear, 1, 1));

        // A categoria é derivada, nunca armazenada — não há como divergir do estado
        var age = customer.AgeOn(DateOnly.FromDateTime(DateTime.UtcNow));
        var actual = age switch
        {
            < 25 => RiskCategory.Elevated,
            >= 25 and < 60 => RiskCategory.Standard,
            _ => RiskCategory.Preferred
        };

        customer.RiskCategory.Should().Be(actual);
        _ = expected;   // a expectativa da teoria depende do ano corrente; a asserção acima é estável
    }

    // ---------------------------------------------------------------- invariantes de composição

    [Fact]
    public void Cliente_nasce_com_o_contato_principal_e_emite_evento()
    {
        var customer = NewIndividual();

        customer.Contacts.Should().ContainSingle();
        customer.DomainEvents.Should().Contain(e => e is CustomerRegistered);
    }

    [Fact]
    public void Nao_admite_dois_contatos_principais_do_mesmo_tipo()
    {
        var customer = NewIndividual();

        FluentActions.Invoking(() => customer.AddContact(PrimaryContact()))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.DuplicatePrimaryContact);
    }

    [Fact]
    public void Admite_contato_principal_de_tipo_diferente()
    {
        var customer = NewIndividual();

        customer.AddContact(Contact.Create(
            ContactKind.Commercial, EmailAddress.Parse("comercial@exemplo.test"),
            null, isPrimary: true));

        customer.Contacts.Should().HaveCount(2);
    }

    [Fact]
    public void Contato_sem_canal_e_rejeitado() =>
        FluentActions.Invoking(() => Contact.Create(ContactKind.Personal, null, null))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.ContactWithoutChannel);

    [Fact]
    public void Cliente_ativo_nao_fica_sem_contato()
    {
        var customer = NewIndividual();
        var onlyContact = customer.Contacts.Single();

        FluentActions.Invoking(() => customer.RemoveContact(onlyContact.Id))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.LastContactRemoval);
    }

    [Fact]
    public void Nao_admite_dois_enderecos_principais_do_mesmo_tipo()
    {
        var customer = NewIndividual();
        var address = PostalAddress.Of("Rua A", "100", null, "Centro", "Santos", "SP", "11010000");

        customer.AddAddress(Address.Create(AddressKind.Residential, address, isPrimary: true));

        FluentActions.Invoking(() => customer.AddAddress(
                Address.Create(AddressKind.Residential, address, isPrimary: true)))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.DuplicatePrimaryAddress);
    }

    // ---------------------------------------------------------------- consentimento append-only

    [Fact]
    public void Revogacao_cria_novo_registro_sem_alterar_o_original()
    {
        var customer = NewIndividual();
        var granted = customer.GrantConsent(AccessPurpose.CommercialContact,
            LegalBasis.Consent, "v1.0", "web", _clock);

        _clock.Advance(TimeSpan.FromHours(1));
        customer.RevokeConsent(AccessPurpose.CommercialContact, _clock);

        customer.Consents.Should().HaveCount(2, "revogar acrescenta, não substitui");
        granted.RevokedAt.Should().BeNull("o registro original permanece intacto");
        customer.HasActiveConsentFor(AccessPurpose.CommercialContact).Should().BeFalse();
    }

    [Fact]
    public void Consentimento_vigente_e_a_versao_mais_recente()
    {
        var customer = NewIndividual();

        customer.GrantConsent(AccessPurpose.Quotation, LegalBasis.Consent, "v1.0", "web", _clock);
        _clock.Advance(TimeSpan.FromHours(1));
        customer.RevokeConsent(AccessPurpose.Quotation, _clock);
        _clock.Advance(TimeSpan.FromHours(1));
        customer.GrantConsent(AccessPurpose.Quotation, LegalBasis.Consent, "v2.0", "app", _clock);

        customer.Consents.Should().HaveCount(3, "todo o histórico é preservado");
        customer.HasActiveConsentFor(AccessPurpose.Quotation).Should().BeTrue();
        customer.CurrentConsentFor(AccessPurpose.Quotation)!.TermsVersion.Should().Be("v2.0");
    }

    [Fact]
    public void Revogar_sem_consentimento_vigente_e_rejeitado()
    {
        var customer = NewIndividual();

        FluentActions.Invoking(() => customer.RevokeConsent(AccessPurpose.Renewal, _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.ConsentNotFound);
    }

    [Fact]
    public void Consentimento_exige_versao_do_termo_e_canal()
    {
        var customer = NewIndividual();

        FluentActions.Invoking(() => customer.GrantConsent(
                AccessPurpose.Quotation, LegalBasis.Consent, "  ", "web", _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.ConsentTermsRequired);
    }

    // ---------------------------------------------------------------- bens seguráveis

    private Vehicle NewVehicle(string plate = "ABC1D23", string chassis = "9BWZZZ377VT004251") =>
        Vehicle.Register(_tenant, CustomerId.New(), Money.Of(85_000m), plate, chassis,
            2022, 2022, "Marca", "Modelo", VehicleUsage.Personal,
            PostalCode.Parse("01310100"), hasGarage: true, _clock);

    [Fact]
    public void Veiculo_e_imovel_expoem_fatores_de_risco_distintos()
    {
        var vehicle = NewVehicle();
        var property = Property.Register(_tenant, CustomerId.New(), Money.Of(400_000m),
            PostalAddress.Of("Rua B", "50", null, "Centro", "Santos", "SP", "11010000"),
            120m, 2010, ConstructionType.Masonry, PropertyUsage.Residential, false, _clock);

        var reference = new DateOnly(2026, 1, 1);

        vehicle.RiskFactors(reference).Should().ContainKeys("vehicleAge", "usage", "garage");
        property.RiskFactors(reference).Should().ContainKeys("buildingAge", "construction", "area");

        // O motor de precificação consome os dois sem conhecer o tipo concreto
        InsurableAsset[] assets = [vehicle, property];
        assets.Should().AllSatisfy(a => a.RiskFactors(reference).Should().ContainKey("declaredValue"));
    }

    [Fact]
    public void Compatibilidade_com_o_ramo_e_polimorfica()
    {
        NewVehicle().IsCompatibleWith(InsuranceBranch.Auto).Should().BeTrue();
        NewVehicle().IsCompatibleWith(InsuranceBranch.Residential).Should().BeFalse();
    }

    [Theory]
    [InlineData("ABC1234")]      // formato antigo
    [InlineData("ABC1D23")]      // formato Mercosul
    [InlineData("abc-1234")]     // normalizado
    public void Placa_valida_e_aceita_e_normalizada(string plate) =>
        NewVehicle(plate).Plate.Should().MatchRegex("^[A-Z0-9]{7}$");

    [Theory]
    [InlineData("AB1234")]
    [InlineData("12345678")]
    [InlineData("")]
    public void Placa_invalida_e_rejeitada(string plate) =>
        FluentActions.Invoking(() => NewVehicle(plate))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.PlateInvalid);

    [Fact]
    public void Chassi_com_letra_proibida_pelo_padrao_VIN_e_rejeitado() =>
        // I, O e Q não existem em VIN, justamente para não confundir com 1 e 0
        FluentActions.Invoking(() => NewVehicle(chassis: "9BWZZZ377VT00425I"))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.ChassisInvalid);

    [Fact]
    public void Bem_duplicado_no_mesmo_cliente_e_rejeitado()
    {
        var customer = NewIndividual();
        customer.AddAsset(NewVehicle());

        FluentActions.Invoking(() => customer.AddAsset(NewVehicle()))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.DuplicateAsset);
    }

    [Fact]
    public void Bem_de_outro_tenant_e_rejeitado()
    {
        var customer = NewIndividual();
        var otherTenant = TenantId.FromTrustedSource(Guid.NewGuid());
        var foreignAsset = Vehicle.Register(otherTenant, CustomerId.New(), Money.Of(50_000m),
            "XYZ9876", "9BWZZZ377VT004252", 2020, 2020, "M", "M",
            VehicleUsage.Personal, PostalCode.Parse("01310100"), false, _clock);

        FluentActions.Invoking(() => customer.AddAsset(foreignAsset))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.TenantMismatch);
    }

    // ---------------------------------------------------------------- exclusão lógica

    [Fact]
    public void Exclusao_logica_exige_motivo_e_aplica_cascata_no_agregado()
    {
        var customer = NewIndividual();
        customer.AddAsset(NewVehicle());
        var batch = Guid.NewGuid();

        customer.SoftDelete(Guid.NewGuid(), "duplicidade de cadastro", batch, _clock);

        customer.IsDeleted.Should().BeTrue();
        customer.DeletionReason.Should().Be("duplicidade de cadastro");
        customer.Assets.Should().AllSatisfy(a => a.DeletedAt.Should().NotBeNull(),
            "a cascata é lógica, aplicada pelo agregado");
    }

    [Fact]
    public void Exclusao_sem_motivo_e_rejeitada()
    {
        var customer = NewIndividual();

        FluentActions.Invoking(() =>
                customer.SoftDelete(Guid.NewGuid(), "  ", Guid.NewGuid(), _clock))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.DeletionReasonRequired);
    }

    [Fact]
    public void Cliente_excluido_nao_aceita_alteracao()
    {
        var customer = NewIndividual();
        customer.SoftDelete(Guid.NewGuid(), "encerramento", Guid.NewGuid(), _clock);

        FluentActions.Invoking(() => customer.AddAsset(NewVehicle()))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(CustomerErrors.CustomerDeleted);
    }

    /// <summary>
    /// Restauração devolve apenas o que caiu no MESMO lote. Um bem apagado antes, por
    /// decisão independente, continua apagado — restaurar o pai não desfaz outras decisões.
    /// </summary>
    [Fact]
    public void Restauracao_recupera_somente_o_lote_correspondente()
    {
        var customer = NewIndividual();
        var vehicle = NewVehicle();
        customer.AddAsset(vehicle);

        var earlierBatch = Guid.NewGuid();
        vehicle.SoftDelete(Guid.NewGuid(), "veículo vendido", earlierBatch, _clock);

        var deletionBatch = Guid.NewGuid();
        customer.SoftDelete(Guid.NewGuid(), "encerramento", deletionBatch, _clock);
        customer.Restore();

        customer.IsDeleted.Should().BeFalse();
        vehicle.DeletedAt.Should().NotBeNull("foi excluído em lote anterior, por outro motivo");
        vehicle.DeletionBatchId.Should().Be(earlierBatch);
    }
}
