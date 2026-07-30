# Modelo físico — PostgreSQL 16

Este é o entregável central do case. O objetivo é demonstrar um banco **objeto-relacional**
correto: normalizado, íntegro, seguro, performático, observável e fielmente integrado aos objetos
de domínio.

## 1. Por que PostgreSQL

| Recurso | Uso concreto no NexusBroker |
|---|---|
| **ACID** | Emissão de apólice confirma proposta, apólice, coberturas, parcelas, comissão, evento e auditoria em uma transação |
| **Integridade referencial** | FKs declaradas com `ON DELETE` explícito; teste tenta inserir órfão por SQL direto e falha |
| **Constraints** | `CHECK`, `UNIQUE` parcial e `EXCLUDE` replicam invariantes do domínio na última linha de defesa |
| **Tipos compostos** | `money_amount`, `postal_address`, `deductible` — mapeiam Value Objects sem espalhar colunas soltas |
| **Domains** | `cpf_digits`, `cnpj_digits`, `uf_code` — validação reutilizável por tipo |
| **Enums** | Status de apólice, proposta, cotação e sinistro tipados no banco |
| **Arrays** | `visible_fields text[]` na auditoria regulatória; tags de produto |
| **JSONB** | Somente onde o atributo é **genuinamente variável**: respostas de questionário, payload de evento, plano de execução |
| **`daterange` + `btree_gist`** | `EXCLUDE` impede sobreposição de vigência — invariante impossível de expressar com `UNIQUE` |
| **Índices** | B-tree, compostos, parciais, GIN (JSONB e full-text), `pg_trgm` para busca aproximada |
| **RLS** | Isolamento multi-tenant na camada mais profunda, aplicado inclusive ao dono da tabela (`FORCE`) |
| **Particionamento** | `audit_events`, `security_events`, `outbox_messages` por mês |
| **FTS** | `tsvector` + GIN para busca de cliente por nome |
| **`xmin`** | Optimistic locking nativo, sem coluna extra de versão |
| **`SKIP LOCKED`** | Outbox consumida por múltiplos workers sem contenção |
| **`EXPLAIN (ANALYZE, BUFFERS)`** | Alimenta o Query Inspector com plano **real**, não estimado |
| **Extensibilidade** | `pgcrypto`, `pg_trgm`, `btree_gist`, `pg_stat_statements` |

**Alternativas descartadas:** MySQL (sem RLS nativo, sem tipos compostos, `EXCLUDE` inexistente —
inviabilizaria metade das demonstrações); SQL Server (RLS existe, mas licenciamento atrapalha um
case aberto em containers); MongoDB (o domínio é intensamente relacional e transacional; trocar
integridade referencial por flexibilidade seria a decisão errada aqui).

## 2. O que vai onde — a decisão de modelagem

| Camada | Conteúdo | Exemplo |
|---|---|---|
| **Relacional normalizado** | Toda entidade com identidade, ciclo de vida ou necessidade de integridade referencial | `policies`, `policy_coverages`, `installments`, `commissions` |
| **Tipo composto** | Value Object multi-campo reutilizado em várias tabelas | `money_amount`, `postal_address` |
| **Domain** | Value Object de campo único com validação reutilizável | `cpf_digits`, `uf_code` |
| **Coluna com conversor** | VO de campo único específico do agregado | `policy_number`, `risk_score` |
| **JSONB** | Estrutura **genuinamente variável**, que muda por versão de produto e não é consultada relacionalmente | `risk_profiles.answers`, `domain_events.payload` |
| **Coluna gerada** | Derivação determinística que precisa de índice | `risk_band`, `search_vector` |

### Critério para usar JSONB (e o teste que impede o abuso)

JSONB é permitido **apenas** quando as três condições valem:

1. O esquema varia legitimamente entre instâncias (o questionário de risco de auto tem campos
   diferentes do de residencial, e muda a cada versão do produto).
2. O dado **não** participa de integridade referencial.
3. Consultas sobre ele são exploratórias ou por chave, não junções relacionais frequentes.

**Onde JSONB seria errado e não é usado:** coberturas (têm identidade, FK e são consultadas em
join — vão para `policy_coverages`); parcelas (têm estado, valor e ciclo de vida); comissões
(precisam de integridade e agregação). Um teste arquitetural verifica que nenhuma coluna JSONB nova
seja adicionada sem um comentário `-- JSONB-JUSTIFICATION:` na migration.

Mesmo o JSONB usado é **validado**: `risk_profiles.answers` é verificado contra o JSON Schema da
versão do produto, tanto na aplicação quanto por `CHECK` com função de validação.

## 3. Extensões, domains, enums e tipos compostos

