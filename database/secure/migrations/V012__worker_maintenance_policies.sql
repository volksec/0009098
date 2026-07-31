-- =====================================================================
-- V012 — Três workers de manutenção estavam cegos sob RLS
-- =====================================================================
--
-- O Outbox Dispatcher recebeu, na V008, uma política própria (p_outbox_worker)
-- porque processa todos os tenants: é conta técnica, com escopo próprio. O
-- raciocínio estava certo, mas foi aplicado a uma tabela só. Os outros três
-- workers varrem tabelas de negócio e ficaram sujeitos à política de tenant,
-- que exige app.current_tenant() — um valor que worker nenhum define.
--
--     Renewal Scanner   → SELECT policies      → via 0 de 147
--     Billing Scheduler → UPDATE installments  → via 0 de 441
--     Quotation Expirer → UPDATE quotations    → via 0 de 67
--
-- O sintoma era silêncio, de novo: os três só registram log quando afetam ao
-- menos uma linha, então rodavam a cada ciclo sem nunca fazer nada e sem nunca
-- reclamar. Renovação nunca era aberta, parcela vencida nunca virava OVERDUE e
-- cotação vencida nunca saía de CALCULATED.
--
-- Estes jobs são, por definição, multi-tenant: a pergunta "qual parcela venceu?"
-- não pertence a corretora nenhuma. Sujeitá-los ao filtro de tenant é pedir que
-- varram apenas o que já lhes é visível — e nada é, porque não há tenant corrente.
--
-- O acesso continua estreito: app_worker é conta técnica sem login de usuário,
-- já não tem DELETE (revogado na V008), e as políticas de app_user seguem
-- intactas. Um corretor continua vendo apenas o próprio tenant.

-- ---------------------------------------------------------------- POLICIES
-- Renewal Scanner: lê apólices perto do vencimento e abre a renovação
CREATE POLICY p_policies_worker ON policies
    FOR ALL TO app_worker USING (true) WITH CHECK (true);

COMMENT ON POLICY p_policies_worker ON policies IS
    'Conta técnica: o Renewal Scanner varre todos os tenants. Sem esta política '
    'a varredura enxerga zero linhas e a renovação nunca é aberta.';

CREATE POLICY p_renewals_worker ON renewals
    FOR ALL TO app_worker USING (true) WITH CHECK (true);

-- ---------------------------------------------------------------- INSTALLMENTS
-- Billing Scheduler: marca como OVERDUE o que venceu
CREATE POLICY p_installments_worker ON installments
    FOR ALL TO app_worker USING (true) WITH CHECK (true);

COMMENT ON POLICY p_installments_worker ON installments IS
    'Conta técnica: "qual parcela venceu?" é pergunta do sistema, não de uma corretora.';

-- ---------------------------------------------------------------- QUOTATIONS
-- Quotation Expirer: encerra cotações fora do prazo de validade
CREATE POLICY p_quotations_worker ON quotations
    FOR ALL TO app_worker USING (true) WITH CHECK (true);

COMMENT ON POLICY p_quotations_worker ON quotations IS
    'Conta técnica: a expiração por decurso de prazo independe de tenant.';
