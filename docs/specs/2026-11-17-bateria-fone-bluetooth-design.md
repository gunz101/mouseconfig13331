# Bateria do fone Bluetooth na barra de tarefas — desenho

**Data:** 2026-11-17 · **Status:** implementado (v1.3)

## 1. Objetivo

Mostrar a bateria do fone sem fio na tira da barra de tarefas, ao lado da bateria do mouse. O pedido
original citava o SuperFrame Wind V2.

## 2. O que a investigação mostrou

Tudo foi verificado só com leitura de propriedades do Windows, sem enviar nada aos dispositivos.

- **Wind V2 pelo dongle 2.4G:** o dongle (`USB\VID_4C4A&PID_4155`, chip Jieli) expõe só áudio USB
  e uma coleção HID Consumer Control com 16 botões de mídia. O relatório de entrada tem 3 bytes, e os
  de saída e *feature*, 0. Não existe canal para ler ou pedir a bateria.
- **Fones Bluetooth:** o Windows recebe a bateria pelo perfil HFP e a guarda como propriedade do nó
  "Hands-Free AG" do fone, a mesma que aparece em Configurações → Bluetooth. O nó `BTHENUM\DEV_<endereço>`
  informa se o fone está conectado. O Windows mantém o último valor de fones desconectados, então a
  conexão precisa ser conferida.
- O Wind V2 é anunciado também com Bluetooth. Pareado assim, aparece como qualquer outro fone, desde
  que informe a bateria.

## 3. Decisões tomadas com o usuário

| Tema | Decisão |
|---|---|
| O que mostrar | **O fone Bluetooth em uso** (qualquer um que informe bateria) |
| Como ler | **Ler o que o Windows já guarda**; nenhuma comunicação com o fone |
| Descartado | APIs de Bluetooth LE (só cobririam LE Audio); falar HFP direto com o fone (disputa a conexão do Windows e seria sondagem) |

## 4. Comportamento

- A tira ganha um item por fone Bluetooth **conectado** que tenha bateria informada: `🖱 80%  🎧 90%`.
  Sem fone conectado, o item some.
- O glifo é o fone da Segoe Fluent Icons (`E7F6`).
- As cores e o alerta são os do mouse: laranja até 20%, vermelho até 10% e uma notificação por
  descarga, "Bateria do fone AuraBuds WF7 em 10%". O alerta rearma acima de 20%, como no mouse.
- O tooltip da tira tem uma linha por dispositivo: "SM809 PRO — 80% · 2.4G · DPI 1600" e
  "AuraBuds WF7 — 90%".
- A bateria do fone costuma vir em passos de 10%, e o HFP não informa carga. O item mostra o último
  valor recebido pelo Windows, sem raio de carregando.
- O menu da bandeja ganha **"Mostrar fones Bluetooth"**, marcado por padrão.
- Plano B (tira indisponível): o número no ícone da bandeja continua sendo o do mouse. O texto do
  ícone inclui a linha do fone quando cabe nos 63 caracteres permitidos.
- A janela de configuração do mouse não muda.

## 5. Arquitetura

### `BluetoothBattery.cs` (novo)

| Tipo | Papel |
|---|---|
| `BtNode` | Retrato de um nó `BTHENUM`: `InstanceId`, `Name`, `Battery` (-1 = sem valor), `Connected` |
| `BtBattery` | Um fone a mostrar: `Address` (12 hex), `Name`, `Percent` |
| `BluetoothBattery.AddressOf(string)` | Puro: extrai o endereço do id do nó (`DEV_<end>` ou `..._<end>_C00000000`); `null` se não achar |
| `BluetoothBattery.Select(IList<BtNode>)` | Puro: junta os nós com bateria ao nó `DEV_` do mesmo endereço, mantém só os conectados, um por endereço (vale o primeiro nó encontrado), ordenados por nome, porcentagem limitada a 0–100 |
| `BluetoothBattery.ReadConnected()` | Lê os nós presentes do enumerador `BTHENUM` pela SetupAPI e devolve `Select(...)`. Nunca lança: em falha devolve lista vazia |

Propriedades lidas, todas pela SetupAPI (`SetupDiGetDevicePropertyW`):

| Propriedade | Chave | Tipo | Onde |
|---|---|---|---|
| Bateria (`DEVPKEY_Bluetooth_Battery`) | `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` | byte | nó "Hands-Free AG" |
| Conectado (`DEVPKEY_Device_IsConnected`) | `{83DA6326-97A6-4088-9453-A1923F573B29} 15` | booleano | nó `DEV_<endereço>` |
| Nome (`DEVPKEY_Device_FriendlyName`) | `{A45C254E-DF1C-4EFD-8020-67D146A850E0} 14` | texto | nó `DEV_<endereço>` |

### Mudanças nos arquivos existentes

- **`TrayHost.cs`:**
  - `BatteryView.ForHeadset(BtBattery)` (puro) gera texto, tom e linha do tooltip.
  - `TrayPrefs` guarda a preferência no registro: `HKCU\Software\SM809Local`, valor `ShowHeadsets`
    (DWORD; sem valor = ligado). O `config.json` não serve porque é gravado pela janela a partir da
    cópia dela, e uma escrita feita pela bandeja seria sobrescrita.
  - `TrayHost` lê os fones no máximo a cada 10 s e monta os itens do mouse e dos fones. O tooltip
    junta as linhas. Cada fone tem seu `AlertLatch`, guardado por endereço. O menu ganha o item da
    preferência.
- **`TaskbarBattery.cs`:** `HeadsetGlyph`; tooltip de várias linhas (`TTM_SETMAXTIPWIDTH`).
- **`build.cmd`, `README.md`:** novo arquivo; seção da bateria na barra.

## 6. Validação

1. **Testes puros:**
   - endereço extraído dos três formatos de id e de um inválido;
   - seleção (conectado, desconectado, sem nó `DEV_`, duplicado, ordem, limites);
   - texto e tons do fone;
   - preferência no registro, numa chave de teste;
   - a tira com dois itens é mais larga que com um.
2. **`--btcheck`:** leitura real do que o Windows informa agora e o tempo dela (abaixo de 100 ms).
3. **`--hostcheck` ampliado:** com um fone conectado, a tira tem o item dele. Desligar a preferência
   tira o item, e religar o devolve.
4. **`--stripshots`:** captura da tira com mouse e fone.
5. **Produção:** depois de publicar, o app residente v1.3 mostra mouse e fone na barra real.

## 7. Fora do escopo

Wind V2 pelo dongle (sem canal de bateria); fones só Bluetooth LE (LE Audio); outros dispositivos
Bluetooth LE, como teclados.
