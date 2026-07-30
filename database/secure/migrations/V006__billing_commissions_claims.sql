-- =============================================================================
-- V006 — Faturamento, comissões e sinistros
-- =============================================================================

-- ---------------------------------------------------------------- BILLING
CREATE TABLE installment_plans (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id         uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    policy_id         uuid NOT NULL REFERENCES policies(id) ON DELETE CASCADE,
    total_amount      money_amount NOT NULL,
    installment_count smallint NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_installment_plans_policy UNIQUE (policy_id),
    CONSTRAINT ck_plans_count CHECK (installment_count BETWEEN 1 AND 12),
    CONSTRAINT ck_plans_amount CHECK ((total_amount).amount > 0)
);

CREATE TABLE installments (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id  uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    plan_id    uuid NOT NULL REFERENCES installment_plans(id) ON DELETE CASCADE,
    sequence   smallint NOT NULL,
    amount     money_amount NOT NULL,
    due_date   date NOT NULL,
    status     installment_status NOT NULL DEFAULT 'PENDING',
    paid_at    timestamptz,
    CONSTRAINT ux_installments_sequence UNIQUE (plan_id, sequence),
    CONSTRAINT ck_installments_amount CHECK ((amount).amount > 0),
    CONSTRAINT ck_installments_paid CHECK ((status = 'PAID') = (paid_at IS NOT NULL)),
    CONSTRAINT ck_installments_sequence_positive CHECK (sequence > 0)
);

CREATE INDEX ix_installments_plan ON installments (plan_id, sequence);
-- Índice PARCIAL para o Billing Scheduler
CREATE INDEX ix_installments_due ON installments (due_date)
    WHERE status IN ('PENDING','OVERDUE');

-- ★ INVARIANTE FINANCEIRA: Σ parcelas = total do plano, ao centavo.
--
-- Constraint declarativa não alcança agregação entre linhas — este é um dos três
-- casos em que uma trigger é justificada (ADR/physical-model §9). É DEFERRABLE
-- INITIALLY DEFERRED para permitir inserir as parcelas uma a uma dentro da mesma
-- transação: a verificação ocorre no COMMIT, não a cada linha.
CREATE OR REPLACE FUNCTION app.assert_installments_sum() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_plan  uuid := COALESCE(NEW.plan_id, OLD.plan_id);
    v_sum   numeric(14,2);
    v_total numeric(14,2);
BEGIN
    SELECT COALESCE(sum((amount).amount), 0) INTO v_sum
      FROM installments WHERE plan_id = v_plan;

    SELECT (total_amount).amount INTO v_total
      FROM installment_plans WHERE id = v_plan;

    -- Plano removido na mesma transação: nada a verificar
    IF v_total IS NULL THEN RETURN NULL; END IF;

    IF v_sum IS DISTINCT FROM v_total THEN
        RAISE EXCEPTION
            'Soma das parcelas (%) difere do total do plano (%) [plano %]', v_sum, v_total, v_plan
            USING ERRCODE = 'check_violation',
                  HINT = 'Use Money.Allocate para dividir o prêmio sem perder centavos.';
    END IF;

    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER tg_installments_sum
    AFTER INSERT OR UPDATE OR DELETE ON installments
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION app.assert_installments_sum();

CREATE TABLE payments (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    installment_id uuid NOT NULL REFERENCES installments(id) ON DELETE RESTRICT,
    amount         money_amount NOT NULL,
    method         varchar(20) NOT NULL CHECK (method IN ('SIMULATED_BOLETO','SIMULATED_CARD','SIMULATED_PIX')),
    paid_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_payments_amount CHECK ((amount).amount > 0)
);

COMMENT ON TABLE payments IS
    'Pagamentos SIMULADOS. Não há liquidação financeira real — os métodos são prefixados '
    'com SIMULATED_ para que nenhuma tela possa apresentá-los como transação real.';

-- ---------------------------------------------------------------- COMMISSIONS
CREATE TABLE commission_rules (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id   uuid NOT NULL REFERENCES insurance_products(id) ON DELETE RESTRICT,
    version      integer NOT NULL,
    rate         numeric(6,4) NOT NULL,
    base_on      commission_base NOT NULL,
    valid_period daterange NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_commission_rules_version UNIQUE (product_id, version),
    -- Teto de negócio de 35%, o mesmo do VO CommissionRate
    CONSTRAINT ck_commission_rate CHECK (rate > 0 AND rate <= 0.35),
    -- Não pode haver duas regras vigentes para o mesmo produto no mesmo período
    EXCLUDE USING gist (product_id WITH =, valid_period WITH &&)
);

