using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Dapper;
using Npgsql;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.SecureApi;

/// <summary>
/// Área administrativa de clientes: consulta paginada, cadastro, edição, exclusão lógica
/// e restauração.
/// </summary>
public static class CustomerEndpoints
{
    public static void MapCustomerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/customers").WithTags("Clientes");

        // Rota vazia, e não "/": dentro de um MapGroup, "/" produz "/api/customers/" com
        // barra final, que não casa com "/api/customers" e devolve 405.
        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("", CreateAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", SoftDeleteAsync);
        group.MapPost("/{id:guid}/restore", RestoreAsync);
    }

    // ---------------------------------------------------------------- consulta

    private static async Task<IResult> ListAsync(
        RequestContext ctx, IDbConnectionFactory factory, ActivityStream stream,
        string? search, string? kind, string? status, Guid? brokerId = null,
        bool includeDeleted = false, int page = 1, int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ctx.OpenScopedAsync(factory);

        const string filter = """
              FROM customers c
              JOIN brokers b ON b.id = c.broker_id
             WHERE (@includeDeleted OR c.deleted_at IS NULL)
               AND (@kind   IS NULL OR c.kind::text   = @kind)
               AND (@status IS NULL OR c.status::text = @status)
               AND (@brokerId IS NULL OR c.broker_id = @brokerId)
               AND (@search IS NULL
                    OR c.search_vector @@ plainto_tsquery('portuguese', @search)
                    OR coalesce(c.first_name,'') || ' ' || coalesce(c.last_name,'')
                       || coalesce(c.legal_name,'') ILIKE '%' || @search || '%')
            """;

        var parameters = new
        {
            search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            kind = string.IsNullOrWhiteSpace(kind) ? null : kind,
            status = string.IsNullOrWhiteSpace(status) ? null : status,
            brokerId,
            includeDeleted,
            offset = (page - 1) * pageSize,
            limit = pageSize
        };

        var total = await connection.ExecuteScalarAsync<int>($"SELECT count(*) {filter}", parameters);

        var items = await connection.QueryAsync($"""
            SELECT c.id, c.kind::text AS kind, c.status::text AS status,
                   CASE c.kind WHEN 'INDIVIDUAL'
                        THEN c.first_name || ' ' || c.last_name
                        ELSE coalesce(c.trade_name, c.legal_name) END AS "displayName",
                   c.first_name AS "firstName", c.last_name AS "lastName", c.birth_date AS "birthDate",
                   c.occupation, c.legal_name AS "legalName", c.trade_name AS "tradeName",
                   c.cnae_code AS "cnaeCode", c.company_size AS "companySize",
                   c.broker_id AS "brokerId", b.full_name AS "brokerName",
                   c.created_at AS "createdAt", c.deleted_at AS "deletedAt",
                   c.deletion_reason AS "deletionReason",
                   (SELECT count(*) FROM insurable_assets a
                     WHERE a.customer_id = c.id AND a.deleted_at IS NULL) AS "assetCount",
                   (SELECT count(*) FROM policies p
                     WHERE p.customer_id = c.id AND p.status = 'ACTIVE')  AS "activePolicies",
                   (SELECT ct.email FROM contacts ct
                     WHERE ct.customer_id = c.id AND ct.is_primary AND ct.deleted_at IS NULL
                     LIMIT 1) AS "email",
                   (SELECT ct.phone FROM contacts ct
                     WHERE ct.customer_id = c.id AND ct.is_primary AND ct.deleted_at IS NULL
                     LIMIT 1) AS "phone"
            {filter}
             ORDER BY c.created_at DESC, c.id
             OFFSET @offset LIMIT @limit
            """, parameters);

        var list = items.ToList();
        var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        stream.Publish(new ProcessingEvent(
            "DatabaseQuery", "Customers", "customers:list",
            $"{list.Count} de {total} cliente(s) — página {page}", "SUCCESS",
            "Customer", null, ctx.TenantId, ctx.CorrelationId, elapsed,
            Redaction.CompactSql("SELECT … FROM customers JOIN brokers … OFFSET @offset LIMIT @limit")));

        return Results.Ok(new Page<object>(list.Cast<object>().ToList(), total, page, pageSize));
    }

