using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Npgsql;
using PortalDoCorretor.Quotations.Domain;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.SecureApi;

/// <summary>Entrada de cotação: bem, produto, questionário e coberturas.</summary>
public sealed class QuotationInput
{
    [Required(ErrorMessage = "Cliente é obrigatório.")]
    public Guid CustomerId { get; init; }

    [Required(ErrorMessage = "Bem segurável é obrigatório.")]
    public Guid AssetId { get; init; }

    [Required(ErrorMessage = "Produto é obrigatório.")]
    public Guid ProductVersionId { get; init; }

    [Required(ErrorMessage = "Selecione ao menos uma cobertura.")]
    [MinLength(1, ErrorMessage = "Selecione ao menos uma cobertura.")]
    public Guid[] CoverageIds { get; init; } = [];

    // Questionário de risco
    public bool HasGarage { get; init; }

    [RegularExpression("^(PERSONAL|COMMUTE|COMMERCIAL|RIDESHARE)$",
        ErrorMessage = "Uso do bem inválido.")]
    public string Usage { get; init; } = "PERSONAL";

    [Range(18, 99, ErrorMessage = "Idade do condutor deve estar entre 18 e 99.")]
    public int DriverAge { get; init; } = 35;

    public bool PreviousClaims { get; init; }
}

/// <summary>Conversão de cotação em proposta.</summary>
public sealed class ConversionInput
{
    [Required(ErrorMessage = "Plano é obrigatório.")]
    [RegularExpression("^(ESSENTIAL|COMPLETE|MASTER)$", ErrorMessage = "Plano inválido.")]
    public string Plan { get; init; } = string.Empty;

    [Range(1, 12, ErrorMessage = "Parcelamento entre 1 e 12 vezes.")]
    public int InstallmentCount { get; init; } = 1;
}

/// <summary>
/// Cotação: catálogo de produtos, cálculo dos três planos e conversão em proposta.
/// </summary>
public static class QuotationEndpoints
{
    public static void MapQuotationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/quotations").WithTags("Cotações");

        group.MapGet("", ListAsync).WithSummary("Cotações com filtro por situação");
        group.MapGet("/{id:guid}", DetailAsync).WithSummary("Cotação com os três planos e o snapshot de cálculo");
        group.MapPost("", CreateAsync).WithSummary("Calcula os três planos; recusa é persistida com o motivo");
        group.MapPost("/{id:guid}/convert", ConvertAsync).WithSummary("Converte em proposta na mesma transação");

