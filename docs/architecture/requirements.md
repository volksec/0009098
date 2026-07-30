# Requisitos — PortalDoCorretor

Convenções: `RF` = requisito funcional, `RNF` = requisito não funcional.
Prioridade `M` (must, Fase 2–5), `S` (should, Fase 6–8), `C` (could, Fase 9–10).

## 1. Escopo

### Dentro do escopo

Ciclo comercial da corretagem: cadastro e manutenção de clientes e bens seguráveis; cotação
multiplano; conversão em proposta; *underwriting* **simulado**; emissão de apólice; geração de
parcelas; apuração de comissão; endosso; renovação; aviso e acompanhamento de sinistro;
notificações; supervisão regulatória simulada; auditoria; observabilidade; laboratórios técnicos
(Engineering Lab, Security Lab, Attack Simulator); agentes de IA com governança.

### Fora do escopo (explicitamente)

- Integração real com seguradoras, SUSEP, bureaus de crédito, Denatran/Detran ou meios de pagamento.
- Cálculo atuarial real de prêmio — a precificação é **determinística e simulada**, documentada como tal.
- Decisão de *underwriting* real — é uma máquina de regras simulada, sem efeito jurídico.
- Liquidação financeira real de comissões, parcelas ou sinistros.
- Aplicativo móvel nativo; a interface é web responsiva.

## 2. Atores

| Ator | Tipo | Descrição |
|---|---|---|
| **Corretor** | Humano | Usuário operacional; vinculado a exatamente uma corretora (tenant). |
| **Usuário regulatório SUSEP (simulado)** | Humano | Supervisão somente-leitura, multi-tenant, com escopo e finalidade declarados. |
| **Outbox Dispatcher** | Conta técnica | Worker que publica mensagens da Outbox de forma idempotente. |
| **Renewal Scanner** | Conta técnica | Worker diário que detecta apólices próximas do vencimento. |
| **Billing Scheduler** | Conta técnica | Worker que avança o estado das parcelas simuladas. |
| **Commission Engine** | Serviço de domínio | Apura comissões a partir de eventos de emissão/endosso/cancelamento. |
| **AI Agent Runtime** | Agente de sistema | Executa os cinco agentes sob guardrails, com identidade e auditoria próprias. |

Não existem personas de atendimento, produto, administração, segurança ou auditoria — essas
funções são **capacidades internas** exercidas por contas técnicas e processos automatizados.

## 3. Requisitos funcionais

### 3.1 Identity and Access

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-001 | Autenticação por e-mail e senha com hash Argon2id | M | Senha nunca em log/trace; falha genérica que não distingue usuário inexistente de senha errada |
| RF-002 | MFA TOTP (RFC 6238) obrigatório para o perfil regulatório, opcional para corretor | M | Segredo TOTP cifrado em repouso; janela de ±1 passo; códigos de recuperação de uso único |
| RF-003 | Sessão com token de acesso curto (15 min) e *refresh* rotativo com detecção de reuso | M | Reuso de refresh revoga toda a família de tokens e gera `SecurityEvent` |
| RF-004 | Listagem de sessões ativas e encerramento individual ou global | M | Encerramento invalida o refresh imediatamente |
| RF-005 | Histórico de autenticação (sucesso, falha, origem, *user agent* truncado) | M | Consultável pelo próprio usuário e pelo perfil regulatório de forma agregada |
| RF-006 | Recuperação de senha **simulada** (token de uso único, expiração de 15 min, sem envio real de e-mail) | S | Token não revelado em resposta HTTP; consumido atomicamente |
| RF-007 | RBAC por papel (`BROKER`, `REGULATOR`) combinado a ABAC por atributo (tenant, carteira, escopo, finalidade) | M | Toda decisão de autorização emite `AuthorizationDecision` observável |
| RF-008 | Bloqueio progressivo por tentativas de autenticação malsucedidas | S | Backoff exponencial por conta e por IP, com `SecurityEvent` |