```sql
CREATE EXTENSION IF NOT EXISTS pgcrypto;      -- gen_random_uuid, digest
CREATE EXTENSION IF NOT EXISTS btree_gist;    -- EXCLUDE com uuid + daterange
CREATE EXTENSION IF NOT EXISTS pg_trgm;       -- busca aproximada por nome
CREATE EXTENSION IF NOT EXISTS citext;        -- e-mail case-insensitive
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

-- ---------- Domains: validação reutilizável ----------
CREATE DOMAIN cpf_digits  AS char(11) CHECK (VALUE ~ '^[0-9]{11}$');
CREATE DOMAIN cnpj_digits AS char(14) CHECK (VALUE ~ '^[0-9]{14}$');
CREATE DOMAIN uf_code     AS char(2)  CHECK (VALUE ~ '^[A-Z]{2}$');
CREATE DOMAIN postal_code AS char(8)  CHECK (VALUE ~ '^[0-9]{8}$');
CREATE DOMAIN currency_code AS char(3) CHECK (VALUE ~ '^[A-Z]{3}$');

-- ---------- Tipos compostos: Value Objects ----------
CREATE TYPE money_amount AS (
    amount   numeric(14,2),
    currency currency_code
);

CREATE TYPE postal_address AS (
    street       varchar(160),
    number       varchar(20),
    complement   varchar(60),
    district     varchar(80),
    city         varchar(80),
    state        uf_code,
    postal_code  postal_code
);

CREATE TYPE deductible AS (
    kind    varchar(12),          -- 'FIXED' | 'PERCENTAGE'
    amount  numeric(14,2),
    percent numeric(6,4)
);

-- ---------- Enums ----------
CREATE TYPE customer_status   AS ENUM ('ACTIVE','INACTIVE','BLOCKED');
CREATE TYPE customer_kind     AS ENUM ('INDIVIDUAL','BUSINESS');
CREATE TYPE asset_kind        AS ENUM ('VEHICLE','PROPERTY');
CREATE TYPE quotation_status  AS ENUM ('DRAFT','CALCULATED','REJECTED','CONVERTED','EXPIRED');
CREATE TYPE proposal_status   AS ENUM ('DRAFT','SUBMITTED','UNDER_ANALYSIS','PENDING',
                                       'APPROVED','REJECTED','ISSUED','EXPIRED');
CREATE TYPE policy_status     AS ENUM ('ACTIVE','CANCELLED','EXPIRED','RENEWED');
CREATE TYPE installment_status AS ENUM ('PENDING','PAID','OVERDUE','CANCELLED');
CREATE TYPE commission_status AS ENUM ('FORECAST','RELEASED','PAID','REVERSED');
CREATE TYPE claim_status      AS ENUM ('REPORTED','UNDER_ANALYSIS','PENDING','APPROVED',
                                       'DENIED','SETTLED','CLOSED');
CREATE TYPE user_profile      AS ENUM ('BROKER','REGULATOR');
CREATE TYPE access_purpose    AS ENUM ('REGULATORY_SUPERVISION','COMPLIANCE_VERIFICATION',
                                       'INCONSISTENCY_INVESTIGATION','INDICATOR_ANALYSIS');
```

**Por que enum e não tabela de domínio?** Esses conjuntos são **fechados pelo código** — adicionar
um status exige mudança de lógica de transição, então deve exigir migration. Para conjuntos que o
negócio edita sem deploy (motivos de cancelamento, tipos de pendência), usa-se tabela de
referência. A distinção é: *enum quando o código conhece cada valor; tabela quando não conhece.*

## 4. Tenancy e o contrato de RLS

```sql
CREATE TABLE brokerages (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    legal_name        varchar(180) NOT NULL,
    trade_name        varchar(180) NOT NULL,
    document          cnpj_digits  NOT NULL,
    susep_registration varchar(20) NOT NULL,   -- fictício
    status            varchar(16)  NOT NULL DEFAULT 'ACTIVE',
    created_at        timestamptz  NOT NULL DEFAULT now(),
    updated_at        timestamptz,
    deleted_at        timestamptz,
    CONSTRAINT ux_brokerages_document CHECK (document IS NOT NULL)
);
CREATE UNIQUE INDEX ux_brokerages_document_active
    ON brokerages (document) WHERE deleted_at IS NULL;

-- A corretora É o tenant: tenant_id nas demais tabelas referencia brokerages.id
```

Toda tabela de negócio carrega `tenant_id uuid NOT NULL REFERENCES brokerages(id)` e ativa RLS:

```sql
-- Função central: lê o tenant do contexto da sessão, definido por SET LOCAL na conexão
CREATE OR REPLACE FUNCTION app.current_tenant() RETURNS uuid
LANGUAGE sql STABLE AS $$
    SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid
$$;

CREATE OR REPLACE FUNCTION app.current_profile() RETURNS text
LANGUAGE sql STABLE AS $$
    SELECT COALESCE(NULLIF(current_setting('app.user_profile', true), ''), 'NONE')
$$;

ALTER TABLE customers ENABLE ROW LEVEL SECURITY;
ALTER TABLE customers FORCE  ROW LEVEL SECURITY;   -- aplica inclusive ao dono da tabela

-- Corretor: acesso total ao próprio tenant
CREATE POLICY p_customers_tenant_isolation ON customers
    FOR ALL TO app_user
    USING      (tenant_id = app.current_tenant())
    WITH CHECK (tenant_id = app.current_tenant());

-- Regulador: leitura multi-tenant, restrita ao escopo autorizado, e SOMENTE leitura
CREATE POLICY p_customers_regulatory_read ON customers
    FOR SELECT TO app_regulator
    USING (
        app.current_profile() = 'REGULATOR'
        AND tenant_id IN (SELECT tenant_id FROM app.regulatory_scope_current())
    );
```

**`FORCE ROW LEVEL SECURITY` é essencial.** Sem ele, o usuário dono da tabela ignora as políticas —
e é exatamente esse detalhe que transforma "temos RLS" em falsa sensação de segurança. O teste de
isolamento verifica que `FORCE` está ativo em **todas** as tabelas multi-tenant.

**Papéis de banco (menor privilégio):**

