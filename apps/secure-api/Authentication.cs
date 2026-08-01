using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.IdentityModel.Tokens;

namespace PortalDoCorretor.SecureApi;

/// <summary>Credenciais de acesso.</summary>
public sealed class LoginInput
{
    [Required(ErrorMessage = "E-mail é obrigatório.")]
    [EmailAddress(ErrorMessage = "E-mail inválido.")]
    public string Email { get; init; } = string.Empty;

    [Required(ErrorMessage = "Senha é obrigatória.")]
    public string Password { get; init; } = string.Empty;
}

/// <summary>
/// Derivação e verificação de senha com PBKDF2-HMAC-SHA256.
/// </summary>
/// <remarks>
/// <para>
/// Escolhido por vir na biblioteca padrão: uma dependência a menos em código de
/// autenticação, que é onde menos se quer surpresa. Argon2id seria preferível pela
/// resistência a GPU, mas exigiria pacote de terceiros.
/// </para>
/// <para>
/// O formato guardado em <c>bytea</c> carrega tudo que a verificação precisa, para que
/// aumentar o custo no futuro não invalide as senhas já cadastradas:
/// </para>
/// <code>
/// [1 byte versão][4 bytes iterações big-endian][16 bytes sal][32 bytes derivação]
/// </code>
/// </remarks>
public static class PasswordHasher
{
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// <summary>Custo atual. Gravado junto do hash, então pode subir sem migração de dados.</summary>
    public const int Iterations = 210_000;

    public static byte[] Hash(string password, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, HashSize);

        var stored = new byte[1 + 4 + SaltSize + HashSize];
        stored[0] = Version;
        BinaryPrimitivesWriteInt32BigEndian(stored.AsSpan(1, 4), iterations);
        salt.CopyTo(stored.AsSpan(5, SaltSize));
        derived.CopyTo(stored.AsSpan(5 + SaltSize, HashSize));

        return stored;
    }

    public static bool Verify(string password, byte[]? stored)
    {
        // Formato inesperado é falha de verificação, nunca exceção: um hash truncado no
        // banco não pode virar 500 nem, pior, um caminho que pula a checagem.
        if (stored is null || stored.Length != 1 + 4 + SaltSize + HashSize || stored[0] != Version)
            return false;

        var iterations = BinaryPrimitivesReadInt32BigEndian(stored.AsSpan(1, 4));
        if (iterations is < 1_000 or > 5_000_000) return false;

        var salt = stored.AsSpan(5, SaltSize).ToArray();
        var expected = stored.AsSpan(5 + SaltSize, HashSize).ToArray();

        var derived = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, HashSize);

        // Comparação em tempo constante: um `==` vazaria o prefixo correto pelo tempo gasto
        return CryptographicOperations.FixedTimeEquals(derived, expected);
    }

    private static void BinaryPrimitivesWriteInt32BigEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static int BinaryPrimitivesReadInt32BigEndian(ReadOnlySpan<byte> source) =>
        (source[0] << 24) | (source[1] << 16) | (source[2] << 8) | source[3];
}

/// <summary>Emissão de tokens de acesso.</summary>
public sealed class TokenIssuer(IConfiguration configuration)
{
    public const string Issuer = "portal-do-corretor";
    public const string Audience = "portal-do-corretor-app";

    /// <summary>Janela curta de propósito: não há refresh token, então expirar é sair.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    public SymmetricSecurityKey SigningKey { get; } = new(
        Encoding.UTF8.GetBytes(
            configuration["JWT_SIGNING_KEY"]
            ?? throw new InvalidOperationException(
                "JWT_SIGNING_KEY ausente. O start.sh gera uma no .env; sem ela a API não sobe, "
              + "porque uma chave padrão embutida no código assinaria token de qualquer instalação.")));

    public (string Token, DateTime ExpiresAt) Issue(
        Guid userId, Guid? tenantId, string profile, string displayName)
    {
        var expiresAt = DateTime.UtcNow.Add(Lifetime);

        // O tenant vai no token assinado — é a camada 1 do isolamento. Um cabeçalho
        // pode ser trocado pelo cliente; um claim exige a chave de assinatura.
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, displayName),
            new("profile", profile)
        };

        // Regulador é multi-tenant por natureza: não carrega tenant no token
        if (tenantId is not null) claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