### 3.2 Broker Management

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-010 | Corretora (`Brokerage`) é a unidade de tenant, com CNPJ sintético e registro SUSEP fictício | M | `tenant_id` imutável após criação |
| RF-011 | Corretor pertence a exatamente uma corretora e possui carteira (`portfolio`) própria | M | Corretor não vê comissão de outro corretor, mesmo no próprio tenant |
| RF-012 | Perfil do corretor com dados profissionais e registro SUSEP fictício | M | — |

### 3.3 Customer Management

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-020 | Cadastro de cliente pessoa física (`IndividualCustomer`) e jurídica (`BusinessCustomer`) | M | Herança polimórfica persistida; CPF/CNPJ sintético validado por dígito verificador |
| RF-021 | Busca de cliente por nome, documento e e-mail, com *full-text search* e paginação por cursor | M | Busca restrita ao tenant; sem *offset* em listas grandes |
| RF-022 | Múltiplos contatos e endereços por cliente, com marcação de principal | M | Invariante: no máximo um principal por tipo |
| RF-023 | Registro de consentimentos LGPD com finalidade, base legal, versão do termo, data e revogação | M | Consentimento é *append-only*; revogação cria novo registro, não apaga |
| RF-024 | Cadastro de bens seguráveis: veículo e imóvel, extensível a novos tipos | M | Herança `InsurableAsset`; placa/chassi e matrícula sintéticos |
| RF-025 | Linha do tempo consolidada do cliente (cotações, propostas, apólices, sinistros, alterações) | M | Construída a partir de eventos, não de *joins* ad hoc |
| RF-026 | Atualização de cliente com histórico versionado de alterações | M | Toda alteração gera `AuditEvent` com estado anterior e posterior |
| RF-027 | *Soft delete* de cliente sem apólice vigente | S | Cliente com apólice ativa não pode ser removido (constraint + invariante) |

### 3.4 Product Catalog

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-030 | Produtos genéricos de seguro (auto, residencial) com versionamento | M | Cotação referencia a **versão** do produto, congelando as regras aplicadas |
| RF-031 | Coberturas com limite (`CoverageLimit`), franquia (`Deductible`) e obrigatoriedade | M | Cobertura obrigatória não pode ser desmarcada na cotação |
| RF-032 | Assistências (serviços agregados) associáveis a produto e plano | M | — |
| RF-033 | Regras de elegibilidade (`EligibilityRule`) avaliadas por *Specification* | M | Regra reprovada retorna motivo legível e código estável |

### 3.5 Quotations

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-040 | Criar cotação para cliente + produto + bem segurável | M | `QuotationNumber` único por tenant e ano |
| RF-041 | Questionário de risco tipado, produzindo `RiskProfile` e `RiskScore` | M | Respostas em JSONB validadas contra JSON Schema da versão do produto |
| RF-042 | Seleção de coberturas com validação de compatibilidade e limites | M | Invariante: soma de limites dentro do teto do produto |
| RF-043 | Cálculo **simulado** e determinístico de 3 planos (Essencial, Completo, Master) | M | Mesmas entradas ⇒ mesmo resultado; fórmula documentada e testada |
| RF-044 | Comparação lado a lado dos planos | M | — |
| RF-045 | `CalculationSnapshot` imutável com entradas, fatores, fórmula e versão do motor | M | Snapshot permite reproduzir o cálculo meses depois |
| RF-046 | Cotação expira em 30 dias; expirada não converte em proposta | M | Invariante no agregado + índice parcial para o worker de expiração |
| RF-047 | Cotação de renovação referencia a apólice anterior | M | `previous_policy_id` preenchido; histórico de cobertura comparável |

### 3.6 Proposals

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-050 | Converter cotação (plano escolhido) em proposta | M | Uma cotação gera no máximo uma proposta ativa (unique parcial) |
| RF-051 | Upload de documentos com validação de tipo real (*magic bytes*), tamanho e antivírus simulado | M | Nome de arquivo sanitizado; armazenamento fora da raiz web; download por URL assinada de curta duração |
| RF-052 | Pendências de proposta abertas e resolvidas | M | Proposta com pendência aberta não avança para aprovação |
| RF-053 | Análise de risco **simulada** produzindo `UnderwritingDecision` (aprovada, recusada, pendente) | M | Decisão imutável, com motivo, regras avaliadas e autor (conta técnica) |
| RF-054 | Histórico de status com transições válidas apenas | M | Máquina de estados no agregado; transição inválida lança exceção de domínio |
| RF-055 | Proposta aprovada pode ser emitida como apólice, uma única vez | M | Idempotência por `idempotency_key` + unique constraint |