CREATE TABLE commissions (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id        uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    policy_id        uuid NOT NULL REFERENCES policies(id) ON DELETE RESTRICT,
    broker_id        uuid NOT NULL REFERENCES brokers(id) ON DELETE RESTRICT,
    rule_id          uuid NOT NULL REFERENCES commission_rules(id) ON DELETE RESTRICT,
    rule_version     integer NOT NULL,
    rate_applied     numeric(6,4) NOT NULL,
    base_amount      money_amount NOT NULL,
    amount           money_amount NOT NULL,
    status           commission_status NOT NULL DEFAULT 'FORECAST',
    reversed_from_id uuid REFERENCES commissions(id) ON DELETE RESTRICT,
    reference_month  date NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    released_at      timestamptz,
    paid_at          timestamptz,
    -- Estorno é lançamento INVERSO (valor negativo), nunca UPDATE destrutivo
    CONSTRAINT ck_commissions_amount_sign CHECK (
        (status = 'REVERSED' AND (amount).amount <= 0) OR
        (status <> 'REVERSED' AND (amount).amount >= 0)),
    CONSTRAINT ck_commissions_reversal CHECK (
        (status = 'REVERSED') = (reversed_from_id IS NOT NULL)),
    CONSTRAINT ck_commissions_rate CHECK (rate_applied > 0 AND rate_applied <= 0.35),
    CONSTRAINT ck_commissions_month CHECK (extract(day from reference_month) = 1)
);

-- broker_id no índice: o corretor só enxerga as PRÓPRIAS comissões (ABAC),
-- então esta é a forma de acesso dominante
CREATE INDEX ix_commissions_broker
    ON commissions (tenant_id, broker_id, status, reference_month DESC);
CREATE INDEX ix_commissions_policy ON commissions (policy_id);

COMMENT ON TABLE commissions IS
    'Registra rule_id, rule_version, rate_applied E base_amount. Ainda que a regra mude '
    'amanhã, a pergunta "por que essa comissão foi esse valor?" continua respondível sem '
    'arqueologia. É o requisito de rastreabilidade traduzido em colunas.';

-- ---------------------------------------------------------------- CLAIMS
CREATE TABLE claims (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id        uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    policy_id        uuid NOT NULL REFERENCES policies(id) ON DELETE RESTRICT,
    broker_id        uuid NOT NULL REFERENCES brokers(id) ON DELETE RESTRICT,
    number           varchar(24) NOT NULL,
    status           claim_status NOT NULL DEFAULT 'REPORTED',
    occurrence_date  date NOT NULL,
    reported_at      timestamptz NOT NULL DEFAULT now(),
    description      text NOT NULL,
    estimated_amount money_amount,
    settled_amount   money_amount,
    decided_at       timestamptz,
    decision_reason  varchar(300),
    correlation_id   uuid NOT NULL,
    deleted_at       timestamptz,
    CONSTRAINT ux_claims_number UNIQUE (tenant_id, number),
    CONSTRAINT ck_claims_occurrence_not_future CHECK (occurrence_date <= CURRENT_DATE),
    CONSTRAINT ck_claims_amounts CHECK (
        (estimated_amount IS NULL OR (estimated_amount).amount >= 0)
        AND (settled_amount IS NULL OR (settled_amount).amount >= 0)),
    CONSTRAINT ck_claims_settled_requires_decision CHECK (
        settled_amount IS NULL OR decided_at IS NOT NULL)
);

CREATE INDEX ix_claims_policy ON claims (policy_id, reported_at DESC);
CREATE INDEX ix_claims_open ON claims (tenant_id, status)
    WHERE status NOT IN ('SETTLED','CLOSED','DENIED');

-- ★ INVARIANTE: a data do evento deve estar DENTRO da vigência da apólice.
-- Cruza duas tabelas, então não cabe em CHECK — é uma das três triggers justificadas.
CREATE OR REPLACE FUNCTION app.assert_claim_within_coverage() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_period daterange;
BEGIN
    SELECT coverage_period INTO v_period FROM policies WHERE id = NEW.policy_id;

    IF NOT (v_period @> NEW.occurrence_date) THEN
        RAISE EXCEPTION
            'Data do evento (%) fora da vigência da apólice %', NEW.occurrence_date, v_period
            USING ERRCODE = 'check_violation';
    END IF;

    RETURN NEW;
