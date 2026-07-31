# ADR-0004 — Isolamento multi-tenant em cinco camadas

**Status:** Aceito · **Data:** 2026-07-30

## Contexto

Cada corretora é um tenant. Vazamento entre tenants é a falha mais grave possível neste domínio.
Confiar em uma única camada — qualquer que seja — é o equivalente a confiar em uma única validação.

## Decisão

Cinco camadas independentes: (1) `tenant_id` derivado exclusivamente do claim do token assinado;
(2) contexto de tenant imutável por requisição; (3) query filter global do EF Core; (4) autorização
por recurso (RBAC + ABAC); (5) Row-Level Security no PostgreSQL com `FORCE ROW LEVEL SECURITY`.

O tipo `TenantId` **não tem construtor público que aceite entrada de usuário** — a manipulação via
payload é impedida pelo sistema de tipos, não por validação em runtime.

## Alternativas consideradas

**Banco por tenant** — descartado. Isolamento mais forte, mas inviabiliza as consultas consolidadas
do perfil regulatório (que são requisito, não opcional) e multiplica o custo de migration por
tenant.

**Schema por tenant** — descartado. Meio-termo que complica migrations e connection pooling sem
resolver o problema regulatório.

**Apenas query filter do ORM** — descartado como camada única. Um SQL cru, um
`IgnoreQueryFilters()` esquecido ou um endpoint novo sem o filtro derrubam o isolamento inteiro:
a garantia fica na disciplina de quem escreve o código, não na infraestrutura.

## Consequências

- Cada requisição executa `SET LOCAL app.tenant_id` — custo mensurável e aceito.
- A RLS torna o debug mais sutil: uma consulta pode "não retornar nada" por política, não por
  ausência de dado. Mitigado pelo Live Processing Console, que mostra a RLS sendo aplicada.
- O teste de isolamento derruba cada camada isoladamente e prova que as demais seguram — é a
  evidência que transforma "defesa em profundidade" de slogan em fato demonstrável.
