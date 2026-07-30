# Ativação do pipeline de CI

O workflow está versionado em [`infrastructure/ci/ci.yml`](../infrastructure/ci/ci.yml).

Ele não foi commitado diretamente em `.github/workflows/` porque o GitHub exige o escopo
OAuth `workflow` para criar ou alterar workflows via push — proteção do próprio GitHub contra
um token comprometido injetar automação no repositório.

Para ativar:

```bash
gh auth refresh -h github.com -s workflow
```

```bash
mkdir -p .github/workflows && git mv infrastructure/ci/ci.yml .github/workflows/ci.yml && git commit -m "ci: ativar pipeline" && git push
```
