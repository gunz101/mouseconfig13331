# Bateria na barra de tarefas + app residente — desenho

**Data:** 2026-11-17 · **Status:** aprovado em conversa, aguardando revisão do documento
**Inspiração:** [krisgfx9/Wireless-Device-Battery-Monitor](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor)
(GPL-3.0 — só a *técnica* é reaproveitada; nenhum código é copiado, o projeto continua MIT).

## 1. Objetivo

Ver a bateria do SM809 PRO o tempo todo, direto na barra de tarefas (`🖱 93%`), e transformar o
SM809 Driver Local em um aplicativo do Windows: inicia com o PC, fica residente na bandeja, e a
janela de configuração abre e fecha sem encerrar o app.

## 2. Escopo

**Dentro:** bateria do SM809 PRO (cabo USB-C e dongle 2.4G); tira na taskbar; alerta de bateria
baixa; ícone na bandeja com menu; iniciar com o Windows (opcional, desligado por padrão); ícone
próprio do app; app residente com instância única.

**Fora (agora):** fone SuperFrame Wind V2 e outros dispositivos (a tira já aceita uma lista de
itens para recebê-los depois); bateria do mouse em modo Bluetooth (o mouse não aceita conexão de
configuração por BT); barras de tarefas de monitores secundários; evitar sobreposição com botões
de apps no Windows 10.

## 3. Decisões tomadas com o usuário

| Tema | Decisão |
|---|---|
| Exibição | Tira desenhada na taskbar (igual à referência) + ícone do app na bandeja para o menu |
| Alerta | Cor (laranja/vermelho) + **uma** notificação do Windows no nível crítico |
| Arquitetura | **A — processo único residente** (um só dono da conexão HID) |

## 4. Arquitetura

```
                ┌──────────────────────── processo SM809Driver.exe ───────────────────────┐
 thread UI      │  TrayHost (ApplicationContext)                                          │
 (STA)          │   ├─ Sm809Device ........ conexão HID (já existe, sem mudanças)          │
                │   ├─ timers ............. reconexão 5 s · status 1,5 s / 15 s            │
                │   ├─ BatteryView ........ status → texto/cor/estado (lógica pura)        │
                │   ├─ AlertLatch ......... dispara a notificação uma vez (lógica pura)    │
                │   ├─ NotifyIcon + menu .. bandeja                                        │
                │   ├─ AutoStart .......... chave Run do usuário                           │
                │   └─ MainForm ........... janela de configuração (criada sob demanda)    │
                │                                                                          │
 thread tira    │  TaskbarBattery ......... janela filha da taskbar (só desenha, sem I/O)  │
 (STA, PMv2)    │                                                                          │
                └──────────────────────────────────────────────────────────────────────────┘
```

| Unidade | Arquivo | Faz | Depende de |
|---|---|---|---|
| `TrayHost` | `TrayHost.cs` (novo) | Ponto de entrada residente: dono do `Sm809Device`, timers, bandeja, menu, janela, alertas, autostart | `Sm809Device`, `TaskbarBattery`, `MainForm` |
| `BatteryView` | `TrayHost.cs` | Converte `DeviceStatus` + estado da conexão em `{texto, nível de cor, esmaecido, carregando, dica}` | nada (função pura) |
| `AlertLatch` | `TrayHost.cs` | Decide se a notificação dispara agora e quando rearma | nada (função pura) |
| `AutoStart` | `TrayHost.cs` | Lê/grava/remove o valor `SM809Driver` em `HKCU\…\Run`; corrige o caminho se o `.exe` mudou de pasta | registro (HKCU) |
| `TaskbarBattery` | `TaskbarBattery.cs` (novo) | Cria, desenha, posiciona e recupera a tira; reporta cliques e indisponibilidade | Win32 (user32/gdi32) |
| `MainForm` | `SM809Driver.cs` (muda pouco) | Configuração — passa a **receber** o `Sm809Device` em vez de criá-lo | `Sm809Device` |

O `Sm809Device` não muda. O `SM809Driver.cs` muda só no construtor da `MainForm`, no fechamento
da janela e no `Main`.

A `MainForm` não depende do `TrayHost`. Ela recebe o `Sm809Device` e mostra o estado da conexão
lendo `IsConnected` no timer de UI que já tem (hoje usado para o NumLock). O botão
**Reconectar** chama `Connect()` no mesmo objeto. Host e janela rodam na mesma thread de UI,
então não disputam a conexão.

## 5. Ciclo de vida

