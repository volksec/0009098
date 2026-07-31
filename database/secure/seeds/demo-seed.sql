-- =============================================================================
-- Massa sintética de demonstração — determinística (setseed fixo).
--
-- Todos os dados são gerados. CPFs e CNPJs têm dígito verificador válido, mas
-- vêm de faixas reservadas para teste. Nomes, placas e endereços são fictícios.
-- =============================================================================

SELECT setseed(0.42);

-- O FORCE ROW LEVEL SECURITY vale inclusive para o dono da tabela, então o
-- contexto precisa ser definido mesmo aqui.
SELECT set_config('app.user_profile', 'BROKER', false),
       set_config('app.actor_id', '00000000-0000-0000-0000-000000000001', false);

-- ---------------------------------------------------------------- corretoras
DO $seed$
DECLARE
    v_names text[] := ARRAY['Alfa Corretora','Beta Seguros','Gama Corretagem','Delta Risco',
                            'Epsilon Proteção','Zeta Corretora','Eta Seguros','Theta Garantias'];
    v_docs  text[] := ARRAY['11222333000181','11444777000161','34028316000103','45997418000153',
                            '60746948000112','33000167000101','47960950000121','07526557000100'];
    v_id uuid;
    i int;
BEGIN
    FOR i IN 1..8 LOOP
        v_id := md5('pdc:brokerage:' || i::text)::uuid;

        PERFORM set_config('app.tenant_id', v_id::text, false);

        INSERT INTO brokerages (id, legal_name, trade_name, document, susep_registration, created_by)
        VALUES (v_id, v_names[i] || ' LTDA', v_names[i], v_docs[i],
                'SUSEP-' || lpad(i::text, 6, '0'), '00000000-0000-0000-0000-000000000001')
        ON CONFLICT DO NOTHING;
    END LOOP;
END $seed$;

-- ---------------------------------------------------------------- produto
DO $seed$
DECLARE
    v_auto uuid := md5('pdc:product:auto')::uuid;
    v_res  uuid := md5('pdc:product:residential')::uuid;
