using Npgsql;

namespace PortalDoCorretor.Workers;

public interface IDbConnectionFactory
{
    Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Conexão dos workers, usando o papel <c>app_worker</c>.
/// </summary>
/// <remarks>
/// Papel distinto do <c>app_user</c> por decisão: o dispatcher precisa atravessar tenants
/// para processar a fila inteira, e essa permissão não deve existir no papel que serve
/// requisições de usuário. A senha vem do ambiente, nunca do arquivo versionado.
/// </remarks>
public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(IConfiguration configuration)
    {
        var baseConnection = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default não configurada.");

        var password = configuration["POSTGRES_APP_WORKER_PASSWORD"]
            ?? Environment.GetEnvironmentVariable("POSTGRES_APP_WORKER_PASSWORD");

        var builder = new NpgsqlConnectionStringBuilder(baseConnection);
        if (!string.IsNullOrEmpty(password)) builder.Password = password;

        if (string.IsNullOrEmpty(builder.Password))
            throw new InvalidOperationException(
                "Senha do banco ausente. Defina POSTGRES_APP_WORKER_PASSWORD no ambiente.");

        _connectionString = builder.ConnectionString;
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // O papel app_worker tem política de RLS própria (p_outbox_worker) para
        // atravessar tenants; o perfil é declarado para que a política seja avaliada.
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT set_config('app.user_profile', 'BROKER', false), " +
            "       set_config('app.actor_id', @actor, false)";
        command.Parameters.AddWithValue("actor", SystemAccount.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    /// <summary>Conta técnica dos processos automatizados.</summary>
    public static readonly Guid SystemAccount = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
