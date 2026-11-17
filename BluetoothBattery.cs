using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SM809
{
    internal sealed class BtNode
    {
        public string InstanceId;
        public string Name;
        public int Battery = -1;
        public bool Connected;
    }

    internal sealed class BtBattery
    {
        public string Address;
        public string Name;
        public int Percent;
    }

    // Lê a bateria que o próprio Windows recebe dos fones pelo HFP (a mesma das Configurações).
    // Só consulta propriedades guardadas pelo Windows: nada é enviado aos dispositivos.
    internal static class BluetoothBattery
    {
        private static readonly DEVPROPKEY BatteryKey = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        private static readonly DEVPROPKEY ConnectedKey = new DEVPROPKEY("83DA6326-97A6-4088-9453-A1923F573B29", 15);
        private static readonly DEVPROPKEY NameKey = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        private const int DIGCF_PRESENT = 0x02;
        private const int DIGCF_ALLCLASSES = 0x04;

        // "BTHENUM\DEV_<end>\..." ou "BTHENUM\{serviço}_...\...&<end>_C00000000"
        public static string AddressOf(string instanceId)
        {
            if (instanceId == null) return null;
            string[] parts = instanceId.Split('\\');
            if (parts.Length < 3) return null;
            string candidate;
            if (IsDeviceNode(instanceId))
                candidate = parts[1].Substring(4);
            else
            {
                string last = parts[2];
                int amp = last.LastIndexOf('&');
                int us = last.IndexOf('_', amp + 1);
                if (amp < 0 || us < 0) return null;
                candidate = last.Substring(amp + 1, us - amp - 1);
            }
            if (candidate.Length != 12) return null;
            foreach (char c in candidate)
                if (!Uri.IsHexDigit(c)) return null;
            return candidate.ToUpperInvariant();
        }

        private static bool IsDeviceNode(string instanceId)
        {
            return instanceId.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase);
        }

        // A bateria fica no nó do serviço Hands-Free; nome e "conectado", no nó DEV_ do mesmo endereço.
        // O Windows guarda o último valor de fones desligados, por isso a conexão é conferida.
        public static List<BtBattery> Select(IList<BtNode> nodes)
        {
            var devices = new Dictionary<string, BtNode>();
            foreach (var n in nodes)
            {
                string a = AddressOf(n.InstanceId);
                if (a != null && IsDeviceNode(n.InstanceId)) devices[a] = n;
            }
            var seen = new HashSet<string>();
            var list = new List<BtBattery>();
            foreach (var n in nodes)
            {
                if (n.Battery < 0) continue;
                string a = AddressOf(n.InstanceId);
                BtNode dev;
                if (a == null || !seen.Add(a) || !devices.TryGetValue(a, out dev) || !dev.Connected) continue;
                var b = new BtBattery();
                b.Address = a;
                b.Name = string.IsNullOrEmpty(dev.Name) ? "Fone Bluetooth" : dev.Name;
                b.Percent = Math.Min(100, n.Battery);
                list.Add(b);
            }
            list.Sort(delegate(BtBattery x, BtBattery y) { return string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase); });
            return list;
        }

        public static List<BtBattery> ReadConnected()
        {
            try { return Select(ReadNodes()); }
            catch (Exception) { return new List<BtBattery>(); }
        }

        private static List<BtNode> ReadNodes()
        {
            var nodes = new List<BtNode>();
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, "BTHENUM", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == new IntPtr(-1)) return nodes;
            try
            {
                var data = new SP_DEVINFO_DATA();
                data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                var id = new StringBuilder(512);
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    int required;
                    if (!SetupDiGetDeviceInstanceId(set, ref data, id, id.Capacity, out required)) continue;
                    var n = new BtNode();
                    n.InstanceId = id.ToString();
                    byte[] v = Property(set, ref data, BatteryKey);
                    if (v != null && v.Length >= 1) n.Battery = v[0];
                    v = Property(set, ref data, ConnectedKey);
                    n.Connected = v != null && v.Length >= 1 && v[0] != 0;
                    v = Property(set, ref data, NameKey);
                    if (v != null) n.Name = Encoding.Unicode.GetString(v).TrimEnd('\0');
                    nodes.Add(n);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return nodes;
        }

        private static byte[] Property(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
        {
            int type, size;
            SetupDiGetDevicePropertyW(set, ref data, ref key, out type, null, 0, out size, 0);
            if (size <= 0) return null;
            var buffer = new byte[size];
            return SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buffer, size, out size, 0) ? buffer : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid Fmtid;
            public int Pid;

            public DEVPROPKEY(string fmtid, int pid)
            {
                Fmtid = new Guid(fmtid);
                Pid = pid;
            }
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
            out int type, byte[] buffer, int size, out int required, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
