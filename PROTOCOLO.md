# Protocolo HID do SOLAKAKA SM809 PRO (V1000_SM809)

Extraído do driver web oficial `https://driver.yuandaxin-tech.com/HYX/V1000_SM809_3311/`
(arquivos `index-CQl4Vnyy.js` e `DeviceView-C4JAQhLH.js`, versão servida via HTTPS em 2026-08-17).

## Identificação do dispositivo

- **VID**: `0x04F3` (1267)
- **PID**: `0x026E` (622) = USB com fio · `0x026F` (623) = dongle 2.4G · outro = Bluetooth
- Interface de configuração: a coleção HID de fornecedor (no Windows, `MI_01&COL04`/`COL05`),
  identificável por `FeatureReportByteLength >= 32`.
- O driver web escolhe o dispositivo WebHID com `collections.length > 2`.

## Formato dos pacotes (feature reports)

Todos os comandos são enviados via **feature report ID 6**:

```
[cmd, seq, subcmd, CHECKSUM, ...dados, ...padding]   (31 bytes de dados; macro: 32)
```

- `seq`: contador 0–255, incrementa a cada comando (`(seq+1) % 256`).
- Monta-se `[cmd, seq, subcmd, ...dados+padding]` com 30 bytes (31 p/ pacote de macro),
  então o **checksum** é **inserido no índice 3**: `chk = (6 + soma_de_todos_os_bytes) & 0xFF`.
  (O `6` é o report ID.)
- **ACK**: ler feature report ID 6; sucesso se `byte[1] == 1` (índice 1 = 1º byte após o report ID).
  O oficial espera ~60 ms antes de ler o ACK e tenta 3x (pausa 100+50·tentativa ms antes de enviar).
- **Status**: ler feature report ID 5 faz o mouse emitir um **input report ID 4**:
  `[linkStatus, nívelDPIAtual, taxaAtual, bateria(0-100), statusBateria, modoLED, ?, taxaMáx, tipoSensor]`
  Semântica (confirmada no driver oficial): `linkStatus == 0` → mouse offline (dormindo
  ou desligado); `statusBateria == 1` → carregando (o oficial **não** mostra a
  porcentagem, porque o valor não é confiável na carga); `statusBateria == 2` → carga
  completa (100%); outro valor → vale `bateria`. As leituras observadas vêm em passos de 10%.

## Comandos (cmd / subcmd / dados / padding)

| cmd | função | subcmd | dados | pad |
|-----|--------|--------|-------|-----|
| 0x01 | Remapear botões (16 botões → 2 pacotes) | 2 (pacote 1) e 18 (pacote 2) | 9×3 e 7×3 bytes de códigos | 0xFF |
| 0x02 | Níveis de DPI | 2 = eixo X, 18 = eixo Y | `[nívelAtual, máscaraNíveis, 8×(lo,hi)]`, valor = `dpi/100 - 1` (sensores 149/80: `dpi/50 - 1`) | 0xFF |
| 0x03 | Cores dos níveis de DPI | 1 | 8×(R,G,B) | 0xFF |
| 0x04 | Taxa de retorno | 1 | `[taxa]`: 8=125Hz, 4=250, 2=500, 1=1000, 17=2000, 18=4000, 20=8000 | 0x00 |
| 0x05 | Lift-off distance | 1 | `[1|2]` (mm) | 0xFF |
| 0x06 | Iluminação | 1 | `[0xFF, modo, brilho<<4\|vel, direção, (R,G,B se aplicável)]` — SM809: brilho/vel/cor não suportados → `[0xFF, modo, 0, 0]` | 0xFF |
| 0x07 | Energia | 1 | `[sleepMin(1-20), acordarAoMover(0/1), ledAoMover(0/1)]` | 0xFF |
| 0x08 | Sensor avançado | 1 | `[angleSnap, motionSync, rippleControl]` (0/1) | 0xFF |
| 0x0F | Reset de fábrica | 1 | `[0xFF]` | 0xFF |
| 0x11 | Apagar área de macros | 1 | `[0xFF]` | 0x00 |
| 0x10 | Dados de macro | `(pkt<<4)\|totalPkts` | `[1+len, botão(16→12), ...até 25 bytes]` (pad até 31, depois checksum) | 0x00 |

### Remapeamento (cmd 0x01) — ordem dos botões nos pacotes

Array de botões ordenado por id 1–16 (índices 0–15). Pacote 1 (subcmd 2) leva os índices
`[11,12,13,14,15,9,10,0,1]` (= ids 12,13,14,15,16,10,11,1,2); pacote 2 (subcmd 18) leva
`[2,3,4,5,8,6,7]` (= ids 3,4,5,6,9,7,8). Botão sem função: `FF FF FF`.

## Códigos de função (3 bytes por botão)

