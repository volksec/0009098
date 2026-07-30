# ADR-0008 — CQRS seletivo, sem event sourcing

**Status:** Aceito · **Data:** 2026-07-30

## Contexto

Escrita e leitura têm necessidades opostas: comandos precisam do agregado completo e das
invariantes; consultas precisam de projeção rápida sobre muitas linhas.

## Decisão

CQRS **seletivo**, aplicado onde paga:

- Comandos: agregado + EF Core, com invariantes e transação.
- Consultas simples: projeção direta para DTO, sem materializar o agregado.
- Relatórios regulatórios: Dapper + materialized view.
- Dashboards: projeção cacheada em Redis, invalidada por evento.

Mesmo banco, mesmas tabelas. **Sem** event sourcing e **sem** banco de leitura separado.

## Alternativas consideradas

**CQRS completo com read model separado** — descartado: exige sincronização, introduz defasagem
observável e dobra o esforço de manutenção. O gargalo aqui não é leitura concorrente em escala.

**Event sourcing** — descartado. É tentador porque o domínio já emite eventos, mas traria
versionamento de evento, replay, snapshots e projeções para resolver um problema de auditoria que
`audit_events` particionado já resolve — com consulta SQL trivial em vez de reconstrução de estado.

**Sem CQRS algum** — descartado: carregar o agregado `Customer` completo, com contatos, endereços,
consentimentos e bens, para exibir cinco colunas em uma lista é desperdício mensurável.

## Consequências

- Duas formas de ler dados (EF Core e Dapper), o que exige critério claro — documentado.
- Consultas de leitura não passam pelo agregado, então **não** herdam suas invariantes; por isso a
  RLS existe: a segurança não pode depender do caminho de leitura escolhido.
