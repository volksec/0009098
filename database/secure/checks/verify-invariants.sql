-- =============================================================================
-- Verificação executável das invariantes do modelo.
--
-- Cada bloco TENTA violar uma invariante e espera ser bloqueado pelo banco.
-- Se algum bloco imprimir FALHA, a invariante correspondente não está protegida.
-- =============================================================================

\set ON_ERROR_STOP off
\pset tuples_only on

CREATE OR REPLACE FUNCTION pg_temp.expect_block(p_label text, p_sql text)
RETURNS text LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE p_sql;
    RETURN format('FALHA  %s — a operação foi ACEITA e deveria ter sido bloqueada', p_label);
EXCEPTION WHEN others THEN
    RETURN format('OK     %s — bloqueado (%s)', p_label, left(SQLERRM, 70));
END $$;

-- ---------------------------------------------------------------- massa mínima
BEGIN;

INSERT INTO brokerages (id, legal_name, trade_name, document, susep_registration, created_by)
VALUES ('11111111-1111-1111-1111-111111111111', 'Corretora Alfa LTDA', 'Alfa',
        '11222333000181', 'SUSEP-A-001', '00000000-0000-0000-0000-000000000001'),
       ('22222222-2222-2222-2222-222222222222', 'Corretora Beta LTDA', 'Beta',
        '11444777000161', 'SUSEP-B-002', '00000000-0000-0000-0000-000000000001');

INSERT INTO users (id, tenant_id, email, password_hash, profile, display_name, created_by)
VALUES ('aaaaaaaa-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111',
        'ana@alfa.test', '\x00'::bytea, 'BROKER', 'Ana', '00000000-0000-0000-0000-000000000001');

INSERT INTO brokers (id, tenant_id, user_id, susep_registration, full_name, hired_at, created_by)
VALUES ('bbbbbbbb-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111',
        'aaaaaaaa-0000-0000-0000-000000000001', 'SUSEP-C-001', 'Ana Souza', '2024-01-10',
        '00000000-0000-0000-0000-000000000001');

INSERT INTO customers (id, tenant_id, broker_id, kind, document_encrypted, document_hash,
                       first_name, last_name, birth_date, created_by)
VALUES ('cccccccc-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111',
        'bbbbbbbb-0000-0000-0000-000000000001', 'INDIVIDUAL', '\x01'::bytea, '\x01'::bytea,
        'Ana', 'Souza', '1990-05-20', '00000000-0000-0000-0000-000000000001');

INSERT INTO insurable_assets (id, tenant_id, customer_id, kind, declared_value, created_by)
VALUES ('dddddddd-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111',
        'cccccccc-0000-0000-0000-000000000001', 'VEHICLE', ROW(80000.00,'BRL')::money_amount,
        '00000000-0000-0000-0000-000000000001');

INSERT INTO vehicles (id, plate, chassis, model_year, manufacture_year, brand, model, usage,
                      overnight_postal_code)
VALUES ('dddddddd-0000-0000-0000-000000000001', 'ABC1D23', '9BWZZZ377VT004251', 2022, 2021,
        'Marca', 'Modelo', 'PERSONAL', '01310100');

INSERT INTO insurance_products (id, code, name, branch)
VALUES ('eeeeeeee-0000-0000-0000-000000000001', 'AUTO-STD', 'Auto Padrão', 'AUTO');

INSERT INTO product_versions (id, product_id, version, branch, base_rate, risk_sensitivity,
       max_acceptable_risk, min_insured_value, max_insured_value, coverage_cap,
       questionnaire_schema, published_at, valid_period)
VALUES ('ffffffff-0000-0000-0000-000000000001', 'eeeeeeee-0000-0000-0000-000000000001', 1,
        'AUTO', 0.05, 0.3, 800, 10000, 500000, ROW(500000.00,'BRL')::money_amount,
        '{}'::jsonb, now(), daterange('2026-01-01','2027-01-01'));

INSERT INTO quotations (id, tenant_id, broker_id, customer_id, asset_id, product_version_id,
       number, status, risk_score, created_by, expires_at)
