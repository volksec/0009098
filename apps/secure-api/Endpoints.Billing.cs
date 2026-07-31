using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Dapper;
using Npgsql;

namespace PortalDoCorretor.SecureApi;

/// <summary>Registro de pagamento simulado de parcela.</summary>
public sealed class PaymentInput
{
    [Required(ErrorMessage = "Meio de pagamento é obrigatório.")]
    [RegularExpression("^SIMULATED_(BOLETO|CARD|PIX)$",
        ErrorMessage = "Meio deve ser SIMULATED_BOLETO, SIMULATED_CARD ou SIMULATED_PIX.")]
    public string Method { get; init; } = "SIMULATED_BOLETO";
}

/// <summary>
/// Faturamento: plano de parcelas, pagamento simulado e inadimplência.
/// </summary>
public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/billing").WithTags("Faturamento");

        group.MapGet("/installments", ListAsync);
        group.MapGet("/policies/{policyId:guid}/installments", ByPolicyAsync);
        group.MapPost("/installments/{id:guid}/pay", PayAsync);
        group.MapGet("/summary", SummaryAsync);
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
              FROM installments i
              JOIN installment_plans pl ON pl.id = i.plan_id
              JOIN policies p           ON p.id  = pl.policy_id
              JOIN customers c          ON c.id  = p.customer_id
             WHERE (@status IS NULL OR i.status::text = @status)
            """;

        var parameters = new
        {
            status = string.IsNullOrWhiteSpace(status) ? null : status,
            offset = (page - 1) * pageSize,
            limit = pageSize
        };

        var total = await connection.ExecuteScalarAsync<int>($"SELECT count(*) {filter}", parameters);

        var items = await connection.QueryAsync($"""
            SELECT i.id, i.sequence, (i.amount).amount AS "amount",
                   i.due_date AS "dueDate", i.status::text AS status, i.paid_at AS "paidAt",
                   p.number AS "policyNumber", p.id AS "policyId",
                   CASE c.kind WHEN 'INDIVIDUAL'
                        THEN c.first_name || ' ' || c.last_name
                        ELSE coalesce(c.trade_name, c.legal_name) END AS "customerName",
                   -- Vencida é derivado, não armazenado: evita estado que envelhece errado
                   (i.status = 'PENDING' AND i.due_date < CURRENT_DATE) AS "isOverdue"
            {filter}
             ORDER BY i.due_date, i.sequence
             OFFSET @offset LIMIT @limit
            """, parameters);

        var list = items.ToList();
        var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        stream.Publish(new ProcessingEvent(
            "DatabaseQuery", "Billing", "billing:installments:list",
            $"{list.Count} de {total} parcela(s)", "SUCCESS",
            "Installment", null, ctx.TenantId, ctx.CorrelationId, elapsed));

        return Results.Ok(new Page<object>(list.Cast<object>().ToList(), total, page, pageSize));
    }

    private static async Task<IResult> ByPolicyAsync(
        Guid policyId, RequestContext ctx, IDbConnectionFactory factory)
    {
        await using var connection = await ctx.OpenScopedAsync(factory);

        var plan = await connection.QuerySingleOrDefaultAsync("""
            SELECT pl.id, (pl.total_amount).amount AS "totalAmount",
                   pl.installment_count AS "installmentCount",
                   p.number AS "policyNumber"
              FROM installment_plans pl
              JOIN policies p ON p.id = pl.policy_id
             WHERE pl.policy_id = @policyId
            """, new { policyId });

        if (plan is null) return Results.NotFound(new { message = "Plano de parcelas não encontrado." });

        var installments = await connection.QueryAsync("""
            SELECT i.id, i.sequence, (i.amount).amount AS "amount",
                   i.due_date AS "dueDate", i.status::text AS status, i.paid_at AS "paidAt",
                   (i.status = 'PENDING' AND i.due_date < CURRENT_DATE) AS "isOverdue"
              FROM installments i
              JOIN installment_plans pl ON pl.id = i.plan_id
             WHERE pl.policy_id = @policyId
             ORDER BY i.sequence
            """, new { policyId });

        return Results.Ok(new { plan, installments });
    }

    /// <summary>
    /// Registra pagamento simulado. A parcela e o pagamento são confirmados juntos.
    /// </summary>
    private static async Task<IResult> PayAsync(
        Guid id, PaymentInput input, RequestContext ctx,
        IDbConnectionFactory factory, ActivityStream stream)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), results, true))
            return Results.UnprocessableEntity(new ValidationProblem("Dados inválidos.",
                new Dictionary<string, string[]> { ["Method"] = [results[0].ErrorMessage ?? "inválido"] }));

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ctx.OpenScopedAsync(factory);
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // ck_installments_paid exige que paid_at exista se e somente se o status for PAID,
            // então os dois campos mudam na mesma instrução.
            var affected = await connection.ExecuteAsync("""
                UPDATE installments
                   SET status = 'PAID', paid_at = now()
                 WHERE id = @id AND status IN ('PENDING','OVERDUE')
                """, new { id }, transaction);

            if (affected == 0)
            {
                await transaction.RollbackAsync();
                return Results.Conflict(new
                {
                    message = "Parcela não encontrada ou já quitada.",
                    code = "INSTALLMENT_NOT_PAYABLE"
                });
            }

            await connection.ExecuteAsync("""
                INSERT INTO payments (tenant_id, installment_id, amount, method)
                SELECT i.tenant_id, i.id, i.amount, @method::varchar
                  FROM installments i WHERE i.id = @id
                """, new { id, method = input.Method }, transaction);

            await transaction.CommitAsync();

            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            stream.Publish(new ProcessingEvent(
                "DomainEvent", "Billing", "billing:installment:paid",
                $"Parcela quitada — {input.Method} (simulado)", "SUCCESS",
                "Installment", id, ctx.TenantId, ctx.CorrelationId, elapsed,
                "UPDATE installments SET status='PAID', paid_at=now(); INSERT INTO payments …"));

            return Results.Ok(new { id, status = "PAID" });
        }
        catch (PostgresException ex)
        {
            await transaction.RollbackAsync();

            stream.Publish(new ProcessingEvent(
                "Error", "Billing", "billing:installment:paid",
                $"Banco recusou — {ex.SqlState} {ex.ConstraintName}", "ERROR",
                "Installment", id, ctx.TenantId, ctx.CorrelationId));

            return Results.BadRequest(new { message = "Não foi possível registrar o pagamento." });
        }
    }

    private static async Task<IResult> SummaryAsync(RequestContext ctx, IDbConnectionFactory factory)
    {
        await using var connection = await ctx.OpenScopedAsync(factory);

        return Results.Ok(await connection.QuerySingleAsync("""
            SELECT count(*) FILTER (WHERE status = 'PENDING')                       AS "pending",
                   count(*) FILTER (WHERE status = 'PAID')                          AS "paid",
                   count(*) FILTER (WHERE status = 'PENDING'
                                      AND due_date < CURRENT_DATE)                  AS "overdue",
                   coalesce(sum((amount).amount) FILTER (WHERE status = 'PENDING'), 0) AS "pendingAmount",
                   coalesce(sum((amount).amount) FILTER (WHERE status = 'PAID'), 0)    AS "paidAmount",
                   coalesce(sum((amount).amount) FILTER (WHERE status = 'PENDING'
                                      AND due_date < CURRENT_DATE), 0)              AS "overdueAmount"
              FROM installments
            """));
    }
}
