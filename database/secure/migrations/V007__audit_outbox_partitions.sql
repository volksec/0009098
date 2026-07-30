-- =============================================================================
-- V007 — Auditoria, eventos de segurança, Outbox e agentes de IA
--
-- Todas particionadas por mês: são as tabelas de maior crescimento do sistema, e
-- o particionamento permite desanexar partições antigas para arquivamento sem
-- DELETE em massa (que geraria bloat e WAL desnecessários).
-- =============================================================================

-- ---------------------------------------------------------------- AUDIT EVENTS
CREATE TABLE audit_events (
    id             uuid NOT NULL DEFAULT gen_random_uuid(),
    occurred_at    timestamptz NOT NULL DEFAULT now(),
    tenant_id      uuid,                        -- NULL em consulta regulatória multi-tenant
    correlation_id uuid NOT NULL,
    trace_id       varchar(32),
    span_id        varchar(16),
    actor_id       uuid NOT NULL,
    actor_profile  user_profile NOT NULL,
    action         varchar(60) NOT NULL,
    resource_type  varchar(60) NOT NULL,
    resource_id    uuid,
    outcome        varchar(20) NOT NULL CHECK (outcome IN ('SUCCESS','DENIED','ERROR')),
    duration_ms    integer,

    -- Campos exclusivos do acesso regulatório (RF-099): os 12 obrigatórios
    access_purpose access_purpose,
    justification  text,
    regulatory_session_id uuid,
    visible_fields text[],
    masked_fields  text[],

    before_state   jsonb,   -- JSONB-JUSTIFICATION: a forma varia por tipo de recurso auditado
    after_state    jsonb,

    PRIMARY KEY (id, occurred_at),

    -- Consulta regulatória SEM finalidade declarada não pode ser gravada.
    -- A auditoria recusa registrar um acesso que não deveria ter acontecido.
    CONSTRAINT ck_audit_regulatory_requires_purpose CHECK (
        actor_profile <> 'REGULATOR'
        OR action NOT LIKE 'REGULATORY_%'
        OR (access_purpose IS NOT NULL AND justification IS NOT NULL))
) PARTITION BY RANGE (occurred_at);

CREATE INDEX ix_audit_correlation ON audit_events (correlation_id);
CREATE INDEX ix_audit_tenant_time ON audit_events (tenant_id, occurred_at DESC);
CREATE INDEX ix_audit_actor_time  ON audit_events (actor_id, occurred_at DESC);
CREATE INDEX ix_audit_resource    ON audit_events (resource_type, resource_id);

COMMENT ON TABLE audit_events IS
    'APPEND-ONLY REAL: UPDATE e DELETE são revogados para os papéis da aplicação em V008. '
    'Gravado na MESMA transação da operação auditada — não existe operação de negócio '
    'confirmada sem auditoria correspondente.';

-- ---------------------------------------------------------------- SECURITY EVENTS
CREATE TABLE security_events (
    id                uuid NOT NULL DEFAULT gen_random_uuid(),
    occurred_at       timestamptz NOT NULL DEFAULT now(),
    tenant_id         uuid,
    actor_id          uuid,
    event_type        varchar(60) NOT NULL,
    severity          varchar(16) NOT NULL CHECK (severity IN ('INFO','LOW','MEDIUM','HIGH','CRITICAL')),
    source_ip         inet,
    correlation_id    uuid,
    resource_type     varchar(60),
    resource_id       uuid,
    control_triggered varchar(80),   -- QUAL controle bloqueou — alimenta o Security Lab
    cwe_id            varchar(16),
    owasp_category    varchar(40),
    details           jsonb,         -- JSONB-JUSTIFICATION: a forma varia por tipo de evento
    PRIMARY KEY (id, occurred_at)
) PARTITION BY RANGE (occurred_at);

CREATE INDEX ix_security_type_time ON security_events (event_type, occurred_at DESC);
CREATE INDEX ix_security_tenant    ON security_events (tenant_id, occurred_at DESC);
CREATE INDEX ix_security_severity  ON security_events (severity, occurred_at DESC)
    WHERE severity IN ('HIGH','CRITICAL');

-- ---------------------------------------------------------------- OUTBOX
CREATE TABLE outbox_messages (
    id              uuid NOT NULL DEFAULT gen_random_uuid(),
    occurred_at     timestamptz NOT NULL DEFAULT now(),
    tenant_id       uuid NOT NULL,
    message_type    varchar(120) NOT NULL,
    payload         jsonb NOT NULL,   -- JSONB-JUSTIFICATION: o contrato varia por tipo de evento
    correlation_id  uuid NOT NULL,
    aggregate_type  varchar(60) NOT NULL,
    aggregate_id    uuid NOT NULL,
    processed_at    timestamptz,
    attempts        smallint NOT NULL DEFAULT 0,
    last_error      text,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (id, occurred_at),
    CONSTRAINT ck_outbox_attempts CHECK (attempts >= 0 AND attempts <= 10)
) PARTITION BY RANGE (occurred_at);

