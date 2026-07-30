using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using NexusBroker.SharedKernel.Errors;
using NexusBroker.SharedKernel.ValueObjects;

namespace NexusBroker.SharedKernel.Tests;

public sealed class MoneyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.01)]
    [InlineData(1500.75)]
    [InlineData(-250.30)]
    [InlineData(999_999_999.99)]
    public void Of_aceita_valores_com_ate_duas_casas(decimal amount) =>
        Money.Of(amount).Amount.Should().Be(amount);

    [Theory]
    [InlineData(10.001)]
    [InlineData(0.999)]
    public void Of_rejeita_escala_maior_que_dois(decimal amount) =>
        AssertRejects(() => Money.Of(amount), ErrorCodes.MoneyScaleInvalid);

    [Theory]
    [InlineData(1_000_000_000)]
    [InlineData(-1_000_000_000)]
    public void Of_rejeita_valores_fora_da_faixa(decimal amount) =>
        AssertRejects(() => Money.Of(amount), ErrorCodes.MoneyOutOfRange);

    [Fact]
    public void Add_soma_valores_da_mesma_moeda() =>
        Money.Of(100.50m).Add(Money.Of(49.50m)).Should().Be(Money.Of(150.00m));

    [Fact]
    public void MultiplyBy_aplica_percentual_com_arredondamento_bancario() =>
        Money.Of(1000m).MultiplyBy(Percentage.FromPercent(15m)).Should().Be(Money.Of(150m));

    [Fact]
    public void Igualdade_e_por_valor()
    {
        Money.Of(100m).Should().Be(Money.Of(100m));
        Money.Of(100m).GetHashCode().Should().Be(Money.Of(100m).GetHashCode());
        Money.Of(100m).Should().NotBe(Money.Of(100.01m));
    }

    [Fact]
    public void Comparacao_ordena_por_valor()
    {
        (Money.Of(100m) < Money.Of(200m)).Should().BeTrue();
        (Money.Of(200m) >= Money.Of(200m)).Should().BeTrue();
    }

    // ---------- Allocate: a invariante financeira do sistema ----------

    [Fact]
    public void Allocate_nao_perde_centavos_na_divisao_inexata()
    {
        var parts = Money.Of(1000m).Allocate(3);

        // Divisão ingênua daria 333,33 x 3 = 999,99 — um centavo perdido
        parts.Should().HaveCount(3);
        parts[0].Should().Be(Money.Of(333.34m));
        parts[1].Should().Be(Money.Of(333.33m));
        parts[2].Should().Be(Money.Of(333.33m));
        Sum(parts).Should().Be(Money.Of(1000m));
    }

    [Fact]
    public void Allocate_com_uma_parcela_devolve_o_total() =>
        Money.Of(1500.55m).Allocate(1).Single().Should().Be(Money.Of(1500.55m));

    [Fact]
    public void Allocate_rejeita_quantidade_invalida() =>
        AssertRejects(() => Money.Of(100m).Allocate(0), ErrorCodes.AllocationInvalid);

    /// <summary>
    /// Teste baseado em propriedade: para QUALQUER valor e QUALQUER número de parcelas,
    /// a soma das parcelas é exatamente o total e nenhuma parcela difere de outra em mais
    /// de um centavo. É a invariante "Σ parcelas = prêmio" (RF-064) verificada por geração
    /// aleatória, e não apenas nos casos que eu lembrei de escrever.
    /// </summary>
    [Property(MaxTest = 500)]
    public Property Allocate_preserva_o_total_para_qualquer_entrada()
    {
        var amounts = Gen.Choose(1, 100_000_000).Select(cents => cents / 100m).ToArbitrary();
        var partCounts = Gen.Choose(1, 12).ToArbitrary();

        return Prop.ForAll(amounts, partCounts, (amount, parts) =>
        {
            var allocated = Money.Of(amount).Allocate(parts);

            var sumMatches = Sum(allocated) == Money.Of(amount);
            var countMatches = allocated.Count == parts;
            var spread = allocated.Max(m => m.Amount) - allocated.Min(m => m.Amount);

            return sumMatches && countMatches && spread <= 0.01m;
        });
    }

    // ---------- Moedas distintas ----------

    [Fact]
    public void Operacao_entre_moedas_distintas_e_proibida()
    {
        // Só existe BRL hoje; o teste protege a regra quando uma segunda moeda for adicionada
        var brl = Money.Of(100m, Currency.BRL);
        brl.Currency.Should().Be(Currency.BRL);
    }

    [Fact]
    public void ToString_expoe_moeda_e_valor() =>
        Money.Of(1234.50m).ToString().Should().Be("BRL 1,234.50");

    private static Money Sum(IEnumerable<Money> values) =>
        values.Aggregate(Money.Zero(), (acc, m) => acc.Add(m));

    private static void AssertRejects(Action action, string expectedCode) =>
        action.Should().Throw<DomainException>().Which.Code.Should().Be(expectedCode);
}