VALUES ('a1000000-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111',
        'bbbbbbbb-0000-0000-0000-000000000001', 'cccccccc-0000-0000-0000-000000000001',
        'dddddddd-0000-0000-0000-000000000001', 'ffffffff-0000-0000-0000-000000000001',
        'CT-2026-00000001-1', 'CALCULATED', 300, '00000000-0000-0000-0000-000000000001',
        now() + interval '30 days');

INSERT INTO proposals (id, tenant_id, quotation_id, broker_id, customer_id, number, status,
       chosen_plan, net_premium, total_premium, created_by)
VALUES ('b1000000-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111',
        'a1000000-0000-0000-0000-000000000001', 'bbbbbbbb-0000-0000-0000-000000000001',
        'cccccccc-0000-0000-0000-000000000001', 'PR-2026-00000001-1', 'APPROVED', 'COMPLETE',
        ROW(2000.00,'BRL')::money_amount, ROW(2400.00,'BRL')::money_amount,
        '00000000-0000-0000-0000-000000000001');

INSERT INTO policies (id, tenant_id, proposal_id, broker_id, customer_id, asset_id,
       product_version_id, number, status, coverage_period, net_premium, total_premium,
       issued_by, correlation_id)
VALUES ('c1000000-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111',
        'b1000000-0000-0000-0000-000000000001', 'bbbbbbbb-0000-0000-0000-000000000001',
        'cccccccc-0000-0000-0000-000000000001', 'dddddddd-0000-0000-0000-000000000001',
        'ffffffff-0000-0000-0000-000000000001', 'PC-2026-00000001-3', 'ACTIVE',
        daterange('2026-02-01','2027-02-01'), ROW(2000.00,'BRL')::money_amount,
        ROW(2400.00,'BRL')::money_amount, '00000000-0000-0000-0000-000000000001',
        gen_random_uuid());

\echo ''
\echo '=========== INVARIANTES DE INTEGRIDADE ==========='

SELECT pg_temp.expect_block(
  'Emissão duplicada (ux_policies_proposal)', $sql$
  INSERT INTO policies (tenant_id, proposal_id, broker_id, customer_id, asset_id,
         product_version_id, number, status, coverage_period, net_premium, total_premium,
         issued_by, correlation_id)
  VALUES ('11111111-1111-1111-1111-111111111111','b1000000-0000-0000-0000-000000000001',
          'bbbbbbbb-0000-0000-0000-000000000001','cccccccc-0000-0000-0000-000000000001',
          'dddddddd-0000-0000-0000-000000000001','ffffffff-0000-0000-0000-000000000001',
          'PC-2026-00000002-6','ACTIVE',daterange('2028-02-01','2029-02-01'),
          ROW(2000.00,'BRL')::money_amount, ROW(2400.00,'BRL')::money_amount,
          '00000000-0000-0000-0000-000000000001', gen_random_uuid()) $sql$);

SELECT pg_temp.expect_block(
  'Sobreposição de vigência (ex_policies_no_overlap)', $sql$
  INSERT INTO proposals (id, tenant_id, quotation_id, broker_id, customer_id, number, status,
         chosen_plan, net_premium, total_premium, created_by)
  VALUES ('b1000000-0000-0000-0000-000000000009','11111111-1111-1111-1111-111111111111',
          'a1000000-0000-0000-0000-000000000001','bbbbbbbb-0000-0000-0000-000000000001',
          'cccccccc-0000-0000-0000-000000000001','PR-2026-00000009-9','REJECTED','COMPLETE',
          ROW(2000.00,'BRL')::money_amount, ROW(2400.00,'BRL')::money_amount,
          '00000000-0000-0000-0000-000000000001');
  INSERT INTO policies (tenant_id, proposal_id, broker_id, customer_id, asset_id,
         product_version_id, number, status, coverage_period, net_premium, total_premium,
         issued_by, correlation_id)
  VALUES ('11111111-1111-1111-1111-111111111111','b1000000-0000-0000-0000-000000000009',
          'bbbbbbbb-0000-0000-0000-000000000001','cccccccc-0000-0000-0000-000000000001',
          'dddddddd-0000-0000-0000-000000000001','ffffffff-0000-0000-0000-000000000001',
          'PC-2026-00000003-9','ACTIVE',daterange('2026-06-01','2027-06-01'),
          ROW(2000.00,'BRL')::money_amount, ROW(2400.00,'BRL')::money_amount,
          '00000000-0000-0000-0000-000000000001', gen_random_uuid()) $sql$);

