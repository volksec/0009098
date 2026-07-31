using Dapper;
using Npgsql;

namespace PortalDoCorretor.Workers;

/// <summary>
/// Publica as mensagens da Outbox e marca como processadas.
/// </summary>
/// <remarks>
/// <para>
/// A consulta usa <c>FOR UPDATE SKIP LOCKED</c>: vários workers podem rodar em paralelo e
/// cada mensagem vai para exatamente um deles, sem contenção e sem duplicação. Sem
/// <c>SKIP LOCKED</c>, o segundo worker ficaria bloqueado esperando o primeiro em vez de
/// pegar o próximo lote.
/// </para>
/// <para>
/// A entrega é <b>ao menos uma vez</b>. Exatamente-uma-vez é inalcançável sem coordenação
/// distribuída, então o consumidor precisa ser idempotente por construção — é o papel de
/// <c>processed_messages</c>.
/// </para>
/// <para>
/// Roda como <c>app_worker</c>, que tem política de RLS própria para atravessar tenants:
/// o dispatcher processa a fila inteira, não a de um tenant só.
/// </para>
/// </remarks>
public sealed class OutboxDispatcher(
    IDbConnectionFactory factory,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private const int BatchSize = 100;
    private const int MaxAttempts = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox Dispatcher iniciado (lote {BatchSize})", BatchSize);

        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var processed = await DispatchBatchAsync(stoppingToken);
                if (processed > 0)
                    logger.LogInformation("Outbox: {Count} mensagem(ns) publicada(s)", processed);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Um erro de infraestrutura não pode derrubar o worker: ele volta no próximo tick
                logger.LogError(ex, "Falha no ciclo do Outbox Dispatcher");
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var batch = (await connection.QueryAsync<OutboxRow>("""
            SELECT id, occurred_at AS "OccurredAt", message_type AS "MessageType",
                   aggregate_type AS "AggregateType", aggregate_id AS "AggregateId",
                   attempts AS "Attempts"
              FROM outbox_messages
             WHERE processed_at IS NULL
               AND next_attempt_at <= now()
               AND attempts < @maxAttempts
             ORDER BY occurred_at
             LIMIT @batchSize
             FOR UPDATE SKIP LOCKED
            """, new { batchSize = BatchSize, maxAttempts = MaxAttempts }, transaction)).ToList();

        if (batch.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        var succeeded = new List<(Guid Id, DateTimeOffset OccurredAt)>();
        var failed = new List<(Guid Id, DateTimeOffset OccurredAt, string Error)>();

        foreach (var message in batch)
        {
            try
            {
                await PublishAsync(connection, transaction, message, cancellationToken);
                succeeded.Add((message.Id, message.OccurredAt));
            }
            catch (Exception ex)
            {
                failed.Add((message.Id, message.OccurredAt, ex.Message));
            }
        }

        if (succeeded.Count > 0)
            await connection.ExecuteAsync("""
                UPDATE outbox_messages SET processed_at = now()
                 WHERE id = @Id AND occurred_at = @OccurredAt
                """, succeeded.Select(x => new { x.Id, x.OccurredAt }), transaction);

        foreach (var (id, occurredAt, error) in failed)
        {
            // Backoff exponencial: 2^tentativas segundos, limitado a 5 minutos
            await connection.ExecuteAsync("""
                UPDATE outbox_messages
                   SET attempts = attempts + 1,
                       last_error = @error,
                       next_attempt_at = now() + least(
                           power(2, attempts + 1) * interval '1 second',
                           interval '5 minutes')
                 WHERE id = @id AND occurred_at = @occurredAt
                """, new { id, occurredAt, error = Truncate(error, 500) }, transaction);
        }

        await transaction.CommitAsync(cancellationToken);
        return succeeded.Count;
    }

    /// <summary>
    /// Consumo de uma mensagem. O registro em <c>processed_messages</c> torna o consumo
    /// idempotente: reprocessar a mesma mensagem não gera efeito duplicado.
    /// </summary>
    private static async Task PublishAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        OutboxRow message, CancellationToken cancellationToken)
    {
        const string consumer = "notifications";

        var alreadyProcessed = await connection.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM processed_messages
             WHERE message_id = @id AND consumer = @consumer
            """, new { id = message.Id, consumer }, transaction);

        if (alreadyProcessed > 0) return;

        await connection.ExecuteAsync("""
            INSERT INTO processed_messages (message_id, consumer)
            VALUES (@id, @consumer)
            ON CONFLICT DO NOTHING
            """, new { id = message.Id, consumer }, transaction);

        await Task.CompletedTask;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private sealed record OutboxRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public string MessageType { get; init; } = string.Empty;
        public string AggregateType { get; init; } = string.Empty;
        public Guid AggregateId { get; init; }
        public short Attempts { get; init; }
    }
}
