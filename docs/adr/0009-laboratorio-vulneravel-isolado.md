# ADR-0009 — Laboratório vulnerável isolado por profile

**Status:** Aceito · **Data:** 2026-07-30

## Contexto

O case exige uma aplicação deliberadamente vulnerável para demonstrar, lado a lado, o efeito dos
controles da versão segura. Código vulnerável é risco real: se subir por engano, é um incidente.

## Decisão

A `vulnerable-api` e o `vulnerable-database` existem apenas sob o profile `security-lab`:

```bash
docker compose up --build
```

```bash
docker compose --profile security-lab up --build
```

Controles de contenção: rede Docker `pdc-lab` **sem rota externa** e sem acesso à rede de dados
segura; banco separado com massa sintética própria; limites de CPU e memória; reset automático a
cada execução do simulador; faixa âmbar permanente na UI com o rótulo
`LAB VULNERÁVEL — DADOS SINTÉTICOS — REDE ISOLADA`; ausência total do GitHub Pages; e teste
arquitetural que falha o build se qualquer projeto de produção referenciar a API vulnerável.

## Alternativas consideradas

**Flag de configuração na mesma aplicação** — descartado, e é a alternativa mais perigosa: uma
variável de ambiente errada em produção ativaria as vulnerabilidades. Separação física é a única
contenção confiável.

**Repositório separado** — descartado: perderia a comparação lado a lado, que é o valor pedagógico
central do Security Lab.

**Apenas descrever as vulnerabilidades em documentação** — descartado: o case exige demonstração
executável, não afirmação.

## Consequências

- Dois esquemas de banco para manter (o vulnerável é o mesmo, sem constraints, índices e RLS).
- O `attack-simulator` é o único componente com rota para as duas redes, por construção, para
  executar o cenário na versão vulnerável e replicá-lo automaticamente na segura.
- O `SECURITY.md` documenta explicitamente que o código vulnerável é intencional, laboratorial e
  não deve ser reutilizado.
