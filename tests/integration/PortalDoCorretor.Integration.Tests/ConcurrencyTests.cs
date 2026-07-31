using System.Data;
using FluentAssertions;
using Npgsql;

namespace PortalDoCorretor.Integration.Tests;

/// <summary>
/// Concorrência, idempotência e Outbox — verificados contra PostgreSQL real.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ConcurrencyTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private const string SystemUser = "00000000-0000-0000-0000-000000000001";

    public async Task InitializeAsync() => await SeedData.EnsureAsync(fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// O cenário central: dois processos tentam emitir apólice para a MESMA proposta,
    /// simultaneamente. O resultado precisa ser exatamente uma apólice.
    /// </summary>
    /// <remarks>
    /// As duas transações abrem, inserem e só então tentam confirmar. O índice único parcial
    /// <c>ux_policies_proposal</c> faz a segunda bloquear até a primeira decidir, e então
    /// falhar com violação de unicidade. Sem essa constraint, as duas passariam — que é
    /// exatamente o que acontece no esquema de comparação.
    /// </remarks>
    [Fact]
    public async Task Emissao_concorrente_produz_exatamente_uma_apolice()
    {
        var proposalId = await CreateApprovedProposalAsync("PR-2026-00009001-1");

        await using var first = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);
        await using var second = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);

        await using var firstTx = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var secondTx = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        await InsertPolicyAsync(first, firstTx, proposalId, "PC-2026-00009001-4", "2026-03-01");

        // A segunda transação fica bloqueada no índice único até a primeira decidir
        var secondInsert = InsertPolicyAsync(second, secondTx, proposalId, "PC-2026-00009002-7", "2030-03-01");

        await firstTx.CommitAsync();

        var act = async () => { await secondInsert; await secondTx.CommitAsync(); };

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);

        await using var check = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);
        var count = await ScalarAsync<long>(check,
            $"SELECT count(*) FROM policies WHERE proposal_id = '{proposalId}'");

        count.Should().Be(1, "as três camadas juntas garantem emissão única");
    }

    /// <summary>
    /// Optimistic locking com <c>xmin</c>: o segundo UPDATE que usa a versão antiga afeta
    /// zero linhas, e é isso que o EF Core traduz em DbUpdateConcurrencyException.
    /// </summary>
    [Fact]
    public async Task Optimistic_lock_por_xmin_detecta_escrita_concorrente()
    {
        var proposalId = await CreateApprovedProposalAsync("PR-2026-00009010-3");
        var policyId = Guid.NewGuid();

        await using var setup = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);
        await InsertPolicyAsync(setup, null, proposalId, "PC-2026-00009010-6", "2035-04-01", policyId);

        // Ambos os "processos" leem a mesma versão
        var originalVersion = await ScalarAsync<uint>(setup,
            $"SELECT xmin FROM policies WHERE id = '{policyId}'");

        await using var writerA = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);
        var affectedA = await ExecAsync(writerA, $"""
            UPDATE policies SET correlation_id = gen_random_uuid()
             WHERE id = '{policyId}' AND xmin = {originalVersion}
            """);

        await using var writerB = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);
        var affectedB = await ExecAsync(writerB, $"""
            UPDATE policies SET correlation_id = gen_random_uuid()
             WHERE id = '{policyId}' AND xmin = {originalVersion}
            """);

        affectedA.Should().Be(1, "a primeira escrita usa a versão vigente");
        affectedB.Should().Be(0, "a segunda usa uma versão obsoleta e não afeta nenhuma linha");
    }

    /// <summary>
    /// A chave de idempotência impede que a mesma chave seja reutilizada para um payload
    /// DIFERENTE — sem o hash do request, a idempotência viraria bypass de validação.
    /// </summary>
    [Fact]
    public async Task Chave_de_idempotencia_e_unica_por_tenant_e_endpoint()
    {
        await using var connection = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);

        await ExecAsync(connection, $"""
            INSERT INTO idempotency_keys (tenant_id, key, endpoint, request_hash, response_status)
            VALUES ('{SeedData.TenantAlfa}', 'abc12345', 'POST /policies', '\x01', 201)
            """);

        var act = async () => await ExecAsync(connection, $"""
            INSERT INTO idempotency_keys (tenant_id, key, endpoint, request_hash, response_status)
            VALUES ('{SeedData.TenantAlfa}', 'abc12345', 'POST /policies', '\x02', 201)
            """);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    /// <summary>
    /// A Outbox é consumida por vários workers com FOR UPDATE SKIP LOCKED: cada mensagem vai
    /// para exatamente um deles, sem contenção e sem duplicação.
    /// </summary>
    [Fact]
    public async Task Outbox_com_skip_locked_nao_entrega_a_mesma_mensagem_a_dois_workers()
    {
        await using var seed = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);

        for (var i = 0; i < 4; i++)
            await ExecAsync(seed, $"""
                INSERT INTO outbox_messages (tenant_id, message_type, payload, correlation_id,
                                             aggregate_type, aggregate_id)
                VALUES ('{SeedData.TenantAlfa}', 'PolicyIssued', jsonb_build_object('n', {i}),
                        gen_random_uuid(), 'Policy', gen_random_uuid())
                """);

        const string claimBatch = """
            SELECT id FROM outbox_messages
             WHERE processed_at IS NULL AND next_attempt_at <= now()
             ORDER BY occurred_at
             LIMIT 2
             FOR UPDATE SKIP LOCKED
            """;

        await using var workerA = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);
        await using var workerB = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);

        await using var txA = await workerA.BeginTransactionAsync();
        await using var txB = await workerB.BeginTransactionAsync();

        var claimedByA = await ListGuidsAsync(workerA, claimBatch, txA);
        var claimedByB = await ListGuidsAsync(workerB, claimBatch, txB);

        await txA.RollbackAsync();
        await txB.RollbackAsync();

        claimedByA.Should().HaveCount(2);
        claimedByB.Should().HaveCount(2);
        claimedByA.Should().NotIntersectWith(claimedByB,
            "SKIP LOCKED garante que cada mensagem seja processada por um único worker");
    }

    /// <summary>
    /// Rollback: uma falha em qualquer ponto da transação de emissão não deixa resíduo —
    /// nem apólice, nem cobertura, nem linha de Outbox.
    /// </summary>
    [Fact]
    public async Task Falha_no_meio_da_transacao_nao_deixa_residuo()
    {
        var proposalId = await CreateApprovedProposalAsync("PR-2026-00009020-8");
        var policyId = Guid.NewGuid();

        await using var connection = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);
        await using var transaction = await connection.BeginTransactionAsync();

        await InsertPolicyAsync(connection, transaction, proposalId, "PC-2026-00009020-1",
                                "2040-05-01", policyId);

        await ExecAsync(connection, $"""
            INSERT INTO outbox_messages (tenant_id, message_type, payload, correlation_id,
                                         aggregate_type, aggregate_id)
            VALUES ('{SeedData.TenantAlfa}', 'PolicyIssued', jsonb_build_object(), gen_random_uuid(),
                    'Policy', '{policyId}')
            """, transaction);

        // Falha injetada: prêmio negativo viola ck_policies_premium_positive
        var act = async () => await ExecAsync(connection,
            $"UPDATE policies SET total_premium = ROW(-1,'BRL')::money_amount WHERE id = '{policyId}'",
            transaction);

        await act.Should().ThrowAsync<PostgresException>();
        await transaction.RollbackAsync();

        await using var check = await fixture.OpenAsAppUserAsync(SeedData.TenantAlfa);
        var policies = await ScalarAsync<long>(check,
            $"SELECT count(*) FROM policies WHERE id = '{policyId}'");
        var outbox = await ScalarAsync<long>(check,
            $"SELECT count(*) FROM outbox_messages WHERE aggregate_id = '{policyId}'");

        policies.Should().Be(0);
        outbox.Should().Be(0, "estado e evento são atômicos: ou ambos, ou nenhum");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Cria uma cotação nova a cada chamada, e não reaproveita a do seed.
    /// </summary>
    /// <remarks>
    /// A constraint <c>ux_proposals_quotation_active</c> permite no máximo uma proposta viva
    /// por cotação. Reaproveitar a mesma cotação fazia o segundo teste falhar na preparação —
    /// a constraint estava correta, o helper é que estava errado.
    /// </remarks>
    private async Task<Guid> CreateApprovedProposalAsync(string number)
    {
        var quotationId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var suffix = Math.Abs(quotationId.GetHashCode()) % 100_000_000;

        await using var connection = await fixture.OpenAsMigratorAsync();
        await ExecAsync(connection, $"""
            SELECT set_config('app.tenant_id', '{SeedData.TenantAlfa}', false),
                   set_config('app.user_profile', 'BROKER', false);

            INSERT INTO quotations (id, tenant_id, broker_id, customer_id, asset_id,
                   product_version_id, number, status, risk_score, created_by, expires_at)
            VALUES ('{quotationId}', '{SeedData.TenantAlfa}', '{SeedData.BrokerAna}',
                    '{SeedData.CustomerAlfa}', '{SeedData.AssetAlfa}', '{SeedData.ProductVersion}',
                    'CT-2026-{suffix:D8}-0', 'CALCULATED', 300, '{SystemUser}',
                    now() + interval '30 days');

            INSERT INTO proposals (id, tenant_id, quotation_id, broker_id, customer_id, number,
                   status, chosen_plan, net_premium, total_premium, created_by)
            VALUES ('{proposalId}', '{SeedData.TenantAlfa}', '{quotationId}',
                    '{SeedData.BrokerAna}', '{SeedData.CustomerAlfa}', '{number}', 'APPROVED',
                    'COMPLETE', ROW(2000.00,'BRL')::money_amount,
                    ROW(2400.00,'BRL')::money_amount, '{SystemUser}')
            """);

        return proposalId;
    }

    private static Task InsertPolicyAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
                                          Guid proposalId, string number, string start,
                                          Guid? policyId = null) =>
        ExecAsync(connection, $"""
            INSERT INTO policies (id, tenant_id, proposal_id, broker_id, customer_id, asset_id,
                   product_version_id, number, status, coverage_period, net_premium, total_premium,
                   issued_by, correlation_id)
            VALUES ('{policyId ?? Guid.NewGuid()}', '{SeedData.TenantAlfa}', '{proposalId}',
                    '{SeedData.BrokerAna}', '{SeedData.CustomerAlfa}', '{SeedData.AssetAlfa}',
                    '{SeedData.ProductVersion}', '{number}', 'ACTIVE',
                    daterange('{start}', ('{start}'::date + interval '1 year')::date),
                    ROW(2000.00,'BRL')::money_amount, ROW(2400.00,'BRL')::money_amount,
                    '{SystemUser}', gen_random_uuid())
            """, transaction);

    private static async Task<int> ExecAsync(NpgsqlConnection connection, string sql,
                                             NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<Guid>> ListGuidsAsync(NpgsqlConnection connection, string sql,
                                                          NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();

        var results = new List<Guid>();
        while (await reader.ReadAsync()) results.Add(reader.GetGuid(0));
        return results;
    }
}
