# Revisão da modelagem contra os 12 critérios de avaliação

Revisão executada em 2026-07-30 sobre a entrega da Fase 1, item a item. O objetivo foi encontrar
lacunas, não confirmar o que já estava pronto.

## Resultado

| # | Critério | Situação | Onde está / o que faltava |
|---|---|---|---|
| 1 | Telas interativas para consulta ao banco | ⚠️ **Lacuna** | Havia Database Explorer (catálogo) e Query Inspector (queries da aplicação), mas **nenhuma tela para consultar os dados propriamente ditos** de forma interativa e segura → **RF-130** |
| 2 | Inserção, atualização, exclusão lógica e consulta | ⚠️ **Lacuna** | Inserção/atualização/consulta cobertas. **Exclusão lógica aparecia uma única vez** (RF-027, só cliente), sem endpoint, sem restauração, sem UI, sem política de cascata → **RF-131, RF-132** |
| 3 | Componentes visuais modernos e reutilizáveis | ⚠️ **Lacuna** | Paleta e tipografia definidas, mas sem inventário de componentes nem contrato de reuso → **RF-133** |
| 4 | Logs de processamento em tempo real | ✅ Coberto | RF-110, RF-111 (Live Processing Console, 14 filtros, 24 passos clicáveis) |
| 5 | Visualização de SQL, transações e eventos | ⚠️ **Parcial** | SQL coberto (RF-113) e eventos coberto. **Transação não tinha visão própria** — aparecia só como passo da timeline → **RF-134** |
| 6 | Banco orientado a objetos e eventos | ✅ Coberto | Tipos compostos, domains, enums, herança TPH/TPT, Outbox, domain events |
| 7 | Modelagem OR: entidades, agregados, VOs, relacionamentos | ✅ Coberto | 5 agregados, 16 VOs, ER completo, mapa invariante→constraint |
| 8 | Processamento assíncrono (domain events + Outbox) | ✅ Coberto | ADR-0006, `SKIP LOCKED`, idempotência por `message_id` |
| 9 | Auditoria completa | ✅ Coberto | `audit_events` particionado, append-only por `REVOKE`, na mesma transação da operação |
| 10 | Segurança da modelagem à persistência | ✅ Coberto | 5 camadas (ADR-0004), `TenantId` sem construtor público, RLS com `FORCE` |
| 11 | Monitoramento de performance, integridade, concorrência, estabilidade | ⚠️ **Parcial** | Performance, concorrência e estabilidade cobertas em RNF-030..034 e RNF-043. **Integridade não tinha métrica própria** → **RF-135** |

**Placar: 6 cobertos, 5 com lacuna.** As cinco lacunas viraram requisitos novos, detalhados abaixo,
e foram incorporadas ao plano da Fase 6 (frontend) e Fase 7 (observabilidade).

---

## Requisitos adicionados

### RF-130 — Data Browser interativo (consulta ao banco pela interface)

Tela que permite ao avaliador **consultar os dados reais** do banco, sem sair da aplicação e sem
abrir um cliente SQL externo.

| Aspecto | Definição |
|---|---|
| Navegação | Lista de tabelas do catálogo → seleção → grid de dados com paginação por cursor |
| Filtros | Por coluna, com operadores tipados (`=`, `LIKE`, `BETWEEN`, `IN`, `IS NULL`) |
| Ordenação | Por qualquer coluna indexada; colunas sem índice são sinalizadas com aviso de custo |
| Navegação por FK | Clicar em uma FK abre a linha referenciada — o grafo relacional navegável |
| Segurança | **Somente leitura.** Nenhum SQL livre é aceito. O filtro do usuário é traduzido para uma consulta **parametrizada** construída pelo servidor a partir de um whitelist de tabelas e colunas |
| Isolamento | Passa pelas mesmas 5 camadas: o corretor só enxerga o próprio tenant; o regulador só vê as views mascaradas |
| Rastreabilidade | Cada consulta aparece no Query Inspector com o SQL gerado, os parâmetros e o plano de execução |
| Auditoria | Consulta de dado sensível gera `AuditEvent`; para o regulador, exige finalidade ativa |

**Decisão de segurança deliberada:** não existe campo de SQL livre para o usuário. Um console SQL
seria a forma mais rápida de demonstrar o banco e a mais irresponsável de construir a aplicação —
transformaria a tela em RCE de banco de dados. O Data Browser demonstra o mesmo (dados reais,
queries reais, planos reais) sem abrir a superfície.

### RF-131 — Exclusão lógica como capacidade transversal

Soft delete deixa de ser detalhe do cliente e vira contrato do sistema.

- Toda entidade de negócio implementa `ISoftDeletable` (`deleted_at`, `deleted_by`, `deletion_reason`).
- O *global query filter* do EF Core exclui registros apagados de toda consulta, por padrão.
- Índices únicos usam `WHERE deleted_at IS NULL`, então apagar libera o valor único (um documento
  pode ser recadastrado depois da exclusão).
- **Cascata lógica explícita:** apagar um cliente marca contatos, endereços e bens como apagados na
  mesma transação. Não é `ON DELETE CASCADE` físico — é regra de domínio, aplicada pelo agregado.
- **Guarda de integridade:** entidade referenciada por registro vivo não pode ser apagada
  (cliente com apólice vigente, apólice com sinistro aberto). Verificado por invariante **e** por FK
  `RESTRICT`.
- Exclusão gera `AuditEvent` com estado anterior completo e motivo obrigatório.
- Exclusão **física** só ocorre por rotina de retenção/anonimização, nunca por ação de usuário.

### RF-132 — Restauração de registro excluído