    private static async Task<IResult> GetAsync(
        Guid id, RequestContext ctx, IDbConnectionFactory factory, ActivityStream stream)
    {
        await using var connection = await ctx.OpenScopedAsync(factory);

        var customer = await connection.QuerySingleOrDefaultAsync("""
            SELECT c.id, c.kind::text AS kind, c.status::text AS status,
                   CASE c.kind WHEN 'INDIVIDUAL'
                        THEN c.first_name || ' ' || c.last_name
                        ELSE coalesce(c.trade_name, c.legal_name) END AS "displayName",
                   c.first_name AS "firstName", c.last_name AS "lastName", c.birth_date AS "birthDate",
                   c.occupation, c.legal_name AS "legalName", c.trade_name AS "tradeName",
                   c.cnae_code AS "cnaeCode", c.company_size AS "companySize",
                   c.broker_id AS "brokerId", c.created_at AS "createdAt",
                   c.deleted_at AS "deletedAt"
              FROM customers c WHERE c.id = @id
            """, new { id });

        if (customer is null)
        {
            // 404 e não 403: um 403 confirmaria que o recurso existe em outro tenant,
            // transformando o controle de acesso em oráculo de enumeração.
            stream.Publish(new ProcessingEvent(
                "RowLevelSecurity", "Customers", "customers:get",
                "Recurso não visível para o tenant corrente — resposta 404", "DENIED",
                "Customer", id, ctx.TenantId, ctx.CorrelationId));

            return Results.NotFound(new { message = "Cliente não encontrado." });
        }

        return Results.Ok(customer);
    }

    // ---------------------------------------------------------------- cadastro

