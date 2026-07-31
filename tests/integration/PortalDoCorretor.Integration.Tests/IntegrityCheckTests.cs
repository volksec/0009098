using FluentAssertions;
using Npgsql;

namespace PortalDoCorretor.Integration.Tests;

/// <summary>
/// Regressão de um bug real: <c>app.run_integrity_checks()</c> era SECURITY INVOKER e
/// rodava sob RLS como <c>app_worker</c>, sem contexto de tenant. Cada consulta interna
/// enxergava zero linhas, então a função devolvia zero divergências para tudo.
/// </summary>
/// <remarks>
/// <para>
/// O modo de falha era silencioso: o worker registrava "10 verificações, nenhuma
/// divergência" enquanto a base tinha 147 apólices sem cobertura. Um monitor que nunca
/// acusa nada é indistinguível de um monitor quebrado — e só um teste que <b>quebra a
/// invariante de propósito</b> distingue os dois casos.
/// </para>
/// <para>
/// Por isso o teste central não se contenta em ver zero: ele introduz a violação e exige
/// que a contagem suba exatamente um.
/// </para>
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public sealed class IntegrityCheckTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private const string SystemUser = "00000000-0000-0000-0000-000000000001";

    public async Task InitializeAsync() => await SeedData.EnsureAsync(fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Verificacao_roda_como_worker_sem_contexto_de_tenant()
    {
        await using var worker = await OpenAsWorkerAsync();

        var checks = await RunChecksAsync(worker);

        checks.Should().HaveCount(10,
            "as dez verificações precisam retornar mesmo sem contexto de tenant");
    }

    /// <summary>
    /// O teste central: com RLS ativa e sem tenant, a função enxergaria zero linhas e
    /// esconderia a violação. Se este teste falhar, o monitor voltou a ser cego.
    /// </summary>
    [Fact]
    public async Task Apolice_sem_cobertura_e_detectada_pelo_worker()
    {
        long antes;
        await using (var worker = await OpenAsWorkerAsync())
            antes = (await RunChecksAsync(worker))["POLICY_WITHOUT_COVERAGE"];

        var policyId = await CreateUncoveredPolicyAsync();

        try
        {
            await using var worker = await OpenAsWorkerAsync();
            var depois = (await RunChecksAsync(worker))["POLICY_WITHOUT_COVERAGE"];

            depois.Should().Be(antes + 1,
                "sob RLS sem contexto de tenant a função enxergaria zero linhas e "
              + "esconderia a apólice sem cobertura recém-criada");
        }
        finally
        {
            await using var migrator = await fixture.OpenAsMigratorAsync();
            await ExecuteAsync(migrator, "DELETE FROM policies WHERE id = @id", policyId);
        }
    }

    /// <summary>
    /// SECURITY DEFINER sem restrição de EXECUTE seria escalada de privilégio: qualquer
    /// papel poderia rodar código com os privilégios do dono do schema.
    /// </summary>
    [Fact]
    public async Task Execucao_permanece_restrita_apesar_do_security_definer()
    {
        await using var connection = await fixture.OpenAsMigratorAsync();

        await using var command = new NpgsqlCommand(
            "SELECT prosecdef, coalesce(array_to_string(proconfig, ','), '') AS config, "
          + "       has_function_privilege('public', oid, 'EXECUTE') AS public_pode "
          + "  FROM pg_proc WHERE proname = 'run_integrity_checks'", connection);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetBoolean(0).Should().BeTrue("a função precisa ser SECURITY DEFINER");
        reader.GetString(1).Should().Contain("search_path",
            "DEFINER sem search_path fixo abre sequestro de resolução de nomes");
        reader.GetBoolean(2).Should().BeFalse("PUBLIC não pode executar a função");
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

    private static async Task<Dictionary<string, long>> RunChecksAsync(NpgsqlConnection connection)
    {
        var results = new Dictionary<string, long>();

        await using var command = new NpgsqlCommand(
            "SELECT check_code, failure_count FROM app.run_integrity_checks()", connection);
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            results[reader.GetString(0)] = reader.GetInt64(1);

        return results;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, Guid? id = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (id.HasValue) command.Parameters.AddWithValue("id", id.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Apólice ativa sem nenhuma cobertura — a violação que o teste precisa que seja vista.
    /// Traz cotação e proposta próprias porque <c>proposal_id</c> é obrigatório e
    /// <c>ux_policies_proposal</c> não admite uma segunda apólice viva por proposta.
    /// </summary>
    private async Task<Guid> CreateUncoveredPolicyAsync()
    {
        var quotationId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var suffix = Math.Abs(policyId.GetHashCode()) % 100_000_000;

        await using var connection = await fixture.OpenAsMigratorAsync();

        await ExecuteAsync(connection, $"""
            SELECT set_config('app.tenant_id', '{SeedData.TenantAlfa}', false),
                   set_config('app.user_profile', 'BROKER', false);

            INSERT INTO quotations (id, tenant_id, broker_id, customer_id, asset_id,
                   product_version_id, number, status, risk_score, created_by, expires_at)
            VALUES ('{quotationId}', '{SeedData.TenantAlfa}', '{SeedData.BrokerAna}',
                    '{SeedData.CustomerAlfa}', '{SeedData.AssetAlfa}', '{SeedData.ProductVersion}',
                    'CT-2026-{suffix:D8}-0', 'CONVERTED', 300, '{SystemUser}',
                    now() + interval '30 days');

            INSERT INTO proposals (id, tenant_id, quotation_id, broker_id, customer_id, number,
                   status, chosen_plan, net_premium, total_premium, created_by)
            VALUES ('{proposalId}', '{SeedData.TenantAlfa}', '{quotationId}',
                    '{SeedData.BrokerAna}', '{SeedData.CustomerAlfa}',
                    'PR-2026-{suffix:D8}-0', 'APPROVED', 'COMPLETE',
                    ROW(2000.00,'BRL')::money_amount, ROW(2400.00,'BRL')::money_amount,
                    '{SystemUser}');

            INSERT INTO policies (id, tenant_id, proposal_id, broker_id, customer_id, asset_id,
                   product_version_id, number, status, coverage_period, net_premium,
                   total_premium, issued_by, correlation_id)
            VALUES ('{policyId}', '{SeedData.TenantAlfa}', '{proposalId}', '{SeedData.BrokerAna}',
                    '{SeedData.CustomerAlfa}', '{SeedData.AssetAlfa}', '{SeedData.ProductVersion}',
                    'PC-2026-{suffix:D8}-0', 'ACTIVE',
                    daterange(current_date + 500, current_date + 865),
                    ROW(2000.00,'BRL')::money_amount, ROW(2400.00,'BRL')::money_amount,
                    '{SystemUser}', gen_random_uuid())
            """);

        return policyId;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
