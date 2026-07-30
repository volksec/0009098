# Plano de implementação

Entrega incremental em 10 fases. Cada fase tem entregáveis, critério de pronto e uma demonstração
verificável — nada é considerado concluído por afirmação, apenas por evidência executável.

| Fase | Escopo | Critério de pronto |
|---|---|---|
| **1 ✅** | Nome, conceito, escopo, requisitos, casos de uso, bounded contexts, modelo de domínio, agregados, VOs, modelo físico, ER, arquitetura, estrutura, plano, ADRs | Documentação revisável e coerente entre si |
| **2** | Fundação: solução .NET, SharedKernel, VOs com testes, Docker Compose base, PostgreSQL, CI mínima | `docker compose up` sobe banco; testes de VO passam; NetArchTest ativo |
| **3** | Banco: migrations completas, tipos compostos, constraints, índices, RLS, particionamento, Outbox, seeds sintéticos | Migrations aplicam e revertem em base limpa; teste de RLS e de constraint passam com PostgreSQL real |
| **4** | Domínio + API do núcleo: Identity, Customers, Products, Quotations, Proposals, Policies; autenticação, autorização, tenant, idempotência, optimistic locking, auditoria, Outbox | Fluxo cliente → cotação → proposta → apólice funciona ponta a ponta; teste de concorrência prova emissão única |
| **5** | Billing, Commissions, Claims, Documents, Notifications, workers | Parcelas, comissão, endosso, renovação e sinistro funcionais; invariante Σ parcelas = prêmio testada |
| **6** | Frontend: design system, autenticação, dashboards, telas de cliente, cotação, proposta, apólice, comissão, sinistro | Telas operando contra a API real; Storybook publicado |
| **7** | Observabilidade + laboratórios: OTel, Prometheus, Grafana, Loki, Tempo, Live Processing Console, Query Inspector, Database Explorer, Engineering Lab | 24 passos observáveis na emissão; `EXPLAIN` real no Query Inspector; benchmarks medidos e versionados |
| **8** | Security Lab: `vulnerable-api`, banco vulnerável, Attack Simulator com 18 cenários e réplica automática | Cada cenário mostra o controle que falhou e o que bloqueou, com CWE/OWASP/ASVS e teste vinculado |
| **9** | Regulatory + IA: perfil regulatório, acesso justificado, mascaramento, indicadores, 5 agentes com guardrails | Restrições negativas do regulador verificadas por teste; teste de prompt injection passa |
| **10** | DevSecOps + vitrine: SAST, SCA, gitleaks, scan de imagem, SBOM, assinatura, GitHub Pages, Recruiter Mode, threat model, guia de apresentação | Pipeline verde; Pages publicado declarando uso de mocks; jornada de 10–15 min completa |

## Ordem e dependências

```mermaid
graph LR
    F1[1 · Concepção] --> F2[2 · Fundação] --> F3[3 · Banco] --> F4[4 · Núcleo]
    F4 --> F5[5 · Billing/Claims]
    F4 --> F6[6 · Frontend]
    F5 --> F7[7 · Observabilidade]
    F6 --> F7
    F7 --> F8[8 · Security Lab]
    F7 --> F9[9 · Regulatory + IA]
    F8 --> F10[10 · DevSecOps + Pages]
    F9 --> F10
```

O banco vem **antes** da API deliberadamente: o case é avaliado principalmente pelo modelo
objeto-relacional, então o esquema, as constraints e a RLS precisam estar corretos e testados
antes de existir código que dependa deles.

## Riscos e mitigações

| Risco | Impacto | Mitigação |
|---|---|---|
| Escopo muito grande para uma pessoa | Entrega incompleta | Fases 2–5 são o núcleo defensável; 6–10 agregam valor mas o case já se sustenta com o banco, o domínio e os testes |
| Benchmarks não reprodutíveis | Perda de credibilidade | Seed fixa, massa determinística, especificação da máquina publicada junto com os números |
| Laboratório vulnerável vazar | Risco real de segurança | Profile Docker separado, rede sem rota externa, nunca no Pages, aviso visual permanente, reset automático |
| Agentes de IA virarem enfeite | Ruído no case | Cada agente resolve um problema concreto do avaliador (revisar query, explicar agregado, mapear ASVS); sem agente sem propósito |
| Documentação divergir do código | Case perde consistência | Diagramas em Mermaid versionados; Database Explorer lê o catálogo real; testes referenciam requisitos |

## Como o case é apresentado

O ponto central da apresentação, em uma frase: **o banco de dados, os objetos de domínio, as
regras, as transações, os controles de segurança e os processamentos são reais e podem ser
acompanhados em tempo real — apenas os dados de negócio são sintéticos, por privacidade e
conformidade.**

A demonstração segue o Recruiter Mode (20 passos), com foco no banco e não na interface:
emitir uma apólice de verdade, ver a transação, o índice, a RLS, o evento e a auditoria
acontecerem; depois atacar a versão vulnerável, ver a falha, e assistir o mesmo ataque ser
bloqueado na versão segura com o controle nomeado.
