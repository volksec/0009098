# NexusBroker

**Plataforma de gestão para corretores de seguros — case técnico de Engenharia de Software.**

> ⚠️ **Aviso de escopo e conformidade**
> Projeto **independente de demonstração técnica**, inspirado *conceitualmente* em portais
> corporativos do segmento de corretagem de seguros. **Não** possui vínculo, integração,
> dado, credencial, endpoint, fluxo interno ou elemento de marca de nenhuma seguradora real.
> **Não** representa integração oficial com a SUSEP — o perfil regulatório é uma **simulação**
> criada para demonstrar controles de supervisão, minimização de dados e auditoria.
> **Todos os dados de negócio são sintéticos.** A aplicação, o banco, as transações,
> as queries, os controles de segurança e os processamentos são **reais**.

---

## 1. Seleção do nome do produto

Cinco candidatos foram avaliados contra cinco critérios: (a) ausência de colisão com marcas do
setor, (b) clareza para público de negócio, (c) pronunciabilidade em pt-BR e en, (d) capacidade de
gerar submarcas, (e) disponibilidade de namespace técnico (`nexusbroker.*`, pacotes, containers).

| # | Candidato | Força | Fraqueza | Veredito |
|---|-----------|-------|----------|----------|
| 1 | **NexusBroker** | "Nexus" traduz o papel de *hub* que liga corretor ↔ cliente ↔ produto ↔ regulador; permite submarcas (`NexusBroker Regulatory`, `NexusBroker Copilot`, `NexusBroker Labs`) | Levemente anglófono | ✅ **Escolhido** |
| 2 | Corretor 360 | Imediatamente compreensível no mercado brasileiro | "360" é sufixo saturado em produtos financeiros; baixa distintividade; difícil registrar | ❌ |
| 3 | SecureBroker | Reforça o eixo AppSec do case | Faz o produto parecer ferramenta de cibersegurança, não plataforma de gestão de carteira | ❌ |
| 4 | BrokerCore | Bom nome de plataforma/infra | "Core" genérico; sugere componente interno, não produto de ponta a ponta | ❌ |
| 5 | Aegis Corretores | Simbolicamente forte (proteção) | Referência erudita, baixa clareza para o corretor; mistura idiomas | ❌ |

**Decisão: `NexusBroker`.** Registro em [ADR-0001](docs/adr/0001-nome-e-identidade-do-produto.md).

## 2. Identidade visual própria

Identidade **autoral**, sem tipografia, logotipo, iconografia, ilustração ou paleta de terceiros.

**Logotipo** — monograma `NB` inscrito em um hexágono **aberto** no vértice superior direito,
representando o nó de rede que conecta os atores do ecossistema. Sem escudos, brasões, gotas,
guarda-chuvas ou qualquer arquétipo visual tradicional de seguradora.

**Paleta `nexus-*`** (tokens do design system):

| Token | Hex | Uso |
|---|---|---|
| `nexus-navy-900` | `#0B2447` | Superfícies institucionais, header, sidebar |
| `nexus-blue-600` | `#1F6FEB` | Ação primária, links, foco |
| `nexus-blue-100` | `#DCE9FD` | Estados selecionados, badges informativos |
| `nexus-slate-900` | `#141821` | Texto primário / fundo do modo escuro |
| `nexus-slate-50` | `#F4F6F8` | Fundo da aplicação (modo claro) |
| `nexus-amber-500` | `#F2A93B` | Pendências, atenção, avisos de laboratório |
| `nexus-red-600` | `#D93F3F` | Erros, bloqueios de autorização, `SecurityEvent` |
| `nexus-green-600` | `#1F9D63` | Sucesso, apólice emitida, controle que bloqueou ataque |

**Tipografia** — `Inter` (interface, licença SIL OFL) e `JetBrains Mono` (telas técnicas:
Live Processing Console, Query Inspector, Database Explorer). Ambas de licença livre.

**Modo laboratório** — quando a aplicação vulnerável está ativa, toda a UI recebe uma faixa
diagonal `nexus-amber-500` e o rótulo `LAB VULNERÁVEL — DADOS SINTÉTICOS — REDE ISOLADA`,
impedindo confusão entre ambientes durante a apresentação.

Detalhamento completo do design system em [ADR-0001](docs/adr/0001-nome-e-identidade-do-produto.md).

## 3. Descrição executiva

O **NexusBroker** cobre o ciclo de vida comercial completo da corretagem de seguros —
**cliente → bem segurável → cotação → proposta → apólice → parcelas → comissão → renovação →
sinistro** — para dois perfis, e apenas dois:

- **Corretor** — usuário operacional, opera exclusivamente dentro do tenant da sua corretora.
- **Usuário regulatório (simulação SUSEP)** — supervisão **somente-leitura**, com acesso
  justificado, dados minimizados/mascarados, escopo por corretora e trilha de auditoria obrigatória.

