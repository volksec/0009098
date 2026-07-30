-- =============================================================================
-- V001 — Domains, tipos compostos e enums
--
-- Esta migration estabelece o vocabulário objeto-relacional do sistema: os tipos
-- que representam Value Objects do domínio diretamente no banco, em vez de
-- espalhá-los como colunas soltas repetidas em cada tabela.
-- =============================================================================

-- ---------------------------------------------------------------- DOMAINS
-- Validação reutilizável por tipo. Uma vez declarada, vale para toda coluna que
-- usar o domain — não é possível esquecer o CHECK em uma tabela nova.

CREATE DOMAIN cpf_digits    AS char(11) CHECK (VALUE ~ '^[0-9]{11}$');
CREATE DOMAIN cnpj_digits   AS char(14) CHECK (VALUE ~ '^[0-9]{14}$');
CREATE DOMAIN uf_code       AS char(2)  CHECK (VALUE ~ '^[A-Z]{2}$');
CREATE DOMAIN postal_code   AS char(8)  CHECK (VALUE ~ '^[0-9]{8}$');
CREATE DOMAIN currency_code AS char(3)  CHECK (VALUE ~ '^[A-Z]{3}$');

COMMENT ON DOMAIN cpf_digits IS
    'CPF apenas dígitos. O dígito verificador é validado no VO DocumentNumber; '
    'aqui garante-se o formato, que é o que o banco consegue verificar barato.';

-- ---------------------------------------------------------------- TIPOS COMPOSTOS
-- Mapeiam Value Objects multi-campo. O objeto do domínio sobrevive à persistência
-- como uma unidade coesa, e não como N colunas que alguém pode desemparelhar.

CREATE TYPE money_amount AS (
    amount   numeric(14,2),
    currency currency_code
);

COMMENT ON TYPE money_amount IS
    'Value Object Money. A invariante de escala vive no VO; as invariantes de sinal e '
    'moeda são replicadas como CHECK em cada coluna, porque o banco é a última linha de defesa.';

CREATE TYPE postal_address AS (
    street      varchar(160),
    number      varchar(20),
    complement  varchar(60),
    district    varchar(80),
    city        varchar(80),
    state       uf_code,
    postal_code postal_code
);

CREATE TYPE deductible AS (
    kind    varchar(12),      -- FIXED | PERCENTAGE
    amount  numeric(14,2),
    percent numeric(6,4)
);

-- ---------------------------------------------------------------- ENUMS
-- Conjuntos FECHADOS PELO CÓDIGO: adicionar um valor exige mudar a lógica de
-- transição de estado, então deve exigir migration. Conjuntos que o negócio edita
-- sem deploy (motivos de cancelamento, tipos de pendência) usam tabela de referência.

CREATE TYPE customer_kind      AS ENUM ('INDIVIDUAL','BUSINESS');
CREATE TYPE customer_status    AS ENUM ('ACTIVE','INACTIVE','BLOCKED');
CREATE TYPE asset_kind         AS ENUM ('VEHICLE','PROPERTY');
CREATE TYPE insurance_branch   AS ENUM ('AUTO','RESIDENTIAL');
CREATE TYPE plan_tier          AS ENUM ('ESSENTIAL','COMPLETE','MASTER');
CREATE TYPE quotation_status   AS ENUM ('DRAFT','CALCULATED','REJECTED','CONVERTED','EXPIRED');
CREATE TYPE proposal_status    AS ENUM ('DRAFT','SUBMITTED','UNDER_ANALYSIS','PENDING',
                                        'APPROVED','REJECTED','ISSUED','EXPIRED');
CREATE TYPE policy_status      AS ENUM ('ACTIVE','CANCELLED','EXPIRED','RENEWED');
CREATE TYPE installment_status AS ENUM ('PENDING','PAID','OVERDUE','CANCELLED');
CREATE TYPE commission_status  AS ENUM ('FORECAST','RELEASED','PAID','REVERSED');
CREATE TYPE commission_base    AS ENUM ('NET_PREMIUM','TOTAL_PREMIUM');
CREATE TYPE claim_status       AS ENUM ('REPORTED','UNDER_ANALYSIS','PENDING','APPROVED',
                                        'DENIED','SETTLED','CLOSED');
CREATE TYPE user_profile       AS ENUM ('BROKER','REGULATOR');
CREATE TYPE access_purpose     AS ENUM ('REGULATORY_SUPERVISION','COMPLIANCE_VERIFICATION',
                                        'INCONSISTENCY_INVESTIGATION','INDICATOR_ANALYSIS');
CREATE TYPE legal_basis        AS ENUM ('CONSENT','CONTRACT','LEGAL_OBLIGATION',
                                        'LEGITIMATE_INTEREST');
CREATE TYPE risk_band          AS ENUM ('LOW','MODERATE','HIGH','SEVERE');

-- ---------------------------------------------------------------- FUNÇÕES AUXILIARES

-- Deriva a faixa de risco a partir do escore. A MESMA regra existe no VO RiskScore.
-- Replicada aqui para permitir coluna gerada e indexada — não existe estado em que
-- escore e faixa possam divergir, porque a faixa nunca é escrita, sempre derivada.
CREATE OR REPLACE FUNCTION app.risk_band_of(score smallint) RETURNS risk_band
LANGUAGE sql IMMUTABLE STRICT
AS $$
    SELECT CASE
        WHEN score <= 250 THEN 'LOW'::risk_band
        WHEN score <= 550 THEN 'MODERATE'::risk_band
        WHEN score <= 800 THEN 'HIGH'::risk_band
        ELSE 'SEVERE'::risk_band
    END
$$;

-- Sequences por ano para a numeração de negócio. A unicidade sob concorrência é
-- garantida pelo PostgreSQL, não por contador em memória da aplicação.
CREATE SEQUENCE app.policy_number_seq   AS bigint START 1;
CREATE SEQUENCE app.proposal_number_seq AS bigint START 1;
CREATE SEQUENCE app.quotation_number_seq AS bigint START 1;
CREATE SEQUENCE app.claim_number_seq    AS bigint START 1;

GRANT USAGE ON ALL SEQUENCES IN SCHEMA app TO app_user, app_worker;