| Ação | Comportamento |
|---|---|
| Abrir o `.exe` (sem argumentos) | Inicia residente e mostra a janela |
| Abrir com `--tray` (usado pelo boot) | Inicia residente **sem** janela: só tira + bandeja |
| "X" da janela | Esconde a janela (salva tamanho/posição como hoje); o app continua rodando |
| Bandeja → **Sair** | Fecha janela, tira e ícone; libera o mouse; encerra o processo |
| Abrir o `.exe` com o app já rodando | A nova instância sinaliza a existente (evento nomeado `SM809LocalDriver.Show`), que traz a janela para a frente; a nova encerra |
| Abrir com `--tray` com o app já rodando | A nova instância encerra em silêncio (não abre a janela) |
| `--uicheck`, `--selftest` | Modos utilitários de console sem acesso ao mouse: executam e saem |
| `--reapply`, `--battcheck` | Modos de console **com** acesso ao mouse: se o app residente estiver rodando (mutex `SM809LocalDriver`), recusam com a mensagem "feche pelo menu da bandeja → Sair, ou use Regravar tudo na aba Sistema". Dois processos falando com o mouse misturariam as confirmações (ACK) e corromperiam gravações |

A `MainForm` é criada na primeira vez que for aberta e depois só escondida/mostrada.

## 6. Dados da bateria

Fonte: o relatório de status que o app já lê (feature 5 → input report 4). Semântica confirmada
no código do driver web oficial:

| Campo | Valor | Significado |
|---|---|---|
| `linkStatus` | `0` | mouse **offline** (dormindo/desligado); outro valor = online |
| `batteryStatus` | `1` | **carregando** — o oficial **não mostra porcentagem** (valor não confiável na carga) |
| `batteryStatus` | `2` | **carga completa** — o oficial mostra "Cheia 100%" |
| `batteryStatus` | outro | descarregando — vale `batteryLevel` (0–100) |

O mouse parece reportar em passos de 10% (leituras observadas: 90 e 70), por isso as faixas usam `≤`.

**Leitura periódica:** a cada 1,5 s com a janela visível (como hoje) e a cada 15 s com a janela
escondida. Os eventos do leitor HID chegam em outra thread e são remetidos para a thread da UI.

## 7. Estados visuais da tira

| Situação (prioridade de cima para baixo) | Tira | Cor do texto |
|---|---|---|
| Sem conexão com cabo/dongle, ou nenhuma leitura ainda | `🖱 —` | tema, esmaecido (50%) |
| `linkStatus == 0`, ou nenhum relatório há mais de 60 s | `🖱 <último>%` | tema, esmaecido |
| `batteryStatus == 1` (carregando) | `🖱⚡` (sem número) | tema |
| `batteryStatus == 2` (completa) | `🖱⚡ 100%` | tema |
| nível ≤ 10 | `🖱 10%` | vermelho `#FF5C5C` |
| nível ≤ 20 | `🖱 20%` | laranja `#FFA43A` |
| demais | `🖱 93%` | tema (branco na barra escura, quase preto na clara) |

**Dica ao passar o mouse:** `SM809 PRO — 93% · 2.4G · DPI 1600` (ou `carregando`, `dormindo`,
`desconectado`). **Clique:** abre a configuração. **Botão direito:** abre o menu da bandeja.

## 8. Alerta de bateria baixa

`AlertLatch` armado → quando o mouse está online, descarregando e com nível ≤ 10, dispara **uma**
notificação ("Bateria do mouse em 10% — conecte o cabo") pelo ícone da bandeja (vira toast no
Windows 11) e desarma. Rearma quando o nível passar de 20 ou o mouse entrar em carga/completa.
Com o modo "não perturbe" ativo (comum em jogos em tela cheia), o Windows guarda a notificação na
central. A cor vermelha na tira continua sendo o sinal principal.

## 9. A tira (técnica)

- **Janela:** `CreateWindowEx` com `WS_EX_LAYERED | WS_EX_NOACTIVATE` e `WS_CHILD | WS_CLIPSIBLINGS`,
  tendo como pai a `Shell_TrayWnd`. É desenhada com `UpdateLayeredWindow` a partir de um bitmap
  ARGB 32 bits premultiplicado (GDI+, antialias em escala de cinza, porque ClearType não funciona
  sobre transparência).
- **Conteúdo:** glifo `U+E962` (mouse) e `U+E945` (raio) da fonte **Segoe Fluent Icons**
  (`SegoeIcons.ttf`), com fallback **Segoe MDL2 Assets**; texto em **Segoe UI**, 13 px a 96 DPI,
  escalado pelo DPI da taskbar. (Segoe UI: a Segoe UI Variable é fonte variável e o GDI+ não a
  renderiza bem.)
- **Posição:** encostada à esquerda da `TrayNotifyWnd`, centralizada na altura da barra. Conferida
  a cada 1 s (largura da área de notificação, DPI, tema claro/escuro em
  `HKCU\…\Themes\Personalize\SystemUsesLightTheme`); só redesenha quando algo muda.
- **DPI:** o processo continua *DPI-unaware* (a configuração validada não muda). Só a thread da
  tira chama `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)` antes de criar a janela, para
  casar com a taskbar.
- **Thread própria e sem I/O:** por ser filha de uma janela do Explorer, a fila de entrada da
  thread da tira fica ligada à da taskbar, e travar essa thread travaria a barra de tarefas. Ela
  **nunca** faz acesso HID nem espera nada: só recebe itens prontos (`SetItems`), desenha e reporta
  cliques.