    private static async Task<IResult> CreateAsync(
        CustomerInput input, RequestContext ctx, IDbConnectionFactory factory, ActivityStream stream)
    {
        if (ctx.TenantId is null)
            return Results.BadRequest(new { message = "Contexto de tenant ausente." });

        if (Validate(input) is { } problem) return problem;

        DocumentNumber document;
        try
        {
            document = DocumentNumber.Parse(input.Document);
        }
        catch (DomainException ex)
        {
            return Invalid(nameof(input.Document), ex.Message);
        }

        // A invariante de tipo é do domínio, não do formulário
        if (input.Kind == "INDIVIDUAL" && document.Kind != DocumentKind.Cpf)
            return Invalid(nameof(input.Document), "Pessoa física exige CPF.");
        if (input.Kind == "BUSINESS" && document.Kind != DocumentKind.Cnpj)
            return Invalid(nameof(input.Document), "Pessoa jurídica exige CNPJ.");

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ctx.OpenScopedAsync(factory);
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var id = Guid.CreateVersion7();

            // O tenant vem do CONTEXTO, nunca do corpo da requisição
            await connection.ExecuteAsync("""
                INSERT INTO customers (id, tenant_id, broker_id, kind, document_encrypted,
                       document_hash, first_name, last_name, birth_date, occupation,
                       legal_name, trade_name, cnae_code, company_size, created_by)
                VALUES (@id, @tenantId, @brokerId, @kind::customer_kind, @documentEncrypted,
                        @documentHash, @firstName, @lastName, @birthDate, @occupation,
                        @legalName, @tradeName, @cnaeCode, @companySize, @actor)
                """, new
            {
                id,
                tenantId = ctx.TenantId,
                brokerId = input.BrokerId,
                kind = input.Kind,
                documentEncrypted = new byte[] { 0x01 },
                documentHash = document.SearchHash(DocumentPepper),
                firstName = input.Kind == "INDIVIDUAL" ? input.FirstName?.Trim() : null,
                lastName = input.Kind == "INDIVIDUAL" ? input.LastName?.Trim() : null,
                // Npgsql não mapeia DateOnly? diretamente em parâmetro dinâmico do Dapper
                birthDate = input.Kind == "INDIVIDUAL" && input.BirthDate.HasValue
                    ? input.BirthDate.Value.ToDateTime(TimeOnly.MinValue)
                    : (DateTime?)null,
                occupation = input.Kind == "INDIVIDUAL" ? input.Occupation?.Trim() : null,
                legalName = input.Kind == "BUSINESS" ? input.LegalName?.Trim() : null,
                tradeName = input.Kind == "BUSINESS" ? input.TradeName?.Trim() : null,
                cnaeCode = input.Kind == "BUSINESS" ? input.CnaeCode?.Trim() : null,
                companySize = input.Kind == "BUSINESS" ? input.CompanySize : null,
                actor = ctx.ActorId
            }, transaction);

            // O agregado exige ao menos um contato ativo
            await connection.ExecuteAsync("""
                INSERT INTO contacts (tenant_id, customer_id, kind, email, phone, is_primary)
                VALUES (@tenantId, @customerId, 'PERSONAL', @email, @phone, true)
                """, new
            {
                tenantId = ctx.TenantId,
                customerId = id,
                email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim().ToLowerInvariant(),
                phone = OnlyDigits(input.Phone)
            }, transaction);

            await transaction.CommitAsync();

            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            stream.Publish(new ProcessingEvent(
                "DomainEvent", "Customers", "customers:create",
                $"Cliente cadastrado — documento {document.Masked}", "SUCCESS",
                "Customer", id, ctx.TenantId, ctx.CorrelationId, elapsed,
                "INSERT INTO customers … ; INSERT INTO contacts … (mesma transação)"));

            return Results.Created($"/api/customers/{id}", new { id });
        }
        catch (PostgresException ex)
        {
            await transaction.RollbackAsync();
            return TranslateDatabaseError(ex, stream, ctx, "customers:create");
        }
    }

    // ---------------------------------------------------------------- edição

    private static async Task<IResult> UpdateAsync(
        Guid id, CustomerUpdateInput input, RequestContext ctx,
        IDbConnectionFactory factory, ActivityStream stream)
    {
        if (Validate(input) is { } problem) return problem;

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ctx.OpenScopedAsync(factory);
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // O documento e o tipo NÃO são editáveis: mudá-los alteraria a identidade do
            // cliente e invalidaria o histórico de apólices já emitidas em seu nome.
            var affected = await connection.ExecuteAsync("""
                UPDATE customers
                   SET first_name = @firstName, last_name = @lastName, birth_date = @birthDate,
                       occupation = @occupation, legal_name = @legalName, trade_name = @tradeName,
                       cnae_code = @cnaeCode, company_size = @companySize,
                       broker_id = @brokerId, updated_at = now(), updated_by = @actor
                 WHERE id = @id AND deleted_at IS NULL
                """, new
            {
                id,
                brokerId = input.BrokerId,
                firstName = input.FirstName?.Trim(),
                lastName = input.LastName?.Trim(),
                birthDate = input.BirthDate?.ToDateTime(TimeOnly.MinValue),
                occupation = input.Occupation?.Trim(),
                legalName = input.LegalName?.Trim(),
                tradeName = input.TradeName?.Trim(),
                cnaeCode = input.CnaeCode?.Trim(),
                companySize = input.CompanySize,
                actor = ctx.ActorId
            }, transaction);

            if (affected == 0)
            {
                await transaction.RollbackAsync();
                return Results.NotFound(new { message = "Cliente não encontrado." });
            }

            await connection.ExecuteAsync("""
                UPDATE contacts SET email = @email, phone = @phone
                 WHERE customer_id = @id AND is_primary AND deleted_at IS NULL
                """, new
            {
                id,
                email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim().ToLowerInvariant(),
                phone = OnlyDigits(input.Phone)
            }, transaction);

            await transaction.CommitAsync();

            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            stream.Publish(new ProcessingEvent(
                "DomainEvent", "Customers", "customers:update",
                "Cliente atualizado", "SUCCESS", "Customer", id,
                ctx.TenantId, ctx.CorrelationId, elapsed,
                "UPDATE customers … WHERE id = @id AND deleted_at IS NULL"));

            return Results.Ok(new { id });
        }
        catch (PostgresException ex)
        {
            await transaction.RollbackAsync();
            return TranslateDatabaseError(ex, stream, ctx, "customers:update");
        }
    }

    // ---------------------------------------------------------------- exclusão lógica

    private static async Task<IResult> SoftDeleteAsync(
        Guid id,
        // Minimal API não infere corpo em DELETE — o motivo da exclusão é obrigatório,
        // então precisa ser declarado explicitamente
        [Microsoft.AspNetCore.Mvc.FromBody] DeletionInput input,
        RequestContext ctx, IDbConnectionFactory factory, ActivityStream stream)
    {
        if (Validate(input) is { } problem) return problem;

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ctx.OpenScopedAsync(factory);
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // Guarda de integridade: cliente com apólice vigente não pode ser removido.
            // A regra cruza a fronteira com o agregado Policy, então vive aqui e não no
            // agregado Customer.
            var activePolicies = await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM policies WHERE customer_id = @id AND status = 'ACTIVE'",
                new { id }, transaction);

            if (activePolicies > 0)
            {
                await transaction.RollbackAsync();

                stream.Publish(new ProcessingEvent(
                    "AuthorizationDecision", "Customers", "customers:delete",
                    $"Exclusão recusada — {activePolicies} apólice(s) vigente(s)", "DENIED",
                    "Customer", id, ctx.TenantId, ctx.CorrelationId));

                return Results.Conflict(new
                {
                    message = $"Cliente possui {activePolicies} apólice(s) vigente(s) e não pode ser excluído.",
                    code = "CUSTOMER_HAS_ACTIVE_POLICIES"
                });
            }

            var batchId = Guid.CreateVersion7();

            var affected = await connection.ExecuteAsync("""
                UPDATE customers
                   SET deleted_at = now(), deleted_by = @actor, deletion_reason = @reason,
                       deletion_batch_id = @batchId, status = 'INACTIVE'
                 WHERE id = @id AND deleted_at IS NULL
                """, new { id, actor = ctx.ActorId, reason = input.Reason.Trim(), batchId }, transaction);

            if (affected == 0)
            {
                await transaction.RollbackAsync();
                return Results.NotFound(new { message = "Cliente não encontrado ou já excluído." });
            }

            // Cascata LÓGICA aplicada na mesma transação — não ON DELETE CASCADE físico
            await connection.ExecuteAsync("""
                UPDATE contacts SET deleted_at = now() WHERE customer_id = @id AND deleted_at IS NULL;
                UPDATE addresses SET deleted_at = now() WHERE customer_id = @id AND deleted_at IS NULL;
                UPDATE insurable_assets
                   SET deleted_at = now(), deleted_by = @actor, deletion_reason = @reason,
                       deletion_batch_id = @batchId
                 WHERE customer_id = @id AND deleted_at IS NULL;
                """, new { id, actor = ctx.ActorId, reason = input.Reason.Trim(), batchId }, transaction);

            await transaction.CommitAsync();

            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            stream.Publish(new ProcessingEvent(
                "AuditEvent", "Customers", "customers:delete",
                $"Exclusão lógica com cascata — motivo: {input.Reason.Trim()}", "SUCCESS",
                "Customer", id, ctx.TenantId, ctx.CorrelationId, elapsed,
                "UPDATE customers SET deleted_at … ; cascata em contacts, addresses, insurable_assets"));

            return Results.Ok(new { id, deletionBatchId = batchId });
        }
        catch (PostgresException ex)
        {
            await transaction.RollbackAsync();
            return TranslateDatabaseError(ex, stream, ctx, "customers:delete");
        }
    }

    private static async Task<IResult> RestoreAsync(
        Guid id, RequestContext ctx, IDbConnectionFactory factory, ActivityStream stream)
    {
        await using var connection = await ctx.OpenScopedAsync(factory);
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var batchId = await connection.ExecuteScalarAsync<Guid?>(
                "SELECT deletion_batch_id FROM customers WHERE id = @id AND deleted_at IS NOT NULL",
                new { id }, transaction);

            var affected = await connection.ExecuteAsync("""
                UPDATE customers
                   SET deleted_at = NULL, deleted_by = NULL, deletion_reason = NULL,
                       deletion_batch_id = NULL, status = 'ACTIVE',
                       updated_at = now(), updated_by = @actor
                 WHERE id = @id AND deleted_at IS NOT NULL
                """, new { id, actor = ctx.ActorId }, transaction);

            if (affected == 0)
            {
                await transaction.RollbackAsync();
                return Results.NotFound(new { message = "Cliente não encontrado ou não está excluído." });
            }

            // Restaura apenas o que saiu no MESMO lote: filhos apagados antes, por decisão
            // independente, continuam apagados.
            await connection.ExecuteAsync("""
                UPDATE contacts SET deleted_at = NULL WHERE customer_id = @id;
                UPDATE addresses SET deleted_at = NULL WHERE customer_id = @id;
                UPDATE insurable_assets
                   SET deleted_at = NULL, deleted_by = NULL, deletion_reason = NULL,
                       deletion_batch_id = NULL
                 WHERE customer_id = @id AND deletion_batch_id = @batchId;
                """, new { id, batchId }, transaction);

            await transaction.CommitAsync();

            stream.Publish(new ProcessingEvent(
                "AuditEvent", "Customers", "customers:restore",
                "Cliente restaurado", "SUCCESS", "Customer", id, ctx.TenantId, ctx.CorrelationId));

            return Results.Ok(new { id });
        }
        catch (PostgresException ex)
        {
            await transaction.RollbackAsync();
            return TranslateDatabaseError(ex, stream, ctx, "customers:restore");
        }
    }

    // ---------------------------------------------------------------- apoio

    private static readonly byte[] DocumentPepper =
        System.Text.Encoding.UTF8.GetBytes(
            Environment.GetEnvironmentVariable("PDC_DOCUMENT_PEPPER") ?? "pdc-local-dev-pepper");

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

        return Results.UnprocessableEntity(
            new ValidationProblem("Dados inválidos.", errors));
    }

    private static IResult Invalid(string field, string message) =>
        Results.UnprocessableEntity(new ValidationProblem(
            "Dados inválidos.", new Dictionary<string, string[]> { [field] = [message] }));

    /// <summary>
    /// Traduz o erro do banco para uma mensagem acionável, sem vazar detalhe interno.
    /// </summary>
    /// <remarks>
    /// Cada constraint violada vira uma mensagem que explica a regra de negócio. É o ponto
    /// em que a invariante do banco chega ao usuário como orientação, e não como stack trace.
    /// </remarks>
    private static IResult TranslateDatabaseError(
        PostgresException ex, ActivityStream stream, RequestContext ctx, string operation)
    {
        stream.Publish(new ProcessingEvent(
            "Error", "Customers", operation,
            $"Banco recusou a operação — {ex.SqlState} {ex.ConstraintName}", "ERROR",
            "Customer", null, ctx.TenantId, ctx.CorrelationId));

        var (status, message, code) = ex.ConstraintName switch
        {
            "ux_customers_tenant_document" =>
                (StatusCodes.Status409Conflict,
                 "Já existe um cliente com este documento nesta corretora.", "CUSTOMER_DUPLICATE"),

            "ck_customers_individual_fields" =>
                (StatusCodes.Status422UnprocessableEntity,
                 "Pessoa física exige nome, sobrenome e data de nascimento, e não aceita razão social.",
                 "CUSTOMER_INDIVIDUAL_FIELDS"),

            "ck_customers_business_fields" =>
                (StatusCodes.Status422UnprocessableEntity,
                 "Pessoa jurídica exige razão social e CNAE, e não aceita dados de pessoa física.",
                 "CUSTOMER_BUSINESS_FIELDS"),

            "ck_customers_birth_date_past" =>
                (StatusCodes.Status422UnprocessableEntity,
                 "Data de nascimento deve ser anterior a hoje.", "CUSTOMER_BIRTH_DATE"),

            "customers_broker_id_fkey" =>
                (StatusCodes.Status422UnprocessableEntity,
                 "Corretor informado não existe nesta corretora.", "BROKER_NOT_FOUND"),

            _ when ex.SqlState == PostgresErrorCodes.InsufficientPrivilege =>
                (StatusCodes.Status403Forbidden,
                 "Operação não permitida para este perfil.", "FORBIDDEN"),

            _ when ex.Message.Contains("row-level security", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status403Forbidden,
                 "Operação bloqueada pelo isolamento entre corretoras.", "TENANT_VIOLATION"),

            _ => (StatusCodes.Status400BadRequest,
                  "Não foi possível concluir a operação.", "OPERATION_FAILED")
        };

        return Results.Json(new { message, code }, statusCode: status);
    }

    private static string? OnlyDigits(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is 10 or 11 ? digits : null;
    }
}
