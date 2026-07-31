# Fase 3 — Modelo físico verificado

**Status:** concluída e **verificada contra PostgreSQL 16.4 real** · **Data:** 2026-07-31

Ambiente: Docker 29.6.2 · WSL 2 (kernel 6.18) · PostgreSQL 16.4 Alpine · 16 CPUs · 16 GB

## Resultado

| Verificação | Resultado |
|---|---|
| Aplicação das 9 migrations | ✅ 9/9 sem erro |
| Objetos criados | 71 tabelas · 224 índices · 3 tipos compostos · 5 domains · 16 enums · 80 partições · 3 views · 2 matviews · 7 triggers |
| RLS com `FORCE` | ✅ 29 tabelas · 58 políticas |
| Constraints de exclusão | ✅ 3 (`ex_policies_no_overlap`, produto, comissão) |
| Invariantes bloqueando | ✅ 13/13 |
| Isolamento multi-tenant | ✅ 8/8 |
| Rollback completo | ✅ 0 tabelas, 0 tipos, 0 enums, 0 domains |
| Ciclo `migrate → rollback → migrate` | ✅ reaplicou 71 tabelas em base limpa |

## Invariantes verificadas (13/13)

Cada teste **tenta violar** a invariante e espera ser bloqueado
(`database/secure/checks/verify-invariants.sql`):

| Invariante | Mecanismo | Erro retornado |
|---|---|---|
| Emissão duplicada por proposta | `ux_policies_proposal` | `duplicate key value violates unique constraint` |
| Sobreposição de vigência | `ex_policies_no_overlap` (GiST) | `conflicting key value violates exclusion constraint` |
| Prêmio negativo | `ck_policies_premium_positive` | `violates check constraint` |
| TPH incoerente (PF com razão social) | `ck_customers_individual_fields` | `violates check constraint` |
| TPT quebrada (veículo em asset PROPERTY) | FK composta `(id, kind)` | `violates foreign key constraint` |
| Regulador com tenant | `ck_users_tenant_by_profile` | `violates check constraint` |
| Regulador sem MFA | `ck_users_regulator_requires_mfa` | `violates check constraint` |
| Placa duplicada | `ux_vehicles_plate` | `duplicate key value` |
| Auditoria mutável | `tg_audit_immutable` | `Tabela audit_events é append-only` |
| Consentimento mutável | `tg_consents_immutable` | `Tabela consents é append-only` |
| Sinistro fora da vigência | `tg_claims_within_coverage` | `Data do evento fora da vigência da apólice` |
| Σ parcelas ≠ prêmio | `tg_installments_sum` | `Soma das parcelas (2300.00) difere do total (2400.00)` |
| Comissão acima do teto | `ck_commission_rate` | `violates check constraint` |

Caso positivo: `Money.Allocate` produzindo `333,34 + 333,33 + 333,33 = 1.000,00` é **aceito**.

## Isolamento multi-tenant verificado (8/8)

Executado conectando como `app_user` — o papel real da aplicação, **sem** `BYPASSRLS`
(`database/secure/checks/verify-rls.sql`):

| # | Cenário | Resultado |
|---|---|---|
| 1 | Consulta **sem** contexto de tenant | `0 linhas` — falha fechado |
| 2 | Contexto = tenant Alfa | vê apenas "Cliente Alfa" |
| 3 | **IDOR**: `WHERE id = <cliente do tenant Beta>` | `0 linhas` |
| 4 | `INSERT` com `tenant_id` forjado | `new row violates row-level security policy` |
| 5 | `UPDATE` movendo cliente para outro tenant | `new row violates row-level security policy` |
| 6 | Contexto = tenant Beta | vê apenas "Cliente Beta" |
| 7 | `DELETE` físico | `permission denied for table customers` |
| 8 | `UPDATE` em auditoria | `permission denied for table audit_events` |

O cenário 1 é o mais importante: sem `SET LOCAL app.tenant_id`, `app.current_tenant()` retorna
`NULL`, e `tenant_id = NULL` é `NULL` — nunca verdadeiro. A ausência de contexto **nega**, em vez
de liberar.

