using Npgsql;
using Testcontainers.PostgreSql;

namespace PortalDoCorretor.Integration.Tests;

/// <summary>
/// Sobe um PostgreSQL 16 real em contêiner e aplica as migrations do repositório.
/// </summary>
/// <remarks>
/// <para>
/// Banco em memória é proibido nesta suíte por decisão. RLS, constraints de exclusão,
/// tipos compostos, índices parciais e <c>xmin</c> simplesmente não existem em SQLite —
/// testar contra ele daria confiança falsa exatamente nos pontos que este projeto afirma
/// garantir.
/// </para>
/// <para>
/// As migrations aplicadas são os <b>mesmos arquivos</b> versionados em
/// <c>database/secure/migrations</c>. Não há esquema duplicado para teste: se uma migration
/// quebrar, a suíte quebra junto.
/// </para>
/// </remarks>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string Database = "portal_do_corretor";
    private const string MigratorUser = "pdc_migrator";
    private const string MigratorPassword = "test_migrator_pwd";
    private const string AppUser = "app_user";
    private const string AppPassword = "test_app_pwd";

    private PostgreSqlContainer _container = null!;

    /// <summary>Conexão do papel de migração — tem DDL, usado apenas para preparar a base.</summary>
    public string MigratorConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Conexão do papel real da aplicação: sem DDL, sem DELETE, sem BYPASSRLS.
    /// Os testes de isolamento precisam usar este, senão validariam nada.
    /// </summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16.4-alpine")
            .WithDatabase(Database)
            .WithUsername(MigratorUser)
            .WithPassword(MigratorPassword)
            // Mesma instrumentação do compose: o plano de execução observado no teste
            // é o mesmo que o Query Inspector exibiria
            .WithCommand("-c", "shared_preload_libraries=pg_stat_statements",
                         "-c", "pg_stat_statements.track=all",
                         "-c", "log_lock_waits=on",
                         "-c", "deadlock_timeout=1s")
            .Build();

        await _container.StartAsync();

        MigratorConnectionString = _container.GetConnectionString();

        var builder = new NpgsqlConnectionStringBuilder(MigratorConnectionString)
        {
            Username = AppUser,
            Password = AppPassword
        };
        AppConnectionString = builder.ConnectionString;

        await BootstrapAsync();
    }

    private async Task BootstrapAsync()
    {
        var root = FindRepositoryRoot();

        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync();

        // Os papéis precisam existir ANTES do init: o script contém GRANTs que os referenciam.
        // Em produção quem os cria é o próprio script, via \gexec com senha do ambiente; aqui
        // as senhas são fixas e conhecidas, então a criação é feita antes e o \gexec é removido.
        await ExecuteAsync(connection, $"""
            DO $$ BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppUser}') THEN
                    CREATE ROLE {AppUser} LOGIN PASSWORD '{AppPassword}';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_regulator') THEN
                    CREATE ROLE app_regulator LOGIN PASSWORD '{AppPassword}';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_worker') THEN
                    CREATE ROLE app_worker LOGIN PASSWORD '{AppPassword}';
                END IF;
            END $$;
            """);

        // Init: extensões, schemas e funções de contexto — os mesmos do ambiente Docker
        var init = await File.ReadAllTextAsync(
            Path.Combine(root, "database", "secure", "scripts", "00-init-roles.sql"));

        await ExecuteAsync(connection, StripPsqlMetaCommands(init));

        await ExecuteAsync(connection, $"""
            GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA app TO {AppUser}, app_regulator, app_worker;
            """);

        // Migrations, na ordem de versão
        var migrations = Directory
            .GetFiles(Path.Combine(root, "database", "secure", "migrations"), "V*.sql")
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var file in migrations)
            await ExecuteAsync(connection, await File.ReadAllTextAsync(file));
    }

    /// <summary>Remove metacomandos do psql, que só existem no cliente interativo.</summary>
    private static string StripPsqlMetaCommands(string sql)
    {
        var kept = new List<string>();
        var skipUntilSemicolon = false;

        foreach (var line in sql.Split('\n'))
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith(@"\getenv", StringComparison.Ordinal)
                || trimmed.StartsWith(@"\gexec", StringComparison.Ordinal))
            {
                skipUntilSemicolon = false;
                continue;
            }

            // O SELECT format(...) que precede \gexec depende de variáveis do psql
            if (trimmed.StartsWith("SELECT format('CREATE ROLE", StringComparison.Ordinal))
            {
                skipUntilSemicolon = true;
                continue;
            }

            if (skipUntilSemicolon)
            {
                if (trimmed.Contains("\\gexec", StringComparison.Ordinal)) skipUntilSemicolon = false;
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = 180;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Localiza a raiz do repositório subindo a partir do diretório de saída do teste.</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PortalDoCorretor.sln")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }

    /// <summary>Conexão da aplicação com o contexto de tenant já definido na transação.</summary>
    public async Task<NpgsqlConnection> OpenAsAppUserAsync(Guid? tenantId = null,
                                                           string profile = "BROKER",
                                                           Guid? actorId = null)
    {
        var connection = new NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT set_config('app.tenant_id',    @tenant,  false),
                   set_config('app.user_profile', @profile, false),
                   set_config('app.actor_id',     @actor,   false)
            """;
        command.Parameters.AddWithValue("tenant", tenantId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("profile", profile);
        command.Parameters.AddWithValue("actor", actorId?.ToString() ?? Guid.Empty.ToString());
        await command.ExecuteNonQueryAsync();

        return connection;
    }

    public async Task<NpgsqlConnection> OpenAsMigratorAsync()
    {
        var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(nameof(DatabaseCollection))]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
