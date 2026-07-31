using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Dapper;
using Npgsql;

namespace PortalDoCorretor.SecureApi;

/// <summary>Estorno de comissão — o motivo entra na auditoria.</summary>
public sealed class ReversalInput
{
    [Required(ErrorMessage = "Motivo do estorno é obrigatório.")]
    [StringLength(300, MinimumLength = 5, ErrorMessage = "Motivo deve ter ao menos 5 caracteres.")]
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Comissões: extrato, consolidação mensal, liberação e estorno.
/// </summary>
/// <remarks>
/// Além do isolamento por tenant, a tabela <c>commissions</c> tem uma política
/// <c>RESTRICTIVE</c> que filtra por <c>broker_id = app.current_actor()</c>: um corretor
/// enxerga apenas as próprias comissões, nunca as do colega, mesmo dentro do próprio tenant.
/// É a segunda dimensão de autorização (ABAC) atuando sobre a primeira.
/// </remarks>
public static class CommissionEndpoints
{
    public static void MapCommissionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/commissions").WithTags("Comissões");

        group.MapGet("", ListAsync);
        group.MapGet("/monthly", MonthlyAsync);
        group.MapPost("/{id:guid}/release", ReleaseAsync);
        group.MapPost("/{id:guid}/reverse", ReverseAsync);
    }

    private static async Task<IResult> ListAsync(
        RequestContext ctx, IDbConnectionFactory factory, ActivityStream stream,
        string? status, int page = 1, int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ctx.OpenScopedAsync(factory);

        const string filter = """
              FROM commissions cm
              JOIN policies p  ON p.id  = cm.policy_id
              JOIN brokers  b  ON b.id  = cm.broker_id
              JOIN customers c ON c.id  = p.customer_id
             WHERE (@status IS NULL OR cm.status::text = @status)
            """;

        var parameters = new
        {
            status = string.IsNullOrWhiteSpace(status) ? null : status,
            offset = (page - 1) * pageSize,
            limit = pageSize
        };

        var total = await connection.ExecuteScalarAsync<int>($"SELECT count(*) {filter}", parameters);

        // rule_id, rule_version, rate_applied e base_amount juntos respondem de forma
        // auditável "por que essa comissão foi esse valor?" — mesmo que a regra mude depois.
        var items = await connection.QueryAsync($"""
            SELECT cm.id, cm.status::text AS status,
                   (cm.amount).amount      AS "amount",
                   (cm.base_amount).amount AS "baseAmount",
                   cm.rate_applied         AS "rateApplied",
                   cm.rule_version         AS "ruleVersion",
                   cm.reference_month      AS "referenceMonth",
                   cm.created_at           AS "createdAt",
                   cm.released_at          AS "releasedAt",
                   cm.reversed_from_id     AS "reversedFromId",
                   p.number                AS "policyNumber",
                   p.id                    AS "policyId",
                   b.full_name             AS "brokerName",
                   CASE c.kind WHEN 'INDIVIDUAL'
                        THEN c.first_name || ' ' || c.last_name
                        ELSE coalesce(c.trade_name, c.legal_name) END AS "customerName"
            {filter}
             ORDER BY cm.reference_month DESC, cm.created_at DESC
             OFFSET @offset LIMIT @limit
            """, parameters);

        var list = items.ToList();
        var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        stream.Publish(new ProcessingEvent(
            "DatabaseQuery", "Commissions", "commissions:list",
            $"{list.Count} de {total} comissão(ões) — política RESTRICTIVE por broker_id aplicada",
            "SUCCESS", "Commission", null, ctx.TenantId, ctx.CorrelationId, elapsed));

        return Results.Ok(new Page<object>(list.Cast<object>().ToList(), total, page, pageSize));
    }

    private static async Task<IResult> MonthlyAsync(RequestContext ctx, IDbConnectionFactory factory)
    {
        await using var connection = await ctx.OpenScopedAsync(factory);

        return Results.Ok(await connection.QueryAsync("""
            SELECT cm.reference_month AS "referenceMonth",
                   count(*)                                                        AS "count",
                   coalesce(sum((cm.amount).amount), 0)                            AS "total",
                   coalesce(sum((cm.amount).amount)
                            FILTER (WHERE cm.status = 'FORECAST'), 0)              AS "forecast",
                   coalesce(sum((cm.amount).amount)
                            FILTER (WHERE cm.status = 'RELEASED'), 0)              AS "released",
                   coalesce(sum((cm.amount).amount)
                            FILTER (WHERE cm.status = 'PAID'), 0)                  AS "paid",
                   coalesce(sum((cm.amount).amount)
                            FILTER (WHERE cm.status = 'REVERSED'), 0)              AS "reversed"
              FROM commissions cm
             GROUP BY cm.reference_month
             ORDER BY cm.reference_month DESC
             LIMIT 24
            """));
    }

