-- =============================================================================
-- V008 — Row-Level Security, imutabilidade e privilégios
--
-- Camada 5 da defesa em profundidade (ADR-0004). É a última linha: mesmo um SQL
-- cru e mal escrito só enxerga o tenant corrente.
-- =============================================================================

-- ---------------------------------------------------------------- IMUTABILIDADE
-- Auditoria e snapshots de cálculo são append-only. A proteção é dupla:
-- (a) REVOKE do privilégio, (b) trigger — para o caso de um GRANT ser concedido por engano.

CREATE OR REPLACE FUNCTION app.forbid_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Tabela % é append-only: % não é permitido.', TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'insufficient_privilege',
              HINT = 'Registros imutáveis por exigência de rastreabilidade regulatória.';
END $$;

CREATE TRIGGER tg_audit_immutable
    BEFORE UPDATE OR DELETE ON audit_events
    FOR EACH STATEMENT EXECUTE FUNCTION app.forbid_mutation();

CREATE TRIGGER tg_security_immutable
    BEFORE UPDATE OR DELETE ON security_events
    FOR EACH STATEMENT EXECUTE FUNCTION app.forbid_mutation();

CREATE TRIGGER tg_snapshot_immutable
    BEFORE UPDATE OR DELETE ON calculation_snapshots
    FOR EACH STATEMENT EXECUTE FUNCTION app.forbid_mutation();

CREATE TRIGGER tg_underwriting_immutable
    BEFORE UPDATE OR DELETE ON underwriting_decisions
    FOR EACH STATEMENT EXECUTE FUNCTION app.forbid_mutation();

-- Consentimento é append-only: revogar cria nova linha, nunca altera a anterior
CREATE TRIGGER tg_consents_immutable
    BEFORE UPDATE OR DELETE ON consents
    FOR EACH STATEMENT EXECUTE FUNCTION app.forbid_mutation();

-- =============================================================================
-- ROW-LEVEL SECURITY
-- =============================================================================

