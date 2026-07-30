# ADR-0010 — Agentes de IA com privilégio mínimo e guardrails

**Status:** Aceito · **Data:** 2026-07-30

## Contexto

O sistema tem cinco agentes (Broker Copilot, Regulatory Assistant, Database Review, Architecture
Review, AppSec Review). Um agente com acesso a dados de múltiplos tenants, ou suscetível a prompt
injection, anularia todo o trabalho de isolamento feito no resto da plataforma.

## Decisão

Todo agente executa **sob a identidade e o tenant do usuário que o invocou**, nunca com conta de
serviço privilegiada. Cada agente declara: skills, allowlist de ferramentas, limite de execução por
janela de tempo e guardrails de recusa.

Defesa contra prompt injection: conteúdo recuperado do banco ou de documentos entra no contexto
como **dado delimitado, nunca como instrução**; instruções encontradas dentro de conteúdo são
ignoradas e geram `SecurityEvent`. Um cenário do Attack Simulator injeta payload em campo sintético
de cliente e verifica que o agente não obedece.

Toda execução é registrada em `agent_executions` com entrada e saída **redigidas**, ferramentas
invocadas, duração, custo e guardrail acionado.

## Alternativas consideradas

**Agente com conta de serviço** — descartado: seria um bypass de tenant por design, contradizendo o
ADR-0004.

**Sem limite de execução** — descartado: custo descontrolado e vetor de negação de serviço.

**Agente com acesso SQL livre** — descartado, inclusive para o Database Review Agent: ele analisa
planos e queries **já coletados** pela instrumentação, e não executa SQL arbitrário.

## Consequências

- O Regulatory Assistant organiza evidência mas recusa explicitamente emitir decisão regulatória, e
  a recusa é auditada.
- A redação de entrada e saída é obrigatória, o que impede depurar o prompt exato — trade-off
  aceito em favor da privacidade.
- Os agentes agregam valor real ao avaliador (explicar um plano de execução, mapear um controle ao
  ASVS) em vez de existirem como enfeite de IA.