### 3.7 Policies

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-060 | Emissão de apólice a partir de proposta aprovada, em transação única | M | Falha em qualquer etapa faz rollback total |
| RF-061 | Geração de `PolicyNumber` único e verificável | M | Unique constraint por tenant; formato validado no VO |
| RF-062 | Registro das coberturas contratadas com limite e franquia congelados | M | Alteração posterior só via endosso |
| RF-063 | Vigência (`DateRange`) sem sobreposição para o mesmo bem e produto | M | Constraint de exclusão (`btree_gist`) no PostgreSQL |
| RF-064 | Geração de parcelas conforme forma de pagamento escolhida | M | Soma das parcelas = prêmio total, ao centavo (invariante testada) |
| RF-065 | Endosso com efeito sobre coberturas, vigência ou prêmio | S | Endosso versiona a apólice; histórico preservado |
| RF-066 | Cancelamento **simulado** com motivo e data de efeito | S | Gera estorno proporcional de comissão |
| RF-067 | Renovação: identificação automática, notificação, nova cotação vinculada, aceite ou recusa | S | Preserva histórico e registra alterações de cobertura |

### 3.8 Billing e Commissions

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-070 | Parcelas com estados: pendente, paga (simulada), vencida, cancelada | M | Transições por worker, com auditoria |
| RF-071 | Comissão calculada por `CommissionRule` versionada, com percentual, valor-base e origem | M | Regra aplicada é registrada por referência, não por cópia solta |
| RF-072 | Estados de comissão: prevista, liberada, paga (simulada), estornada | M | Estorno referencia a comissão original |
| RF-073 | Extrato de comissões do corretor, com consolidação mensal | M | Corretor vê **apenas** as próprias comissões |
| RF-074 | Histórico completo da comissão com apólice, regra, percentual e valor-base | M | — |

### 3.9 Claims

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-080 | Aviso de sinistro vinculado a apólice vigente na data do evento | M | Invariante: data do evento dentro da vigência |
| RF-081 | Eventos de sinistro (`ClaimEvent`) formando linha do tempo *append-only* | M | — |
| RF-082 | Documentos e pendências de sinistro | M | Mesmos controles de upload da proposta |
| RF-083 | Decisão e valores **simulados** de indenização | M | Marcados explicitamente como simulados na UI e na API |

### 3.10 Regulatory Supervision

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-090 | Perfil regulatório é **estritamente somente-leitura** | M | Nenhuma rota de escrita autorizada; tentativa gera `SecurityEvent` |
| RF-091 | Acesso justificado: finalidade obrigatória antes de consulta sensível | M | Finalidade de lista fechada, validada e registrada; sem finalidade ⇒ 403 |
| RF-092 | Dados pessoais sempre mascarados/minimizados para o perfil regulatório | M | CPF `***.***.789-**`; nome parcial; e-mail e telefone mascarados |
| RF-093 | Indicadores consolidados por corretora, produto e período | M | Servidos por *materialized view* com atualização agendada |
| RF-094 | Consulta a trilha de auditoria, eventos de segurança e histórico de alterações | M | Somente dentro do escopo autorizado do usuário regulatório |
| RF-095 | Verificação do isolamento entre corretoras (relatório de segregação) | M | Demonstra que RLS e filtros estão ativos |
| RF-096 | Exportação de relatório sintético (CSV/JSON) com marca d'água de finalidade | S | Exportação também é auditada |
| RF-097 | Acompanhamento do ciclo completo de uma proposta específica | M | Visão cronológica ponta a ponta |
| RF-098 | Toda consulta regulatória gera `AuditEvent` com 12 campos obrigatórios (ver RF-099) | M | Sem auditoria gravada ⇒ transação não confirma |
| RF-099 | Campos da auditoria regulatória: usuário, escopo, finalidade, recurso, tenant, campos vistos, dados mascarados, timestamp, correlation ID, resultado, justificativa, tempo de resposta | M | Verificado por teste de integração |

