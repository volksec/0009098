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
        v_id := ('a0000000-0000-4000-8000-' || lpad(i::text, 12, '0'))::uuid;

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
    v_auto uuid := 'c0000000-0000-4000-8000-000000000001';
    v_res  uuid := 'c0000000-0000-4000-8000-000000000002';
BEGIN
    INSERT INTO insurance_products (id, code, name, branch) VALUES
        (v_auto, 'AUTO-STD', 'Auto Proteção Total', 'AUTO'),
        (v_res,  'RES-STD',  'Residencial Completo', 'RESIDENTIAL')
    ON CONFLICT DO NOTHING;

    INSERT INTO product_versions (id, product_id, version, branch, base_rate, risk_sensitivity,
           max_acceptable_risk, min_insured_value, max_insured_value, coverage_cap,
           questionnaire_schema, published_at, valid_period)
    VALUES ('d0000000-0000-4000-8000-000000000001', v_auto, 1, 'AUTO', 0.045, 0.35,
            800, 10000, 500000, ROW(500000,'BRL')::money_amount,
            jsonb_build_object('type','object'), now(), daterange('2026-01-01','2027-01-01')),
           ('d0000000-0000-4000-8000-000000000002', v_res, 1, 'RESIDENTIAL', 0.012, 0.25,
            850, 50000, 2000000, ROW(2000000,'BRL')::money_amount,
            jsonb_build_object('type','object'), now(), daterange('2026-01-01','2027-01-01'))
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
    v_product uuid; v_pv uuid; v_rule uuid;
    v_seq bigint := 0;
    -- Contador global de veículos. A placa precisa ser única no banco inteiro
    -- (ux_vehicles_plate), então não pode derivar apenas do índice do laço interno.
    v_vehicle_n int := 0;
    v_plate text;
    v_is_business boolean;
    v_premium numeric(14,2);
    v_net numeric(14,2);
    v_start date;
    v_name text;
    t int; b int; c int; k int;
BEGIN
    FOR t IN 1..8 LOOP
        v_tenant := ('a0000000-0000-4000-8000-' || lpad(t::text, 12, '0'))::uuid;
        PERFORM set_config('app.tenant_id', v_tenant::text, false);

        -- 3 a 6 corretores por corretora
        FOR b IN 1..(3 + (t % 4)) LOOP
            v_seq := v_seq + 1;
            v_user   := ('b0000000-0000-4000-8000-' || lpad(v_seq::text, 12, '0'))::uuid;
            v_broker := ('b1000000-0000-4000-8000-' || lpad(v_seq::text, 12, '0'))::uuid;
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
                    v_product := 'c0000000-0000-4000-8000-000000000002';
                    v_pv := 'd0000000-0000-4000-8000-000000000002';

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
                    v_product := 'c0000000-0000-4000-8000-000000000001';
                    v_pv := 'd0000000-0000-4000-8000-000000000001';

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
                                        gen_random_uuid());
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
