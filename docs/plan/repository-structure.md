# Estrutura do repositório

```
0009098/
├── apps/
│   ├── frontend/                    # React + TS + Vite (design system próprio)
│   │   ├── src/
│   │   │   ├── design-system/       # tokens pdc-*, componentes base, Storybook
│   │   │   ├── features/            # vertical slices espelhando os módulos do backend
│   │   │   │   ├── auth/  customers/  quotations/  proposals/
│   │   │   │   ├── policies/  commissions/  claims/  regulatory/
│   │   │   │   └── labs/            # engineering-lab, security-lab,
│   │   │   │                        # live-console, query-inspector, db-explorer
│   │   │   ├── lib/                 # api client, sse client, zod schemas, masking
│   │   │   └── recruiter-mode/      # jornada guiada de 20 passos
│   │   └── mocks/                   # MSW + IndexedDB (somente para o GitHub Pages)
│   │
│   ├── secure-api/                  # host ASP.NET Core do monólito modular
│   ├── vulnerable-api/              # ⚠️ laboratório: falhas propositais, profile isolado
│   ├── attack-simulator/            # 18 cenários; executa contra vulnerável e replica na segura
│   ├── ai-agent-service/            # runtime dos 5 agentes, com guardrails
│   └── workers/                     # Outbox Dispatcher, Renewal Scanner, Billing Scheduler
│
├── modules/                         # um projeto por bounded context
│   ├── identity/        ├── brokers/       ├── customers/     ├── products/
│   ├── quotations/      ├── proposals/     ├── policies/      ├── billing/
│   ├── commissions/     ├── claims/        ├── documents/     ├── notifications/
│   ├── regulatory/      ├── auditing/      ├── observability/ └── ai/
│   │
│   └── <cada módulo>/
│       ├── PortalDoCorretor.<Modulo>.Domain/          # sem dependência de framework
│       ├── PortalDoCorretor.<Modulo>.Application/
│       ├── PortalDoCorretor.<Modulo>.Infrastructure/
│       └── PortalDoCorretor.<Modulo>.Contracts/       # único assembly referenciável por outros
│
├── shared/
│   ├── PortalDoCorretor.SharedKernel/    # Entity, AggregateRoot, VOs comuns, IDomainEvent
│   ├── PortalDoCorretor.Persistence/     # DbContext base, interceptors, conversores, RLS
│   └── PortalDoCorretor.Web/             # middlewares, problem details, rate limit, headers
│
├── database/
│   ├── secure/
│   │   ├── migrations/              # cada migration com Up e Down funcionais
│   │   ├── scripts/                 # RLS, tipos compostos, partições, funções, triggers
│   │   ├── seeds/                   # massa sintética determinística (seed fixa)
│   │   └── backups/                 # scripts de backup, restore e anonimização
│   └── vulnerable/                  # ⚠️ mesmo esquema SEM constraints, índices e RLS
│       ├── migrations/  ├── seeds/  └── scripts/
│
├── tests/
│   ├── unit/                        # VOs, agregados, invariantes, serviços de domínio
│   ├── integration/                 # Testcontainers com PostgreSQL 16 real + Respawn
│   ├── architecture/                # NetArchTest: fronteiras e regra de dependência
│   ├── contract/                    # contratos de API e cabeçalhos de segurança
│   ├── e2e/                         # Playwright, fluxos ponta a ponta
│   ├── performance/                 # benchmarks reais (k6 + BenchmarkDotNet)
│   └── security/                    # isolamento, autorização, RLS, os 18 cenários
│
├── docs/
│   ├── architecture/  adr/  c4/  uml/  domain/  database/
│   ├── threat-model/  security/  benchmarks/  recruiter-mode/  plan/
│
├── infrastructure/
│   ├── docker/                      # Dockerfiles multi-stage, não-root, read-only
│   ├── compose/                     # docker-compose.yml + profile security-lab
│   ├── monitoring/                  # otel collector, prometheus, grafana, loki, tempo
│   └── scripts/                     # bootstrap, reset do lab, geração de massa
│
├── .github/
│   ├── workflows/                   # ci, security, benchmarks, pages
│   ├── ISSUE_TEMPLATE/
│   └── PULL_REQUEST_TEMPLATE.md
│
├── README.md  SECURITY.md  CONTRIBUTING.md  CODE_OF_CONDUCT.md
├── CHANGELOG.md  LICENSE  docker-compose.yml
└── PortalDoCorretor.sln
```

## Regras de dependência (verificadas por NetArchTest)

| # | Regra | Falha o build se |
|---|---|---|
| 1 | `*.Domain` não referencia EF Core, ASP.NET, Serilog ou qualquer framework | Houver `using Microsoft.EntityFrameworkCore` no domínio |
| 2 | `*.Domain` não referencia `*.Application` nem `*.Infrastructure` | Dependência apontar para fora |
| 3 | Um módulo só referencia `<Outro>.Contracts` | Referência direta a `<Outro>.Domain` ou `.Infrastructure` |
| 4 | Não existem ciclos entre módulos | Grafo de referência tiver ciclo |
| 5 | `regulatory` não contém nenhum command handler | Houver tipo implementando `ICommandHandler` no módulo |
| 6 | Toda entidade `ITenantScoped` tem query filter configurado | Faltar `HasQueryFilter` no mapeamento |
| 7 | Nenhum agregado expõe coleção mutável pública | Propriedade retornar `List<T>` em vez de `IReadOnlyCollection<T>` |
| 8 | `vulnerable-api` não é referenciada por nenhum projeto de produção | Houver referência a partir de `secure-api` |

As regras 5 a 8 são específicas deste case: transformam decisões de segurança e modelagem em
falhas de compilação, em vez de convenções que erodem.