- Endpoint `POST /{recurso}/{id}/restore`, autorizado por recurso e auditado.
- Restauração valida que as invariantes voltam a ser satisfeitas — se outro registro assumiu o
  documento único no intervalo, a restauração é rejeitada com `409` e motivo claro.
- UI: filtro "incluir excluídos" nas listagens, com o registro marcado visualmente e ação de
  restaurar disponível conforme permissão.
- Restauração em cascata é **opcional e explícita**: o usuário escolhe restaurar apenas o pai ou o
  pai com os filhos apagados no mesmo evento (correlacionados por `deletion_batch_id`).

### RF-133 — Biblioteca de componentes reutilizáveis

Design system próprio, com inventário definido e documentado no Storybook. Todo componente tem:
variantes tipadas, estados (default, hover, focus, disabled, loading, error, empty), suporte a
tema claro/escuro, acessibilidade WCAG 2.1 AA (foco visível, navegação por teclado, rótulo ARIA) e
teste de interação.

| Grupo | Componentes |
|---|---|
| Formulário | `TextField`, `MaskedField` (CPF/CNPJ/placa/CEP), `MoneyField`, `PercentageField`, `DateRangePicker`, `Select`, `Combobox`, `FileUpload`, `FormSection` |
| Dados | `DataTable` (paginação por cursor, ordenação, filtro por coluna, seleção), `DetailPanel`, `Timeline`, `KeyValueList`, `EmptyState`, `Skeleton` |
| Feedback | `Toast`, `ConfirmDialog`, `InlineError`, `StatusBadge`, `AuditBadge`, `SimulatedDataBadge` |
| Domínio | `PolicyCard`, `QuotationComparator`, `CoverageTable`, `InstallmentSchedule`, `CommissionStatement`, `ClaimTimeline`, `ConsentPanel` |
| Técnicos | `LogStream`, `QueryViewer` (Monaco), `ExecutionPlanTree`, `EntityGraph` (Cytoscape), `TransactionTimeline`, `MetricTile`, `SecurityVerdict` |
| Layout | `AppShell`, `SideNav`, `PageHeader`, `TabbedPanel`, `SplitView`, `LabBanner` (faixa âmbar do laboratório) |

`SimulatedDataBadge` é obrigatório em toda tela que exibe valor calculado (prêmio, decisão de
underwriting, indenização), para que nada no case possa ser confundido com cálculo real.

### RF-134 — Transaction Inspector

Visão dedicada ao ciclo de vida das transações, complementando o Query Inspector.

Exibe, por transação: identificador, `correlation_id`, momento de início e de fim, duração, nível
de isolamento, quantidade de comandos, tabelas tocadas, resultado (`COMMIT` / `ROLLBACK`), locks
adquiridos e tempo de espera, eventos de domínio produzidos, linhas de Outbox gravadas e
`AuditEvent`s gerados.

Casos que a tela precisa demonstrar de forma explícita, porque são o argumento de estabilidade:
rollback automático com falha injetada; conflito de optimistic lock (`xmin` divergente) com o
perdedor identificado; disputa de emissão concorrente; e `SKIP LOCKED` distribuindo lotes da Outbox
entre workers.

### RF-135 — Monitoramento de integridade

Métricas próprias de integridade, além das de performance:

| Métrica | O que revela |
|---|---|
| `constraint_violations_total{constraint,table}` | Qual invariante o banco precisou barrar — e se a aplicação está deixando passar |
| `optimistic_lock_conflicts_total{aggregate}` | Contenção real por agregado |
| `deadlocks_total`, `lock_wait_seconds` | Saúde de concorrência |
| `outbox_pending_age_seconds` | Atraso do processamento assíncrono (alerta > 60 s) |
| `outbox_dead_letter_total` | Mensagens que esgotaram as tentativas |
| `audit_coverage_ratio` | Proporção de operações de escrita com `AuditEvent` correspondente — **meta: 1.0** |
| `tenant_violation_attempts_total` | Tentativas de acesso cross-tenant |
| `integrity_check_failures_total` | Falhas da verificação periódica (órfãos, soma de parcelas divergente, apólice sem cobertura) |

**Job de verificação de integridade:** worker diário que roda um conjunto de asserções SQL sobre a
base (órfãos, `Σ parcelas ≠ prêmio`, apólice ativa sem cobertura, comissão sem regra, consentimento
sem finalidade). Qualquer divergência gera alerta e aparece no dashboard regulatório de
conformidade. A integridade deixa de ser presumida e passa a ser **medida continuamente**.

---

## Impacto no plano

| Fase | Ajuste |
|---|---|
| 3 (Banco) | `deleted_at`/`deleted_by`/`deletion_reason`/`deletion_batch_id` em todas as tabelas de negócio; índices únicos parciais revisados; asserções do job de integridade escritas como SQL versionado |
| 4 (Núcleo) | `ISoftDeletable` no SharedKernel; endpoints de exclusão lógica e restauração; guarda de integridade nos agregados |
| 6 (Frontend) | Data Browser (RF-130), biblioteca de componentes (RF-133), filtro "incluir excluídos" e ação de restaurar |
| 7 (Observabilidade) | Transaction Inspector (RF-134) e métricas de integridade (RF-135) |

## Conclusão da revisão

A modelagem estava sólida nos pontos estruturais — agregados, VOs, invariantes espelhadas em
constraints, isolamento em cinco camadas, Outbox e auditoria. As lacunas encontradas eram todas de
**superfície de uso**: faltava como o avaliador *interage* com o banco (RF-130, RF-134), como o
ciclo de vida completo do registro é exercido (RF-131, RF-132), com quais peças a interface é
construída (RF-133) e como a integridade é *medida* em vez de presumida (RF-135).

Com os cinco requisitos incorporados, os 12 critérios estão cobertos. Fase 2 liberada.