BEGIN
    INSERT INTO insurance_products (id, code, name, branch) VALUES
        (v_auto, 'AUTO-STD', 'Auto Proteção Total', 'AUTO'),
        (v_res,  'RES-STD',  'Residencial Completo', 'RESIDENTIAL')
    ON CONFLICT DO NOTHING;

    INSERT INTO product_versions (id, product_id, version, branch, base_rate, risk_sensitivity,
           max_acceptable_risk, min_insured_value, max_insured_value, coverage_cap,
           questionnaire_schema, published_at, valid_period)
    VALUES (md5('pdc:pv:auto:1')::uuid, v_auto, 1, 'AUTO', 0.045, 0.35,
            800, 10000, 500000, ROW(500000,'BRL')::money_amount,
            jsonb_build_object('type','object'), now(), daterange('2026-01-01','2027-01-01')),
           (md5('pdc:pv:residential:1')::uuid, v_res, 1, 'RESIDENTIAL', 0.012, 0.25,
            850, 50000, 2000000, ROW(2000000,'BRL')::money_amount,
            jsonb_build_object('type','object'), now(), daterange('2026-01-01','2027-01-01'))
    ON CONFLICT DO NOTHING;

    -- Coberturas do produto de automóvel. As obrigatórias não podem ser desmarcadas
    -- na cotação — a invariante vive no agregado Quotation.
    INSERT INTO coverages (id, product_version_id, code, name, description, is_mandatory,
           min_limit, max_limit, default_deductible, rate_factor) VALUES
        (md5('pdc:cov:auto:collision')::uuid, md5('pdc:pv:auto:1')::uuid,
         'COLLISION', 'Colisão e capotagem',
         'Danos ao veículo segurado em colisão, capotagem ou abalroamento', true,
         ROW(10000,'BRL')::money_amount, ROW(500000,'BRL')::money_amount,
         ROW('PERCENTAGE', NULL, 0.05)::deductible, 0.028),

        (md5('pdc:cov:auto:theft')::uuid, md5('pdc:pv:auto:1')::uuid,
         'THEFT', 'Roubo e furto',
         'Indenização integral em caso de roubo ou furto do veículo', true,
         ROW(10000,'BRL')::money_amount, ROW(500000,'BRL')::money_amount,
         ROW('FIXED', 0, NULL)::deductible, 0.014),

        (md5('pdc:cov:auto:thirdparty')::uuid, md5('pdc:pv:auto:1')::uuid,
         'THIRD_PARTY', 'Danos a terceiros',
         'Responsabilidade civil por danos materiais e corporais a terceiros', true,
         ROW(50000,'BRL')::money_amount, ROW(1000000,'BRL')::money_amount,
         ROW('FIXED', 1500, NULL)::deductible, 0.009),

        (md5('pdc:cov:auto:glass')::uuid, md5('pdc:pv:auto:1')::uuid,
         'GLASS', 'Vidros e faróis',
         'Reparo ou troca de vidros, faróis e retrovisores', false,
         ROW(1000,'BRL')::money_amount, ROW(15000,'BRL')::money_amount,
         ROW('FIXED', 250, NULL)::deductible, 0.004),

        (md5('pdc:cov:auto:naturalevents')::uuid, md5('pdc:pv:auto:1')::uuid,
         'NATURAL_EVENTS', 'Eventos da natureza',
         'Alagamento, granizo, queda de árvore e demais eventos naturais', false,
         ROW(10000,'BRL')::money_amount, ROW(500000,'BRL')::money_amount,
         ROW('PERCENTAGE', NULL, 0.03)::deductible, 0.006),

        (md5('pdc:cov:auto:driver')::uuid, md5('pdc:pv:auto:1')::uuid,
         'DRIVER_PA', 'Acidentes pessoais de passageiros',
         'Morte acidental e invalidez permanente de ocupantes', false,
         ROW(5000,'BRL')::money_amount, ROW(200000,'BRL')::money_amount,
         ROW('FIXED', 0, NULL)::deductible, 0.003)
    ON CONFLICT DO NOTHING;

    -- Coberturas do produto residencial
    INSERT INTO coverages (id, product_version_id, code, name, description, is_mandatory,
           min_limit, max_limit, default_deductible, rate_factor) VALUES
        (md5('pdc:cov:res:fire')::uuid, md5('pdc:pv:residential:1')::uuid,
         'FIRE', 'Incêndio, raio e explosão',
         'Danos ao imóvel e conteúdo por incêndio, queda de raio ou explosão', true,
         ROW(50000,'BRL')::money_amount, ROW(2000000,'BRL')::money_amount,
         ROW('FIXED', 1000, NULL)::deductible, 0.006),

        (md5('pdc:cov:res:theft')::uuid, md5('pdc:pv:residential:1')::uuid,
         'BURGLARY', 'Roubo de bens',
         'Subtração de bens mediante arrombamento ou grave ameaça', false,
         ROW(10000,'BRL')::money_amount, ROW(300000,'BRL')::money_amount,
         ROW('PERCENTAGE', NULL, 0.10)::deductible, 0.009),

        (md5('pdc:cov:res:electrical')::uuid, md5('pdc:pv:residential:1')::uuid,
         'ELECTRICAL', 'Danos elétricos',
         'Queima de equipamentos por variação de tensão', false,
         ROW(2000,'BRL')::money_amount, ROW(50000,'BRL')::money_amount,
         ROW('FIXED', 500, NULL)::deductible, 0.005),

        (md5('pdc:cov:res:liability')::uuid, md5('pdc:pv:residential:1')::uuid,
         'LIABILITY', 'Responsabilidade civil familiar',
         'Danos involuntários causados a terceiros pelo segurado ou familiares', false,
         ROW(20000,'BRL')::money_amount, ROW(500000,'BRL')::money_amount,
         ROW('FIXED', 750, NULL)::deductible, 0.004)
    ON CONFLICT DO NOTHING;

    INSERT INTO commission_rules (product_id, version, rate, base_on, valid_period) VALUES
        (v_auto, 1, 0.15, 'NET_PREMIUM', daterange('2026-01-01','2027-01-01')),
        (v_res,  1, 0.20, 'NET_PREMIUM', daterange('2026-01-01','2027-01-01'))
    ON CONFLICT DO NOTHING;
