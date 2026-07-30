# ADR-0001 — Nome e identidade visual próprios

**Status:** Aceito · **Data:** 2026-07-30

## Decisão

Nome **PortalDoCorretor**, com identidade autoral completa: logotipo (monograma `NB` em hexágono
aberto), paleta `pdc-*` (azul-marinho, azul-elétrico, âmbar, verde, vermelho), tipografia livre
(Inter + JetBrains Mono) e design system próprio construído sobre Tailwind + shadcn/ui, com os
componentes versionados no repositório.

Cinco candidatos foram avaliados (ver README). "PortalDoCorretor" venceu por comunicar o papel de hub
entre corretor, cliente, produto e regulador, permitir submarcas e não colidir com nomenclatura
existente no setor.

## Alternativas consideradas

- **Corretor 360** — descartado: "360" é sufixo saturado no mercado financeiro, baixa distintividade.
- **SecureBroker** — descartado: posiciona o produto como ferramenta de segurança, não de gestão.
- **BrokerCore** — descartado: genérico, sugere componente interno em vez de produto.
- **Aegis Corretores** — descartado: baixa clareza para o público de negócio, mistura idiomas.

## Consequências

- Todo material (README, Pages, UI, containers, namespaces) usa `PortalDoCorretor` / `portal-do-corretor`.
- O aviso de escopo aparece no README, na landing do Pages e no rodapé da aplicação.
- O modo laboratório usa faixa âmbar com rótulo explícito, impedindo confusão entre ambientes.
- shadcn/ui foi escolhido justamente porque os componentes são **copiados para o repositório**, não
  consumidos como dependência — o design system é de fato próprio e customizável.
