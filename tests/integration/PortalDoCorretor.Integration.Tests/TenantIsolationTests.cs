using FluentAssertions;
using Npgsql;

namespace PortalDoCorretor.Integration.Tests;

/// <summary>
/// Camada 5 do isolamento: Row-Level Security.
/// </summary>
/// <remarks>
/// Todos os testes conectam como <c>app_user</c> — o papel real da aplicação, sem
/// <c>BYPASSRLS</c>. Conectar como superusuário aqui invalidaria a suíte inteira: as
/// políticas seriam ignoradas e tudo passaria.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public sealed class TenantIsolationTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await SeedData.EnsureAsync(fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// O comportamento mais importante do isolamento: sem contexto de tenant,
    /// <c>app.current_tenant()</c> retorna NULL, e <c>tenant_id = NULL</c> nunca é verdadeiro.
    /// A ausência de contexto NEGA, em vez de liberar tudo.
    /// </summary>
    [Fact]
    public async Task Sem_contexto_de_tenant_nenhuma_linha_e_visivel()
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();

        var count = await ScalarAsync<long>(connection, "SELECT count(*) FROM customers");

        count.Should().Be(0, "falha fechado: sem SET LOCAL app.tenant_id a política nega");
    }

    [Fact]
    public async Task Corretor_ve_apenas_clientes_do_proprio_tenant()
    {
        await using var alfa = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);
        var namesAlfa = await ListAsync(alfa, "SELECT first_name || ' ' || last_name FROM customers");

        await using var beta = await fixture.OpenAsAppUserAsync(SeedData.TenantBeta);
        var namesBeta = await ListAsync(beta, "SELECT first_name || ' ' || last_name FROM customers");

        namesAlfa.Should().ContainSingle().Which.Should().Be("Cliente Alfa");
        namesBeta.Should().ContainSingle().Which.Should().Be("Cliente Beta");
    }

    /// <summary>
    /// IDOR: mesmo conhecendo o identificador exato do recurso alheio, a consulta retorna vazio.
    /// É o que permite à API responder 404 em vez de 403 — sem confirmar que o recurso existe.
    /// </summary>
    [Fact]
    public async Task Acesso_direto_por_id_de_outro_tenant_retorna_vazio()
    {
        await using var connection = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);

        var count = await ScalarAsync<long>(connection,
            $"SELECT count(*) FROM customers WHERE id = '{SeedData.CustomerBeta}'");

        count.Should().Be(0);
    }

    /// <summary>
    /// Prova que a política tem <c>WITH CHECK</c>, e não apenas <c>USING</c>. Sem isso o
    /// corretor não leria dados alheios, mas conseguiria ESCREVER neles.
    /// </summary>
    [Fact]
    public async Task Insert_com_tenant_forjado_e_bloqueado()
    {
        await using var connection = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);

        var act = async () => await ExecAsync(connection, $"""
            INSERT INTO customers (tenant_id, broker_id, kind, document_encrypted, document_hash,
                                   first_name, last_name, birth_date, created_by)
            VALUES ('{SeedData.TenantBeta}', '{SeedData.BrokerCarla}', 'INDIVIDUAL', '\x01', '\xCC',
                    'Forjado', 'X', '1990-01-01', '{SeedData.UserAna}')
            """);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.Message.Should().Contain("row-level security");
    }

    [Fact]
    public async Task Update_movendo_registro_para_outro_tenant_e_bloqueado()
    {
        await using var connection = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);

        var act = async () => await ExecAsync(connection,
            $"UPDATE customers SET tenant_id = '{SeedData.TenantBeta}' WHERE id = '{SeedData.CustomerAlfa}'");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.Message.Should().Contain("row-level security");
    }

    /// <summary>Exclusão é lógica: o privilégio de DELETE físico é revogado da aplicação.</summary>
    [Fact]
    public async Task Delete_fisico_e_negado_para_a_aplicacao()
    {
        await using var connection = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);

        var act = async () => await ExecAsync(connection,
            $"DELETE FROM customers WHERE id = '{SeedData.CustomerAlfa}'");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>A trilha de auditoria é append-only de verdade: nem a aplicação a altera.</summary>
    [Fact]
    public async Task Auditoria_nao_pode_ser_alterada_pela_aplicacao()
    {
        await using var connection = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);

        var update = async () => await ExecAsync(connection, "UPDATE audit_events SET action = 'X'");
        var delete = async () => await ExecAsync(connection, "DELETE FROM audit_events");

        (await update.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        await delete.Should().ThrowAsync<PostgresException>();
    }

    /// <summary>
    /// Todas as tabelas multi-tenant precisam de FORCE. Sem ele, o dono da tabela ignora as
    /// políticas — o detalhe que transforma "temos RLS" em falsa sensação de segurança.
    /// </summary>
    [Fact]
    public async Task Todas_as_tabelas_com_rls_usam_force()
    {
        await using var connection = await fixture.OpenAsMigratorAsync();

        var withoutForce = await ListAsync(connection, """
            SELECT relname FROM pg_class
             WHERE relrowsecurity AND NOT relforcerowsecurity
               AND relnamespace = 'public'::regnamespace
            """);

        withoutForce.Should().BeEmpty("RLS sem FORCE não protege contra o dono da tabela");
    }

    [Fact]
    public async Task Toda_tabela_com_tenant_id_tem_rls_habilitada()
    {
        await using var connection = await fixture.OpenAsMigratorAsync();

        var unprotected = await ListAsync(connection, """
            SELECT c.relname
              FROM pg_class c
              JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id'
             WHERE c.relkind = 'r'
               AND c.relnamespace = 'public'::regnamespace
               AND NOT c.relispartition
               AND NOT c.relrowsecurity
            """);

        unprotected.Should().BeEmpty(
            "uma tabela com tenant_id sem RLS é um buraco de isolamento");
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> ListAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var results = new List<string>();
        while (await reader.ReadAsync()) results.Add(reader.GetString(0));
        return results;
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
