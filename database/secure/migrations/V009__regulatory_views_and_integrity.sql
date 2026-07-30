-- =============================================================================
-- V009 — Views mascaradas do regulador, indicadores e verificação de integridade
-- =============================================================================

-- ---------------------------------------------------------------- MASCARAMENTO
CREATE OR REPLACE FUNCTION app.mask_document(p_document text, p_kind customer_kind)
RETURNS text LANGUAGE sql IMMUTABLE
AS $$
    SELECT CASE
        WHEN p_document IS NULL THEN NULL
        WHEN p_kind = 'INDIVIDUAL' THEN '***.***.' || substr(p_document, 7, 3) || '-**'
        ELSE '**.***.' || substr(p_document, 6, 3) || '/****-**'
    END
$$;

CREATE OR REPLACE FUNCTION app.mask_name(p_name text) RETURNS text
LANGUAGE sql IMMUTABLE
AS $$
    SELECT CASE
        WHEN p_name IS NULL OR length(p_name) = 0 THEN NULL
        WHEN length(p_name) <= 2 THEN left(p_name, 1) || '*****'
        ELSE left(p_name, 2) || repeat('*', greatest(3, length(p_name) - 2))
    END
$$;

-- =============================================================================
-- VIEWS DO REGULADOR — a ÚNICA superfície de leitura do perfil regulatório.
-- O papel app_regulator não tem privilégio nas tabelas base (V008).
-- =============================================================================

CREATE VIEW regulatory.customers_masked AS
SELECT
    c.id,
    c.tenant_id,
    b.trade_name AS brokerage_name,
    c.kind,
    c.status,
    CASE c.kind
        WHEN 'INDIVIDUAL' THEN app.mask_name(c.first_name)
        ELSE app.mask_name(c.legal_name)
    END AS masked_name,
    -- O documento em claro NUNCA sai daqui: a view expõe apenas a forma mascarada
    app.mask_document(app.decrypt_document(c.document_encrypted), c.kind)
        AS masked_document,
    date_trunc('month', c.created_at) AS created_month   -- minimização temporal
FROM customers c
JOIN brokerages b ON b.id = c.tenant_id
WHERE c.deleted_at IS NULL
  AND c.tenant_id IN (SELECT tenant_id FROM app.regulatory_scope_current());

CREATE VIEW regulatory.policies_summary AS
SELECT
    p.id,
    p.tenant_id,
    b.trade_name AS brokerage_name,
    p.number,
    p.status,
    lower(p.coverage_period)  AS period_start,
    upper(p.coverage_period)  AS period_end,
    (p.total_premium).amount  AS total_premium,
    pr.name  AS product_name,
    pv.branch,
    date_trunc('month', p.issued_at) AS issued_month,
    app.mask_name(br.full_name) AS masked_broker
FROM policies p
JOIN brokerages b        ON b.id  = p.tenant_id
JOIN brokers br          ON br.id = p.broker_id
JOIN product_versions pv ON pv.id = p.product_version_id
JOIN insurance_products pr ON pr.id = pv.product_id
WHERE p.tenant_id IN (SELECT tenant_id FROM app.regulatory_scope_current());

CREATE VIEW regulatory.proposal_lifecycle AS
SELECT
    pr.id            AS proposal_id,
    pr.tenant_id,
    pr.number        AS proposal_number,
    pr.status,
    q.number         AS quotation_number,
    q.created_at     AS quoted_at,
    pr.submitted_at,
    pr.decided_at,
    ud.outcome       AS underwriting_outcome,
    ud.reasons       AS underwriting_reasons,
    po.number        AS policy_number,
    po.issued_at,
    (SELECT count(*) FROM documents d
      WHERE d.owner_type = 'PROPOSAL' AND d.owner_id = pr.id AND d.deleted_at IS NULL)
                     AS document_count,
    (SELECT count(*) FROM pendencies pe WHERE pe.proposal_id = pr.id)
                     AS pendency_count
FROM proposals pr
JOIN quotations q ON q.id = pr.quotation_id
LEFT JOIN LATERAL (
    SELECT outcome, reasons FROM underwriting_decisions d
     WHERE d.proposal_id = pr.id ORDER BY version DESC LIMIT 1
) ud ON true
LEFT JOIN policies po ON po.proposal_id = pr.id AND po.status <> 'CANCELLED'
WHERE pr.deleted_at IS NULL
  AND pr.tenant_id IN (SELECT tenant_id FROM app.regulatory_scope_current());

