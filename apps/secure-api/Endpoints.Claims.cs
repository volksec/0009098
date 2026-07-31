using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Dapper;
using Npgsql;

namespace PortalDoCorretor.SecureApi;

/// <summary>Aviso de sinistro.</summary>
public sealed class ClaimInput
{
    [Required(ErrorMessage = "Apólice é obrigatória.")]
    public Guid PolicyId { get; init; }

    [Required(ErrorMessage = "Data do evento é obrigatória.")]
    public DateOnly? OccurrenceDate { get; init; }

    [Required(ErrorMessage = "Descrição é obrigatória.")]
    [StringLength(1000, MinimumLength = 10, ErrorMessage = "Descrição deve ter ao menos 10 caracteres.")]
    public string Description { get; init; } = string.Empty;

    [Range(0, 999_999_999, ErrorMessage = "Valor estimado inválido.")]
    public decimal? EstimatedAmount { get; init; }
}

/// <summary>Evento acrescentado à linha do tempo do sinistro.</summary>
public sealed class ClaimEventInput
{
    [Required(ErrorMessage = "Tipo do evento é obrigatório.")]
    [StringLength(40)]
    public string Kind { get; init; } = string.Empty;

    [Required(ErrorMessage = "Descrição é obrigatória.")]
    [StringLength(300, MinimumLength = 5)]
    public string Description { get; init; } = string.Empty;
}

/// <summary>Decisão simulada de sinistro.</summary>
public sealed class ClaimDecisionInput
{
    [Required(ErrorMessage = "Decisão é obrigatória.")]
    [RegularExpression("^(APPROVED|DENIED)$", ErrorMessage = "Decisão deve ser APPROVED ou DENIED.")]
    public string Outcome { get; init; } = string.Empty;

    [Required(ErrorMessage = "Motivo é obrigatório.")]
    [StringLength(300, MinimumLength = 5)]
    public string Reason { get; init; } = string.Empty;

    [Range(0, 999_999_999)]
    public decimal? SettledAmount { get; init; }
}

