# ADR-0007 — Sem RabbitMQ na versão inicial

**Status:** Aceito · **Data:** 2026-07-30

## Contexto

A especificação permite mensageria "somente quando houver justificativa". É preciso decidir se um
broker externo agrega valor real neste sistema.

## Decisão

**Não usar RabbitMQ nesta versão.** A Outbox no PostgreSQL com `SKIP LOCKED` cumpre o papel de fila
para o volume do sistema, com uma dependência a menos para operar e — o ponto decisivo — com
garantia transacional que um broker externo não daria sem 2PC.

## Alternativas consideradas

**RabbitMQ desde o início** — descartado. Adicionaria um componente para operar, monitorar e
recuperar, sem resolver nenhum problema que a Outbox não resolva. Seria arquitetura por currículo,
não por necessidade — exatamente o tipo de decisão que um Tech Lead avalia negativamente.

**Redis Streams** — descartado. O Redis já está no ambiente para cache, mas a durabilidade é mais
fraca que a do PostgreSQL, e mensagens de domínio precisam sobreviver a reinício.

## Consequências

- Menos um contêiner, menos um ponto de falha, menos um dashboard.
- Se o volume crescer a ponto de o polling virar gargalo, o dispatcher passa a publicar em um
  broker — a Outbox continua sendo o ponto de origem, então a mudança é local e não toca o domínio.
- A decisão é registrada explicitamente para que a ausência do broker seja lida como escolha
  deliberada, não como omissão.