COMMENT ON VIEW regulatory.proposal_lifecycle IS
    'RF-097: ciclo completo de uma proposta. Expõe metadados de documento (contagem), '
    'nunca o conteúdo.';

-- ---------------------------------------------------------------- INDICADORES
-- Supressão de células pequenas (k-anonimato, k=5): uma corretora com 2 apólices no mês
-- teria o prêmio médio praticamente identificando o cliente. Abaixo do limiar, NULL.
CREATE MATERIALIZED VIEW regulatory.brokerage_indicators AS
SELECT
    p.tenant_id,
    date_trunc('month', p.issued_at)::date AS reference_month,
    pv.product_id,
    count(*)                                                   AS policies_issued,
    count(*) FILTER (WHERE p.status = 'CANCELLED')             AS cancellations,
    CASE WHEN count(*) >= 5 THEN sum((p.total_premium).amount) END AS total_premium,
    CASE WHEN count(*) >= 5 THEN round(avg((p.total_premium).amount), 2) END AS avg_premium,
    CASE WHEN count(*) >= 5
         THEN round(percentile_cont(0.5) WITHIN GROUP (ORDER BY (p.total_premium).amount)::numeric, 2)
    END AS median_premium
FROM policies p
JOIN product_versions pv ON pv.id = p.product_version_id
GROUP BY 1, 2, 3;

-- Índice único é PRÉ-REQUISITO do REFRESH CONCURRENTLY, que não bloqueia leitura
CREATE UNIQUE INDEX ux_brokerage_indicators
    ON regulatory.brokerage_indicators (tenant_id, reference_month, product_id);

CREATE MATERIALIZED VIEW regulatory.compliance_indicators AS
SELECT
    b.id AS tenant_id,
    b.trade_name,
    (SELECT count(*) FROM customers c
      WHERE c.tenant_id = b.id AND c.deleted_at IS NULL) AS active_customers,
    (SELECT count(*) FROM customers c
      WHERE c.tenant_id = b.id AND c.deleted_at IS NULL
        AND NOT EXISTS (SELECT 1 FROM consents cs
                         WHERE cs.customer_id = c.id AND cs.revoked_at IS NULL))
                                                          AS customers_without_consent,
    (SELECT count(*) FROM security_events se
      WHERE se.tenant_id = b.id AND se.event_type = 'TENANT_VIOLATION_ATTEMPT'
        AND se.occurred_at > now() - interval '30 days')  AS tenant_violations_30d,
    (SELECT count(*) FROM security_events se
      WHERE se.tenant_id = b.id AND se.severity IN ('HIGH','CRITICAL')
        AND se.occurred_at > now() - interval '30 days')  AS high_severity_events_30d,
    (SELECT count(*) FROM audit_events ae
      WHERE ae.tenant_id = b.id AND ae.occurred_at > now() - interval '30 days')
                                                          AS audit_events_30d
FROM brokerages b
WHERE b.deleted_at IS NULL;

CREATE UNIQUE INDEX ux_compliance_indicators ON regulatory.compliance_indicators (tenant_id);

GRANT SELECT ON ALL TABLES IN SCHEMA regulatory TO app_regulator;

-- =============================================================================
-- VERIFICAÇÃO DE INTEGRIDADE (RF-135)
--
-- A integridade deixa de ser presumida e passa a ser MEDIDA. Um worker diário roda
-- estas asserções; qualquer divergência gera alerta e aparece no dashboard de
-- conformidade. Se o modelo estiver correto, todas retornam zero — sempre.
-- =============================================================================

CREATE TABLE integrity_check_results (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    check_code    varchar(60) NOT NULL,
    executed_at   timestamptz NOT NULL DEFAULT now(),
    failure_count integer NOT NULL,
    details       jsonb,   -- JSONB-JUSTIFICATION: a forma do detalhe varia por verificação
    duration_ms   integer NOT NULL
);

CREATE INDEX ix_integrity_results ON integrity_check_results (check_code, executed_at DESC);