- **Recuperação:** se a `Shell_TrayWnd` sumir ou mudar (Explorer reiniciou), a checagem de 1 s
  recria a tira. Se não for possível criá-la (barra vertical, classes ausentes), o `TrayHost`
  passa ao **plano B**: o número aparece desenhado dentro do ícone da bandeja.
- **Manifest:** `app.manifest` declara compatibilidade com Windows 8/8.1/10/11 (exigido para janela
  filha *layered*), Common Controls v6 (dica com visual atual) e `asInvoker`. Não declara DPI,
  para manter o processo *unaware*.

## 10. Bandeja e menu

`NotifyIcon` com o ícone do app. Duplo clique abre a configuração. Menu (tema escuro):
**Abrir configurações** · **Iniciar com o Windows** ✓ · separador · **Sair**. Dica do ícone =
mesma dica da tira.

## 11. Iniciar com o Windows

- Valor `SM809Driver` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` =
  `"<caminho completo do .exe>" --tray`. É só do usuário e não pede administrador. Aparece em
  Gerenciador de Tarefas → Aplicativos de inicialização.
- **Desligado por padrão.** Só é gravado quando o usuário marca a opção (menu da bandeja ou
  checkbox na aba Sistema); desmarcar remove o valor.
- Ao iniciar, se a opção estiver ligada e o caminho gravado for diferente do `.exe` atual (o
  arquivo mudou de pasta), o valor é atualizado.

## 12. Ícone do app

O glifo de mouse (`U+E962`) em azul `#00B8FC` sobre fundo transparente. Arquivo `images/app.ico`
multi-tamanho (16, 20, 24, 32, 48, 256), gerado por `tools/make-icon.ps1` e versionado. É usado
no `.exe` (`-win32icon`), embutido como recurso para a bandeja e para a janela (substitui o uso do
`logo.png` como ícone da janela).

## 13. Build

Um `build.cmd` na raiz compila os dois alvos com o mesmo comando-base:

```
csc -nologo -codepage:65001 -platform:anycpu -r:System.Web.Extensions.dll
    -win32manifest:app.manifest -win32icon:images\app.ico -resource:images\app.ico,SM809.app.ico
    SM809Driver.cs TrayHost.cs TaskbarBattery.cs
    → -target:winexe -out:SM809Driver.exe   (app)
    → -target:exe    -out:SM809Restore.exe  (console: --reapply/--uicheck/--battcheck/--selftest)
```

## 14. Erros e recuperação

| Falha | Reação |
|---|---|
| Mouse/dongle ausente na inicialização | Tira `🖱 —`; reconexão a cada 5 s, como hoje |
| Dongle removido durante o uso | `ConnectionLost` (já existe) → tira `🖱 —`; reconecta sozinho |
| Explorer reiniciado | Tira recriada na checagem de 1 s; `NotifyIcon` se recria sozinho |
| Fonte de ícones ausente | Fallback MDL2; se também faltar, mostra só o texto |
| Falha ao gravar no registro | Mensagem na barra de status; a opção volta para o estado real |
| Exceção ao desenhar a tira | Capturada na thread da tira; plano B (número no ícone da bandeja) |

## 15. Validação

1. `--selftest` (console, sem hardware): `BatteryView` cobrindo todos os estados da tabela §7;
   transições do `AlertLatch` (dispara uma vez, não repete, rearma); formatação e correção de
   caminho do `AutoStart` (sem gravar no registro); cálculo de posição da tira dado o retângulo da
   área de notificação e o DPI.
2. `--uicheck` continua passando (layout da configuração intacto).
3. `--battcheck` (console, só leitura, com o app residente fechado): imprime `linkStatus`,
   `batteryStatus` e nível lidos do mouse real. Também confirmo que ele **recusa** rodar com o
   app residente aberto.
4. App com `--tray`: o processo fica vivo; a janela filha da tira existe dentro da `Shell_TrayWnd`
   com posição e tamanho coerentes (inspecionado por enumeração de janelas); captura de tela da
   barra para o usuário conferir.
5. X esconde, segunda abertura traz a janela, Sair encerra e libera o mouse.
6. **Pelo usuário:** ligar "Iniciar com o Windows" (depois confirmo só *lendo* o registro) e
   reiniciar o Explorer para ver a tira voltar.

## 16. Riscos e limitações

- Janelas internas do Explorer (`Shell_TrayWnd`, `TrayNotifyWnd`) não são API oficial; uma
  atualização do Windows pode mudá-las. A mitigação é o plano B automático (número no ícone da
  bandeja).
- No Windows 10, ou no 11 com apps alinhados à esquerda e a barra cheia, a tira pode ficar sobre
  botões de apps.
- Com a escala de passos de 10%, o alerta crítico ocorre em 10%.

## 17. Próximo passo previsto (fora deste trabalho)

Fone SuperFrame Wind V2: investigar como ele expõe bateria (Bluetooth GATT Battery Service, HID
do dongle ou protocolo próprio) e adicioná-lo como segundo item da tira.
