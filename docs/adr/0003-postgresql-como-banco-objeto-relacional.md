# ADR-0003 — PostgreSQL como banco objeto-relacional

**Status:** Aceito · **Data:** 2026-07-30

## Contexto

O critério principal de avaliação do case é a qualidade do banco objeto-relacional: modelagem,
integridade, segurança, performance, rastreabilidade e integração com os objetos de domínio.

## Decisão

PostgreSQL 16, usando os recursos objeto-relacionais como parte central do desenho, não como
detalhe: tipos compostos para Value Objects, domains para validação reutilizável, enums, arrays,
`daterange` com constraint de exclusão, RLS com `FORCE`, particionamento, índices parciais e GIN,
colunas geradas, `xmin` para optimistic locking e `SKIP LOCKED` para a Outbox.

## Alternativas consideradas

**MySQL/MariaDB** — descartado. Sem RLS nativo, sem tipos compostos, sem constraint de exclusão,
sem índice parcial. Metade das demonstrações do case seria impossível.

**SQL Server** — descartado. Tem RLS e é competente, mas o licenciamento atrapalha um case aberto
distribuído em containers, e não oferece tipos compostos nem `EXCLUDE`.

**MongoDB** — descartado. O domínio é intensamente relacional (cliente → bem → cotação → proposta →
apólice → parcela → comissão → sinistro) e exige integridade referencial e transações
multi-documento. Trocar isso por flexibilidade de esquema resolveria um problema que não temos e
criaria vários que teríamos.

## Consequências

- O banco carrega parte da garantia de correção, não apenas armazena dados.
- Testes de integração exigem PostgreSQL real (Testcontainers); banco em memória fica proibido
  porque não implementa nada disso — testar contra SQLite daria confiança falsa exatamente nos
  pontos que o case precisa provar.
- Alguns recursos (tipos compostos, `EXCLUDE`, RLS) exigem SQL puro nas migrations, além do EF Core.
- O acoplamento ao PostgreSQL é deliberado e assumido: portabilidade entre bancos não é requisito,
  e buscá-la significaria abrir mão exatamente do que dá valor ao case.
