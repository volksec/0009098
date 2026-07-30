# ADR-0002 — Monólito modular em vez de microserviços

**Status:** Aceito · **Data:** 2026-07-30

## Contexto

O sistema tem 16 bounded contexts e precisa ser construído e mantido por **uma pessoa**. As
invariantes mais críticas (emissão de apólice com coberturas, parcelas, comissão, evento e
auditoria) exigem atomicidade.

## Decisão

Monólito modular: um processo, um banco, módulos com fronteira forte. Clean Architecture dentro de
cada módulo; comunicação entre módulos apenas via `<Modulo>.Contracts` ou eventos de integração
pela Outbox. As fronteiras são verificadas por NetArchTest, então falham o build se erodirem.

## Alternativas consideradas

**Microserviços** — descartado. A emissão de apólice viraria saga distribuída com compensação,
introduzindo estados intermediários visíveis ao usuário e triplicando a complexidade do fluxo mais
importante do case. Ganharia escalabilidade independente que este sistema não precisa e custaria
operação que uma pessoa não sustenta.

**Monólito em camadas tradicional** (Controllers → Services → Repositories, camadas horizontais) —
descartado. Camadas horizontais não impõem fronteira de domínio: qualquer service acaba chamando
qualquer repository e, em poucos meses, o "módulo" existe apenas no nome das pastas.

## Consequências

- Deploy único, transação única, depuração simples — vantagens reais no escopo declarado.
- Risco de erosão das fronteiras mitigado por teste arquitetural, não por disciplina.
- Extração futura preparada: AI → Documents → Notifications → Regulatory, nessa ordem de atrito.
- Core Domain (Quotations, Proposals, Policies, Commissions) permanece junto por exigência
  transacional; extraí-lo seria um erro, não uma evolução.
