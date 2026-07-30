-- =============================================================================
--  ⚠️  LABORATÓRIO VULNERÁVEL — NÃO USAR COMO REFERÊNCIA  ⚠️
--
--  Este esquema contém falhas DELIBERADAS. Ele existe para demonstrar, lado a
--  lado, o que os controles da versão segura impedem. Roda apenas sob o profile
--  Docker `security-lab`, em rede sem rota externa, com dados sintéticos próprios.
--
--  NUNCA copie deste arquivo para código real. Ver ADR-0009.
--
--  É o MESMO domínio da versão segura, sem: constraints, índices, RLS,
--  auditoria, versionamento e integridade referencial.
-- =============================================================================

-- ❌ FALHA 1 — Sem tipos compostos nem domains.
-- Valores monetários viram `float`, que não representa dinheiro com exatidão:
-- 0.1 + 0.2 <> 0.3 em ponto flutuante binário. Na versão segura é numeric(14,2).

CREATE TABLE brokerages (
    id         serial PRIMARY KEY,     -- ❌ FALHA 2: id sequencial previsível → IDOR trivial
    name       text,
    document   text                    -- ❌ sem validação de formato
);

CREATE TABLE users (
    id        serial PRIMARY KEY,
    tenant_id integer,                 -- ❌ FALHA 3: sem FK — aponta para tenant inexistente
    email     text,
    password  text,                    -- ❌ FALHA 4: senha em TEXTO CLARO
    profile   text,                    -- ❌ sem enum: qualquer string vira perfil
    is_admin  boolean DEFAULT false    -- ❌ FALHA 5: alvo de mass assignment
);

CREATE TABLE customers (
    id        serial PRIMARY KEY,
    tenant_id integer,                 -- ❌ sem FK, sem RLS, sem índice
    name      text,
    document  text,                    -- ❌ FALHA 6: CPF em claro, sem cifragem nem hash
    email     text,
    phone     text,
    notes     text                     -- ❌ FALHA 7: renderizado sem escape → Stored XSS
);
-- ❌ FALHA 8: nenhum índice. Toda busca é sequential scan — visível no Engineering Lab
--             quando comparado com a versão segura sobre a mesma massa.

CREATE TABLE assets (
    id          serial PRIMARY KEY,
    customer_id integer,
    kind        text,                  -- ❌ sem herança: veículo e imóvel na mesma tabela,
    plate       text,                  --    com metade das colunas sempre NULL e nenhuma
    chassis     text,                  --    obrigatoriedade garantida
    area_sqm    float,
    value       float
);

CREATE TABLE quotations (
    id          serial PRIMARY KEY,
    tenant_id   integer,
    customer_id integer,
    asset_id    integer,
    product     text,                  -- ❌ sem versionamento: alterar o catálogo
    premium     float,                 --    reescreve o passado
    status      text,                  -- ❌ sem máquina de estados
    created_at  timestamp DEFAULT now()
    -- ❌ FALHA 9: sem expires_at — cotação nunca expira
);

CREATE TABLE proposals (
    id           serial PRIMARY KEY,
    tenant_id    integer,
    quotation_id integer,              -- ❌ FALHA 10: sem unique — a mesma cotação
    status       text,                 --    vira N propostas
    premium      float
);

CREATE TABLE policies (
    id          serial PRIMARY KEY,
    tenant_id   integer,
    proposal_id integer,               -- ❌ FALHA 11: SEM UNIQUE.
    customer_id integer,               --    Duas emissões concorrentes para a mesma
    asset_id    integer,               --    proposta criam DUAS apólices.
    number      text,                  -- ❌ sem dígito verificador → enumerável
    status      text,
    start_date  date,                  -- ❌ FALHA 12: sem daterange e sem EXCLUDE —
    end_date    date,                  --    vigências se sobrepõem livremente
    premium     float,
    created_at  timestamp DEFAULT now()
    -- ❌ FALHA 13: sem coluna de versão nem uso de xmin → sem optimistic locking
);

CREATE TABLE installments (
    id         serial PRIMARY KEY,
    policy_id  integer,
    seq        integer,
    amount     float,                  -- ❌ FALHA 14: sem trigger de soma —
    due_date   date,                   --    Σ parcelas pode divergir do prêmio
    status     text
);

CREATE TABLE commissions (
    id        serial PRIMARY KEY,
    tenant_id integer,
    broker_id integer,                 -- ❌ FALHA 15: sem RLS e sem ABAC —
    policy_id integer,                 --    um corretor consulta a comissão do outro
    rate      float,                   -- ❌ sem referência à versão da regra:
    amount    float,                   --    impossível responder "por que esse valor?"
    status    text
);

CREATE TABLE claims (
    id              serial PRIMARY KEY,
    tenant_id       integer,
    policy_id       integer,
    occurrence_date date,              -- ❌ FALHA 16: sem validação de vigência —
    description     text,              --    aceita sinistro fora da cobertura
    amount          float
);

CREATE TABLE documents (
    id            serial PRIMARY KEY,
    owner_id      integer,
    file_name     text,                -- ❌ FALHA 17: nome original preservado →
    content_type  text,                --    path traversal e execução
    storage_path  text
    -- ❌ sem validação por magic bytes, sem hash, sem limite de tamanho
);

-- ❌ FALHA 18: SEM tabela de auditoria. Nenhuma operação deixa rastro.
-- ❌ FALHA 19: SEM Outbox. Eventos publicados fora da transação (dual write).
-- ❌ FALHA 20: SEM idempotency_keys. Requisição repetida duplica o recurso.
-- ❌ FALHA 21: SEM RLS em nenhuma tabela. O isolamento depende inteiramente de o
--              desenvolvedor lembrar do WHERE tenant_id — e um endpoint novo esquece.
-- ❌ FALHA 22: SEM soft delete. DELETE físico destrói histórico regulatório.

-- ❌ FALHA 23: privilégio excessivo. A aplicação roda como superusuário do banco,
--              então qualquer SQL Injection vira controle total do servidor.
--              Na versão segura, app_user não tem DDL, não tem DELETE e não tem BYPASSRLS.
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO vulnerable_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO vulnerable_app;

COMMENT ON SCHEMA public IS
    'LABORATORIO VULNERAVEL - DADOS SINTETICOS - REDE ISOLADA - NAO USAR COMO REFERENCIA';
