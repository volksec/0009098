# ADR-0001 — Identidade visual e design system próprios

**Status:** Aceito · **Data:** 2026-07-30

## Contexto

A plataforma precisa de uma identidade visual autoral e de um design system que sustente tanto as
telas de operação (clientes, cotações, propostas, apólices, comissões, sinistros) quanto as
ferramentas técnicas (Live Processing Console, Query Inspector, Database Explorer), que têm
necessidades visuais bastante diferentes entre si.

O requisito é que nenhum elemento visual — logotipo, tipografia, iconografia, ilustração ou paleta
— seja proveniente de terceiros. Isso vale tanto por licenciamento quanto porque identidade
emprestada torna impossível evoluir o produto sem retrabalho.

## Decisão

### Nome

`Portal do Corretor`, com o identificador técnico `PortalDoCorretor` em namespaces e projetos, e
`portal-do-corretor` em nomes de contêiner, rede e diretório.

### Logotipo

Monograma `PC` inscrito em um hexágono **aberto** no vértice superior direito, representando o nó
de rede que conecta os atores do ecossistema — corretor, cliente, produto e supervisão. Sem
escudos, brasões, gotas, guarda-chuvas ou qualquer arquétipo visual tradicional do setor.

### Paleta

Tokens `pdc-*`, definidos uma única vez e consumidos por toda a interface:

| Token | Hex | Uso |
|---|---|---|
| `pdc-navy-900` | `#0B2447` | Superfícies institucionais, header, sidebar |
| `pdc-blue-600` | `#1F6FEB` | Ação primária, links, foco |
| `pdc-blue-100` | `#DCE9FD` | Estados selecionados, badges informativos |
| `pdc-slate-900` | `#141821` | Texto primário / fundo do modo escuro |
| `pdc-slate-50` | `#F4F6F8` | Fundo da aplicação (modo claro) |
| `pdc-amber-500` | `#F2A93B` | Pendências, atenção, avisos de ambiente |
| `pdc-red-600` | `#D93F3F` | Erros, bloqueios de autorização, eventos de segurança |
| `pdc-green-600` | `#1F9D63` | Sucesso, apólice emitida, controle que bloqueou |

Semântica fixa por cor: âmbar sempre indica atenção ou pendência, vermelho sempre indica bloqueio
ou erro, verde sempre indica sucesso ou controle atuando. Não há uso decorativo dessas três — o
que garante que a leitura de um painel seja consistente entre telas.

### Tipografia

`Inter` para a interface e `JetBrains Mono` para as telas técnicas, ambas sob licença livre
(SIL OFL). A escolha de duas famílias é funcional: SQL, planos de execução e logs exigem largura
fixa para que colunas e indentação sejam legíveis.

### Design system

Construído sobre **Tailwind CSS + shadcn/ui**.

A escolha do shadcn é deliberada e é o ponto central desta decisão: os componentes são **copiados
para o repositório**, não consumidos como dependência de terceiros. O design system é efetivamente
autoral e customizável, e uma atualização de biblioteca externa nunca altera a aparência do produto
sem revisão.

### Modo de ambiente

Quando a API de comparação está ativa, a interface recebe uma faixa diagonal `pdc-amber-500` com
rótulo permanente identificando o ambiente, tornando impossível confundir as duas implementações
durante uma demonstração.

## Alternativas consideradas

**Biblioteca de componentes pronta (MUI, Ant Design, Chakra)** — descartada. Entregaria velocidade
inicial, mas o produto herdaria uma linguagem visual reconhecível de terceiros, e customizações
profundas em bibliotecas com tema próprio costumam custar mais do que construir sobre utilitários.

**CSS puro ou CSS Modules sem framework** — descartado. Sem uma camada de tokens e utilitários, a
consistência passa a depender de disciplina, e o sistema diverge à medida que as telas crescem.

**Tailwind sem shadcn** — descartado. Tailwind resolve estilo, mas não resolve acessibilidade de
componentes compostos (combobox, dialog, menu). O shadcn traz esses componentes já acessíveis, com
o código no repositório.

## Consequências

- Todo material do projeto (documentação, interface, contêineres, namespaces) usa
  `PortalDoCorretor` ou `portal-do-corretor` de forma consistente.
- Os componentes ficam versionados no repositório, então mudanças visuais aparecem em code review
  como qualquer outra alteração de código.
- O inventário de componentes é documentado no Storybook, com variantes, estados e testes de
  interação, atendendo ao requisito de acessibilidade WCAG 2.1 AA.
- A semântica fixa de cores precisa ser respeitada pelas telas novas; um uso decorativo de vermelho
  ou âmbar quebra a leitura dos painéis e deve ser rejeitado em revisão.