### 3.11 Documents, Notifications, Audit

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-100 | Documentos com hash SHA-256, tipo validado e vínculo polimórfico (proposta ou sinistro) | M | Deduplicação por hash dentro do tenant |
| RF-101 | Notificações geradas por eventos de domínio, via Outbox | M | Entrega ao menos uma vez, consumo idempotente |
| RF-102 | `AuditEvent` imutável e particionado por mês | M | Sem `UPDATE`/`DELETE` (revogado por *grant*) |
| RF-103 | `SecurityEvent` para falha de autorização, violação de tenant, anomalia de autenticação e upload rejeitado | M | Correlacionável ao trace |

### 3.12 Observabilidade e laboratórios

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-110 | **Live Processing Console** — eventos em tempo real via SSE (fallback: polling), com 14 filtros | M | Sem dado sensível; redação automática verificada por teste |
| RF-111 | Linha do tempo técnica por operação, com 24 passos clicáveis (camada, classe, método, estado anterior/posterior, query, índice, duração, controle, teste, ADR) | M | Dados reais coletados por instrumentação, não fixtures |
| RF-112 | **Database Explorer** — tabelas, relações, cardinalidade, agregados, mapeamento ORM, índices, constraints, RLS, partições, views | M | Lido do catálogo do PostgreSQL em tempo real |
| RF-113 | **Query Inspector** — SQL, parâmetros mascarados, tempo, linhas, plano de execução, tipo de scan, origem no código, correlation ID | M | `EXPLAIN (ANALYZE, BUFFERS)` real; nenhum número inventado |
| RF-114 | **Engineering Lab** — comparativos: ORM vs Dapper, com/sem índice, N+1 vs projeção, paginado vs não paginado, lazy vs eager | M | Benchmarks reais executados localmente e versionados com o ambiente de medição |
| RF-115 | **Security Lab** e **Attack Simulator** — 18 cenários executados contra a versão vulnerável e replicados automaticamente contra a segura | M | Cada cenário mapeia CWE, OWASP Top 10 e ASVS, e aponta o teste automatizado correspondente |
| RF-116 | Cenário de concorrência: dois processos emitindo apólice para a mesma proposta | M | Vulnerável duplica; segura bloqueia por invariante + unique + optimistic lock + idempotência |
| RF-117 | **Recruiter Mode** — jornada guiada de 10 a 15 minutos, com foco no banco de dados | S | 20 passos conforme especificação |

### 3.13 Interação com o banco e ciclo de vida do registro

Requisitos incorporados após a [revisão contra os 12 critérios de avaliação](requirements-review.md).

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-130 | **Data Browser** interativo: consulta aos dados reais pela interface, com filtros tipados, ordenação e navegação por FK | M | Somente leitura; sem SQL livre; consulta parametrizada gerada pelo servidor a partir de whitelist; passa pelas 5 camadas de isolamento; aparece no Query Inspector |
| RF-131 | Exclusão lógica como capacidade transversal (`ISoftDeletable`), com cascata lógica e guarda de integridade | M | Query filter global exclui apagados; índice único parcial libera o valor; entidade referenciada por registro vivo não é apagável; motivo obrigatório e auditado |
| RF-132 | Restauração de registro excluído, com revalidação de invariantes | M | `409` se outro registro assumiu o valor único no intervalo; restauração em cascata é opcional e explícita |
| RF-133 | Biblioteca de componentes visuais próprios, reutilizáveis e acessíveis | M | Inventário no Storybook; variantes tipadas, estados completos, WCAG 2.1 AA, teste de interação; `SimulatedDataBadge` obrigatório em valor calculado |
| RF-134 | **Transaction Inspector**: ciclo de vida das transações (duração, isolamento, locks, commit/rollback, eventos, Outbox, auditoria) | M | Demonstra rollback, conflito de optimistic lock, emissão concorrente e `SKIP LOCKED` com dados reais |
| RF-135 | Monitoramento de **integridade**: métricas próprias e job diário de verificação | M | `audit_coverage_ratio` = 1.0; divergência de integridade gera alerta e aparece no dashboard de conformidade |