```sql
CREATE ROLE app_migrator LOGIN;   -- DDL; usado só pelas migrations
CREATE ROLE app_user     LOGIN;   -- DML no tenant; SEM DDL, SEM BYPASSRLS
CREATE ROLE app_regulator LOGIN;  -- SELECT em views mascaradas; sem acesso às tabelas base
CREATE ROLE app_worker   LOGIN;   -- Outbox e jobs; escopo restrito

REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO app_user;
REVOKE UPDATE, DELETE ON audit_events, security_events FROM app_user;  -- append-only real
GRANT SELECT ON ALL TABLES IN SCHEMA regulatory TO app_regulator;
```

A imutabilidade da auditoria não é convenção: o `UPDATE`/`DELETE` está **revogado**. Nem a
aplicação consegue adulterar a trilha.

## 5. Tabelas principais

### 5.1 Customers — herança TPH

```sql
CREATE TABLE customers (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    kind           customer_kind NOT NULL,              -- discriminador TPH
    status         customer_status NOT NULL DEFAULT 'ACTIVE',

    document       varchar(14) NOT NULL,                -- cifrado em repouso (pgcrypto)
    document_hash  bytea       NOT NULL,                -- HMAC com pepper, para busca/unicidade

    -- Campos de pessoa física (NULL para PJ)
    first_name     varchar(80),
    last_name      varchar(120),
    birth_date     date,

    -- Campos de pessoa jurídica (NULL para PF)
    legal_name     varchar(180),
    trade_name     varchar(180),
    cnae_code      varchar(10),

    search_vector  tsvector GENERATED ALWAYS AS (
        to_tsvector('portuguese',
            coalesce(first_name,'') || ' ' || coalesce(last_name,'') || ' ' ||
            coalesce(legal_name,'') || ' ' || coalesce(trade_name,''))
    ) STORED,

    created_at     timestamptz NOT NULL DEFAULT now(),
    created_by     uuid NOT NULL,
    updated_at     timestamptz,
    updated_by     uuid,
    deleted_at     timestamptz,

    -- Invariante do domínio replicada: campos coerentes com o discriminador
    CONSTRAINT ck_customers_individual_fields CHECK (
        kind <> 'INDIVIDUAL' OR
        (first_name IS NOT NULL AND last_name IS NOT NULL AND birth_date IS NOT NULL
         AND legal_name IS NULL AND trade_name IS NULL)
    ),
    CONSTRAINT ck_customers_business_fields CHECK (
        kind <> 'BUSINESS' OR
        (legal_name IS NOT NULL
         AND first_name IS NULL AND last_name IS NULL AND birth_date IS NULL)
    ),
    CONSTRAINT ck_customers_birth_date_past CHECK (birth_date IS NULL OR birth_date < CURRENT_DATE)
);

-- Unicidade do documento POR TENANT (não global: a mesma pessoa pode ser cliente de duas corretoras)
CREATE UNIQUE INDEX ux_customers_tenant_document
    ON customers (tenant_id, document_hash) WHERE deleted_at IS NULL;

CREATE INDEX ix_customers_search       ON customers USING gin (search_vector);
CREATE INDEX ix_customers_name_trgm    ON customers USING gin (
    (coalesce(first_name,'') || ' ' || coalesce(last_name,'') || ' ' || coalesce(legal_name,''))
    gin_trgm_ops);
CREATE INDEX ix_customers_tenant_status ON customers (tenant_id, status) WHERE deleted_at IS NULL;
```

O `CHECK` com discriminador é o que impede o problema clássico do TPH: uma linha `INDIVIDUAL` com
`legal_name` preenchido. A herança do C# fica **realmente** garantida no banco.

### 5.2 Insurable assets — herança TPT

```sql
CREATE TABLE insurable_assets (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      uuid NOT NULL REFERENCES brokerages(id),
    customer_id    uuid NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
    kind           asset_kind NOT NULL,
    declared_value money_amount NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    deleted_at     timestamptz,
    CONSTRAINT ck_assets_value_positive CHECK ((declared_value).amount > 0),
    CONSTRAINT ux_assets_kind UNIQUE (id, kind)          -- permite FK composta nas filhas
);

CREATE TABLE vehicles (
    id            uuid PRIMARY KEY,
    kind          asset_kind NOT NULL DEFAULT 'VEHICLE' CHECK (kind = 'VEHICLE'),
    plate         varchar(7)  NOT NULL,
    chassis       varchar(17) NOT NULL,
    model_year    smallint    NOT NULL,
    manufacture_year smallint NOT NULL,
    brand         varchar(60) NOT NULL,
    model         varchar(80) NOT NULL,
    usage         varchar(20) NOT NULL,
    overnight_postal_code postal_code NOT NULL,
    FOREIGN KEY (id, kind) REFERENCES insurable_assets (id, kind) ON DELETE CASCADE,
    CONSTRAINT ck_vehicles_plate  CHECK (plate ~ '^([A-Z]{3}[0-9]{4}|[A-Z]{3}[0-9][A-Z][0-9]{2})$'),
    CONSTRAINT ck_vehicles_years  CHECK (model_year >= manufacture_year
                                     AND model_year BETWEEN 1950 AND EXTRACT(YEAR FROM now()) + 1)
);

CREATE TABLE properties (
    id             uuid PRIMARY KEY,
    kind           asset_kind NOT NULL DEFAULT 'PROPERTY' CHECK (kind = 'PROPERTY'),
    location       postal_address NOT NULL,
    area_sqm       numeric(10,2) NOT NULL CHECK (area_sqm > 0),
    built_year     smallint NOT NULL,
    construction_type varchar(30) NOT NULL,
    property_usage varchar(20) NOT NULL,
    FOREIGN KEY (id, kind) REFERENCES insurable_assets (id, kind) ON DELETE CASCADE
);

CREATE UNIQUE INDEX ux_vehicles_tenant_plate ON vehicles (plate);
CREATE UNIQUE INDEX ux_vehicles_chassis      ON vehicles (chassis);
```

