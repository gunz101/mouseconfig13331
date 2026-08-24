# SM809 PRO — Driver Local

Software local de configuração para o mouse **SOLAKAKA SM809 PRO**, substituindo o
driver web oficial (`https://driver.yuandaxin-tech.com/HYX/V1000_SM809_3311/`) que
não carrega de forma confiável fora da China (servidor Alibaba Cloud com TLS instável).

É uma **reimplementação do mesmo protocolo HID** do site oficial (documentado em
[PROTOCOLO.md](PROTOCOLO.md)): tudo o que você configura é **gravado na memória
interna do mouse**, exatamente como o driver oficial faz. Depois de aplicar, as
configurações funcionam em qualquer computador, sem precisar deste programa aberto.

Visual: tema escuro estilo Logitech G HUB (grafite + azul elétrico, cards
arredondados, seleção azul, aba ativa com barra azul).

## Como usar

1. Conecte o mouse pelo **cabo USB-C** ou pelo **dongle 2.4G**
   (por Bluetooth a configuração não é possível — limitação do próprio mouse).
2. Abra `SM809Driver.exe`.
3. Selecione um botão na lista à esquerda (ou clique no número na imagem).
4. Escolha a função na aba **Atribuir** — é gravada no mouse na hora.

### Abas

| Aba | O que faz |
|-----|-----------|
| **Atribuir** | Funções de mouse, **teclado numérico completo**, Janelas/Sistema (Alt+Tab, Win+Tab, Alt+F4, captura…), Navegador (abas, F5…), Discord (mudo/deafen), Planilhas/Excel, Texto/Office, multimídia, **F13–F24**, DPI, trava de DPI, atalho personalizado, tecla de fogo, desativar |
| **DPI** | Até 6 níveis (100–26000), cor do LED por nível, nível ativo |
| **Desemp.** | Taxa de retorno (125–1000 Hz), lift-off, economia de energia, angle snap / motion sync / ripple |
| **LED** | 11 efeitos de iluminação |
| **Macros** | Gravador de macros (teclas + cliques + pausas), repetição, atribuição a botões |
| **Sistema** | **Perfis nomeados** (Jogo, Trabalho…), regravar tudo, reset de fábrica |

### Números: existem DUAS categorias — escolha certo

| Categoria | Quando usar |
|-----------|-------------|
| **Números — digitam sempre (recomendado)** | Para digitar valores. Usa os dígitos da fileira de cima: **digita número sempre**, com NumLock ligado ou não. Inclui `,` `.` `-`, Enter, Tab e Backspace. |
| **Teclado numérico (numpad — exige NumLock ligado)** | Só quando precisa do numpad *de verdade*: `Alt`+códigos de caracteres e programas que diferenciam as teclas. Com NumLock **desligado** viram seta/Home/End — por isso "não digitava número". |

Sugestão de layout nos 9 botões laterais: dígitos 1–9 na disposição do numpad físico
(7-8-9 em cima), e `0`, vírgula e Enter nos botões da borda esquerda (10 e 11).
Salve como perfil "Numpad" na aba Sistema.

### Dica: F13–F24 (Discord push-to-talk, OBS)

As teclas F13 a F24 existem no protocolo do teclado mas não no teclado físico —
atribua uma a um botão lateral e use como push-to-talk no Discord ou hotkey do OBS:
nunca conflita com nada. No Discord: Configurações → Teclas de atalho → aperte o
botão do mouse ao definir a tecla.

O botão **12 (clique esquerdo) é travado** de propósito — mesma proteção do driver
oficial, para você nunca ficar sem clique esquerdo.

## Números dos botões

1–9 = laterais (grade do polegar) · 10–11 = borda esquerda · 12 = clique esq. ·
13 = clique dir. · 14 = roda · 15/16 = DPI+/−. Veja `images/mouse_nr.png`.

Padrões de fábrica: laterais enviam `1`–`6`, Win+D, Avançar e Voltar
(por isso alguns botões "não faziam nada" — mande a função que quiser neles).

## Compilar (após editar o código)

```bash
C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe /nologo /codepage:65001 /target:winexe /platform:anycpu /out:SM809Driver.exe /r:System.Web.Extensions.dll SM809Driver.cs
```

Não precisa instalar nada — usa o compilador que já vem com o Windows.

## Arquivos

- `SM809Driver.exe` — o programa (autônomo, sem instalação)
- `SM809Driver.cs` — código-fonte (C# / WinForms)
- `PROTOCOLO.md` — protocolo HID completo do mouse (engenharia reversa do driver oficial)
- `images/` — imagens do driver oficial (numeração dos botões)
- Configuração local: `%APPDATA%\SM809Local\config.json` (cópia do que foi gravado no mouse)

## Solução de problemas

- **"Desconectado"**: use cabo ou dongle 2.4G; clique em Reconectar.
- **Trocou de cabo para dongle** (ou vice-versa): use *Sistema → Regravar tudo no mouse*.
- **Mouse "estranho"**: *Sistema → Reset de fábrica* volta tudo ao padrão.
- O site oficial, se um dia carregar, continua funcionando normalmente
  (Chrome/Edge → Connect Device) — este programa e ele gravam no mesmo lugar.

## NumLock e sincronização em tempo real

- **Indicador NumLock no topo da janela**: mostra o estado atual e **alterna com um
  clique** (liga ⇄ desliga). Necessário porque teclado sem numpad não tem a tecla.
- **Função real de cada botão**: a lista à esquerda mostra o que o botão faz AGORA.
  Com NumLock desligado, botões com tecla de numpad aparecem em amarelo com a função
  efetiva (ex.: "Num 2 → seta ↓").
- **Espelhamento do mouse** (a cada 1,5 s): nível de DPI, taxa de retorno, modo de LED
  e bateria são lidos do mouse e refletidos na interface — inclusive quando você troca
  DPI/LED pelos botões físicos do mouse.
- **Limitação de hardware** (verificada por sondagem): o mouse **não tem comando de
  leitura** dos mapeamentos de botões — só aceita escrita e reporta status. Nem o
  driver oficial consegue ler de volta. Por isso os mapeamentos exibidos vêm do último
  estado gravado (config local), que é fiel desde que só este app (ou o site oficial
  na mesma máquina) configure o mouse. Em dúvida: *Sistema → Regravar tudo*.

## SM809Restore.exe — restauração por linha de comando

```bash
./SM809Restore.exe --reapply
```

Regrava toda a configuração salva no mouse sem abrir a janela. Útil em script de
logon ou atalho, p. ex. depois de usar o mouse em outro PC.
