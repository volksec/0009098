using FluentAssertions;
using NexusBroker.SharedKernel.Errors;
using NexusBroker.SharedKernel.ValueObjects;

namespace NexusBroker.SharedKernel.Tests;

/// <summary>
/// Documentos usados aqui são <b>sintéticos</b>: possuem dígito verificador válido, mas foram
/// gerados para teste e não correspondem a pessoas reais.
/// </summary>
public sealed class DocumentNumberTests
{
    private const string ValidCpf = "52998224725";
    private const string ValidCnpj = "11222333000181";

    [Theory]
    [InlineData(ValidCpf)]
    [InlineData("529.982.247-25")]
    [InlineData(" 529 982 247 25 ")]
    public void Parse_aceita_cpf_valido_em_qualquer_formatacao(string input)
    {
        var document = DocumentNumber.Parse(input);

        document.Kind.Should().Be(DocumentKind.Cpf);
        document.Value.Should().Be(ValidCpf);
    }

    [Theory]
    [InlineData(ValidCnpj)]
    [InlineData("11.222.333/0001-81")]
    public void Parse_aceita_cnpj_valido(string input)
    {
        var document = DocumentNumber.Parse(input);

        document.Kind.Should().Be(DocumentKind.Cnpj);
        document.Value.Should().Be(ValidCnpj);
    }

    [Theory]
    [InlineData("52998224724")]      // último dígito errado
    [InlineData("11222333000180")]   // CNPJ com DV errado
    [InlineData("11111111111")]      // sequência repetida passa no cálculo, mas não é válida
    [InlineData("00000000000")]
    [InlineData("123")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_rejeita_documento_invalido(string? input) =>
        FluentActions.Invoking(() => DocumentNumber.Parse(input))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be(ErrorCodes.DocumentInvalid);

    /// <summary>
    /// A mensagem de erro NÃO pode ecoar o valor recebido: erro que imprime o documento
    /// vaza dado pessoal em log agregado.
    /// </summary>
    [Fact]
    public void Mensagem_de_erro_nao_vaza_o_documento_recebido()
    {
        const string attempted = "12345678901";

        var exception = FluentActions.Invoking(() => DocumentNumber.Parse(attempted))
            .Should().Throw<DomainException>().Which;

        exception.Message.Should().NotContain(attempted);
    }

    [Fact]
    public void ToString_retorna_a_forma_mascarada_por_padrao()
    {
        // Segurança por default: interpolação acidental em log não vaza o documento
        DocumentNumber.Parse(ValidCpf).ToString().Should().Be("***.***.247-**");
        DocumentNumber.Parse(ValidCpf).ToString().Should().NotContain(ValidCpf);
    }

    [Fact]
    public void Masked_preserva_apenas_os_digitos_de_conferencia()
    {
        DocumentNumber.Parse(ValidCpf).Masked.Should().Be("***.***.247-**");
        DocumentNumber.Parse(ValidCnpj).Masked.Should().Be("**.***.333/****-**");
    }

    [Fact]
    public void Formatted_exige_decisao_explicita_de_exibir_o_dado_completo()
    {
        DocumentNumber.Parse(ValidCpf).Formatted.Should().Be("529.982.247-25");
        DocumentNumber.Parse(ValidCnpj).Formatted.Should().Be("11.222.333/0001-81");
    }

    [Fact]
    public void SearchHash_e_deterministico_para_o_mesmo_pepper()
    {
        var pepper = "pepper-de-teste"u8.ToArray();
        var document = DocumentNumber.Parse(ValidCpf);

        document.SearchHash(pepper).Should().Equal(document.SearchHash(pepper));
    }

    [Fact]
    public void SearchHash_muda_com_pepper_diferente()
    {
        var document = DocumentNumber.Parse(ValidCpf);

        document.SearchHash("pepper-a"u8.ToArray())
            .Should().NotEqual(document.SearchHash("pepper-b"u8.ToArray()));
    }

    [Fact]
    public void Igualdade_e_por_valor()
    {
        DocumentNumber.Parse(ValidCpf).Should().Be(DocumentNumber.Parse("529.982.247-25"));
        DocumentNumber.Parse(ValidCpf).Should().NotBe(DocumentNumber.Parse(ValidCnpj));
    }

    [Fact]
    public void TryParse_nao_lanca_para_entrada_invalida()
    {
        DocumentNumber.TryParse("invalido", out _).Should().BeFalse();
        DocumentNumber.TryParse(ValidCpf, out var document).Should().BeTrue();
        document.Kind.Should().Be(DocumentKind.Cpf);
    }
}
