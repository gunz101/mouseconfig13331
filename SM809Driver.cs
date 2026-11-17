// ============================================================================
//  SM809 PRO — Driver Local (SOLAKAKA SM809 PRO / V1000_SM809)
//  Reimplementação local do driver web oficial (driver.yuandaxin-tech.com).
//  Grava a configuração DIRETAMENTE na memória do mouse via HID feature
//  reports — o mesmo protocolo do site oficial. Ver PROTOCOLO.md.
//  Compilar: csc /codepage:65001 /target:winexe /r:System.Web.Extensions.dll SM809Driver.cs
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace SM809
{
    // ========================================================================
    //  P/Invoke HID / SetupAPI
    // ========================================================================
    internal static class NativeHid
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        public const int DIGCF_PRESENT = 0x02;
        public const int DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const int FILE_SHARE_READ = 1;
        public const int FILE_SHARE_WRITE = 2;
        public const int OPEN_EXISTING = 3;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attr);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);

        [DllImport("hid.dll")]
        public static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);

        [DllImport("hid.dll")]
        public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] data, int len);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] data, int len);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int len);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll")]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA ifData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, int detailSize, ref int required, IntPtr devInfoData);

        [DllImport("setupapi.dll")]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string path, uint access, int share, IntPtr sec, int disposition, int flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle h, byte[] buf, int count, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
    }

    internal class HidDeviceEntry
    {
        public string Path;
        public ushort Vid;
        public ushort Pid;
        public ushort UsagePage;
        public ushort Usage;
        public int FeatureLen;
        public int InputLen;
    }

    // ========================================================================
    //  Dispositivo SM809 — enumeração, conexão, envio de comandos
    // ========================================================================
    internal class DeviceStatus
    {
        public int LinkStatus;
        public int CurrentLevel;
        public int CurrentReportRate;
        public int BatteryLevel;
        public int BatteryStatus;
        public int LightEffectMode;
        public int MaxReportRate;
        public int SensorType;
        public bool Valid;
    }

    internal class Sm809Device : IDisposable
    {
        public const ushort VID = 0x04F3;          // 1267
        public const ushort PID_USB = 0x026E;      // 622
        public const ushort PID_24G = 0x026F;      // 623

        private SafeFileHandle _handle;
        private SafeFileHandle _inputHandle;
        private int _featureLen;
        private int _inputLen;
        private byte _seq;
        private Thread _reader;
        private volatile bool _stop;
        private volatile bool _dead;
        private readonly object _ioLock = new object();

        public ushort Pid;
        public DeviceStatus Status = new DeviceStatus();
        public event Action StatusChanged;
        public event Action ConnectionLost;

        public bool IsConnected
        {
            get { return _handle != null && !_handle.IsInvalid && !_handle.IsClosed && !_dead; }
        }

        // Um cabo/dongle removido não invalida o handle do Win32 — as chamadas
        // apenas passam a falhar. Marca explicitamente para o timer reconectar.
        private void MarkDead()
        {
            if (_dead) return;
            _dead = true;
            Status.Valid = false;
            var ev = ConnectionLost;
            if (ev != null) { try { ev(); } catch { } }
        }

        public string ConnectionMode
        {
            get
            {
                if (Pid == PID_24G) return "2.4G";
                if (Pid == PID_USB) return "USB";
                return "BT";
            }
        }

        public static List<HidDeviceEntry> Enumerate()
        {
            var result = new List<HidDeviceEntry>();
            Guid guid;
            NativeHid.HidD_GetHidGuid(out guid);
            IntPtr devInfo = NativeHid.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
                NativeHid.DIGCF_PRESENT | NativeHid.DIGCF_DEVICEINTERFACE);
            if (devInfo == IntPtr.Zero || devInfo.ToInt64() == -1) return result;
            try
            {
                for (int i = 0; ; i++)
                {
                    var ifData = new NativeHid.SP_DEVICE_INTERFACE_DATA();
                    ifData.cbSize = Marshal.SizeOf(typeof(NativeHid.SP_DEVICE_INTERFACE_DATA));
                    if (!NativeHid.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, i, ref ifData))
                        break;
                    int required = 0;
                    NativeHid.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, ref required, IntPtr.Zero);
                    if (required <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!NativeHid.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, detail, required, ref required, IntPtr.Zero))
                            continue;
                        string path = Marshal.PtrToStringAuto(new IntPtr(detail.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        using (var probe = NativeHid.CreateFile(path, 0,
                            NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
                            IntPtr.Zero, NativeHid.OPEN_EXISTING, 0, IntPtr.Zero))
                        {
                            if (probe.IsInvalid) continue;
                            var attr = new NativeHid.HIDD_ATTRIBUTES();
                            attr.Size = Marshal.SizeOf(typeof(NativeHid.HIDD_ATTRIBUTES));
                            if (!NativeHid.HidD_GetAttributes(probe, ref attr)) continue;

                            var entry = new HidDeviceEntry();
                            entry.Path = path;
                            entry.Vid = attr.VendorID;
                            entry.Pid = attr.ProductID;

                            IntPtr prep;
                            if (NativeHid.HidD_GetPreparsedData(probe, out prep))
                            {
                                var caps = new NativeHid.HIDP_CAPS();
                                if (NativeHid.HidP_GetCaps(prep, ref caps) == NativeHid.HIDP_STATUS_SUCCESS)
                                {
                                    entry.UsagePage = caps.UsagePage;
                                    entry.Usage = caps.Usage;
                                    entry.FeatureLen = caps.FeatureReportByteLength;
                                    entry.InputLen = caps.InputReportByteLength;
                                }
                                NativeHid.HidD_FreePreparsedData(prep);
                            }
                            result.Add(entry);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                NativeHid.SetupDiDestroyDeviceInfoList(devInfo);
            }
            return result;
        }

        // Conecta na coleção de fornecedor do SM809 (feature report >= 32 bytes,
        // equivalente ao "collections.length > 2" do driver web).
        public bool Connect()
        {
            Disconnect();
            _dead = false;
            // Assinatura verificada no hardware: coleção UsagePage 0x000A com
            // feature report grande. PIDs conhecidos têm prioridade (VID 0x04F3
            // é da ELAN — touchpads de notebook usam o mesmo VID).
            var candidates = Enumerate()
                .Where(d => d.Vid == VID && d.FeatureLen >= 32)
                .OrderByDescending(d =>
                    ((d.Pid == PID_USB || d.Pid == PID_24G) ? 4 : 0) +
                    (d.UsagePage == 0x000A ? 2 : 0) +
                    (d.InputLen == 0 ? 1 : 0))
                .ToList();
            foreach (var cand in candidates)
            {
                var h = NativeHid.CreateFile(cand.Path,
                    NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
                    NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
                    IntPtr.Zero, NativeHid.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;

                // valida: consegue ler o feature report de status (id 5)?
                var buf = new byte[cand.FeatureLen];
                buf[0] = 5;
                if (!NativeHid.HidD_GetFeature(h, buf, buf.Length))
                {
                    h.Dispose();
                    continue;
                }
                _handle = h;
                _featureLen = cand.FeatureLen;
                Pid = cand.Pid;
                _stop = false;

                // Os relatórios de status (input report 4) chegam por OUTRA coleção
                // (ex.: COL04) — mesma usage page, com input e sem feature.
                var inputCand = Enumerate().FirstOrDefault(d =>
                    d.Vid == VID && d.Pid == cand.Pid && d.Path != cand.Path &&
                    d.UsagePage == cand.UsagePage && d.InputLen >= 10);
                if (inputCand != null)
                {
                    var hi = NativeHid.CreateFile(inputCand.Path, NativeHid.GENERIC_READ,
                        NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
                        IntPtr.Zero, NativeHid.OPEN_EXISTING, 0, IntPtr.Zero);
                    if (!hi.IsInvalid)
                    {
                        _inputHandle = hi;
                        _inputLen = inputCand.InputLen;
                        _reader = new Thread(ReaderLoop);
                        _reader.IsBackground = true;
                        _reader.Start();
                    }
                }
                TriggerStatus();
                return true;
            }
            return false;
        }

        public void Disconnect()
        {
            _stop = true;
            if (_inputHandle != null)
            {
                // desbloqueia o ReadFile síncrono do leitor
                try { NativeHid.CancelIoEx(_inputHandle, IntPtr.Zero); } catch { }
            }
            if (_handle != null)
            {
                try { _handle.Dispose(); } catch { }
                _handle = null;
            }
            if (_inputHandle != null)
            {
                try { _inputHandle.Dispose(); } catch { }
                _inputHandle = null;
            }
            if (_reader != null)
            {
                try { _reader.Join(200); } catch { }
                _reader = null;
            }
            Status.Valid = false;
        }

        public void Dispose()
        {
            Disconnect();
        }

        private void ReaderLoop()
        {
            var h = _inputHandle;
            var buf = new byte[_inputLen];
            while (!_stop)
            {
                int read;
                bool ok;
                try { ok = NativeHid.ReadFile(h, buf, buf.Length, out read, IntPtr.Zero); }
                catch { break; }
                if (!ok || read <= 0) break;
                if (_stop) break;
                if (buf[0] == 4 && read >= 10)
                {
                    // [id=4, linkStatus, nível, taxa, bateria, statusBat, modoLED, ?, taxaMáx, sensor]
                    Status.LinkStatus = buf[1];
                    Status.CurrentLevel = buf[2];
                    Status.CurrentReportRate = buf[3];
                    Status.BatteryLevel = buf[4];
                    Status.BatteryStatus = buf[5];
                    Status.LightEffectMode = buf[6];
                    Status.MaxReportRate = buf[8];
                    Status.SensorType = buf[9];
                    Status.Valid = true;
                    var ev = StatusChanged;
                    if (ev != null) { try { ev(); } catch { } }
                }
            }
            if (!_stop) MarkDead();   // dongle/cabo removido
        }

        private byte NextSeq()
        {
            _seq = (byte)((_seq + 1) % 256);
            return _seq;
        }

        // Monta [cmd, seq, sub, ...dados] com padding, insere checksum no índice 3.
        public byte[] BuildPacket(byte cmd, byte sub, byte[] data, byte pad, int padTo)
        {
            var p = new List<byte>();
            p.Add(cmd);
            p.Add(NextSeq());
            p.Add(sub);
            if (data != null) p.AddRange(data);
            while (p.Count < padTo) p.Add(pad);
            int sum = 6;
            for (int i = 0; i < p.Count; i++) sum += p[i];
            p.Insert(3, (byte)(sum & 0xFF));
            return p.ToArray();
        }

        // Envia via feature report 6, aguarda e confere o ACK (byte[1] == 1).
        public bool SendCommand(byte[] packet, int retries)
        {
            if (!IsConnected) return false;
            lock (_ioLock)
            {
                for (int attempt = 0; attempt < retries; attempt++)
                {
                    Thread.Sleep(100 + attempt * 50);
                    var buf = new byte[_featureLen];
                    buf[0] = 6;
                    Array.Copy(packet, 0, buf, 1, Math.Min(packet.Length, _featureLen - 1));
                    bool sent;
                    try { sent = NativeHid.HidD_SetFeature(_handle, buf, buf.Length); }
                    catch { sent = false; }
                    if (!sent) continue;

                    Thread.Sleep(60);
                    var ack = new byte[_featureLen];
                    ack[0] = 6;
                    bool got;
                    try { got = NativeHid.HidD_GetFeature(_handle, ack, ack.Length); }
                    catch { got = false; }
                    if (got && ack[1] == 1) return true;
                }
                return false;
            }
        }

        public bool SendSimple(byte cmd, byte sub, byte[] data, byte pad)
        {
            return SendCommand(BuildPacket(cmd, sub, data, pad, 30), 3);
        }

        // Pede o status: ler feature 5 faz o mouse emitir o input report 4.
        public void TriggerStatus()
        {
            if (!IsConnected) return;
            lock (_ioLock)
            {
                var buf = new byte[_featureLen];
                buf[0] = 5;
                bool ok;
                try { ok = NativeHid.HidD_GetFeature(_handle, buf, buf.Length); } catch { ok = false; }
                if (!ok) MarkDead();
            }
        }

        // ---------------- Comandos de alto nível (protocolo oficial) --------

        // cmd 0x01 — remapeia os 16 botões (2 pacotes, ordem oficial).
        public bool ApplyButtons(byte[][] codes16)
        {
            int[] order = { 11, 12, 13, 14, 15, 9, 10, 0, 1, 2, 3, 4, 5, 8, 6, 7 };
            var d1 = new List<byte>();
            for (int i = 0; i < 9; i++) AppendCode(d1, codes16[order[i]]);
            if (!SendCommand(BuildPacket(1, 2, d1.ToArray(), 0xFF, 30), 3)) return false;
            Thread.Sleep(60);
            var d2 = new List<byte>();
            for (int i = 9; i < 16; i++) AppendCode(d2, codes16[order[i]]);
            return SendCommand(BuildPacket(1, 18, d2.ToArray(), 0xFF, 30), 3);
        }

        private static void AppendCode(List<byte> dst, byte[] code)
        {
            if (code != null && code.Length >= 3) { dst.Add(code[0]); dst.Add(code[1]); dst.Add(code[2]); }
            else { dst.Add(0xFF); dst.Add(0xFF); dst.Add(0xFF); }
        }

        // cmd 0x02 (X e Y) + 0x03 (cores) — níveis de DPI.
        public bool ApplyDpi(int[] levels, string[] colorsHex, int count, int currentLevel, int sensorType)
        {
            int step = (sensorType == 149 || sensorType == 80) ? 50 : 100;
            byte mask = 0;
            for (int i = 0; i < count && i < 8; i++) mask |= (byte)(1 << i);

            var data = new List<byte>();
            data.Add((byte)currentLevel);
            data.Add(mask);
            for (int i = 0; i < 8; i++)
            {
                if (i < count)
                {
                    int v = levels[i] / step - 1;
                    if (v < 0) v = 0;
                    data.Add((byte)(v & 0xFF));
                    data.Add((byte)((v >> 8) & 0xFF));
                }
                else { data.Add(0xFF); data.Add(0xFF); }
            }
            if (!SendCommand(BuildPacket(2, 2, data.ToArray(), 0xFF, 30), 3)) return false;
            Thread.Sleep(200);
            // eixo Y = mesmos valores (SM809 não tem X/Y independente)
            var dataY = new List<byte>(data);
            if (!SendCommand(BuildPacket(2, 18, dataY.ToArray(), 0xFF, 30), 3)) return false;
            Thread.Sleep(100);

            var cd = new List<byte>();
            for (int i = 0; i < 8; i++)
            {
                string hex = (colorsHex != null && i < colorsHex.Length && !string.IsNullOrEmpty(colorsHex[i]))
                    ? colorsHex[i] : "#ea5e56";
                if (!hex.StartsWith("#") || hex.Length < 7) hex = "#ea5e56";
                byte r, g, b;
                try
                {
                    r = Convert.ToByte(hex.Substring(1, 2), 16);
                    g = Convert.ToByte(hex.Substring(3, 2), 16);
                    b = Convert.ToByte(hex.Substring(5, 2), 16);
                }
                catch { r = 0xEA; g = 0x5E; b = 0x56; }
                cd.Add(r); cd.Add(g); cd.Add(b);
            }
            return SendCommand(BuildPacket(3, 1, cd.ToArray(), 0xFF, 30), 3);
        }

        public bool ApplyReportRate(int rateValue)
        {
            return SendCommand(BuildPacket(4, 1, new byte[] { (byte)rateValue }, 0x00, 30), 3);
        }

        public bool ApplyLod(int mm)
        {
            return SendSimple(5, 1, new byte[] { (byte)mm }, 0xFF);
        }

        public bool ApplyLighting(int mode)
        {
            // SM809 (esquema 3): sem brilho/velocidade/cor → [0xFF, modo, 0, 0]
            return SendSimple(6, 1, new byte[] { 0xFF, (byte)mode, 0, 0 }, 0xFF);
        }

        public bool ApplyPower(int sleepMin, bool moveWakeup, bool moveLighting)
        {
            return SendSimple(7, 1, new byte[] { (byte)sleepMin, (byte)(moveWakeup ? 1 : 0), (byte)(moveLighting ? 1 : 0) }, 0xFF);
        }

        public bool ApplySensorAdvanced(bool angleSnap, bool motionSync, bool ripple)
        {
            return SendSimple(8, 1, new byte[] { (byte)(angleSnap ? 1 : 0), (byte)(motionSync ? 1 : 0), (byte)(ripple ? 1 : 0) }, 0xFF);
        }

        public bool FactoryReset()
        {
            return SendSimple(15, 1, new byte[] { 0xFF }, 0xFF);
        }

        // cmd 0x11 (apagar) + 0x10 (dados) — envia todas as macros atribuídas.
        public bool SendMacros(List<KeyValuePair<int, MacroDef>> assignments)
        {
            if (assignments == null || assignments.Count == 0) return true;
            if (!SendCommand(BuildPacket(17, 1, new byte[] { 0xFF }, 0x00, 30), 3)) return false;
            Thread.Sleep(100);

            foreach (var kv in assignments)
            {
                int btnId = kv.Key;
                var macro = kv.Value;
                if (macro == null || macro.Events == null || macro.Events.Count == 0) continue;

                var payload = Proto.EncodeMacroEvents(macro.Events);
                // o cabeçalho de pacote só suporta 15 pacotes (nibble) = 375 bytes
                if (payload.Count > 375) payload = payload.GetRange(0, 375);
                int total = payload.Count;
                int pkts = (total + 24) / 25;
                if (pkts == 0) pkts = 1;
                int remaining = total;
                for (int p = 0; p < pkts; p++)
                {
                    int len = remaining <= 25 ? remaining : 25;
                    remaining -= len;
                    byte btn = (byte)(btnId == 16 ? 12 : btnId);
                    var d = new List<byte>();
                    d.Add((byte)(1 + len));
                    d.Add(btn);
                    for (int i = 0; i < len; i++)
                    {
                        int src = p * 25 + i;
                        d.Add(src < payload.Count ? payload[src] : (byte)0);
                    }
                    byte sub = (byte)(((p << 4) & 0xF0) | (pkts & 0x0F));
                    if (!SendCommand(BuildPacket(16, sub, d.ToArray(), 0x00, 31), 3)) return false;
                    Thread.Sleep(120);
                }
            }
            return true;
        }
    }

    // ========================================================================
    //  Protocolo — codificação de macros
    // ========================================================================
    internal class MacroEvent
    {
        public string Type;   // "keydown" | "keyup" | "delay"
        public int Hid;       // uso HID (teclado 4..231, mouse 240..244)
        public int Delay;     // ms desde o evento ANTERIOR
        public string Name;   // exibição
    }

    internal class MacroDef
    {
        public string Id;
        public string Name;
        public List<MacroEvent> Events = new List<MacroEvent>();
        public int ExecType = 1;     // 1 = uma vez, 2 = repetir N, 4 = até soltar
        public int RepeatCount = 1;
    }

    internal static class Proto
    {
        // Codifica eventos em trincas [flags, delayLo, código] (formato oficial).
        // MacroEvent.Delay = ms desde o evento ANTERIOR; o formato do mouse guarda
        // em cada tripla o intervalo até o PRÓXIMO evento. Pausas inseridas
        // ("delay") são agregadas ao intervalo — nunca contadas em dobro.
        public static List<byte> EncodeMacroEvents(List<MacroEvent> events)
        {
            var outp = new List<byte>();
            var keys = new List<MacroEvent>();
            var gapAfter = new List<int>();
            int leading = 0;
            foreach (var ev in events)
            {
                int d = ev.Delay;
                if (d < 0) d = 0;
                if (ev.Type == "delay")
                {
                    if (keys.Count == 0) leading += d;
                    else gapAfter[gapAfter.Count - 1] += d;
                }
                else
                {
                    if (keys.Count > 0) gapAfter[gapAfter.Count - 1] += d;
                    keys.Add(ev);
                    gapAfter.Add(0);
                }
            }
            EmitDelay(outp, leading);
            for (int i = 0; i < keys.Count; i++)
            {
                int toNext = i < keys.Count - 1 ? gapAfter[i] : 0;
                int inline = toNext > 4095 ? 4095 : toNext;
                EmitKey(outp, keys[i], inline);
                EmitDelay(outp, toNext - inline);   // excedente vira pausas puras
            }
            return outp;
        }

        private static void EmitDelay(List<byte> outp, int total)
        {
            while (total > 0)
            {
                int c = total > 4095 ? 4095 : total;
                outp.Add((byte)((c >> 8) & 0x0F));
                outp.Add((byte)(c & 0xFF));
                outp.Add(0);
                total -= c;
            }
        }

        private static void EmitKey(List<byte> outp, MacroEvent ev, int delayNext)
        {
            bool down = ev.Type == "keydown";
            int k = ev.Hid;
            int b1 = (delayNext >> 8) & 0x0F;
            int typeBits;
            int code = k;
            int[] bits = { 1, 2, 4, 8, 16, 32, 64, 128 };
            if (k < 145) typeBits = 0x30;               // tecla comum
            else if (k < 232) { typeBits = 0x20; code = bits[k & 7]; }   // modificador
            else if (k >= 240 && k <= 244) { typeBits = 0x10; code = bits[k & 7]; } // botão do mouse
            else if (k == 245) typeBits = 0x40;
            else if (k == 246) typeBits = 0x50;
            else if (k == 247) typeBits = 0x60;
            else typeBits = 0x30;
            if (!down) b1 |= 0x80;
            b1 |= typeBits;
            outp.Add((byte)b1);
            outp.Add((byte)(delayNext & 0xFF));
            outp.Add((byte)code);
        }
    }

    // ========================================================================
    //  Tabelas de teclas (HID Usage) e catálogo de funções
    // ========================================================================
    internal class ActionDef
    {
        public string Name;
        public byte[] Code;
        public ActionDef(string name, byte a, byte b, byte c)
        {
            Name = name;
            Code = new byte[] { a, b, c };
        }
    }

    internal static class KeyTables
    {
        // .NET Keys -> HID usage (teclado)
        public static int KeysToHid(Keys k)
        {
            if (k >= Keys.A && k <= Keys.Z) return 4 + (k - Keys.A);
            if (k >= Keys.D1 && k <= Keys.D9) return 30 + (k - Keys.D1);
            if (k == Keys.D0) return 39;
            if (k >= Keys.F1 && k <= Keys.F12) return 58 + (k - Keys.F1);
            if (k >= Keys.NumPad1 && k <= Keys.NumPad9) return 89 + (k - Keys.NumPad1);
            switch (k)
            {
                case Keys.Enter: return 40;
                case Keys.Escape: return 41;
                case Keys.Back: return 42;
                case Keys.Tab: return 43;
                case Keys.Space: return 44;
                case Keys.OemMinus: return 45;
                case Keys.Oemplus: return 46;
                case Keys.OemOpenBrackets: return 47;
                case Keys.Oem6: return 48;
                case Keys.Oem5: return 49;
                case Keys.Oem1: return 51;
                case Keys.Oem7: return 52;
                case Keys.Oem3: return 53;
                case Keys.Oemcomma: return 54;
                case Keys.OemPeriod: return 55;
                case Keys.OemQuestion: return 56;
                case Keys.CapsLock: return 57;
                case Keys.PrintScreen: return 70;
                case Keys.Scroll: return 71;
                case Keys.Pause: return 72;
                case Keys.Insert: return 73;
                case Keys.Home: return 74;
                case Keys.PageUp: return 75;
                case Keys.Delete: return 76;
                case Keys.End: return 77;
                case Keys.PageDown: return 78;
                case Keys.Right: return 79;
                case Keys.Left: return 80;
                case Keys.Down: return 81;
                case Keys.Up: return 82;
                case Keys.NumLock: return 83;
                case Keys.NumPad0: return 98;
                case Keys.Multiply: return 85;
                case Keys.Add: return 87;
                case Keys.Subtract: return 86;
                case Keys.Decimal: return 99;
                case Keys.Divide: return 84;
                case Keys.Apps: return 101;
                case Keys.ControlKey: case Keys.LControlKey: return 224;
                case Keys.ShiftKey: case Keys.LShiftKey: return 225;
                case Keys.Menu: case Keys.LMenu: return 226;
                case Keys.LWin: case Keys.RWin: return 227;
                case Keys.RControlKey: return 224;
                case Keys.RShiftKey: return 225;
                case Keys.RMenu: return 226;
                default: return 0;
            }
        }

        private static readonly Dictionary<int, string> HidNames = BuildHidNames();

        private static Dictionary<int, string> BuildHidNames()
        {
            var d = new Dictionary<int, string>();
            for (int i = 0; i < 26; i++) d[4 + i] = ((char)('A' + i)).ToString();
            for (int i = 0; i < 9; i++) d[30 + i] = ((char)('1' + i)).ToString();
            d[39] = "0";
            for (int i = 0; i < 12; i++) d[58 + i] = "F" + (i + 1);
            d[40] = "Enter"; d[41] = "Esc"; d[42] = "Backspace"; d[43] = "Tab"; d[44] = "Espaço";
            d[45] = "-"; d[46] = "="; d[47] = "["; d[48] = "]"; d[49] = "\\"; d[51] = ";";
            d[52] = "'"; d[53] = "`"; d[54] = ","; d[55] = "."; d[56] = "/"; d[57] = "CapsLock";
            d[70] = "PrintScreen"; d[71] = "ScrollLock"; d[72] = "Pause"; d[73] = "Insert";
            d[74] = "Home"; d[75] = "PageUp"; d[76] = "Delete"; d[77] = "End"; d[78] = "PageDown";
            d[79] = "→"; d[80] = "←"; d[81] = "↓"; d[82] = "↑"; d[83] = "NumLock";
            d[84] = "Num /"; d[85] = "Num *"; d[86] = "Num -"; d[87] = "Num +"; d[88] = "Num Enter";
            for (int i = 0; i < 9; i++) d[89 + i] = "Num " + (i + 1);
            d[98] = "Num 0"; d[99] = "Num ."; d[101] = "Menu";
            for (int i = 0; i < 12; i++) d[104 + i] = "F" + (13 + i);
            d[224] = "Ctrl"; d[225] = "Shift"; d[226] = "Alt"; d[227] = "Win";
            d[240] = "Clique esq."; d[241] = "Clique dir."; d[242] = "Clique meio";
            // bitmask 1<<(hid&7): 243->8, 244->16; no remap 8=voltar e 16=avançar
            d[243] = "Voltar"; d[244] = "Avançar";
            return d;
        }

        public static string HidName(int hid)
        {
            string s;
            if (HidNames.TryGetValue(hid, out s)) return s;
            return "0x" + hid.ToString("X2");
        }

        // Nome amigável de um código de função (3 bytes)
        public static string CodeName(int[] code, List<MacroDef> macros, string macroId)
        {
            if (code == null || code.Length < 3) return "—";
            int t = code[0];
            if (t == 0xFF) return "Sem função";
            if (t == 16)
            {
                switch (code[1])
                {
                    case 1: return "Clique esquerdo";
                    case 2: return "Clique direito";
                    case 4: return "Clique do meio";
                    case 8: return "Voltar";
                    case 16: return "Avançar";
                }
                return "Mouse";
            }
            if (t == 48)
            {
                if (code[2] == 3) return "Tiro triplo";
                return string.Format("Tecla de fogo ({0}ms × {1})", code[1], code[2] == 0 ? "segurar" : code[2].ToString());
            }
            if (t == 64)
            {
                switch (code[1])
                {
                    case 1: return "Ciclar DPI";
                    case 2: return "DPI +";
                    case 3: return "DPI −";
                }
                return "DPI";
            }
            if (t == 80) return string.Format("Trava DPI {0}", 100 + code[1] * 100);
            if (t == 96)
            {
                if (code[1] == 4) return "Liga/desliga LED";
                return "Desativado";
            }
            if (t == 112)
            {
                var parts = new List<string>();
                if ((code[1] & 1) != 0) parts.Add("Ctrl");
                if ((code[1] & 2) != 0) parts.Add("Shift");
                if ((code[1] & 4) != 0) parts.Add("Alt");
                if ((code[1] & 8) != 0) parts.Add("Win");
                string key = code[2] != 0 ? HidName(code[2]) : "";
                if (parts.Count > 0 && key.Length > 0) return string.Join("+", parts.ToArray()) + "+" + key;
                if (key.Length > 0) return key;
                if (parts.Count > 0) return string.Join("+", parts.ToArray());
                return "Atalho";
            }
            if (t == 128)
            {
                int usage = code[1] | (code[2] << 8);
                switch (usage)
                {
                    case 0x183: return "Player de mídia";
                    case 0xB6: return "Faixa anterior";
                    case 0xB5: return "Próxima faixa";
                    case 0xB7: return "Parar mídia";
                    case 0xCD: return "Play/Pause";
                    case 0xE2: return "Mudo";
                    case 0xE9: return "Volume +";
                    case 0xEA: return "Volume −";
                    case 0x18A: return "E-mail";
                    case 0x192: return "Calculadora";
                    case 0x194: return "Meu computador";
                    case 0x223: return "Página inicial";
                }
                return "Multimídia";
            }
            if (t == 144)
            {
                if (macros != null && macroId != null)
                {
                    var m = macros.FirstOrDefault(x => x.Id == macroId);
                    if (m != null) return "Macro: " + m.Name;
                }
                return "Macro";
            }
            return string.Format("[{0},{1},{2}]", code[0], code[1], code[2]);
        }

        // Catálogo de funções (idêntico ao driver oficial)
        public static List<ActionDef> MouseActions()
        {
            return new List<ActionDef>
            {
                new ActionDef("Clique esquerdo", 16, 1, 0),
                new ActionDef("Clique direito", 16, 2, 0),
                new ActionDef("Clique do meio", 16, 4, 0),
                new ActionDef("Avançar", 16, 16, 0),
                new ActionDef("Voltar", 16, 8, 0),
                new ActionDef("Tiro triplo", 48, 50, 3),
            };
        }

        public static List<ActionDef> EditActions()
        {
            return new List<ActionDef>
            {
                new ActionDef("Copiar (Ctrl+C)", 112, 1, 6),
                new ActionDef("Colar (Ctrl+V)", 112, 1, 25),
                new ActionDef("Recortar (Ctrl+X)", 112, 1, 27),
                new ActionDef("Selecionar tudo", 112, 1, 4),
                new ActionDef("Desfazer (Ctrl+Z)", 112, 1, 29),
                new ActionDef("Novo (Ctrl+N)", 112, 1, 17),
                new ActionDef("Salvar (Ctrl+S)", 112, 1, 22),
                new ActionDef("Imprimir (Ctrl+P)", 112, 1, 19),
                new ActionDef("Explorador (Win+E)", 112, 8, 8),
                new ActionDef("Área de trabalho (Win+D)", 112, 8, 7),
                new ActionDef("Enter", 112, 0, 40),
                new ActionDef("Esc", 112, 0, 41),
            };
        }

        public static List<ActionDef> MediaActions()
        {
            return new List<ActionDef>
            {
                new ActionDef("Play/Pause", 128, 205, 0),
                new ActionDef("Próxima faixa", 128, 181, 0),
                new ActionDef("Faixa anterior", 128, 182, 0),
                new ActionDef("Parar mídia", 128, 183, 0),
                new ActionDef("Volume +", 128, 233, 0),
                new ActionDef("Volume −", 128, 234, 0),
                new ActionDef("Mudo", 128, 226, 0),
                new ActionDef("Player de mídia", 128, 131, 1),
                new ActionDef("E-mail", 128, 138, 1),
                new ActionDef("Calculadora", 128, 146, 1),
                new ActionDef("Meu computador", 128, 148, 1),
            };
        }

        public static List<ActionDef> DpiActions()
        {
            return new List<ActionDef>
            {
                new ActionDef("Ciclar DPI", 64, 1, 0),
                new ActionDef("DPI +", 64, 2, 0),
                new ActionDef("DPI −", 64, 3, 0),
            };
        }

        public static List<ActionDef> DpiLockActions()
        {
            var list = new List<ActionDef>();
            for (int n = 1; n <= 11; n++)
                list.Add(new ActionDef(string.Format("Trava DPI {0}", 100 + n * 100), 80, (byte)n, 0));
            return list;
        }

        public static List<ActionDef> SpecialActions()
        {
            return new List<ActionDef>
            {
                new ActionDef("Desativar botão", 96, 0, 0),
                new ActionDef("Liga/desliga LED", 96, 4, 0),
            };
        }

        public static List<ActionDef> WindowsActions()
        {
            return new List<ActionDef>
            {
                new ActionDef("Alternar janela (Alt+Tab)", 112, 4, 43),
                new ActionDef("Visão de tarefas (Win+Tab)", 112, 8, 43),
                new ActionDef("Fechar janela (Alt+F4)", 112, 4, 61),
                new ActionDef("Captura (Win+Shift+S)", 112, 10, 22),
                new ActionDef("Print Screen", 112, 0, 70),
                new ActionDef("Bloquear PC (Win+L)", 112, 8, 15),
                new ActionDef("Ger. de tarefas (Ctrl+Shift+Esc)", 112, 3, 41),
            };
        }

        public static List<ActionDef> BrowserActions()
        {
            return new List<ActionDef>
            {
                new ActionDef("Nova aba (Ctrl+T)", 112, 1, 23),
                new ActionDef("Fechar aba (Ctrl+W)", 112, 1, 26),
                new ActionDef("Reabrir aba (Ctrl+Shift+T)", 112, 3, 23),
                new ActionDef("Próxima aba (Ctrl+Tab)", 112, 1, 43),
                new ActionDef("Aba anterior (Ctrl+Shift+Tab)", 112, 3, 43),
                new ActionDef("Atualizar (F5)", 112, 0, 62),
                new ActionDef("Localizar (Ctrl+F)", 112, 1, 9),
                new ActionDef("Favoritar (Ctrl+D)", 112, 1, 7),
                new ActionDef("Página inicial", 128, 35, 2),
            };
        }

        public static List<ActionDef> DiscordActions()
        {
            return new List<ActionDef>
            {
                new ActionDef("Mudo microfone (Ctrl+Shift+M)", 112, 3, 16),
                new ActionDef("Deafen — mudo tudo (Ctrl+Shift+D)", 112, 3, 7),
            };
        }

        public static List<ActionDef> ExcelActions()
        {
            return new List<ActionDef>
            {
                new ActionDef("Refazer (Ctrl+Y)", 112, 1, 28),
                new ActionDef("Repetir ação (F4)", 112, 0, 61),
                new ActionDef("Editar célula (F2)", 112, 0, 59),
                new ActionDef("Negrito (Ctrl+B)", 112, 1, 5),
                new ActionDef("Autossoma (Alt+=)", 112, 4, 46),
                new ActionDef("Colar valores (Ctrl+Shift+V)", 112, 3, 25),
                new ActionDef("Início da planilha (Ctrl+Home)", 112, 1, 74),
            };
        }

        // Dígitos da fileira de cima — SEMPRE digitam número, não dependem do
        // NumLock (ao contrário do numpad). Melhor opção para digitar valores.
        public static List<ActionDef> DigitActions()
        {
            var list = new List<ActionDef>();
            for (int n = 1; n <= 9; n++)
                list.Add(new ActionDef(n.ToString(), 112, 0, (byte)(29 + n)));
            list.Add(new ActionDef("0", 112, 0, 39));
            list.Add(new ActionDef("vírgula ,", 112, 0, 54));
            list.Add(new ActionDef("ponto .", 112, 0, 55));
            list.Add(new ActionDef("menos -", 112, 0, 45));
            list.Add(new ActionDef("Enter", 112, 0, 40));
            list.Add(new ActionDef("Tab", 112, 0, 43));
            list.Add(new ActionDef("Backspace", 112, 0, 42));
            return list;
        }

        // Teclado numérico completo — substitui o numpad em teclados tenkeyless.
        // Os códigos são os do numpad de verdade (não os números da fileira),
        // então funcionam em campos numéricos, Excel e Alt+códigos.
        public static List<ActionDef> NumpadActions()
        {
            var list = new List<ActionDef>();
            list.Add(new ActionDef("Num 0", 112, 0, 98));
            for (int n = 1; n <= 9; n++)
                list.Add(new ActionDef("Num " + n, 112, 0, (byte)(88 + n)));
            list.Add(new ActionDef("Num .", 112, 0, 99));
            list.Add(new ActionDef("Num +", 112, 0, 87));
            list.Add(new ActionDef("Num −", 112, 0, 86));
            list.Add(new ActionDef("Num ×", 112, 0, 85));
            list.Add(new ActionDef("Num ÷", 112, 0, 84));
            list.Add(new ActionDef("Num Enter", 112, 0, 88));
            list.Add(new ActionDef("NumLock", 112, 0, 83));
            return list;
        }

        // Função REAL de uma tecla do numpad quando o NumLock está desligado.
        // Retorna null se a tecla não muda de comportamento.
        public static string NumLockOffAlias(int[] code)
        {
            if (code == null || code.Length < 3 || code[0] != 112 || code[1] != 0) return null;
            switch (code[2])
            {
                case 89: return "End";
                case 90: return "seta ↓";
                case 91: return "PageDown";
                case 92: return "seta ←";
                case 93: return "(nada)";
                case 94: return "seta →";
                case 95: return "Home";
                case 96: return "seta ↑";
                case 97: return "PageUp";
                case 98: return "Insert";
                case 99: return "Delete";
                default: return null;
            }
        }

        // F13–F24: teclas que não existem no teclado físico — ideais para
        // push-to-talk do Discord, OBS e atalhos livres sem conflito.
        public static List<ActionDef> FKeyActions()
        {
            var list = new List<ActionDef>();
            for (int i = 0; i < 12; i++)
                list.Add(new ActionDef("F" + (13 + i), 112, 0, (byte)(104 + i)));
            return list;
        }
    }

    // ========================================================================
    //  NumLock — as teclas do numpad só digitam números com ele ligado
    // ========================================================================
    internal static class NumLockHelper
    {
        private const byte VK_NUMLOCK = 0x90;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x01;
        private const uint KEYEVENTF_KEYUP = 0x02;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

        public static bool IsOn
        {
            get { return Control.IsKeyLocked(Keys.NumLock); }
        }

        public static void TurnOn()
        {
            if (!IsOn) Toggle();
        }

        public static void TurnOff()
        {
            if (IsOn) Toggle();
        }

        public static void Toggle()
        {
            keybd_event(VK_NUMLOCK, 0x45, KEYEVENTF_EXTENDEDKEY, IntPtr.Zero);
            keybd_event(VK_NUMLOCK, 0x45, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, IntPtr.Zero);
        }

        // true se o código é uma tecla do numpad (usages 84–99), exceto o próprio
        // NumLock (83) — essas dependem do estado do NumLock para digitar dígitos.
        public static bool IsNumpadCode(int[] code)
        {
            return code != null && code.Length >= 3 && code[0] == 112 && code[1] == 0
                && code[2] >= 84 && code[2] <= 99;
        }
    }

    // ========================================================================
    //  Configuração local (JSON em %APPDATA%\SM809Local)
    // ========================================================================
    public class ButtonCfg
    {
        public int Id;
        public string Label;
        public int[] Code;
        public bool Locked;
        public string MacroId;
    }

    public class MacroEventJson
    {
        public string Type;
        public int Hid;
        public int Delay;
        public string Name;
    }

    public class MacroJson
    {
        public string Id;
        public string Name;
        public int ExecType;
        public int RepeatCount;
        public List<MacroEventJson> Events;
    }

    public class LocalConfig
    {
        public List<ButtonCfg> Buttons;
        public int[] DpiLevels;
        public string[] DpiColors;
        public int DpiCount;
        public int DpiCurrent;
        public int PollRate;
        public int Lod;
        public int SleepTime;
        public bool MoveWakeup;
        public bool MoveLighting;
        public bool AngleSnap;
        public bool MotionSync;
        public bool RippleControl;
        public int LightMode;
        public List<MacroJson> Macros;
        public int WinW;          // tamanho preferido da janela (área útil)
        public int WinH;
        public bool WinMax;

        public static string ConfigDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SM809Local"); }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(ConfigDir, "config.json"); }
        }

        private static readonly string[] PhysicalLabels =
        {
            "Lateral 1", "Lateral 2", "Lateral 3", "Lateral 4", "Lateral 5",
            "Lateral 6", "Lateral 7", "Lateral 8", "Lateral 9",
            "Borda esq. 1", "Borda esq. 2", "Clique esquerdo", "Clique direito",
            "Roda (meio)", "DPI +", "DPI −"
        };

        public static LocalConfig Defaults()
        {
            var c = new LocalConfig();
            c.Buttons = new List<ButtonCfg>();
            int[][] defCodes =
            {
                new[] {112, 8, 7},   // 1  Win+D
                new[] {112, 0, 31},  // 2  "2"
                new[] {112, 0, 30},  // 3  "1"
                new[] {16, 16, 0},   // 4  Avançar
                new[] {112, 0, 33},  // 5  "4"
                new[] {112, 0, 32},  // 6  "3"
                new[] {112, 0, 35},  // 7  "6"
                new[] {112, 0, 34},  // 8  "5"
                new[] {16, 8, 0},    // 9  Voltar
                new[] {48, 16, 2},   // 10 Tecla de fogo
                new[] {128, 148, 1}, // 11 Meu computador
                new[] {16, 1, 0},    // 12 Esquerdo (travado)
                new[] {16, 2, 0},    // 13 Direito
                new[] {16, 4, 0},    // 14 Meio
                new[] {64, 2, 0},    // 15 DPI+
                new[] {64, 3, 0},    // 16 DPI−
            };
            for (int i = 0; i < 16; i++)
            {
                var b = new ButtonCfg();
                b.Id = i + 1;
                b.Label = PhysicalLabels[i];
                b.Code = defCodes[i];
                b.Locked = (i + 1 == 12);
                c.Buttons.Add(b);
            }
            c.DpiLevels = new[] { 400, 800, 1600, 2400, 3200, 6400, 6400, 6400 };
            c.DpiColors = new[] { "#ff0000", "#0000ff", "#00ff00", "#ffff00", "#00ffff", "#ff00ff", "#ff6b6b", "#4ecdc4" };
            c.DpiCount = 6;
            c.DpiCurrent = 2;
            c.PollRate = 2;      // 500 Hz
            c.Lod = 2;
            c.SleepTime = 20;
            c.MoveWakeup = true;
            c.MoveLighting = true;
            c.AngleSnap = false;
            c.MotionSync = true;
            c.RippleControl = false;
            c.LightMode = 2;
            c.Macros = new List<MacroJson>();
            return c;
        }

        public static LocalConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var ser = new JavaScriptSerializer();
                    var cfg = ser.Deserialize<LocalConfig>(File.ReadAllText(ConfigPath, Encoding.UTF8));
                    if (cfg != null && cfg.Buttons != null && cfg.Buttons.Count == 16)
                        return Normalize(cfg);
                }
            }
            catch { }
            return Defaults();
        }

        // Repara campos ausentes/inválidos (arquivo editado à mão, versão antiga)
        // para nunca quebrar a inicialização.
        public static LocalConfig Normalize(LocalConfig cfg)
        {
            var defs = Defaults();
            if (cfg.Macros == null) cfg.Macros = new List<MacroJson>();
            foreach (var m in cfg.Macros)
                if (m.Events == null) m.Events = new List<MacroEventJson>();
            var macroIds = new HashSet<string>();
            foreach (var m in cfg.Macros) if (!string.IsNullOrEmpty(m.Id)) macroIds.Add(m.Id);

            for (int i = 0; i < 16; i++)
            {
                var b = cfg.Buttons[i];
                if (b == null) { cfg.Buttons[i] = defs.Buttons[i]; continue; }
                if (string.IsNullOrEmpty(b.Label)) b.Label = PhysicalLabels[i];
                b.Locked = (i + 1 == 12);
                if (b.Code == null || b.Code.Length < 3) b.Code = defs.Buttons[i].Code;
                if (!string.IsNullOrEmpty(b.MacroId) && !macroIds.Contains(b.MacroId))
                {
                    b.MacroId = null;
                    b.Code = defs.Buttons[i].Code;
                }
            }

            if (cfg.DpiLevels == null || cfg.DpiLevels.Length < 8)
            {
                var lv = new int[8];
                for (int i = 0; i < 8; i++)
                    lv[i] = (cfg.DpiLevels != null && i < cfg.DpiLevels.Length) ? cfg.DpiLevels[i] : defs.DpiLevels[i];
                cfg.DpiLevels = lv;
            }
            for (int i = 0; i < 8; i++)
            {
                if (cfg.DpiLevels[i] < 100) cfg.DpiLevels[i] = 100;
                if (cfg.DpiLevels[i] > 26000) cfg.DpiLevels[i] = 26000;
            }
            if (cfg.DpiColors == null || cfg.DpiColors.Length < 8)
            {
                var cl = new string[8];
                for (int i = 0; i < 8; i++)
                    cl[i] = (cfg.DpiColors != null && i < cfg.DpiColors.Length) ? cfg.DpiColors[i] : defs.DpiColors[i];
                cfg.DpiColors = cl;
            }
            for (int i = 0; i < 8; i++)
                if (!IsHexColor(cfg.DpiColors[i])) cfg.DpiColors[i] = defs.DpiColors[i];

            if (cfg.DpiCount < 1 || cfg.DpiCount > 6) cfg.DpiCount = defs.DpiCount;
            if (cfg.DpiCurrent < 0 || cfg.DpiCurrent >= cfg.DpiCount) cfg.DpiCurrent = 0;
            int[] validRates = { 8, 4, 2, 1, 17, 18, 20 };
            if (Array.IndexOf(validRates, cfg.PollRate) < 0) cfg.PollRate = defs.PollRate;
            if (cfg.Lod != 1 && cfg.Lod != 2) cfg.Lod = defs.Lod;
            if (cfg.SleepTime < 1 || cfg.SleepTime > 20) cfg.SleepTime = defs.SleepTime;
            if (cfg.LightMode < 0 || cfg.LightMode > 10) cfg.LightMode = defs.LightMode;
            return cfg;
        }

        private static bool IsHexColor(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.StartsWith("#") || s.Length < 7) return false;
            for (int i = 1; i < 7; i++)
            {
                char c = char.ToLowerInvariant(s[i]);
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!ok) return false;
            }
            return true;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var ser = new JavaScriptSerializer();
                File.WriteAllText(ConfigPath, ser.Serialize(this), Encoding.UTF8);
            }
            catch { }
        }
    }

    // ========================================================================
    //  Tema escuro
    // ========================================================================
    internal static class Theme
    {
        // Paleta estilo Logitech G HUB: grafite + azul elétrico
        public static readonly Color Back = Color.FromArgb(22, 24, 29);        // #16181D
        public static readonly Color Panel = Color.FromArgb(30, 33, 39);       // #1E2127
        public static readonly Color Panel2 = Color.FromArgb(34, 38, 46);      // #22262E
        public static readonly Color Chip = Color.FromArgb(38, 42, 51);        // #262A33
        public static readonly Color ChipHover = Color.FromArgb(51, 57, 71);   // #333947
        public static readonly Color Border = Color.FromArgb(42, 46, 54);      // #2A2E36
        public static readonly Color Text = Color.FromArgb(223, 227, 234);     // #DFE3EA
        public static readonly Color SubText = Color.FromArgb(139, 147, 161);  // #8B93A1
        public static readonly Color Accent = Color.FromArgb(0, 184, 252);     // #00B8FC
        public static readonly Color AccentHover = Color.FromArgb(51, 198, 253);
        public static readonly Color AccentDark = Color.FromArgb(14, 58, 82);  // #0E3A52 seleção
        public static readonly Color OnAccent = Color.FromArgb(1, 36, 53);     // texto sobre o azul
        public static readonly Color Danger = Color.FromArgb(255, 92, 92);

        public static Button MakeChip(string text, int w, int h)
        {
            var b = new Button();
            b.Text = text;
            b.Size = new Size(w, h);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ChipHover;
            b.BackColor = Chip;
            b.ForeColor = Text;
            b.Font = new Font("Segoe UI", 8.5f);
            int tw = TextRenderer.MeasureText(text, b.Font).Width + 22;
            if (tw > w) b.Width = tw;
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            Round(b, 6);
            return b;
        }

        public static Button MakePrimary(string text, int w, int h)
        {
            var b = MakeChip(text, w, h);
            b.BackColor = Accent;
            b.ForeColor = OnAccent;
            b.FlatAppearance.MouseOverBackColor = AccentHover;
            b.Font = new Font("Segoe UI Semibold", 9f);
            int tw2 = TextRenderer.MeasureText(b.Text, b.Font).Width + 24;
            if (tw2 > b.Width) b.Width = tw2;
            return b;
        }

        // Cantos arredondados via Region (recalculado quando o controle redimensiona)
        public static void Round(Control c, int radius)
        {
            EventHandler apply = delegate
            {
                var r = c.ClientRectangle;
                if (r.Width <= 1 || r.Height <= 1) return;
                int d = radius * 2;
                var path = new GraphicsPath();
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
                path.CloseFigure();
                var old = c.Region;
                c.Region = new Region(path);
                if (old != null) old.Dispose();
            };
            c.Resize += apply;
            apply(c, EventArgs.Empty);
        }

        public static Label MakeSection(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = Text;
            l.Font = new Font("Segoe UI Semibold", 9.5f);
            l.Margin = new Padding(4, 14, 4, 4);
            return l;
        }
    }

    // ========================================================================
    //  Janela principal
    // ========================================================================
    internal class MainForm : Form
    {
        private readonly Sm809Device _dev = new Sm809Device();
        private LocalConfig _cfg;

        private ListView _btnList;
        private PictureBox _mousePic;
        private Label _statusLabel;
        private Label _connLabel;
        private Label _batteryLabel;
        private Label _numLockLabel;
        // true enquanto a interface está sendo espelhada do mouse: impede que os
        // handlers de rádio/checkbox reenviem comandos de volta ao dispositivo.
        private bool _syncing;
        private Button _reconnectBtn;
        private TabControl _tabs;
        private System.Windows.Forms.Timer _statusTimer;
        private System.Windows.Forms.Timer _reconnectTimer;

        // DPI tab
        private NumericUpDown _dpiCount;
        private readonly RadioButton[] _dpiRadios = new RadioButton[6];
        private readonly NumericUpDown[] _dpiNums = new NumericUpDown[6];
        private readonly Button[] _dpiColorBtns = new Button[6];

        // Desempenho
        private readonly List<RadioButton> _rateRadios = new List<RadioButton>();
        private FlowLayoutPanel _ratePanel;
        private RadioButton _lod1, _lod2;
        private NumericUpDown _sleepNum;
        private CheckBox _wakeChk, _ledMoveChk, _angleChk, _syncChk, _rippleChk;

        // Iluminação
        private readonly List<RadioButton> _lightRadios = new List<RadioButton>();

        // Macros
        private ListBox _macroList;

        // Perfis
        private ComboBox _profileCombo;

        // hotspots na imagem (coordenadas na imagem original 400×400)
        private static readonly Point[] Hotspots =
        {
            new Point(118, 152), new Point(88, 200), new Point(133, 185), new Point(152, 172),
            new Point(128, 240), new Point(148, 225), new Point(140, 287), new Point(160, 267),
            new Point(170, 238), new Point(160, 57), new Point(170, 92), new Point(195, 40),
            new Point(255, 33), new Point(245, 75), new Point(258, 155), new Point(265, 202)
        };

        public MainForm()
        {
            _cfg = LocalConfig.Load();
            Text = "SM809 PRO — Driver Local";
            ClientSize = new Size(1240, 760);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimumSize = Size;   // 1240×760 de área útil é o mínimo — nada quebra ao reduzir
            if (_cfg.WinW >= 1240 && _cfg.WinH >= 760)
                ClientSize = new Size(Math.Min(_cfg.WinW, 3840), Math.Min(_cfg.WinH, 2400));
            if (_cfg.WinMax) WindowState = FormWindowState.Maximized;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;

            var appIcon = AppIcon.Load(SystemInformation.IconSize);
            if (appIcon != null) Icon = appIcon;

            BuildHeader();
            BuildLeftPanel();
            BuildMousePanel();
            BuildTabs();
            BuildStatusBar();

            RefreshButtonList();

            _dev.StatusChanged += OnDeviceStatus;
            _dev.ConnectionLost += OnConnectionLost;

            _statusTimer = new System.Windows.Forms.Timer();
            _statusTimer.Interval = 1500;   // espelhamento quase em tempo real
            _statusTimer.Tick += delegate
            {
                if (_dev.IsConnected) _dev.TriggerStatus();
                UpdateNumLockIndicator();
            };
            _statusTimer.Start();

            _reconnectTimer = new System.Windows.Forms.Timer();
            _reconnectTimer.Interval = 5000;
            _reconnectTimer.Tick += delegate { if (!_dev.IsConnected) TryConnect(false); };
            _reconnectTimer.Start();

            Shown += delegate { TryConnect(true); UpdateNumLockIndicator(); };
            FormClosing += delegate
            {
                _cfg.WinMax = WindowState == FormWindowState.Maximized;
                if (WindowState == FormWindowState.Normal)
                {
                    _cfg.WinW = ClientSize.Width;
                    _cfg.WinH = ClientSize.Height;
                }
                _cfg.Save();
            };
            FormClosed += delegate { _dev.Dispose(); };
        }

        private static string AppDir()
        {
            return Path.GetDirectoryName(Application.ExecutablePath);
        }

        // ------------------------------------------------------------ header
        private Panel _header;

        private void BuildHeader()
        {
            _header = new Panel();
            _header.Height = 62;
            _header.Dock = DockStyle.Top;
            _header.BackColor = Theme.Panel;
            Controls.Add(_header);

            var title = new Label();
            title.Text = "SM809 PRO";
            title.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            title.ForeColor = Theme.Text;
            title.AutoSize = true;
            title.Location = new Point(18, 6);
            _header.Controls.Add(title);

            var sub = new Label();
            sub.Text = "Driver Local — grava direto na memória do mouse";
            sub.ForeColor = Theme.SubText;
            sub.AutoSize = true;
            sub.Location = new Point(20, 38);
            _header.Controls.Add(sub);

            _batteryLabel = new Label();
            _batteryLabel.Text = "";
            _batteryLabel.ForeColor = Theme.SubText;
            _batteryLabel.AutoSize = true;
            _header.Controls.Add(_batteryLabel);

            _numLockLabel = new Label();
            _numLockLabel.AutoSize = true;
            _numLockLabel.Font = new Font("Segoe UI Semibold", 9.5f);
            _numLockLabel.Cursor = Cursors.Hand;
            _numLockLabel.Click += delegate
            {
                NumLockHelper.Toggle();          // alterna: liga ⇄ desliga
                UpdateNumLockIndicator();
                Toast(NumLockHelper.IsOn
                    ? "✔ NumLock LIGADO — botões do numpad digitam números."
                    : "NumLock DESLIGADO — botões do numpad viram setas/Home/End.",
                    NumLockHelper.IsOn);
            };
            _header.Controls.Add(_numLockLabel);

            _connLabel = new Label();
            _connLabel.Text = "● Desconectado";
            _connLabel.ForeColor = Theme.Danger;
            _connLabel.AutoSize = true;
            _connLabel.Font = new Font("Segoe UI Semibold", 9.5f);
            _header.Controls.Add(_connLabel);

            _reconnectBtn = Theme.MakePrimary("Reconectar", 110, 30);
            _reconnectBtn.Click += delegate { TryConnect(true); };
            _header.Controls.Add(_reconnectBtn);

            // realinha sempre que um texto muda de tamanho ou a janela redimensiona
            _batteryLabel.SizeChanged += delegate { LayoutHeader(); };
            _numLockLabel.SizeChanged += delegate { LayoutHeader(); };
            _connLabel.SizeChanged += delegate { LayoutHeader(); };
            _header.Resize += delegate { LayoutHeader(); };
            LayoutHeader();
        }

        // Duas linhas alinhadas pela direita: [conexão · Reconectar] em cima,
        // [NumLock · bateria/DPI/taxa] embaixo — textos longos nunca se sobrepõem.
        private void LayoutHeader()
        {
            if (_header == null || _reconnectBtn == null) return;
            int w = _header.ClientSize.Width;
            _reconnectBtn.Location = new Point(w - 12 - _reconnectBtn.Width, 8);
            _connLabel.Location = new Point(_reconnectBtn.Left - 16 - _connLabel.Width, 15);
            _batteryLabel.Location = new Point(w - 14 - _batteryLabel.Width, 40);
            _numLockLabel.Location = new Point(_batteryLabel.Left - 28 - _numLockLabel.Width, 39);
        }

        // -------------------------------------------------------- botom list
        private void BuildLeftPanel()
        {
            var panel = new Panel();
            panel.Bounds = new Rectangle(12, 74, 300, 636);
            panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            panel.BackColor = Theme.Panel;
            Controls.Add(panel);

            var lbl = new Label();
            lbl.Text = "Botões do mouse";
            lbl.Font = new Font("Segoe UI Semibold", 10f);
            lbl.ForeColor = Theme.Text;
            lbl.AutoSize = true;
            lbl.Location = new Point(12, 10);
            panel.Controls.Add(lbl);

            _btnList = new ListView();
            _btnList.Bounds = new Rectangle(8, 38, 284, 590);
            _btnList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            _btnList.View = View.Details;
            _btnList.FullRowSelect = true;
            _btnList.HideSelection = false;
            _btnList.MultiSelect = false;
            _btnList.HeaderStyle = ColumnHeaderStyle.None;
            _btnList.BackColor = Theme.Panel2;
            _btnList.ForeColor = Theme.Text;
            _btnList.BorderStyle = BorderStyle.None;
            _btnList.Columns.Add("#", 28);
            _btnList.Columns.Add("Botão", 108);
            _btnList.Columns.Add("Função", 144);
            _btnList.SelectedIndexChanged += delegate { _mousePic.Invalidate(); };
            var rowHeight = new ImageList();
            rowHeight.ImageSize = new Size(1, 22);
            _btnList.SmallImageList = rowHeight;
            _btnList.OwnerDraw = true;
            _btnList.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = true; };
            _btnList.DrawSubItem += DrawButtonRow;
            panel.Controls.Add(_btnList);
        }

        // Atualiza a lista SEM recriar itens (não pisca, não perde a seleção) e
        // mostra a função REAL de cada botão agora: teclas do numpad viram
        // seta/Home/End quando o NumLock está desligado.
        private void RefreshButtonList()
        {
            var macros = ToMacroDefs(_cfg.Macros);
            bool numLockOn = NumLockHelper.IsOn;

            if (_btnList.Items.Count != _cfg.Buttons.Count)
            {
                _btnList.BeginUpdate();
                _btnList.Items.Clear();
                foreach (var b in _cfg.Buttons)
                {
                    var it = new ListViewItem(b.Id.ToString());
                    it.SubItems.Add(b.Label);
                    it.SubItems.Add("");
                    _btnList.Items.Add(it);
                }
                _btnList.EndUpdate();
            }

            _btnList.BeginUpdate();
            for (int i = 0; i < _cfg.Buttons.Count; i++)
            {
                var b = _cfg.Buttons[i];
                string fn = KeyTables.CodeName(b.Code, macros, b.MacroId);
                Color fg = b.Locked ? Theme.SubText : Theme.Text;
                if (!numLockOn)
                {
                    string alias = KeyTables.NumLockOffAlias(b.Code);
                    if (alias != null)
                    {
                        fn = fn + " → " + alias;
                        fg = Color.FromArgb(240, 200, 90);   // aviso: não digita número
                    }
                }
                if (b.Locked) fn += " 🔒";
                var item = _btnList.Items[i];
                if (item.SubItems[1].Text != b.Label) item.SubItems[1].Text = b.Label;
                if (item.SubItems[2].Text != fn) item.SubItems[2].Text = fn;
                if (item.ForeColor != fg) item.ForeColor = fg;
            }
            _btnList.EndUpdate();
        }

        // Desenho estilo G HUB: linha selecionada com fundo azul + barra lateral.
        private void DrawButtonRow(object sender, DrawListViewSubItemEventArgs e)
        {
            bool selected = e.Item.Selected;
            Color bg = selected ? Theme.AccentDark : Theme.Panel2;
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            if (selected && e.ColumnIndex == 0)
                using (var bar = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(bar, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
            Color fg = e.Item.ForeColor;
            if (selected && fg == Theme.SubText) fg = Theme.Text;
            var rect = new Rectangle(e.Bounds.X + (e.ColumnIndex == 0 ? 6 : 2), e.Bounds.Y,
                e.Bounds.Width - 8, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _btnList.Font, rect, fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        private int SelectedButtonIndex()
        {
            if (_btnList != null && _btnList.SelectedIndices.Count > 0) return _btnList.SelectedIndices[0];
            return -1;
        }

        // -------------------------------------------------------- mouse pic
        private void BuildMousePanel()
        {
            var panel = new Panel();
            panel.Bounds = new Rectangle(320, 74, 400, 636);
            panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            panel.BackColor = Theme.Panel;
            Controls.Add(panel);

            _mousePic = new PictureBox();
            _mousePic.Bounds = new Rectangle(10, 40, 380, 420);
            _mousePic.SizeMode = PictureBoxSizeMode.Zoom;
            _mousePic.BackColor = Theme.Panel;
            string img = Path.Combine(AppDir(), "images", "mouse_nr.png");
            if (File.Exists(img))
            {
                try { _mousePic.Image = Image.FromFile(img); } catch { }
            }
            _mousePic.MouseClick += MousePicClick;
            _mousePic.Paint += MousePicPaint;
            panel.Controls.Add(_mousePic);

            var lbl = new Label();
            lbl.Text = "Clique em um número na imagem (ou na lista)\npara escolher o botão, depois selecione a\nfunção na aba \"Atribuir\".";
            lbl.ForeColor = Theme.SubText;
            lbl.AutoSize = true;
            lbl.Location = new Point(14, 480);
            panel.Controls.Add(lbl);

            var hint = new Label();
            hint.Text = "As configurações ficam gravadas no próprio mouse:\ndepois de aplicar, funcionam em qualquer computador\nsem precisar deste programa aberto.";
            hint.ForeColor = Theme.Accent;
            hint.AutoSize = true;
            hint.Location = new Point(14, 550);
            panel.Controls.Add(hint);

            var mouseTitle = new Label();
            mouseTitle.Text = "SOLAKAKA SM809 PRO";
            mouseTitle.Font = new Font("Segoe UI Semibold", 10f);
            mouseTitle.ForeColor = Theme.Text;
            mouseTitle.AutoSize = true;
            mouseTitle.Location = new Point(12, 10);
            panel.Controls.Add(mouseTitle);
        }

        // Transforma coordenadas da imagem (400×400) para o PictureBox (Zoom)
        private RectangleF ImageDrawRect()
        {
            if (_mousePic.Image == null) return RectangleF.Empty;
            float iw = _mousePic.Image.Width, ih = _mousePic.Image.Height;
            float cw = _mousePic.ClientSize.Width, ch = _mousePic.ClientSize.Height;
            float scale = Math.Min(cw / iw, ch / ih);
            float w = iw * scale, h = ih * scale;
            return new RectangleF((cw - w) / 2f, (ch - h) / 2f, w, h);
        }

        private void MousePicClick(object sender, MouseEventArgs e)
        {
            if (_mousePic.Image == null) return;
            var r = ImageDrawRect();
            if (r.Width <= 0) return;
            float sx = (e.X - r.X) / r.Width * _mousePic.Image.Width;
            float sy = (e.Y - r.Y) / r.Height * _mousePic.Image.Height;
            int best = -1;
            double bestDist = 26 * 26;
            for (int i = 0; i < Hotspots.Length; i++)
            {
                double dx = sx - Hotspots[i].X, dy = sy - Hotspots[i].Y;
                double d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            if (best >= 0 && best < _btnList.Items.Count)
            {
                _btnList.Items[best].Selected = true;
                _btnList.EnsureVisible(best);
                _btnList.Focus();
            }
        }

        private void MousePicPaint(object sender, PaintEventArgs e)
        {
            int sel = SelectedButtonIndex();
            if (sel < 0 || _mousePic.Image == null) return;
            var r = ImageDrawRect();
            if (r.Width <= 0) return;
            float scale = r.Width / _mousePic.Image.Width;
            float x = r.X + Hotspots[sel].X * scale;
            float y = r.Y + Hotspots[sel].Y * scale;
            using (var pen = new Pen(Theme.Accent, 2.5f))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawEllipse(pen, x - 14, y - 14, 28, 28);
            }
        }

        // ------------------------------------------------------------- tabs
        private void BuildTabs()
        {
            _tabs = new TabControl();
            _tabs.Bounds = new Rectangle(728, 74, 500, 636);
            _tabs.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right;
            _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            _tabs.SizeMode = TabSizeMode.Fixed;
            _tabs.ItemSize = new Size(78, 26);
            _tabs.DrawItem += TabsDrawItem;
            Controls.Add(_tabs);

            _tabs.TabPages.Add(BuildAssignTab());
            _tabs.TabPages.Add(BuildDpiTab());
            _tabs.TabPages.Add(BuildPerfTab());
            _tabs.TabPages.Add(BuildLightTab());
            _tabs.TabPages.Add(BuildMacroTab());
            _tabs.TabPages.Add(BuildSystemTab());
        }

        private void TabsDrawItem(object sender, DrawItemEventArgs e)
        {
            var tab = _tabs.TabPages[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(selected ? Theme.Panel2 : Theme.Panel))
                e.Graphics.FillRectangle(back, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tab.Text, Font, e.Bounds,
                selected ? Theme.Accent : Theme.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (selected)
                using (var bar = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(bar, e.Bounds.X + 8, e.Bounds.Bottom - 3, e.Bounds.Width - 16, 3);
        }

        private TabPage MakePage(string title)
        {
            var page = new TabPage(title);
            page.BackColor = Theme.Panel2;
            return page;
        }

        // ------------------------------------------------------ aba Atribuir
        private TabPage BuildAssignTab()
        {
            var page = MakePage("Atribuir");
            var flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.AutoScroll = true;
            flow.FlowDirection = FlowDirection.LeftToRight;
            flow.WrapContents = true;
            flow.Padding = new Padding(8);
            page.Controls.Add(flow);

            AddCategory(flow, "Mouse", KeyTables.MouseActions(), 106);
            AddCategory(flow, "Números — digitam sempre (recomendado)", KeyTables.DigitActions(), 84);
            AddCategory(flow, "Teclado numérico (numpad — exige NumLock ligado)", KeyTables.NumpadActions(), 84);
            AddCategory(flow, "Janelas / Sistema", KeyTables.WindowsActions(), 148);
            AddCategory(flow, "Navegador", KeyTables.BrowserActions(), 148);
            AddCategory(flow, "Discord", KeyTables.DiscordActions(), 220);
            AddCategory(flow, "Planilhas / Excel", KeyTables.ExcelActions(), 148);
            AddCategory(flow, "Texto / Office", KeyTables.EditActions(), 148);
            AddCategory(flow, "Multimídia", KeyTables.MediaActions(), 106);
            AddCategory(flow, "F13–F24 (Discord PTT / OBS, sem conflito)", KeyTables.FKeyActions(), 68);
            AddCategory(flow, "DPI", KeyTables.DpiActions(), 106);
            AddCategory(flow, "Trava de DPI (enquanto segura)", KeyTables.DpiLockActions(), 106);
            AddCategory(flow, "Especiais", KeyTables.SpecialActions(), 148);

            var advLabel = Theme.MakeSection("Avançado");
            flow.Controls.Add(advLabel);
            flow.SetFlowBreak(advLabel, true);

            var comboBtn = Theme.MakeChip("⌨  Atalho de teclado…", 148, 30);
            comboBtn.Click += delegate { AssignComboKey(); };
            flow.Controls.Add(comboBtn);

            var fireBtn = Theme.MakeChip("🔥  Tecla de fogo…", 148, 30);
            fireBtn.Click += delegate { AssignFireKey(); };
            flow.Controls.Add(fireBtn);

            var macroBtn = Theme.MakeChip("🎬  Macro…", 148, 30);
            macroBtn.Click += delegate { _tabs.SelectedIndex = 4; };
            flow.Controls.Add(macroBtn);

            return page;
        }

        // Título em linha própria (SetFlowBreak) — funciona em qualquer largura,
        // ao contrário de espaçadores de largura fixa.
        private void AddCategory(FlowLayoutPanel flow, string title, List<ActionDef> actions, int chipW)
        {
            var label = Theme.MakeSection(title);
            flow.Controls.Add(label);
            flow.SetFlowBreak(label, true);
            Control last = null;
            foreach (var a in actions)
            {
                var chip = Theme.MakeChip(a.Name, chipW, 30);
                var captured = a;
                chip.Click += delegate { AssignAction(captured.Name, captured.Code, null); };
                flow.Controls.Add(chip);
                last = chip;
            }
            if (last != null) flow.SetFlowBreak(last, true);
        }

        private ButtonCfg CurrentButton(bool warn)
        {
            int idx = SelectedButtonIndex();
            if (idx < 0)
            {
                if (warn) Toast("Selecione primeiro um botão na lista à esquerda.", false);
                return null;
            }
            var b = _cfg.Buttons[idx];
            if (b.Locked)
            {
                if (warn) Toast("O clique esquerdo é travado (proteção contra perder o mouse).", false);
                return null;
            }
            return b;
        }

        private void AssignAction(string name, byte[] code, string macroId)
        {
            var b = CurrentButton(true);
            if (b == null) return;

            // proteção: manter pelo menos um clique esquerdo
            bool removingLeft = b.Code != null && b.Code.Length >= 3 && b.Code[0] == 16 && b.Code[1] == 1;
            if (removingLeft)
            {
                int leftCount = _cfg.Buttons.Count(x => x.Code != null && x.Code.Length >= 3 && x.Code[0] == 16 && x.Code[1] == 1);
                bool newIsLeft = code[0] == 16 && code[1] == 1;
                if (leftCount <= 1 && !newIsLeft)
                {
                    Toast("Pelo menos um botão precisa continuar como clique esquerdo.", false);
                    return;
                }
            }

            b.Code = new int[] { code[0], code[1], code[2] };
            b.MacroId = macroId;
            _cfg.Save();
            RefreshButtonList();
            ApplyButtonsToDevice(string.Format("Botão {0} → {1}", b.Id, name));
            WarnIfNumLockOff(b.Code);
        }

        // Teclas do numpad só digitam números com o NumLock ligado — sem ele
        // viram seta/Home/End. Em teclado sem numpad o usuário não tem como ligar.
        private void WarnIfNumLockOff(int[] code)
        {
            if (!NumLockHelper.IsNumpadCode(code) || NumLockHelper.IsOn) return;
            var r = MessageBox.Show(this,
                "O NumLock está DESLIGADO — as teclas do numpad viram setas/Home/End em vez de "
                + "digitar números.\n\nLigar o NumLock agora?\n\n(Dica: a categoria \"Números — digitam "
                + "sempre\" não depende do NumLock.)",
                "NumLock desligado", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            NumLockHelper.TurnOn();
            UpdateNumLockIndicator();
            Toast(NumLockHelper.IsOn
                ? "✔ NumLock ligado — os botões do numpad já digitam números."
                : "Não foi possível ligar o NumLock.", NumLockHelper.IsOn);
        }

        private void AssignComboKey()
        {
            var b = CurrentButton(true);
            if (b == null) return;
            using (var dlg = new ComboKeyDialog())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.MainHid > 0)
                {
                    int mods = dlg.Modifiers;
                    var code = new byte[] { 112, (byte)mods, (byte)dlg.MainHid };
                    AssignAction(KeyTables.CodeName(new[] { 112, mods, dlg.MainHid }, null, null), code, null);
                }
            }
        }

        private void AssignFireKey()
        {
            var b = CurrentButton(true);
            if (b == null) return;
            using (var dlg = new FireKeyDialog())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var code = new byte[] { 48, (byte)dlg.Interval, (byte)dlg.Count };
                    AssignAction("Tecla de fogo", code, null);
                }
            }
        }

        private void ApplyButtonsToDevice(string desc)
        {
            if (!_dev.IsConnected)
            {
                Toast("Salvo localmente. Conecte o mouse para gravar (via cabo ou dongle 2.4G).", false);
                return;
            }
            Cursor = Cursors.WaitCursor;
            try
            {
                var codes = new byte[16][];
                for (int i = 0; i < 16; i++)
                {
                    var bc = _cfg.Buttons[i].Code;
                    codes[i] = (bc != null && bc.Length >= 3)
                        ? new byte[] { (byte)bc[0], (byte)bc[1], (byte)bc[2] }
                        : new byte[] { 0xFF, 0xFF, 0xFF };
                }
                bool ok = _dev.ApplyButtons(codes);

                // macros atribuídas
                var assigns = new List<KeyValuePair<int, MacroDef>>();
                foreach (var btn in _cfg.Buttons)
                {
                    if (!string.IsNullOrEmpty(btn.MacroId))
                    {
                        var mj = _cfg.Macros.FirstOrDefault(m => m.Id == btn.MacroId);
                        if (mj != null) assigns.Add(new KeyValuePair<int, MacroDef>(btn.Id, FromJson(mj)));
                    }
                }
                if (ok && assigns.Count > 0) ok = _dev.SendMacros(assigns);

                Toast(ok ? "✔ " + desc + " — gravado no mouse." : "Falha ao gravar no mouse. Tente novamente.", ok);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        // ---------------------------------------------------------- aba DPI
        private TabPage BuildDpiTab()
        {
            var page = MakePage("DPI");
            var y = 16;

            var lbl = new Label();
            lbl.Text = "Quantidade de níveis:";
            lbl.ForeColor = Theme.Text;
            lbl.AutoSize = true;
            lbl.Location = new Point(16, y + 4);
            page.Controls.Add(lbl);

            _dpiCount = MakeNum(150, y, 1, 6, _cfg.DpiCount);
            _dpiCount.Location = new Point(160, y);
            _dpiCount.ValueChanged += delegate { UpdateDpiRowsVisibility(); };
            page.Controls.Add(_dpiCount);
            y += 42;

            for (int i = 0; i < 6; i++)
            {
                var radio = new RadioButton();
                radio.Text = string.Format("Nível {0}", i + 1);
                radio.ForeColor = Theme.Text;
                radio.AutoSize = true;
                radio.Location = new Point(16, y + 4);
                _dpiRadios[i] = radio;
                page.Controls.Add(radio);

                var num = MakeNum(110, y, 100, 26000, Math.Min(Math.Max(_cfg.DpiLevels[i], 100), 26000));
                num.Increment = 100;
                num.Location = new Point(110, y);
                _dpiNums[i] = num;
                page.Controls.Add(num);

                var colorBtn = new Button();
                colorBtn.Size = new Size(46, 26);
                colorBtn.FlatStyle = FlatStyle.Flat;
                colorBtn.FlatAppearance.BorderColor = Theme.Border;
                colorBtn.BackColor = HexColor(_cfg.DpiColors[i]);
                colorBtn.Location = new Point(210, y);
                colorBtn.Cursor = Cursors.Hand;
                int ci = i;
                colorBtn.Click += delegate
                {
                    using (var cd = new ColorDialog())
                    {
                        cd.Color = _dpiColorBtns[ci].BackColor;
                        cd.FullOpen = true;
                        if (cd.ShowDialog(this) == DialogResult.OK)
                            _dpiColorBtns[ci].BackColor = cd.Color;
                    }
                };
                _dpiColorBtns[i] = colorBtn;
                page.Controls.Add(colorBtn);

                var tag = new Label();
                tag.Text = "cor do LED";
                tag.ForeColor = Theme.SubText;
                tag.AutoSize = true;
                tag.Location = new Point(264, y + 6);
                page.Controls.Add(tag);
                y += 34;
            }
            if (_cfg.DpiCurrent >= 0 && _cfg.DpiCurrent < 6)
                _dpiRadios[_cfg.DpiCurrent].Checked = true;

            y += 8;
            var apply = Theme.MakePrimary("Aplicar DPI no mouse", 200, 34);
            apply.Location = new Point(16, y);
            apply.Click += delegate { ApplyDpiClicked(); };
            page.Controls.Add(apply);

            var note = new Label();
            note.Text = "O botão DPI do mouse cicla entre os níveis ativos.\nPasso de 100 DPI (sensores especiais: 50).";
            note.ForeColor = Theme.SubText;
            note.AutoSize = true;
            note.Location = new Point(16, y + 46);
            page.Controls.Add(note);

            UpdateDpiRowsVisibility();
            return page;
        }

        private void UpdateDpiRowsVisibility()
        {
            int count = (int)_dpiCount.Value;
            for (int i = 0; i < 6; i++)
            {
                bool on = i < count;
                _dpiRadios[i].Enabled = on;
                _dpiNums[i].Enabled = on;
                _dpiColorBtns[i].Enabled = on;
                if (!on && _dpiRadios[i].Checked) _dpiRadios[0].Checked = true;
            }
        }

        private void ApplyDpiClicked()
        {
            int count = (int)_dpiCount.Value;
            int current = 0;
            for (int i = 0; i < count; i++) if (_dpiRadios[i].Checked) current = i;

            var levels = new int[8];
            var colors = new string[8];
            for (int i = 0; i < 8; i++)
            {
                levels[i] = i < 6 ? (int)_dpiNums[i].Value : 1600;
                colors[i] = i < 6 ? ColorHex(_dpiColorBtns[i].BackColor) : "#ea5e56";
            }
            _cfg.DpiLevels = levels;
            _cfg.DpiColors = colors;
            _cfg.DpiCount = count;
            _cfg.DpiCurrent = current;
            _cfg.Save();

            if (!_dev.IsConnected) { Toast("Salvo localmente. Conecte o mouse para gravar.", false); return; }
            Cursor = Cursors.WaitCursor;
            try
            {
                bool ok = _dev.ApplyDpi(levels, colors, count, current, _dev.Status.SensorType);
                Toast(ok ? "✔ Níveis de DPI gravados no mouse." : "Falha ao gravar DPI.", ok);
            }
            finally { Cursor = Cursors.Default; }
        }

        // -------------------------------------------------- aba Desempenho
        private TabPage BuildPerfTab()
        {
            var page = MakePage("Desemp.");
            int y = 14;

            page.Controls.Add(SectionAt("Taxa de retorno (polling rate)", 16, y)); y += 26;
            _ratePanel = new FlowLayoutPanel();
            _ratePanel.Location = new Point(16, y);
            _ratePanel.Size = new Size(460, 34);
            _ratePanel.BackColor = Color.Transparent;
            page.Controls.Add(_ratePanel);
            RebuildRateOptions();
            y += 44;

            page.Controls.Add(SectionAt("Distância de lift-off", 16, y)); y += 26;
            _lod1 = MakeRadio("1 mm", 16, y);
            _lod2 = MakeRadio("2 mm", 90, y);
            page.Controls.Add(_lod1);
            page.Controls.Add(_lod2);
            if (_cfg.Lod == 1) _lod1.Checked = true; else _lod2.Checked = true;
            _lod1.CheckedChanged += delegate { if (_lod1.Checked && !_syncing) ApplyLodClicked(1); };
            _lod2.CheckedChanged += delegate { if (_lod2.Checked && !_syncing) ApplyLodClicked(2); };
            y += 40;

            page.Controls.Add(SectionAt("Energia (modo sem fio)", 16, y)); y += 26;
            var sl = new Label();
            sl.Text = "Dormir após (min):";
            sl.ForeColor = Theme.Text;
            sl.AutoSize = true;
            sl.Location = new Point(16, y + 4);
            page.Controls.Add(sl);
            _sleepNum = MakeNum(140, y, 1, 20, Math.Min(Math.Max(_cfg.SleepTime, 1), 20));
            _sleepNum.Location = new Point(140, y);
            page.Controls.Add(_sleepNum);
            y += 34;
            _wakeChk = MakeCheck("Acordar ao mover", 16, y, _cfg.MoveWakeup);
            page.Controls.Add(_wakeChk);
            _ledMoveChk = MakeCheck("LED aceso só em movimento", 190, y, _cfg.MoveLighting);
            page.Controls.Add(_ledMoveChk);
            y += 30;
            var powerBtn = Theme.MakePrimary("Aplicar energia", 150, 30);
            powerBtn.Location = new Point(16, y);
            powerBtn.Click += delegate { ApplyPowerClicked(); };
            page.Controls.Add(powerBtn);
            y += 46;

            page.Controls.Add(SectionAt("Sensor avançado", 16, y)); y += 26;
            _angleChk = MakeCheck("Correção de ângulo (angle snap)", 16, y, _cfg.AngleSnap);
            page.Controls.Add(_angleChk); y += 28;
            _syncChk = MakeCheck("Motion sync", 16, y, _cfg.MotionSync);
            page.Controls.Add(_syncChk); y += 28;
            _rippleChk = MakeCheck("Controle de ripple", 16, y, _cfg.RippleControl);
            page.Controls.Add(_rippleChk); y += 32;
            var sensorBtn = Theme.MakePrimary("Aplicar sensor", 150, 30);
            sensorBtn.Location = new Point(16, y);
            sensorBtn.Click += delegate { ApplySensorClicked(); };
            page.Controls.Add(sensorBtn);

            return page;
        }

        private int _rateBuiltForMax = -1;

        private void RebuildRateOptions()
        {
            int maxNow = _dev.Status.Valid ? _dev.Status.MaxReportRate : 1;
            if (_rateBuiltForMax == maxNow)
            {
                // já montado para este hardware: só re-marca o rádio correto
                bool prev = _syncing;
                _syncing = true;
                try
                {
                    foreach (var rb in _rateRadios)
                        if (rb.Tag is int && (int)rb.Tag == _cfg.PollRate && !rb.Checked) rb.Checked = true;
                }
                finally { _syncing = prev; }
                return;
            }
            _rateBuiltForMax = maxNow;
            var stale = new List<Control>();
            foreach (Control c in _ratePanel.Controls) stale.Add(c);
            _ratePanel.Controls.Clear();
            foreach (var c in stale) c.Dispose();
            _rateRadios.Clear();
            var options = new List<KeyValuePair<string, int>>
            {
                new KeyValuePair<string, int>("125 Hz", 8),
                new KeyValuePair<string, int>("250 Hz", 4),
                new KeyValuePair<string, int>("500 Hz", 2),
                new KeyValuePair<string, int>("1000 Hz", 1),
            };
            int max = maxNow;
            if (max == 2 || max == 4 || max == 8) options.Add(new KeyValuePair<string, int>("2000 Hz", 17));
            if (max == 4 || max == 8) options.Add(new KeyValuePair<string, int>("4000 Hz", 18));
            if (max == 8) options.Add(new KeyValuePair<string, int>("8000 Hz", 20));

            foreach (var opt in options)
            {
                var r = new RadioButton();
                r.Text = opt.Key;
                r.Tag = opt.Value;
                r.AutoSize = true;
                r.ForeColor = Theme.Text;
                if (_cfg.PollRate == opt.Value) r.Checked = true;
                var captured = opt;
                r.CheckedChanged += delegate(object s, EventArgs e)
                {
                    var rb = (RadioButton)s;
                    if (rb.Checked && !_syncing) ApplyRateClicked(captured.Value);
                };
                _rateRadios.Add(r);
                _ratePanel.Controls.Add(r);
            }
        }

        private void ApplyRateClicked(int val)
        {
            if (_cfg.PollRate == val) return;
            _cfg.PollRate = val;
            _cfg.Save();
            if (!_dev.IsConnected) { Toast("Salvo localmente (mouse desconectado).", false); return; }
            bool ok = _dev.ApplyReportRate(val);
            Toast(ok ? "✔ Taxa de retorno aplicada." : "Falha ao aplicar taxa.", ok);
        }

        private void ApplyLodClicked(int mm)
        {
            if (_cfg.Lod == mm) return;
            _cfg.Lod = mm;
            _cfg.Save();
            if (!_dev.IsConnected) { Toast("Salvo localmente (mouse desconectado).", false); return; }
            bool ok = _dev.ApplyLod(mm);
            Toast(ok ? "✔ Lift-off aplicado." : "Falha ao aplicar lift-off.", ok);
        }

        private void ApplyPowerClicked()
        {
            _cfg.SleepTime = (int)_sleepNum.Value;
            _cfg.MoveWakeup = _wakeChk.Checked;
            _cfg.MoveLighting = _ledMoveChk.Checked;
            _cfg.Save();
            if (!_dev.IsConnected) { Toast("Salvo localmente (mouse desconectado).", false); return; }
            bool ok = _dev.ApplyPower(_cfg.SleepTime, _cfg.MoveWakeup, _cfg.MoveLighting);
            Toast(ok ? "✔ Energia aplicada." : "Falha ao aplicar energia.", ok);
        }

        private void ApplySensorClicked()
        {
            _cfg.AngleSnap = _angleChk.Checked;
            _cfg.MotionSync = _syncChk.Checked;
            _cfg.RippleControl = _rippleChk.Checked;
            _cfg.Save();
            if (!_dev.IsConnected) { Toast("Salvo localmente (mouse desconectado).", false); return; }
            bool ok = _dev.ApplySensorAdvanced(_cfg.AngleSnap, _cfg.MotionSync, _cfg.RippleControl);
            Toast(ok ? "✔ Sensor aplicado." : "Falha ao aplicar sensor.", ok);
        }

        // -------------------------------------------------- aba Iluminação
        private TabPage BuildLightTab()
        {
            var page = MakePage("LED");
            string[] modes =
            {
                "Desligado",                 // 0
                "Estrelas piscando",         // 1
                "Fluxo colorido",            // 2
                "Alongamento 7 cores",       // 3
                "Flash 7 cores",             // 4
                "Respiração colorida",       // 5
                "Explosão 7 cores",          // 6
                "Cobertura colorida",        // 7
                "Vermelho fixo",             // 8
                "Verde fixo",                // 9
                "Azul fixo",                 // 10
            };
            int y = 16;
            page.Controls.Add(SectionAt("Efeito de iluminação", 16, y));
            y += 30;
            for (int m = 0; m < modes.Length; m++)
            {
                var r = MakeRadio(modes[m], 16 + (m % 2) * 230, y + (m / 2) * 30);
                r.Tag = m;
                if (_cfg.LightMode == m) r.Checked = true;
                int captured = m;
                r.CheckedChanged += delegate(object s, EventArgs e)
                {
                    var rb = (RadioButton)s;
                    if (rb.Checked && !_syncing) ApplyLightClicked(captured);
                };
                _lightRadios.Add(r);
                page.Controls.Add(r);
            }
            y += (modes.Length / 2 + 1) * 30 + 16;
            var note = new Label();
            note.Text = "O efeito é aplicado na hora e fica salvo no mouse.\nA cor de cada nível de DPI é configurada na aba DPI.";
            note.ForeColor = Theme.SubText;
            note.AutoSize = true;
            note.Location = new Point(16, y);
            page.Controls.Add(note);
            return page;
        }

        private void ApplyLightClicked(int mode)
        {
            if (_cfg.LightMode == mode) return;
            _cfg.LightMode = mode;
            _cfg.Save();
            if (!_dev.IsConnected) { Toast("Salvo localmente (mouse desconectado).", false); return; }
            bool ok = _dev.ApplyLighting(mode);
            Toast(ok ? "✔ Iluminação aplicada." : "Falha ao aplicar iluminação.", ok);
        }

        // ------------------------------------------------------ aba Macros
        private TabPage BuildMacroTab()
        {
            var page = MakePage("Macros");
            int y = 14;
            page.Controls.Add(SectionAt("Macros salvas", 16, y));
            y += 28;

            _macroList = new ListBox();
            _macroList.Bounds = new Rectangle(16, y, 300, 300);
            _macroList.BackColor = Theme.Panel;
            _macroList.ForeColor = Theme.Text;
            _macroList.BorderStyle = BorderStyle.FixedSingle;
            _macroList.DrawMode = DrawMode.OwnerDrawFixed;
            _macroList.ItemHeight = 26;
            _macroList.DrawItem += delegate(object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Theme.AccentDark : Theme.Panel))
                    e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, _macroList.Items[e.Index].ToString(),
                    _macroList.Font, e.Bounds, sel ? Color.White : Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            page.Controls.Add(_macroList);
            RefreshMacroList();

            int bx = 330;
            var newBtn = Theme.MakePrimary("Nova macro…", 145, 32);
            newBtn.Location = new Point(bx, y);
            newBtn.Click += delegate { EditMacro(null); };
            page.Controls.Add(newBtn);

            var editBtn = Theme.MakeChip("Editar…", 145, 32);
            editBtn.Location = new Point(bx, y + 40);
            editBtn.Click += delegate
            {
                var m = SelectedMacro();
                if (m != null) EditMacro(m);
            };
            page.Controls.Add(editBtn);

            var delBtn = Theme.MakeChip("Excluir", 145, 32);
            delBtn.Location = new Point(bx, y + 80);
            delBtn.Click += delegate
            {
                var m = SelectedMacro();
                if (m == null) return;
                // botões que usavam a macro voltam ao padrão de fábrica
                var defs = LocalConfig.Defaults();
                bool touched = false;
                foreach (var b in _cfg.Buttons)
                {
                    if (b.MacroId == m.Id)
                    {
                        b.MacroId = null;
                        b.Code = defs.Buttons[b.Id - 1].Code;
                        touched = true;
                    }
                }
                _cfg.Macros.Remove(m);
                _cfg.Save();
                RefreshMacroList();
                RefreshButtonList();
                if (touched) ApplyButtonsToDevice("Macro excluída — botão restaurado");
            };
            page.Controls.Add(delBtn);

            var assignBtn = Theme.MakePrimary("Atribuir ao botão\nselecionado", 145, 48);
            assignBtn.Location = new Point(bx, y + 130);
            assignBtn.Click += delegate { AssignMacroToButton(); };
            page.Controls.Add(assignBtn);

            var note = new Label();
            note.Text = "1. Crie/grave uma macro (teclas + tempos).\n2. Selecione um botão na lista à esquerda.\n3. Clique em \"Atribuir ao botão selecionado\".\nA macro é gravada na memória do mouse.";
            note.ForeColor = Theme.SubText;
            note.AutoSize = true;
            note.Location = new Point(16, y + 316);
            page.Controls.Add(note);

            return page;
        }

        private void RefreshMacroList()
        {
            _macroList.Items.Clear();
            foreach (var m in _cfg.Macros)
                _macroList.Items.Add(string.Format("{0}  ({1} eventos)", m.Name, m.Events != null ? m.Events.Count : 0));
        }

        private MacroJson SelectedMacro()
        {
            int i = _macroList.SelectedIndex;
            if (i < 0 || i >= _cfg.Macros.Count)
            {
                Toast("Selecione uma macro na lista.", false);
                return null;
            }
            return _cfg.Macros[i];
        }

        private void EditMacro(MacroJson existing)
        {
            using (var dlg = new MacroDialog(existing != null ? FromJson(existing) : null))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                {
                    var mj = ToJson(dlg.Result);
                    bool reassigned = false;
                    if (existing != null)
                    {
                        mj.Id = existing.Id;
                        int idx = _cfg.Macros.IndexOf(existing);
                        _cfg.Macros[idx] = mj;
                        // botões que usam esta macro: atualiza modo/repetições e regrava
                        foreach (var b in _cfg.Buttons)
                        {
                            if (b.MacroId == mj.Id)
                            {
                                int execHigh = mj.ExecType == 2 ? 2 : mj.ExecType == 4 ? 4 : 1;
                                int repeat = mj.ExecType == 2 ? Math.Max(1, mj.RepeatCount) : 0;
                                int nib = b.Id == 16 ? 12 : (b.Id & 15);
                                b.Code = new int[] { 144, (execHigh << 4) | nib, repeat };
                                reassigned = true;
                            }
                        }
                    }
                    else
                    {
                        mj.Id = "macro_" + DateTime.Now.Ticks;
                        _cfg.Macros.Add(mj);
                    }
                    _cfg.Save();
                    RefreshMacroList();
                    RefreshButtonList();
                    if (reassigned) ApplyButtonsToDevice(string.Format("Macro \"{0}\" atualizada", mj.Name));
                }
            }
        }

        private void AssignMacroToButton()
        {
            var m = SelectedMacro();
            if (m == null) return;
            var b = CurrentButton(true);
            if (b == null) return;

            int execHigh;
            switch (m.ExecType)
            {
                case 2: execHigh = 2; break;
                case 4: execHigh = 4; break;
                default: execHigh = 1; break;
            }
            int repeat = m.ExecType == 2 ? Math.Max(1, m.RepeatCount) : 0;
            int btnNibble = b.Id == 16 ? 12 : (b.Id & 15);
            var code = new byte[] { 144, (byte)((execHigh << 4) | btnNibble), (byte)repeat };
            b.Code = new int[] { code[0], code[1], code[2] };
            b.MacroId = m.Id;
            _cfg.Save();
            RefreshButtonList();
            ApplyButtonsToDevice(string.Format("Botão {0} → macro \"{1}\"", b.Id, m.Name));
        }

        private static MacroDef FromJson(MacroJson j)
        {
            var m = new MacroDef();
            m.Id = j.Id;
            m.Name = j.Name;
            m.ExecType = j.ExecType;
            m.RepeatCount = j.RepeatCount;
            m.Events = new List<MacroEvent>();
            if (j.Events != null)
            {
                foreach (var e in j.Events)
                {
                    var ev = new MacroEvent();
                    ev.Type = e.Type;
                    ev.Hid = e.Hid;
                    ev.Delay = e.Delay;
                    ev.Name = e.Name;
                    m.Events.Add(ev);
                }
            }
            return m;
        }

        private static MacroJson ToJson(MacroDef m)
        {
            var j = new MacroJson();
            j.Id = m.Id;
            j.Name = m.Name;
            j.ExecType = m.ExecType;
            j.RepeatCount = m.RepeatCount;
            j.Events = new List<MacroEventJson>();
            foreach (var e in m.Events)
            {
                var ej = new MacroEventJson();
                ej.Type = e.Type;
                ej.Hid = e.Hid;
                ej.Delay = e.Delay;
                ej.Name = e.Name;
                j.Events.Add(ej);
            }
            return j;
        }

        private static List<MacroDef> ToMacroDefs(List<MacroJson> list)
        {
            var r = new List<MacroDef>();
            if (list != null) foreach (var j in list) r.Add(FromJson(j));
            return r;
        }

        // ----------------------------------------------------- aba Sistema
        private TabPage BuildSystemTab()
        {
            var page = MakePage("Sistema");
            int y = 16;

            page.Controls.Add(SectionAt("Perfis (ex.: Jogo, Trabalho)", 16, y)); y += 28;
            _profileCombo = new ComboBox();
            _profileCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _profileCombo.Bounds = new Rectangle(16, y, 200, 26);
            _profileCombo.BackColor = Theme.Panel;
            _profileCombo.ForeColor = Theme.Text;
            page.Controls.Add(_profileCombo);
            RefreshProfileList();

            var saveProf = Theme.MakeChip("Salvar atual como…", 130, 27);
            saveProf.Location = new Point(226, y);
            saveProf.Click += delegate { SaveProfileAs(); };
            page.Controls.Add(saveProf);

            var applyProf = Theme.MakePrimary("Aplicar", 78, 27);
            applyProf.Location = new Point(362, y);
            applyProf.Click += delegate { ApplyProfile(); };
            page.Controls.Add(applyProf);

            var delProf = Theme.MakeChip("Excluir", 70, 27);
            delProf.Location = new Point(16, y + 32);
            delProf.Click += delegate { DeleteProfile(); };
            page.Controls.Add(delProf);
            y += 66;

            var profNote = new Label();
            profNote.Text = "\"Aplicar\" carrega o perfil e grava tudo no mouse de uma vez.";
            profNote.ForeColor = Theme.SubText;
            profNote.AutoSize = true;
            profNote.Location = new Point(16, y);
            page.Controls.Add(profNote);
            y += 30;

            page.Controls.Add(SectionAt("Gravação", 16, y)); y += 28;
            var reapply = Theme.MakePrimary("Regravar tudo no mouse", 210, 34);
            reapply.Location = new Point(16, y);
            reapply.Click += delegate { ReapplyAll(); };
            page.Controls.Add(reapply);
            y += 44;

            var expl = new Label();
            expl.Text = "Envia botões, DPI, desempenho e iluminação de uma vez.\nÚtil depois de reconectar ou trocar de modo (cabo/2.4G).";
            expl.ForeColor = Theme.SubText;
            expl.AutoSize = true;
            expl.Location = new Point(16, y);
            page.Controls.Add(expl);
            y += 56;

            page.Controls.Add(SectionAt("Restaurar", 16, y)); y += 28;
            var resetBtn = Theme.MakeChip("Reset de fábrica do mouse", 210, 34);
            resetBtn.ForeColor = Theme.Danger;
            resetBtn.Location = new Point(16, y);
            resetBtn.Click += delegate
            {
                if (!_dev.IsConnected) { Toast("Conecte o mouse primeiro.", false); return; }
                var confirm = MessageBox.Show(this,
                    "Isso apaga TODA a configuração gravada no mouse e volta aos padrões de fábrica. Continuar?",
                    "Reset de fábrica", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes) return;
                bool ok = _dev.FactoryReset();
                if (ok)
                {
                    _cfg = LocalConfig.Defaults();
                    _cfg.Save();
                    UpdateUiFromConfig();
                    Toast("✔ Mouse restaurado para os padrões de fábrica.", true);
                }
                else Toast("Falha no reset.", false);
            };
            page.Controls.Add(resetBtn);
            y += 56;

            page.Controls.Add(SectionAt("Sobre", 16, y)); y += 28;
            var about = new Label();
            about.Text = "SM809 PRO — Driver Local v1.0\n" +
                "Reimplementação do driver web oficial (driver.yuandaxin-tech.com)\n" +
                "para uso offline. Protocolo documentado em PROTOCOLO.md.\n\n" +
                "As configurações são gravadas na memória interna do mouse\n" +
                "(perfil onboard) — iguais às feitas pelo site oficial.";
            about.ForeColor = Theme.SubText;
            about.AutoSize = true;
            about.Location = new Point(16, y);
            page.Controls.Add(about);

            return page;
        }

        // ---------------------------------------------------------- perfis
        private static string ProfileDir()
        {
            return Path.Combine(LocalConfig.ConfigDir, "profiles");
        }

        private void RefreshProfileList()
        {
            _profileCombo.Items.Clear();
            try
            {
                if (Directory.Exists(ProfileDir()))
                    foreach (var f in Directory.GetFiles(ProfileDir(), "*.json"))
                        _profileCombo.Items.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            if (_profileCombo.Items.Count > 0) _profileCombo.SelectedIndex = 0;
        }

        private void SaveProfileAs()
        {
            using (var dlg = new InputDialog("Salvar perfil", "Nome do perfil (ex.: Jogo, Trabalho):"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string name = dlg.Value.Trim();
                if (name.Length == 0) return;
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                try
                {
                    Directory.CreateDirectory(ProfileDir());
                    var ser = new JavaScriptSerializer();
                    File.WriteAllText(Path.Combine(ProfileDir(), name + ".json"), ser.Serialize(_cfg), Encoding.UTF8);
                    RefreshProfileList();
                    _profileCombo.SelectedItem = name;
                    Toast(string.Format("✔ Perfil \"{0}\" salvo.", name), true);
                }
                catch { Toast("Falha ao salvar o perfil.", false); }
            }
        }

        private void ApplyProfile()
        {
            if (_profileCombo.SelectedItem == null) { Toast("Selecione um perfil.", false); return; }
            string name = _profileCombo.SelectedItem.ToString();
            try
            {
                var ser = new JavaScriptSerializer();
                var cfg = ser.Deserialize<LocalConfig>(File.ReadAllText(Path.Combine(ProfileDir(), name + ".json"), Encoding.UTF8));
                if (cfg == null || cfg.Buttons == null || cfg.Buttons.Count != 16)
                {
                    Toast("Perfil inválido.", false);
                    return;
                }
                _cfg = LocalConfig.Normalize(cfg);
                _cfg.Save();
                UpdateUiFromConfig();
                ReapplyAll();
                Toast(string.Format("✔ Perfil \"{0}\" aplicado e gravado no mouse.", name), true);
            }
            catch { Toast("Falha ao carregar o perfil.", false); }
        }

        private void DeleteProfile()
        {
            if (_profileCombo.SelectedItem == null) return;
            string name = _profileCombo.SelectedItem.ToString();
            var confirm = MessageBox.Show(this, string.Format("Excluir o perfil \"{0}\"?", name),
                "Excluir perfil", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;
            try
            {
                File.Delete(Path.Combine(ProfileDir(), name + ".json"));
                RefreshProfileList();
            }
            catch { }
        }

        // Regrava a configuração completa no mouse (botões, DPI, desempenho, LED).
        private void ReapplyAll()
        {
            ApplyButtonsToDevice("Configuração completa");
            if (_dev.IsConnected)
            {
                _dev.ApplyDpi(_cfg.DpiLevels, _cfg.DpiColors, _cfg.DpiCount, _cfg.DpiCurrent, _dev.Status.SensorType);
                _dev.ApplyReportRate(_cfg.PollRate);
                _dev.ApplyLod(_cfg.Lod);
                _dev.ApplyPower(_cfg.SleepTime, _cfg.MoveWakeup, _cfg.MoveLighting);
                _dev.ApplySensorAdvanced(_cfg.AngleSnap, _cfg.MotionSync, _cfg.RippleControl);
                _dev.ApplyLighting(_cfg.LightMode);
            }
        }

        // Sincroniza todos os controles das abas com _cfg (reset, troca de perfil).
        // Os guards "valor igual → return" nos handlers evitam reenvios ao mouse.
        private void UpdateUiFromConfig()
        {
            _dpiCount.Value = Math.Min(Math.Max(_cfg.DpiCount, 1), 6);
            for (int i = 0; i < 6; i++)
            {
                int v = (_cfg.DpiLevels != null && i < _cfg.DpiLevels.Length) ? _cfg.DpiLevels[i] : 1600;
                _dpiNums[i].Value = Math.Min(Math.Max(v, 100), 26000);
                _dpiColorBtns[i].BackColor = HexColor(_cfg.DpiColors[i]);
            }
            int cur = _cfg.DpiCurrent;
            if (cur < 0 || cur >= 6) cur = 0;
            _dpiRadios[cur].Checked = true;
            UpdateDpiRowsVisibility();

            if (_cfg.Lod == 1) _lod1.Checked = true; else _lod2.Checked = true;
            _sleepNum.Value = Math.Min(Math.Max(_cfg.SleepTime, 1), 20);
            _wakeChk.Checked = _cfg.MoveWakeup;
            _ledMoveChk.Checked = _cfg.MoveLighting;
            _angleChk.Checked = _cfg.AngleSnap;
            _syncChk.Checked = _cfg.MotionSync;
            _rippleChk.Checked = _cfg.RippleControl;
            RebuildRateOptions();
            foreach (var r in _lightRadios)
                if (r.Tag is int && (int)r.Tag == _cfg.LightMode) r.Checked = true;
            RefreshButtonList();
            RefreshMacroList();
            UpdateNumLockIndicator();
        }

        // ------------------------------------------------------- status bar
        private void BuildStatusBar()
        {
            var bar = new Panel();
            bar.Height = 42;
            bar.Dock = DockStyle.Bottom;
            bar.BackColor = Theme.Panel;
            Controls.Add(bar);

            _statusLabel = new Label();
            _statusLabel.Text = "Pronto.";
            _statusLabel.ForeColor = Theme.SubText;
            _statusLabel.AutoSize = true;
            _statusLabel.Location = new Point(18, 12);
            bar.Controls.Add(_statusLabel);
        }

        private bool _lastNumLock = true;

        // Botão de alternância no topo: sempre visível, liga E desliga o NumLock.
        // Quando o estado muda, a lista de botões é re-renderizada para mostrar a
        // função REAL de cada tecla naquele momento.
        private void UpdateNumLockIndicator()
        {
            bool on = NumLockHelper.IsOn;
            string txt = on ? "⊙ NumLock LIGADO (clique p/ desligar)"
                            : "⊙ NumLock DESLIGADO (clique p/ ligar)";
            if (_numLockLabel.Text != txt) _numLockLabel.Text = txt;
            Color fg = on ? Theme.Accent : Color.FromArgb(240, 200, 90);
            if (_numLockLabel.ForeColor != fg) _numLockLabel.ForeColor = fg;
            if (_lastNumLock != on)
            {
                _lastNumLock = on;
                RefreshButtonList();   // funções efetivas mudaram
            }
        }

        private void Toast(string msg, bool ok)
        {
            _statusLabel.Text = msg;
            _statusLabel.ForeColor = ok ? Theme.Accent : Color.FromArgb(240, 200, 90);
        }

        // ---------------------------------------------------------- conexão
        private void TryConnect(bool verbose)
        {
            bool ok = _dev.Connect();
            if (ok)
            {
                _connLabel.Text = "● Conectado (" + _dev.ConnectionMode + ")";
                _connLabel.ForeColor = Theme.Accent;
                if (verbose) Toast("✔ Mouse conectado no modo " + _dev.ConnectionMode + ".", true);
            }
            else
            {
                _connLabel.Text = "● Desconectado";
                _connLabel.ForeColor = Theme.Danger;
                _batteryLabel.Text = "";
                if (verbose)
                    Toast("Mouse não encontrado. Conecte pelo cabo USB-C ou dongle 2.4G (Bluetooth não suporta configuração).", false);
            }
        }

        private void OnConnectionLost()
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _connLabel.Text = "● Desconectado";
                    _connLabel.ForeColor = Theme.Danger;
                    _batteryLabel.Text = "";
                    Toast("Mouse desconectado — reconecto sozinho quando ele voltar.", false);
                });
            }
            catch { }
        }

        // Chega a cada leitura de status: espelha na interface o que o mouse
        // realmente tem AGORA (nível de DPI, taxa, LED, bateria) — inclusive
        // quando você troca pelos botões do próprio mouse.
        private void OnDeviceStatus()
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    var s = _dev.Status;

                    string bat = s.BatteryStatus == 1
                        ? "⚡ carregando"
                        : string.Format("🔋 {0}%", s.BatteryLevel);
                    string lvl = (s.CurrentLevel >= 0 && s.CurrentLevel < _cfg.DpiCount)
                        ? string.Format("DPI {0} (nível {1})", _cfg.DpiLevels[s.CurrentLevel], s.CurrentLevel + 1)
                        : "DPI ?";
                    string txt = string.Format("{0}   ·   {1}   ·   {2}   ·   {3}",
                        bat, lvl, RateName(s.CurrentReportRate), _dev.ConnectionMode);
                    if (_batteryLabel.Text != txt) _batteryLabel.Text = txt;

                    bool changed = false;
                    _syncing = true;
                    try
                    {
                        // nível de DPI trocado pelos botões do próprio mouse
                        if (s.CurrentLevel >= 0 && s.CurrentLevel < 6 && s.CurrentLevel < _cfg.DpiCount
                            && _cfg.DpiCurrent != s.CurrentLevel)
                        {
                            _cfg.DpiCurrent = s.CurrentLevel;
                            _dpiRadios[s.CurrentLevel].Checked = true;
                            changed = true;
                        }
                        // taxa de retorno vigente no mouse
                        if (s.CurrentReportRate != 0 && s.CurrentReportRate != _cfg.PollRate)
                        {
                            _cfg.PollRate = s.CurrentReportRate;
                            changed = true;
                        }
                        // modo de LED (o botão de LED do mouse também muda isso)
                        if (s.LightEffectMode >= 0 && s.LightEffectMode <= 10
                            && s.LightEffectMode != _cfg.LightMode)
                        {
                            _cfg.LightMode = s.LightEffectMode;
                            foreach (var r in _lightRadios)
                                if (r.Tag is int && (int)r.Tag == _cfg.LightMode) r.Checked = true;
                            changed = true;
                        }
                        RebuildRateOptions();
                    }
                    finally { _syncing = false; }
                    if (changed) _cfg.Save();
                });
            }
            catch { }
        }

        private static string RateName(int v)
        {
            switch (v)
            {
                case 8: return "125 Hz";
                case 4: return "250 Hz";
                case 2: return "500 Hz";
                case 1: return "1000 Hz";
                case 17: return "2000 Hz";
                case 18: return "4000 Hz";
                case 20: return "8000 Hz";
                default: return "? Hz";
            }
        }

        // --------------------------------------------------------- helpers
        private Label SectionAt(string text, int x, int y)
        {
            var l = Theme.MakeSection(text);
            l.Location = new Point(x, y);
            return l;
        }

        private RadioButton MakeRadio(string text, int x, int y)
        {
            var r = new RadioButton();
            r.Text = text;
            r.AutoSize = true;
            r.ForeColor = Theme.Text;
            r.Location = new Point(x, y);
            return r;
        }

        private CheckBox MakeCheck(string text, int x, int y, bool val)
        {
            var c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.ForeColor = Theme.Text;
            c.Location = new Point(x, y);
            c.Checked = val;
            return c;
        }

        private NumericUpDown MakeNum(int x, int y, int min, int max, int val)
        {
            var n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Min(Math.Max(val, min), max);
            n.Size = new Size(86, 26);
            n.BackColor = Theme.Panel;
            n.ForeColor = Theme.Text;
            n.BorderStyle = BorderStyle.FixedSingle;
            return n;
        }

        private static Color HexColor(string hex)
        {
            try
            {
                if (!string.IsNullOrEmpty(hex) && hex.StartsWith("#") && hex.Length >= 7)
                    return Color.FromArgb(
                        Convert.ToInt32(hex.Substring(1, 2), 16),
                        Convert.ToInt32(hex.Substring(3, 2), 16),
                        Convert.ToInt32(hex.Substring(5, 2), 16));
            }
            catch { }
            return Color.Red;
        }

        private static string ColorHex(Color c)
        {
            return string.Format("#{0:x2}{1:x2}{2:x2}", c.R, c.G, c.B);
        }
    }

    // ========================================================================
    //  Diálogo: atalho de teclado
    // ========================================================================
    internal class ComboKeyDialog : Form
    {
        public int MainHid;
        public int Modifiers;
        private TextBox _box;
        private CheckBox _ctrl, _shift, _alt, _win;
        private Label _preview;

        public ComboKeyDialog()
        {
            Text = "Atalho de teclado";
            ClientSize = new Size(360, 240);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;

            var lbl = new Label();
            lbl.Text = "Clique na caixa e pressione a tecla principal:";
            lbl.AutoSize = true;
            lbl.Location = new Point(16, 14);
            Controls.Add(lbl);

            _box = new TextBox();
            _box.ReadOnly = true;
            _box.BackColor = Theme.Panel;
            _box.ForeColor = Theme.Accent;
            _box.Font = new Font("Segoe UI Semibold", 11f);
            _box.Bounds = new Rectangle(16, 38, 326, 30);
            _box.TextAlign = HorizontalAlignment.Center;
            _box.TabStop = true;
            Controls.Add(_box);

            var lbl2 = new Label();
            lbl2.Text = "Modificadores:";
            lbl2.AutoSize = true;
            lbl2.Location = new Point(16, 82);
            Controls.Add(lbl2);

            _ctrl = MkChk("Ctrl", 16, 106);
            _shift = MkChk("Shift", 86, 106);
            _alt = MkChk("Alt", 166, 106);
            _win = MkChk("Win", 236, 106);

            _preview = new Label();
            _preview.AutoSize = true;
            _preview.ForeColor = Theme.SubText;
            _preview.Location = new Point(16, 140);
            Controls.Add(_preview);

            var okBtn = Theme.MakePrimary("Confirmar", 110, 32);
            okBtn.Location = new Point(126, 190);
            okBtn.Click += delegate
            {
                CollectModifiers();
                DialogResult = DialogResult.OK;
            };
            Controls.Add(okBtn);

            var cancel = Theme.MakeChip("Cancelar", 100, 32);
            cancel.Location = new Point(242, 190);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(cancel);

            KeyDown += OnKey;
            _box.KeyDown += OnKey;
        }

        private CheckBox MkChk(string t, int x, int y)
        {
            var c = new CheckBox();
            c.Text = t;
            c.AutoSize = true;
            c.ForeColor = Theme.Text;
            c.Location = new Point(x, y);
            c.CheckedChanged += delegate { UpdatePreview(); };
            Controls.Add(c);
            return c;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            var k = e.KeyCode;
            if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu ||
                k == Keys.LWin || k == Keys.RWin) return;
            int hid = KeyTables.KeysToHid(k);
            if (hid == 0 || hid >= 224) return;
            MainHid = hid;
            if (e.Control) _ctrl.Checked = true;
            if (e.Shift) _shift.Checked = true;
            if (e.Alt) _alt.Checked = true;
            UpdatePreview();
        }

        private void CollectModifiers()
        {
            Modifiers = 0;
            if (_ctrl.Checked) Modifiers |= 1;
            if (_shift.Checked) Modifiers |= 2;
            if (_alt.Checked) Modifiers |= 4;
            if (_win.Checked) Modifiers |= 8;
        }

        private void UpdatePreview()
        {
            CollectModifiers();
            var parts = new List<string>();
            if (_ctrl.Checked) parts.Add("Ctrl");
            if (_shift.Checked) parts.Add("Shift");
            if (_alt.Checked) parts.Add("Alt");
            if (_win.Checked) parts.Add("Win");
            if (MainHid > 0) parts.Add(KeyTables.HidName(MainHid));
            _box.Text = string.Join(" + ", parts.ToArray());
            _preview.Text = MainHid > 0 ? "Será gravado no mouse como atalho." : "";
        }
    }

    // ========================================================================
    //  Diálogo: tecla de fogo
    // ========================================================================
    internal class FireKeyDialog : Form
    {
        public int Interval = 50;
        public int Count;
        private NumericUpDown _interval, _count;

        public FireKeyDialog()
        {
            Text = "Tecla de fogo (cliques rápidos)";
            ClientSize = new Size(340, 200);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;

            var l1 = new Label();
            l1.Text = "Intervalo entre cliques (ms):";
            l1.AutoSize = true;
            l1.Location = new Point(16, 18);
            Controls.Add(l1);
            _interval = new NumericUpDown();
            _interval.Minimum = 5;
            _interval.Maximum = 255;
            _interval.Value = 50;
            _interval.Bounds = new Rectangle(210, 14, 90, 26);
            _interval.BackColor = Theme.Panel;
            _interval.ForeColor = Theme.Text;
            Controls.Add(_interval);

            var l2 = new Label();
            l2.Text = "Quantidade de cliques:\n(0 = repetir enquanto segurar)";
            l2.AutoSize = true;
            l2.Location = new Point(16, 56);
            Controls.Add(l2);
            _count = new NumericUpDown();
            _count.Minimum = 0;
            _count.Maximum = 255;
            _count.Value = 0;
            _count.Bounds = new Rectangle(210, 58, 90, 26);
            _count.BackColor = Theme.Panel;
            _count.ForeColor = Theme.Text;
            Controls.Add(_count);

            var okBtn = Theme.MakePrimary("Confirmar", 110, 32);
            okBtn.Location = new Point(104, 150);
            okBtn.Click += delegate
            {
                Interval = (int)_interval.Value;
                Count = (int)_count.Value;
                DialogResult = DialogResult.OK;
            };
            Controls.Add(okBtn);

            var cancel = Theme.MakeChip("Cancelar", 100, 32);
            cancel.Location = new Point(222, 150);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(cancel);
        }
    }

    // ========================================================================
    //  Diálogo: editor de macro
    // ========================================================================
    internal class MacroDialog : Form
    {
        public MacroDef Result;
        private readonly MacroDef _macro;
        private TextBox _name;
        private ListView _events;
        private Button _recBtn;
        private bool _recording;
        private long _lastTick;
        private readonly HashSet<Keys> _pressed = new HashSet<Keys>();
        private RadioButton _once, _repeat, _untilRelease;
        private NumericUpDown _repeatNum, _delayNum;
        private ComboBox _mouseCombo;

        public MacroDialog(MacroDef existing)
        {
            _macro = existing != null ? existing : new MacroDef();
            if (_macro.Events == null) _macro.Events = new List<MacroEvent>();

            Text = "Editor de macro";
            ClientSize = new Size(520, 520);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;

            var l1 = new Label();
            l1.Text = "Nome:";
            l1.AutoSize = true;
            l1.Location = new Point(16, 18);
            Controls.Add(l1);

            _name = new TextBox();
            _name.Bounds = new Rectangle(70, 14, 250, 26);
            _name.BackColor = Theme.Panel;
            _name.ForeColor = Theme.Text;
            _name.Text = _macro.Name ?? "";
            Controls.Add(_name);

            _recBtn = Theme.MakePrimary("● Gravar teclas", 150, 30);
            _recBtn.Location = new Point(340, 12);
            _recBtn.Click += delegate { ToggleRecording(); };
            Controls.Add(_recBtn);

            _events = new ListView();
            _events.Bounds = new Rectangle(16, 52, 474, 250);
            _events.View = View.Details;
            _events.FullRowSelect = true;
            _events.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _events.BackColor = Theme.Panel;
            _events.ForeColor = Theme.Text;
            _events.Columns.Add("Evento", 200);
            _events.Columns.Add("Tipo", 130);
            _events.Columns.Add("Atraso (ms)", 110);
            Controls.Add(_events);
            RefreshEvents();

            // inserções manuais
            var dl = new Label();
            dl.Text = "Atraso (ms):";
            dl.AutoSize = true;
            dl.Location = new Point(16, 314);
            Controls.Add(dl);
            _delayNum = new NumericUpDown();
            _delayNum.Minimum = 1;
            _delayNum.Maximum = 4095;
            _delayNum.Value = 100;
            _delayNum.Bounds = new Rectangle(96, 310, 76, 26);
            _delayNum.BackColor = Theme.Panel;
            _delayNum.ForeColor = Theme.Text;
            Controls.Add(_delayNum);
            var addDelay = Theme.MakeChip("+ atraso", 80, 27);
            addDelay.Location = new Point(180, 310);
            addDelay.Click += delegate
            {
                if (!CanAddEvents(1)) return;
                var ev = new MacroEvent();
                ev.Type = "delay";
                ev.Delay = (int)_delayNum.Value;
                ev.Name = "(pausa)";
                _macro.Events.Add(ev);
                RefreshEvents();
            };
            Controls.Add(addDelay);

            _mouseCombo = new ComboBox();
            _mouseCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _mouseCombo.Items.AddRange(new object[] { "Clique esq.", "Clique dir.", "Clique meio", "Avançar", "Voltar" });
            _mouseCombo.SelectedIndex = 0;
            _mouseCombo.Bounds = new Rectangle(280, 310, 120, 26);
            _mouseCombo.BackColor = Theme.Panel;
            _mouseCombo.ForeColor = Theme.Text;
            Controls.Add(_mouseCombo);
            var addClick = Theme.MakeChip("+ clique", 80, 27);
            addClick.Location = new Point(408, 310);
            addClick.Click += delegate
            {
                if (!CanAddEvents(2)) return;
                int[] hids = { 240, 241, 242, 244, 243 };   // Avançar=bit16, Voltar=bit8
                int hid = hids[_mouseCombo.SelectedIndex];
                var d1 = new MacroEvent();
                d1.Type = "keydown"; d1.Hid = hid; d1.Delay = 20; d1.Name = KeyTables.HidName(hid);
                var d2 = new MacroEvent();
                d2.Type = "keyup"; d2.Hid = hid; d2.Delay = 30; d2.Name = KeyTables.HidName(hid);
                _macro.Events.Add(d1);
                _macro.Events.Add(d2);
                RefreshEvents();
            };
            Controls.Add(addClick);

            var rmBtn = Theme.MakeChip("Remover selecionado", 150, 27);
            rmBtn.Location = new Point(16, 346);
            rmBtn.Click += delegate
            {
                if (_events.SelectedIndices.Count > 0)
                {
                    _macro.Events.RemoveAt(_events.SelectedIndices[0]);
                    RefreshEvents();
                }
            };
            Controls.Add(rmBtn);

            var clearBtn = Theme.MakeChip("Limpar tudo", 100, 27);
            clearBtn.Location = new Point(174, 346);
            clearBtn.Click += delegate { _macro.Events.Clear(); RefreshEvents(); };
            Controls.Add(clearBtn);

            // modo de execução
            var em = Theme.MakeSection("Modo de execução");
            em.Location = new Point(16, 386);
            Controls.Add(em);

            _once = new RadioButton();
            _once.Text = "Executar 1 vez";
            _once.AutoSize = true;
            _once.ForeColor = Theme.Text;
            _once.Location = new Point(16, 412);
            Controls.Add(_once);

            _repeat = new RadioButton();
            _repeat.Text = "Repetir";
            _repeat.AutoSize = true;
            _repeat.ForeColor = Theme.Text;
            _repeat.Location = new Point(146, 412);
            Controls.Add(_repeat);

            _repeatNum = new NumericUpDown();
            _repeatNum.Minimum = 1;
            _repeatNum.Maximum = 255;
            _repeatNum.Value = Math.Min(Math.Max(_macro.RepeatCount, 1), 255);
            _repeatNum.Bounds = new Rectangle(220, 410, 60, 26);
            _repeatNum.BackColor = Theme.Panel;
            _repeatNum.ForeColor = Theme.Text;
            Controls.Add(_repeatNum);

            var rl = new Label();
            rl.Text = "vezes";
            rl.AutoSize = true;
            rl.Location = new Point(284, 414);
            Controls.Add(rl);

            _untilRelease = new RadioButton();
            _untilRelease.Text = "Repetir até soltar o botão";
            _untilRelease.AutoSize = true;
            _untilRelease.ForeColor = Theme.Text;
            _untilRelease.Location = new Point(340, 412);
            Controls.Add(_untilRelease);

            if (_macro.ExecType == 2) _repeat.Checked = true;
            else if (_macro.ExecType == 4) _untilRelease.Checked = true;
            else _once.Checked = true;

            var okBtn = Theme.MakePrimary("Salvar macro", 130, 34);
            okBtn.Location = new Point(250, 468);
            okBtn.Click += delegate { SaveAndClose(); };
            Controls.Add(okBtn);

            var cancel = Theme.MakeChip("Cancelar", 100, 34);
            cancel.Location = new Point(390, 468);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(cancel);

            KeyDown += RecKeyDown;
            KeyUp += RecKeyUp;
            FormClosing += delegate { _recording = false; };
        }

        // 15 pacotes × 25 bytes = 375 bytes = 125 eventos no máximo (nibble de 4 bits)
        private bool CanAddEvents(int n)
        {
            if (_macro.Events.Count + n <= 120) return true;
            MessageBox.Show(this, "Limite de 120 eventos por macro atingido.", "Macro cheia",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        private void ToggleRecording()
        {
            _recording = !_recording;
            _recBtn.Text = _recording ? "■ Parar gravação" : "● Gravar teclas";
            _recBtn.BackColor = _recording ? Theme.Danger : Theme.AccentDark;
            if (_recording)
            {
                _lastTick = Environment.TickCount;
                _pressed.Clear();
                Focus();
            }
        }

        private int TakeDelay()
        {
            long now = Environment.TickCount;
            int d = (int)(now - _lastTick);
            _lastTick = now;
            if (_macro.Events.Count == 0) return 0;
            if (d < 0) d = 0;
            if (d > 4095) d = 4095;
            return d;
        }

        private void RecKeyDown(object sender, KeyEventArgs e)
        {
            if (!_recording) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (_pressed.Contains(e.KeyCode)) return; // repetição automática
            if (_macro.Events.Count >= 120) { ToggleRecording(); return; }
            int hid = KeyTables.KeysToHid(e.KeyCode);
            if (hid == 0) return;
            _pressed.Add(e.KeyCode);
            var ev = new MacroEvent();
            ev.Type = "keydown";
            ev.Hid = hid;
            ev.Delay = TakeDelay();
            ev.Name = KeyTables.HidName(hid);
            _macro.Events.Add(ev);
            RefreshEvents();
        }

        private void RecKeyUp(object sender, KeyEventArgs e)
        {
            if (!_recording) return;
            e.Handled = true;
            _pressed.Remove(e.KeyCode);
            int hid = KeyTables.KeysToHid(e.KeyCode);
            if (hid == 0) return;
            if (_macro.Events.Count >= 120) { if (_recording) ToggleRecording(); return; }
            var ev = new MacroEvent();
            ev.Type = "keyup";
            ev.Hid = hid;
            ev.Delay = TakeDelay();
            ev.Name = KeyTables.HidName(hid);
            _macro.Events.Add(ev);
            RefreshEvents();
        }

        private void RefreshEvents()
        {
            _events.BeginUpdate();
            _events.Items.Clear();
            foreach (var ev in _macro.Events)
            {
                string tipo = ev.Type == "keydown" ? "pressionar" : ev.Type == "keyup" ? "soltar" : "pausa";
                var item = new ListViewItem(ev.Name ?? "");
                item.SubItems.Add(tipo);
                item.SubItems.Add(ev.Delay.ToString());
                _events.Items.Add(item);
            }
            if (_events.Items.Count > 0)
                _events.EnsureVisible(_events.Items.Count - 1);
            _events.EndUpdate();
        }

        private void SaveAndClose()
        {
            if (_macro.Events.Count == 0)
            {
                MessageBox.Show(this, "Grave ou insira pelo menos um evento.", "Macro vazia",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string nm = _name.Text.Trim();
            if (nm.Length == 0) nm = "Macro " + DateTime.Now.ToString("HH:mm:ss");
            _macro.Name = nm;
            _macro.ExecType = _repeat.Checked ? 2 : _untilRelease.Checked ? 4 : 1;
            _macro.RepeatCount = (int)_repeatNum.Value;
            Result = _macro;
            DialogResult = DialogResult.OK;
        }
    }

    // ========================================================================
    //  Diálogo: entrada de texto simples
    // ========================================================================
    internal class InputDialog : Form
    {
        public string Value = "";
        private TextBox _box;

        public InputDialog(string title, string prompt)
        {
            Text = title;
            ClientSize = new Size(360, 130);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;

            var lbl = new Label();
            lbl.Text = prompt;
            lbl.AutoSize = true;
            lbl.Location = new Point(16, 12);
            Controls.Add(lbl);

            _box = new TextBox();
            _box.Bounds = new Rectangle(16, 40, 326, 26);
            _box.BackColor = Theme.Panel;
            _box.ForeColor = Theme.Text;
            Controls.Add(_box);

            var ok = Theme.MakePrimary("OK", 90, 30);
            ok.Location = new Point(154, 84);
            ok.Click += delegate { Value = _box.Text; DialogResult = DialogResult.OK; };
            Controls.Add(ok);

            var cancel = Theme.MakeChip("Cancelar", 90, 30);
            cancel.Location = new Point(252, 84);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    // ========================================================================
    //  Entry point
    // ========================================================================
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] argv)
        {
            // Modo utilitário: regrava a config salva no mouse e sai. Útil para
            // restaurar o mouse sem abrir a janela (atalho, script, tarefa).
            foreach (var a in argv)
            {
                if (a == "--reapply" || a == "-r")
                {
                    ReapplyHeadless();
                    return;
                }
            }
            bool created;
            using (var mutex = new Mutex(true, "SM809LocalDriver", out created))
            {
                if (!created)
                {
                    MessageBox.Show("O SM809 Driver Local já está aberto.", "SM809",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }

        private static void ReapplyHeadless()
        {
            var cfg = LocalConfig.Load();
            using (var dev = new Sm809Device())
            {
                if (!dev.Connect())
                {
                    Console.WriteLine("Mouse nao encontrado (use cabo USB-C ou dongle 2.4G).");
                    return;
                }
                Thread.Sleep(400);   // deixa chegar o primeiro status (tipo de sensor)
                var codes = new byte[16][];
                for (int i = 0; i < 16; i++)
                {
                    var bc = cfg.Buttons[i].Code;
                    codes[i] = (bc != null && bc.Length >= 3)
                        ? new byte[] { (byte)bc[0], (byte)bc[1], (byte)bc[2] }
                        : new byte[] { 0xFF, 0xFF, 0xFF };
                }
                Console.WriteLine("botoes: " + (dev.ApplyButtons(codes) ? "ok" : "FALHOU"));
                Console.WriteLine("dpi:    " + (dev.ApplyDpi(cfg.DpiLevels, cfg.DpiColors, cfg.DpiCount,
                    cfg.DpiCurrent, dev.Status.SensorType) ? "ok" : "FALHOU"));
                Console.WriteLine("taxa:   " + (dev.ApplyReportRate(cfg.PollRate) ? "ok" : "FALHOU"));
                Console.WriteLine("lod:    " + (dev.ApplyLod(cfg.Lod) ? "ok" : "FALHOU"));
                Console.WriteLine("energia:" + (dev.ApplyPower(cfg.SleepTime, cfg.MoveWakeup, cfg.MoveLighting) ? "ok" : "FALHOU"));
                Console.WriteLine("sensor: " + (dev.ApplySensorAdvanced(cfg.AngleSnap, cfg.MotionSync, cfg.RippleControl) ? "ok" : "FALHOU"));
                Console.WriteLine("led:    " + (dev.ApplyLighting(cfg.LightMode) ? "ok" : "FALHOU"));
            }
        }
    }
}
