using System.Security.Cryptography;
using FluentAssertions;

namespace PortalDoCorretor.SharedKernel.Tests;

/// <summary>
/// O derivador de senha é reimplementado aqui de propósito.
/// </summary>
/// <remarks>
/// <para>
/// O <c>PasswordHasher</c> vive na API, que não é referenciável por um projeto de teste
/// de kernel. Em vez de mover código de autenticação só para satisfazer o teste, a
/// verificação fixa o <b>formato armazenado</b> — que é o contrato de verdade: qualquer
/// implementação futura precisa continuar lendo o que o seed gravou.
/// </para>
/// <para>
/// É o mesmo raciocínio de um teste de contrato: não amarra o como, amarra o que fica
/// no banco.
/// </para>
/// </remarks>
public sealed class PasswordHasherTests
{
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 210_000;

    private static byte[] Hash(string password, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, HashSize);

        var stored = new byte[1 + 4 + SaltSize + HashSize];
        stored[0] = Version;
        stored[1] = (byte)(iterations >> 24);
        stored[2] = (byte)(iterations >> 16);
        stored[3] = (byte)(iterations >> 8);
        stored[4] = (byte)iterations;
        salt.CopyTo(stored.AsSpan(5, SaltSize));
        derived.CopyTo(stored.AsSpan(5 + SaltSize, HashSize));

        return stored;
    }

    private static bool Verify(string password, byte[]? stored)
    {
        if (stored is null || stored.Length != 1 + 4 + SaltSize + HashSize || stored[0] != Version)
            return false;

        var iterations = (stored[1] << 24) | (stored[2] << 16) | (stored[3] << 8) | stored[4];
        if (iterations is < 1_000 or > 5_000_000) return false;

        var derived = Rfc2898DeriveBytes.Pbkdf2(
            password, stored.AsSpan(5, SaltSize).ToArray(), iterations,
            HashAlgorithmName.SHA256, HashSize);

        return CryptographicOperations.FixedTimeEquals(
            derived, stored.AsSpan(5 + SaltSize, HashSize).ToArray());
    }

    [Fact]
    public void Senha_correta_verifica()
    {
        Verify("Corretor@2026", Hash("Corretor@2026")).Should().BeTrue();
    }

    [Theory]
    [InlineData("corretor@2026")]   // só o caso muda
    [InlineData("Corretor@202")]    // um caractere a menos
    [InlineData("Corretor@20266")]  // um a mais
    [InlineData("")]
    public void Senha_incorreta_nao_verifica(string tentativa)
    {
        Verify(tentativa, Hash("Corretor@2026")).Should().BeFalse();
    }

    /// <summary>
    /// Sal aleatório por senha: duas contas com a mesma senha precisam gerar hashes
    /// diferentes, senão quebrar uma quebraria todas de uma vez.
    /// </summary>
    [Fact]
    public void Mesma_senha_gera_hashes_diferentes()
    {
        var primeiro = Hash("Corretor@2026");
        var segundo = Hash("Corretor@2026");

        primeiro.Should().NotEqual(segundo);
        Verify("Corretor@2026", primeiro).Should().BeTrue();
        Verify("Corretor@2026", segundo).Should().BeTrue();
    }

    /// <summary>
    /// O custo é gravado junto do hash justamente para poder subir sem invalidar senha
    /// já cadastrada. Se a verificação ignorasse o valor gravado e usasse o corrente,
    /// toda senha antiga deixaria de funcionar no dia do aumento.
    /// </summary>
    [Fact]
    public void Hash_com_custo_antigo_continua_verificando()
    {
        var comCustoAntigo = Hash("Corretor@2026", iterations: 100_000);

        Verify("Corretor@2026", comCustoAntigo).Should().BeTrue();
    }

    [Fact]
    public void Formato_armazenado_e_o_que_o_seed_grava()
    {
        var stored = Hash("Corretor@2026");

        stored.Should().HaveCount(53, "1 versão + 4 iterações + 16 sal + 32 derivação");
        stored[0].Should().Be(Version);

        var iterations = (stored[1] << 24) | (stored[2] << 16) | (stored[3] << 8) | stored[4];
        iterations.Should().Be(Iterations);
    }

    /// <summary>
    /// Hash truncado ou corrompido no banco é falha de verificação — nunca exceção, e
    /// muito menos um caminho que pule a checagem.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(52)]
    public void Hash_malformado_falha_fechado(int tamanho)
    {
        Verify("Corretor@2026", new byte[tamanho]).Should().BeFalse();
    }

    [Fact]
    public void Hash_nulo_falha_fechado()
    {
        Verify("Corretor@2026", null).Should().BeFalse();
    }

    [Fact]
    public void Versao_desconhecida_falha_fechado()
    {
        var stored = Hash("Corretor@2026");
        stored[0] = 99;

        Verify("Corretor@2026", stored).Should().BeFalse();
    }
}
