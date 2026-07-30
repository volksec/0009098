# ADR-0006 — Outbox transacional no PostgreSQL

**Status:** Aceito · **Data:** 2026-07-30

## Contexto

A emissão de apólice precisa confirmar o estado **e** publicar eventos. Escrever no banco e
publicar em um broker são duas operações que não compartilham transação — o problema clássico de
*dual write*: se o commit passa e a publicação falha, o evento some; se publica antes e o commit
falha, o evento é uma mentira.

## Decisão

Outbox transacional: o evento é gravado como linha em `outbox_messages` **na mesma transação** que
altera o estado. Um worker separado lê com `FOR UPDATE SKIP LOCKED`, publica e marca como
processado. Consumidores são idempotentes por `message_id`, registrado em `processed_messages`.

Entrega **ao menos uma vez**, com consumo idempotente — e não exatamente-uma-vez, que é
inalcançável sem coordenação distribuída.

## Alternativas consideradas

**Publicar direto no handler** — descartado: é o dual write, com perda ou fantasma de evento.

**Two-phase commit** — descartado: exige coordenador transacional, degrada performance e tem
suporte irregular. Complexidade desproporcional ao problema.

**Change Data Capture (Debezium)** — descartado: solução sólida, mas adiciona Kafka e conector ao
ambiente. Para o volume deste sistema, a Outbox com `SKIP LOCKED` entrega a mesma garantia com uma
dependência a menos.

## Consequências

- Latência de publicação = intervalo de polling (100 ms). Aceitável: nada no fluxo depende de
  publicação síncrona.
- `SKIP LOCKED` permite múltiplos workers sem contenção nem duplicação.
- A tabela é particionada por mês e tem índice parcial `WHERE processed_at IS NULL`, mantendo o
  índice quente pequeno mesmo com milhões de mensagens históricas.
- Handlers que exigem consistência forte (geração de parcelas, cálculo de comissão) rodam
  in-process na mesma transação, não pela Outbox — a Outbox é para integração, não para invariante.
