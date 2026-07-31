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
-- Conjunto fechado, e não formato: '^[A-Z]{2}$' aceitaria 'ZZ' e prometeria uma
-- garantia que não entrega. As 27 unidades federativas são definidas em lei.
CREATE DOMAIN uf_code AS char(2) CHECK (
    VALUE IN ('AC','AL','AP','AM','BA','CE','DF','ES','GO','MA','MT','MS','MG',
              'PA','PB','PR','PE','PI','RJ','RN','RS','RO','RR','SC','SP','SE','TO')
);
CREATE DOMAIN postal_code   AS char(8)  CHECK (VALUE ~ '^[0-9]{8}$');
-- Idem: o sistema opera exclusivamente em BRL. O cálculo de prêmio, o rateio de
-- parcelas e a apuração de comissão somam valores assumindo moeda única — aceitar
-- 'USD' deixaria montantes de moedas diferentes somáveis entre si sem que nenhuma
-- linha de código tivesse sido revista.
CREATE DOMAIN currency_code AS char(3) CHECK (VALUE IN ('BRL'));

COMMENT ON DOMAIN uf_code IS
    'Unidade federativa. Enumerar os 27 valores é mais honesto que checar formato: '
    'a alternativa aceita ZZ. Note que este CHECK precisa nascer aqui — depois que '
    'postal_address for usado por alguma coluna, o PostgreSQL recusa ALTER DOMAIN '
    'sobre um domínio aninhado em tipo composto já referenciado.';

COMMENT ON DOMAIN currency_code IS
    'Moeda dos valores monetários. Conjunto fechado pelo código: ampliar exige '
    'migration E revisão de todo cálculo que hoje assume moeda única.';

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

-- ---------------------------------------------------------------- CIFRAGEM
-- O documento (CPF/CNPJ) é cifrado em repouso. A chave vem de uma configuração de
-- sessão definida pela aplicação a partir de um segredo externo (Docker secret /
-- cofre), NUNCA de uma constante no banco: um dump vazado não deve conter a chave
-- que o decifra.
CREATE OR REPLACE FUNCTION app.encryption_key() RETURNS text
LANGUAGE sql STABLE
AS $$
    SELECT NULLIF(current_setting('app.encryption_key', true), '')
$$;

CREATE OR REPLACE FUNCTION app.encrypt_document(p_plain text) RETURNS bytea
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    v_key text := app.encryption_key();
BEGIN
    IF v_key IS NULL THEN
        RAISE EXCEPTION 'Chave de cifragem ausente no contexto da sessão.'
            USING ERRCODE = 'insufficient_privilege',
                  HINT = 'A aplicação deve executar SET LOCAL app.encryption_key.';
    END IF;
    RETURN pgp_sym_encrypt(p_plain, v_key, 'cipher-algo=aes256');
END $$;

CREATE OR REPLACE FUNCTION app.decrypt_document(p_cipher bytea) RETURNS text
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    v_key text := app.encryption_key();
BEGIN
    IF v_key IS NULL THEN
        RAISE EXCEPTION 'Chave de cifragem ausente no contexto da sessão.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN pgp_sym_decrypt(p_cipher, v_key);
END $$;

COMMENT ON FUNCTION app.decrypt_document(bytea) IS
    'Falha FECHADO quando não há chave no contexto: sem SET LOCAL app.encryption_key, '
    'a função lança em vez de retornar NULL silenciosamente.';

-- Sequences por ano para a numeração de negócio. A unicidade sob concorrência é
-- garantida pelo PostgreSQL, não por contador em memória da aplicação.
CREATE SEQUENCE app.policy_number_seq   AS bigint START 1;
CREATE SEQUENCE app.proposal_number_seq AS bigint START 1;
CREATE SEQUENCE app.quotation_number_seq AS bigint START 1;
CREATE SEQUENCE app.claim_number_seq    AS bigint START 1;

GRANT USAGE ON ALL SEQUENCES IN SCHEMA app TO app_user, app_worker;
