using FluentAssertions;
using NexusBroker.SharedKernel.Errors;
using NexusBroker.SharedKernel.ValueObjects;

namespace NexusBroker.SharedKernel.Tests;

public sealed class DateRangeTests
{
    private static readonly DateOnly Jan01 = new(2026, 1, 1);
    private static readonly DateOnly Jul01 = new(2026, 7, 1);
    private static readonly DateOnly Dec31 = new(2026, 12, 31);

    [Fact]
    public void Of_rejeita_intervalo_invertido_ou_vazio()
    {
        AssertRejects(() => DateRange.Of(Dec31, Jan01));
        AssertRejects(() => DateRange.Of(Jan01, Jan01));
    }

    [Fact]
    public void Contains_usa_intervalo_semiaberto()
    {
        var range = DateRange.Of(Jan01, Dec31);

        range.Contains(Jan01).Should().BeTrue("o início está contido");
        range.Contains(Jul01).Should().BeTrue();
        range.Contains(Dec31).Should().BeFalse("o fim NÃO está contido — [start, end)");
    }

    [Fact]
    public void Overlaps_detecta_sobreposicao_de_vigencia()
    {
        var first = DateRange.Of(Jan01, Jul01);

        first.Overlaps(DateRange.Of(new DateOnly(2026, 6, 1), Dec31)).Should().BeTrue();
        first.Overlaps(DateRange.Of(Jul01, Dec31)).Should().BeFalse("intervalos adjacentes não se sobrepõem");
    }

    [Fact]
    public void OfMonths_calcula_vigencia_anual()
    {
        var range = DateRange.OfYear(Jan01);

        range.End.Should().Be(new DateOnly(2027, 1, 1));
        range.DurationInDays.Should().Be(365);
    }

    [Fact]
    public void IsExpiringWithin_identifica_apolice_proxima_do_vencimento()
    {
        var range = DateRange.Of(Jan01, Jul01);
        var reference = new DateOnly(2026, 6, 1);   // 30 dias antes do fim

        range.IsExpiringWithin(reference, 45).Should().BeTrue();
        range.IsExpiringWithin(reference, 15).Should().BeFalse();
        range.IsExpiringWithin(Dec31, 45).Should().BeFalse("já vencida não está 'expirando'");
    }

    private static void AssertRejects(Action action) =>
        action.Should().Throw<DomainException>()
            .Which.Code.Should().Be(ErrorCodes.DateRangeInvalid);
}

public sealed class TenantIdTests
{
    /// <summary>
    /// O ponto central do isolamento multi-tenant: não existe caminho público que construa
    /// um TenantId a partir de entrada do usuário. Se este teste um dia falhar porque alguém
    /// adicionou um overload público que aceita string de requisição, a camada 1 da defesa
    /// em profundidade (ADR-0004) foi removida.
    /// </summary>
    [Fact]
    public void Nao_existe_construtor_publico_que_aceite_entrada_de_usuario()
    {
        var publicFactories = typeof(TenantId)
            .GetMethods()
            .Where(m => m.IsStatic && m.IsPublic && m.ReturnType == typeof(TenantId))
            .Select(m => m.Name)
            .ToArray();

        publicFactories.Should().BeEquivalentTo([nameof(TenantId.FromTrustedSource)],
            "a criação de TenantId deve passar apenas pela origem confiável (claim ou banco)");

        typeof(TenantId).GetConstructors().Should().BeEmpty(
            "não deve haver construtor público");
    }

    [Fact]
    public void FromTrustedSource_rejeita_guid_vazio() =>
        FluentActions.Invoking(() => TenantId.FromTrustedSource(Guid.Empty))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ErrorCodes.TenantIdInvalid);

    [Fact]
    public void Igualdade_e_por_valor()
    {
        var guid = Guid.NewGuid();

        TenantId.FromTrustedSource(guid).Should().Be(TenantId.FromTrustedSource(guid));
        TenantId.FromTrustedSource(guid).Should().NotBe(TenantId.FromTrustedSource(Guid.NewGuid()));
    }
}

public sealed class BusinessNumberTests
{
    [Fact]
    public void Generate_produz_numero_com_digito_verificador_valido()
    {
        var number = PolicyNumber.Generate(2026, 42);

        number.Value.Should().MatchRegex(@"^NB-2026-00000042-\d$");
        PolicyNumber.Parse(number.Value).Should().Be(number);
        number.Year.Should().Be(2026);
    }

    /// <summary>
    /// O dígito verificador transforma enumeração por incremento em erro de validação
    /// ANTES de tocar o banco — o atacante não consegue simplesmente varrer sequências.
    /// </summary>
    [Fact]
    public void Numero_adivinhado_por_incremento_falha_no_digito_verificador()
    {
        var valid = PolicyNumber.Generate(2026, 100);
        var guessed = valid.Value[..^1] + (valid.Value[^1] == '0' ? '1' : '0');

        FluentActions.Invoking(() => PolicyNumber.Parse(guessed))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ErrorCodes.PolicyNumberCheckDigit);
    }

    [Theory]
    [InlineData("NB-2026-0000004-2")]     // sequência curta
    [InlineData("XX-2026-00000042-1")]    // prefixo errado
    [InlineData("PR-2026-00000042-1")]    // prefixo de proposta em apólice
    [InlineData("")]
    [InlineData(null)]
    public void Parse_rejeita_formato_invalido(string? input) =>
        FluentActions.Invoking(() => PolicyNumber.Parse(input))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ErrorCodes.PolicyNumberInvalid);

    [Fact]
    public void Cada_tipo_de_numero_tem_prefixo_proprio()
    {
        PolicyNumber.Generate(2026, 1).Value.Should().StartWith("NB-");
        ProposalNumber.Generate(2026, 1).Value.Should().StartWith("PR-");
        QuotationNumber.Generate(2026, 1).Value.Should().StartWith("CT-");
    }
}