        app.MapGet("/api/products", ProductsAsync).WithTags("Produtos")
           .WithSummary("Catálogo: versões de produto e coberturas disponíveis");
        app.MapGet("/api/customers/{customerId:guid}/assets", AssetsAsync).WithTags("Clientes")
           .WithSummary("Bens seguráveis do cliente, com o valor declarado");
    }

    // ---------------------------------------------------------------- catálogo

    private static async Task<IResult> ProductsAsync(RequestContext ctx, IDbConnectionFactory factory)
    {
        await using var connection = await ctx.OpenScopedAsync(factory);

        var products = (await connection.QueryAsync("""
            SELECT pv.id, pr.name, pv.branch::text AS branch, pv.version,
                   pv.base_rate AS "baseRate", pv.risk_sensitivity AS "riskSensitivity",
                   pv.max_acceptable_risk AS "maxAcceptableRisk",
                   pv.min_insured_value AS "minInsuredValue",
                   pv.max_insured_value AS "maxInsuredValue"
              FROM product_versions pv
              JOIN insurance_products pr ON pr.id = pv.product_id
             WHERE pv.published_at IS NOT NULL AND pr.deleted_at IS NULL
             ORDER BY pr.name
            """)).ToList();

        var coverages = (await connection.QueryAsync("""
            SELECT id, product_version_id AS "productVersionId", code, name, description,
                   is_mandatory AS "isMandatory",
                   (min_limit).amount AS "minLimit", (max_limit).amount AS "maxLimit",
                   (default_deductible).kind    AS "deductibleKind",
                   (default_deductible).amount  AS "deductibleAmount",
                   (default_deductible).percent AS "deductiblePercent"
              FROM coverages ORDER BY is_mandatory DESC, name
            """)).ToList();

        return Results.Ok(new { products, coverages });
    }

    private static async Task<IResult> AssetsAsync(
        Guid customerId, RequestContext ctx, IDbConnectionFactory factory)
    {
        await using var connection = await ctx.OpenScopedAsync(factory);

        return Results.Ok(await connection.QueryAsync("""
            SELECT a.id, a.kind::text AS kind, (a.declared_value).amount AS "declaredValue",
                   CASE a.kind
                     WHEN 'VEHICLE'
                       THEN v.brand || ' ' || v.model || ' ' || v.model_year || ' — ' || v.plate
                     ELSE 'Imóvel ' || p.area_sqm || ' m² — ' || (p.location).city
                   END AS "label",
                   v.overnight_postal_code AS "vehiclePostalCode",
                   (p.location).postal_code AS "propertyPostalCode"
              FROM insurable_assets a
              LEFT JOIN vehicles v   ON v.id = a.id
              LEFT JOIN properties p ON p.id = a.id
             WHERE a.customer_id = @customerId AND a.deleted_at IS NULL
             ORDER BY a.created_at DESC
            """, new { customerId }));
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
              FROM quotations q
              JOIN customers c           ON c.id  = q.customer_id
              JOIN product_versions pv   ON pv.id = q.product_version_id
              JOIN insurance_products pr ON pr.id = pv.product_id
             WHERE q.deleted_at IS NULL
               AND (@status IS NULL OR q.status::text = @status)
            """;

        var parameters = new
        {
            status = string.IsNullOrWhiteSpace(status) ? null : status,
            offset = (page - 1) * pageSize,
            limit = pageSize
        };

        var total = await connection.ExecuteScalarAsync<int>($"SELECT count(*) {filter}", parameters);

        var items = await connection.QueryAsync($"""
            SELECT q.id, q.number, q.status::text AS status,
                   q.risk_score AS "riskScore", q.risk_band::text AS "riskBand",
                   q.created_at AS "createdAt", q.expires_at AS "expiresAt",
                   q.rejection_reasons AS "rejectionReasons",
                   pr.name AS "productName",
                   CASE c.kind WHEN 'INDIVIDUAL'
                        THEN c.first_name || ' ' || c.last_name
                        ELSE coalesce(c.trade_name, c.legal_name) END AS "customerName",
                   (SELECT min((i.total_premium).amount) FROM quotation_items i
                     WHERE i.quotation_id = q.id) AS "fromPremium",
                   -- Expirada é derivado: o worker materializa o status, mas a leitura
                   -- fica correta mesmo entre duas execuções dele
                   (q.expires_at <= now() AND q.status IN ('DRAFT','CALCULATED')) AS "isExpired",
                   EXISTS (SELECT 1 FROM proposals p
                            WHERE p.quotation_id = q.id
                              AND p.status NOT IN ('REJECTED','EXPIRED')) AS "hasProposal"
            {filter}
             ORDER BY q.created_at DESC
             OFFSET @offset LIMIT @limit
            """, parameters);

        var list = items.ToList();
        var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        stream.Publish(new ProcessingEvent(
            "DatabaseQuery", "Quotations", "quotations:list",
            $"{list.Count} de {total} cotação(ões)", "SUCCESS",
            "Quotation", null, ctx.TenantId, ctx.CorrelationId, elapsed));

        return Results.Ok(new Page<object>(list.Cast<object>().ToList(), total, page, pageSize));
    }

    private static async Task<IResult> DetailAsync(
        Guid id, RequestContext ctx, IDbConnectionFactory factory)
    {
        await using var connection = await ctx.OpenScopedAsync(factory);

        var quotation = await connection.QuerySingleOrDefaultAsync("""
            SELECT q.id, q.number, q.status::text AS status,
                   q.risk_score AS "riskScore", q.risk_band::text AS "riskBand",
                   q.created_at AS "createdAt", q.expires_at AS "expiresAt",
                   q.rejection_reasons AS "rejectionReasons",
                   pr.name AS "productName",
                   (a.declared_value).amount AS "insuredValue",
                   CASE c.kind WHEN 'INDIVIDUAL'
                        THEN c.first_name || ' ' || c.last_name
                        ELSE coalesce(c.trade_name, c.legal_name) END AS "customerName",
                   (q.expires_at <= now() AND q.status IN ('DRAFT','CALCULATED')) AS "isExpired",
                   -- Sem isto a tela não distingue "já convertida" de "expirada" e
                   -- atribui o motivo errado ao usuário
                   EXISTS (SELECT 1 FROM proposals p
                            WHERE p.quotation_id = q.id
                              AND p.status NOT IN ('REJECTED','EXPIRED')) AS "hasProposal"
              FROM quotations q
              JOIN customers c           ON c.id  = q.customer_id
              JOIN insurable_assets a    ON a.id  = q.asset_id
              JOIN product_versions pv   ON pv.id = q.product_version_id
              JOIN insurance_products pr ON pr.id = pv.product_id
             WHERE q.id = @id AND q.deleted_at IS NULL
            """, new { id });

        if (quotation is null) return Results.NotFound(new { message = "Cotação não encontrada." });

        var plans = (await connection.QueryAsync("""
            SELECT i.id, i.plan::text AS plan,
                   (i.net_premium).amount   AS "netPremium",
                   (i.total_premium).amount AS "totalPremium",
                   s.risk_multiplier AS "riskMultiplier",
                   s.plan_multiplier AS "planMultiplier",
                   s.engine_version  AS "engineVersion",
                   s.inputs          AS "factors"
              FROM quotation_items i
              LEFT JOIN calculation_snapshots s ON s.quotation_item_id = i.id
             WHERE i.quotation_id = @id
             ORDER BY (i.total_premium).amount
            """, new { id })).ToList();

        var coverages = await connection.QueryAsync("""
            SELECT sc.quotation_item_id AS "planId", cv.code, cv.name,
                   cv.is_mandatory AS "isMandatory",
                   (sc.limit_amount).amount AS "limit",
                   (sc.premium).amount      AS "premium",
                   (sc.deductible).kind     AS "deductibleKind",
                   (sc.deductible).amount   AS "deductibleAmount",
                   (sc.deductible).percent  AS "deductiblePercent"
              FROM selected_coverages sc
              JOIN coverages cv ON cv.id = sc.coverage_id
              JOIN quotation_items i ON i.id = sc.quotation_item_id
             WHERE i.quotation_id = @id
             ORDER BY cv.is_mandatory DESC, cv.name
            """, new { id });

        // Questionário guardado como JSONB — o esquema varia por versão de produto
        var risk = await connection.QuerySingleOrDefaultAsync("""
            SELECT answers, schema_version AS "schemaVersion", computed_score AS "computedScore"
              FROM risk_profiles WHERE quotation_id = @id
            """, new { id });

        return Results.Ok(new { quotation, plans, coverages, risk });
    }

    // ---------------------------------------------------------------- cálculo

    /// <summary>
    /// Cria a cotação calculando os três planos em uma única transação.
    /// </summary>
    /// <remarks>
    /// O cálculo é <b>puro</b>: acontece em memória, sem I/O, antes de qualquer escrita. Isso
    /// mantém a transação curta e torna o motor testável sem banco. O
    /// <c>CalculationSnapshot</c> grava os fatores que produziram cada prêmio, permitindo
    /// reproduzir a oferta meses depois.
    /// </remarks>
    private static async Task<IResult> CreateAsync(
        QuotationInput input, RequestContext ctx, IDbConnectionFactory factory, ActivityStream stream)
    {
        if (ctx.TenantId is null)
            return Results.BadRequest(new { message = "Contexto de tenant ausente." });

        if (Validate(input) is { } problem) return problem;

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ctx.OpenScopedAsync(factory);

        // --- leitura dos dados necessários ao cálculo ------------------------
        var asset = await connection.QuerySingleOrDefaultAsync("""
            SELECT a.id, a.kind::text AS kind, (a.declared_value).amount AS "declaredValue",
                   a.customer_id AS "customerId",
                   coalesce(v.overnight_postal_code, (p.location).postal_code) AS "postalCode",
                   c.broker_id AS "brokerId"
              FROM insurable_assets a
              JOIN customers c ON c.id = a.customer_id
              LEFT JOIN vehicles v ON v.id = a.id
              LEFT JOIN properties p ON p.id = a.id
             WHERE a.id = @assetId AND a.deleted_at IS NULL
            """, new { input.AssetId });

        if (asset is null)
            return Results.NotFound(new { message = "Bem segurável não encontrado." });

        if ((Guid)asset.customerId != input.CustomerId)
            return Results.UnprocessableEntity(new ValidationProblem("Dados inválidos.",
                new Dictionary<string, string[]>
                {
                    ["AssetId"] = ["O bem informado não pertence a este cliente."]
                }));

        var product = await connection.QuerySingleOrDefaultAsync("""
            SELECT pv.id, pv.branch::text AS branch, pv.base_rate AS "baseRate",
                   pv.risk_sensitivity AS "riskSensitivity",
                   pv.max_acceptable_risk AS "maxAcceptableRisk",
                   pv.min_insured_value AS "minInsuredValue",
                   pv.max_insured_value AS "maxInsuredValue"
              FROM product_versions pv
             WHERE pv.id = @productVersionId AND pv.published_at IS NOT NULL
            """, new { input.ProductVersionId });

        if (product is null)
            return Results.NotFound(new { message = "Versão do produto não encontrada." });

        // Invariante: o bem precisa ser compatível com o ramo do produto
        var expectedKind = (string)product.branch == "AUTO" ? "VEHICLE" : "PROPERTY";
        if ((string)asset.kind != expectedKind)
            return Results.UnprocessableEntity(new ValidationProblem("Dados inválidos.",
                new Dictionary<string, string[]>
                {
                    ["ProductVersionId"] =
                    [
                        (string)product.branch == "AUTO"
                            ? "Produto de automóvel exige um veículo como bem segurável."
                            : "Produto residencial exige um imóvel como bem segurável."
                    ]
                }));

        var options = (await connection.QueryAsync<CoverageRow>("""
            SELECT id AS "Id", code AS "Code", name AS "Name",
                   is_mandatory AS "IsMandatory",
                   (min_limit).amount AS "MinLimit", (max_limit).amount AS "MaxLimit",
                   rate_factor AS "RateFactor",
                   (default_deductible).kind    AS "DeductibleKind",
                   (default_deductible).amount  AS "DeductibleAmount",
                   (default_deductible).percent AS "DeductiblePercent"
              FROM coverages WHERE product_version_id = @productVersionId
            """, new { input.ProductVersionId }))
            .Select(r => new CoverageOption(r.Id, r.Code, r.Name, r.IsMandatory,
                r.MinLimit, r.MaxLimit, r.RateFactor,
                r.DeductibleKind, r.DeductibleAmount, r.DeductiblePercent))
            .ToList();

        // --- cálculo puro, sem I/O -------------------------------------------
        var parameters = new ProductParameters(
            (Guid)product.id, (string)product.branch, (decimal)product.baseRate,
            (decimal)product.riskSensitivity, (int)product.maxAcceptableRisk,
            (decimal)product.minInsuredValue, (decimal)product.maxInsuredValue);

        var answers = new RiskAnswers(
            input.HasGarage, input.Usage, input.DriverAge, input.PreviousClaims,
            ((string?)asset.postalCode ?? "0")[..1]);

        IReadOnlyList<CalculationResult> results;
        try
        {
            results = PremiumCalculator.CalculateAllPlans(
                parameters, Money.Of((decimal)asset.declaredValue), answers, options, input.CoverageIds);
        }
        catch (DomainException ex)
        {
            // Recusa também é informação de negócio: a cotação é persistida como REJECTED
            // para que o histórico mostre que houve tentativa e por que foi negada.
            var rejectedId = await PersistRejectedAsync(
                connection, ctx, input, asset, answers, ex);

            stream.Publish(new ProcessingEvent(
                "DomainEvent", "Quotations", "quotations:rejected",
                $"Cotação recusada — {ex.Message}", "DENIED",
                "Quotation", rejectedId, ctx.TenantId, ctx.CorrelationId));

            return Results.UnprocessableEntity(new
            {
                message = ex.Message,
                code = ex.Code,
                quotationId = rejectedId
            });
        }

        // --- persistência em uma transação -----------------------------------
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var quotationId = Guid.CreateVersion7();
            var sequence = await connection.ExecuteScalarAsync<long>(
                "SELECT nextval('app.quotation_number_seq')", transaction: transaction);
            var number = QuotationNumber.Generate(DateTime.UtcNow.Year, sequence).Value;
            var riskScore = results[0].RiskScore;

            await connection.ExecuteAsync("""
                INSERT INTO quotations (id, tenant_id, broker_id, customer_id, asset_id,
                       product_version_id, number, status, risk_score, created_by, expires_at)
                VALUES (@quotationId, @tenantId, @brokerId, @customerId, @assetId,
                        @productVersionId, @number, 'CALCULATED', @riskScore, @actor,
                        now() + interval '30 days')
                """, new
            {
                quotationId,
                tenantId = ctx.TenantId,
                brokerId = (Guid)asset.brokerId,
                input.CustomerId,
                input.AssetId,
                input.ProductVersionId,
                number,
                riskScore = (short)riskScore.Value,
                actor = ctx.ActorId
            }, transaction);

            await connection.ExecuteAsync("""
                INSERT INTO risk_profiles (quotation_id, answers, schema_version, computed_score)
                VALUES (@quotationId, @answers::jsonb, @schemaVersion, @score)
                """, new
            {
                quotationId,
                answers = JsonSerializer.Serialize(answers),
                schemaVersion = PremiumCalculator.EngineVersion,
                score = (short)riskScore.Value
            }, transaction);

            foreach (var result in results)
            {
                var itemId = Guid.CreateVersion7();

                await connection.ExecuteAsync("""
                    INSERT INTO quotation_items (id, quotation_id, plan, net_premium, total_premium)
                    VALUES (@itemId, @quotationId, @plan::plan_tier,
                            ROW(@net,'BRL')::money_amount, ROW(@total,'BRL')::money_amount)
                    """, new
                {
                    itemId,
                    quotationId,
                    plan = result.Plan.Code,
                    net = result.NetPremium.Amount,
                    total = result.TotalPremium.Amount
                }, transaction);

                foreach (var pricing in result.Coverages)
                {
                    await connection.ExecuteAsync("""
                        INSERT INTO selected_coverages (quotation_item_id, coverage_id,
                               limit_amount, deductible, premium)
                        VALUES (@itemId, @coverageId,
                                ROW(@limit,'BRL')::money_amount,
                                ROW(@dKind, @dAmount, @dPercent)::deductible,
                                ROW(@premium,'BRL')::money_amount)
                        """, new
                    {
                        itemId,
                        coverageId = pricing.CoverageId,
                        limit = pricing.Limit.Amount,
                        dKind = pricing.DeductibleKind,
                        dAmount = pricing.DeductibleKind == "FIXED" ? pricing.DeductibleValue : (decimal?)null,
                        dPercent = pricing.DeductibleKind == "PERCENTAGE" ? pricing.DeductibleValue : (decimal?)null,
                        premium = pricing.Premium.Amount
                    }, transaction);
                }

                // Snapshot imutável: permite reproduzir o cálculo campo a campo depois
                await connection.ExecuteAsync("""
                    INSERT INTO calculation_snapshots (quotation_item_id, engine_version, inputs,
                           risk_multiplier, plan_multiplier, base_premium, final_premium)
                    VALUES (@itemId, @engineVersion, @inputs::jsonb, @riskMultiplier,
                            @planMultiplier, ROW(@base,'BRL')::money_amount,
                            ROW(@final,'BRL')::money_amount)
                    """, new
                {
                    itemId,
                    engineVersion = PremiumCalculator.EngineVersion,
                    inputs = JsonSerializer.Serialize(result.Factors),
                    riskMultiplier = result.RiskMultiplier,
                    planMultiplier = result.Plan.Multiplier,
                    @base = result.NetPremium.Amount,
                    final = result.TotalPremium.Amount
                }, transaction);
            }

            await connection.ExecuteAsync("""
                INSERT INTO outbox_messages (tenant_id, message_type, payload, correlation_id,
                       aggregate_type, aggregate_id)
                VALUES (@tenantId, 'QuotationCreated', @payload::jsonb, @correlationId,
                        'Quotation', @quotationId)
                """, new
            {
                tenantId = ctx.TenantId,
                payload = JsonSerializer.Serialize(new { quotationId, number, riskScore = riskScore.Value }),
                correlationId = ctx.CorrelationId,
                quotationId
            }, transaction);

            await transaction.CommitAsync();

            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            stream.Publish(new ProcessingEvent(
                "DomainEvent", "Quotations", "quotations:create",
                $"Cotação {number} — 3 planos, risco {riskScore.Value} ({riskScore.Band})",
                "SUCCESS", "Quotation", quotationId, ctx.TenantId, ctx.CorrelationId, elapsed,
                "INSERT quotations + risk_profiles + 3× quotation_items + selected_coverages "
              + "+ calculation_snapshots + outbox_messages — uma transação"));

            return Results.Created($"/api/quotations/{quotationId}", new
            {
                id = quotationId,
                number,
                riskScore = riskScore.Value,
                riskBand = riskScore.Band.ToString().ToUpperInvariant()
            });
        }
        catch (PostgresException ex)
        {
            await transaction.RollbackAsync();

            stream.Publish(new ProcessingEvent(
                "Error", "Quotations", "quotations:create",
                $"Banco recusou — {ex.SqlState} {ex.ConstraintName}", "ERROR",
                "Quotation", null, ctx.TenantId, ctx.CorrelationId));

            return Results.BadRequest(new { message = "Não foi possível criar a cotação." });
        }
    }

    private static async Task<Guid> PersistRejectedAsync(
        NpgsqlConnection connection, RequestContext ctx, QuotationInput input,
        dynamic asset, RiskAnswers answers, DomainException reason)
    {
        var id = Guid.CreateVersion7();
        var sequence = await connection.ExecuteScalarAsync<long>(
            "SELECT nextval('app.quotation_number_seq')");
        var score = PremiumCalculator.ComputeRiskScore(answers);

        await connection.ExecuteAsync("""
            INSERT INTO quotations (id, tenant_id, broker_id, customer_id, asset_id,
                   product_version_id, number, status, risk_score, rejection_reasons,
                   created_by, expires_at)
            VALUES (@id, @tenantId, @brokerId, @customerId, @assetId, @productVersionId,
                    @number, 'REJECTED', @riskScore, @reasons, @actor,
                    now() + interval '30 days')
            """, new
        {
            id,
            tenantId = ctx.TenantId,
            brokerId = (Guid)asset.brokerId,
            input.CustomerId,
            input.AssetId,
            input.ProductVersionId,
            number = QuotationNumber.Generate(DateTime.UtcNow.Year, sequence).Value,
            riskScore = (short)score.Value,
            reasons = new[] { reason.Message },
            actor = ctx.ActorId
        });

        return id;
    }

    // ---------------------------------------------------------------- conversão

    /// <summary>
    /// Converte a cotação em proposta.
    /// </summary>
    /// <remarks>
    /// A cotação transiciona para <c>CONVERTED</c> e a proposta nasce na <b>mesma transação</b>.
    /// Sem isso, uma falha entre as duas escritas permitiria converter a mesma cotação duas
    /// vezes. O índice único parcial <c>ux_proposals_quotation_active</c> é a garantia final.
    /// </remarks>
    private static async Task<IResult> ConvertAsync(
        Guid id, ConversionInput input, RequestContext ctx,
        IDbConnectionFactory factory, ActivityStream stream)
    {
        if (Validate(input) is { } problem) return problem;

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ctx.OpenScopedAsync(factory);
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var quotation = await connection.QuerySingleOrDefaultAsync("""
                SELECT q.id, q.status::text AS status, q.customer_id AS "customerId",
                       q.broker_id AS "brokerId", q.expires_at AS "expiresAt",
                       (q.expires_at <= now()) AS "isExpired"
                  FROM quotations q
                 WHERE q.id = @id AND q.deleted_at IS NULL
                """, new { id }, transaction);

            if (quotation is null)
            {
                await transaction.RollbackAsync();
                return Results.NotFound(new { message = "Cotação não encontrada." });
            }

            if ((string)quotation.status != "CALCULATED")
            {
                await transaction.RollbackAsync();
                return Results.Conflict(new
                {
                    message = $"Cotação em status {quotation.status} não pode ser convertida.",
                    code = "QUOTATION_NOT_CONVERTIBLE"
                });
            }

            if ((bool)quotation.isExpired)
            {
                await transaction.RollbackAsync();

                stream.Publish(new ProcessingEvent(
                    "AuthorizationDecision", "Quotations", "quotations:convert",
                    "Conversão recusada — cotação expirada", "DENIED",
                    "Quotation", id, ctx.TenantId, ctx.CorrelationId));

                return Results.Conflict(new
                {
                    message = "Cotação expirada. Gere uma nova cotação.",
                    code = "QUOTATION_EXPIRED"
                });
            }

            var item = await connection.QuerySingleOrDefaultAsync("""
                SELECT (net_premium).amount AS "net", (total_premium).amount AS "total"
                  FROM quotation_items WHERE quotation_id = @id AND plan = @plan::plan_tier
                """, new { id, plan = input.Plan }, transaction);

            if (item is null)
            {
                await transaction.RollbackAsync();
                return Results.UnprocessableEntity(new { message = "Plano inexistente nesta cotação." });
            }

            var proposalId = Guid.CreateVersion7();
            var sequence = await connection.ExecuteScalarAsync<long>(
                "SELECT nextval('app.proposal_number_seq')", transaction: transaction);
            var number = ProposalNumber.Generate(DateTime.UtcNow.Year, sequence).Value;

            await connection.ExecuteAsync("""
                INSERT INTO proposals (id, tenant_id, quotation_id, broker_id, customer_id,
                       number, status, chosen_plan, net_premium, total_premium,
                       installment_count, created_by, submitted_at)
                VALUES (@proposalId, @tenantId, @quotationId, @brokerId, @customerId,
                        @number, 'SUBMITTED', @plan::plan_tier,
                        ROW(@net,'BRL')::money_amount, ROW(@total,'BRL')::money_amount,
                        @installments, @actor, now())
                """, new
            {
                proposalId,
                tenantId = ctx.TenantId,
                quotationId = id,
                brokerId = (Guid)quotation.brokerId,
                customerId = (Guid)quotation.customerId,
                number,
                plan = input.Plan,
                net = (decimal)item.net,
                total = (decimal)item.total,
                installments = (short)input.InstallmentCount,
                actor = ctx.ActorId
            }, transaction);

            await connection.ExecuteAsync("""
                UPDATE quotations SET status = 'CONVERTED' WHERE id = @id
                """, new { id }, transaction);

            await connection.ExecuteAsync("""
                INSERT INTO proposal_status_history (tenant_id, proposal_id, from_status,
                       to_status, reason, changed_by, correlation_id)
                VALUES (@tenantId, @proposalId, NULL, 'SUBMITTED',
                        'Convertida da cotação', @actor, @correlationId)
                """, new
            {
                tenantId = ctx.TenantId,
                proposalId,
                actor = ctx.ActorId,
                correlationId = ctx.CorrelationId
            }, transaction);

            await transaction.CommitAsync();

            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            stream.Publish(new ProcessingEvent(
                "DomainEvent", "Proposals", "quotations:convert",
                $"Proposta {number} criada a partir da cotação — plano {input.Plan}",
                "SUCCESS", "Proposal", proposalId, ctx.TenantId, ctx.CorrelationId, elapsed,
                "INSERT proposals + UPDATE quotations SET status='CONVERTED' — mesma transação"));

            return Results.Created($"/api/proposals/{proposalId}", new { id = proposalId, number });
        }
        catch (PostgresException ex)
        {
            await transaction.RollbackAsync();

            // ux_proposals_quotation_active: uma cotação gera no máximo uma proposta viva
            if (ex.ConstraintName == "ux_proposals_quotation_active")
                return Results.Conflict(new
                {
                    message = "Esta cotação já possui uma proposta ativa.",
                    code = "QUOTATION_ALREADY_CONVERTED"
                });

            stream.Publish(new ProcessingEvent(
                "Error", "Quotations", "quotations:convert",
                $"Banco recusou — {ex.SqlState} {ex.ConstraintName}", "ERROR",
                "Quotation", id, ctx.TenantId, ctx.CorrelationId));

            return Results.BadRequest(new { message = "Não foi possível converter a cotação." });
        }
    }

    // ---------------------------------------------------------------- apoio

    private sealed record CoverageRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IsMandatory { get; init; }
        public decimal MinLimit { get; init; }
        public decimal MaxLimit { get; init; }
        public decimal RateFactor { get; init; }
        public string DeductibleKind { get; init; } = "FIXED";
        public decimal? DeductibleAmount { get; init; }
        public decimal? DeductiblePercent { get; init; }
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
