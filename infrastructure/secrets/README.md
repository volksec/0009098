# Segredos locais

**Nenhuma credencial é versionada neste repositório**, nem de desenvolvimento (RNF-004).

Os arquivos `*.txt` deste diretório estão no `.gitignore`. Apenas os `*.txt.example` são
versionados, com valores-marcador.

## Preparar o ambiente local

```bash
cp infrastructure/secrets/db_password.txt.example infrastructure/secrets/db_password.txt
cp .env.example .env
```

Depois edite os dois arquivos com valores locais próprios. O `docker compose up` falha
explicitamente se as variáveis não estiverem definidas — falha fechado, em vez de subir
com um padrão inseguro.

## Por que não deixar a senha de desenvolvimento no repositório

"É só local" é a justificativa que costuma preceder o vazamento: o valor local vira o valor
de homologação, que vira o de produção. Além disso, um segredo no histórico do git permanece
lá mesmo depois de removido do HEAD.

O CI executa varredura de segredo (gitleaks) e bloqueia o merge se alguma credencial for
commitada.