Os cenários 4 e 5 provam que a política tem `WITH CHECK`, e não apenas `USING`: sem isso, um
corretor não conseguiria *ler* dados de outro tenant, mas conseguiria *escrever* neles.

## Bugs reais encontrados e corrigidos

Executar o SQL revelou quatro defeitos que nenhuma revisão de código teria pego.

### 1. `cap_drop: ALL` impedia o PostgreSQL de iniciar

```
error: failed switching to "postgres": operation not permitted
```

O entrypoint da imagem inicia como root, ajusta a propriedade do `PGDATA` e troca para o usuário
`postgres` — o que exige `SETUID`/`SETGID`. Com todas as capabilities descartadas, a troca falha e
o contêiner nunca sobe.

**Correção:** manter `cap_drop: ALL` e devolver apenas o mínimo — `CHOWN`, `DAC_OVERRIDE`,
`FOWNER`, `SETUID`, `SETGID`. O endurecimento continua, mas agora funciona.

### 2. Nome de banco com hífen

O rename global do projeto transformou o nome do banco em `portal-do-corretor`, com hífens — o que
exige aspas em toda referência SQL e quebra `pg_isready`. **Correção:** `portal_do_corretor`, e
usuário `pdc_migrator`.

### 3. Interpolação do psql não funciona dentro de `DO $$ ... $$`

```
ERROR: syntax error at or near ":"
```

A substituição `:'variavel'` acontece no **cliente**, antes de enviar o comando. Dentro de um bloco
*dollar-quoted*, o conteúdo é uma string literal que o psql não processa — o servidor recebia o
`:` cru.

**Correção:** trocar o bloco `DO` por `SELECT format(...) \gexec`, que avalia o `format()` como
consulta normal e executa o resultado. O `%L` continua fazendo o escape do literal, então a senha
nunca é concatenada sem tratamento.

### 4. Ordem de rollback não é o inverso da criação

```
ERROR: cannot drop function app.regulatory_scope_current()
       because other objects depend on it
```

A função é referenciada pelas **políticas de RLS** de `brokers` e `users`. O rollback a removia na
seção V002, antes dessas tabelas.

**Correção:** mover o `DROP FUNCTION` para depois das tabelas. A lição é que a ordem de rollback é
o inverso das **dependências**, não o inverso da ordem de criação — e só um rollback executado de
verdade expõe a diferença.

## Uma limitação de teste que vale registrar

O primeiro resultado apontou `tg_installments_sum` como não bloqueando. A trigger está correta; o
**teste** é que estava errado.

A trigger é `DEFERRABLE INITIALLY DEFERRED`, então dispara no `COMMIT`. Meu harness envolvia cada
tentativa em uma função PL/pgSQL com `EXCEPTION WHEN others` — o que cria uma *subtransação*, e
constraints deferidas não são avaliadas no fim de uma subtransação.

Verificada em uma transação real com `COMMIT`, a trigger bloqueia corretamente:

```
ERROR:  Soma das parcelas (2300.00) difere do total do plano (2400.00)
HINT:   Use Money.Allocate para dividir o prêmio sem perder centavos.
```

Fica documentado porque é um erro fácil de repetir: um teste que passa contra constraint imediata
pode dar falso negativo contra constraint deferida.

## Como reproduzir

```bash
docker compose up -d secure-database redis
```

```bash
for f in database/secure/migrations/V*.sql; do
  docker exec -i pdc-secure-db psql -U pdc_migrator -d portal_do_corretor -v ON_ERROR_STOP=1 -q < "$f"
done
```

```bash
docker exec -i pdc-secure-db psql -U pdc_migrator -d portal_do_corretor -q < database/secure/checks/verify-invariants.sql
```

```bash
docker exec -i pdc-secure-db psql -U pdc_migrator -d portal_do_corretor -q < database/secure/checks/verify-rls.sql
```

## Próximo passo

Fase 4 — mapeamentos EF Core (Value Object → tipo composto, `xmin` como token de concorrência,
herança TPH/TPT), repositórios e os testes de integração com Testcontainers para concorrência na
emissão, idempotência e processamento da Outbox.
