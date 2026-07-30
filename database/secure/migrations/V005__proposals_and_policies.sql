-- =============================================================================
-- V005 — Propostas e apólices
--
-- É aqui que moram as invariantes mais importantes do case: emissão única por
-- proposta e vigências sem sobreposição.
-- =============================================================================

-- ---------------------------------------------------------------- PROPOSALS
CREATE TABLE proposals (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    quotation_id    uuid NOT NULL REFERENCES quotations(id) ON DELETE RESTRICT,
    broker_id       uuid NOT NULL REFERENCES brokers(id) ON DELETE RESTRICT,
    customer_id     uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
    number          varchar(24) NOT NULL,
    status          proposal_status NOT NULL DEFAULT 'DRAFT',
    chosen_plan     plan_tier NOT NULL,
    net_premium     money_amount NOT NULL,
    total_premium   money_amount NOT NULL,
    installment_count smallint NOT NULL DEFAULT 1,
    submitted_at    timestamptz,
    decided_at      timestamptz,
    issued_at       timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    created_by      uuid NOT NULL,
    updated_at      timestamptz,
    updated_by      uuid,
    deleted_at      timestamptz,
    deleted_by      uuid,
    deletion_reason text,
    deletion_batch_id uuid,
    CONSTRAINT ck_proposals_premium CHECK (
        (net_premium).amount > 0 AND (total_premium).amount >= (net_premium).amount),
    CONSTRAINT ck_proposals_currency CHECK ((total_premium).currency = 'BRL'),
    CONSTRAINT ck_proposals_installments CHECK (installment_count BETWEEN 1 AND 12),
    -- Coerência temporal: não se decide antes de submeter, nem emite antes de decidir
    CONSTRAINT ck_proposals_timeline CHECK (
        (submitted_at IS NULL OR submitted_at >= created_at)
        AND (decided_at IS NULL OR (submitted_at IS NOT NULL AND decided_at >= submitted_at))
        AND (issued_at IS NULL OR (decided_at IS NOT NULL AND issued_at >= decided_at))),
    CONSTRAINT ck_proposals_issued_status CHECK ((status = 'ISSUED') = (issued_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_proposals_tenant_number ON proposals (tenant_id, number);

-- ★ INVARIANTE: uma cotação gera no máximo UMA proposta viva.
CREATE UNIQUE INDEX ux_proposals_quotation_active ON proposals (quotation_id)
    WHERE status NOT IN ('REJECTED','EXPIRED') AND deleted_at IS NULL;

CREATE INDEX ix_proposals_broker_status ON proposals (tenant_id, broker_id, status)
    WHERE deleted_at IS NULL;
CREATE INDEX ix_proposals_pending ON proposals (tenant_id, created_at DESC)
    WHERE status IN ('SUBMITTED','UNDER_ANALYSIS','PENDING') AND deleted_at IS NULL;

-- FK circular resolvida agora que proposals existe
ALTER TABLE quotations ADD CONSTRAINT fk_quotations_previous_policy
    FOREIGN KEY (previous_policy_id) REFERENCES quotations(id) DEFERRABLE INITIALLY DEFERRED;

-- ---------------------------------------------------------------- PENDÊNCIAS
CREATE TABLE pendencies (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    proposal_id uuid NOT NULL REFERENCES proposals(id) ON DELETE CASCADE,
    code        varchar(40) NOT NULL,
    description varchar(300) NOT NULL,
    opened_at   timestamptz NOT NULL DEFAULT now(),
    resolved_at timestamptz,
    resolved_by uuid,
    CONSTRAINT ck_pendencies_resolution CHECK (
        (resolved_at IS NULL) = (resolved_by IS NULL)
        AND (resolved_at IS NULL OR resolved_at >= opened_at))
);

-- Índice PARCIAL: a consulta "esta proposta tem pendência aberta?" é a mais frequente
CREATE INDEX ix_pendencies_open ON pendencies (proposal_id) WHERE resolved_at IS NULL;

-- ---------------------------------------------------------------- UNDERWRITING
CREATE TABLE underwriting_decisions (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    proposal_id    uuid NOT NULL REFERENCES proposals(id) ON DELETE CASCADE,
    version        integer NOT NULL,
    outcome        varchar(20) NOT NULL CHECK (outcome IN ('APPROVED','REJECTED','PENDING')),
    reasons        text[] NOT NULL DEFAULT '{}',
    evaluated_rules jsonb NOT NULL,  -- JSONB-JUSTIFICATION: o conjunto de regras avaliadas
                                     -- varia por versão de produto
    decided_at     timestamptz NOT NULL DEFAULT now(),
    decided_by     uuid NOT NULL,    -- conta técnica do Underwriting Engine
    correlation_id uuid NOT NULL,
    CONSTRAINT ux_underwriting_version UNIQUE (proposal_id, version)
);

COMMENT ON TABLE underwriting_decisions IS
    'IMUTÁVEL e VERSIONADA. Reanálise cria uma nova versão; a decisão anterior nunca é '
    'sobrescrita. É o que permite auditar por que uma proposta foi recusada em uma data.';

CREATE TABLE proposal_status_history (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    proposal_id uuid NOT NULL REFERENCES proposals(id) ON DELETE CASCADE,
    from_status proposal_status,
    to_status   proposal_status NOT NULL,
    reason      varchar(200),
    changed_at  timestamptz NOT NULL DEFAULT now(),
    changed_by  uuid NOT NULL,
    correlation_id uuid NOT NULL
);

CREATE INDEX ix_proposal_history ON proposal_status_history (proposal_id, changed_at);

-- ---------------------------------------------------------------- POLICIES ★
CREATE TABLE policies (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id          uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    proposal_id        uuid NOT NULL REFERENCES proposals(id) ON DELETE RESTRICT,
    broker_id          uuid NOT NULL REFERENCES brokers(id) ON DELETE RESTRICT,
    customer_id        uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
    asset_id           uuid NOT NULL REFERENCES insurable_assets(id) ON DELETE RESTRICT,
    product_version_id uuid NOT NULL REFERENCES product_versions(id) ON DELETE RESTRICT,
    number             varchar(24) NOT NULL,
    status             policy_status NOT NULL DEFAULT 'ACTIVE',
    coverage_period    daterange NOT NULL,
    net_premium        money_amount NOT NULL,
    total_premium      money_amount NOT NULL,
    issued_at          timestamptz NOT NULL DEFAULT now(),
    issued_by          uuid NOT NULL,
    cancelled_at       timestamptz,
    cancellation_reason varchar(60),
    renewed_from_id    uuid REFERENCES policies(id) ON DELETE SET NULL,
    correlation_id     uuid NOT NULL,

    CONSTRAINT ck_policies_premium_positive CHECK ((total_premium).amount > 0),
    CONSTRAINT ck_policies_currency CHECK ((total_premium).currency = 'BRL'),
    CONSTRAINT ck_policies_net_le_total CHECK ((net_premium).amount <= (total_premium).amount),
    CONSTRAINT ck_policies_period_valid CHECK (NOT isempty(coverage_period)),
    CONSTRAINT ck_policies_cancel_coherent CHECK (
        (status = 'CANCELLED') = (cancelled_at IS NOT NULL)
        AND (cancelled_at IS NULL) = (cancellation_reason IS NULL))
);

-- ★ INVARIANTE 1: exatamente UMA apólice viva por proposta.
-- Junto com o optimistic lock (xmin) e a Idempotency-Key, forma as três camadas
-- que impedem emissão duplicada — o cenário de concorrência obrigatório do case.
CREATE UNIQUE INDEX ux_policies_proposal ON policies (proposal_id)
    WHERE status <> 'CANCELLED';

CREATE UNIQUE INDEX ux_policies_tenant_number ON policies (tenant_id, number);

-- ★ INVARIANTE 2: vigências não se sobrepõem para o mesmo bem/produto.
-- Impossível de expressar com UNIQUE: sobreposição de intervalos não é igualdade.
-- É a mesma regra do método DateRange.Overlaps() do domínio.
ALTER TABLE policies ADD CONSTRAINT ex_policies_no_overlap
    EXCLUDE USING gist (
        tenant_id          WITH =,
        asset_id           WITH =,
        product_version_id WITH =,
        coverage_period    WITH &&
    ) WHERE (status = 'ACTIVE');

CREATE INDEX ix_policies_broker_status ON policies (tenant_id, broker_id, status);
CREATE INDEX ix_policies_customer ON policies (tenant_id, customer_id);
-- Índice PARCIAL para o Renewal Scanner: indexa apenas o que está ativo
CREATE INDEX ix_policies_expiring ON policies (upper(coverage_period)) WHERE status = 'ACTIVE';

-- Agora a FK de renovação da cotação pode apontar para policies
ALTER TABLE quotations DROP CONSTRAINT fk_quotations_previous_policy;
ALTER TABLE quotations ADD CONSTRAINT fk_quotations_previous_policy
    FOREIGN KEY (previous_policy_id) REFERENCES policies(id) ON DELETE SET NULL;

-- ---------------------------------------------------------------- POLICY COVERAGES
CREATE TABLE policy_coverages (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id    uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    policy_id    uuid NOT NULL REFERENCES policies(id) ON DELETE CASCADE,
    coverage_id  uuid NOT NULL REFERENCES coverages(id) ON DELETE RESTRICT,
    limit_amount money_amount NOT NULL,
    deductible   deductible NOT NULL,
    premium      money_amount NOT NULL,
    is_mandatory boolean NOT NULL,
    CONSTRAINT ux_policy_coverage UNIQUE (policy_id, coverage_id),
    CONSTRAINT ck_policy_coverages_limit CHECK ((limit_amount).amount > 0),
    CONSTRAINT ck_policy_coverages_premium CHECK ((premium).amount >= 0)
);

CREATE INDEX ix_policy_coverages_policy ON policy_coverages (policy_id);

COMMENT ON TABLE policy_coverages IS
    'CONGELADAS na emissão a partir do CalculationSnapshot. Alteração posterior só por '
    'endosso, que versiona — a apólice original permanece consultável.';

-- ---------------------------------------------------------------- ENDOSSOS E RENOVAÇÕES
CREATE TABLE endorsements (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    policy_id      uuid NOT NULL REFERENCES policies(id) ON DELETE RESTRICT,
    sequence       integer NOT NULL,
    kind           varchar(30) NOT NULL
                   CHECK (kind IN ('COVERAGE_CHANGE','PERIOD_CHANGE','DATA_CORRECTION','CANCELLATION')),
    description    varchar(300) NOT NULL,
    premium_delta  money_amount NOT NULL,
    effective_date date NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    created_by     uuid NOT NULL,
    correlation_id uuid NOT NULL,
    CONSTRAINT ux_endorsements_sequence UNIQUE (policy_id, sequence)
);

CREATE INDEX ix_endorsements_policy ON endorsements (policy_id, sequence);

CREATE TABLE renewals (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id        uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    policy_id        uuid NOT NULL REFERENCES policies(id) ON DELETE RESTRICT,
    cycle            integer NOT NULL,
    new_quotation_id uuid REFERENCES quotations(id) ON DELETE SET NULL,
    new_policy_id    uuid REFERENCES policies(id) ON DELETE SET NULL,
    outcome          varchar(20) NOT NULL DEFAULT 'PENDING'
                     CHECK (outcome IN ('PENDING','ACCEPTED','DECLINED','EXPIRED')),
    detected_at      timestamptz NOT NULL DEFAULT now(),
    decided_at       timestamptz,
    decline_reason   varchar(200),
    -- Idempotência do Renewal Scanner: um registro por apólice e ciclo
    CONSTRAINT ux_renewals_policy_cycle UNIQUE (policy_id, cycle),
    CONSTRAINT ck_renewals_decision CHECK (
        (outcome = 'PENDING') = (decided_at IS NULL))
);

CREATE INDEX ix_renewals_pending ON renewals (tenant_id, detected_at)
    WHERE outcome = 'PENDING';
