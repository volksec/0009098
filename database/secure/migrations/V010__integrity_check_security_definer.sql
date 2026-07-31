-- =====================================================================
-- V010 — Verificação de integridade precisa enxergar todos os tenants
-- =====================================================================
--
-- Bug corrigido: app.run_integrity_checks() era SECURITY INVOKER. O worker
-- conecta como app_worker, que está sujeito à Row-Level Security e não tem
-- contexto de tenant definido — logo, cada consulta interna via zero linhas
-- e a função devolvia 0 para as dez verificações.
--
-- O sintoma era o pior possível em um monitor: silêncio. O log dizia
-- "10 verificações, nenhuma divergência" enquanto a base tinha 147 apólices
-- sem cobertura e 159 sem trilha de auditoria. Um monitor que nunca acusa
-- nada é indistinguível de um monitor quebrado.
--
-- A verificação é, por natureza, uma auditoria de invariantes do sistema
-- inteiro: pergunta "existe alguma apólice sem cobertura em qualquer
-- corretora?". Fazê-la respeitar RLS é uma contradição — seria pedir que
-- auditasse apenas o que já lhe é permitido ver.
--
-- SECURITY DEFINER resolve, e é seguro aqui porque a função:
--   1. não recebe parâmetro algum, então não há entrada para injetar;
--   2. devolve apenas contagens agregadas — nenhuma linha de negócio, nenhum
--      dado de cliente atravessa a fronteira do tenant;
--   3. fixa search_path, fechando o vetor clássico de sequestro de resolução
--      de nomes contra funções DEFINER.
--
-- O corpo das dez verificações é o mesmo da V009, sem uma vírgula alterada:
-- esta migration muda o contexto de execução, não a regra.

CREATE OR REPLACE FUNCTION app.run_integrity_checks()
RETURNS TABLE (check_code text, failure_count bigint)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = app, public, pg_temp
AS $$
BEGIN
    RETURN QUERY

    -- Σ das parcelas deve bater com o total do plano
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
    'Auditoria de invariantes do sistema inteiro. SECURITY DEFINER de propósito: '
    'sob RLS a função enxergaria zero linhas e devolveria zero divergências, que é '
    'o modo de falha mais perigoso de um monitor. Devolve apenas contagens agregadas, '
    'nunca dados de negócio, então não abre canal entre tenants. search_path fixo.';

-- EXECUTE continua restrito: DEFINER sem essa restrição seria escalada de privilégio
REVOKE ALL ON FUNCTION app.run_integrity_checks() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.run_integrity_checks() TO app_worker;
