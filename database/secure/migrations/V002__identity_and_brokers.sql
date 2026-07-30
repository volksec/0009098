-- =============================================================================
-- V002 — Identidade, corretoras (tenants) e corretores
-- =============================================================================

-- ---------------------------------------------------------------- BROKERAGES (tenant)
CREATE TABLE brokerages (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    legal_name         varchar(180) NOT NULL,
    trade_name         varchar(180) NOT NULL,
    document           cnpj_digits  NOT NULL,
    susep_registration varchar(20)  NOT NULL,
    status             varchar(16)  NOT NULL DEFAULT 'ACTIVE'
                       CHECK (status IN ('ACTIVE','SUSPENDED','INACTIVE')),
    created_at         timestamptz  NOT NULL DEFAULT now(),
    created_by         uuid         NOT NULL,
    updated_at         timestamptz,
    updated_by         uuid,
    deleted_at         timestamptz,
    deleted_by         uuid,
    deletion_reason    text,
    deletion_batch_id  uuid,
    CONSTRAINT ck_brokerages_deletion_coherent
        CHECK ((deleted_at IS NULL) = (deleted_by IS NULL)
           AND (deleted_at IS NULL) = (deletion_reason IS NULL))
);

-- Índice único PARCIAL: a exclusão lógica libera o documento para recadastro (RF-131)
CREATE UNIQUE INDEX ux_brokerages_document ON brokerages (document) WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_brokerages_susep    ON brokerages (susep_registration) WHERE deleted_at IS NULL;

COMMENT ON TABLE brokerages IS
    'Corretora. É a unidade de tenant: brokerages.id é o tenant_id das demais tabelas.';

-- ---------------------------------------------------------------- USERS
CREATE TABLE users (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id         uuid REFERENCES brokerages(id) ON DELETE RESTRICT,  -- NULL p/ regulador
    email             citext NOT NULL,
    password_hash     bytea  NOT NULL,          -- Argon2id
    profile           user_profile NOT NULL,
    display_name      varchar(160) NOT NULL,
    mfa_enabled       boolean NOT NULL DEFAULT false,
    totp_secret       bytea,                    -- cifrado em repouso
    failed_attempts   smallint NOT NULL DEFAULT 0,
    locked_until      timestamptz,
    last_login_at     timestamptz,
    created_at        timestamptz NOT NULL DEFAULT now(),
    created_by        uuid NOT NULL,
    updated_at        timestamptz,
    updated_by        uuid,
    deleted_at        timestamptz,
    deleted_by        uuid,
    deletion_reason   text,
    deletion_batch_id uuid,

    -- Corretor SEMPRE tem tenant; regulador NUNCA tem (é multi-tenant por escopo).
    -- Esta constraint é o que impede um regulador "pertencer" a uma corretora e,
    -- por tabela, herdar acesso de escrita ao tenant dela.
    CONSTRAINT ck_users_tenant_by_profile CHECK (
        (profile = 'BROKER'    AND tenant_id IS NOT NULL) OR
        (profile = 'REGULATOR' AND tenant_id IS NULL)),

    -- RF-002: MFA é OBRIGATÓRIO para o perfil regulatório
    CONSTRAINT ck_users_regulator_requires_mfa CHECK (
        profile <> 'REGULATOR' OR (mfa_enabled AND totp_secret IS NOT NULL))
);

CREATE UNIQUE INDEX ux_users_email ON users (email) WHERE deleted_at IS NULL;
CREATE INDEX ix_users_tenant ON users (tenant_id) WHERE deleted_at IS NULL;

-- ---------------------------------------------------------------- SESSIONS
CREATE TABLE sessions (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id            uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    refresh_token_hash bytea NOT NULL,
    token_family       uuid  NOT NULL,     -- detecção de reuso de refresh (RF-003)
    source_ip          inet,
    user_agent         varchar(256),
    created_at         timestamptz NOT NULL DEFAULT now(),
    last_used_at       timestamptz,
    expires_at         timestamptz NOT NULL,
    revoked_at         timestamptz,
    revocation_reason  varchar(40),
    CONSTRAINT ck_sessions_expiry CHECK (expires_at > created_at)
);

CREATE UNIQUE INDEX ux_sessions_refresh ON sessions (refresh_token_hash);
CREATE INDEX ix_sessions_user_active ON sessions (user_id) WHERE revoked_at IS NULL;
CREATE INDEX ix_sessions_family ON sessions (token_family);

COMMENT ON COLUMN sessions.token_family IS
    'Refresh rotativo: o reuso de um token já rotacionado revoga a FAMÍLIA inteira. '
    'É o que transforma roubo de refresh token em incidente detectado, não em acesso silencioso.';

-- ---------------------------------------------------------------- RBAC
CREATE TABLE roles (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code        varchar(40) NOT NULL UNIQUE,
    description varchar(160) NOT NULL
);

