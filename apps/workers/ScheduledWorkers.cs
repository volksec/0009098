using Dapper;

namespace PortalDoCorretor.Workers;

/// <summary>
/// Detecta apólices próximas do vencimento e abre o ciclo de renovação.
/// </summary>
/// <remarks>
/// A consulta usa o índice parcial <c>ix_policies_expiring</c>, que indexa apenas apólices
/// ativas — mantém o índice pequeno mesmo com o histórico crescendo.
///
/// A idempotência vem da constraint <c>ux_renewals_policy_cycle</c>: rodar duas vezes no
/// mesmo dia não cria dois registros de renovação para a mesma apólice e ciclo.
/// </remarks>
public sealed class RenewalScanner(
    IDbConnectionFactory factory,
    ILogger<RenewalScanner> logger) : ScheduledWorker(TimeSpan.FromHours(6), logger)
{
    private const int NoticeWindowDays = 45;

    protected override string Name => "Renewal Scanner";

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken);

        var detected = await connection.ExecuteAsync("""
            INSERT INTO renewals (tenant_id, policy_id, cycle, outcome, detected_at)
            SELECT p.tenant_id, p.id,
                   -- O ciclo é o ano de término: uma renovação por apólice e por ano
                   extract(year FROM upper(p.coverage_period))::int,
                   'PENDING', now()
              FROM policies p
             WHERE p.status = 'ACTIVE'
               AND upper(p.coverage_period) <= CURRENT_DATE + @window
               AND upper(p.coverage_period) > CURRENT_DATE
            ON CONFLICT (policy_id, cycle) DO NOTHING
            """, new { window = NoticeWindowDays });

        if (detected > 0)
            Logger.LogInformation("{Name}: {Count} renovação(ões) aberta(s)", Name, detected);
    }
}

/// <summary>
/// Marca parcelas vencidas.
/// </summary>
/// <remarks>
/// O estado <c>OVERDUE</c> é materializado por este worker porque precisa ser filtrável e
/// contável. A consulta de leitura ainda deriva <c>isOverdue</c> por comparação de data, de
/// modo que a interface fica correta mesmo entre duas execuções do worker.
/// </remarks>
public sealed class BillingScheduler(
    IDbConnectionFactory factory,
    ILogger<BillingScheduler> logger) : ScheduledWorker(TimeSpan.FromHours(1), logger)
{
    protected override string Name => "Billing Scheduler";

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken);

        var overdue = await connection.ExecuteAsync("""
            UPDATE installments
               SET status = 'OVERDUE'
             WHERE status = 'PENDING' AND due_date < CURRENT_DATE
            """);

        if (overdue > 0)
            Logger.LogInformation("{Name}: {Count} parcela(s) marcada(s) como vencida(s)", Name, overdue);
    }
}

/// <summary>
/// Expira cotações vencidas.
/// </summary>
public sealed class QuotationExpirer(
    IDbConnectionFactory factory,
    ILogger<QuotationExpirer> logger) : ScheduledWorker(TimeSpan.FromHours(1), logger)
{
    protected override string Name => "Quotation Expirer";

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken);

        // Usa ix_quotations_expiring, que indexa só o que ainda pode expirar
        var expired = await connection.ExecuteAsync("""
            UPDATE quotations
               SET status = 'EXPIRED'
             WHERE status IN ('DRAFT','CALCULATED')
               AND expires_at <= now()
               AND deleted_at IS NULL
            """);

        if (expired > 0)
            Logger.LogInformation("{Name}: {Count} cotação(ões) expirada(s)", Name, expired);
    }
}

/// <summary>
/// Executa as asserções de integridade e grava o resultado.
/// </summary>
/// <remarks>
/// É o que transforma integridade de suposição em medição. Se o modelo estiver correto,
/// todas as verificações retornam zero — qualquer valor diferente indica que uma invariante
/// foi contornada, por bug, script manual ou migration errada.
/// </remarks>
public sealed class IntegrityChecker(
    IDbConnectionFactory factory,
    ILogger<IntegrityChecker> logger) : ScheduledWorker(TimeSpan.FromHours(12), logger)
{
    protected override string Name => "Integrity Checker";

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var results = (await connection.QueryAsync<(string CheckCode, long FailureCount)>(
            "SELECT check_code, failure_count FROM app.run_integrity_checks()")).ToList();

        var elapsed = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        foreach (var (code, failures) in results)
        {
            await connection.ExecuteAsync("""
                INSERT INTO integrity_check_results (check_code, failure_count, duration_ms, details)
                VALUES (@code, @failures, @elapsed, NULL)
                """, new { code, failures = (int)failures, elapsed });
        }

        var broken = results.Where(r => r.FailureCount > 0).ToList();

        if (broken.Count == 0)
        {
            Logger.LogInformation("{Name}: {Total} verificação(ões), nenhuma divergência", Name, results.Count);
            return;
        }

        // Divergência de integridade é sinal de invariante contornada — merece nível de erro
        foreach (var (code, failures) in broken)
            Logger.LogError("{Name}: {Code} encontrou {Failures} divergência(s)", Name, code, failures);
    }
}

/// <summary>Base dos workers periódicos: laço, cancelamento e isolamento de falha.</summary>
public abstract class ScheduledWorker(TimeSpan interval, ILogger logger) : BackgroundService
{
    protected ILogger Logger { get; } = logger;
    protected abstract string Name { get; }
    protected abstract Task RunAsync(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("{Name} iniciado (intervalo {Interval})", Name, interval);

        // Executa uma vez ao subir, para que o efeito seja visível sem esperar o intervalo
        await SafeRunAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SafeRunAsync(stoppingToken);
    }

    private async Task SafeRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A falha de um ciclo não pode encerrar o worker
            Logger.LogError(ex, "{Name}: falha no ciclo", Name);
        }
    }
}