A **FK composta `(id, kind)`** é o detalhe que faz o TPT ser correto: impede que uma linha de
`insurable_assets` marcada como `VEHICLE` tenha um registro filho em `properties`. É a herança
do modelo OO preservada pela integridade referencial.

### 5.3 Quotations

```sql
CREATE TABLE quotations (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id          uuid NOT NULL REFERENCES brokerages(id),
    broker_id          uuid NOT NULL REFERENCES brokers(id),
    customer_id        uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
    asset_id           uuid NOT NULL REFERENCES insurable_assets(id) ON DELETE RESTRICT,
    product_version_id uuid NOT NULL REFERENCES product_versions(id),
    previous_policy_id uuid REFERENCES policies(id),      -- renovação
    number             varchar(24) NOT NULL,
    status             quotation_status NOT NULL DEFAULT 'DRAFT',
    risk_score         smallint NOT NULL CHECK (risk_score BETWEEN 0 AND 1000),
    risk_band          varchar(10) GENERATED ALWAYS AS (
        CASE WHEN risk_score <= 250 THEN 'LOW'
             WHEN risk_score <= 550 THEN 'MODERATE'
             WHEN risk_score <= 800 THEN 'HIGH'
             ELSE 'SEVERE' END) STORED,
    rejection_reasons  text[],
    created_at         timestamptz NOT NULL DEFAULT now(),
    expires_at         timestamptz NOT NULL,
    CONSTRAINT ck_quotations_expiry CHECK (expires_at > created_at)
);

CREATE UNIQUE INDEX ux_quotations_tenant_number ON quotations (tenant_id, number);
CREATE INDEX ix_quotations_customer  ON quotations (tenant_id, customer_id, created_at DESC);
-- Índice PARCIAL: o worker de expiração só olha o que ainda pode expirar
CREATE INDEX ix_quotations_expiring  ON quotations (expires_at)
    WHERE status IN ('DRAFT','CALCULATED');

CREATE TABLE risk_profiles (
    quotation_id uuid PRIMARY KEY REFERENCES quotations(id) ON DELETE CASCADE,
    answers      jsonb NOT NULL,     -- JSONB-JUSTIFICATION: esquema varia por versão de produto
    schema_version varchar(20) NOT NULL,
    computed_score smallint NOT NULL,
    CONSTRAINT ck_risk_answers_object CHECK (jsonb_typeof(answers) = 'object')
);
CREATE INDEX ix_risk_profiles_answers ON risk_profiles USING gin (answers jsonb_path_ops);

CREATE TABLE calculation_snapshots (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    quotation_item_id uuid NOT NULL REFERENCES quotation_items(id) ON DELETE CASCADE,
    engine_version    varchar(20) NOT NULL,
    inputs            jsonb NOT NULL,   -- JSONB-JUSTIFICATION: fatores variam por tipo de bem
    risk_multiplier   numeric(10,6) NOT NULL,
    plan_multiplier   numeric(10,6) NOT NULL,
    base_premium      money_amount NOT NULL,
    final_premium     money_amount NOT NULL,
    calculated_at     timestamptz NOT NULL DEFAULT now()
);
-- Snapshot é imutável: trigger bloqueia UPDATE (justificada — ver §9)
```

### 5.4 Proposals e Policies — o núcleo das invariantes

