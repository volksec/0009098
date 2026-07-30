-- =============================================================================
-- V004 — Catálogo de produtos versionado e cotações
--
-- O versionamento do produto é requisito de rastreabilidade: cotação e apólice
-- referenciam a VERSÃO, não o produto. Alterar o catálogo nunca reescreve o passado.
-- =============================================================================

-- ---------------------------------------------------------------- PRODUCTS
CREATE TABLE insurance_products (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code        varchar(30) NOT NULL UNIQUE,
    name        varchar(120) NOT NULL,
    branch      insurance_branch NOT NULL,
    description text,
    created_at  timestamptz NOT NULL DEFAULT now(),
    deleted_at  timestamptz
);

CREATE TABLE product_versions (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id            uuid NOT NULL REFERENCES insurance_products(id) ON DELETE RESTRICT,
    version               integer NOT NULL,
    branch                insurance_branch NOT NULL,
    base_rate             numeric(9,6) NOT NULL,
    risk_sensitivity      numeric(9,6) NOT NULL,
    max_acceptable_risk   smallint NOT NULL,
    min_insured_value     numeric(14,2) NOT NULL,
    max_insured_value     numeric(14,2) NOT NULL,
    max_vehicle_age       smallint,
    coverage_cap          money_amount NOT NULL,
    questionnaire_schema  jsonb NOT NULL,   -- JSONB-JUSTIFICATION: o questionário muda a cada
                                            -- versão de produto e não participa de FK
    published_at          timestamptz,
    valid_period          daterange NOT NULL,
    CONSTRAINT ux_product_versions UNIQUE (product_id, version),
    CONSTRAINT ck_product_rates CHECK (base_rate > 0 AND risk_sensitivity >= 0),
    CONSTRAINT ck_product_risk CHECK (max_acceptable_risk BETWEEN 0 AND 1000),
    CONSTRAINT ck_product_values CHECK (min_insured_value > 0
                                    AND max_insured_value > min_insured_value),
    CONSTRAINT ck_product_schema CHECK (jsonb_typeof(questionnaire_schema) = 'object'),
    -- Não pode haver duas versões vigentes do mesmo produto no mesmo período
    EXCLUDE USING gist (product_id WITH =, valid_period WITH &&)
        WHERE (published_at IS NOT NULL)
);

CREATE INDEX ix_product_versions_active ON product_versions (product_id, valid_period)
    WHERE published_at IS NOT NULL;

-- ---------------------------------------------------------------- COVERAGES
CREATE TABLE coverages (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_version_id uuid NOT NULL REFERENCES product_versions(id) ON DELETE CASCADE,
    code               varchar(40) NOT NULL,
    name               varchar(120) NOT NULL,
    description        text,
    is_mandatory       boolean NOT NULL DEFAULT false,
    min_limit          money_amount NOT NULL,
    max_limit          money_amount NOT NULL,
    default_deductible deductible NOT NULL,
    rate_factor        numeric(9,6) NOT NULL,
    CONSTRAINT ux_coverages_code UNIQUE (product_version_id, code),
    CONSTRAINT ck_coverages_limits CHECK (
        (min_limit).amount > 0 AND (max_limit).amount >= (min_limit).amount),
    CONSTRAINT ck_coverages_rate CHECK (rate_factor > 0),
    CONSTRAINT ck_coverages_deductible CHECK (
        (default_deductible).kind IN ('FIXED','PERCENTAGE')
        AND ((default_deductible).kind <> 'FIXED' OR (default_deductible).amount >= 0)
        AND ((default_deductible).kind <> 'PERCENTAGE'
             OR ((default_deductible).percent > 0 AND (default_deductible).percent <= 1)))
);

CREATE INDEX ix_coverages_product ON coverages (product_version_id);

CREATE TABLE assistances (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_version_id uuid NOT NULL REFERENCES product_versions(id) ON DELETE CASCADE,
    code               varchar(40) NOT NULL,
    name               varchar(120) NOT NULL,
    available_in_plans plan_tier[] NOT NULL DEFAULT '{}',
    monthly_cost       money_amount NOT NULL,
    CONSTRAINT ux_assistances_code UNIQUE (product_version_id, code),
    CONSTRAINT ck_assistances_cost CHECK ((monthly_cost).amount >= 0)
);

-- ---------------------------------------------------------------- ELIGIBILITY
CREATE TABLE eligibility_rules (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_version_id uuid NOT NULL REFERENCES product_versions(id) ON DELETE CASCADE,
    code               varchar(40) NOT NULL,
    description        varchar(200) NOT NULL,
    expression         jsonb NOT NULL,  -- JSONB-JUSTIFICATION: árvore de Specification,
                                        -- cuja forma varia por regra
    rejection_message  varchar(200) NOT NULL,
    CONSTRAINT ux_eligibility_code UNIQUE (product_version_id, code)
);