END $$;

CREATE TRIGGER tg_claims_within_coverage
    BEFORE INSERT OR UPDATE OF occurrence_date, policy_id ON claims
    FOR EACH ROW EXECUTE FUNCTION app.assert_claim_within_coverage();

CREATE TABLE claim_events (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    claim_id    uuid NOT NULL REFERENCES claims(id) ON DELETE CASCADE,
    sequence    integer NOT NULL,
    kind        varchar(40) NOT NULL,
    description varchar(300) NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    recorded_by uuid NOT NULL,
    CONSTRAINT ux_claim_events_sequence UNIQUE (claim_id, sequence)
);

CREATE INDEX ix_claim_events_claim ON claim_events (claim_id, sequence);

CREATE TABLE damages (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    claim_id    uuid NOT NULL REFERENCES claims(id) ON DELETE CASCADE,
    coverage_id uuid NOT NULL REFERENCES coverages(id) ON DELETE RESTRICT,
    description varchar(300) NOT NULL,
    estimated   money_amount NOT NULL,
    CONSTRAINT ck_damages_amount CHECK ((estimated).amount >= 0)
);

CREATE TABLE claim_status_history (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    claim_id       uuid NOT NULL REFERENCES claims(id) ON DELETE CASCADE,
    from_status    claim_status,
    to_status      claim_status NOT NULL,
    reason         varchar(200),
    changed_at     timestamptz NOT NULL DEFAULT now(),
    changed_by     uuid NOT NULL,
    correlation_id uuid NOT NULL
);

CREATE INDEX ix_claim_history ON claim_status_history (claim_id, changed_at);

-- ---------------------------------------------------------------- DOCUMENTS
CREATE TABLE documents (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    owner_type    varchar(20) NOT NULL CHECK (owner_type IN ('PROPOSAL','CLAIM','CUSTOMER')),
    owner_id      uuid NOT NULL,
    file_name     varchar(200) NOT NULL,   -- nome sanitizado, regerado como UUID
    original_name varchar(200) NOT NULL,   -- exibição apenas; nunca usado no filesystem
    content_type  varchar(100) NOT NULL,
    detected_type varchar(100) NOT NULL,   -- por magic bytes, não pelo header do cliente
    size_bytes    bigint NOT NULL,
    content_hash  bytea NOT NULL,          -- SHA-256
    storage_path  varchar(400) NOT NULL,
    uploaded_at   timestamptz NOT NULL DEFAULT now(),
    uploaded_by   uuid NOT NULL,
    deleted_at    timestamptz,
    deleted_by    uuid,
    deletion_reason text,
    CONSTRAINT ck_documents_size CHECK (size_bytes > 0 AND size_bytes <= 20971520),
    -- O tipo declarado pelo cliente PRECISA bater com o detectado por magic bytes.
    -- É a contenção de "PDF" que na verdade é HTML com script.
    CONSTRAINT ck_documents_type_matches CHECK (content_type = detected_type)
);

-- Deduplicação por hash DENTRO do tenant: idempotência natural do upload
CREATE UNIQUE INDEX ux_documents_tenant_hash ON documents (tenant_id, content_hash)
    WHERE deleted_at IS NULL;
CREATE INDEX ix_documents_owner ON documents (owner_type, owner_id) WHERE deleted_at IS NULL;

-- ---------------------------------------------------------------- NOTIFICATIONS
CREATE TABLE notifications (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    recipient_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    kind        varchar(40) NOT NULL,
    title       varchar(160) NOT NULL,
    body        varchar(600) NOT NULL,
    resource_type varchar(40),
    resource_id uuid,
    created_at  timestamptz NOT NULL DEFAULT now(),
    read_at     timestamptz,
    source_message_id uuid          -- idempotência do consumidor da Outbox
);

CREATE INDEX ix_notifications_unread ON notifications (recipient_id, created_at DESC)
    WHERE read_at IS NULL;
CREATE UNIQUE INDEX ux_notifications_source ON notifications (source_message_id)
    WHERE source_message_id IS NOT NULL;