SELECT pg_temp.expect_block(
  'Prêmio negativo (ck_policies_premium_positive)', $sql$
  UPDATE policies SET total_premium = ROW(-1.00,'BRL')::money_amount
   WHERE id = 'c1000000-0000-0000-0000-000000000001' $sql$);

SELECT pg_temp.expect_block(
  'TPH incoerente: PF com razão social', $sql$
  UPDATE customers SET legal_name = 'Empresa X'
   WHERE id = 'cccccccc-0000-0000-0000-000000000001' $sql$);

SELECT pg_temp.expect_block(
  'TPT quebrada: veículo apontando para asset PROPERTY', $sql$
  INSERT INTO insurable_assets (id, tenant_id, customer_id, kind, declared_value, created_by)
  VALUES ('dddddddd-0000-0000-0000-0000000000AA','11111111-1111-1111-1111-111111111111',
          'cccccccc-0000-0000-0000-000000000001','PROPERTY',ROW(1000.00,'BRL')::money_amount,
          '00000000-0000-0000-0000-000000000001');
  INSERT INTO vehicles (id, plate, chassis, model_year, manufacture_year, brand, model, usage,
                        overnight_postal_code)
  VALUES ('dddddddd-0000-0000-0000-0000000000AA','XYZ9K88','9BWZZZ377VT004999',2022,2021,
          'M','M','PERSONAL','01310100') $sql$);

SELECT pg_temp.expect_block(
  'Regulador com tenant (ck_users_tenant_by_profile)', $sql$
  INSERT INTO users (tenant_id, email, password_hash, profile, display_name, mfa_enabled,
                     totp_secret, created_by)
  VALUES ('11111111-1111-1111-1111-111111111111','reg@x.test','\x00'::bytea,'REGULATOR',
          'Reg', true, '\x01'::bytea, '00000000-0000-0000-0000-000000000001') $sql$);

SELECT pg_temp.expect_block(
  'Regulador sem MFA (ck_users_regulator_requires_mfa)', $sql$
  INSERT INTO users (email, password_hash, profile, display_name, created_by)
  VALUES ('reg2@x.test','\x00'::bytea,'REGULATOR','Reg2',
          '00000000-0000-0000-0000-000000000001') $sql$);

SELECT pg_temp.expect_block(
  'Placa duplicada (ux_vehicles_plate)', $sql$
  INSERT INTO insurable_assets (id, tenant_id, customer_id, kind, declared_value, created_by)
  VALUES ('dddddddd-0000-0000-0000-0000000000BB','11111111-1111-1111-1111-111111111111',
          'cccccccc-0000-0000-0000-000000000001','VEHICLE',ROW(1000.00,'BRL')::money_amount,
          '00000000-0000-0000-0000-000000000001');
  INSERT INTO vehicles (id, plate, chassis, model_year, manufacture_year, brand, model, usage,
                        overnight_postal_code)
  VALUES ('dddddddd-0000-0000-0000-0000000000BB','ABC1D23','9BWZZZ377VT004777',2022,2021,
          'M','M','PERSONAL','01310100') $sql$);

SELECT pg_temp.expect_block(
  'Auditoria mutável (tg_audit_immutable)', $sql$
  INSERT INTO audit_events (tenant_id, correlation_id, actor_id, actor_profile, action,
                            resource_type, outcome)
  VALUES ('11111111-1111-1111-1111-111111111111', gen_random_uuid(),
          'aaaaaaaa-0000-0000-0000-000000000001','BROKER','TEST','Policy','SUCCESS');
  UPDATE audit_events SET action = 'ADULTERADO' $sql$);