Funções de segurança, auditoria e administração existem como **capacidades internas** da
plataforma (contas técnicas, background workers, agentes de sistema, eventos de domínio) e
**não** como personas adicionais.

### O problema de negócio

O corretor de seguros brasileiro opera hoje fragmentado entre planilhas, portais distintos por
seguradora, e-mail e WhatsApp. Isso produz quatro custos concretos:

1. **Perda de receita** — cotações que expiram sem conversão e renovações perdidas por falta de
   alerta antecipado (a renovação é a receita mais barata da carteira).
2. **Risco operacional** — divergência entre a comissão esperada e a apurada, sem rastro da regra
   aplicada nem do valor-base que a originou.
3. **Risco de conformidade** — dados pessoais de segurados (LGPD) manipulados sem registro de
   consentimento, sem finalidade declarada e sem trilha de quem acessou o quê.
4. **Opacidade regulatória** — a supervisão do setor exige rastreabilidade de ponta a ponta, e
   sistemas sem auditoria estruturada só conseguem responder a um questionamento com exportação
   manual de banco, o que é lento e, por si só, um incidente de privacidade.

O NexusBroker ataca os quatro pontos com **modelo de domínio rico**, **invariantes no agregado**,
**isolamento multi-tenant com defesa em profundidade** e **auditoria como cidadã de primeira classe**.

### O objetivo técnico do case

Este case **não** é uma vitrine de interface. O objetivo declarado é provar, com **evidência
observável em tempo real**, que o banco objeto-relacional e o modelo de objetos foram
corretamente projetados. O avaliador deve ser capaz de disparar uma operação de negócio real
(emitir uma apólice) e assistir, ao vivo:

o Value Object validando → o agregado carregando com *optimistic lock* → a invariante rejeitando
o estado inválido → a query parametrizada com seu plano de execução e índice → a RLS do
PostgreSQL filtrando por tenant → o evento de domínio → a linha da Outbox → o `AuditEvent` →
o commit da transação → o worker publicando a notificação → a métrica subindo → o trace fechando.

E, em seguida, ver **o mesmo ataque** rodar contra a versão vulnerável (que falha) e contra a
versão segura (que bloqueia, registra `SecurityEvent` e explica **qual controle** atuou).

### A tese profissional

O autor é **Pentester Sênior em transição para Engenharia e Arquitetura de Software Sênior**.
A tese do case é que conhecimento ofensivo, aplicado na fase de concepção, produz software
corporativo mais seguro do que revisão tardia: cada controle da versão segura existe porque o
ataque correspondente está implementado, executável e demonstrável no Security Lab —
não porque um checklist mandou.

## 4. Documentação — Fase 1 (concepção e modelagem)

| Documento | Conteúdo |
|---|---|
| [Requisitos](docs/architecture/requirements.md) | RF (funcionais) e RNF (não funcionais), com critérios de aceite |
| [Casos de uso](docs/architecture/use-cases.md) | UC por perfil, fluxos principais e alternativos |
| [Bounded Contexts](docs/architecture/bounded-contexts.md) | 16 contextos, mapa de contexto e relações |
| [Modelo de domínio](docs/domain/domain-model.md) | Classes, herança, polimorfismo, serviços, specifications |
| [Agregados](docs/domain/aggregates.md) | Aggregate Roots, invariantes, limites transacionais, concorrência |
| [Value Objects](docs/domain/value-objects.md) | 16 VOs, regras de validação e estratégia de persistência |
| [Modelo físico](docs/database/physical-model.md) | Tabelas, constraints, índices, RLS, particionamento, Outbox |
| [Diagrama ER](docs/database/er-diagram.md) | ER completo em Mermaid |
| [Arquitetura](docs/architecture/overview.md) | Modular Monolith + Clean + Hexagonal + DDD; stack e trade-offs |
| [Diagramas C4](docs/c4/) | Contexto, Contêineres, Componentes |
| [ADRs](docs/adr/) | Decisões arquiteturais com alternativas e trade-offs |
| [Estrutura do repositório](docs/plan/repository-structure.md) | Layout de pastas e regras de dependência |
| [Plano de implementação](docs/plan/implementation-plan.md) | Fases 2 a 10, com entregáveis e critérios de pronto |

## 5. Estado atual

**Fase 1 concluída** — concepção, requisitos, modelagem de domínio, modelo físico e arquitetura.
As fases seguintes (código, migrations, seeds, containers, APIs, telas, testes, observabilidade,
Security Lab, IA, GitHub Pages) estão detalhadas no
[plano de implementação](docs/plan/implementation-plan.md) e serão entregues incrementalmente.

Repositório: `https://github.com/volksec/0009098`
