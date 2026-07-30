-- =============================================================================
-- ROLLBACK — reverte V001 a V009, na ordem inversa das dependências
--
-- Migration sem Down funcional FALHA O BUILD (RNF-054). Este script é executado
-- por teste de integração: aplica toda a cadeia, reverte, e verifica que o schema
-- volta ao estado inicial — um rollback que nunca foi exercitado não é rollback.
-- =============================================================================

-- ---------------------------------------------------------------- V009
DROP MATERIALIZED VIEW IF EXISTS regulatory.compliance_indicators;
DROP MATERIALIZED VIEW IF EXISTS regulatory.brokerage_indicators;
DROP VIEW IF EXISTS regulatory.proposal_lifecycle;
DROP VIEW IF EXISTS regulatory.policies_summary;
DROP VIEW IF EXISTS regulatory.customers_masked;
DROP TABLE IF EXISTS integrity_check_results;
DROP FUNCTION IF EXISTS app.run_integrity_checks();
DROP FUNCTION IF EXISTS app.mask_name(text);
DROP FUNCTION IF EXISTS app.mask_document(text, customer_kind);

-- ---------------------------------------------------------------- V008
DROP TRIGGER IF EXISTS tg_consents_immutable     ON consents;
DROP TRIGGER IF EXISTS tg_underwriting_immutable ON underwriting_decisions;
DROP TRIGGER IF EXISTS tg_snapshot_immutable     ON calculation_snapshots;
DROP TRIGGER IF EXISTS tg_security_immutable     ON security_events;
DROP TRIGGER IF EXISTS tg_audit_immutable        ON audit_events;
DROP FUNCTION IF EXISTS app.forbid_mutation();
DROP FUNCTION IF EXISTS app.apply_tenant_rls(text);

-- ---------------------------------------------------------------- V007
DROP TABLE IF EXISTS agent_executions;
DROP TABLE IF EXISTS agent_skills;
DROP TABLE IF EXISTS agents;
DROP TABLE IF EXISTS idempotency_keys;
DROP TABLE IF EXISTS processed_messages;
DROP TABLE IF EXISTS outbox_messages;      -- CASCADE implícito nas partições
DROP TABLE IF EXISTS security_events;
DROP TABLE IF EXISTS audit_events;
DROP FUNCTION IF EXISTS app.ensure_monthly_partition(text, date);

-- ---------------------------------------------------------------- V006
DROP TABLE IF EXISTS notifications;
DROP TABLE IF EXISTS documents;
DROP TABLE IF EXISTS claim_status_history;
DROP TABLE IF EXISTS damages;
DROP TABLE IF EXISTS claim_events;
DROP TRIGGER IF EXISTS tg_claims_within_coverage ON claims;
DROP TABLE IF EXISTS claims;
DROP FUNCTION IF EXISTS app.assert_claim_within_coverage();
DROP TABLE IF EXISTS commissions;
DROP TABLE IF EXISTS commission_rules;
DROP TABLE IF EXISTS payments;
DROP TRIGGER IF EXISTS tg_installments_sum ON installments;
DROP TABLE IF EXISTS installments;
DROP FUNCTION IF EXISTS app.assert_installments_sum();
DROP TABLE IF EXISTS installment_plans;

-- ---------------------------------------------------------------- V005
DROP TABLE IF EXISTS renewals;
DROP TABLE IF EXISTS endorsements;
DROP TABLE IF EXISTS policy_coverages;
ALTER TABLE IF EXISTS quotations DROP CONSTRAINT IF EXISTS fk_quotations_previous_policy;
DROP TABLE IF EXISTS policies;
DROP TABLE IF EXISTS proposal_status_history;
DROP TABLE IF EXISTS underwriting_decisions;
DROP TABLE IF EXISTS pendencies;
DROP TABLE IF EXISTS proposals;

-- ---------------------------------------------------------------- V004
DROP TABLE IF EXISTS calculation_snapshots;
DROP TABLE IF EXISTS selected_coverages;
DROP TABLE IF EXISTS quotation_items;
DROP TABLE IF EXISTS risk_profiles;
DROP TABLE IF EXISTS quotations;
DROP TABLE IF EXISTS eligibility_rules;
DROP TABLE IF EXISTS assistances;
DROP TABLE IF EXISTS coverages;
DROP TABLE IF EXISTS product_versions;
DROP TABLE IF EXISTS insurance_products;

-- ---------------------------------------------------------------- V003
DROP TABLE IF EXISTS properties;
DROP TABLE IF EXISTS vehicles;
DROP TABLE IF EXISTS insurable_assets;
DROP TABLE IF EXISTS consents;
DROP TABLE IF EXISTS addresses;
DROP TABLE IF EXISTS contacts;
DROP TABLE IF EXISTS customers;

-- ---------------------------------------------------------------- V002
DROP FUNCTION IF EXISTS app.regulatory_scope_current();
DROP TABLE IF EXISTS regulatory_access_sessions;
DROP TABLE IF EXISTS susep_regulatory_users;
DROP TABLE IF EXISTS brokers;
DROP TABLE IF EXISTS authentication_attempts;
DROP TABLE IF EXISTS user_roles;
DROP TABLE IF EXISTS role_permissions;
DROP TABLE IF EXISTS permissions;
DROP TABLE IF EXISTS roles;
DROP TABLE IF EXISTS sessions;
DROP TABLE IF EXISTS users;
DROP TABLE IF EXISTS brokerages;

-- ---------------------------------------------------------------- V001
DROP SEQUENCE IF EXISTS app.claim_number_seq;
DROP SEQUENCE IF EXISTS app.quotation_number_seq;
DROP SEQUENCE IF EXISTS app.proposal_number_seq;
DROP SEQUENCE IF EXISTS app.policy_number_seq;
DROP FUNCTION IF EXISTS app.risk_band_of(smallint);
DROP FUNCTION IF EXISTS app.decrypt_document(bytea);
DROP FUNCTION IF EXISTS app.encrypt_document(text);
DROP FUNCTION IF EXISTS app.encryption_key();

DROP TYPE IF EXISTS risk_band;
DROP TYPE IF EXISTS legal_basis;
DROP TYPE IF EXISTS access_purpose;
DROP TYPE IF EXISTS user_profile;
DROP TYPE IF EXISTS claim_status;
DROP TYPE IF EXISTS commission_base;
DROP TYPE IF EXISTS commission_status;
DROP TYPE IF EXISTS installment_status;
DROP TYPE IF EXISTS policy_status;
DROP TYPE IF EXISTS proposal_status;
DROP TYPE IF EXISTS quotation_status;
DROP TYPE IF EXISTS plan_tier;
DROP TYPE IF EXISTS insurance_branch;
DROP TYPE IF EXISTS asset_kind;
DROP TYPE IF EXISTS customer_status;
DROP TYPE IF EXISTS customer_kind;

DROP TYPE IF EXISTS deductible;
DROP TYPE IF EXISTS postal_address;
DROP TYPE IF EXISTS money_amount;

DROP DOMAIN IF EXISTS currency_code;
DROP DOMAIN IF EXISTS postal_code;
DROP DOMAIN IF EXISTS uf_code;
DROP DOMAIN IF EXISTS cnpj_digits;
DROP DOMAIN IF EXISTS cpf_digits;
