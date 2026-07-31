using System.Data;
using Npgsql;

namespace PortalDoCorretor.SecureApi;

public interface IDbConnectionFactory
{
    Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default);
}

public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// Monta a string de conexão a partir da configuração, com a senha vinda do ambiente.
    /// </summary>
    /// <remarks>
    /// O arquivo de configuração versionado não contém senha — nem de desenvolvimento.
    /// A senha vem de <c>POSTGRES_APP_USER_PASSWORD</c>, a mesma variável que o Docker
    /// Compose usa, então ambiente local e contêiner leem a mesma origem.
    /// </remarks>
    public NpgsqlConnectionFactory(IConfiguration configuration)
    {
        var baseConnection = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default não configurada.");

        var password = configuration["POSTGRES_APP_USER_PASSWORD"]
            ?? Environment.GetEnvironmentVariable("POSTGRES_APP_USER_PASSWORD");

        var builder = new NpgsqlConnectionStringBuilder(baseConnection);
        if (!string.IsNullOrEmpty(password)) builder.Password = password;

        if (string.IsNullOrEmpty(builder.Password))
            throw new InvalidOperationException(
                "Senha do banco ausente. Defina POSTGRES_APP_USER_PASSWORD no ambiente "
              + "(o mesmo valor usado no arquivo .env).");

        _connectionString = builder.ConnectionString;
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

/// <summary>
/// Contexto da requisição: tenant, ator e perfil.
/// </summary>
/// <remarks>
/// <para>
/// Nesta fatia de demonstração o tenant chega por cabeçalho <c>X-Tenant-Id</c> para permitir
/// alternar de corretora na interface sem um fluxo de autenticação completo. <b>Isso é
/// deliberadamente provisório</b> e está marcado como tal: na versão com autenticação, o tenant
/// vem exclusivamente do claim do token assinado, e o cabeçalho deixa de ser lido.
/// </para>
/// <para>
/// O que já é definitivo: o tenant é aplicado ao banco via <c>set_config(..., is_local => true)</c>,
/// então a Row-Level Security atua mesmo que a consulta esqueça o filtro. A camada 5 não depende
/// de como o tenant foi resolvido.
/// </para>
/// </remarks>
public sealed class RequestContext(IHttpContextAccessor accessor)
{
    private readonly HttpContext _http = accessor.HttpContext
        ?? throw new InvalidOperationException("Sem HttpContext.");

    public Guid CorrelationId => _http.Items["CorrelationId"] is Guid id ? id : Guid.Empty;

    public Guid? TenantId =>
        Guid.TryParse(_http.Request.Headers["X-Tenant-Id"].FirstOrDefault(), out var tenant)
        && tenant != Guid.Empty ? tenant : null;

    public string Profile =>
        _http.Request.Headers["X-Profile"].FirstOrDefault()?.ToUpperInvariant() is "REGULATOR"
            ? "REGULATOR" : "BROKER";

    /// <summary>
    /// Ator da requisição — usado em <c>created_by</c>, <c>deleted_by</c> e na auditoria.
    /// </summary>
    /// <remarks>
    /// Provisório junto com o tenant: enquanto não há autenticação, o ator chega por
    /// cabeçalho <c>X-Actor-Id</c>, com uma conta técnica como padrão. Passa a vir do claim
    /// do token quando o login existir.
    /// </remarks>
    public Guid ActorId =>
        Guid.TryParse(_http.Request.Headers["X-Actor-Id"].FirstOrDefault(), out var actor)
        && actor != Guid.Empty
            ? actor
            : SystemAccount;

    /// <summary>Conta técnica usada quando não há ator humano identificado.</summary>
    public static readonly Guid SystemAccount = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// Abre a conexão já com o contexto de tenant aplicado — a Row-Level Security passa a
    /// filtrar a partir daqui.
    /// </summary>
    public async Task<NpgsqlConnection> OpenScopedAsync(IDbConnectionFactory factory,
                                                        CancellationToken cancellationToken = default)
    {
        var connection = await factory.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // set_config com is_local = true equivale a SET LOCAL e ACEITA parâmetro. SET LOCAL
        // exigiria interpolar a string — reabrindo injeção de SQL justamente na função que
        // existe para fechar o isolamento.
        command.CommandText = """
            SELECT set_config('app.tenant_id',      @tenant,      false),
                   set_config('app.user_profile',   @profile,     false),
                   set_config('app.actor_id',       @actor,       false),
                   set_config('app.correlation_id', @correlation, false)
            """;
        command.Parameters.AddWithValue("tenant", TenantId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("profile", Profile);
        command.Parameters.AddWithValue("actor", ActorId.ToString());
        command.Parameters.AddWithValue("correlation", CorrelationId.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
