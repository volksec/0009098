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

// ---------------------------------------------------------------- clientes

app.MapGet("/api/customers", async (RequestContext ctx, IDbConnectionFactory factory,
                                    string? search, int limit = 25) =>
{
    await using var connection = await ctx.OpenScopedAsync(factory);

    // Consulta PARAMETRIZADA — o filtro do usuário nunca é concatenado
    return Results.Ok(await connection.QueryAsync("""
        SELECT c.id, c.kind::text AS kind, c.status::text AS status,
               CASE c.kind WHEN 'INDIVIDUAL'
                    THEN c.first_name || ' ' || c.last_name
                    ELSE coalesce(c.trade_name, c.legal_name) END AS "displayName",
               c.created_at AS "createdAt",
               b.full_name  AS "brokerName",
               (SELECT count(*) FROM insurable_assets a
                 WHERE a.customer_id = c.id AND a.deleted_at IS NULL) AS "assetCount",
               (SELECT count(*) FROM policies p
                 WHERE p.customer_id = c.id AND p.status = 'ACTIVE')  AS "activePolicies"
          FROM customers c
          JOIN brokers b ON b.id = c.broker_id
         WHERE c.deleted_at IS NULL
           AND (@search IS NULL OR c.search_vector @@ plainto_tsquery('portuguese', @search))
         ORDER BY c.created_at DESC
         LIMIT @limit
        """, new { search, limit = Math.Clamp(limit, 1, 100) }));
}).WithTags("Clientes");

app.MapGet("/api/customers/{id:guid}", async (Guid id, RequestContext ctx, IDbConnectionFactory factory) =>
{
    await using var connection = await ctx.OpenScopedAsync(factory);

    var customer = await connection.QuerySingleOrDefaultAsync("""
        SELECT c.id, c.kind::text AS kind, c.status::text AS status,
               CASE c.kind WHEN 'INDIVIDUAL'
                    THEN c.first_name || ' ' || c.last_name
                    ELSE coalesce(c.trade_name, c.legal_name) END AS "displayName",
               c.created_at AS "createdAt"
          FROM customers c WHERE c.id = @id AND c.deleted_at IS NULL
        """, new { id });

    // 404 e não 403: responder 403 confirmaria que o recurso existe em outro tenant,
    // transformando o controle de acesso em oráculo de enumeração.
    return customer is null ? Results.NotFound() : Results.Ok(customer);
}).WithTags("Clientes");

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

app.Run();
