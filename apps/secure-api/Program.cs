using Dapper;
using PortalDoCorretor.SecureApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new()
{
    Title = "Portal do Corretor — API",
    Version = "v1",
    Description = "Plataforma de gestão para corretores de seguros. "
                + "Dados sintéticos; banco, transações e controles reais."
}));

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddSingleton<ActivityStream>();
builder.Services.AddScoped<RequestContext>();

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader().AllowAnyMethod()
    .WithExposedHeaders("X-Correlation-Id")));

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Portal do Corretor v1"));
app.UseCors();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";

    var incoming = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    var correlationId = Guid.TryParse(incoming, out var parsed) && parsed != Guid.Empty
        ? parsed : Guid.CreateVersion7();

    context.Items["CorrelationId"] = correlationId;
    headers["X-Correlation-Id"] = correlationId.ToString();

    await next();
});

// ---------------------------------------------------------------- health

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).WithTags("Health");

app.MapGet("/health/ready", async (IDbConnectionFactory factory) =>
{
    try
    {
        await using var connection = await factory.OpenAsync();
        var tables = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'");

        return tables > 0
            ? Results.Ok(new { status = "ready", tables })
            : Results.Json(new { status = "degraded", reason = "migrations nao aplicadas" }, statusCode: 503);
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", reason = ex.GetType().Name }, statusCode: 503);
    }
}).WithTags("Health");

// ---------------------------------------------------------------- corretoras

app.MapGet("/api/brokerages", async (IDbConnectionFactory factory) =>
{
    await using var connection = await factory.OpenAsync();
    return Results.Ok(await connection.QueryAsync("""
        SELECT id, trade_name AS "tradeName", susep_registration AS "susepRegistration", status
          FROM brokerages WHERE deleted_at IS NULL ORDER BY trade_name
        """));
}).WithTags("Corretoras");

// ---------------------------------------------------------------- índice da API
// `/api` é prefixo de rota, não rota: sem isto, quem abrisse a URL anunciada pelo
// start.sh recebia 404 e concluía que a API estava fora. O índice devolve o mapa
// real e o cabeçalho que a maioria das rotas exige.
app.MapGet("/api", (HttpContext http) =>
{
    var raiz = $"{http.Request.Scheme}://{http.Request.Host}";

    return Results.Ok(new
    {
        nome = "Portal do Corretor — API",
        documentacao = $"{raiz}/swagger",
        especificacao = $"{raiz}/swagger/v1/swagger.json",
        cabecalhos = new
        {
            xTenantId = "obrigatório na maioria das rotas — identifica a corretora",
            xActorId = "define quais comissões a política RESTRICTIVE torna visíveis",
            idempotencyKey = "emissão de apólice: reenviar a mesma chave devolve a resposta original"
        },
        // Começar por /api/brokerages é deliberado: é a única rota que dispensa
        // X-Tenant-Id, e o id devolvido por ela alimenta todas as demais.
        comecarPor = $"{raiz}/api/brokerages",
        rotas = new
        {
            corretoras = "GET /api/brokerages",
            corretores = "GET /api/brokers",
            painel = "GET /api/dashboard",
            clientes = "GET|POST /api/customers · GET|PUT|DELETE /api/customers/{id} · POST /api/customers/{id}/restore",
            bens = "GET /api/customers/{id}/assets",
            produtos = "GET /api/products",
            cotacoes = "GET|POST /api/quotations · GET /api/quotations/{id} · POST /api/quotations/{id}/convert",
            propostas = "GET /api/proposals · GET /api/proposals/{id} · POST /api/proposals/{id}/underwrite · POST /api/proposals/{id}/issue",
            apolices = "GET /api/policies",
            faturamento = "GET /api/billing/summary · GET /api/billing/installments · POST /api/billing/installments/{id}/pay",
            comissoes = "GET /api/commissions · GET /api/commissions/monthly · POST /api/commissions/{id}/release · POST /api/commissions/{id}/reverse",
            sinistros = "GET /api/claims · GET /api/claims/{id} · POST /api/claims · POST /api/claims/{id}/events · POST /api/claims/{id}/decide",
            engenharia = "GET /api/engineering/schema · /rls · /invariants",
            eventos = "GET /api/events/stream (SSE) · GET /api/events/recent"
        }
    });
}).WithTags("Índice").WithSummary("Mapa das rotas disponíveis");

