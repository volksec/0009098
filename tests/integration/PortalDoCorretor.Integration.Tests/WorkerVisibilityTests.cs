using FluentAssertions;
using Npgsql;

namespace PortalDoCorretor.Integration.Tests;

/// <summary>
/// Regressão de um bug real: três workers de manutenção rodavam sob RLS como
/// <c>app_worker</c>, sem contexto de tenant, e enxergavam zero linhas.
/// </summary>
/// <remarks>
/// <para>
/// O Outbox Dispatcher tinha política própria desde a V008 — o raciocínio estava certo,
/// mas foi aplicado a uma tabela só. Renewal Scanner, Billing Scheduler e Quotation
/// Expirer ficaram sujeitos à política de tenant, que exige <c>app.current_tenant()</c>,
/// um valor que worker nenhum define.
/// </para>
/// <para>
/// O sintoma era silêncio: os três só registram log quando afetam ao menos uma linha,
/// então rodavam a cada ciclo sem fazer nada e sem reclamar. Por isso o teste verifica
/// que o worker <b>enxerga</b> as linhas — contar zero é o modo de falha, não o sucesso.
/// </para>
/// <para>
/// A contrapartida é igualmente importante: alargar o acesso do worker não pode ter
/// alargado o do corretor. Os dois últimos testes fixam isso.
/// </para>
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public sealed class WorkerVisibilityTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await SeedData.EnsureAsync(fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("policies", "Renewal Scanner")]
    [InlineData("installments", "Billing Scheduler")]
    [InlineData("quotations", "Quotation Expirer")]
    [InlineData("outbox_messages", "Outbox Dispatcher")]
    public async Task Worker_enxerga_o_mesmo_que_o_dono_da_tabela(string tabela, string worker)
    {
        await using var migrator = await fixture.OpenAsMigratorAsync();
        var total = await ScalarAsync<long>(migrator, $"SELECT count(*) FROM {tabela}");

        await using var connection = await OpenAsWorkerAsync();
        var visiveis = await ScalarAsync<long>(connection, $"SELECT count(*) FROM {tabela}");

        visiveis.Should().Be(total,
            $"o {worker} varre todos os tenants sem definir app.tenant_id; "
          + "sob a política de tenant ele enxergaria zero linhas e não faria nada");
    }

    /// <summary>
    /// A comparação acima é vácua se a tabela estiver vazia. Este teste garante que a
    /// massa da fixture não sumiu por baixo dela.
    /// </summary>
    [Fact]
    public async Task Massa_de_teste_tem_linhas_para_a_comparacao_valer()
    {
        await using var connection = await OpenAsWorkerAsync();

        (await ScalarAsync<long>(connection, "SELECT count(*) FROM policies"))
            .Should().BeGreaterThan(0);
        (await ScalarAsync<long>(connection, "SELECT count(*) FROM quotations"))
            .Should().BeGreaterThan(0);
    }

    /// <summary>
    /// O worker precisa escrever, não só ler: o Billing Scheduler marca OVERDUE e o
    /// Quotation Expirer move para EXPIRED. Sem WITH CHECK a leitura passaria e a
    /// escrita falharia — um meio-conserto que o teste de contagem não pegaria.
    /// </summary>
    [Theory]
    [InlineData("quotations")]
    [InlineData("policies")]
    public async Task Worker_consegue_escrever_em_todos_os_tenants(string tabela)
    {
        await using var migrator = await fixture.OpenAsMigratorAsync();
        var total = await ScalarAsync<long>(migrator, $"SELECT count(*) FROM {tabela}");

        await using var connection = await OpenAsWorkerAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using var command = new NpgsqlCommand(
            $"UPDATE {tabela} SET status = status", connection, transaction);
        var afetadas = await command.ExecuteNonQueryAsync();

        // Sem WITH CHECK a leitura passaria e a escrita falharia — um meio-conserto que
        // a contagem sozinha não pegaria.
        afetadas.Should().Be((int)total, $"o worker precisa poder atualizar {tabela} em todos os tenants");

        // Escrita de verdade não pertence a um teste: só a permissão está sendo verificada
        await transaction.RollbackAsync();
    }

    /// <summary>
    /// Formulado sobre o conteúdo das linhas, e não sobre a contagem: vale mesmo que a
    /// fixture concentre tudo em um tenant só.
    /// </summary>
    [Fact]
    public async Task Corretor_continua_sem_ver_nada_fora_do_tenant()
    {
        await using var alfa = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);

        var alheias = await ScalarAsync<long>(alfa,
            $"SELECT count(*) FROM policies WHERE tenant_id <> '{SeedData.TenantAlfa}'");

        alheias.Should().Be(0,
            "a política do worker não pode ter alargado o alcance do corretor");
    }

    [Fact]
    public async Task Corretor_sem_contexto_de_tenant_continua_vendo_zero()
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();

        var visiveis = await ScalarAsync<long>(connection, "SELECT count(*) FROM policies");

        visiveis.Should().Be(0, "falha fechado: sem tenant a política de app_user nega");
    }

    // ---------------------------------------------------------------- auxiliares

    private async Task<NpgsqlConnection> OpenAsWorkerAsync()
    {
        // Sem SET LOCAL app.tenant_id: é exatamente assim que o worker conecta
        var builder = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString)
        {
            Username = "app_worker"
        };

        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