END $seed$;

-- ---------------------------------------------------------------- corretores, clientes, apólices
DO $seed$
DECLARE
    v_first text[] := ARRAY['Ana','Bruno','Carla','Diego','Elisa','Fábio','Gabriela','Henrique',
                            'Isabela','João','Karina','Lucas','Mariana','Nelson','Olívia','Paulo',
                            'Renata','Sérgio','Tatiana','Vitor'];
    v_last  text[] := ARRAY['Souza','Lima','Dias','Alves','Costa','Ramos','Pereira','Martins',
                            'Rocha','Barbosa','Nunes','Teixeira','Moreira','Cardoso','Freitas'];
    v_company text[] := ARRAY['Transportes','Comércio','Serviços','Indústria','Logística',
                              'Construtora','Distribuidora','Consultoria'];

    v_tenant uuid; v_user uuid; v_broker uuid; v_customer uuid; v_asset uuid;
    v_quotation uuid; v_proposal uuid; v_policy uuid; v_plan uuid;
    v_product uuid; v_pv uuid; v_rule uuid; v_claim uuid;
    v_seq bigint := 0;
    -- Contador global de veículos. A placa precisa ser única no banco inteiro
    -- (ux_vehicles_plate), então não pode derivar apenas do índice do laço interno.
    v_vehicle_n int := 0;
    v_plate text;
    v_is_business boolean;
    v_premium numeric(14,2);
    v_item uuid;
    v_plan_code text;
    v_mult numeric;
    v_cov record;
    v_cov_total int;
    v_cov_i int;
    v_cov_prem numeric(14,2);
    v_acc numeric(14,2);
    v_item_total numeric(14,2);
    v_item_net numeric(14,2);
    v_net numeric(14,2);
    v_start date;
    v_name text;
    t int; b int; c int; k int;
