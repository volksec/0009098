# Fase 2 — Fundação executável

**Status:** concluída · **Data:** 2026-07-30

## Entregue

| Item | Onde | Verificação |
|---|---|---|
| Solução .NET 9 com regras de build rígidas | `Directory.Build.props` | `TreatWarningsAsErrors` ativo — aviso quebra o build |
| SharedKernel: abstrações de domínio | `shared/PortalDoCorretor.SharedKernel/Domain/` | `Entity`, `AggregateRoot`, `IDomainEvent`, `ITenantScoped`, `ISoftDeletable`, `IAuditable`, `IClock` |
| 16 Value Objects imutáveis e autovalidados | `shared/.../ValueObjects/` | 89 testes unitários |
| Testes arquiteturais | `tests/architecture/` | 6 regras verificadas |
| Docker Compose com profile isolado | `docker-compose.yml` | Laboratório vulnerável fora do comando padrão |
| Inicialização do PostgreSQL | `database/secure/scripts/00-init-roles.sql` | Papéis de menor privilégio, extensões, contexto de tenant |
| Pipeline CI | `.github/workflows/ci.yml` | Build, testes, gitleaks, dependências vulneráveis, guardrails do laboratório |

**Resultado da suíte:** 95 testes, 0 falhas, build Release sem avisos.

## Value Objects implementados

`Money`, `Percentage`, `CommissionRate`, `DocumentNumber`, `EmailAddress`, `PhoneNumber`,
`PostalCode`, `StateCode`, `PostalAddress`, `DateRange`, `PolicyNumber`, `ProposalNumber`,
`QuotationNumber`, `RiskScore`, `CoverageLimit`, `Deductible`, `TenantId`, `CorrelationId`,
`IdempotencyKey`.

## Bug real encontrado pelo teste de propriedade

O teste `Allocate_preserva_o_total_para_qualquer_entrada` (FsCheck, 500 casos gerados)
reprovou a primeira implementação de `Money.Allocate`.

**A implementação original** somava todo o resíduo do arredondamento à primeira parcela. A soma
ficava correta, então os testes de exemplo que eu havia escrito (`R$ 1.000,00 ÷ 3`) passavam. Mas
para valores pequenos com parcelamento longo — `R$ 0,05 ÷ 12` — o resultado era uma parcela de
`R$ 0,05` e onze de `R$ 0,00`: matematicamente somando certo, comercialmente absurdo.

**A correção** opera em centavos inteiros e distribui o resíduo um centavo por parcela (método do
maior resto), o que garante que nenhuma parcela difira de outra em mais de um centavo.

O registro fica aqui porque é o argumento a favor do teste baseado em propriedade: nenhum teste de
exemplo que eu escreveria por intuição teria encontrado esse caso. A propriedade — *"para qualquer
valor e qualquer número de parcelas, a soma é exata e a dispersão é ≤ 1 centavo"* — encontrou em
menos de um segundo.

## Duas decisões de segurança embutidas no código

**`TenantId` sem construtor público.** A criação passa apenas por `FromTrustedSource`, chamada pelo
resolvedor de claims e pelo materializador do ORM. Um DTO de requisição não consegue produzir um
`TenantId` válido, então manipulação de tenant via payload fica impedida pelo **sistema de tipos**,
não por uma validação que alguém pode esquecer de chamar. Há um teste arquitetural que falha se
alguém adicionar um overload público.

**`ToString()` mascarado por padrão** em `DocumentNumber`, `EmailAddress`, `PhoneNumber` e
`PostalAddress`. Se um desenvolvedor interpolar o objeto em um log por descuido, o comportamento
padrão é o mascaramento — segurança por default em vez de disciplina. Exibir o dado completo exige
chamar `Formatted` explicitamente, o que é uma decisão visível em code review. Também verificado
por teste arquitetural.

## Limitação conhecida do ambiente

**Docker não está instalado nesta máquina.** Consequências:

- O `docker-compose.yml` e o script de inicialização do PostgreSQL foram escritos e revisados, mas
  **não foram executados** — serão validados na Fase 3, quando o Docker estiver disponível.
- Os testes de integração com Testcontainers (RNF-051) só rodam a partir da Fase 3 e exigem Docker.
- Os 95 testes desta fase são unitários e arquiteturais, que não dependem de contêiner.

Isso está registrado como pendência de verificação, não como item concluído. O CI no GitHub Actions
roda em `ubuntu-latest` com Docker disponível, então a validação acontece lá assim que o
repositório for publicado.

## Próxima fase

**Fase 3 — Banco.** Migrations completas, tipos compostos, constraints, índices, RLS com `FORCE`,
particionamento, Outbox e seeds sintéticos determinísticos. É a fase que sustenta o critério
principal de avaliação do case, e vem antes da API deliberadamente.
