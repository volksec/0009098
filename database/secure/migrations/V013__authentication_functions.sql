-- =====================================================================
-- V013 — A autenticação precisa atravessar o isolamento, uma vez só
-- =====================================================================
--
-- Problema: o login lê `users` para descobrir quem é o usuário e a qual corretora
-- ele pertence. Mas a política p_users_tenant exige app.current_tenant(), e antes
-- de autenticar não existe tenant corrente — é justamente o que se quer descobrir.
-- Resultado: a consulta de login enxergava zero linhas e toda senha correta era
-- recusada como se fosse errada.
--
-- Alternativas descartadas:
--
--   · Dar BYPASSRLS ao app_user — abriria TODAS as tabelas para TODAS as consultas
--     da aplicação para resolver uma consulta só. O remédio seria pior.
--
--   · Política que permita ler `users` sem tenant — transformaria a tabela de
--     usuários em lista pública dentro da aplicação, e quem fizesse SELECT em
--     qualquer ponto do código veria todo mundo.
--
-- Escolha: três funções SECURITY DEFINER, cada uma fazendo exatamente um passo do
-- login e nada além. A travessia do isolamento fica confinada a elas, com nome
-- próprio e superfície declarada — em vez de espalhada por uma permissão ampla.
--
-- O que as torna seguras:
--   1. recebem apenas o e-mail ou o id, nunca SQL;
--   2. devolvem só o necessário para autenticar — nenhuma outra tabela é alcançável;
--   3. search_path fixo, fechando o sequestro de resolução de nomes;
--   4. EXECUTE restrito a app_user, revogado de PUBLIC.
--
-- O hash devolvido não é segredo utilizável: é PBKDF2 com 210 mil iterações, e quem
-- já pode executar a função é a própria aplicação que faria a verificação de todo jeito.

-- ---------------------------------------------------------------- BUSCA
CREATE OR REPLACE FUNCTION app.authenticate_lookup(p_email text)
RETURNS TABLE (
    id              uuid,
    tenant_id       uuid,
    profile         text,
    display_name    text,
    password_hash   bytea,
    failed_attempts smallint,
    locked_until    timestamptz,
    broker_id       uuid,
    tenant_name     text
)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = app, public, pg_temp
AS $$
    SELECT u.id, u.tenant_id, u.profile::text, u.display_name, u.password_hash,
           u.failed_attempts, u.locked_until,
           (SELECT b.id FROM brokers b WHERE b.user_id = u.id),
           (SELECT br.trade_name FROM brokerages br WHERE br.id = u.tenant_id)
      FROM users u
     WHERE u.email = p_email::citext
       AND u.deleted_at IS NULL;
$$;

COMMENT ON FUNCTION app.authenticate_lookup(text) IS
    'Única leitura de users que atravessa o tenant, porque antes de autenticar não há '
    'tenant a respeitar. Devolve só o necessário para verificar credencial.';

-- ---------------------------------------------------------------- FALHA
CREATE OR REPLACE FUNCTION app.register_login_failure(
    p_user uuid, p_max int, p_lock_minutes int)
RETURNS void
LANGUAGE sql
SECURITY DEFINER
SET search_path = app, public, pg_temp
AS $$
    UPDATE users
       SET failed_attempts = failed_attempts + 1,
           -- Bloqueio temporário: freia força bruta sem dar a um atacante a
           -- capacidade de trancar a conta alheia em definitivo
           locked_until = CASE
               WHEN failed_attempts + 1 >= p_max
               THEN now() + make_interval(mins => p_lock_minutes)
           END
     WHERE id = p_user;
$$;

-- ---------------------------------------------------------------- SUCESSO
CREATE OR REPLACE FUNCTION app.register_login_success(p_user uuid)
RETURNS void
LANGUAGE sql
SECURITY DEFINER
SET search_path = app, public, pg_temp
AS $$
    -- Zera o contador: as tentativas precisam ser seguidas para bloquear
    UPDATE users
       SET failed_attempts = 0, locked_until = NULL, last_login_at = now()
     WHERE id = p_user;
$$;

-- ---------------------------------------------------------------- PRIVILÉGIOS
REVOKE ALL ON FUNCTION app.authenticate_lookup(text)             FROM PUBLIC;
REVOKE ALL ON FUNCTION app.register_login_failure(uuid, int, int) FROM PUBLIC;
REVOKE ALL ON FUNCTION app.register_login_success(uuid)          FROM PUBLIC;

GRANT EXECUTE ON FUNCTION app.authenticate_lookup(text)             TO app_user;
GRANT EXECUTE ON FUNCTION app.register_login_failure(uuid, int, int) TO app_user;
GRANT EXECUTE ON FUNCTION app.register_login_success(uuid)          TO app_user;

-- ---------------------------------------------------------------- DEMONSTRAÇÃO
-- Uma conta por corretora, para que a tela de login permita comparar tenants.
--
-- Em sistema real listar e-mails seria vazamento. Aqui a base é sintética e gerada por
-- seed: sem esta lista ninguém adivinharia um endereço para entrar, e o case ficaria
-- inavaliável. Devolve apenas e-mail e nome de exibição — nunca hash, nunca id.
CREATE OR REPLACE FUNCTION app.demo_accounts()
RETURNS TABLE (email text, nome text, corretora text)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = app, public, pg_temp
AS $$
    SELECT DISTINCT ON (u.tenant_id)
           u.email::text, u.display_name, br.trade_name
      FROM users u
      JOIN brokerages br ON br.id = u.tenant_id
     WHERE u.deleted_at IS NULL AND u.profile = 'BROKER'
     ORDER BY u.tenant_id, u.email;
$$;

COMMENT ON FUNCTION app.demo_accounts() IS
    'Vitrine da massa sintética: uma conta por corretora para a tela de login. '
    'Não devolve hash nem identificador — só o suficiente para escolher um tenant.';

REVOKE ALL ON FUNCTION app.demo_accounts() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.demo_accounts() TO app_user;
