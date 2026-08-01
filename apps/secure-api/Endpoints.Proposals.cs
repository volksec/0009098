using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Npgsql;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.SecureApi;

/// <summary>Decisão simulada de underwriting.</summary>
public sealed class UnderwritingInput
{
    [Required(ErrorMessage = "Decisão é obrigatória.")]
    [RegularExpression("^(APPROVED|REJECTED|PENDING)$", ErrorMessage = "Decisão inválida.")]
    public string Outcome { get; init; } = string.Empty;

    [Required(ErrorMessage = "Motivo é obrigatório.")]
    [StringLength(300, MinimumLength = 5, ErrorMessage = "Motivo deve ter ao menos 5 caracteres.")]
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Emissão de apólice.</summary>
public sealed class IssuanceInput
{
    /// <summary>Início da vigência. Se ausente, começa hoje.</summary>
    public DateOnly? StartDate { get; init; }
}

/// <summary>
/// Propostas: análise simulada de risco e emissão de apólice.
/// </summary>
public static class ProposalEndpoints
{
    public static void MapProposalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/proposals").WithTags("Propostas");

        group.MapGet("", ListAsync).WithSummary("Propostas com filtro por situação");
        group.MapGet("/{id:guid}", DetailAsync).WithSummary("Proposta com decisões versionadas e histórico");
        group.MapPost("/{id:guid}/underwrite", UnderwriteAsync).WithSummary("Registra decisão de risco (versionada e imutável)");
        group.MapPost("/{id:guid}/issue", IssueAsync).WithSummary("Emite a apólice — aceita Idempotency-Key");
    }

    // ---------------------------------------------------------------- consulta

    private static async Task<IResult> ListAsync(
        RequestContext ctx, IDbConnectionFactory factory, ActivityStream stream,
        string? status, int page = 1, int pageSize = 15)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ctx.OpenScopedAsync(factory);

        const string filter = """
              FROM proposals p
              JOIN customers c  ON c.id = p.customer_id
              JOIN quotations q ON q.id = p.quotation_id
             WHERE p.deleted_at IS NULL
               AND (@status IS NULL OR p.status::text = @status)
            """;

        var parameters = new
        {
            status = string.IsNullOrWhiteSpace(status) ? null : status,
            offset = (page - 1) * pageSize,
            limit = pageSize
        };

        var total = await connection.ExecuteScalarAsync<int>($"SELECT count(*) {filter}", parameters);

        var items = await connection.QueryAsync($"""
            SELECT p.id, p.number, p.status::text AS status,
                   p.chosen_plan::text AS "chosenPlan",
                   (p.total_premium).amount AS "totalPremium",
                   p.installment_count AS "installmentCount",
                   p.created_at AS "createdAt", p.submitted_at AS "submittedAt",
                   p.decided_at AS "decidedAt", p.issued_at AS "issuedAt",
                   q.number AS "quotationNumber",
                   CASE c.kind WHEN 'INDIVIDUAL'
                        THEN c.first_name || ' ' || c.last_name
                        ELSE coalesce(c.trade_name, c.legal_name) END AS "customerName",
                   (SELECT count(*) FROM pendencies pe
                     WHERE pe.proposal_id = p.id AND pe.resolved_at IS NULL) AS "openPendencies",
                   (SELECT po.number FROM policies po
                     WHERE po.proposal_id = p.id AND po.status <> 'CANCELLED') AS "policyNumber",
                   (SELECT ud.outcome FROM underwriting_decisions ud
                     WHERE ud.proposal_id = p.id
                     ORDER BY ud.version DESC LIMIT 1) AS "lastDecision"
            {filter}
             ORDER BY p.created_at DESC
             OFFSET @offset LIMIT @limit
            """, parameters);

        var list = items.ToList();
        var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        stream.Publish(new ProcessingEvent(
            "DatabaseQuery", "Proposals", "proposals:list",
            $"{list.Count} de {total} proposta(s)", "SUCCESS",
            "Proposal", null, ctx.TenantId, ctx.CorrelationId, elapsed));