| tipo | código | significado |
|------|--------|-------------|
| Mouse | `[0x10, máscara, 0]` | 1=esq, 2=dir, 4=meio, 8=voltar, 16=avançar |
| Tecla de fogo | `[0x30, intervalo_ms, contagem]` | contagem 0 = enquanto segurar; 3 = tiro triplo |
| DPI | `[0x40, n, 0]` | 1=ciclar, 2=DPI+, 3=DPI− |
| Trava de DPI | `[0x50, n, 0]` | n=1..11 → trava em 200..1200 DPI |
| Especial | `[0x60, 0, 0]` desativar · `[0x60, 4, 0]` liga/desliga LED |
| Teclado | `[0x70, modificadores, usoHID]` | mods: 1=Ctrl, 2=Shift, 4=Alt, 8=Win; usoHID = HID Usage Table (teclado) |
| Consumer | `[0x80, usoLo, usoHi]` | usage page Consumer (0x0C), ex.: PlayPause=0xCD → `[0x80,205,0]` |
| Macro | `[0x90, modo<<4\|botão, repetições]` | modo: 1=1x, 2=repetir N, 4=até soltar; botão 16 usa 12 |

### Atalhos padrão do driver oficial

Copiar `[112,1,6]`, Colar `[112,1,25]`, Recortar `[112,1,27]`, Selecionar tudo `[112,1,4]`,
Novo `[112,1,17]`, Salvar `[112,1,22]`, Imprimir `[112,1,19]`, Abrir (Win+E) `[112,8,8]`,
Desfazer `[112,1,29]`.

Multimídia: Player `[128,131,1]`, Anterior `[128,182,0]`, Próxima `[128,181,0]`,
Parar `[128,183,0]`, Play/Pause `[128,205,0]`, Mudo `[128,226,0]`, Vol+ `[128,233,0]`,
Vol− `[128,234,0]`, E-mail `[128,138,1]`, Calculadora `[128,146,1]`, Meu computador `[128,148,1]`.

## Padrões de fábrica (template V1000_SM809)

| id | botão físico | função padrão | código |
|----|--------------|---------------|--------|
| 1 | Lateral 1 | Win+D | `[112,8,7]` |
| 2 | Lateral 2 | "2" | `[112,0,31]` |
| 3 | Lateral 3 | "1" | `[112,0,30]` |
| 4 | Lateral 4 | Avançar | `[16,16,0]` |
| 5 | Lateral 5 | "4" | `[112,0,33]` |
| 6 | Lateral 6 | "3" | `[112,0,32]` |
| 7 | Lateral 7 | "6" | `[112,0,35]` |
| 8 | Lateral 8 | "5" | `[112,0,34]` |
| 9 | Lateral 9 | Voltar | `[16,8,0]` |
| 10 | Borda esq. (frente) | Tecla de fogo | `[48,16,2]` |
| 11 | Borda esq. (trás) | Meu computador | `[128,148,1]` |
| 12 | Clique esquerdo | Esquerdo (travado) | `[16,1,0]` |
| 13 | Clique direito | Direito | `[16,2,0]` |
| 14 | Roda (meio) | Meio | `[16,4,0]` |
| 15 | DPI + | DPI+ | `[64,2,0]` |
| 16 | DPI − | DPI− | `[64,3,0]` |

DPI padrão: 6 níveis, atual = nível 2 (1600), cores `#ff0000 #0000ff #00ff00 #ffff00 #00ffff #ff00ff`.
Desempenho: taxa 500Hz (valor 2), LOD 2mm, sleep 20 min, acordar ao mover ✔, LED ao mover ✔.
Iluminação: modo 2 (fluxo colorido). Esquema de LED nº 3 (modos 1–10 + 0=desligado, sem ajuste
de brilho/velocidade/cor).

## Macros (cmd 0x11 + 0x10)

1. Apagar área: `[17, seq, 1, 0xFF]` pad 0 → checksum → enviar.
2. Eventos codificados em 3 bytes: `[flags, delayLo, código]`
   - `flags`: nibble alto = tipo (0x30 tecla normal, 0x20 modificador, 0x10 botão do mouse,
     0x00 delay puro, 0x40/0x50 cursor/roda) + bit 0x80 = soltar; nibble baixo = delay >> 8 (máx 4095 ms).
   - Modificadores/botões: código vira bitmask `1<<(hid&7)`.
   - O delay armazenado em cada evento é o intervalo até o **próximo** evento.
3. Dados divididos em pacotes de 25 bytes: `[16, seq, (i<<4)|total, 1+len, botão, ...dados]`
   pad 0 até 31 → checksum → 32 bytes. Pausa ~120 ms entre pacotes.
   Máximo de 15 pacotes (nibble de 4 bits) = 375 bytes = 125 eventos por macro.

Nota: nos eventos de macro os botões do mouse usam bitmask `1<<(hid&7)` —
hid 243→bit 8 e 244→bit 16. Pela tabela de remapeamento (8=voltar, 16=avançar),
243=voltar e 244=avançar. O driver oficial rotula 243 como "avançar" (前进) —
aparentemente um bug do próprio driver; este projeto segue a semântica da
tabela de remapeamento.
