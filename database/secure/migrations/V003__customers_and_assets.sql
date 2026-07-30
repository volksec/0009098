-- =============================================================================
-- V003 — Clientes (herança TPH) e bens seguráveis (herança TPT)
--
-- Duas estratégias de herança no mesmo sistema, cada uma escolhida pelo perfil
-- de acesso e pela divergência de atributos. Justificativa em ADR-0005.
-- =============================================================================

-- ---------------------------------------------------------------- CUSTOMERS (TPH)
CREATE TABLE customers (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id         uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    broker_id         uuid NOT NULL REFERENCES brokers(id) ON DELETE RESTRICT,
    kind              customer_kind NOT NULL,           -- discriminador TPH
    status            customer_status NOT NULL DEFAULT 'ACTIVE',

    document_encrypted bytea NOT NULL,                  -- pgcrypto, chave fora do banco
    document_hash      bytea NOT NULL,                  -- HMAC com pepper: busca e unicidade

    -- Pessoa física (NULL para PJ)
    first_name        varchar(80),
    last_name         varchar(120),
    birth_date        date,
    occupation        varchar(120),

    -- Pessoa jurídica (NULL para PF)
    legal_name        varchar(180),
    trade_name        varchar(180),
    cnae_code         varchar(10),
    company_size      varchar(20),

    search_vector tsvector GENERATED ALWAYS AS (
        to_tsvector('portuguese',
            coalesce(first_name,'') || ' ' || coalesce(last_name,'') || ' ' ||
            coalesce(legal_name,'') || ' ' || coalesce(trade_name,''))
    ) STORED,

    created_at        timestamptz NOT NULL DEFAULT now(),
    created_by        uuid NOT NULL,
    updated_at        timestamptz,
    updated_by        uuid,
    deleted_at        timestamptz,
    deleted_by        uuid,
    deletion_reason   text,
    deletion_batch_id uuid,

    -- A herança do modelo OO garantida por CHECK: impede o problema clássico do TPH,
    -- que é uma linha INDIVIDUAL com legal_name preenchido.
    CONSTRAINT ck_customers_individual_fields CHECK (
        kind <> 'INDIVIDUAL' OR (
            first_name IS NOT NULL AND last_name IS NOT NULL AND birth_date IS NOT NULL
            AND legal_name IS NULL AND trade_name IS NULL AND cnae_code IS NULL)),
    CONSTRAINT ck_customers_business_fields CHECK (
        kind <> 'BUSINESS' OR (
            legal_name IS NOT NULL AND cnae_code IS NOT NULL
            AND first_name IS NULL AND last_name IS NULL AND birth_date IS NULL)),
    CONSTRAINT ck_customers_birth_date_past CHECK (birth_date IS NULL OR birth_date < CURRENT_DATE),
    CONSTRAINT ck_customers_deletion_coherent CHECK (
        (deleted_at IS NULL) = (deleted_by IS NULL)
        AND (deleted_at IS NULL) = (deletion_reason IS NULL))
);

-- Unicidade POR TENANT, não global: a mesma pessoa pode ser cliente de duas corretoras.
-- Parcial em deleted_at para que a exclusão lógica libere o documento (RF-131).
CREATE UNIQUE INDEX ux_customers_tenant_document
    ON customers (tenant_id, document_hash) WHERE deleted_at IS NULL;

CREATE INDEX ix_customers_search ON customers USING gin (search_vector);
CREATE INDEX ix_customers_name_trgm ON customers USING gin (
    (coalesce(first_name,'') || ' ' || coalesce(last_name,'') || ' ' || coalesce(legal_name,''))
    gin_trgm_ops);
-- tenant_id SEMPRE primeiro no índice composto: toda query é filtrada por tenant
CREATE INDEX ix_customers_tenant_status ON customers (tenant_id, status) WHERE deleted_at IS NULL;
CREATE INDEX ix_customers_broker ON customers (tenant_id, broker_id) WHERE deleted_at IS NULL;

COMMENT ON COLUMN customers.document_hash IS
    'HMAC-SHA256 do documento com pepper mantido FORA do banco. O espaço de CPFs é '
    'pequeno o bastante para força bruta, então um hash sem pepper vazado com o dump '
    'permitiria reverter os documentos.';

-- ---------------------------------------------------------------- CONTACTS
CREATE TABLE contacts (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
    kind        varchar(20) NOT NULL CHECK (kind IN ('PERSONAL','COMMERCIAL','EMERGENCY')),
    email       citext,
    phone       varchar(11),
    is_primary  boolean NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now(),
    deleted_at  timestamptz,
    CONSTRAINT ck_contacts_has_channel CHECK (email IS NOT NULL OR phone IS NOT NULL),
    CONSTRAINT ck_contacts_phone_format CHECK (phone IS NULL OR phone ~ '^[0-9]{10,11}$')
);

-- Invariante do agregado replicada: no máximo UM contato principal por tipo
CREATE UNIQUE INDEX ux_contacts_primary_per_kind
    ON contacts (customer_id, kind) WHERE is_primary AND deleted_at IS NULL;
CREATE INDEX ix_contacts_customer ON contacts (customer_id) WHERE deleted_at IS NULL;

-- ---------------------------------------------------------------- ADDRESSES
CREATE TABLE addresses (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id   uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
    kind        varchar(20) NOT NULL CHECK (kind IN ('RESIDENTIAL','COMMERCIAL','BILLING')),
    value       postal_address NOT NULL,          -- tipo composto = Value Object
    is_primary  boolean NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now(),
    deleted_at  timestamptz,
    CONSTRAINT ck_addresses_required CHECK (
        (value).street IS NOT NULL AND (value).number IS NOT NULL
        AND (value).city IS NOT NULL AND (value).state IS NOT NULL
        AND (value).postal_code IS NOT NULL)
);