        return Results.Ok(new Page<object>(list.Cast<object>().ToList(), total, page, pageSize));
    }

    private static async Task<IResult> DetailAsync(
        Guid id, RequestContext ctx, IDbConnectionFactory factory)
    {
        await using var connection = await ctx.OpenScopedAsync(factory);

        var proposal = await connection.QuerySingleOrDefaultAsync("""
            SELECT p.id, p.number, p.status::text AS status,
                   p.chosen_plan::text AS "chosenPlan",
                   (p.net_premium).amount   AS "netPremium",
                   (p.total_premium).amount AS "totalPremium",
                   p.installment_count AS "installmentCount",
                   p.created_at AS "createdAt", p.submitted_at AS "submittedAt",
                   p.decided_at AS "decidedAt", p.issued_at AS "issuedAt",
                   q.id AS "quotationId", q.number AS "quotationNumber",
                   q.risk_score AS "riskScore", q.risk_band::text AS "riskBand",
                   pr.name AS "productName",
                   (SELECT count(*) FROM pendencies pe
                     WHERE pe.proposal_id = p.id AND pe.resolved_at IS NULL) AS "openPendencies",
                   CASE c.kind WHEN 'INDIVIDUAL'
                        THEN c.first_name || ' ' || c.last_name
                        ELSE coalesce(c.trade_name, c.legal_name) END AS "customerName",
                   (SELECT po.id FROM policies po
                     WHERE po.proposal_id = p.id AND po.status <> 'CANCELLED') AS "policyId",
                   (SELECT po.number FROM policies po
                     WHERE po.proposal_id = p.id AND po.status <> 'CANCELLED') AS "policyNumber"
              FROM proposals p
              JOIN customers c           ON c.id  = p.customer_id
              JOIN quotations q          ON q.id  = p.quotation_id
              JOIN product_versions pv   ON pv.id = q.product_version_id
              JOIN insurance_products pr ON pr.id = pv.product_id
             WHERE p.id = @id AND p.deleted_at IS NULL
            """, new { id });

        if (proposal is null) return Results.NotFound(new { message = "Proposta não encontrada." });

        // Decisões são versionadas: reanálise cria nova versão, nunca sobrescreve
        var decisions = await connection.QueryAsync("""
            SELECT version, outcome, reasons, decided_at AS "decidedAt"
              FROM underwriting_decisions WHERE proposal_id = @id ORDER BY version DESC
            """, new { id });

        var pendencies = await connection.QueryAsync("""
            SELECT id, code, description, opened_at AS "openedAt", resolved_at AS "resolvedAt"
              FROM pendencies WHERE proposal_id = @id ORDER BY opened_at
            """, new { id });

        var history = await connection.QueryAsync("""
            SELECT from_status AS "fromStatus", to_status AS "toStatus", reason,
                   changed_at AS "changedAt"
              FROM proposal_status_history WHERE proposal_id = @id ORDER BY changed_at
            """, new { id });

        return Results.Ok(new { proposal, decisions, pendencies, history });
    }

    // ---------------------------------------------------------------- underwriting

    /// <summary>
    /// Registra a decisão simulada de underwriting.
    /// </summary>
    /// <remarks>
    /// A decisão é <b>imutável e versionada</b>: reanálise cria uma nova versão em vez de
    /// sobrescrever a anterior. Sem isso, seria impossível auditar por que uma proposta foi
    /// recusada em uma data e aprovada depois. A tabela tem trigger que bloqueia UPDATE.
    /// </remarks>
    private static async Task<IResult> UnderwriteAsync(
        Guid id, UnderwritingInput input, RequestContext ctx,
        IDbConnectionFactory factory, ActivityStream stream)
    {
        if (Validate(input) is { } problem) return problem;

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ctx.OpenScopedAsync(factory);
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var proposal = await connection.QuerySingleOrDefaultAsync("""
                SELECT p.status::text AS status, q.risk_score AS "riskScore",
                       (SELECT count(*) FROM pendencies pe
                         WHERE pe.proposal_id = p.id AND pe.resolved_at IS NULL) AS "openPendencies"
                  FROM proposals p
                  JOIN quotations q ON q.id = p.quotation_id
                 WHERE p.id = @id AND p.deleted_at IS NULL
                """, new { id }, transaction);

            if (proposal is null)
            {
                await transaction.RollbackAsync();
                return Results.NotFound(new { message = "Proposta não encontrada." });
            }

            var status = (string)proposal.status;
            if (status is not ("SUBMITTED" or "UNDER_ANALYSIS" or "PENDING"))
            {
                await transaction.RollbackAsync();
                return Results.Conflict(new
                {
                    message = $"Proposta em status {status} não aceita nova decisão.",
                    code = "PROPOSAL_NOT_ANALYZABLE"
                });
            }

            // Invariante: proposta com pendência aberta não é aprovada
            if (input.Outcome == "APPROVED" && (long)proposal.openPendencies > 0)
            {
                await transaction.RollbackAsync();

                stream.Publish(new ProcessingEvent(
                    "AuthorizationDecision", "Proposals", "proposals:underwrite",
                    $"Aprovação recusada — {proposal.openPendencies} pendência(s) aberta(s)",
                    "DENIED", "Proposal", id, ctx.TenantId, ctx.CorrelationId));

                return Results.Conflict(new
                {
                    message = $"Proposta possui {proposal.openPendencies} pendência(s) em aberto.",
                    code = "PROPOSAL_HAS_PENDENCIES"
                });
            }

            var version = await connection.ExecuteScalarAsync<int>("""
                SELECT coalesce(max(version), 0) + 1
                  FROM underwriting_decisions WHERE proposal_id = @id
                """, new { id }, transaction);

            var rules = new
            {
                riskScore = (short)proposal.riskScore,
                openPendencies = (long)proposal.openPendencies,
                evaluatedBy = "UnderwritingEngine (simulado)",
                engineVersion = "1.0.0"
            };

            await connection.ExecuteAsync("""
                INSERT INTO underwriting_decisions (tenant_id, proposal_id, version, outcome,
                       reasons, evaluated_rules, decided_by, correlation_id)
                VALUES (@tenantId, @id, @version, @outcome, @reasons, @rules::jsonb,
                        @actor, @correlationId)
                """, new
            {
                tenantId = ctx.TenantId,
                id,
                version,
                input.Outcome,
                reasons = new[] { input.Reason.Trim() },
                rules = JsonSerializer.Serialize(rules),
                actor = ctx.ActorId,
                correlationId = ctx.CorrelationId
            }, transaction);

            var newStatus = input.Outcome switch
            {
                "APPROVED" => "APPROVED",
                "REJECTED" => "REJECTED",
                _ => "PENDING"
            };

            await connection.ExecuteAsync("""
                UPDATE proposals SET status = @newStatus::proposal_status, decided_at = now(),
                       updated_at = now(), updated_by = @actor
                 WHERE id = @id
                """, new { id, newStatus, actor = ctx.ActorId }, transaction);

            await connection.ExecuteAsync("""
                INSERT INTO proposal_status_history (tenant_id, proposal_id, from_status,
                       to_status, reason, changed_by, correlation_id)
                VALUES (@tenantId, @id, @from::proposal_status, @to::proposal_status,
                        @reason, @actor, @correlationId)
                """, new
            {
                tenantId = ctx.TenantId,
                id,
                from = status,
                to = newStatus,
                reason = input.Reason.Trim(),
                actor = ctx.ActorId,
                correlationId = ctx.CorrelationId
            }, transaction);

            // Pendência gerada automaticamente quando a decisão é PENDING
            if (input.Outcome == "PENDING")
                await connection.ExecuteAsync("""
                    INSERT INTO pendencies (tenant_id, proposal_id, code, description)
                    VALUES (@tenantId, @id, 'UNDERWRITING_INFO', @description)
                    """, new
                {
                    tenantId = ctx.TenantId,
                    id,
                    description = input.Reason.Trim()
                }, transaction);

            await transaction.CommitAsync();

            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            stream.Publish(new ProcessingEvent(
                "DomainEvent", "Proposals", "proposals:underwrite",
                $"Decisão v{version}: {input.Outcome} (simulada)", "SUCCESS",
                "Proposal", id, ctx.TenantId, ctx.CorrelationId, elapsed,
                "INSERT underwriting_decisions (imutável, versionada) + UPDATE proposals"));

            return Results.Ok(new { id, version, status = newStatus, simulated = true });
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync();
            return Results.BadRequest(new { message = "Não foi possível registrar a decisão." });
        }
    }

    // ---------------------------------------------------------------- emissão

    /// <summary>
    /// Emite a apólice — o caso de uso central do sistema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Apólice, coberturas congeladas, plano de parcelas, comissão, evento de Outbox e transição
    /// da proposta são confirmados na <b>mesma transação</b>. Consistência eventual aqui seria
    /// observável pelo usuário e financeiramente incorreta: uma apólice sem parcelas ou sem
    /// comissão, ainda que por segundos, é um estado que o negócio não aceita.
    /// </para>
    /// <para>
    /// Três camadas independentes impedem emissão duplicada:
    /// <c>Idempotency-Key</c> (replay devolve a resposta original), o índice único parcial
    /// <c>ux_policies_proposal</c>, e a constraint de exclusão <c>ex_policies_no_overlap</c>,
    /// que ainda bloqueia vigências sobrepostas para o mesmo bem.
    /// </para>
    /// </remarks>
    private static async Task<IResult> IssueAsync(
        Guid id, IssuanceInput input, HttpContext http, RequestContext ctx,
        IDbConnectionFactory factory, ActivityStream stream)
    {
        var started = Stopwatch.GetTimestamp();
        var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();

        await using var connection = await ctx.OpenScopedAsync(factory);

        // --- camada 1: replay devolve a resposta original ---------------------
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var cached = await connection.QuerySingleOrDefaultAsync<string?>("""
                SELECT response_body::text FROM idempotency_keys
                 WHERE tenant_id = @tenantId AND key = @key AND endpoint = @endpoint
                   AND completed_at IS NOT NULL
                """, new
            {
                tenantId = ctx.TenantId,
                key = idempotencyKey,
                endpoint = "POST /api/proposals/issue"
            });

            if (cached is not null)
            {
                stream.Publish(new ProcessingEvent(
                    "ApplicationLog", "Policies", "policies:issue",
                    "Replay detectado pela Idempotency-Key — resposta original devolvida",
                    "SUCCESS", "Policy", id, ctx.TenantId, ctx.CorrelationId));

                return Results.Content(cached, "application/json");
            }
        }

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        try
        {
            // --- carrega a proposta com o token de concorrência (xmin) --------
            var proposal = await connection.QuerySingleOrDefaultAsync("""
                SELECT p.id, p.status::text AS status, p.xmin::text AS "version",
                       p.quotation_id AS "quotationId", p.broker_id AS "brokerId",
                       p.customer_id AS "customerId", p.chosen_plan::text AS "chosenPlan",
                       (p.net_premium).amount   AS "netPremium",
                       (p.total_premium).amount AS "totalPremium",
                       p.installment_count AS "installmentCount",
                       q.asset_id AS "assetId", q.product_version_id AS "productVersionId",
                       (SELECT count(*) FROM pendencies pe
                         WHERE pe.proposal_id = p.id AND pe.resolved_at IS NULL) AS "openPendencies",
                       (SELECT ud.outcome FROM underwriting_decisions ud
                         WHERE ud.proposal_id = p.id ORDER BY ud.version DESC LIMIT 1) AS "decision"
                  FROM proposals p
                  JOIN quotations q ON q.id = p.quotation_id
                 WHERE p.id = @id AND p.deleted_at IS NULL
                """, new { id }, transaction);

            if (proposal is null)
            {
                await transaction.RollbackAsync();
                return Results.NotFound(new { message = "Proposta não encontrada." });
            }

            // --- invariantes de emissão ---------------------------------------
            if ((string)proposal.status != "APPROVED")
            {
                await transaction.RollbackAsync();
                return Results.Conflict(new
                {
                    message = $"Proposta em status {proposal.status} não pode ser emitida. "
                            + "É necessário aprovar na análise de risco.",
                    code = "PROPOSAL_NOT_APPROVED"
                });
            }

            if ((long)proposal.openPendencies > 0)
            {
                await transaction.RollbackAsync();
                return Results.Conflict(new
                {
                    message = $"Proposta possui {proposal.openPendencies} pendência(s) em aberto.",
                    code = "PROPOSAL_HAS_PENDENCIES"
                });
            }

            if ((string?)proposal.decision != "APPROVED")
            {
                await transaction.RollbackAsync();
                return Results.Conflict(new
                {
                    message = "Decisão de underwriting não permite emissão.",
                    code = "UNFAVORABLE_DECISION"
                });
            }

            // --- autorização de recurso (camada 4 do isolamento) ---------------
            // A emissão grava a comissão do corretor, e a política RESTRICTIVE
            // p_commissions_own_broker só aceita a linha se o ator for aquele corretor.
            // Verificar aqui transforma um erro de RLS opaco em uma negativa explícita —
            // a política continua sendo a garantia final, esta checagem é a mensagem.
            var actorBrokerId = await connection.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM brokers WHERE user_id = @actor",
                new { actor = ctx.ActorId }, transaction);

            if (actorBrokerId != (Guid)proposal.brokerId)
            {
                await transaction.RollbackAsync();

                stream.Publish(new ProcessingEvent(
                    "Security", "Policies", "policies:issue",
                    "Emissão negada — ator não é o corretor responsável pela proposta",
                    "DENIED", "Proposal", id, ctx.TenantId, ctx.CorrelationId));

                return Results.Json(new
                {
                    message = "Somente o corretor responsável pela proposta pode emiti-la.",
                    code = "NOT_PROPOSAL_OWNER"
                }, statusCode: 403);
            }

            // --- camada 2: optimistic lock via xmin ---------------------------
            // O UPDATE só afeta a linha se a versão lida continuar valendo. Se outra
            // transação emitiu no intervalo, zero linhas são afetadas e abortamos.
            var claimed = await connection.ExecuteAsync("""
                UPDATE proposals SET status = 'ISSUED', issued_at = now(),
                       updated_at = now(), updated_by = @actor
                 WHERE id = @id AND xmin = @version::xid AND status = 'APPROVED'
                """, new { id, version = (string)proposal.version, actor = ctx.ActorId }, transaction);

            if (claimed == 0)
            {
                await transaction.RollbackAsync();

                stream.Publish(new ProcessingEvent(
                    "Transaction", "Policies", "policies:issue",
                    "Optimistic lock: xmin divergente — outra transação emitiu primeiro",
                    "DENIED", "Proposal", id, ctx.TenantId, ctx.CorrelationId));

                return Results.Conflict(new
                {
                    message = "Outra operação alterou esta proposta. Recarregue e tente novamente.",
                    code = "CONCURRENCY_CONFLICT"
                });
            }

            var startDate = input.StartDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var endDate = startDate.AddYears(1);
            var policyId = Guid.CreateVersion7();
            var sequence = await connection.ExecuteScalarAsync<long>(
                "SELECT nextval('app.policy_number_seq')", transaction: transaction);
            var number = PolicyNumber.Generate(startDate.Year, sequence).Value;

            // --- camada 3: unique parcial + constraint de exclusão -------------
            await connection.ExecuteAsync("""
                INSERT INTO policies (id, tenant_id, proposal_id, broker_id, customer_id,
                       asset_id, product_version_id, number, status, coverage_period,
                       net_premium, total_premium, issued_by, correlation_id)
                VALUES (@policyId, @tenantId, @proposalId, @brokerId, @customerId,
                        @assetId, @productVersionId, @number, 'ACTIVE',
                        -- o cast é necessário: DateOnly trafega como timestamp e não existe
                        -- daterange(timestamp, timestamp)
                        daterange(@startDate::date, @endDate::date),
                        ROW(@net,'BRL')::money_amount, ROW(@total,'BRL')::money_amount,
                        @actor, @correlationId)
                """, new
            {
                policyId,
                tenantId = ctx.TenantId,
                proposalId = id,
                brokerId = (Guid)proposal.brokerId,
                customerId = (Guid)proposal.customerId,
                assetId = (Guid)proposal.assetId,
                productVersionId = (Guid)proposal.productVersionId,
                number,
                startDate = startDate.ToDateTime(TimeOnly.MinValue),
                endDate = endDate.ToDateTime(TimeOnly.MinValue),
                net = (decimal)proposal.netPremium,
                total = (decimal)proposal.totalPremium,
                actor = ctx.ActorId,
                correlationId = ctx.CorrelationId
            }, transaction);

            // Coberturas CONGELADAS a partir do snapshot da cotação
            await connection.ExecuteAsync("""
                INSERT INTO policy_coverages (tenant_id, policy_id, coverage_id,
                       limit_amount, deductible, premium, is_mandatory)
                SELECT @tenantId, @policyId, sc.coverage_id, sc.limit_amount,
                       sc.deductible, sc.premium, cv.is_mandatory
                  FROM selected_coverages sc
                  JOIN quotation_items i ON i.id = sc.quotation_item_id
                  JOIN coverages cv      ON cv.id = sc.coverage_id
                 WHERE i.quotation_id = @quotationId AND i.plan = @plan::plan_tier
                """, new
            {
                tenantId = ctx.TenantId,
                policyId,
                quotationId = (Guid)proposal.quotationId,
                plan = (string)proposal.chosenPlan
            }, transaction);

            // --- parcelas: Σ parcelas = prêmio, ao centavo ---------------------
            var installmentCount = (short)proposal.installmentCount;
            var totalPremium = Money.Of((decimal)proposal.totalPremium);
            var allocated = totalPremium.Allocate(installmentCount);
            var planId = Guid.CreateVersion7();

            await connection.ExecuteAsync("""
                INSERT INTO installment_plans (id, tenant_id, policy_id, total_amount, installment_count)
                VALUES (@planId, @tenantId, @policyId, ROW(@total,'BRL')::money_amount, @count)
                """, new
            {
                planId,
                tenantId = ctx.TenantId,
                policyId,
                total = totalPremium.Amount,
                count = installmentCount
            }, transaction);

            for (var i = 0; i < allocated.Count; i++)
                await connection.ExecuteAsync("""
                    INSERT INTO installments (tenant_id, plan_id, sequence, amount, due_date, status)
                    VALUES (@tenantId, @planId, @sequence, ROW(@amount,'BRL')::money_amount,
                            @dueDate, 'PENDING')
                    """, new
                {
                    tenantId = ctx.TenantId,
                    planId,
                    sequence = (short)(i + 1),
                    amount = allocated[i].Amount,
                    dueDate = startDate.AddMonths(i).ToDateTime(TimeOnly.MinValue)
                }, transaction);

            // --- comissão pela regra vigente -----------------------------------
            var rule = await connection.QuerySingleOrDefaultAsync("""
                SELECT r.id, r.version, r.rate, r.base_on::text AS "baseOn"
                  FROM commission_rules r
                  JOIN product_versions pv ON pv.product_id = r.product_id
                 WHERE pv.id = @productVersionId
                   AND r.valid_period @> @startDate::date
                 ORDER BY r.version DESC LIMIT 1
                """, new
            {
                productVersionId = (Guid)proposal.productVersionId,
                startDate = startDate.ToDateTime(TimeOnly.MinValue)
            }, transaction);

            if (rule is not null)
            {
                var baseAmount = (string)rule.baseOn == "NET_PREMIUM"
                    ? (decimal)proposal.netPremium
                    : (decimal)proposal.totalPremium;

                await connection.ExecuteAsync("""
                    INSERT INTO commissions (tenant_id, policy_id, broker_id, rule_id, rule_version,
                           rate_applied, base_amount, amount, status, reference_month)
                    VALUES (@tenantId, @policyId, @brokerId, @ruleId, @ruleVersion, @rate,
                            ROW(@baseAmount,'BRL')::money_amount,
                            ROW(@amount,'BRL')::money_amount, 'FORECAST', @month)
                    """, new
                {
                    tenantId = ctx.TenantId,
                    policyId,
                    brokerId = (Guid)proposal.brokerId,
                    ruleId = (Guid)rule.id,
                    ruleVersion = (int)rule.version,
                    rate = (decimal)rule.rate,
                    baseAmount,
                    amount = Math.Round(baseAmount * (decimal)rule.rate, 2, MidpointRounding.ToEven),
                    month = new DateOnly(startDate.Year, startDate.Month, 1)
                                .ToDateTime(TimeOnly.MinValue)
                }, transaction);
            }

            // --- evento de domínio na MESMA transação --------------------------
            await connection.ExecuteAsync("""
                INSERT INTO outbox_messages (tenant_id, message_type, payload, correlation_id,
                       aggregate_type, aggregate_id)
                VALUES (@tenantId, 'PolicyIssued', @payload::jsonb, @correlationId,
                        'Policy', @policyId)
                """, new
            {
                tenantId = ctx.TenantId,
                payload = JsonSerializer.Serialize(new
                {
                    policyId,
                    number,
                    proposalId = id,
                    totalPremium = totalPremium.Amount,
                    installments = installmentCount
                }),
                correlationId = ctx.CorrelationId,
                policyId
            }, transaction);

            // --- trilha de auditoria, na MESMA transação ------------------------
            // Sem isto a emissão pela aplicação não deixava rastro: só as apólices do
            // seed apareciam auditadas, e POLICY_WITHOUT_AUDIT acusava toda emissão real.
            // Auditoria fora da transação é auditoria que pode faltar justamente quando
            // mais importa — por isso vai junto do INSERT que ela descreve.
            await connection.ExecuteAsync("""
                INSERT INTO audit_events (id, occurred_at, tenant_id, correlation_id, actor_id,
                       actor_profile, action, resource_type, resource_id, outcome, duration_ms,
                       after_state)
                VALUES (gen_random_uuid(), now(), @tenantId, @correlationId, @actor,
                        'BROKER', 'POLICY_ISSUED', 'Policy', @policyId, 'SUCCESS', @elapsed,
                        @afterState::jsonb)
                """, new
            {
                tenantId = ctx.TenantId,
                correlationId = ctx.CorrelationId,
                actor = ctx.ActorId,
                policyId,
                elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                afterState = JsonSerializer.Serialize(new
                {
                    number,
                    status = "ACTIVE",
                    netPremium = (decimal)proposal.netPremium,
                    totalPremium = totalPremium.Amount,
                    installments = installmentCount,
                    periodStart = startDate.ToString("yyyy-MM-dd"),
                    periodEnd = endDate.ToString("yyyy-MM-dd")
                })
            }, transaction);

            await connection.ExecuteAsync("""
                INSERT INTO proposal_status_history (tenant_id, proposal_id, from_status,
                       to_status, reason, changed_by, correlation_id)
                VALUES (@tenantId, @id, 'APPROVED', 'ISSUED', @reason, @actor, @correlationId)
                """, new
            {
                tenantId = ctx.TenantId,
                id,
                reason = $"Apólice {number} emitida",
                actor = ctx.ActorId,
                correlationId = ctx.CorrelationId
            }, transaction);

            var response = JsonSerializer.Serialize(new
            {
                policyId,
                number,
                periodStart = startDate.ToString("yyyy-MM-dd"),
                periodEnd = endDate.ToString("yyyy-MM-dd"),
                totalPremium = totalPremium.Amount,
                installments = installmentCount
            });

            if (!string.IsNullOrWhiteSpace(idempotencyKey))
                await connection.ExecuteAsync("""
                    INSERT INTO idempotency_keys (tenant_id, key, endpoint, request_hash,
                           response_status, response_body, completed_at)
                    VALUES (@tenantId, @key, @endpoint, @hash, 201, @body::jsonb, now())
                    ON CONFLICT (tenant_id, key, endpoint) DO NOTHING
                    """, new
                {
                    tenantId = ctx.TenantId,
                    key = idempotencyKey,
                    endpoint = "POST /api/proposals/issue",
                    hash = System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(id.ToString())),
                    body = response
                }, transaction);

            await transaction.CommitAsync();

            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            stream.Publish(new ProcessingEvent(
                "DomainEvent", "Policies", "policies:issue",
                $"Apólice {number} emitida — {installmentCount}× parcela(s), comissão apurada",
                "SUCCESS", "Policy", policyId, ctx.TenantId, ctx.CorrelationId, elapsed,
                "BEGIN → UPDATE proposals (xmin) → INSERT policies → policy_coverages → "
              + "installment_plans → installments → commissions → outbox_messages → COMMIT"));

            return Results.Content(response, "application/json", statusCode: 201);
        }
        catch (PostgresException ex)
        {
            await transaction.RollbackAsync();

            var (message, code) = ex.ConstraintName switch
            {
                "ux_policies_proposal" =>
                    ("Esta proposta já possui uma apólice emitida.", "POLICY_ALREADY_ISSUED"),

                "ex_policies_no_overlap" =>
                    ("Já existe apólice vigente para este bem e produto no período informado.",
                     "COVERAGE_PERIOD_OVERLAP"),

                _ when ex.MessageText.Contains("Soma das parcelas", StringComparison.OrdinalIgnoreCase) =>
                    ("Falha na geração das parcelas: a soma não confere com o prêmio.",
                     "INSTALLMENTS_SUM_MISMATCH"),

                _ => ("Não foi possível emitir a apólice.", "ISSUANCE_FAILED")
            };

            // O cliente recebe mensagem de negócio; o detalhe do banco fica apenas no log
            // do servidor — vazá-lo na resposta entregaria o esquema a quem sondasse a API.
            if (code == "ISSUANCE_FAILED")
                Console.Error.WriteLine(
                    $"[policies:issue] {ex.SqlState} {ex.ConstraintName ?? "sem constraint"}: "
                  + $"{ex.MessageText} | {ex.Detail}");

            stream.Publish(new ProcessingEvent(
                "Error", "Policies", "policies:issue",
                $"Emissão bloqueada — {ex.SqlState} {ex.ConstraintName ?? "—"}", "ERROR",
                "Proposal", id, ctx.TenantId, ctx.CorrelationId));

            return Results.Conflict(new { message, code });
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