-- ---------------------------------------------------------------- QUOTATIONS
CREATE TABLE quotations (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id          uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    broker_id          uuid NOT NULL REFERENCES brokers(id) ON DELETE RESTRICT,
    customer_id        uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
    asset_id           uuid NOT NULL REFERENCES insurable_assets(id) ON DELETE RESTRICT,
    product_version_id uuid NOT NULL REFERENCES product_versions(id) ON DELETE RESTRICT,
    previous_policy_id uuid,   -- FK adicionada em V005, após a criação de policies
    number             varchar(24) NOT NULL,
    status             quotation_status NOT NULL DEFAULT 'DRAFT',
    risk_score         smallint NOT NULL,
    -- Faixa DERIVADA: não existe estado em que escore e faixa divirjam
    risk_band          risk_band GENERATED ALWAYS AS (app.risk_band_of(risk_score)) STORED,
    rejection_reasons  text[],
    created_at         timestamptz NOT NULL DEFAULT now(),
    created_by         uuid NOT NULL,
    expires_at         timestamptz NOT NULL,
    deleted_at         timestamptz,
    deleted_by         uuid,
    deletion_reason    text,
    deletion_batch_id  uuid,
    CONSTRAINT ck_quotations_risk CHECK (risk_score BETWEEN 0 AND 1000),
    CONSTRAINT ck_quotations_expiry CHECK (expires_at > created_at),
    CONSTRAINT ck_quotations_rejection CHECK (
        (status = 'REJECTED') = (rejection_reasons IS NOT NULL
                                 AND cardinality(rejection_reasons) > 0))
);

CREATE UNIQUE INDEX ux_quotations_tenant_number ON quotations (tenant_id, number);
CREATE INDEX ix_quotations_customer ON quotations (tenant_id, customer_id, created_at DESC)
    WHERE deleted_at IS NULL;
CREATE INDEX ix_quotations_broker ON quotations (tenant_id, broker_id, status)
    WHERE deleted_at IS NULL;
-- Índice PARCIAL para o Quotation Expirer: indexa só o que ainda pode expirar
CREATE INDEX ix_quotations_expiring ON quotations (expires_at)
    WHERE status IN ('DRAFT','CALCULATED') AND deleted_at IS NULL;

CREATE TABLE risk_profiles (
    quotation_id   uuid PRIMARY KEY REFERENCES quotations(id) ON DELETE CASCADE,
    answers        jsonb NOT NULL,  -- JSONB-JUSTIFICATION: o esquema do questionário varia
                                    -- por versão de produto; validado contra JSON Schema
    schema_version varchar(20) NOT NULL,
    computed_score smallint NOT NULL,
    computed_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_risk_answers_object CHECK (jsonb_typeof(answers) = 'object'),
    CONSTRAINT ck_risk_score_range CHECK (computed_score BETWEEN 0 AND 1000)
);

CREATE INDEX ix_risk_profiles_answers ON risk_profiles USING gin (answers jsonb_path_ops);

CREATE TABLE quotation_items (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    quotation_id   uuid NOT NULL REFERENCES quotations(id) ON DELETE CASCADE,
    plan           plan_tier NOT NULL,
    net_premium    money_amount NOT NULL,
    total_premium  money_amount NOT NULL,
    CONSTRAINT ux_quotation_items_plan UNIQUE (quotation_id, plan),
    CONSTRAINT ck_quotation_items_premium CHECK (
        (net_premium).amount > 0
        AND (total_premium).amount >= (net_premium).amount)
);

CREATE TABLE selected_coverages (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    quotation_item_id uuid NOT NULL REFERENCES quotation_items(id) ON DELETE CASCADE,
    coverage_id       uuid NOT NULL REFERENCES coverages(id) ON DELETE RESTRICT,
    limit_amount      money_amount NOT NULL,
    deductible        deductible NOT NULL,
    premium           money_amount NOT NULL,
    CONSTRAINT ux_selected_coverage UNIQUE (quotation_item_id, coverage_id),
    CONSTRAINT ck_selected_limit CHECK ((limit_amount).amount > 0),
    CONSTRAINT ck_selected_premium CHECK ((premium).amount >= 0)
);

-- ---------------------------------------------------------------- CALCULATION SNAPSHOT
CREATE TABLE calculation_snapshots (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    quotation_item_id uuid NOT NULL REFERENCES quotation_items(id) ON DELETE CASCADE,
    engine_version    varchar(20) NOT NULL,
    inputs            jsonb NOT NULL,  -- JSONB-JUSTIFICATION: os fatores de risco variam
                                       -- conforme o tipo de bem (polimorfismo)
    risk_multiplier   numeric(12,6) NOT NULL,
    plan_multiplier   numeric(12,6) NOT NULL,
    base_premium      money_amount NOT NULL,
    final_premium     money_amount NOT NULL,
    calculated_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_snapshot_item UNIQUE (quotation_item_id),
    CONSTRAINT ck_snapshot_multipliers CHECK (risk_multiplier > 0 AND plan_multiplier > 0)
);

COMMENT ON TABLE calculation_snapshots IS
    'IMUTÁVEL (trigger em V008). Registra entradas, fatores, fórmula e versão do motor, '
    'permitindo reproduzir o cálculo campo a campo anos depois. Sem isso, é impossível '
    'responder a um questionamento sobre um prêmio ofertado no passado.';