CREATE TABLE permissions (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code        varchar(80) NOT NULL UNIQUE,   -- ex.: policies:issue, customers:read
    resource    varchar(40) NOT NULL,
    action      varchar(40) NOT NULL,
    description varchar(200) NOT NULL,
    CONSTRAINT ux_permissions_resource_action UNIQUE (resource, action)
);

CREATE TABLE role_permissions (
    role_id       uuid NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    permission_id uuid NOT NULL REFERENCES permissions(id) ON DELETE CASCADE,
    PRIMARY KEY (role_id, permission_id)
);

CREATE TABLE user_roles (
    user_id    uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role_id    uuid NOT NULL REFERENCES roles(id) ON DELETE RESTRICT,
    granted_at timestamptz NOT NULL DEFAULT now(),
    granted_by uuid NOT NULL,
    PRIMARY KEY (user_id, role_id)
);

-- ---------------------------------------------------------------- AUTENTICAÇÃO (histórico)
CREATE TABLE authentication_attempts (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email        citext NOT NULL,
    user_id      uuid REFERENCES users(id) ON DELETE SET NULL,
    succeeded    boolean NOT NULL,
    failure_code varchar(40),
    source_ip    inet,
    user_agent   varchar(256),
    occurred_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_auth_attempts_email_time ON authentication_attempts (email, occurred_at DESC);
CREATE INDEX ix_auth_attempts_ip_time    ON authentication_attempts (source_ip, occurred_at DESC);

-- ---------------------------------------------------------------- BROKERS
CREATE TABLE brokers (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id          uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    user_id            uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    susep_registration varchar(20) NOT NULL,
    full_name          varchar(160) NOT NULL,
    status             varchar(16) NOT NULL DEFAULT 'ACTIVE'
                       CHECK (status IN ('ACTIVE','SUSPENDED','INACTIVE')),
    hired_at           date NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    created_by         uuid NOT NULL,
    updated_at         timestamptz,
    updated_by         uuid,
    deleted_at         timestamptz,
    deleted_by         uuid,
    deletion_reason    text,
    deletion_batch_id  uuid,
    CONSTRAINT ux_brokers_user UNIQUE (user_id)
);

CREATE UNIQUE INDEX ux_brokers_susep ON brokers (susep_registration) WHERE deleted_at IS NULL;
CREATE INDEX ix_brokers_tenant ON brokers (tenant_id, status) WHERE deleted_at IS NULL;

-- ---------------------------------------------------------------- REGULADOR
CREATE TABLE susep_regulatory_users (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id            uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    registration       varchar(20) NOT NULL,   -- fictício
    full_name          varchar(160) NOT NULL,
    authorized_tenants uuid[] NOT NULL DEFAULT '{}',
    created_at         timestamptz NOT NULL DEFAULT now(),
    created_by         uuid NOT NULL,
    CONSTRAINT ux_regulatory_user UNIQUE (user_id),
    CONSTRAINT ux_regulatory_registration UNIQUE (registration)
);

COMMENT ON COLUMN susep_regulatory_users.authorized_tenants IS
    'Escopo do supervisor: array vazio significa NENHUM acesso, não acesso total. '
    'A política de RLS falha fechado quando o escopo não contém o tenant consultado.';

-- ---------------------------------------------------------------- ACESSO JUSTIFICADO (RF-091)
CREATE TABLE regulatory_access_sessions (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    regulator_id  uuid NOT NULL REFERENCES susep_regulatory_users(id) ON DELETE RESTRICT,
    purpose       access_purpose NOT NULL,
    justification text NOT NULL,
    scope_tenants uuid[] NOT NULL,
    opened_at     timestamptz NOT NULL DEFAULT now(),
    expires_at    timestamptz NOT NULL,
    closed_at     timestamptz,
    correlation_id uuid NOT NULL,
    CONSTRAINT ck_regulatory_justification_length CHECK (length(trim(justification)) >= 20),
    CONSTRAINT ck_regulatory_session_ttl CHECK (expires_at > opened_at),
    CONSTRAINT ck_regulatory_scope_not_empty CHECK (cardinality(scope_tenants) > 0)
);

CREATE INDEX ix_regulatory_sessions_active ON regulatory_access_sessions (regulator_id, expires_at)
    WHERE closed_at IS NULL;

COMMENT ON TABLE regulatory_access_sessions IS
    'Sessão de acesso com finalidade declarada e TTL. Consulta sensível sem sessão ativa '
    'retorna 403 — a finalidade é pré-requisito, não um campo preenchido depois para o relatório.';

-- Função usada pelas políticas de RLS do perfil regulatório
CREATE OR REPLACE FUNCTION app.regulatory_scope_current() RETURNS TABLE (tenant_id uuid)
LANGUAGE sql STABLE
AS $$
    SELECT unnest(s.scope_tenants)
    FROM regulatory_access_sessions s
    JOIN susep_regulatory_users r ON r.id = s.regulator_id
    WHERE r.user_id = app.current_actor()
      AND s.closed_at IS NULL
      AND s.expires_at > now()
$$;

GRANT EXECUTE ON FUNCTION app.regulatory_scope_current() TO app_user, app_regulator, app_worker;
