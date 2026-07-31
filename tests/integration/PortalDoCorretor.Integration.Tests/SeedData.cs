using Npgsql;

namespace PortalDoCorretor.Integration.Tests;

/// <summary>Massa mínima e determinística para os testes de integração.</summary>
public static class SeedData
{
    public static readonly Guid TenantAlfa = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid TenantBeta = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static readonly Guid BrokerAna = Guid.Parse("b0000000-0000-0000-0000-00000000000a");
    public static readonly Guid BrokerCarla = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    public static readonly Guid UserAna = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    public static readonly Guid UserCarla = Guid.Parse("a0000000-0000-0000-0000-00000000000b");

    public static readonly Guid CustomerAlfa = Guid.Parse("c0000000-0000-0000-0000-00000000000a");
    public static readonly Guid CustomerBeta = Guid.Parse("c0000000-0000-0000-0000-00000000000b");

    public static readonly Guid AssetAlfa = Guid.Parse("d0000000-0000-0000-0000-00000000000a");
    public static readonly Guid ProductVersion = Guid.Parse("f0000000-0000-0000-0000-00000000000f");
    public static readonly Guid QuotationAlfa = Guid.Parse("a1000000-0000-0000-0000-00000000000a");
    public static readonly Guid ProposalAlfa = Guid.Parse("b1000000-0000-0000-0000-00000000000a");

    private const string SystemUser = "00000000-0000-0000-0000-000000000001";

    /// <summary>Insere a massa como migrator, definindo o contexto exigido pelo FORCE RLS.</summary>
    public static async Task EnsureAsync(DatabaseFixture fixture)
    {
        await using var connection = await fixture.OpenAsMigratorAsync();

        await ExecAsync(connection, $"""
            SELECT set_config('app.tenant_id', '{TenantAlfa}', false),
                   set_config('app.user_profile', 'BROKER', false),
                   set_config('app.actor_id', '{SystemUser}', false);

            INSERT INTO brokerages (id, legal_name, trade_name, document, susep_registration, created_by)
            VALUES ('{TenantAlfa}', 'Corretora Alfa LTDA', 'Alfa', '11222333000181', 'S-A-1', '{SystemUser}')
            ON CONFLICT DO NOTHING;

            INSERT INTO users (id, tenant_id, email, password_hash, profile, display_name, created_by)
            VALUES ('{UserAna}', '{TenantAlfa}', 'ana@alfa.test', '\x00', 'BROKER', 'Ana', '{SystemUser}')
            ON CONFLICT DO NOTHING;

            INSERT INTO brokers (id, tenant_id, user_id, susep_registration, full_name, hired_at, created_by)
            VALUES ('{BrokerAna}', '{TenantAlfa}', '{UserAna}', 'S-CA-1', 'Ana Souza', '2024-01-01', '{SystemUser}')
            ON CONFLICT DO NOTHING;

            INSERT INTO customers (id, tenant_id, broker_id, kind, document_encrypted, document_hash,
                                   first_name, last_name, birth_date, created_by)
            VALUES ('{CustomerAlfa}', '{TenantAlfa}', '{BrokerAna}', 'INDIVIDUAL', '\x01', '\xAA',
                    'Cliente', 'Alfa', '1990-01-01', '{SystemUser}')
            ON CONFLICT DO NOTHING;

            INSERT INTO insurable_assets (id, tenant_id, customer_id, kind, declared_value, created_by)
            VALUES ('{AssetAlfa}', '{TenantAlfa}', '{CustomerAlfa}', 'VEHICLE',
                    ROW(80000.00,'BRL')::money_amount, '{SystemUser}')
            ON CONFLICT DO NOTHING;

            INSERT INTO vehicles (id, plate, chassis, model_year, manufacture_year, brand, model,
                                  usage, overnight_postal_code)
            VALUES ('{AssetAlfa}', 'ABC1D23', '9BWZZZ377VT004251', 2022, 2021, 'Marca', 'Modelo',
                    'PERSONAL', '01310100')
            ON CONFLICT DO NOTHING;

            INSERT INTO insurance_products (id, code, name, branch)
            VALUES ('e0000000-0000-0000-0000-00000000000e', 'AUTO-STD', 'Auto Padrão', 'AUTO')
            ON CONFLICT DO NOTHING;

            INSERT INTO product_versions (id, product_id, version, branch, base_rate, risk_sensitivity,
                   max_acceptable_risk, min_insured_value, max_insured_value, coverage_cap,
                   questionnaire_schema, published_at, valid_period)
            VALUES ('{ProductVersion}', 'e0000000-0000-0000-0000-00000000000e', 1, 'AUTO', 0.05, 0.3,
                    800, 1000, 900000, ROW(900000.00,'BRL')::money_amount, jsonb_build_object(), now(),
                    daterange('2026-01-01','2027-01-01'))
            ON CONFLICT DO NOTHING;

            INSERT INTO quotations (id, tenant_id, broker_id, customer_id, asset_id, product_version_id,
                   number, status, risk_score, created_by, expires_at)
            VALUES ('{QuotationAlfa}', '{TenantAlfa}', '{BrokerAna}', '{CustomerAlfa}', '{AssetAlfa}',
                    '{ProductVersion}', 'CT-2026-00000001-1', 'CALCULATED', 300, '{SystemUser}',
                    now() + interval '30 days')
            ON CONFLICT DO NOTHING;

            INSERT INTO proposals (id, tenant_id, quotation_id, broker_id, customer_id, number, status,
                   chosen_plan, net_premium, total_premium, created_by)
            VALUES ('{ProposalAlfa}', '{TenantAlfa}', '{QuotationAlfa}', '{BrokerAna}', '{CustomerAlfa}',
                    'PR-2026-00000001-1', 'APPROVED', 'COMPLETE',
                    ROW(2000.00,'BRL')::money_amount, ROW(2400.00,'BRL')::money_amount, '{SystemUser}')
            ON CONFLICT DO NOTHING;
            """);

        await ExecAsync(connection, $"""
            SELECT set_config('app.tenant_id', '{TenantBeta}', false);

            INSERT INTO brokerages (id, legal_name, trade_name, document, susep_registration, created_by)
            VALUES ('{TenantBeta}', 'Corretora Beta LTDA', 'Beta', '11444777000161', 'S-B-1', '{SystemUser}')
            ON CONFLICT DO NOTHING;

            INSERT INTO users (id, tenant_id, email, password_hash, profile, display_name, created_by)
            VALUES ('{UserCarla}', '{TenantBeta}', 'carla@beta.test', '\x00', 'BROKER', 'Carla', '{SystemUser}')
            ON CONFLICT DO NOTHING;

            INSERT INTO brokers (id, tenant_id, user_id, susep_registration, full_name, hired_at, created_by)
            VALUES ('{BrokerCarla}', '{TenantBeta}', '{UserCarla}', 'S-CB-1', 'Carla Dias', '2024-01-01', '{SystemUser}')
            ON CONFLICT DO NOTHING;

            INSERT INTO customers (id, tenant_id, broker_id, kind, document_encrypted, document_hash,
                                   first_name, last_name, birth_date, created_by)
            VALUES ('{CustomerBeta}', '{TenantBeta}', '{BrokerCarla}', 'INDIVIDUAL', '\x01', '\xBB',
                    'Cliente', 'Beta', '1990-01-01', '{SystemUser}')
            ON CONFLICT DO NOTHING;
            """);
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }
}