public sealed class RiskAndCoverageTests
{
    [Theory]
    [InlineData(0, RiskBand.Low)]
    [InlineData(250, RiskBand.Low)]
    [InlineData(251, RiskBand.Moderate)]
    [InlineData(550, RiskBand.Moderate)]
    [InlineData(801, RiskBand.Severe)]
    [InlineData(1000, RiskBand.Severe)]
    public void Band_e_derivada_do_escore_nas_fronteiras(int score, RiskBand expected) =>
        RiskScore.Of(score).Band.Should().Be(expected);

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void RiskScore_rejeita_valor_fora_da_faixa(int score) =>
        FluentActions.Invoking(() => RiskScore.Of(score))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ErrorCodes.RiskScoreOutOfRange);

    [Fact]
    public void CoverageLimit_rejeita_valor_nao_positivo()
    {
        FluentActions.Invoking(() => CoverageLimit.Of(0m))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ErrorCodes.CoverageLimitInvalid);

        FluentActions.Invoking(() => CoverageLimit.Of(-1m)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Deductible_fixa_independe_do_valor_segurado() =>
        Deductible.Fixed(Money.Of(1500m)).AppliedTo(Money.Of(80_000m))
            .Should().Be(Money.Of(1500m));

    [Fact]
    public void Deductible_proporcional_calcula_sobre_o_valor_segurado() =>
        Deductible.Proportional(Percentage.FromPercent(5m)).AppliedTo(Money.Of(80_000m))
            .Should().Be(Money.Of(4000m));

    [Fact]
    public void CommissionRate_respeita_o_teto_de_negocio()
    {
        CommissionRate.Of(0.35m).Value.Should().Be(0.35m);

        FluentActions.Invoking(() => CommissionRate.Of(0.36m))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ErrorCodes.CommissionRateOutOfRange);

        FluentActions.Invoking(() => CommissionRate.Of(0m)).Should().Throw<DomainException>();
    }

    [Fact]
    public void CommissionRate_aplicada_calcula_o_valor_da_comissao() =>
        CommissionRate.Of(0.12m).AppliedTo(Money.Of(2500m)).Should().Be(Money.Of(300m));
}

public sealed class ContactValueObjectTests
{
    [Theory]
    [InlineData("Corretor@Exemplo.COM.BR", "corretor@exemplo.com.br")]
    [InlineData("  teste@dominio.com  ", "teste@dominio.com")]
    public void Email_e_normalizado_para_minusculas(string input, string expected) =>
        EmailAddress.Parse(input).Value.Should().Be(expected);

    [Theory]
    [InlineData("sem-arroba.com")]
    [InlineData("dois@@arrobas.com")]
    [InlineData("sem@dominio")]
    [InlineData("@semlocal.com")]
    [InlineData("com espaco@dominio.com")]
    [InlineData("ponto@duplo..com")]
    [InlineData(null)]
    public void Email_rejeita_formato_invalido(string? input) =>
        FluentActions.Invoking(() => EmailAddress.Parse(input))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ErrorCodes.EmailInvalid);

    [Fact]
    public void Email_mascarado_preserva_apenas_o_inicio_e_o_dominio() =>
        EmailAddress.Parse("corretor@exemplo.com.br").Masked
            .Should().Be("co******@exemplo.com.br");

    [Theory]
    [InlineData("11987654321", true)]
    [InlineData("+55 11 98765-4321", true)]
    [InlineData("1133334444", false)]
    public void Telefone_identifica_movel_e_fixo(string input, bool isMobile) =>
        PhoneNumber.Parse(input).IsMobile.Should().Be(isMobile);

    [Theory]
    [InlineData("10987654321")]   // DDD inválido
    [InlineData("11887654321")]   // móvel de 11 dígitos sem iniciar com 9
    [InlineData("123")]
    public void Telefone_rejeita_numero_invalido(string input) =>
        FluentActions.Invoking(() => PhoneNumber.Parse(input))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ErrorCodes.PhoneInvalid);

    [Fact]
    public void Telefone_mascarado_preserva_ddd_e_ultimos_digitos() =>
        PhoneNumber.Parse("11987654321").Masked.Should().Be("(11) *****-4321");

    [Fact]
    public void Endereco_exige_campos_obrigatorios() =>
        FluentActions.Invoking(() =>
                PostalAddress.Of("Rua Teste", "", null, "Centro", "São Paulo", "SP", "01310100"))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ErrorCodes.AddressIncomplete);

    [Fact]
    public void Endereco_rejeita_uf_invalida() =>
        FluentActions.Invoking(() =>
                PostalAddress.Of("Rua Teste", "100", null, "Centro", "São Paulo", "XX", "01310100"))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ErrorCodes.StateCodeInvalid);

    [Fact]
    public void Endereco_minimizado_omite_logradouro_e_numero()
    {
        var address = PostalAddress.Of(
            "Avenida Paulista", "1000", "Conj. 51", "Bela Vista", "São Paulo", "SP", "01310-100");

        address.Minimized.Should().Be("São Paulo/SP - 01310***");
        address.Minimized.Should().NotContain("Paulista");
        address.RegionCode.Should().Be(0);
    }

    [Fact]
    public void Endereco_e_igual_por_valor()
    {
        var a = PostalAddress.Of("Rua A", "1", null, "Centro", "Santos", "SP", "11010000");
        var b = PostalAddress.Of("Rua A", "1", null, "Centro", "Santos", "SP", "11010000");

        a.Should().Be(b);
    }
}