CREATE OR REPLACE FUNCTION app.run_integrity_checks()
RETURNS TABLE (check_code text, failure_count bigint)
LANGUAGE plpgsql AS $$
BEGIN
    RETURN QUERY

    -- Σ parcelas deve ser igual ao total do plano
    SELECT 'INSTALLMENTS_SUM_MISMATCH'::text, count(*)
      FROM (SELECT p.id FROM installment_plans p
             JOIN installments i ON i.plan_id = p.id
            GROUP BY p.id, (p.total_amount).amount
           HAVING sum((i.amount).amount) <> (p.total_amount).amount) x

    UNION ALL
    -- Apólice ativa sem nenhuma cobertura contratada
    SELECT 'POLICY_WITHOUT_COVERAGE', count(*)
      FROM policies p
     WHERE p.status = 'ACTIVE'
       AND NOT EXISTS (SELECT 1 FROM policy_coverages c WHERE c.policy_id = p.id)

    UNION ALL
    -- Prêmio da apólice divergente da soma das coberturas
    SELECT 'POLICY_PREMIUM_MISMATCH', count(*)
      FROM (SELECT p.id FROM policies p
             JOIN policy_coverages c ON c.policy_id = p.id
            GROUP BY p.id, (p.total_premium).amount
           HAVING sum((c.premium).amount) <> (p.total_premium).amount) x

    UNION ALL
    -- Duas apólices vivas para a mesma proposta (a unique deveria impedir)
    SELECT 'DUPLICATE_POLICY_PER_PROPOSAL', count(*)
      FROM (SELECT proposal_id FROM policies WHERE status <> 'CANCELLED'
            GROUP BY proposal_id HAVING count(*) > 1) x

    UNION ALL
    -- Comissão sem regra vigente para o produto no período
    SELECT 'COMMISSION_WITHOUT_VALID_RULE', count(*)
      FROM commissions cm
      JOIN policies p ON p.id = cm.policy_id
      JOIN product_versions pv ON pv.id = p.product_version_id
     WHERE NOT EXISTS (
        SELECT 1 FROM commission_rules r
         WHERE r.id = cm.rule_id AND r.product_id = pv.product_id)

    UNION ALL
    -- Bem segurável sem o registro filho correspondente ao seu tipo (TPT quebrado)
    SELECT 'ASSET_WITHOUT_SUBTYPE', count(*)
      FROM insurable_assets a
     WHERE (a.kind = 'VEHICLE'  AND NOT EXISTS (SELECT 1 FROM vehicles   v WHERE v.id = a.id))
        OR (a.kind = 'PROPERTY' AND NOT EXISTS (SELECT 1 FROM properties p WHERE p.id = a.id))

    UNION ALL
    -- Sinistro com data fora da vigência (a trigger deveria impedir)
    SELECT 'CLAIM_OUTSIDE_COVERAGE', count(*)
      FROM claims c JOIN policies p ON p.id = c.policy_id
     WHERE NOT (p.coverage_period @> c.occurrence_date)

    UNION ALL
    -- Cliente sem contato ativo (invariante do agregado Customer)
    SELECT 'CUSTOMER_WITHOUT_CONTACT', count(*)
      FROM customers c
     WHERE c.deleted_at IS NULL AND c.status = 'ACTIVE'
       AND NOT EXISTS (SELECT 1 FROM contacts ct
                        WHERE ct.customer_id = c.id AND ct.deleted_at IS NULL)

    UNION ALL
    -- Mensagens de Outbox presas há mais de uma hora
    SELECT 'OUTBOX_STUCK', count(*)
      FROM outbox_messages
     WHERE processed_at IS NULL AND occurred_at < now() - interval '1 hour'

    UNION ALL
    -- ★ Cobertura de auditoria: emissão de apólice sem AuditEvent correspondente.
    -- A meta de audit_coverage_ratio = 1.0 significa que esta contagem é sempre zero.
    SELECT 'POLICY_WITHOUT_AUDIT', count(*)
      FROM policies p
     WHERE NOT EXISTS (
        SELECT 1 FROM audit_events a
         WHERE a.resource_type = 'Policy' AND a.resource_id = p.id
           AND a.action = 'POLICY_ISSUED');
END $$;

COMMENT ON FUNCTION app.run_integrity_checks() IS
    'RF-135. Se o modelo estiver correto, TODAS retornam zero. Qualquer valor diferente '
    'indica que uma invariante foi contornada — por bug, script manual ou migration errada.';

GRANT EXECUTE ON FUNCTION app.run_integrity_checks() TO app_worker;
