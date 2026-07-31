-- =============================================================================
-- Verificação executável do isolamento multi-tenant (camada 5: RLS)
--
-- Conecta como app_user — o papel real da aplicação, sem BYPASSRLS — e prova que
-- o corretor de um tenant não alcança dados de outro, nem por ID direto.
-- =============================================================================

\pset tuples_only on
\set ON_ERROR_STOP off

\echo ''
\echo '=========== PREPARO: dois tenants com um cliente cada ==========='

-- Executado como migrator (dono), que também está sob FORCE ROW LEVEL SECURITY.
-- Por isso o contexto precisa ser definido inclusive aqui.
SELECT set_config('app.tenant_id', '11111111-1111-1111-1111-111111111111', false);
SELECT set_config('app.user_profile', 'BROKER', false);

INSERT INTO brokerages (id, legal_name, trade_name, document, susep_registration, created_by)
VALUES ('11111111-1111-1111-1111-111111111111','Alfa','Alfa','11222333000181','S-A','00000000-0000-0000-0000-000000000001')
ON CONFLICT DO NOTHING;

INSERT INTO users (id,tenant_id,email,password_hash,profile,display_name,created_by)
VALUES ('a0000000-0000-0000-0000-000000000001','11111111-1111-1111-1111-111111111111','ana@alfa.test','\x00','BROKER','Ana','00000000-0000-0000-0000-000000000001')
ON CONFLICT DO NOTHING;

INSERT INTO brokers (id,tenant_id,user_id,susep_registration,full_name,hired_at,created_by)
VALUES ('b0000000-0000-0000-0000-000000000001','11111111-1111-1111-1111-111111111111','a0000000-0000-0000-0000-000000000001','S-CA','Ana','2024-01-01','00000000-0000-0000-0000-000000000001')
ON CONFLICT DO NOTHING;

INSERT INTO customers (id,tenant_id,broker_id,kind,document_encrypted,document_hash,first_name,last_name,birth_date,created_by)
VALUES ('c0000000-0000-0000-0000-00000000000A','11111111-1111-1111-1111-111111111111','b0000000-0000-0000-0000-000000000001','INDIVIDUAL','\x01','\xAA','Cliente','Alfa','1990-01-01','00000000-0000-0000-0000-000000000001')
ON CONFLICT DO NOTHING;

SELECT set_config('app.tenant_id', '22222222-2222-2222-2222-222222222222', false);

INSERT INTO brokerages (id, legal_name, trade_name, document, susep_registration, created_by)
VALUES ('22222222-2222-2222-2222-222222222222','Beta','Beta','11444777000161','S-B','00000000-0000-0000-0000-000000000001')
ON CONFLICT DO NOTHING;

INSERT INTO users (id,tenant_id,email,password_hash,profile,display_name,created_by)
VALUES ('a0000000-0000-0000-0000-000000000002','22222222-2222-2222-2222-222222222222','carla@beta.test','\x00','BROKER','Carla','00000000-0000-0000-0000-000000000001')
ON CONFLICT DO NOTHING;

INSERT INTO brokers (id,tenant_id,user_id,susep_registration,full_name,hired_at,created_by)
VALUES ('b0000000-0000-0000-0000-000000000002','22222222-2222-2222-2222-222222222222','a0000000-0000-0000-0000-000000000002','S-CB','Carla','2024-01-01','00000000-0000-0000-0000-000000000001')
ON CONFLICT DO NOTHING;

INSERT INTO customers (id,tenant_id,broker_id,kind,document_encrypted,document_hash,first_name,last_name,birth_date,created_by)
VALUES ('c0000000-0000-0000-0000-00000000000B','22222222-2222-2222-2222-222222222222','b0000000-0000-0000-0000-000000000002','INDIVIDUAL','\x01','\xBB','Cliente','Beta','1990-01-01','00000000-0000-0000-0000-000000000001')
ON CONFLICT DO NOTHING;

GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO app_user;
REVOKE UPDATE, DELETE ON audit_events, security_events, consents,
       calculation_snapshots, underwriting_decisions FROM app_user;

\echo 'preparo concluído'
\echo ''
\echo '=========== TESTES COMO app_user (papel real da aplicação) ==========='
