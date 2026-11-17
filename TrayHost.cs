using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SM809
{
    internal enum BatteryTone { Normal, Warning, Critical }

    // Semântica do status confirmada no driver oficial: linkStatus 0 = offline;
    // batteryStatus 1 = carregando (porcentagem não confiável), 2 = carga completa.
    internal sealed class BatteryView
    {
        public const int WarnAt = 20;
        public const int CriticalAt = 10;
        public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

        public string Text;
        public BatteryTone Tone;
        public bool Dim;
        public bool Charging;
        public string Tooltip;

        public static BatteryView From(bool connected, DeviceStatus s, TimeSpan sinceLastReport,
                                       int lastKnownPercent, string detail)
        {
            var v = new BatteryView();
            if (!connected || s == null || !s.Valid)
            {
                v.Text = "—";
                v.Dim = true;
                v.Tooltip = connected ? "SM809 PRO — aguardando leitura" : "SM809 PRO — desconectado";
                return v;
            }
            if (s.LinkStatus == 0 || sinceLastReport > StaleAfter)
            {
                v.Dim = true;
                v.Text = lastKnownPercent >= 0 ? lastKnownPercent + "%" : "—";
                v.Tooltip = lastKnownPercent >= 0
                    ? "SM809 PRO — dormindo (último: " + lastKnownPercent + "%)"
                    : "SM809 PRO — dormindo";
                return v;
            }
            if (s.BatteryStatus == 1)
            {
                v.Text = "";
                v.Charging = true;
                v.Tooltip = "SM809 PRO — carregando · " + detail;
                return v;
            }
            if (s.BatteryStatus == 2)
            {
                v.Text = "100%";
                v.Charging = true;
                v.Tooltip = "SM809 PRO — carga completa · " + detail;
                return v;
            }
            int p = Clamp(s.BatteryLevel);
            v.Text = p + "%";
            v.Tone = p <= CriticalAt ? BatteryTone.Critical : p <= WarnAt ? BatteryTone.Warning : BatteryTone.Normal;
            v.Tooltip = "SM809 PRO — " + p + "% · " + detail;
            return v;
        }

        public static int NextKnownPercent(int previous, DeviceStatus s)
        {
            if (s == null || !s.Valid || s.LinkStatus == 0 || s.BatteryStatus == 1) return previous;
            return s.BatteryStatus == 2 ? 100 : Clamp(s.BatteryLevel);
        }

        public static int Clamp(int level)
        {
            return Math.Max(0, Math.Min(100, level));
        }
    }

    // Uma notificação por descarga: rearma só acima de 20% ou ao carregar.
    internal sealed class AlertLatch
    {
        private bool _armed = true;

        public bool Update(bool online, int batteryStatus, int percent)
        {
            if (!online) return false;
            if (batteryStatus == 1 || batteryStatus == 2 || percent > BatteryView.WarnAt)
            {
                _armed = true;
                return false;
            }
            if (!_armed || percent > BatteryView.CriticalAt) return false;
            _armed = false;
            return true;
        }
    }

    internal sealed class AutoStart
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ValueName = "SM809Driver";
        public static readonly AutoStart Default = new AutoStart(RunKey, ValueName);

        private readonly string _key;
        private readonly string _name;

        public AutoStart(string key, string name)
        {
            _key = key;
            _name = name;
        }

        public static string CommandFor(string exePath)
        {
            return "\"" + exePath + "\" --tray";
        }

        public static string ExeOf(string command)
        {
            if (command == null) return null;
            int a = command.IndexOf('"');
            int b = a < 0 ? -1 : command.IndexOf('"', a + 1);
            return b > a ? command.Substring(a + 1, b - a - 1) : command.Trim();
        }

        // Só corrige quando o exe gravado não existe mais: outro executável (ex.: uma
        // cópia em outra pasta) nunca sequestra a inicialização.
        public static bool NeedsFix(string stored, string exePath, Predicate<string> fileExists)
        {
            if (stored == null || string.Equals(stored, CommandFor(exePath), StringComparison.OrdinalIgnoreCase))
                return false;
            return !fileExists(ExeOf(stored));
        }

        public string Read()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(_key))
                return k == null ? null : k.GetValue(_name) as string;
        }

        public bool IsEnabled
        {
            get { return Read() != null; }
        }

        public void Enable(string exePath)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(_key))
                k.SetValue(_name, CommandFor(exePath), RegistryValueKind.String);
        }

        public void Disable()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(_key, true))
                if (k != null) k.DeleteValue(_name, false);
        }

        public void FixPathIfMoved(string exePath)
        {
            try
            {
                if (NeedsFix(Read(), exePath, File.Exists)) Enable(exePath);
            }
            catch (Exception) { }
        }
    }
}