    private static async Task<IResult> ReleaseAsync(
        Guid id, RequestContext ctx, IDbConnectionFactory factory, ActivityStream stream)
    {
        await using var connection = await ctx.OpenScopedAsync(factory);

        var affected = await connection.ExecuteAsync("""
            UPDATE commissions
               SET status = 'RELEASED', released_at = now()
             WHERE id = @id AND status = 'FORECAST'
            """, new { id });

        if (affected == 0)
            return Results.Conflict(new
            {
                message = "Comissão não encontrada ou não está prevista.",
                code = "COMMISSION_NOT_RELEASABLE"
            });

        stream.Publish(new ProcessingEvent(
            "DomainEvent", "Commissions", "commissions:release",
            "Comissão liberada", "SUCCESS", "Commission", id,
            ctx.TenantId, ctx.CorrelationId, null,
            "UPDATE commissions SET status='RELEASED' WHERE status='FORECAST'"));

        return Results.Ok(new { id, status = "RELEASED" });
    }

    /// <summary>
    /// Estorna criando um lançamento INVERSO, nunca alterando o original.
    /// </summary>
    /// <remarks>
    /// A constraint <c>ck_commissions_amount_sign</c> exige valor ≤ 0 quando o status é
    /// REVERSED, e <c>ck_commissions_reversal</c> exige que o estorno aponte para a origem.
    /// Sobrescrever o lançamento original destruiria o rastro contábil — o histórico precisa
    /// mostrar que houve uma comissão e depois um estorno, não que a comissão nunca existiu.
    /// </remarks>
    private static async Task<IResult> ReverseAsync(
        Guid id, ReversalInput input, RequestContext ctx,
        IDbConnectionFactory factory, ActivityStream stream)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), results, true))
            return Results.UnprocessableEntity(new ValidationProblem("Dados inválidos.",
                new Dictionary<string, string[]> { ["Reason"] = [results[0].ErrorMessage ?? "inválido"] }));

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ctx.OpenScopedAsync(factory);
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var origin = await connection.QuerySingleOrDefaultAsync("""
                SELECT id, tenant_id AS "tenantId", policy_id AS "policyId",
                       broker_id AS "brokerId", rule_id AS "ruleId",
                       rule_version AS "ruleVersion", rate_applied AS "rateApplied",
                       (base_amount).amount AS "baseAmount", (amount).amount AS "amount",
                       reference_month AS "referenceMonth", status::text AS status
                  FROM commissions WHERE id = @id
                """, new { id }, transaction);

            if (origin is null)
            {
                await transaction.RollbackAsync();
                return Results.NotFound(new { message = "Comissão não encontrada." });
            }

            if ((string)origin.status == "REVERSED")
            {
                await transaction.RollbackAsync();
                return Results.Conflict(new
                {
                    message = "Esta comissão já é um estorno.",
                    code = "COMMISSION_ALREADY_REVERSAL"
                });
            }

            var alreadyReversed = await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM commissions WHERE reversed_from_id = @id",
                new { id }, transaction);

            if (alreadyReversed > 0)
            {
                await transaction.RollbackAsync();
                return Results.Conflict(new
                {
                    message = "Comissão já estornada.",
                    code = "COMMISSION_ALREADY_REVERSED"
                });
            }

            var reversalId = Guid.CreateVersion7();

            await connection.ExecuteAsync("""
                INSERT INTO commissions (id, tenant_id, policy_id, broker_id, rule_id, rule_version,
                       rate_applied, base_amount, amount, status, reversed_from_id, reference_month)
                VALUES (@reversalId, @tenantId, @policyId, @brokerId, @ruleId, @ruleVersion,
                        @rateApplied, ROW(@baseAmount,'BRL')::money_amount,
                        ROW(@negatedAmount,'BRL')::money_amount, 'REVERSED', @originId,
                        @referenceMonth)
                """, new
            {
                reversalId,
                tenantId = (Guid)origin.tenantId,
                policyId = (Guid)origin.policyId,
                brokerId = (Guid)origin.brokerId,
                ruleId = (Guid)origin.ruleId,
                ruleVersion = (int)origin.ruleVersion,
                rateApplied = (decimal)origin.rateApplied,
                baseAmount = (decimal)origin.baseAmount,
                negatedAmount = -(decimal)origin.amount,
                originId = id,
                referenceMonth = (DateTime)origin.referenceMonth
            }, transaction);

            await transaction.CommitAsync();

            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            stream.Publish(new ProcessingEvent(
                "AuditEvent", "Commissions", "commissions:reverse",
                $"Estorno lançado — motivo: {input.Reason.Trim()}", "SUCCESS",
                "Commission", reversalId, ctx.TenantId, ctx.CorrelationId, elapsed,
                "INSERT INTO commissions (… amount negativo, reversed_from_id = origem)"));

            return Results.Ok(new { reversalId, reversedFrom = id, status = "REVERSED" });
        }
        catch (PostgresException ex)
        {
            await transaction.RollbackAsync();

            stream.Publish(new ProcessingEvent(
                "Error", "Commissions", "commissions:reverse",
                $"Banco recusou — {ex.SqlState} {ex.ConstraintName}", "ERROR",
                "Commission", id, ctx.TenantId, ctx.CorrelationId));

            return Results.BadRequest(new { message = "Não foi possível estornar a comissão." });
        }
    }
}
