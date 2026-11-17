using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SM809
{
    internal sealed class StripItem
    {
        public string Glyph;
        public string Text;
        public bool Charging;
        public bool Dim;
        public BatteryTone Tone;
    }

    // Janela layered filha da Shell_TrayWnd. Como é filha de uma janela do Explorer, as
    // filas de entrada ficam ligadas: esta thread nunca bloqueia nem faz I/O, senão
    // trava a barra de tarefas. Por isso a tira roda numa thread própria, DPI-aware
    // como a barra.
    internal sealed class TaskbarBattery : IDisposable
    {
        public const string WindowTitle = "SM809BatteryStrip";
        public const string MouseGlyph = "";
        private const string BoltGlyph = "";
        private const int WmItems = 0x8001;
        private const int WmStop = 0x8002;

        public event Action Clicked;
        public event Action RightClicked;
        public event Action<bool> AvailabilityChanged;

        private readonly object _lock = new object();
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private List<StripItem> _items = new List<StripItem>();
        private string _tooltip = "";
        private Thread _thread;

        private ControlWindow _control;
        private StripWindow _strip;
        private IntPtr _taskbar;
        private IntPtr _tip;
        private Size _size;
        private string _painted;
        private bool _reported;
        private bool _available;

        public void Start()
        {
            _thread = new Thread(ThreadMain);
            _thread.IsBackground = true;
            _thread.Name = "SM809 taskbar strip";
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void SetItems(IList<StripItem> items, string tooltip)
        {
            lock (_lock)
            {
                _items = new List<StripItem>(items);
                _tooltip = tooltip ?? "";
            }
            Post(WmItems);
        }

        public void Dispose()
        {
            if (_thread == null) return;
            if (_ready.WaitOne(2000)) Post(WmStop);
            _thread.Join(2000);
            _thread = null;
        }

        internal static Point PlaceLeftOf(Rectangle taskbar, Rectangle tray, Size strip, int gap)
        {
            int x = tray.Left - taskbar.Left - gap - strip.Width;
            int y = (taskbar.Height - strip.Height) / 2;
            return new Point(Math.Max(0, x), Math.Max(0, y));
        }

        internal static Bitmap Render(IList<StripItem> items, bool light, int dpi, int height)
        {
            float s = dpi / 96f;
            Color theme = light ? Color.FromArgb(27, 27, 27) : Color.White;
            int pad = Px(6, s), iconW = Px(18, s), boltW = Px(9, s), gap = Px(4, s), between = Px(12, s);
            using (var icons = IconFont(16 * s))
            using (var bolt = IconFont(10 * s))
            using (var text = new Font("Segoe UI", 13 * s, GraphicsUnit.Pixel))
            using (var typo = StringFormat.GenericTypographic)
            using (var center = (StringFormat)typo.Clone())
            using (var left = (StringFormat)typo.Clone())
            using (var probe = new Bitmap(1, 1))
            using (var pg = Graphics.FromImage(probe))
            {
                center.Alignment = StringAlignment.Center;
                center.LineAlignment = StringAlignment.Center;
                left.LineAlignment = StringAlignment.Center;

                var widths = new int[items.Count];
                int total = pad * 2;
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    int w = icons != null ? iconW : 0;
                    if (it.Charging && bolt != null) w += boltW;
                    if (!string.IsNullOrEmpty(it.Text))
                        w += gap + (int)Math.Ceiling(pg.MeasureString(it.Text, text, PointF.Empty, typo).Width);
                    widths[i] = w;
                    total += w + (i > 0 ? between : 0);
                }

                var bmp = new Bitmap(Math.Max(total, 1), Math.Max(height, 1), PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    // alfa 1 (invisível) em vez de 0: pixel com alfa 0 não recebe clique
                    g.Clear(Color.FromArgb(1, 0, 0, 0));
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    float x = pad;
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        float start = x;
                        int alpha = it.Dim ? 128 : 255;
                        using (var iconBrush = new SolidBrush(Color.FromArgb(alpha, theme)))
                        using (var textBrush = new SolidBrush(Color.FromArgb(alpha, ToneColor(it.Tone, light, theme))))
                        {
                            if (icons != null)
                            {
                                g.DrawString(it.Glyph, icons, iconBrush, new RectangleF(x, 0, iconW, height), center);
                                x += iconW;
                            }
                            if (it.Charging && bolt != null)
                            {
                                g.DrawString(BoltGlyph, bolt, iconBrush, new RectangleF(x - Px(2, s), 0, boltW, height), center);
                                x += boltW;
                            }
                            if (!string.IsNullOrEmpty(it.Text))
                                g.DrawString(it.Text, text, textBrush, new RectangleF(x + gap, 0, widths[i], height), left);
                        }
                        x = start + widths[i] + between;
                    }
                }
                return bmp;
            }
        }

        private static int Px(float value, float scale)
        {
            return (int)Math.Round(value * scale);
        }

        private static Color ToneColor(BatteryTone tone, bool light, Color theme)
        {
            if (tone == BatteryTone.Critical) return light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 92, 92);
            if (tone == BatteryTone.Warning) return light ? Color.FromArgb(184, 92, 0) : Color.FromArgb(255, 164, 58);
            return theme;
        }

        internal static Font IconFont(float px)
        {
            foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                var f = new Font(name, px, GraphicsUnit.Pixel);
                if (f.Name == name) return f;
                f.Dispose();
            }
            return null;
        }

        private static bool LightTheme()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                return v is int && (int)v == 1;
            }
        }

        private void Post(int msg)
        {
            var c = _control;
            if (c != null && c.Handle != IntPtr.Zero) Win32.PostMessage(c.Handle, msg, IntPtr.Zero, IntPtr.Zero);
        }

        private void ThreadMain()
        {
            try { Win32.SetThreadDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { }
            var icc = new Win32.INITCOMMONCONTROLSEX();
            icc.dwSize = Marshal.SizeOf(typeof(Win32.INITCOMMONCONTROLSEX));
            icc.dwICC = 0xFF;
            Win32.InitCommonControlsEx(ref icc);

            _control = new ControlWindow(this);
            _ready.Set();
            using (var timer = new System.Windows.Forms.Timer())
            {
                timer.Interval = 1000;
                timer.Tick += delegate { Tick(); };
                timer.Start();
                Tick();
                Application.Run();
            }
            DestroyStrip();
            _control.DestroyHandle();
        }

        private void Tick()
        {
            try
            {
                if (_strip == null || _strip.Handle == IntPtr.Zero || !Win32.IsWindow(_taskbar))
                {
                    DestroyStrip();
                    if (!CreateStrip())
                    {
                        Report(false);
                        return;
                    }
                }
                Repaint();
                Reposition();
                Report(true);
            }
            catch (Exception)
            {
                DestroyStrip();
                Report(false);
            }
        }

        private void OnItems()
        {
            if (_strip == null) return;
            try
            {
                Repaint();
                Reposition();
            }
            catch (Exception)
            {
                DestroyStrip();
                Report(false);
            }
        }

        private void Report(bool ok)
        {
            if (_reported && ok == _available) return;
            _reported = true;
            _available = ok;
            var ev = AvailabilityChanged;
            if (ev != null) ev(ok);
        }

        private bool CreateStrip()
        {
            _taskbar = Win32.FindWindow("Shell_TrayWnd", null);
            if (_taskbar == IntPtr.Zero) return false;
            Win32.RECT tb;
            if (!Win32.GetWindowRect(_taskbar, out tb) || tb.Bottom - tb.Top > tb.Right - tb.Left) return false;
            if (Win32.FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null) == IntPtr.Zero) return false;

            var cp = new CreateParams();
            cp.Caption = WindowTitle;
            cp.Parent = _taskbar;
            cp.Style = Win32.WS_CHILD | Win32.WS_CLIPSIBLINGS;
            cp.ExStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE;
            _strip = new StripWindow(this);
            _strip.CreateHandle(cp);

            _tip = Win32.CreateWindowEx(Win32.WS_EX_TOPMOST, "tooltips_class32", null,
                Win32.WS_POPUP | Win32.TTS_ALWAYSTIP | Win32.TTS_NOPREFIX,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_tip != IntPtr.Zero)
            {
                var ti = ToolInfo("");
                Win32.SendMessage(_tip, Win32.TTM_ADDTOOLW, IntPtr.Zero, ref ti);
            }
            _painted = null;
            return true;
        }

        private void DestroyStrip()
        {
            if (_tip != IntPtr.Zero)
            {
                Win32.DestroyWindow(_tip);
                _tip = IntPtr.Zero;
            }
            if (_strip != null)
            {
                if (_strip.Handle != IntPtr.Zero) _strip.DestroyHandle();
                _strip = null;
            }
            _painted = null;
        }

        private void Repaint()
        {
            List<StripItem> items;
            string tip;
            lock (_lock)
            {
                items = _items;
                tip = _tooltip;
            }
            Win32.RECT tb;
            Win32.GetWindowRect(_taskbar, out tb);
            int dpi = TaskbarDpi();
            bool light = LightTheme();
            string key = Describe(items) + "|" + tip + "|" + dpi + "|" + light + "|" + (tb.Bottom - tb.Top);
            if (key == _painted) return;
            using (var bmp = Render(items, light, dpi, tb.Bottom - tb.Top))
            {
                Push(bmp);
                _size = bmp.Size;
            }
            if (_tip != IntPtr.Zero)
            {
                var ti = ToolInfo(tip);
                Win32.SendMessage(_tip, Win32.TTM_UPDATETIPTEXTW, IntPtr.Zero, ref ti);
            }
            _painted = key;
        }

        private static string Describe(List<StripItem> items)
        {
            var sb = new StringBuilder();
            foreach (var it in items)
                sb.Append(it.Glyph).Append(it.Text).Append(it.Charging).Append(it.Dim).Append(it.Tone).Append(';');
            return sb.ToString();
        }

        private void Push(Bitmap bmp)
        {
            IntPtr screen = Win32.GetDC(IntPtr.Zero);
            IntPtr mem = Win32.CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Win32.SelectObject(mem, hbmp);
            try
            {
                var size = new Win32.SIZE { Cx = bmp.Width, Cy = bmp.Height };
                var src = new Win32.POINT();
                var blend = new Win32.BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
                Win32.UpdateLayeredWindow(_strip.Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, Win32.ULW_ALPHA);
            }
            finally
            {
                Win32.SelectObject(mem, old);
                Win32.DeleteObject(hbmp);
                Win32.DeleteDC(mem);
                Win32.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // A camada XAML da taskbar do Windows 11 cobre a barra inteira: HWND_TOP (Zero)
        // a cada reposição mantém a tira acima dela.
        private void Reposition()
        {
            IntPtr tray = Win32.FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            Win32.RECT tb, tr;
            if (tray == IntPtr.Zero || !Win32.GetWindowRect(_taskbar, out tb) || !Win32.GetWindowRect(tray, out tr)) return;
            Point p = PlaceLeftOf(tb.ToRectangle(), tr.ToRectangle(), _size, Px(8, TaskbarDpi() / 96f));
            Win32.SetWindowPos(_strip.Handle, IntPtr.Zero, p.X, p.Y, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }

        private int TaskbarDpi()
        {
            try
            {
                int d = (int)Win32.GetDpiForWindow(_taskbar);
                return d > 0 ? d : 96;
            }
            catch (EntryPointNotFoundException)
            {
                return 96;
            }
        }

        private Win32.TOOLINFO ToolInfo(string text)
        {
            var ti = new Win32.TOOLINFO();
            ti.cbSize = Marshal.SizeOf(typeof(Win32.TOOLINFO));
            ti.uFlags = Win32.TTF_IDISHWND;
            ti.hwnd = _strip.Handle;
            ti.uId = _strip.Handle;
            ti.lpszText = text;
            return ti;
        }

        private void Relay(ref Message m)
        {
            if (_tip == IntPtr.Zero) return;
            var msg = new Win32.MSG();
            msg.hwnd = m.HWnd;
            msg.message = m.Msg;
            msg.wParam = m.WParam;
            msg.lParam = m.LParam;
            Win32.SendMessage(_tip, Win32.TTM_RELAYEVENT, IntPtr.Zero, ref msg);
        }

        private static void Raise(Action handler)
        {
            if (handler == null) return;
            try { handler(); }
            catch (Exception) { }
        }

        private sealed class StripWindow : NativeWindow
        {
            private readonly TaskbarBattery _owner;

            public StripWindow(TaskbarBattery owner)
            {
                _owner = owner;
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case Win32.WM_MOUSEACTIVATE:
                        m.Result = new IntPtr(Win32.MA_NOACTIVATE);
                        return;
                    case Win32.WM_MOUSEMOVE:
                    case Win32.WM_LBUTTONDOWN:
                    case Win32.WM_RBUTTONDOWN:
                        _owner.Relay(ref m);
                        break;
                    case Win32.WM_LBUTTONUP:
                        _owner.Relay(ref m);
                        Raise(_owner.Clicked);
                        break;
                    case Win32.WM_RBUTTONUP:
                        _owner.Relay(ref m);
                        Raise(_owner.RightClicked);
                        break;
                }
                base.WndProc(ref m);
            }
        }

        private sealed class ControlWindow : NativeWindow
        {
            private readonly TaskbarBattery _owner;

            public ControlWindow(TaskbarBattery owner)
            {
                _owner = owner;
                var cp = new CreateParams();
                cp.Parent = new IntPtr(-3);
                CreateHandle(cp);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmItems)
                {
                    _owner.OnItems();
                    return;
                }
                if (m.Msg == WmStop)
                {
                    Application.ExitThread();
                    return;
                }
                base.WndProc(ref m);
            }
        }
    }

    internal static class Win32
    {
        public const int WS_CHILD = 0x40000000;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_CLIPSIBLINGS = 0x04000000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int TTS_ALWAYSTIP = 0x01;
        public const int TTS_NOPREFIX = 0x02;
        public const int TTF_IDISHWND = 0x0001;
        public const int TTM_RELAYEVENT = 0x0407;
        public const int TTM_ADDTOOLW = 0x0432;
        public const int TTM_UPDATETIPTEXTW = 0x0439;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int MA_NOACTIVATE = 3;
        public const int SWP_NOSIZE = 0x0001;
        public const int SWP_NOACTIVATE = 0x0010;
        public const int SWP_SHOWWINDOW = 0x0040;
        public const int ULW_ALPHA = 0x02;
        public const int SW_RESTORE = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;

            public Rectangle ToRectangle()
            {
                return Rectangle.FromLTRB(Left, Top, Right, Bottom);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int Cx, Cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct TOOLINFO
        {
            public int cbSize;
            public int uFlags;
            public IntPtr hwnd;
            public IntPtr uId;
            public RECT rect;
            public IntPtr hinst;
            public string lpszText;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INITCOMMONCONTROLSEX { public int dwSize, dwICC; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, ref TOOLINFO l);
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, ref MSG l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string cls, string title, int style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE size,
            IntPtr hdcSrc, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("comctl32.dll")]
        public static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