CREATE UNIQUE INDEX ux_addresses_primary_per_kind
    ON addresses (customer_id, kind) WHERE is_primary AND deleted_at IS NULL;
CREATE INDEX ix_addresses_customer ON addresses (customer_id) WHERE deleted_at IS NULL;

-- ---------------------------------------------------------------- CONSENTS (append-only)
CREATE TABLE consents (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    customer_id   uuid NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
    purpose       access_purpose NOT NULL,
    basis         legal_basis NOT NULL,
    terms_version varchar(20) NOT NULL,
    channel       varchar(30) NOT NULL,
    granted_at    timestamptz NOT NULL DEFAULT now(),
    revoked_at    timestamptz,
    recorded_at   timestamptz NOT NULL DEFAULT now(),
    recorded_by   uuid NOT NULL,
    CONSTRAINT ck_consents_revocation CHECK (revoked_at IS NULL OR revoked_at >= granted_at)
);

CREATE INDEX ix_consents_customer_purpose ON consents (customer_id, purpose, recorded_at DESC);

COMMENT ON TABLE consents IS
    'APPEND-ONLY. Revogar cria uma NOVA linha com revoked_at; o registro original nunca é '
    'alterado nem apagado. O consentimento vigente é a última linha por finalidade. '
    'Sem isso, é impossível provar o que o titular consentiu em uma data passada.';

-- ---------------------------------------------------------------- INSURABLE ASSETS (TPT)
CREATE TABLE insurable_assets (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      uuid NOT NULL REFERENCES brokerages(id) ON DELETE RESTRICT,
    customer_id    uuid NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
    kind           asset_kind NOT NULL,                -- discriminador TPT
    declared_value money_amount NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    created_by     uuid NOT NULL,
    updated_at     timestamptz,
    deleted_at     timestamptz,
    deleted_by     uuid,
    deletion_reason text,
    deletion_batch_id uuid,
    CONSTRAINT ck_assets_value_positive CHECK ((declared_value).amount > 0),
    CONSTRAINT ck_assets_currency CHECK ((declared_value).currency = 'BRL'),
    -- Permite a FK composta das tabelas filhas — é o que garante a hierarquia
    CONSTRAINT ux_assets_kind UNIQUE (id, kind)
);

CREATE INDEX ix_assets_customer ON insurable_assets (tenant_id, customer_id) WHERE deleted_at IS NULL;

CREATE TABLE vehicles (
    id                    uuid PRIMARY KEY,
    kind                  asset_kind NOT NULL DEFAULT 'VEHICLE' CHECK (kind = 'VEHICLE'),
    plate                 varchar(7) NOT NULL,
    chassis               varchar(17) NOT NULL,
    model_year            smallint NOT NULL,
    manufacture_year      smallint NOT NULL,
    brand                 varchar(60) NOT NULL,
    model                 varchar(80) NOT NULL,
    usage                 varchar(20) NOT NULL
                          CHECK (usage IN ('PERSONAL','COMMUTE','COMMERCIAL','RIDESHARE')),
    overnight_postal_code postal_code NOT NULL,
    has_garage            boolean NOT NULL DEFAULT false,
    -- FK COMPOSTA: impede que um asset marcado como PROPERTY tenha registro aqui.
    -- É a herança do modelo OO preservada por integridade referencial.
    FOREIGN KEY (id, kind) REFERENCES insurable_assets (id, kind) ON DELETE CASCADE,
    CONSTRAINT ck_vehicles_plate CHECK (
        plate ~ '^([A-Z]{3}[0-9]{4}|[A-Z]{3}[0-9][A-Z][0-9]{2})$'),
    CONSTRAINT ck_vehicles_chassis CHECK (chassis ~ '^[A-HJ-NPR-Z0-9]{17}$'),
    CONSTRAINT ck_vehicles_years CHECK (
        model_year >= manufacture_year
        AND manufacture_year BETWEEN 1950 AND (EXTRACT(YEAR FROM now())::int + 1))
);

CREATE UNIQUE INDEX ux_vehicles_plate   ON vehicles (plate);
CREATE UNIQUE INDEX ux_vehicles_chassis ON vehicles (chassis);

CREATE TABLE properties (
    id                uuid PRIMARY KEY,
    kind              asset_kind NOT NULL DEFAULT 'PROPERTY' CHECK (kind = 'PROPERTY'),
    location          postal_address NOT NULL,
    area_sqm          numeric(10,2) NOT NULL,
    built_year        smallint NOT NULL,
    construction_type varchar(30) NOT NULL
                      CHECK (construction_type IN ('MASONRY','WOOD','MIXED','STEEL')),
    property_usage    varchar(20) NOT NULL
                      CHECK (property_usage IN ('RESIDENTIAL','COMMERCIAL','VACATION')),
    has_alarm         boolean NOT NULL DEFAULT false,
    FOREIGN KEY (id, kind) REFERENCES insurable_assets (id, kind) ON DELETE CASCADE,
    CONSTRAINT ck_properties_area CHECK (area_sqm > 0 AND area_sqm < 100000),
    CONSTRAINT ck_properties_built_year CHECK (
        built_year BETWEEN 1900 AND (EXTRACT(YEAR FROM now())::int + 1))
);

CREATE INDEX ix_properties_postal ON properties (((location).postal_code));
