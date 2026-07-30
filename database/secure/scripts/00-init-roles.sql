-- =============================================================================
-- PortalDoCorretor — inicialização de papéis, extensões e contexto de tenant
-- Executado uma única vez, na criação do contêiner do PostgreSQL.
--
-- Princípio: menor privilégio. A aplicação NUNCA usa um papel com DDL ou BYPASSRLS.
-- =============================================================================

-- ---------------------------------------------------------------- extensões
CREATE EXTENSION IF NOT EXISTS pgcrypto;            -- gen_random_uuid, cifragem de documento
CREATE EXTENSION IF NOT EXISTS btree_gist;          -- EXCLUDE combinando uuid + daterange
CREATE EXTENSION IF NOT EXISTS pg_trgm;             -- busca aproximada por nome
CREATE EXTENSION IF NOT EXISTS citext;              -- e-mail case-insensitive
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;  -- alimenta o Query Inspector

-- ---------------------------------------------------------------- esquemas
CREATE SCHEMA IF NOT EXISTS app;         -- funções internas (contexto de tenant, integridade)
CREATE SCHEMA IF NOT EXISTS regulatory;  -- views mascaradas: única porta do perfil regulatório

COMMENT ON SCHEMA app IS
    'Funções internas da plataforma: contexto de tenant, verificações de integridade.';
COMMENT ON SCHEMA regulatory IS
    'Views mascaradas e minimizadas. Única superfície de leitura do perfil regulatório simulado.';

-- ---------------------------------------------------------------- papéis
-- nexus_migrator (POSTGRES_USER) aplica DDL. A aplicação NÃO usa este papel.

-- As senhas vêm do ambiente (arquivo .env local, não versionado). Nenhuma credencial,
-- nem de desenvolvimento, é escrita neste arquivo — um repositório público não é lugar
-- para senha, e "é só local" é exatamente a justificativa que precede o vazamento.
\getenv app_user_password POSTGRES_APP_USER_PASSWORD
\getenv app_regulator_password POSTGRES_APP_REGULATOR_PASSWORD
\getenv app_worker_password POSTGRES_APP_WORKER_PASSWORD

DO $$
BEGIN
    -- DML dentro do tenant. Sem DDL, sem BYPASSRLS.
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') THEN
        EXECUTE format('CREATE ROLE app_user LOGIN PASSWORD %L', :'app_user_password');
    END IF;

    -- Somente leitura, e apenas no schema regulatory (views mascaradas).
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_regulator') THEN
        EXECUTE format('CREATE ROLE app_regulator LOGIN PASSWORD %L', :'app_regulator_password');
    END IF;

    -- Workers: Outbox, renovação, faturamento, verificação de integridade.
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_worker') THEN
        EXECUTE format('CREATE ROLE app_worker LOGIN PASSWORD %L', :'app_worker_password');
    END IF;
END $$;

-- Nenhum privilégio implícito: tudo é concedido explicitamente pelas migrations.
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public, app TO app_user, app_worker;
GRANT USAGE ON SCHEMA app TO app_regulator;
GRANT USAGE ON SCHEMA regulatory TO app_regulator;

-- =============================================================================
-- Contexto de tenant — base da camada 5 da defesa em profundidade (ADR-0004)
--
-- A aplicação executa SET LOCAL app.tenant_id no início de cada transação.
-- SET LOCAL (e não SET) é essencial: o valor morre no fim da transação, então uma
-- conexão devolvida ao pool nunca carrega o tenant da requisição anterior.
-- =============================================================================

CREATE OR REPLACE FUNCTION app.current_tenant() RETURNS uuid
LANGUAGE sql STABLE
AS $$
    SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid
$$;

COMMENT ON FUNCTION app.current_tenant() IS
    'Tenant da transação corrente, definido por SET LOCAL. NULL quando não há contexto — '
    'as políticas de RLS negam acesso nesse caso, então ausência de contexto falha fechado.';

CREATE OR REPLACE FUNCTION app.current_profile() RETURNS text
LANGUAGE sql STABLE
AS $$
    SELECT COALESCE(NULLIF(current_setting('app.user_profile', true), ''), 'NONE')
$$;

CREATE OR REPLACE FUNCTION app.current_actor() RETURNS uuid
LANGUAGE sql STABLE
AS $$
    SELECT NULLIF(current_setting('app.actor_id', true), '')::uuid
$$;

CREATE OR REPLACE FUNCTION app.current_correlation() RETURNS uuid
LANGUAGE sql STABLE
AS $$
    SELECT NULLIF(current_setting('app.correlation_id', true), '')::uuid
$$;

GRANT EXECUTE ON FUNCTION
    app.current_tenant(), app.current_profile(),
    app.current_actor(), app.current_correlation()
TO app_user, app_regulator, app_worker;