// ---------------------------------------------------------------- módulos
// Cada área vive no próprio arquivo de endpoints; aqui só o registro.
app.MapCustomerEndpoints();    // consulta, cadastro, edição, exclusão lógica, restauração
app.MapBillingEndpoints();     // parcelas, pagamento simulado, inadimplência
app.MapCommissionEndpoints();  // extrato, consolidação mensal, liberação, estorno
app.MapClaimEndpoints();       // aviso, linha do tempo, decisão simulada
app.MapQuotationEndpoints();   // catálogo, cálculo dos 3 planos, conversão
app.MapProposalEndpoints();    // underwriting simulado e emissão de apólice

// ---------------------------------------------------------------- corretores

app.MapGet("/api/brokers", async (RequestContext ctx, IDbConnectionFactory factory) =>
{
    await using var connection = await ctx.OpenScopedAsync(factory);
    return Results.Ok(await connection.QueryAsync("""
        SELECT id, user_id AS "userId", full_name AS "fullName",
               susep_registration AS "susepRegistration",
               status::text AS status
          FROM brokers WHERE deleted_at IS NULL ORDER BY full_name
        """));
}).WithTags("Corretores");

// ---------------------------------------------------------------- apólices

app.MapGet("/api/policies", async (RequestContext ctx, IDbConnectionFactory factory,
                                   string? status, int limit = 25) =>
{
    await using var connection = await ctx.OpenScopedAsync(factory);

    return Results.Ok(await connection.QueryAsync("""
        SELECT p.id, p.number, p.status::text AS status,
               lower(p.coverage_period) AS "periodStart",
               upper(p.coverage_period) AS "periodEnd",
               (p.total_premium).amount AS "totalPremium",
               pr.name AS "productName",
               CASE c.kind WHEN 'INDIVIDUAL'
                    THEN c.first_name || ' ' || c.last_name
                    ELSE coalesce(c.trade_name, c.legal_name) END AS "customerName",
               p.issued_at AS "issuedAt"
          FROM policies p
          JOIN customers c           ON c.id  = p.customer_id
          JOIN product_versions pv   ON pv.id = p.product_version_id
          JOIN insurance_products pr ON pr.id = pv.product_id
         WHERE (@status IS NULL OR p.status::text = @status)
         ORDER BY p.issued_at DESC
         LIMIT @limit
        """, new { status, limit = Math.Clamp(limit, 1, 100) }));
}).WithTags("Apólices");

// ---------------------------------------------------------------- dashboard

app.MapGet("/api/dashboard", async (RequestContext ctx, IDbConnectionFactory factory) =>
{
    await using var connection = await ctx.OpenScopedAsync(factory);

    return Results.Ok(await connection.QuerySingleAsync("""
        SELECT (SELECT count(*) FROM customers  WHERE deleted_at IS NULL)     AS "customers",
               (SELECT count(*) FROM quotations WHERE status = 'CALCULATED')  AS "openQuotations",
               (SELECT count(*) FROM proposals
                 WHERE status IN ('SUBMITTED','UNDER_ANALYSIS','PENDING'))    AS "pendingProposals",
               (SELECT count(*) FROM policies   WHERE status = 'ACTIVE')      AS "activePolicies",
               (SELECT count(*) FROM claims
                 WHERE status NOT IN ('SETTLED','CLOSED','DENIED'))           AS "openClaims",
               (SELECT coalesce(sum((amount).amount), 0) FROM commissions
                 WHERE status IN ('FORECAST','RELEASED'))                     AS "forecastCommission",
               (SELECT count(*) FROM policies
                 WHERE status = 'ACTIVE'
                   AND upper(coverage_period) <= CURRENT_DATE + 45)           AS "upcomingRenewals"
        """));
}).WithTags("Dashboard");

// ---------------------------------------------------------------- engenharia
// Lido do CATÁLOGO do PostgreSQL em tempo real, não de uma lista fixa no código.