-- Índice PARCIAL: o dispatcher só enxerga o pendente. Mantém o índice quente pequeno
-- mesmo com milhões de mensagens já processadas.
CREATE INDEX ix_outbox_pending ON outbox_messages (next_attempt_at)
    WHERE processed_at IS NULL;
CREATE INDEX ix_outbox_aggregate ON outbox_messages (aggregate_type, aggregate_id);

-- Dead letter: mensagens que esgotaram as tentativas
CREATE INDEX ix_outbox_dead_letter ON outbox_messages (occurred_at)
    WHERE processed_at IS NULL AND attempts >= 10;

-- ---------------------------------------------------------------- IDEMPOTÊNCIA
CREATE TABLE processed_messages (
    message_id   uuid NOT NULL,
    consumer     varchar(120) NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (message_id, consumer)
);

COMMENT ON TABLE processed_messages IS
    'A Outbox entrega AO MENOS UMA VEZ. Exatamente-uma-vez é inalcançável sem coordenação '
    'distribuída, então o consumidor precisa ser idempotente por construção — esta tabela '
    'é o registro que torna isso verificável.';

CREATE TABLE idempotency_keys (
    tenant_id       uuid NOT NULL,
    key             varchar(64) NOT NULL,
    endpoint        varchar(160) NOT NULL,
    request_hash    bytea NOT NULL,
    response_status smallint,
    response_body   jsonb,
    created_at      timestamptz NOT NULL DEFAULT now(),
    completed_at    timestamptz,
    PRIMARY KEY (tenant_id, key, endpoint)
);

CREATE INDEX ix_idempotency_created ON idempotency_keys (created_at);

COMMENT ON TABLE idempotency_keys IS
    'Terceira camada contra emissão duplicada (as outras: optimistic lock e ux_policies_proposal). '
    'O request_hash impede que a mesma chave seja reutilizada para um payload DIFERENTE — '
    'sem isso, a idempotência viraria um bypass de validação.';

-- ---------------------------------------------------------------- AGENTES DE IA
CREATE TABLE agents (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code           varchar(40) NOT NULL UNIQUE,
    name           varchar(120) NOT NULL,
    description    text NOT NULL,
    allowed_tools  text[] NOT NULL DEFAULT '{}',   -- allowlist, não denylist
    max_executions_per_hour smallint NOT NULL DEFAULT 30,
    requires_profile user_profile,
    enabled        boolean NOT NULL DEFAULT true,
    created_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_agents_rate_limit CHECK (max_executions_per_hour BETWEEN 1 AND 500)
);

CREATE TABLE agent_skills (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    agent_id    uuid NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    code        varchar(40) NOT NULL,
    description varchar(300) NOT NULL,
    CONSTRAINT ux_agent_skills UNIQUE (agent_id, code)
);

CREATE TABLE agent_executions (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           uuid REFERENCES brokerages(id) ON DELETE RESTRICT,
    agent_id            uuid NOT NULL REFERENCES agents(id) ON DELETE RESTRICT,
    actor_id            uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    correlation_id      uuid NOT NULL,
    input_redacted      text NOT NULL,
    output_redacted     text,
    tools_invoked       text[] NOT NULL DEFAULT '{}',
    tokens_input        integer,
    tokens_output       integer,
    duration_ms         integer,
    outcome             varchar(20) NOT NULL CHECK (outcome IN ('SUCCESS','REFUSED','ERROR','TIMEOUT')),
    guardrail_triggered varchar(80),
    created_at          timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_agent_exec_actor ON agent_executions (actor_id, created_at DESC);
CREATE INDEX ix_agent_exec_agent ON agent_executions (agent_id, created_at DESC);
-- Suporta o rate limit por usuário e janela
CREATE INDEX ix_agent_exec_rate ON agent_executions (actor_id, agent_id, created_at);

COMMENT ON COLUMN agent_executions.input_redacted IS
    'Entrada com dados sensíveis REDIGIDOS. Trade-off aceito: impede depurar o prompt exato, '
    'em favor da privacidade. Guardar o prompt cru seria guardar dado pessoal em texto livre.';

-- =============================================================================
-- Criação automática de partições
-- =============================================================================

CREATE OR REPLACE FUNCTION app.ensure_monthly_partition(
    p_table text, p_month date) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_start date := date_trunc('month', p_month)::date;
    v_end   date := (date_trunc('month', p_month) + interval '1 month')::date;
    v_name  text := format('%s_%s', p_table, to_char(v_start, 'YYYY_MM'));
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_class WHERE relname = v_name) THEN
        EXECUTE format(
            'CREATE TABLE %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
            v_name, p_table, v_start, v_end);
    END IF;
END $$;

-- Partições do mês corrente e dos próximos três, para que a aplicação nunca
-- falhe por ausência de partição na virada do mês.
DO $$
DECLARE
    v_table text;
    v_offset int;
BEGIN
    FOREACH v_table IN ARRAY ARRAY['audit_events','security_events','outbox_messages'] LOOP
        FOR v_offset IN -1..3 LOOP
            PERFORM app.ensure_monthly_partition(
                v_table, (date_trunc('month', now()) + (v_offset || ' month')::interval)::date);
        END LOOP;
    END LOOP;
END $$;