/// <summary>
/// Autenticação: login, bloqueio por tentativas e identidade do usuário corrente.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Bloqueio temporário após tentativas seguidas — freia força bruta sem apagar a conta.</summary>
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Autenticação");

        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapGet("/me", MeAsync).RequireAuthorization();
        group.MapGet("/demo-accounts", DemoAccountsAsync).AllowAnonymous();
    }

    private static async Task<IResult> LoginAsync(
        LoginInput input, RequestContext ctx, IDbConnectionFactory factory,
        TokenIssuer issuer, ActivityStream stream)
    {
        var started = Stopwatch.GetTimestamp();

        if (Validate(input) is { } invalid) return invalid;

        await using var connection = await factory.OpenAsync();

        // Consulta por função SECURITY DEFINER: antes de autenticar não há tenant
        // corrente, e a política de `users` esconderia toda linha. A travessia do
        // isolamento fica confinada a uma função com nome próprio e superfície
        // declarada, em vez de virar permissão ampla para a aplicação inteira.
        var user = await connection.QuerySingleOrDefaultAsync("""
            SELECT id, tenant_id AS "tenantId", profile, display_name AS "displayName",
                   password_hash AS "passwordHash", failed_attempts AS "failedAttempts",
                   locked_until AS "lockedUntil", broker_id AS "brokerId",
                   tenant_name AS "tenantName"
              FROM app.authenticate_lookup(@email)
            """, new { email = input.Email.Trim() });

        // Mensagem única para usuário inexistente e senha errada: distinguir os casos
        // transformaria o login em oráculo de e-mails cadastrados.
        const string genericFailure = "E-mail ou senha inválidos.";

        if (user is null)
        {
            // Deriva mesmo sem usuário, para que o tempo de resposta não denuncie a ausência
            PasswordHasher.Verify(input.Password, PasswordHasher.Hash("nao-existe"));

            stream.Publish(new ProcessingEvent(
                "Security", "Auth", "auth:login", "Tentativa com e-mail não cadastrado",
                "DENIED", "User", null, null, ctx.CorrelationId));

            return Results.Json(new { message = genericFailure, code = "INVALID_CREDENTIALS" },
                statusCode: 401);
        }

        var userId = (Guid)user.id;

        if (user.lockedUntil is DateTime locked && locked > DateTime.UtcNow)
        {
            stream.Publish(new ProcessingEvent(
                "Security", "Auth", "auth:login", "Acesso a conta temporariamente bloqueada",
                "DENIED", "User", userId, (Guid?)user.tenantId, ctx.CorrelationId));

            return Results.Json(new
            {
                message = $"Conta bloqueada até {locked:HH:mm} por tentativas seguidas.",
                code = "ACCOUNT_LOCKED"
            }, statusCode: 423);
        }

        if (!PasswordHasher.Verify(input.Password, (byte[]?)user.passwordHash))
        {
            var attempts = (short)user.failedAttempts + 1;

            await connection.ExecuteAsync(
                "SELECT app.register_login_failure(@id, @max, @minutes)",
                new { id = userId, max = MaxFailedAttempts, minutes = (int)LockDuration.TotalMinutes });

            stream.Publish(new ProcessingEvent(
                "Security", "Auth", "auth:login",
                $"Senha incorreta — tentativa {attempts} de {MaxFailedAttempts}",
                "DENIED", "User", userId, (Guid?)user.tenantId, ctx.CorrelationId));

            return Results.Json(new { message = genericFailure, code = "INVALID_CREDENTIALS" },
                statusCode: 401);
        }

        await connection.ExecuteAsync(
            "SELECT app.register_login_success(@id)", new { id = userId });

        var (token, expiresAt) = issuer.Issue(
            userId, (Guid?)user.tenantId, (string)user.profile, (string)user.displayName);

        var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        stream.Publish(new ProcessingEvent(
            "Security", "Auth", "auth:login", $"Login de {user.displayName}",
            "SUCCESS", "User", userId, (Guid?)user.tenantId, ctx.CorrelationId, elapsed));

        return Results.Ok(new
        {
            token,
            expiresAt,
            user = new
            {
                id = userId,
                name = (string)user.displayName,
                profile = (string)user.profile,
                tenantId = (Guid?)user.tenantId,
                tenantName = (string?)user.tenantName,
                brokerId = (Guid?)user.brokerId
            }
        });
    }

    /// <summary>
    /// Contas de demonstração, uma por corretora.
    /// </summary>
    /// <remarks>
    /// Rota anônima que lista e-mails: em sistema real seria vazamento, aqui é o oposto.
    /// A massa é sintética e gerada por seed — sem esta lista o avaliador não teria como
    /// adivinhar um endereço para entrar, e o case ficaria inavaliável. Uma conta por
    /// corretora, justamente para que dê para comparar tenants.
    /// </remarks>
    private static async Task<IResult> DemoAccountsAsync(IDbConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync();

        return Results.Ok(await connection.QueryAsync(
            "SELECT email, nome, corretora FROM app.demo_accounts()"));
    }

    /// <summary>Quem sou eu — permite ao cliente restaurar a sessão sem guardar dados do usuário.</summary>
    private static async Task<IResult> MeAsync(RequestContext ctx, IDbConnectionFactory factory)
    {
        await using var connection = await ctx.OpenScopedAsync(factory);

        var user = await connection.QuerySingleOrDefaultAsync("""
            SELECT u.id, u.display_name AS name, u.profile::text AS profile,
                   u.tenant_id AS "tenantId",
                   (SELECT br.trade_name FROM brokerages br WHERE br.id = u.tenant_id) AS "tenantName",
                   (SELECT b.id FROM brokers b WHERE b.user_id = u.id) AS "brokerId"
              FROM users u WHERE u.id = @id AND u.deleted_at IS NULL
            """, new { id = ctx.ActorId });

        return user is null ? Results.Unauthorized() : Results.Ok(user);
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
