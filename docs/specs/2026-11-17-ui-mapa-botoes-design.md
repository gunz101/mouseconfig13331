# Interface: mapa dos botões, layout fluido e barra lateral — desenho

**Data:** 2026-11-17 · **Status:** implementado (v1.2)

## 1. Objetivo

Revisar a janela de configuração antes do suporte ao fone SuperFrame Wind V2:

- **Mapa dos botões:** a lista "Botões do mouse" vira uma grade de blocos que reproduz a
  disposição física dos botões, com a função escrita em cada bloco.
- **Tamanho:** o layout passa a acompanhar a janela, inclusive maximizada.
- **Estilo:** as abas nativas do Windows (borda clara, setas, "Sistema" escondida) dão lugar a uma
  barra lateral no estilo G HUB, e as barras de rolagem ficam escuras.

## 2. Decisões tomadas com o usuário

| Tema | Decisão |
|---|---|
| Mapeamento na tela | **Grade dos botões** (estilo MMO): blocos na disposição física, foto pequena só de referência |
| Janela grande | **Tudo cresce junto**: grade e painel de funções dividem o espaço; nenhuma área vazia |
| Navegação | **Barra lateral** com ícone e nome de cada seção (estilo G HUB) |
| Implementação | **Gerenciadores de layout + controles desenhados sob medida** (`ButtonMapView`, `NavBar`) |

## 3. Problemas encontrados no estado atual

- **Espaço vazio ao maximizar:** a janela recebia o tamanho salvo *antes* de os painéis serem
  criados. As âncoras do WinForms gravam a distância até a borda no momento da criação, então os
  painéis "decoravam" uma margem de ~420 px e a mantinham.
- **Clique na foto selecionando o botão errado:** a coordenada do botão 2 estava 28 px fora do
  número (o clique no "2" caía mais perto do botão 3) e a do 15 estava 7 px acima. As coordenadas
  passam a ser os centros medidos dos números vermelhos de `images/mouse_nr.png`.
- **Abas:** o `TabControl` nativo desenha uma borda clara que o tema não alcança; com largura fixa
  de 78 px, 6 abas não cabiam em 500 px.

## 4. Layout da janela

```
┌ Cabeçalho (topo) ──────────────────────────────────────────────────────┐
├────────┬──────────────────────────────┬────────────────────────────────┤
│ NavBar │  Mapa dos botões             │  Seção ativa                   │
│  84 px │  48% da largura restante     │  52% da largura restante       │
├────────┴──────────────────────────────┴────────────────────────────────┤
│ Barra de status (base)                                                 │
└────────────────────────────────────────────────────────────────────────┘
```

- Tudo por encaixe (`Dock`) e uma `TableLayoutPanel` de duas colunas em porcentagem; nenhum
  controle de primeiro nível com posição fixa.
- Tamanho mínimo continua 1240×760. O tamanho e o maximizado salvos são aplicados **depois** de a
  janela ser montada.
- O mapa fica sempre visível, em qualquer seção.
- As seções (Atribuir, DPI, Desempenho, LED, Macros, Sistema) deixam de ser `TabPage` e viram
  painéis comuns com rolagem automática; só a ativa fica visível. O conteúdo interno de cada seção
  não muda.
- A seção ativa é lembrada entre aberturas (`NavIndex` no `config.json`).
- O atalho "Macro…" da seção Atribuir ativa a seção Macros pela `NavBar`.

## 5. NavBar

Controle desenhado sob medida, largura 84 px. Cada item tem 64 px de altura, com glifo da Segoe
Fluent Icons em cima e nome embaixo:

| Seção | Glifo |
|---|---|
| Atribuir | `E765` |
| DPI | `EC4A` |
| Desempenho | `E945` |
| LED | `EA80` |
| Macros | `E7C8` |
| Sistema | `E713` |

Item ativo: fundo `AccentDark` com barra `Accent` de 3 px à esquerda e texto branco. Passar o
mouse: fundo `ChipHover`. Os demais itens usam `SubText`. Evento `SelectedChanged(int)`.

## 6. Mapa dos botões (`ButtonMapView`)

**Fonte única das posições:** os centros dos números na foto (coordenadas da imagem 400×400),
usados para o clique na foto e para montar a grade:

| Botão | Centro | Botão | Centro |
|---|---|---|---|
| 1 | (118,152) | 9 | (166,233) |
| 2 | (116,201) | 10 | (163,55) |
| 3 | (135,184) | 11 | (173,93) |
| 4 | (153,171) | 12 | (195,40) |
| 5 | (127,244) | 13 | (257,33) |
| 6 | (145,226) | 14 | (246,73) |
| 7 | (142,292) | 15 | (259,162) |
| 8 | (159,268) | 16 | (267,202) |