### 3.14 Agentes de IA

| ID | Requisito | Pri | Critério de aceite |
|---|---|---|---|
| RF-120 | Cinco agentes: Broker Copilot, Regulatory Assistant, Database Review, Architecture Review, AppSec Review | S | Cada um com skills, ferramentas permitidas e guardrails declarados |
| RF-121 | Agente executa sob a identidade e o tenant do usuário, nunca com privilégio elevado | S | Isolamento verificado por teste |
| RF-122 | Toda execução registrada em `AgentExecution` com entrada/saída redigidas, custo e duração | S | Limite de execução por usuário e por janela de tempo |
| RF-123 | Defesa contra *prompt injection*: conteúdo recuperado é dado, nunca instrução | S | Teste de segurança com payload de injeção em campo sintético |
| RF-124 | Agente regulatório não emite decisão regulatória, apenas organiza evidência | S | Recusa explícita e auditada |

## 4. Requisitos não funcionais

### 4.1 Segurança

| ID | Requisito | Meta / verificação |
|---|---|---|
| RNF-001 | Defesa em profundidade multi-tenant em 5 camadas: claim no token → contexto imutável de requisição → *global query filter* do EF Core → autorização por recurso → RLS no PostgreSQL | Teste de isolamento derruba cada camada isoladamente e prova que a seguinte segura |
| RNF-002 | `tenant_id` jamais aceito de entrada do usuário (corpo, query, header, rota) | Teste de *mass assignment*; DTOs sem a propriedade |
| RNF-003 | 100% das queries parametrizadas na aplicação segura | Análise estática + teste de SQLi no Attack Simulator |
| RNF-004 | Segredos fora do código e fora da imagem; injeção por Docker secrets/variáveis em runtime | Scan de segredo no CI (gitleaks) bloqueando o merge |
| RNF-005 | Aderência a OWASP ASVS 4.0 nível 2 nos controles implementados | Matriz de rastreabilidade controle ↔ ASVS ↔ teste |
| RNF-006 | Cabeçalhos de segurança: CSP sem `unsafe-inline`, HSTS, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy` | Teste de contrato sobre as respostas |
| RNF-007 | *Rate limiting* por IP, usuário e rota sensível | 429 com `Retry-After`; `SecurityEvent` no estouro |
| RNF-008 | Proteção CSRF em fluxos baseados em cookie; `SameSite=Strict`, `HttpOnly`, `Secure` | Cenário CSRF no Attack Simulator |
| RNF-009 | Log e trace **nunca** contêm senha, token, cookie, documento completo, dado pessoal completo, segredo ou *connection string* | Redação por *enricher* do Serilog + teste que varre a saída de log |
| RNF-010 | Laboratório vulnerável isolado: profile Docker próprio, rede sem rota externa, banco separado, limites de CPU/memória, *reset* automático, aviso visual permanente | Nunca publicado; ausente do `docker compose up` padrão |

### 4.2 Integridade e estabilidade

| ID | Requisito | Meta / verificação |
|---|---|---|
| RNF-020 | Toda operação de negócio é atômica; falha parcial faz rollback total | Teste de rollback com falha injetada |
| RNF-021 | *Optimistic locking* por `xmin`/coluna de versão em todos os agregados | Teste de concorrência com duas transações simultâneas |
| RNF-022 | Idempotência em todos os comandos que criam recurso, via `Idempotency-Key` | Replay retorna a mesma resposta, sem efeito colateral |
| RNF-023 | Outbox transacional garante que evento e estado sejam confirmados juntos | Teste com falha entre commit e publicação |
| RNF-024 | Integridade referencial declarada no banco, não apenas na aplicação | Teste que tenta inserir órfão diretamente por SQL |
| RNF-025 | Resiliência com Polly: retry com *jitter*, timeout e circuit breaker em dependências | Teste com dependência derrubada |

### 4.3 Performance

| ID | Requisito | Meta (ambiente local, documentado) |
|---|---|---|
| RNF-030 | Consultas de lista com paginação por cursor | p95 < 150 ms com massa de referência |
| RNF-031 | Emissão de apólice ponta a ponta | p95 < 500 ms |
| RNF-032 | Ausência de N+1 nos fluxos principais | Detector de N+1 instrumentado; contagem de queries por operação registrada como métrica |
| RNF-033 | *Sequential scan* ausente nas consultas críticas | Verificado por `EXPLAIN` real no Query Inspector |
| RNF-034 | Cache Redis em catálogo de produto e agregações regulatórias, com invalidação por evento | *Hit rate* exposto como métrica |

> Todos os números de performance publicados serão **medições reais** do ambiente local,
> acompanhadas da especificação da máquina, versão do PostgreSQL e massa de dados usada.
> Nenhum benchmark será estimado.

### 4.4 Observabilidade

| ID | Requisito |
|---|---|
| RNF-040 | OpenTelemetry ponta a ponta (traces, métricas, logs) exportados via OTel Collector |
| RNF-041 | Correlation ID propagado do frontend ao banco (via `application_name` / comentário SQL) |
| RNF-042 | Logs estruturados em JSON com redação automática |
| RNF-043 | Métricas obrigatórias: latência de query (média, p95, p99), queries por operação, N+1 detectadas, *seq scans*, cache hit/miss, tempo de transação, locks, deadlocks, taxa de erro, throughput, CPU, memória, eventos de domínio, mensagens de Outbox, falhas de autorização, violações de tenant, operações regulatórias, apólices emitidas, propostas aprovadas, comissões calculadas |
| RNF-044 | Health checks `/health/live` e `/health/ready` com verificação de banco, cache e migrations |
| RNF-045 | Alertas no Grafana para violação de tenant, pico de falha de autorização e atraso da Outbox |

### 4.5 Qualidade, operação e conformidade

| ID | Requisito |
|---|---|
| RNF-050 | Testes: unitários, VOs, agregados, invariantes, integração (Testcontainers com PostgreSQL real), migrations, constraints, repositórios, RLS, isolamento, autorização, concorrência, idempotência, Outbox, rollback, performance, carga, E2E, arquiteturais (NetArchTest), segurança e agentes de IA |
| RNF-051 | Cobertura mínima de 85% na camada de domínio; banco em memória **proibido** em testes de integração |
| RNF-052 | Pipeline DevSecOps: build, testes, SAST, análise de dependências, scan de segredo, scan de imagem, SBOM e assinatura de artefato |
| RNF-053 | Containers: multi-stage, usuário não-root, base mínima, healthcheck, filesystem somente-leitura, `cap_drop: ALL`, redes segregadas, limites de recurso |
| RNF-054 | Migrations versionadas com script de rollback correspondente e teste de aplicação em base limpa |
| RNF-055 | Estratégia documentada de backup, restore, retenção e anonimização |
| RNF-056 | LGPD por concepção: minimização, finalidade, consentimento versionado, mascaramento e retenção definida |
| RNF-057 | O GitHub Pages declara explicitamente que opera com dados sintéticos e mocks (MSW/IndexedDB), **sem** banco real |
| RNF-058 | Acessibilidade WCAG 2.1 AA: contraste, foco visível, navegação por teclado, rótulos ARIA |
| RNF-059 | Ambiente executável por uma pessoa em um comando (`docker compose up --build`) |

## 5. Rastreabilidade

Cada RF/RNF será rastreado até: (a) o teste automatizado que o verifica, (b) o ADR que registra a
decisão, quando houver, e (c) o passo do Recruiter Mode que o demonstra. A matriz será mantida em
`docs/architecture/traceability.md` a partir da Fase 3, quando existirem testes reais para referenciar.