```sql
CREATE TABLE proposals (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       uuid NOT NULL REFERENCES brokerages(id),
    quotation_id    uuid NOT NULL REFERENCES quotations(id) ON DELETE RESTRICT,
    broker_id       uuid NOT NULL REFERENCES brokers(id),
    number          varchar(24) NOT NULL,
    status          proposal_status NOT NULL DEFAULT 'DRAFT',
    chosen_plan     varchar(20) NOT NULL,
    total_premium   money_amount NOT NULL,
    idempotency_key varchar(64),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz,
    CONSTRAINT ck_proposals_premium CHECK ((total_premium).amount > 0)
);

CREATE UNIQUE INDEX ux_proposals_tenant_number ON proposals (tenant_id, number);
-- INVARIANTE: uma cotação gera no máximo UMA proposta viva
CREATE UNIQUE INDEX ux_proposals_quotation_active ON proposals (quotation_id)
    WHERE status NOT IN ('REJECTED','EXPIRED');

CREATE TABLE policies (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       uuid NOT NULL REFERENCES brokerages(id),
    proposal_id     uuid NOT NULL REFERENCES proposals(id) ON DELETE RESTRICT,
    broker_id       uuid NOT NULL REFERENCES brokers(id),
    customer_id     uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
    asset_id        uuid NOT NULL REFERENCES insurable_assets(id) ON DELETE RESTRICT,
    product_version_id uuid NOT NULL REFERENCES product_versions(id),
    number          varchar(24) NOT NULL,
    status          policy_status NOT NULL DEFAULT 'ACTIVE',
    coverage_period daterange NOT NULL,
    net_premium     money_amount NOT NULL,
    total_premium   money_amount NOT NULL,
    issued_at       timestamptz NOT NULL DEFAULT now(),
    cancelled_at    timestamptz,
    cancellation_reason varchar(60),
    created_by      uuid NOT NULL,

    CONSTRAINT ck_policies_premium_positive CHECK ((total_premium).amount > 0),
    CONSTRAINT ck_policies_premium_currency CHECK ((total_premium).currency = 'BRL'),
    CONSTRAINT ck_policies_net_le_total     CHECK ((net_premium).amount <= (total_premium).amount),
    CONSTRAINT ck_policies_period_valid     CHECK (NOT isempty(coverage_period)),
    CONSTRAINT ck_policies_cancel_coherent  CHECK (
        (status = 'CANCELLED') = (cancelled_at IS NOT NULL))
);

-- INVARIANTE 1: exatamente uma apólice viva por proposta (bloqueia emissão duplicada)
CREATE UNIQUE INDEX ux_policies_proposal ON policies (proposal_id)
    WHERE status <> 'CANCELLED';

CREATE UNIQUE INDEX ux_policies_tenant_number ON policies (tenant_id, number);

-- INVARIANTE 2: vigências não se sobrepõem para o mesmo bem/produto
-- Impossível de expressar com UNIQUE; exige constraint de exclusão com btree_gist
ALTER TABLE policies ADD CONSTRAINT ex_policies_no_overlap
    EXCLUDE USING gist (
        tenant_id       WITH =,
        asset_id        WITH =,
        product_version_id WITH =,
        coverage_period WITH &&
    ) WHERE (status = 'ACTIVE');

CREATE INDEX ix_policies_broker_status ON policies (tenant_id, broker_id, status);
CREATE INDEX ix_policies_customer      ON policies (tenant_id, customer_id);
-- Índice PARCIAL para o worker de renovação: indexa só o que está ativo
CREATE INDEX ix_policies_expiring ON policies (upper(coverage_period))
    WHERE status = 'ACTIVE';

CREATE TABLE policy_coverages (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    policy_id    uuid NOT NULL REFERENCES policies(id) ON DELETE CASCADE,
    coverage_id  uuid NOT NULL REFERENCES coverages(id),
    limit_amount money_amount NOT NULL,
    deductible   deductible   NOT NULL,
    premium      money_amount NOT NULL,
    is_mandatory boolean NOT NULL,
    CONSTRAINT ck_policy_coverages_limit   CHECK ((limit_amount).amount > 0),
    CONSTRAINT ck_policy_coverages_premium CHECK ((premium).amount >= 0),
    CONSTRAINT ux_policy_coverage UNIQUE (policy_id, coverage_id)
);
```

Três camadas independentes impedem a emissão duplicada: a invariante no agregado `Policy`, o
índice `ux_policies_proposal` e a `Idempotency-Key`. O Security Lab derruba uma de cada vez para
mostrar que as demais seguram — é a demonstração de defesa em profundidade aplicada a
integridade, não só a segurança.

### 5.5 Billing e Commissions

```sql
CREATE TABLE installment_plans (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     uuid NOT NULL REFERENCES brokerages(id),
    policy_id     uuid NOT NULL REFERENCES policies(id) ON DELETE CASCADE,
    total_amount  money_amount NOT NULL,
    installment_count smallint NOT NULL CHECK (installment_count BETWEEN 1 AND 12),
    CONSTRAINT ux_installment_plans_policy UNIQUE (policy_id)
);

CREATE TABLE installments (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   uuid NOT NULL REFERENCES brokerages(id),
    plan_id     uuid NOT NULL REFERENCES installment_plans(id) ON DELETE CASCADE,
    sequence    smallint NOT NULL,
    amount      money_amount NOT NULL,
    due_date    date NOT NULL,
    status      installment_status NOT NULL DEFAULT 'PENDING',
    paid_at     timestamptz,
    CONSTRAINT ck_installments_amount CHECK ((amount).amount > 0),
    CONSTRAINT ck_installments_paid   CHECK ((status = 'PAID') = (paid_at IS NOT NULL)),
    CONSTRAINT ux_installments_plan_sequence UNIQUE (plan_id, sequence)
);

-- INVARIANTE FINANCEIRA verificada no banco: soma das parcelas = total do plano.
-- Constraint declarativa não alcança agregação entre linhas; usa-se trigger deferida (justificada).
CREATE OR REPLACE FUNCTION app.assert_installments_sum() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_plan uuid := COALESCE(NEW.plan_id, OLD.plan_id);
    v_sum  numeric(14,2);
    v_total numeric(14,2);
BEGIN
    SELECT sum((amount).amount) INTO v_sum   FROM installments      WHERE plan_id = v_plan;
    SELECT (total_amount).amount INTO v_total FROM installment_plans WHERE id = v_plan;

    IF v_sum IS DISTINCT FROM v_total THEN
        RAISE EXCEPTION 'Soma das parcelas (%) difere do total do plano (%)', v_sum, v_total
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER tg_installments_sum
    AFTER INSERT OR UPDATE OR DELETE ON installments
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION app.assert_installments_sum();

CREATE TABLE commission_rules (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id   uuid NOT NULL REFERENCES insurance_products(id),
    version      integer NOT NULL,
    rate         numeric(6,4) NOT NULL CHECK (rate > 0 AND rate <= 0.35),
    base_on      varchar(20) NOT NULL CHECK (base_on IN ('NET_PREMIUM','TOTAL_PREMIUM')),
    valid_period daterange NOT NULL,
    CONSTRAINT ux_commission_rules_version UNIQUE (product_id, version),
    -- Não pode haver duas regras vigentes para o mesmo produto no mesmo período
    EXCLUDE USING gist (product_id WITH =, valid_period WITH &&)
);

CREATE TABLE commissions (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id         uuid NOT NULL REFERENCES brokerages(id),
    policy_id         uuid NOT NULL REFERENCES policies(id) ON DELETE RESTRICT,
    broker_id         uuid NOT NULL REFERENCES brokers(id),
    rule_id           uuid NOT NULL REFERENCES commission_rules(id),
    rule_version      integer NOT NULL,
    rate_applied      numeric(6,4) NOT NULL,
    base_amount       money_amount NOT NULL,
    amount            money_amount NOT NULL,
    status            commission_status NOT NULL DEFAULT 'FORECAST',
    reversed_from_id  uuid REFERENCES commissions(id),   -- estorno = lançamento inverso
    created_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_commissions_amount CHECK (
        (status = 'REVERSED' AND (amount).amount <= 0) OR
        (status <> 'REVERSED' AND (amount).amount >= 0))
);

CREATE INDEX ix_commissions_broker ON commissions (tenant_id, broker_id, status, created_at DESC);
```