**Grade do polegar (1–9):** três colunas escalonadas, como no mouse: externa (1, 2, 5, 7), meio
(3, 6, 8) e interna (4, 9). Cada bloco fica na coluna do seu botão. A altura dele é proporcional ao
centro real (y de 152 a 292), com escala escolhida para que a menor distância dentro de uma coluna
(42 px reais) caiba um bloco mais o espaçamento. A ordem vertical dos blocos é a mesma do mouse.

**Topo e outros (10–16):** blocos em linhas, com quebra automática pela largura (bloco mínimo de
104 px), na ordem 10, 11, 12, 13, 14, 15, 16.

**Foto:** à direita da grade do polegar, quadrada, clicável. O clique seleciona o botão cujo
número esteja a até 14 px (coordenadas da imagem) e desenha um anel `Accent` no botão selecionado.

**Tamanhos:** a grade ocupa 58% da largura interna e a foto o restante. A largura do bloco é
derivada da coluna. A altura vem do que couber na vertical, limitada entre 40 e 120 px e a no
máximo 0,8× a largura. O conjunto é centralizado na vertical. A fonte da função escala com a
altura do bloco (0,22× a altura, de 12 a 18 px), o que garante duas linhas inteiras no tamanho
mínimo da janela.

**Bloco:** número pequeno no canto (`SubText`; `Accent` quando selecionado) e a função centralizada,
quebrada em linhas, com reticências na última linha visível quando o texto não cabe.

| Estado | Aparência |
|---|---|
| normal | fundo `Chip` |
| passar o mouse | fundo `ChipHover` |
| selecionado | fundo `AccentDark` com borda `Accent` de 2 px |
| travado (botão 12) | texto `SubText` e cadeado |
| tecla de numpad com NumLock desligado | função em amarelo, com o que ela faz de fato (ex.: "Num 2 → seta baixo") |

**Dica:** passar o mouse mostra "Botão 5 — Lateral 5: Num 5".

**Teclado:** foco por Tab. Setas para cima/esquerda vão ao botão anterior e para baixo/direita ao
próximo (1→16).

**Interface pública:** `SetTiles(IList<MapTile>)`, `SelectedIndex` (0–15 ou -1) e o evento
`SelectionChanged`. O cálculo de posições é uma função pura, `MapLayout.Compute(Size)`, e o clique
na foto é `MapLayout.PhotoHit(PointF imagem)`; os dois são testáveis sem janela.

## 7. Acabamento

- **Barras de rolagem escuras:** `SetWindowTheme(handle, "DarkMode_Explorer")` nos controles com
  rolagem (seções, lista de macros).
- **Barra de título escura:** `DWMWA_USE_IMMERSIVE_DARK_MODE` em todas as janelas (base `DarkForm`).
- **Atribuir:** uma linha no topo da seção diz "Atribuindo ao botão 5 — Lateral 5", ou "Escolha um
  botão no mapa" sem seleção.
- **Textos:** "lista à esquerda" vira "mapa". As dicas fixas sob a foto antiga saem; a informação de
  que a configuração fica gravada no mouse permanece em Sistema → Sobre.

## 8. Arquivos

| Arquivo | Mudança |
|---|---|
| `MapView.cs` (novo) | `MapTile`, `MapLayout` (puro), `ButtonMapView` |
| `NavBar.cs` (novo) | `NavBar` |
| `SM809Driver.cs` | `MainForm` monta o layout novo; saem lista, foto e abas antigas; `NavIndex` na configuração; `Theme.DarkScroll`; `DarkForm` |
| `TaskbarBattery.cs` | `Win32.SetWindowTheme`, `Win32.DwmSetWindowAttribute`; `IconFont` reaproveitado pela `NavBar` |
| `build.cmd`, `README.md` | novos arquivos; uso da grade e da barra lateral |

## 9. Validação

1. **Testes puros do layout**, nos tamanhos mínimo, médio, grande e 4K: os 16 blocos ficam dentro
   da área e sem sobreposição; a foto não encosta em bloco nenhum; a ordem das colunas e a ordem
   vertical seguem o mouse; o bloco cresce com a área.
2. **Clique na foto:** o centro de cada número seleciona o próprio botão (16 casos). O centro do "2"
   seleciona o 2, e não o 3.
3. **NavBar:** cada faixa de altura corresponde à seção certa.
4. **Layout da janela:** sem sobreposições em cada uma das 6 seções, no mínimo e ampliado. Ampliada,
   a área de conteúdo chega até a borda direita e até a barra de status (sem vão). A seção Sistema
   cabe sem cortar.
5. **Capturas da janela** no tamanho mínimo e maximizada.

## 10. Fora do escopo

Fone SuperFrame Wind V2 (próximo trabalho). Reorganizar o conteúdo interno de DPI, Desempenho,
LED, Macros e Sistema, que continuam como estão, só que dentro do painel novo.