SELECT pg_temp.expect_block(
  'Consentimento mutável (tg_consents_immutable)', $sql$
  INSERT INTO consents (tenant_id, customer_id, purpose, basis, terms_version, channel,
                        recorded_by)
  VALUES ('11111111-1111-1111-1111-111111111111','cccccccc-0000-0000-0000-000000000001',
          'REGULATORY_SUPERVISION','CONSENT','v1','WEB',
          '00000000-0000-0000-0000-000000000001');
  UPDATE consents SET terms_version = 'v2' $sql$);

SELECT pg_temp.expect_block(
  'Sinistro fora da vigência (tg_claims_within_coverage)', $sql$
  INSERT INTO claims (tenant_id, policy_id, broker_id, number, occurrence_date, description,
                      correlation_id)
  VALUES ('11111111-1111-1111-1111-111111111111','c1000000-0000-0000-0000-000000000001',
          'bbbbbbbb-0000-0000-0000-000000000001','SN-2026-0001','2025-01-15','Fora da vigência',
          gen_random_uuid()) $sql$);

SELECT pg_temp.expect_block(
  'Soma das parcelas divergente (tg_installments_sum)', $sql$
  INSERT INTO installment_plans (id, tenant_id, policy_id, total_amount, installment_count)
  VALUES ('e1000000-0000-0000-0000-000000000001','11111111-1111-1111-1111-111111111111',
          'c1000000-0000-0000-0000-000000000001', ROW(2400.00,'BRL')::money_amount, 3);
  INSERT INTO installments (tenant_id, plan_id, sequence, amount, due_date)
  VALUES ('11111111-1111-1111-1111-111111111111','e1000000-0000-0000-0000-000000000001',
          1, ROW(800.00,'BRL')::money_amount, '2026-03-01'),
         ('11111111-1111-1111-1111-111111111111','e1000000-0000-0000-0000-000000000001',
          2, ROW(800.00,'BRL')::money_amount, '2026-04-01'),
         ('11111111-1111-1111-1111-111111111111','e1000000-0000-0000-0000-000000000001',
          3, ROW(700.00,'BRL')::money_amount, '2026-05-01') $sql$);

SELECT pg_temp.expect_block(
  'Taxa de comissão acima do teto (ck_commission_rate)', $sql$
  INSERT INTO commission_rules (product_id, version, rate, base_on, valid_period)
  VALUES ('eeeeeeee-0000-0000-0000-000000000001', 1, 0.50, 'NET_PREMIUM',
          daterange('2026-01-01','2027-01-01')) $sql$);

\echo ''
\echo '=========== CASO POSITIVO: soma correta deve PASSAR ==========='

SAVEPOINT sp_ok;
INSERT INTO installment_plans (id, tenant_id, policy_id, total_amount, installment_count)
VALUES ('e1000000-0000-0000-0000-0000000000FF','11111111-1111-1111-1111-111111111111',
        'c1000000-0000-0000-0000-000000000001', ROW(1000.00,'BRL')::money_amount, 3);
INSERT INTO installments (tenant_id, plan_id, sequence, amount, due_date)
VALUES ('11111111-1111-1111-1111-111111111111','e1000000-0000-0000-0000-0000000000FF',
        1, ROW(333.34,'BRL')::money_amount, '2026-03-01'),
       ('11111111-1111-1111-1111-111111111111','e1000000-0000-0000-0000-0000000000FF',
        2, ROW(333.33,'BRL')::money_amount, '2026-04-01'),
       ('11111111-1111-1111-1111-111111111111','e1000000-0000-0000-0000-0000000000FF',
        3, ROW(333.33,'BRL')::money_amount, '2026-05-01');
\echo 'OK     Money.Allocate (333.34 + 333.33 + 333.33 = 1000.00) aceito'

ROLLBACK;