A comissão guarda `rule_id` **e** `rule_version` **e** `rate_applied` **e** `base_amount`. Ainda
que a regra mude amanhã, a pergunta "por que essa comissão foi esse valor?" continua respondível
sem arqueologia. É o requisito de rastreabilidade traduzido em colunas.

### 5.6 Auditoria e Outbox — tabelas particionadas

```sql
CREATE TABLE audit_events (
    id             uuid NOT NULL DEFAULT gen_random_uuid(),
    tenant_id      uuid,                       -- NULL para eventos multi-tenant do regulador
    occurred_at    timestamptz NOT NULL DEFAULT now(),
    correlation_id uuid NOT NULL,
    trace_id       varchar(32),
    actor_id       uuid NOT NULL,
    actor_profile  user_profile NOT NULL,
    action         varchar(60) NOT NULL,
    resource_type  varchar(60) NOT NULL,
    resource_id    uuid,
    outcome        varchar(20) NOT NULL,
    -- Campos específicos do acesso regulatório (RF-099)
    access_purpose access_purpose,
    justification  text,
    visible_fields text[],
    masked_fields  text[],
    duration_ms    integer,
    before_state   jsonb,     -- JSONB-JUSTIFICATION: forma varia por tipo de recurso auditado
    after_state    jsonb,
    PRIMARY KEY (id, occurred_at)
) PARTITION BY RANGE (occurred_at);

CREATE TABLE audit_events_2026_07 PARTITION OF audit_events
    FOR VALUES FROM ('2026-07-01') TO ('2026-08-01');
-- Partições futuras criadas por job agendado

CREATE INDEX ix_audit_correlation ON audit_events (correlation_id);
CREATE INDEX ix_audit_tenant_time  ON audit_events (tenant_id, occurred_at DESC);
CREATE INDEX ix_audit_actor        ON audit_events (actor_id, occurred_at DESC);

CREATE TABLE outbox_messages (
    id             uuid NOT NULL DEFAULT gen_random_uuid(),
    tenant_id      uuid NOT NULL,
    occurred_at    timestamptz NOT NULL DEFAULT now(),
    message_type   varchar(120) NOT NULL,
    payload        jsonb NOT NULL,    -- JSONB-JUSTIFICATION: contrato varia por tipo de evento
    correlation_id uuid NOT NULL,
    aggregate_type varchar(60) NOT NULL,
    aggregate_id   uuid NOT NULL,
    processed_at   timestamptz,
    attempts       smallint NOT NULL DEFAULT 0,
    last_error     text,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (id, occurred_at)
) PARTITION BY RANGE (occurred_at);

-- Índice PARCIAL: o dispatcher só enxerga o que está pendente.
-- Mantém o índice pequeno mesmo com milhões de mensagens já processadas.
CREATE INDEX ix_outbox_pending ON outbox_messages (next_attempt_at)
    WHERE processed_at IS NULL;

CREATE TABLE processed_messages (       -- idempotência do consumidor
    message_id   uuid PRIMARY KEY,
    consumer     varchar(120) NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE idempotency_keys (
    key          varchar(64) NOT NULL,
    tenant_id    uuid NOT NULL,
    endpoint     varchar(160) NOT NULL,
    request_hash bytea NOT NULL,
    response_status smallint,
    response_body jsonb,
    created_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, key, endpoint)
);
```

Consulta do dispatcher, com `SKIP LOCKED` para permitir múltiplos workers sem contenção:

```sql
WITH batch AS (
    SELECT id, occurred_at
    FROM outbox_messages
    WHERE processed_at IS NULL AND next_attempt_at <= now()
    ORDER BY occurred_at
    LIMIT 100
    FOR UPDATE SKIP LOCKED
)
UPDATE outbox_messages o
   SET attempts = o.attempts + 1
  FROM batch b
 WHERE o.id = b.id AND o.occurred_at = b.occurred_at
RETURNING o.*;
```

### 5.7 Segurança, IA e regulatório

