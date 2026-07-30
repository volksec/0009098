# ADR-0005 — TPH para Customer, TPT para InsurableAsset

**Status:** Aceito · **Data:** 2026-07-30

## Contexto

Duas hierarquias precisam ser persistidas: `Customer → IndividualCustomer | BusinessCustomer` e
`InsurableAsset → Vehicle | Property`. As opções são Table Per Hierarchy (TPH), Table Per Type
(TPT) e Table Per Concrete Type (TPC).

## Decisão

**TPH para `Customer`.** As subclasses compartilham a maior parte dos atributos (documento, status,
contatos, endereços, consentimentos) e quase toda consulta é polimórfica ("liste os clientes do
tenant"). TPH evita join nessas consultas. Os campos específicos ficam anuláveis, e a coerência é
garantida por `CHECK` com o discriminador — uma linha `INDIVIDUAL` com `legal_name` preenchido é
rejeitada pelo banco.

**TPT para `InsurableAsset`.** Veículo e imóvel divergem quase completamente: placa, chassi, ano e
uso versus área, tipo de construção, ano de construção e localização. TPH produziria uma tabela
majoritariamente nula e — mais grave — impediria `NOT NULL` em campos obrigatórios de cada tipo. A
integridade da hierarquia é garantida por **FK composta `(id, kind)`**, que impede um registro
`VEHICLE` de ter filho em `properties`.

## Alternativas consideradas

**TPH para ambos** — descartado para assets: perderia todas as constraints `NOT NULL` dos campos
específicos, que é justamente onde a integridade do bem segurável mora.

**TPT para ambos** — descartado para clientes: adicionaria join em toda listagem e busca, que são
as consultas mais frequentes do sistema.

**TPC** — descartado: dificulta FK apontando para a base (`quotations.asset_id` precisa referenciar
qualquer bem) e complica a geração de identidade.

## Consequências

- Duas estratégias no mesmo sistema, o que exige justificar a escolha — feito aqui.
- `insurable_assets` precisa da constraint `UNIQUE (id, kind)` para permitir a FK composta.
- Acrescentar um novo tipo de bem exige nova tabela e novo valor de enum, mas **nenhum `switch`
  existente muda** — o polimorfismo do domínio continua fechado para modificação.