-- Aplica RLS de tenant a uma tabela. FORCE é essencial: sem ele, o usuário DONO da
-- tabela ignora as políticas — é o detalhe que transforma "temos RLS" em falsa
-- sensação de segurança.
CREATE OR REPLACE FUNCTION app.apply_tenant_rls(p_table text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', p_table);
    EXECUTE format('ALTER TABLE %I FORCE  ROW LEVEL SECURITY', p_table);

    -- Corretor: acesso total dentro do próprio tenant.
    -- app.current_tenant() retorna NULL quando não há SET LOCAL, e NULL = qualquer coisa
    -- é NULL (nunca true), então ausência de contexto FALHA FECHADO.
    EXECUTE format($f$
        CREATE POLICY p_%1$s_tenant_isolation ON %1$I
            FOR ALL TO app_user, app_worker
            USING      (tenant_id = app.current_tenant())
            WITH CHECK  (tenant_id = app.current_tenant())
    $f$, p_table);

    -- Regulador: SOMENTE LEITURA, restrita ao escopo da sessão de acesso ativa.
    -- Note FOR SELECT: não existe política de escrita para app_regulator em tabela alguma.
    EXECUTE format($f$
        CREATE POLICY p_%1$s_regulatory_read ON %1$I
            FOR SELECT TO app_regulator
            USING (
                app.current_profile() = 'REGULATOR'
                AND tenant_id IN (SELECT tenant_id FROM app.regulatory_scope_current())
            )
    $f$, p_table);
END $$;

DO $$
DECLARE
    v_table text;
BEGIN
    FOREACH v_table IN ARRAY ARRAY[
        'customers', 'contacts', 'addresses', 'consents', 'insurable_assets',
        'quotations', 'proposals', 'pendencies', 'underwriting_decisions',
        'proposal_status_history', 'policies', 'policy_coverages', 'endorsements',
        'renewals', 'installment_plans', 'installments', 'payments', 'commissions',
        'claims', 'claim_events', 'damages', 'claim_status_history',
        'documents', 'notifications',
        -- Execuções de agente carregam tenant_id e a entrada/saída do usuário. Ficaram de
        -- fora da primeira versão desta lista, e um teste de integração que varre o catálogo
        -- procurando tabelas com tenant_id sem RLS encontrou a omissão. É o motivo de esse
        -- teste existir: a lista é escrita à mão e uma tabela nova entra sem ela facilmente.
        'agent_executions',
        -- Chaves de idempotência guardam o corpo da resposta original por tenant. Sem RLS,
        -- um tenant leria a resposta de uma emissão de outro. Também encontrada pelo teste
        -- de varredura do catálogo.
        'idempotency_keys'
    ] LOOP
        PERFORM app.apply_tenant_rls(v_table);
    END LOOP;
END $$;

-- ---------------------------------------------------------------- COMISSÃO: ABAC
-- Um corretor não vê a comissão de OUTRO corretor, mesmo dentro do próprio tenant.
-- A política de tenant sozinha não cobre isso — é uma segunda dimensão de autorização.
CREATE POLICY p_commissions_own_broker ON commissions
    AS RESTRICTIVE
    FOR ALL TO app_user
    USING (
        broker_id IN (
            SELECT b.id FROM brokers b WHERE b.user_id = app.current_actor()
        )
    );

COMMENT ON POLICY p_commissions_own_broker ON commissions IS
    'RESTRICTIVE: combina com AND à política de tenant, em vez de OR. Sem AS RESTRICTIVE, '
    'as políticas somariam permissões e um corretor veria a comissão do colega.';

-- ---------------------------------------------------------------- TABELAS SEM tenant_id
-- Auditoria e Outbox têm tenant_id anulável (consulta regulatória é multi-tenant),
-- então recebem políticas próprias.

ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_events FORCE  ROW LEVEL SECURITY;

CREATE POLICY p_audit_tenant ON audit_events
    FOR ALL TO app_user, app_worker
    USING (tenant_id = app.current_tenant() OR tenant_id IS NULL)
    WITH CHECK (tenant_id = app.current_tenant() OR tenant_id IS NULL);

CREATE POLICY p_audit_regulatory ON audit_events
    FOR SELECT TO app_regulator
    USING (
        app.current_profile() = 'REGULATOR'
        AND (tenant_id IS NULL
             OR tenant_id IN (SELECT tenant_id FROM app.regulatory_scope_current()))
    );

ALTER TABLE security_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE security_events FORCE  ROW LEVEL SECURITY;

CREATE POLICY p_security_tenant ON security_events
    FOR ALL TO app_user, app_worker
    USING (tenant_id = app.current_tenant() OR tenant_id IS NULL)
    WITH CHECK (true);   -- registrar evento de segurança nunca pode falhar por RLS

CREATE POLICY p_security_regulatory ON security_events
    FOR SELECT TO app_regulator
    USING (app.current_profile() = 'REGULATOR');

ALTER TABLE outbox_messages ENABLE ROW LEVEL SECURITY;
ALTER TABLE outbox_messages FORCE  ROW LEVEL SECURITY;

CREATE POLICY p_outbox_tenant ON outbox_messages
    FOR ALL TO app_user
    USING (tenant_id = app.current_tenant())
    WITH CHECK (tenant_id = app.current_tenant());

-- O dispatcher processa TODOS os tenants — é conta técnica, com escopo próprio
CREATE POLICY p_outbox_worker ON outbox_messages
    FOR ALL TO app_worker USING (true) WITH CHECK (true);

-- ---------------------------------------------------------------- USERS
ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE users FORCE  ROW LEVEL SECURITY;

CREATE POLICY p_users_tenant ON users
    FOR ALL TO app_user
    USING (tenant_id = app.current_tenant() OR id = app.current_actor())
    WITH CHECK (tenant_id = app.current_tenant());

ALTER TABLE brokers ENABLE ROW LEVEL SECURITY;
ALTER TABLE brokers FORCE  ROW LEVEL SECURITY;

CREATE POLICY p_brokers_tenant ON brokers
    FOR ALL TO app_user
    USING (tenant_id = app.current_tenant())
    WITH CHECK (tenant_id = app.current_tenant());

CREATE POLICY p_brokers_regulatory ON brokers
    FOR SELECT TO app_regulator
    USING (tenant_id IN (SELECT tenant_id FROM app.regulatory_scope_current()));

-- =============================================================================
-- PRIVILÉGIOS — menor privilégio, concedido explicitamente
-- =============================================================================

REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;

GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO app_user;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO app_worker;

-- ★ Auditoria é append-only DE VERDADE: nem a aplicação consegue adulterar a trilha.
REVOKE UPDATE, DELETE ON audit_events    FROM app_user, app_worker;
REVOKE UPDATE, DELETE ON security_events FROM app_user, app_worker;
REVOKE UPDATE, DELETE ON consents        FROM app_user, app_worker;
REVOKE UPDATE, DELETE ON calculation_snapshots   FROM app_user, app_worker;
REVOKE UPDATE, DELETE ON underwriting_decisions  FROM app_user, app_worker;

-- DELETE só é permitido onde a exclusão é lógica (soft delete usa UPDATE).
-- Nenhuma tabela de negócio aceita DELETE físico pela aplicação.
REVOKE DELETE ON ALL TABLES IN SCHEMA public FROM app_user, app_worker;

-- Catálogo de produtos é somente leitura para a aplicação: publicar versão é DDL/migration
REVOKE INSERT, UPDATE ON insurance_products, product_versions, coverages,
                          assistances, eligibility_rules, commission_rules
    FROM app_user;

-- ★ O regulador NÃO tem acesso às tabelas base. Apenas às views mascaradas (V009).
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM app_regulator;
GRANT SELECT ON audit_events, security_events TO app_regulator;

GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA app TO app_user, app_worker;

-- Novas tabelas herdam o padrão seguro
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE ON TABLES TO app_user, app_worker;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    REVOKE DELETE ON TABLES FROM app_user, app_worker;