app.MapGet("/api/engineering/schema", async (IDbConnectionFactory factory) =>
{
    await using var connection = await factory.OpenAsync();

    return Results.Ok(await connection.QuerySingleAsync("""
        SELECT (SELECT count(*) FROM pg_tables  WHERE schemaname = 'public')  AS "tables",
               (SELECT count(*) FROM pg_indexes WHERE schemaname = 'public')  AS "indexes",
               (SELECT count(*) FROM pg_class WHERE relrowsecurity
                  AND relnamespace = 'public'::regnamespace)                  AS "tablesWithRls",
               (SELECT count(*) FROM pg_policies WHERE schemaname = 'public') AS "rlsPolicies",
               (SELECT count(*) FROM pg_class WHERE relispartition)           AS "partitions",
               (SELECT count(*) FROM pg_constraint WHERE contype = 'x')       AS "exclusionConstraints",
               (SELECT count(*) FROM pg_type WHERE typtype = 'e'
                  AND typnamespace = 'public'::regnamespace)                  AS "enums",
               (SELECT count(*) FROM pg_type WHERE typtype = 'c'
                  AND typrelid IN (SELECT oid FROM pg_class WHERE relkind = 'c')
                  AND typnamespace = 'public'::regnamespace)                  AS "compositeTypes"
        """));
}).WithTags("Engenharia");

app.MapGet("/api/engineering/rls", async (IDbConnectionFactory factory) =>
{
    await using var connection = await factory.OpenAsync();

    return Results.Ok(await connection.QueryAsync("""
        SELECT p.tablename  AS "table",
               p.policyname AS "policy",
               p.cmd        AS "command",
               array_to_string(p.roles, ', ') AS "roles",
               c.relforcerowsecurity AS "forced"
          FROM pg_policies p
          JOIN pg_class c ON c.relname = p.tablename
                         AND c.relnamespace = 'public'::regnamespace
         WHERE p.schemaname = 'public'
         ORDER BY p.tablename, p.policyname
        """));
}).WithTags("Engenharia");

// Invariantes do modelo, lidas das constraints reais do banco.
app.MapGet("/api/engineering/invariants", async (IDbConnectionFactory factory) =>
{
    await using var connection = await factory.OpenAsync();

    return Results.Ok(await connection.QueryAsync("""
        SELECT conname AS "name",
               CASE contype WHEN 'x' THEN 'EXCLUSION' WHEN 'c' THEN 'CHECK'
                            WHEN 'u' THEN 'UNIQUE'    WHEN 'f' THEN 'FOREIGN KEY'
                            ELSE contype::text END AS "kind",
               conrelid::regclass::text AS "table",
               pg_get_constraintdef(oid) AS "definition"
          FROM pg_constraint
         WHERE connamespace = 'public'::regnamespace
           AND contype IN ('x','c','u')
           AND conname NOT LIKE '%_not_null'
         ORDER BY contype DESC, conrelid::regclass::text, conname
         LIMIT 200
        """));
}).WithTags("Engenharia");

// ---------------------------------------------------------------- Live Processing Console
// Stream de eventos em tempo real via Server-Sent Events.

app.MapGet("/api/events/stream", async (HttpContext context, ActivityStream stream,
                                        CancellationToken cancellationToken) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Connection = "keep-alive";
    // Impede que um proxy reverso acumule a resposta em buffer e destrua o "tempo real"
    context.Response.Headers["X-Accel-Buffering"] = "no";

    var reader = stream.Subscribe(out var subscription);
    using (subscription)
    {
        // Envia o histórico recente para que quem acabou de conectar já veja contexto
        foreach (var recent in stream.Recent())
            await WriteEventAsync(context, recent, cancellationToken);

        await context.Response.Body.FlushAsync(cancellationToken);

        // Heartbeat: sem tráfego, proxies e navegadores encerram a conexão ociosa
        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(20));
        var heartbeatTask = HeartbeatAsync(context, heartbeat, cancellationToken);

        try
        {
            await foreach (var processingEvent in reader.ReadAllAsync(cancellationToken))
            {
                await WriteEventAsync(context, processingEvent, cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Cliente desconectou — encerramento normal, não é erro
        }

        await heartbeatTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
    }

    static async Task WriteEventAsync(HttpContext context, ProcessingEvent processingEvent,
                                      CancellationToken cancellationToken)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(processingEvent,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        await context.Response.WriteAsync($"id: {processingEvent.Id}\n", cancellationToken);
        await context.Response.WriteAsync($"event: {processingEvent.Category}\n", cancellationToken);
        await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
    }

    static async Task HeartbeatAsync(HttpContext context, PeriodicTimer timer,
                                     CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await context.Response.WriteAsync(": keep-alive\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}).WithTags("Observabilidade")
  .WithSummary("Stream SSE do Live Processing Console")
  .ExcludeFromDescription();

// Snapshot dos eventos recentes — fallback por polling quando SSE não estiver disponível.
app.MapGet("/api/events/recent", (ActivityStream stream) => Results.Ok(stream.Recent()))
   .WithTags("Observabilidade");

app.Run();