BEGIN
    FOR t IN 1..8 LOOP
        v_tenant := md5('pdc:brokerage:' || t::text)::uuid;
        PERFORM set_config('app.tenant_id', v_tenant::text, false);

        -- 3 a 6 corretores por corretora
        FOR b IN 1..(3 + (t % 4)) LOOP
            v_seq := v_seq + 1;
            v_user   := md5('pdc:user:' || v_seq::text)::uuid;
            v_broker := md5('pdc:broker:' || v_seq::text)::uuid;
            v_name := v_first[1 + (v_seq % 20)] || ' ' || v_last[1 + (v_seq % 15)];

            INSERT INTO users (id, tenant_id, email, password_hash, profile, display_name, created_by)
            VALUES (v_user, v_tenant,
                    lower(replace(v_name,' ','.')) || v_seq || '@corretora' || t || '.test',
                    '\x00', 'BROKER', v_name, '00000000-0000-0000-0000-000000000001')
            ON CONFLICT DO NOTHING;

            INSERT INTO brokers (id, tenant_id, user_id, susep_registration, full_name, hired_at, created_by)
            VALUES (v_broker, v_tenant, v_user, 'COR-' || lpad(v_seq::text, 6, '0'), v_name,
                    DATE '2023-01-01' + ((v_seq % 700)::int), '00000000-0000-0000-0000-000000000001')
            ON CONFLICT DO NOTHING;

            -- 8 a 15 clientes por corretor
            FOR c IN 1..(8 + (v_seq % 8)) LOOP
                v_customer := gen_random_uuid();
                v_is_business := (c % 4 = 0);
                v_asset := gen_random_uuid();

                IF v_is_business THEN
                    v_product := md5('pdc:product:residential')::uuid;
                    v_pv := md5('pdc:pv:residential:1')::uuid;

                    INSERT INTO customers (id, tenant_id, broker_id, kind, document_encrypted,
                           document_hash, legal_name, trade_name, cnae_code, company_size, created_by)
                    VALUES (v_customer, v_tenant, v_broker, 'BUSINESS', '\x01',
                            digest(v_customer::text, 'sha256'),
                            v_company[1 + (c % 8)] || ' ' || v_last[1 + (c % 15)] || ' LTDA',
                            v_company[1 + (c % 8)] || ' ' || v_last[1 + (c % 15)],
                            '4711-3', 'MEDIUM', '00000000-0000-0000-0000-000000000001');

                    INSERT INTO insurable_assets (id, tenant_id, customer_id, kind, declared_value, created_by)
                    VALUES (v_asset, v_tenant, v_customer, 'PROPERTY',
                            ROW(180000 + (c * 17000), 'BRL')::money_amount,
                            '00000000-0000-0000-0000-000000000001');

                    INSERT INTO properties (id, location, area_sqm, built_year, construction_type, property_usage)
                    VALUES (v_asset,
                            ROW('Rua das Palmeiras', (100 + c)::text, NULL, 'Centro',
                                'São Paulo', 'SP', '01310100')::postal_address,
                            80 + (c * 7), 2005 + (c % 15), 'MASONRY', 'COMMERCIAL');
                ELSE
                    v_product := md5('pdc:product:auto')::uuid;
                    v_pv := md5('pdc:pv:auto:1')::uuid;

                    INSERT INTO customers (id, tenant_id, broker_id, kind, document_encrypted,
                           document_hash, first_name, last_name, birth_date, occupation, created_by)
                    VALUES (v_customer, v_tenant, v_broker, 'INDIVIDUAL', '\x01',
                            digest(v_customer::text, 'sha256'),
                            v_first[1 + ((v_seq + c) % 20)], v_last[1 + ((v_seq * c) % 15)],
                            DATE '1970-01-01' + (((v_seq * c * 37) % 12000)::int), 'Analista',
                            '00000000-0000-0000-0000-000000000001');

                    INSERT INTO insurable_assets (id, tenant_id, customer_id, kind, declared_value, created_by)
                    VALUES (v_asset, v_tenant, v_customer, 'VEHICLE',
                            ROW(45000 + (c * 3500), 'BRL')::money_amount,
                            '00000000-0000-0000-0000-000000000001');

                    -- Placa no padrão antigo AAA0000, derivada de um contador global:
                    -- as três letras codificam a faixa e os quatro dígitos o resto,
                    -- o que garante unicidade sem depender de sorteio.
                    v_vehicle_n := v_vehicle_n + 1;
                    v_plate := chr(65 + ((v_vehicle_n / 10000 / 676) % 26))
                            || chr(65 + ((v_vehicle_n / 10000 / 26) % 26))
                            || chr(65 + ((v_vehicle_n / 10000) % 26))
                            || lpad((v_vehicle_n % 10000)::text, 4, '0');

                    INSERT INTO vehicles (id, plate, chassis, model_year, manufacture_year,
                           brand, model, usage, overnight_postal_code)
                    VALUES (v_asset, v_plate,
                            'SYN' || lpad(v_vehicle_n::text, 14, '0'),
                            2018 + (c % 7), 2017 + (c % 7), 'Sintética', 'Modelo ' || c,
                            'PERSONAL', '0' || lpad(((c * 137) % 9999999)::text, 7, '0'));
                END IF;

                INSERT INTO contacts (tenant_id, customer_id, kind, email, phone, is_primary)
                VALUES (v_tenant, v_customer, 'PERSONAL',
                        'cliente' || v_seq || c || '@exemplo.test',
                        '119' || lpad(((v_seq * c * 71) % 100000000)::text, 8, '0'), true);

                -- 60% dos clientes viram cotação; dessas, 60% viram proposta; dessas, 70% viram apólice
                IF (c % 5) < 3 THEN
                    v_quotation := gen_random_uuid();
                    v_premium := round((2000 + (c * 137) % 4000)::numeric, 2);
                    v_net := round(v_premium * 0.82, 2);

                    INSERT INTO quotations (id, tenant_id, broker_id, customer_id, asset_id,
                           product_version_id, number, status, risk_score, created_by, expires_at)
                    VALUES (v_quotation, v_tenant, v_broker, v_customer, v_asset, v_pv,
                            'CT-2026-' || lpad(nextval('app.quotation_number_seq')::text, 8, '0') || '-0',
                            (CASE WHEN (c % 5) = 0 THEN 'CALCULATED' ELSE 'CONVERTED' END)::quotation_status,
                            150 + ((c * 53) % 600), '00000000-0000-0000-0000-000000000001',
                            now() + interval '30 days');

                    -- Os três planos da cotação. COMPLETE vale exatamente v_premium,
                    -- que é o valor que a proposta e a apólice herdam — assim a
                    -- verificação POLICY_PREMIUM_MISMATCH continua zerada.
                    SELECT count(*) INTO v_cov_total
                      FROM coverages WHERE product_version_id = v_pv AND is_mandatory;

                    FOREACH v_plan_code IN ARRAY ARRAY['ESSENTIAL','COMPLETE','MASTER'] LOOP
                        v_mult := CASE v_plan_code WHEN 'ESSENTIAL' THEN 0.85
                                                   WHEN 'COMPLETE'  THEN 1.00
                                                   ELSE 1.28 END;
                        v_item := gen_random_uuid();
                        v_item_total := round(v_premium * v_mult, 2);
                        v_item_net   := round(v_item_total * 0.82, 2);

                        INSERT INTO quotation_items (id, quotation_id, plan, net_premium, total_premium)
                        VALUES (v_item, v_quotation, v_plan_code::plan_tier,
                                ROW(v_item_net,'BRL')::money_amount,
                                ROW(v_item_total,'BRL')::money_amount);

                        INSERT INTO calculation_snapshots (id, quotation_item_id, engine_version,
                               inputs, risk_multiplier, plan_multiplier, base_premium,
                               final_premium, calculated_at)
                        VALUES (gen_random_uuid(), v_item, '1.0.0',
                                jsonb_build_object(
                                    'baseRate', 0.0180,
                                    'riskScore', 150 + ((c * 53) % 600),
                                    'riskSensitivity', 0.35,
                                    'planMultiplier', v_mult,
                                    'loadingRate', 0.22,
                                    'seeded', true),
                                round(1 + ((150 + ((c * 53) % 600))::numeric / 1000) * 0.35, 6),
                                v_mult,
                                ROW(v_item_net,'BRL')::money_amount,
                                ROW(v_item_total,'BRL')::money_amount,
                                now() - interval '14 days');

                        -- Rateio das coberturas: a última absorve o resto para que a
                        -- soma feche com o prêmio do plano, sem centavo perdido
                        v_acc := 0; v_cov_i := 0;
                        FOR v_cov IN
                            SELECT id, (min_limit).amount AS min_l, (max_limit).amount AS max_l,
                                   default_deductible AS ded
                              FROM coverages
                             WHERE product_version_id = v_pv AND is_mandatory
                             ORDER BY code
                        LOOP
                            v_cov_i := v_cov_i + 1;
                            IF v_cov_i = v_cov_total THEN
                                v_cov_prem := v_item_total - v_acc;
                            ELSE
                                v_cov_prem := round(v_item_total / v_cov_total, 2);
                                v_acc := v_acc + v_cov_prem;
                            END IF;

                            INSERT INTO selected_coverages (id, quotation_item_id, coverage_id,
                                   limit_amount, deductible, premium)
                            VALUES (gen_random_uuid(), v_item, v_cov.id,
                                    ROW(least(greatest(round(
                                        (CASE WHEN v_is_business THEN 180000 + (c * 17000)
                                              ELSE 45000 + (c * 3500) END)::numeric * v_mult, 2),
                                        v_cov.min_l), v_cov.max_l), 'BRL')::money_amount,
                                    v_cov.ded,
                                    ROW(v_cov_prem,'BRL')::money_amount);
                        END LOOP;
                    END LOOP;

                    IF (c % 5) > 0 THEN
                        v_proposal := gen_random_uuid();

                        INSERT INTO proposals (id, tenant_id, quotation_id, broker_id, customer_id,
                               number, status, chosen_plan, net_premium, total_premium, created_by,
                               created_at, submitted_at, decided_at, issued_at)
                        VALUES (v_proposal, v_tenant, v_quotation, v_broker, v_customer,
                                'PR-2026-' || lpad(nextval('app.proposal_number_seq')::text, 8, '0') || '-0',
                                (CASE WHEN (c % 4) = 1 THEN 'UNDER_ANALYSIS' ELSE 'ISSUED' END)::proposal_status,
                                'COMPLETE', ROW(v_net,'BRL')::money_amount,
                                ROW(v_premium,'BRL')::money_amount,
                                '00000000-0000-0000-0000-000000000001',
                                now() - interval '12 days', now() - interval '10 days',
                                now() - interval '8 days',
                                -- ck_proposals_issued_status: issued_at existe se e somente se
                                -- o status for ISSUED
                                CASE WHEN (c % 4) <> 1 THEN now() - interval '6 days' END);

                        IF (c % 4) <> 1 THEN
                            v_policy := gen_random_uuid();
                            v_plan := gen_random_uuid();
                            v_start := DATE '2026-01-01' + ((c * 11) % 300);

                            INSERT INTO policies (id, tenant_id, proposal_id, broker_id, customer_id,
                                   asset_id, product_version_id, number, status, coverage_period,
                                   net_premium, total_premium, issued_by, correlation_id)
                            VALUES (v_policy, v_tenant, v_proposal, v_broker, v_customer, v_asset, v_pv,
                                    'PC-2026-' || lpad(nextval('app.policy_number_seq')::text, 8, '0') || '-0',
                                    'ACTIVE', daterange(v_start, v_start + 365),
                                    ROW(v_net,'BRL')::money_amount, ROW(v_premium,'BRL')::money_amount,
                                    '00000000-0000-0000-0000-000000000001', gen_random_uuid());

                            -- Coberturas CONGELADAS: copiadas do plano COMPLETE da cotação.
                            -- Uma apólice sem cobertura não segura nada, e é justamente o que
                            -- a verificação POLICY_WITHOUT_COVERAGE acusa.
                            INSERT INTO policy_coverages (id, tenant_id, policy_id, coverage_id,
                                   limit_amount, deductible, premium, is_mandatory)
                            SELECT gen_random_uuid(), v_tenant, v_policy, sc.coverage_id,
                                   sc.limit_amount, sc.deductible, sc.premium, true
                              FROM selected_coverages sc
                              JOIN quotation_items qi ON qi.id = sc.quotation_item_id
                             WHERE qi.quotation_id = v_quotation AND qi.plan = 'COMPLETE';

                            -- Trilha de auditoria da emissão: sem ela a cobertura de
                            -- auditoria não fecha em 1.0 (POLICY_WITHOUT_AUDIT)
                            INSERT INTO audit_events (id, occurred_at, tenant_id, correlation_id,
                                   actor_id, actor_profile, action, resource_type, resource_id,
                                   outcome, duration_ms, after_state)
                            VALUES (gen_random_uuid(), now() - interval '6 days', v_tenant,
                                    gen_random_uuid(),
                                    '00000000-0000-0000-0000-000000000001', 'BROKER',
                                    'POLICY_ISSUED', 'Policy', v_policy, 'SUCCESS', 42,
                                    jsonb_build_object('status','ACTIVE',
                                                       'totalPremium', v_premium));

                            -- Parcelas: a soma precisa bater com o prêmio (constraint trigger)
                            INSERT INTO installment_plans (id, tenant_id, policy_id, total_amount, installment_count)
                            VALUES (v_plan, v_tenant, v_policy, ROW(v_premium,'BRL')::money_amount, 4);

                            FOR k IN 1..4 LOOP
                                INSERT INTO installments (tenant_id, plan_id, sequence, amount, due_date, status, paid_at)
                                VALUES (v_tenant, v_plan, k,
                                        ROW(CASE WHEN k = 1
                                                 THEN v_premium - 3 * round(v_premium/4, 2)
                                                 ELSE round(v_premium/4, 2) END, 'BRL')::money_amount,
                                        v_start + (k * 30),
                                        (CASE WHEN k = 1 THEN 'PAID' ELSE 'PENDING' END)::installment_status,
                                        -- ck_installments_paid: paid_at existe se e somente se PAID
                                        CASE WHEN k = 1 THEN now() - interval '5 days' END);
                            END LOOP;

                            SELECT id INTO v_rule FROM commission_rules
                             WHERE product_id = v_product ORDER BY version DESC LIMIT 1;

                            INSERT INTO commissions (tenant_id, policy_id, broker_id, rule_id,
                                   rule_version, rate_applied, base_amount, amount, status, reference_month)
                            VALUES (v_tenant, v_policy, v_broker, v_rule, 1,
                                    CASE WHEN v_is_business THEN 0.20 ELSE 0.15 END,
                                    ROW(v_net,'BRL')::money_amount,
                                    ROW(round(v_net * CASE WHEN v_is_business THEN 0.20 ELSE 0.15 END, 2),
                                        'BRL')::money_amount,
                                    (CASE WHEN (c % 3) = 0 THEN 'RELEASED' ELSE 'FORECAST' END)::commission_status,
                                    date_trunc('month', v_start)::date);

                            -- Alguns sinistros, sempre dentro da vigência
                            -- c=6 e c=12 satisfazem as condições de cotação, proposta e apólice
                            IF (c % 6) = 0 THEN
                                INSERT INTO claims (tenant_id, policy_id, broker_id, number, status,
                                       occurrence_date, description, estimated_amount, correlation_id)
                                VALUES (v_tenant, v_policy, v_broker,
                                        'SN-2026-' || lpad(nextval('app.claim_number_seq')::text, 8, '0'),
                                        'UNDER_ANALYSIS',
                                        LEAST(v_start + 40, CURRENT_DATE),
                                        'Evento sintético para demonstração',
                                        ROW(round(v_premium * 2, 2),'BRL')::money_amount,
                                        gen_random_uuid())
                                RETURNING id INTO v_claim;

                                -- Linha do tempo append-only: todo sinistro nasce com o
                                -- evento de aviso, e a análise entra como segundo passo.
                                INSERT INTO claim_events (tenant_id, claim_id, sequence, kind,
                                       description, occurred_at, recorded_by) VALUES
                                    (v_tenant, v_claim, 1, 'REPORTED',
                                     'Aviso de sinistro registrado pelo corretor',
                                     now() - interval '5 days',
                                     '00000000-0000-0000-0000-000000000001'),
                                    (v_tenant, v_claim, 2, 'DOCUMENTS_REQUESTED',
                                     'Documentação solicitada ao segurado',
                                     now() - interval '3 days',
                                     '00000000-0000-0000-0000-000000000001'),
                                    (v_tenant, v_claim, 3, 'UNDER_ANALYSIS',
                                     'Sinistro em análise técnica',
                                     now() - interval '1 day',
                                     '00000000-0000-0000-0000-000000000001');
                            END IF;
                        END IF;
                    END IF;
                END IF;
            END LOOP;
        END LOOP;
    END LOOP;
END $seed$;

-- Atualiza os indicadores consolidados do perfil regulatório
REFRESH MATERIALIZED VIEW regulatory.brokerage_indicators;
REFRESH MATERIALIZED VIEW regulatory.compliance_indicators;

SELECT 'corretoras=' || (SELECT count(*) FROM brokerages)
    || ' corretores=' || (SELECT count(*) FROM brokers)
    || ' clientes='   || (SELECT count(*) FROM customers)
    || ' cotacoes='   || (SELECT count(*) FROM quotations)
    || ' propostas='  || (SELECT count(*) FROM proposals)
    || ' apolices='   || (SELECT count(*) FROM policies)
    || ' parcelas='   || (SELECT count(*) FROM installments)
    || ' comissoes='  || (SELECT count(*) FROM commissions)
    || ' sinistros='  || (SELECT count(*) FROM claims) AS resumo;