/// <summary>
/// Sinistros: aviso, acompanhamento, linha do tempo e decisão simulada.
/// </summary>
public static class ClaimEndpoints
{
    public static void MapClaimEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/claims").WithTags("Sinistros");

        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", DetailAsync);
        group.MapPost("", ReportAsync);
        group.MapPost("/{id:guid}/events", AddEventAsync);
        group.MapPost("/{id:guid}/decide", DecideAsync);
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
              FROM claims cl
              JOIN policies p  ON p.id = cl.policy_id
              JOIN customers c ON c.id = p.customer_id
             WHERE cl.deleted_at IS NULL
               AND (@status IS NULL OR cl.status::text = @status)
            """;

        var parameters = new
        {
            status = string.IsNullOrWhiteSpace(status) ? null : status,
            offset = (page - 1) * pageSize,
            limit = pageSize
        };

        var total = await connection.ExecuteScalarAsync<int>($"SELECT count(*) {filter}", parameters);

        var items = await connection.QueryAsync($"""
            SELECT cl.id, cl.number, cl.status::text AS status,
                   cl.occurrence_date AS "occurrenceDate", cl.reported_at AS "reportedAt",
                   cl.description,
                   (cl.estimated_amount).amount AS "estimatedAmount",
                   (cl.settled_amount).amount   AS "settledAmount",
                   cl.decided_at AS "decidedAt", cl.decision_reason AS "decisionReason",
                   p.number AS "policyNumber", p.id AS "policyId",
                   CASE c.kind WHEN 'INDIVIDUAL'
                        THEN c.first_name || ' ' || c.last_name
                        ELSE coalesce(c.trade_name, c.legal_name) END AS "customerName",
                   (SELECT count(*) FROM claim_events e WHERE e.claim_id = cl.id) AS "eventCount"
            {filter}
             ORDER BY cl.reported_at DESC
             OFFSET @offset LIMIT @limit
            """, parameters);

        var list = items.ToList();
        var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        stream.Publish(new ProcessingEvent(
            "DatabaseQuery", "Claims", "claims:list",
            $"{list.Count} de {total} sinistro(s)", "SUCCESS",
            "Claim", null, ctx.TenantId, ctx.CorrelationId, elapsed));

        return Results.Ok(new Page<object>(list.Cast<object>().ToList(), total, page, pageSize));
    }

    private static async Task<IResult> DetailAsync(
        Guid id, RequestContext ctx, IDbConnectionFactory factory)
    {
        await using var connection = await ctx.OpenScopedAsync(factory);

        var claim = await connection.QuerySingleOrDefaultAsync("""
            SELECT cl.id, cl.number, cl.status::text AS status,
                   cl.occurrence_date AS "occurrenceDate", cl.reported_at AS "reportedAt",
                   cl.description,
                   (cl.estimated_amount).amount AS "estimatedAmount",
                   (cl.settled_amount).amount   AS "settledAmount",
                   cl.decided_at AS "decidedAt", cl.decision_reason AS "decisionReason",
                   p.number AS "policyNumber",
                   lower(p.coverage_period) AS "coverageStart",
                   upper(p.coverage_period) AS "coverageEnd"
              FROM claims cl
              JOIN policies p ON p.id = cl.policy_id
             WHERE cl.id = @id AND cl.deleted_at IS NULL
            """, new { id });

        if (claim is null) return Results.NotFound(new { message = "Sinistro não encontrado." });

        // Linha do tempo append-only: a ordem é a sequência, não a data de escrita
        var timeline = await connection.QueryAsync("""
            SELECT sequence, kind, description, occurred_at AS "occurredAt"
              FROM claim_events WHERE claim_id = @id ORDER BY sequence
            """, new { id });

        return Results.Ok(new { claim, timeline });
    }

    /// <summary>
    /// Registra o aviso. A invariante "data do evento dentro da vigência" é garantida por
    /// trigger no banco, então a validação aqui existe para dar uma mensagem melhor — não
    /// para substituir a garantia.
    /// </summary>
    private static async Task<IResult> ReportAsync(
        ClaimInput input, RequestContext ctx, IDbConnectionFactory factory, ActivityStream stream)
    {
        if (Validate(input) is { } problem) return problem;

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ctx.OpenScopedAsync(factory);
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var policy = await connection.QuerySingleOrDefaultAsync("""
                SELECT id, broker_id AS "brokerId", tenant_id AS "tenantId",
                       lower(coverage_period) AS "start", upper(coverage_period) AS "end"
                  FROM policies WHERE id = @policyId
                """, new { input.PolicyId }, transaction);

            if (policy is null)
            {
                await transaction.RollbackAsync();
                return Results.NotFound(new { message = "Apólice não encontrada." });
            }

            var occurrence = input.OccurrenceDate!.Value.ToDateTime(TimeOnly.MinValue);

            // Um sinistro não pode ter ocorrido amanhã. A vigência sozinha não pega isso:
            // uma apólice anual aceita datas muitos meses à frente e ainda assim dentro dela.
            if (input.OccurrenceDate!.Value > DateOnly.FromDateTime(DateTime.UtcNow))
            {
                await transaction.RollbackAsync();

                stream.Publish(new ProcessingEvent(
                    "AuthorizationDecision", "Claims", "claims:report",
                    "Data do evento no futuro", "DENIED",
                    "Claim", null, ctx.TenantId, ctx.CorrelationId));

                return Results.UnprocessableEntity(new ValidationProblem("Dados inválidos.",
                    new Dictionary<string, string[]>
                    {
                        ["OccurrenceDate"] = ["A data do evento não pode ser futura."]
                    }));
            }

            if (occurrence < (DateTime)policy.start || occurrence >= (DateTime)policy.end)
            {
                await transaction.RollbackAsync();

                stream.Publish(new ProcessingEvent(
                    "AuthorizationDecision", "Claims", "claims:report",
                    "Data do evento fora da vigência da apólice", "DENIED",
                    "Claim", null, ctx.TenantId, ctx.CorrelationId));

                return Results.UnprocessableEntity(new ValidationProblem("Dados inválidos.",
                    new Dictionary<string, string[]>
                    {
                        ["OccurrenceDate"] =
                        [
                            $"A data deve estar entre {((DateTime)policy.start):dd/MM/yyyy} e "
                          + $"{((DateTime)policy.end):dd/MM/yyyy}, a vigência da apólice."
                        ]
                    }));
            }

            var claimId = Guid.CreateVersion7();
            var sequence = await connection.ExecuteScalarAsync<long>(
                "SELECT nextval('app.claim_number_seq')", transaction: transaction);
            var number = $"SN-{DateTime.UtcNow.Year}-{sequence:D8}";

            await connection.ExecuteAsync("""
                INSERT INTO claims (id, tenant_id, policy_id, broker_id, number, status,
                       occurrence_date, description, estimated_amount, correlation_id)
                VALUES (@claimId, @tenantId, @policyId, @brokerId, @number, 'REPORTED',
                        @occurrenceDate, @description,
                        CASE WHEN @estimated IS NULL THEN NULL
                             ELSE ROW(@estimated,'BRL')::money_amount END,
                        @correlationId)
                """, new
            {
                claimId,
                tenantId = (Guid)policy.tenantId,
                policyId = input.PolicyId,
                brokerId = (Guid)policy.brokerId,
                number,
                occurrenceDate = occurrence,
                description = input.Description.Trim(),
                estimated = input.EstimatedAmount,
                correlationId = ctx.CorrelationId
            }, transaction);

            // O aviso é o primeiro evento da linha do tempo
            await connection.ExecuteAsync("""
                INSERT INTO claim_events (tenant_id, claim_id, sequence, kind, description, recorded_by)
                VALUES (@tenantId, @claimId, 1, 'REPORTED', 'Aviso de sinistro registrado', @actor)
                """, new { tenantId = (Guid)policy.tenantId, claimId, actor = ctx.ActorId }, transaction);

            await transaction.CommitAsync();

            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            stream.Publish(new ProcessingEvent(
                "DomainEvent", "Claims", "claims:report",
                $"Sinistro {number} registrado", "SUCCESS",
                "Claim", claimId, ctx.TenantId, ctx.CorrelationId, elapsed,
                "INSERT INTO claims … ; INSERT INTO claim_events (sequence 1) — mesma transação"));

            return Results.Created($"/api/claims/{claimId}", new { id = claimId, number });
        }
        catch (PostgresException ex)
        {
            await transaction.RollbackAsync();

            stream.Publish(new ProcessingEvent(
                "Error", "Claims", "claims:report",
                $"Banco recusou — {ex.SqlState} {ex.ConstraintName ?? ex.MessageText}", "ERROR",
                "Claim", null, ctx.TenantId, ctx.CorrelationId));

            // A trigger tg_claims_within_coverage é a garantia final da invariante
            var message = ex.MessageText.Contains("vigência", StringComparison.OrdinalIgnoreCase)
                ? "Data do evento fora da vigência da apólice."
                : "Não foi possível registrar o sinistro.";

            return Results.UnprocessableEntity(new { message, code = "CLAIM_INVALID" });
        }
    }

    private static async Task<IResult> AddEventAsync(
        Guid id, ClaimEventInput input, RequestContext ctx,
        IDbConnectionFactory factory, ActivityStream stream)
    {
        if (Validate(input) is { } problem) return problem;

        await using var connection = await ctx.OpenScopedAsync(factory);
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var tenantId = await connection.ExecuteScalarAsync<Guid?>(
                "SELECT tenant_id FROM claims WHERE id = @id AND deleted_at IS NULL",
                new { id }, transaction);

            if (tenantId is null)
            {
                await transaction.RollbackAsync();
                return Results.NotFound(new { message = "Sinistro não encontrado." });
            }

            // A sequência vem do próprio agregado, não de um contador global
            var next = await connection.ExecuteScalarAsync<int>(
                "SELECT coalesce(max(sequence), 0) + 1 FROM claim_events WHERE claim_id = @id",
                new { id }, transaction);

            await connection.ExecuteAsync("""
                INSERT INTO claim_events (tenant_id, claim_id, sequence, kind, description, recorded_by)
                VALUES (@tenantId, @id, @next, @kind, @description, @actor)
                """, new
            {
                tenantId,
                id,
                next,
                kind = input.Kind.Trim().ToUpperInvariant(),
                description = input.Description.Trim(),
                actor = ctx.ActorId
            }, transaction);

            // Primeiro evento após o aviso move o sinistro para análise
            await connection.ExecuteAsync("""
                UPDATE claims SET status = 'UNDER_ANALYSIS'
                 WHERE id = @id AND status = 'REPORTED'
                """, new { id }, transaction);

            await transaction.CommitAsync();

            stream.Publish(new ProcessingEvent(
                "DomainEvent", "Claims", "claims:event:add",
                $"Evento {next} adicionado à linha do tempo", "SUCCESS",
                "Claim", id, ctx.TenantId, ctx.CorrelationId));

            return Results.Ok(new { id, sequence = next });
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync();
            return Results.BadRequest(new { message = "Não foi possível registrar o evento." });
        }
    }

    /// <summary>Decisão e valores são <b>simulados</b> e rotulados como tal na interface.</summary>
    private static async Task<IResult> DecideAsync(
        Guid id, ClaimDecisionInput input, RequestContext ctx,
        IDbConnectionFactory factory, ActivityStream stream)
    {
        if (Validate(input) is { } problem) return problem;

        if (input.Outcome == "APPROVED" && input.SettledAmount is null or <= 0)
            return Results.UnprocessableEntity(new ValidationProblem("Dados inválidos.",
                new Dictionary<string, string[]>
                {
                    ["SettledAmount"] = ["Sinistro aprovado exige valor de indenização."]
                }));

        await using var connection = await ctx.OpenScopedAsync(factory);
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // ck_claims_settled_requires_decision: valor liquidado exige decided_at
            var affected = await connection.ExecuteAsync("""
                UPDATE claims
                   SET status = @outcome::claim_status,
                       decided_at = now(),
                       decision_reason = @reason,
                       settled_amount = CASE WHEN @settled IS NULL THEN NULL
                                             ELSE ROW(@settled,'BRL')::money_amount END
                 WHERE id = @id AND status IN ('REPORTED','UNDER_ANALYSIS','PENDING')
                """, new
            {
                id,
                outcome = input.Outcome,
                reason = input.Reason.Trim(),
                settled = input.Outcome == "APPROVED" ? input.SettledAmount : null
            }, transaction);

            if (affected == 0)
            {
                await transaction.RollbackAsync();
                return Results.Conflict(new
                {
                    message = "Sinistro não encontrado ou já decidido.",
                    code = "CLAIM_NOT_DECIDABLE"
                });
            }

            var tenantId = await connection.ExecuteScalarAsync<Guid>(
                "SELECT tenant_id FROM claims WHERE id = @id", new { id }, transaction);

            var next = await connection.ExecuteScalarAsync<int>(
                "SELECT coalesce(max(sequence), 0) + 1 FROM claim_events WHERE claim_id = @id",
                new { id }, transaction);

            await connection.ExecuteAsync("""
                INSERT INTO claim_events (tenant_id, claim_id, sequence, kind, description, recorded_by)
                VALUES (@tenantId, @id, @next, @kind, @description, @actor)
                """, new
            {
                tenantId,
                id,
                next,
                kind = input.Outcome == "APPROVED" ? "APPROVED" : "DENIED",
                description = $"Decisão simulada: {input.Reason.Trim()}",
                actor = ctx.ActorId
            }, transaction);

            await transaction.CommitAsync();

            stream.Publish(new ProcessingEvent(
                "AuditEvent", "Claims", "claims:decide",
                $"Decisão simulada registrada — {input.Outcome}", "SUCCESS",
                "Claim", id, ctx.TenantId, ctx.CorrelationId, null,
                "UPDATE claims SET status, decided_at, settled_amount; INSERT claim_events"));

            return Results.Ok(new { id, status = input.Outcome, simulated = true });
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync();
            return Results.BadRequest(new { message = "Não foi possível registrar a decisão." });
        }
    }

    private static IResult? Validate(object input)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(input, new ValidationContext(input), results, true))
            return null;

        var errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty)
                              .Select(name => (name, r.ErrorMessage ?? "Valor inválido.")))
            .GroupBy(x => x.name)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Item2).ToArray());

        return Results.UnprocessableEntity(new ValidationProblem("Dados inválidos.", errors));
    }
}