```sql
CREATE TABLE security_events (
    id            uuid NOT NULL DEFAULT gen_random_uuid(),
    occurred_at   timestamptz NOT NULL DEFAULT now(),
    tenant_id     uuid,
    actor_id      uuid,
    event_type    varchar(60) NOT NULL,  -- TENANT_VIOLATION_ATTEMPT, AUTHZ_DENIED, SQLI_BLOCKED...
    severity      varchar(16) NOT NULL,
    source_ip     inet,
    correlation_id uuid,
    resource_type varchar(60),
    resource_id   uuid,
    control_triggered varchar(80),       -- qual controle bloqueou
    details       jsonb,                 -- JSONB-JUSTIFICATION: forma varia por tipo de evento
    PRIMARY KEY (id, occurred_at)
) PARTITION BY RANGE (occurred_at);

CREATE TABLE agent_executions (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      uuid NOT NULL REFERENCES brokerages(id),
    agent_id       uuid NOT NULL REFERENCES agents(id),
    actor_id       uuid NOT NULL,
    correlation_id uuid NOT NULL,
    input_redacted   text NOT NULL,
    output_redacted  text,
    tools_invoked  text[],
    tokens_input   integer,
    tokens_output  integer,
    duration_ms    integer,
    outcome        varchar(20) NOT NULL,
    guardrail_triggered varchar(80),
    created_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE regulatory_access_sessions (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    regulator_id  uuid NOT NULL REFERENCES susep_regulatory_users(id),
    purpose       access_purpose NOT NULL,
    justification text NOT NULL CHECK (length(justification) >= 20),
    scope_tenants uuid[] NOT NULL,
    opened_at     timestamptz NOT NULL DEFAULT now(),
    expires_at    timestamptz NOT NULL,
    CONSTRAINT ck_regulatory_session_ttl CHECK (expires_at > opened_at)
);
```

## 6. Views e materialized views

```sql
-- View mascarada: é a ÚNICA porta de entrada do perfil regulatório aos dados de cliente
CREATE VIEW regulatory.customers_masked AS
SELECT
    c.id,
    c.tenant_id,
    b.trade_name AS brokerage_name,
    c.kind,
    c.status,
    CASE c.kind
        WHEN 'INDIVIDUAL' THEN left(c.first_name, 1) || repeat('*', 5)
        ELSE left(c.legal_name, 3) || repeat('*', 5)
    END AS masked_name,
    '***.***.' || right(app.decrypt_document(c.document), 3) || '-**' AS masked_document,
    date_trunc('month', c.created_at) AS created_month     -- minimização temporal
FROM customers c
JOIN brokerages b ON b.id = c.tenant_id
WHERE c.deleted_at IS NULL;

-- Indicadores consolidados, com supressão de células pequenas (anti-reidentificação)
CREATE MATERIALIZED VIEW regulatory.brokerage_indicators AS
SELECT
    p.tenant_id,
    date_trunc('month', p.issued_at) AS reference_month,
    pv.product_id,
    count(*)                                   AS policies_issued,
    CASE WHEN count(*) >= 5
         THEN sum((p.total_premium).amount) END AS total_premium,  -- NULL se k < 5
    CASE WHEN count(*) >= 5
         THEN avg((p.total_premium).amount) END AS avg_premium,
    count(*) FILTER (WHERE p.status = 'CANCELLED') AS cancellations
FROM policies p
JOIN product_versions pv ON pv.id = p.product_version_id
GROUP BY 1, 2, 3;

CREATE UNIQUE INDEX ux_brokerage_indicators
    ON regulatory.brokerage_indicators (tenant_id, reference_month, product_id);
-- Índice único é pré-requisito do REFRESH CONCURRENTLY (não bloqueia leitura)
```

## 7. Estratégia de índices

| Tipo | Onde | Por quê |
|---|---|---|
| **B-tree composto** | `(tenant_id, broker_id, status)` | `tenant_id` **sempre primeiro**: toda query é filtrada por tenant, então é a coluna de maior seletividade prática |
| **Parcial** | `WHERE processed_at IS NULL`, `WHERE status = 'ACTIVE'` | O índice indexa só o subconjunto quente. A Outbox pode ter milhões de linhas processadas e um índice de centenas |
| **Único parcial** | `WHERE deleted_at IS NULL` | Permite soft delete sem quebrar unicidade — o registro apagado libera o documento |
| **GIN (tsvector)** | `customers.search_vector` | Busca textual por nome |
| **GIN (trigram)** | nome do cliente | Busca com erro de digitação |
| **GIN (jsonb_path_ops)** | `risk_profiles.answers` | Consulta por chave dentro do JSONB, menor que `jsonb_ops` |
| **GiST** | `coverage_period`, `valid_period` | Necessário para `EXCLUDE` com `&&` |
| **Descendente** | `created_at DESC` | Listagens são sempre "mais recente primeiro"; evita sort |

**Antipadrões evitados:** índice em coluna de baixa cardinalidade isolada; índice redundante com o
prefixo de um composto; índice em toda FK sem verificar padrão de acesso (cada índice custa em
escrita). O Query Inspector expõe índices não utilizados via `pg_stat_user_indexes` — indexação
é avaliada com medição real.

## 8. Concorrência: optimistic locking com `xmin`

```csharp
modelBuilder.Entity<Policy>()
    .Property<uint>("Version")
    .HasColumnName("xmin")
    .HasColumnType("xid")
    .ValueGeneratedOnAddOrUpdate()
    .IsConcurrencyToken();
```

`xmin` é uma coluna de sistema que o PostgreSQL já mantém — não custa espaço, não precisa de
trigger e não pode ser esquecida em um `UPDATE` manual. Uma coluna `version` própria seria
atualizável por engano; `xmin` não.

O `UPDATE` gerado inclui `WHERE id = @id AND xmin = @version`. Se outra transação alterou a linha,
zero linhas são afetadas, o EF Core lança `DbUpdateConcurrencyException` e a API responde `409`.

## 9. Triggers — apenas quando justificadas

Triggers são evitadas por padrão (lógica invisível, difícil de depurar e de testar). Três exceções,
cada uma porque a regra é **impossível ou insegura** de garantir apenas na aplicação:

| Trigger | Justificativa |
|---|---|
| `tg_installments_sum` | Invariante agregada entre linhas; nenhuma constraint declarativa a expressa |
| `tg_audit_immutable` | Bloqueia `UPDATE`/`DELETE` em auditoria mesmo se um `GRANT` for concedido por engano — dupla proteção |
| `tg_snapshot_immutable` | O `CalculationSnapshot` precisa ser reproduzível anos depois; imutabilidade garantida no banco |

Explicitamente **não** se usa trigger para: preencher `updated_at` (é responsabilidade do
`SaveChanges` interceptor, e a aplicação precisa saber o valor), calcular derivados (colunas
geradas fazem isso de forma transparente) ou aplicar regra de negócio (pertence ao domínio).

## 10. Backup, retenção e anonimização

| Aspecto | Estratégia |
|---|---|
| **Backup** | `pg_dump` lógico diário + WAL archiving contínuo para PITR |
| **Restore** | Script versionado com teste de restauração executado no CI mensalmente — backup não testado não é backup |
| **Retenção** | Dados operacionais: 5 anos (prazo regulatório do setor). Auditoria: 5 anos. Logs de aplicação: 90 dias. Partições antigas são desanexadas (`DETACH`) e arquivadas, não apagadas |
| **Anonimização** | Script que gera base de desenvolvimento: substitui documento, nome, e-mail e telefone por sintéticos **preservando o formato e a distribuição**; mantém integridade referencial |
| **Cifragem** | Documento cifrado em repouso com `pgcrypto`; chave fora do banco, injetada por Docker secret |

## 11. Como o ORM reconstrói os objetos

O EF Core materializa o agregado usando o **construtor privado** e escreve nos *backing fields*,
não nas propriedades — por isso o encapsulamento sobrevive à persistência:

```csharp
public sealed class PolicyConfiguration : IEntityTypeConfiguration<Policy>
{
    public void Configure(EntityTypeBuilder<Policy> builder)
    {
        builder.ToTable("policies");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
               .HasConversion(id => id.Value, v => new PolicyId(v));

        builder.Property(p => p.Number)
               .HasConversion(n => n.Value, v => PolicyNumber.Parse(v))
               .HasColumnName("number").HasMaxLength(24);

        // Value Object multi-campo → tipo composto do PostgreSQL
        builder.OwnsOne(p => p.TotalPremium, m =>
        {
            m.Property(x => x.Amount).HasColumnName("total_premium_amount");
            m.Property(x => x.Currency).HasColumnName("total_premium_currency");
        });

        builder.Property(p => p.Period)
               .HasConversion(new DateRangeToNpgsqlRangeConverter())
               .HasColumnName("coverage_period").HasColumnType("daterange");

        // Coleção privada: acesso por backing field preserva o encapsulamento
        builder.Metadata
               .FindNavigation(nameof(Policy.Coverages))!
               .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(p => p.Coverages)
               .WithOne().HasForeignKey("policy_id")
               .OnDelete(DeleteBehavior.Cascade);

        // Optimistic locking nativo
        builder.Property<uint>("Version").HasColumnName("xmin")
               .HasColumnType("xid").IsRowVersion();

        // Filtro global de tenant: 3ª camada da defesa em profundidade
        builder.HasQueryFilter(p =>
            p.TenantId == _tenantContext.Current && p.DeletedAt == null);

        builder.Ignore(p => p.DomainEvents);   // eventos vivem em memória, não no banco
    }
}
```

Pontos que o avaliador deve notar: os VOs não vazam para o banco como `string`/`decimal` soltos;
as coleções continuam privadas; o token de concorrência é nativo; e o filtro de tenant é
declarado no mapeamento, não lembrado a cada consulta.

## 12. Migrations

Ferramenta: **EF Core Migrations** para o esquema versionado pela aplicação, com scripts SQL puros
para o que o EF não expressa (RLS, tipos compostos, particionamento, funções, triggers,
constraints de exclusão) aplicados via `migrationBuilder.Sql()` a partir de arquivos versionados.

Cada migration tem: script `Up`, script `Down` testado (rollback real, não `throw`), e teste de
integração que aplica a cadeia inteira em base limpa via Testcontainers. Migration sem `Down`
funcional **falha o build**.

## 13. Massa sintética de referência

| Tabela | Volume | Observação |
|---|---|---|
| `brokerages` | 8 tenants | Permite demonstrar isolamento com vizinhos reais |
| `brokers` | 40 | 3 a 8 por corretora |
| `customers` | 25.000 | 70% PF, 30% PJ, distribuídos de forma desigual entre tenants (realista) |
| `insurable_assets` | 38.000 | Veículos e imóveis |
| `quotations` | 60.000 | Com distribuição de status realista |
| `proposals` | 22.000 | ~37% de conversão |
| `policies` | 14.000 | Vigências escalonadas, incluindo próximas do vencimento |
| `installments` | 84.000 | |
| `commissions` | 14.000 | |
| `claims` | 1.800 | |
| `audit_events` | 400.000 | Distribuídos por vários meses para exercitar o particionamento |

Volume escolhido para que a diferença entre "com índice" e "sem índice" seja **visível e medível**
no Engineering Lab. Com 500 linhas, tudo é rápido e a demonstração não prova nada. Geração
determinística por *seed* fixa: a mesma base é reproduzida por qualquer avaliador, o que torna os
benchmarks comparáveis.

Todos os CPFs/CNPJs são gerados com dígito verificador válido a partir de faixas reservadas para
teste, garantindo que **não colidem com documentos reais**.
