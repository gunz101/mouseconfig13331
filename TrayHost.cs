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

    internal static class AppIcon
    {
        public static Icon Load(Size size)
        {
            try
            {
                using (var s = typeof(AppIcon).Assembly.GetManifestResourceStream("SM809.app.ico"))
                    return s == null ? null : new Icon(s, size);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Theme.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, e.ImageRectangle, Theme.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private sealed class DarkMenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemBorder { get { return Theme.ChipHover; } }
            public override Color MenuItemSelected { get { return Theme.ChipHover; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Border; } }
            public override Color CheckBackground { get { return Theme.Panel; } }
            public override Color CheckSelectedBackground { get { return Theme.ChipHover; } }
            public override Color CheckPressedBackground { get { return Theme.ChipHover; } }
        }
    }

    // Processo residente: dono da conexão com o mouse, da bandeja e da tira. A janela
    // de configuração é criada sob demanda e o X só a esconde.
    internal sealed class TrayHost : ApplicationContext
    {
        private const int ForegroundPollMs = 1500;
        private const int BackgroundPollMs = 15000;

        private readonly Sm809Device _dev = new Sm809Device();
        private readonly Control _ui = new Control();
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _reconnect = new System.Windows.Forms.Timer();
        private readonly TaskbarBattery _strip = new TaskbarBattery();
        private readonly Icon _appIcon;
        private MainForm _form;
        private DateTime _lastReport = DateTime.MinValue;
        private int _lastKnownPercent = -1;
        private int[] _dpiLevels;
        private int _dpiCount;
        private DateTime _cfgStamp;
        private readonly AlertLatch _alert = new AlertLatch();
        private readonly ToolStripMenuItem _autoStartItem;
        private bool _stripShowing = true;
        private string _iconKey = "app";
        private IntPtr _numberIcon;

        public TrayHost(bool showWindow)
        {
            GC.KeepAlive(_ui.Handle);
            _appIcon = AppIcon.Load(SystemInformation.SmallIconSize) ?? SystemIcons.Application;

            var open = new ToolStripMenuItem("Abrir configurações", null, delegate { ShowMainWindow(); });
            open.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _menu.Renderer = new DarkMenuRenderer();
            _menu.Font = new Font("Segoe UI", 9f);
            _autoStartItem = new ToolStripMenuItem("Iniciar com o Windows", null, delegate { ToggleAutoStart(); });
            _menu.Items.Add(open);
            _menu.Items.Add(_autoStartItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Opening += delegate { _autoStartItem.Checked = AutoStart.Default.IsEnabled; };
            _menu.Items.Add(new ToolStripMenuItem("Sair", null, delegate { Exit(); }));

            _tray.Icon = _appIcon;
            _tray.Text = "SM809 PRO";
            _tray.ContextMenuStrip = _menu;
            _tray.MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowMainWindow();
            };
            _tray.Visible = true;

            _dev.StatusChanged += delegate { OnUi(OnStatus); };
            _dev.ConnectionLost += delegate { OnUi(Refresh); };

            _strip.Clicked += delegate { OnUi(ShowMainWindow); };
            _strip.RightClicked += delegate { OnUi(ShowMenuAtCursor); };
            _strip.AvailabilityChanged += delegate(bool ok) { OnUi(delegate { OnStripAvailability(ok); }); };
            _strip.Start();

            _poll.Interval = BackgroundPollMs;
            _poll.Tick += delegate
            {
                if (_dev.IsConnected) _dev.TriggerStatus();
                Refresh();
            };
            _poll.Start();
            _reconnect.Interval = 5000;
            _reconnect.Tick += delegate
            {
                if (!_dev.IsConnected && _dev.Connect()) Refresh();
            };
            _reconnect.Start();

            AutoStart.Default.FixPathIfMoved(Application.ExecutablePath);
            _dev.Connect();
            Refresh();
            if (showWindow) ShowMainWindow();
        }

        internal void ShowMainWindow()
        {
            if (_form == null || _form.IsDisposed)
            {
                _form = new MainForm(_dev);
                _form.VisibleChanged += delegate
                {
                    _poll.Interval = _form.Visible ? ForegroundPollMs : BackgroundPollMs;
                };
            }
            if (!_form.Visible) _form.Show();
            if (_form.WindowState == FormWindowState.Minimized) Win32.ShowWindow(_form.Handle, Win32.SW_RESTORE);
            _form.Activate();
            if (_dev.IsConnected) _dev.TriggerStatus();
        }

        internal void Exit()
        {
            _poll.Stop();
            _reconnect.Stop();
            _strip.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            if (_numberIcon != IntPtr.Zero) Win32.DestroyIcon(_numberIcon);
            if (_form != null && !_form.IsDisposed) _form.CloseForExit();
            _dev.Dispose();
            _ui.Dispose();
            ExitThread();
        }

        internal void ListenForShowRequests(EventWaitHandle signal)
        {
            var t = new Thread(delegate()
            {
                try
                {
                    while (signal.WaitOne()) OnUi(ShowMainWindow);
                }
                catch (ObjectDisposedException) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ShowMenuAtCursor()
        {
            _menu.Show(Cursor.Position);
            Win32.SetForegroundWindow(_menu.Handle);
        }

        internal static void ShowLowBattery(NotifyIcon icon, int percent)
        {
            icon.ShowBalloonTip(10000, "Bateria do mouse em " + percent + "%",
                "Conecte o cabo USB-C para não ficar sem mouse no meio do jogo.", ToolTipIcon.Warning);
        }

        private void OnStripAvailability(bool ok)
        {
            _stripShowing = ok;
            Refresh();
        }

        private void ToggleAutoStart()
        {
            try
            {
                if (AutoStart.Default.IsEnabled) AutoStart.Default.Disable();
                else AutoStart.Default.Enable(Application.ExecutablePath);
            }
            catch (Exception ex)
            {
                _tray.ShowBalloonTip(5000, "Não foi possível alterar a inicialização", ex.Message, ToolTipIcon.Error);
            }
            _autoStartItem.Checked = AutoStart.Default.IsEnabled;
        }

        // Plano B (barra vertical ou Explorer fora do ar): o número vai para o ícone da bandeja.
        private void UpdateTrayIcon(BatteryView v)
        {
            string key = _stripShowing ? "app" : v.Text + "|" + v.Tone + "|" + v.Dim + "|" + v.Charging;
            if (key == _iconKey) return;
            _iconKey = key;
            IntPtr old = _numberIcon;
            if (_stripShowing)
            {
                _tray.Icon = _appIcon;
                _numberIcon = IntPtr.Zero;
            }
            else
            {
                _numberIcon = NumberIcon(v);
                _tray.Icon = Icon.FromHandle(_numberIcon);
            }
            if (old != IntPtr.Zero) Win32.DestroyIcon(old);
        }

        private static IntPtr NumberIcon(BatteryView v)
        {
            string text = v.Charging ? "⚡" : v.Text.Replace("%", "");
            Color fg = v.Tone == BatteryTone.Critical ? Color.FromArgb(255, 92, 92)
                     : v.Tone == BatteryTone.Warning ? Color.FromArgb(255, 164, 58)
                     : v.Dim ? Theme.SubText : Color.White;
            using (var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var bg = new SolidBrush(Theme.Back))
            using (var brush = new SolidBrush(fg))
            using (var font = new Font(v.Charging ? "Segoe UI Symbol" : "Segoe UI", text.Length >= 3 ? 7f : 9f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fmt = new StringFormat())
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.FillRectangle(bg, 0, 0, 16, 16);
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Center;
                g.DrawString(text, font, brush, new RectangleF(0, 0, 16, 16), fmt);
                return bmp.GetHicon();
            }
        }

        private void OnUi(MethodInvoker action)
        {
            try
            {
                if (!_ui.IsDisposed) _ui.BeginInvoke(action);
            }
            catch (InvalidOperationException) { }
        }

        private void OnStatus()
        {
            _lastReport = DateTime.UtcNow;
            _lastKnownPercent = BatteryView.NextKnownPercent(_lastKnownPercent, _dev.Status);
            Refresh();
        }

        private void Refresh()
        {
            var s = _dev.Status;
            TimeSpan since = _lastReport == DateTime.MinValue ? TimeSpan.Zero : DateTime.UtcNow - _lastReport;
            var v = BatteryView.From(_dev.IsConnected, s, since, _lastKnownPercent, Detail(s));
            var item = new StripItem();
            item.Glyph = TaskbarBattery.MouseGlyph;
            item.Text = v.Text;
            item.Tone = v.Tone;
            item.Dim = v.Dim;
            item.Charging = v.Charging;
            _strip.SetItems(new[] { item }, v.Tooltip);
            _tray.Text = v.Tooltip.Length > 63 ? v.Tooltip.Substring(0, 63) : v.Tooltip;
            UpdateTrayIcon(v);
            bool online = _dev.IsConnected && s.Valid && s.LinkStatus != 0 && since <= BatteryView.StaleAfter;
            int percent = BatteryView.Clamp(s.BatteryLevel);
            if (_alert.Update(online, s.BatteryStatus, percent)) ShowLowBattery(_tray, percent);
        }

        private string Detail(DeviceStatus s)
        {
            LoadDpiLevels();
            bool known = _dpiLevels != null && s.CurrentLevel >= 0 && s.CurrentLevel < _dpiCount && s.CurrentLevel < _dpiLevels.Length;
            return _dev.ConnectionMode + " · " + (known ? "DPI " + _dpiLevels[s.CurrentLevel] : "DPI ?");
        }

        private void LoadDpiLevels()
        {
            try
            {
                DateTime stamp = File.Exists(LocalConfig.ConfigPath) ? File.GetLastWriteTimeUtc(LocalConfig.ConfigPath) : DateTime.MinValue;
                if (_dpiLevels != null && stamp == _cfgStamp) return;
                var cfg = LocalConfig.Load();
                _dpiLevels = cfg.DpiLevels;
                _dpiCount = cfg.DpiCount;
                _cfgStamp = stamp;
            }
            catch (Exception) { }
        }
    }
}
