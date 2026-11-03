// BC Dark Overlay
// Recolours the Business Central Windows client on screen, without touching BC itself.
// One click-through window covers all monitors. A Windows Magnification control inside it
// redraws the screen through a colour matrix, and the window's region is cut down to the
// parts of the BC windows that are actually visible, so nothing else gets recoloured.
//
// Usage: BCDarkOverlay.exe [--process NAME]   (default: Microsoft.Dynamics.Nav.Client)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BCDarkOverlay
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string target = "Microsoft.Dynamics.Nav.Client";
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--process") target = args[i + 1];

            // Must happen before any window exists, so all coordinates are real pixels.
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));

            bool isFirst;
            using (var mutex = new Mutex(true, "BCDarkOverlay.SingleInstance", out isFirst))
            {
                if (!isFirst) return 0;

                if (!Native.MagInitialize())
                {
                    MessageBox.Show("Could not start the Windows Magnification API.", "BC Dark Overlay");
                    return 1;
                }
                Application.EnableVisualStyles();
                Application.Run(new OverlayForm(target));
                Native.MagUninitialize();
            }
            return 0;
        }
    }

    sealed class OverlayForm : Form
    {
        const int HotkeyId = 1;
        const int FastInterval = 16;   // about 60 updates a second while BC is on screen
        const int IdleInterval = 250;
        const int MinEventGap = 4;
        const float NormalSaturation = 1f;
        // Higher values also strengthen the orange and blue edges ClearType puts on text.
        const float VividSaturation = 1.25f;
        const float ExtraVividSaturation = 1.6f;

        readonly string targetProcess;
        readonly Dictionary<uint, bool> targetPids = new Dictionary<uint, bool>();
        readonly HashSet<IntPtr> animationsDisabled = new HashSet<IntPtr>();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem enabledItem = new ToolStripMenuItem("Dark mode on");
        readonly ToolStripMenuItem softItem = new ToolStripMenuItem("Soft dark");
        readonly ToolStripMenuItem blackItem = new ToolStripMenuItem("Black");
        readonly ToolStripMenuItem normalColoursItem = new ToolStripMenuItem("Normal colours");
        readonly ToolStripMenuItem vividColoursItem = new ToolStripMenuItem("Vivid colours");
        readonly ToolStripMenuItem extraVividColoursItem = new ToolStripMenuItem("Extra vivid colours");

        IntPtr magnifier;
        IntPtr lastRegion;
        Native.RECT virtualScreen;
        bool enabled = true;
        bool hostVisible;
        bool targetAboveHost;
        bool softTheme = true;
        float saturation = VividSaturation;
        bool inTick;
        int tick;
        readonly Stopwatch sinceLastTick = Stopwatch.StartNew();
        Native.WinEventProc windowEventProc;   // kept in a field so the GC can't collect it
        readonly List<IntPtr> hooks = new List<IntPtr>();

        public OverlayForm(string targetProcess)
        {
            this.targetProcess = targetProcess;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;

            enabledItem.Click += delegate { SetEnabled(!enabled); };
            softItem.Click += delegate { SetTheme(true); };
            blackItem.Click += delegate { SetTheme(false); };
            normalColoursItem.Click += delegate { SetSaturation(NormalSaturation); };
            vividColoursItem.Click += delegate { SetSaturation(VividSaturation); };
            extraVividColoursItem.Click += delegate { SetSaturation(ExtraVividSaturation); };

            var menu = new ContextMenuStrip();
            menu.Items.Add(enabledItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(softItem);
            menu.Items.Add(blackItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(normalColoursItem);
            menu.Items.Add(vividColoursItem);
            menu.Items.Add(extraVividColoursItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Close(); });

            tray.Icon = SystemIcons.Application;
            tray.Text = "BC Dark Overlay (Ctrl+Alt+D)";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { SetEnabled(!enabled); };
            tray.Visible = true;

            timer.Interval = FastInterval;
            timer.Tick += delegate { Tick(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST
                            | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // The form is shown only through ShowHost, never by Application.Run.
        protected override void SetVisibleCore(bool value)
        {
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);

            magnifier = Native.CreateWindowEx(0, "Magnifier", "BCDarkOverlayMagnifier",
                Native.WS_CHILD | Native.WS_VISIBLE, 0, 0, 1, 1, Handle, IntPtr.Zero,
                Native.GetModuleHandle(null), IntPtr.Zero);

            var self = new[] { Handle };
            Native.MagSetWindowFilterList(magnifier, Native.MW_FILTERMODE_EXCLUDE, 1, self);

            SetTheme(true);
            SetSaturation(VividSaturation);
            Native.RegisterHotKey(Handle, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT, (uint)Keys.D);

            windowEventProc = OnWindowEvent;
            Hook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND);
            Hook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND);
            Hook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_REORDER);
            Hook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE);

            timer.Start();
        }

        void Hook(uint first, uint last)
        {
            hooks.Add(Native.SetWinEventHook(first, last, IntPtr.Zero, windowEventProc, 0, 0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId) SetEnabled(!enabled);
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            foreach (IntPtr hook in hooks) Native.UnhookWinEvent(hook);
            Native.UnregisterHotKey(Handle, HotkeyId);
            tray.Visible = false;
            tray.Dispose();
            if (lastRegion != IntPtr.Zero) Native.DeleteObject(lastRegion);
            base.OnFormClosed(e);
        }

        void SetEnabled(bool value)
        {
            enabled = value;
            enabledItem.Checked = value;
            if (!value) HideHost();
        }

        void SetTheme(bool soft)
        {
            softTheme = soft;
            softItem.Checked = soft;
            blackItem.Checked = !soft;
            enabledItem.Checked = enabled;
            ApplyEffect();
        }

        void SetSaturation(float value)
        {
            saturation = value;
            normalColoursItem.Checked = value == NormalSaturation;
            vividColoursItem.Checked = value == VividSaturation;
            extraVividColoursItem.Checked = value == ExtraVividSaturation;
            ApplyEffect();
        }

        void ApplyEffect()
        {
            // Soft keeps backgrounds dark grey and text off-white, which is easier on the eyes.
            var effect = BuildEffect(softTheme ? 0.12f : 0f, softTheme ? 0.92f : 1f, saturation);
            Native.MagSetColorEffect(magnifier, ref effect);
        }

        // Inverts brightness and turns hue 180 degrees back, so red stays red and so on.
        // White maps to `floor`, black maps to `ceiling`. Saturation then strengthens colours
        // and leaves greys alone, so text and backgrounds keep their brightness.
        static Native.MAGCOLOREFFECT BuildEffect(float floor, float ceiling, float saturation)
        {
            float s = ceiling - floor;
            float a = 1f / 3f * s;
            float b = -2f / 3f * s;
            float[,] invert = { { a, b, b }, { b, a, b }, { b, b, a } };

            float[] luma = { 0.2126f, 0.7152f, 0.0722f };
            var transform = new float[25];
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                {
                    float sum = 0;
                    for (int i = 0; i < 3; i++)
                        sum += invert[row, i] * ((1 - saturation) * luma[i] + (i == column ? saturation : 0));
                    transform[row * 5 + column] = sum;
                }

            transform[18] = 1;
            transform[20] = transform[21] = transform[22] = ceiling;
            transform[24] = 1;
            return new Native.MAGCOLOREFFECT { transform = transform };
        }

        void Tick()
        {
            if (inTick) return;
            inTick = true;
            try { UpdateOverlay(); }
            finally
            {
                sinceLastTick.Restart();
                inTick = false;
            }
        }

        void UpdateOverlay()
        {
            if (!enabled)
            {
                timer.Interval = IdleInterval;
                return;
            }

            UpdateVirtualScreen();

            // Processes change rarely, so only look them up about once a second.
            if (tick++ % 60 == 0)
            {
                targetPids.Clear();
                animationsDisabled.Clear();
            }

            IntPtr region = BuildVisibleRegion();
            bool hasArea = Native.GetRgnBox(region, out _unused) != Native.NULLREGION;

            bool changed = lastRegion == IntPtr.Zero || !Native.EqualRgn(region, lastRegion);

            // Where BC just went away, stop recolouring first, or whatever was behind it gets
            // inverted for a frame. Where BC just appeared, draw first so it's never shown stale.
            bool losesArea = changed && lastRegion != IntPtr.Zero && LosesArea(lastRegion, region);
            if (losesArea) ApplyRegion(region);

            if (hasArea)
            {
                Native.MagSetWindowSource(magnifier, virtualScreen);
                Native.InvalidateRect(magnifier, IntPtr.Zero, false);
                Native.UpdateWindow(magnifier);
            }

            if (!changed) Native.DeleteObject(region);
            else if (!losesArea) ApplyRegion(region);

            if (!hasArea)
            {
                timer.Interval = IdleInterval;
                return;
            }

            ShowHost();

            // BC's dropdowns are always-on-top windows, so they open above the overlay.
            if (targetAboveHost)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            timer.Interval = FastInterval;
        }

        static bool LosesArea(IntPtr before, IntPtr after)
        {
            IntPtr removed = Native.CreateRectRgn(0, 0, 0, 0);
            bool loses = Native.CombineRgn(removed, before, after, Native.RGN_DIFF) != Native.NULLREGION;
            Native.DeleteObject(removed);
            return loses;
        }

        void ApplyRegion(IntPtr region)
        {
            if (lastRegion != IntPtr.Zero) Native.DeleteObject(lastRegion);
            lastRegion = region;

            // SetWindowRgn takes ownership of the region it is given, so hand it a copy.
            IntPtr copy = Native.CreateRectRgn(0, 0, 0, 0);
            Native.CombineRgn(copy, region, IntPtr.Zero, Native.RGN_COPY);
            Native.SetWindowRgn(Handle, copy, true);
        }

        // Runs when any window appears, hides, moves in the stack or takes focus, so the overlay
        // reacts within a frame instead of waiting for the timer.
        void OnWindowEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != Native.OBJID_WINDOW || idChild != 0) return;
            if (inTick || sinceLastTick.ElapsedMilliseconds < MinEventGap) return;
            Tick();
        }

        Native.RECT _unused;

        void UpdateVirtualScreen()
        {
            var r = new Native.RECT();
            r.Left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            r.Top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            r.Right = r.Left + Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            r.Bottom = r.Top + Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

            if (r.Left == virtualScreen.Left && r.Top == virtualScreen.Top
                && r.Right == virtualScreen.Right && r.Bottom == virtualScreen.Bottom) return;

            virtualScreen = r;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.Left, r.Top, w, h,
                Native.SWP_NOACTIVATE | (hostVisible ? 0u : Native.SWP_NOZORDER));
            Native.SetWindowPos(magnifier, IntPtr.Zero, 0, 0, w, h, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        }

        // Walks windows from front to back: a BC window counts only where nothing in front covers it.
        IntPtr BuildVisibleRegion()
        {
            IntPtr result = Native.CreateRectRgn(0, 0, 0, 0);
            IntPtr covered = Native.CreateRectRgn(0, 0, 0, 0);
            IntPtr scratch = Native.CreateRectRgn(0, 0, 0, 0);

            bool passedHost = false;
            targetAboveHost = false;

            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                if (hwnd == Handle)
                {
                    passedHost = true;
                    return true;
                }
                if (!IsPainted(hwnd)) return true;

                Native.RECT rect;
                if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out rect,
                        Marshal.SizeOf(typeof(Native.RECT))) != 0
                    && !Native.GetWindowRect(hwnd, out rect)) return true;
                if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return true;

                IntPtr windowRgn = Native.CreateRectRgn(rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (IsTarget(hwnd))
                {
                    if (!passedHost) targetAboveHost = true;
                    DisableAnimations(hwnd);
                    Native.CombineRgn(scratch, windowRgn, covered, Native.RGN_DIFF);
                    Native.CombineRgn(result, result, scratch, Native.RGN_OR);
                }
                Native.CombineRgn(covered, covered, windowRgn, Native.RGN_OR);
                Native.DeleteObject(windowRgn);
                return true;
            }, IntPtr.Zero);

            Native.DeleteObject(covered);
            Native.DeleteObject(scratch);
            Native.OffsetRgn(result, -virtualScreen.Left, -virtualScreen.Top);
            return result;
        }

        static readonly StringBuilder classBuffer = new StringBuilder(256);

        // False for windows that exist but put nothing on screen, such as invisible overlays.
        static bool IsPainted(IntPtr hwnd)
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return false;

            int cloaked;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0
                && cloaked != 0) return false;

            // The Alt-Tab screen is a full-monitor window, but mostly see-through.
            classBuffer.Length = 0;
            Native.GetClassName(hwnd, classBuffer, classBuffer.Capacity);
            string className = classBuffer.ToString();
            if (className == "XamlExplorerHostIslandWindow" || className == "MultitaskingViewFrame"
                || className == "TaskSwitcherWnd") return false;

            int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            if ((exStyle & Native.WS_EX_TRANSPARENT) != 0) return false;
            if ((exStyle & Native.WS_EX_LAYERED) != 0)
            {
                uint colorKey, flags;
                byte alpha;
                if (Native.GetLayeredWindowAttributes(hwnd, out colorKey, out alpha, out flags)
                    && (flags & Native.LWA_ALPHA) != 0 && alpha == 0) return false;
            }
            return true;
        }

        // While Windows animates a minimize or maximize, the window is drawn outside its real
        // position, so the overlay can't cover it. The setting ends when the window closes.
        void DisableAnimations(IntPtr hwnd)
        {
            if (!animationsDisabled.Add(hwnd)) return;
            int disabled = 1;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
        }

        bool IsTarget(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);

            bool isTarget;
            if (targetPids.TryGetValue(pid, out isTarget)) return isTarget;

            try
            {
                using (var process = Process.GetProcessById((int)pid))
                    isTarget = string.Equals(process.ProcessName, targetProcess, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { isTarget = false; }
            catch (InvalidOperationException) { isTarget = false; }

            targetPids[pid] = isTarget;
            return isTarget;
        }

        void ShowHost()
        {
            if (hostVisible) return;
            hostVisible = true;
            int w = virtualScreen.Right - virtualScreen.Left, h = virtualScreen.Bottom - virtualScreen.Top;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, virtualScreen.Left, virtualScreen.Top, w, h,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        void HideHost()
        {
            if (!hostVisible) return;
            hostVisible = false;
            Native.ShowWindow(Handle, Native.SW_HIDE);
        }
    }

    static class Native
    {
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const uint WS_CHILD = 0x40000000;
        public const uint WS_VISIBLE = 0x10000000;
        public const int GWL_EXSTYLE = -20;
        public const uint LWA_ALPHA = 2;
        public const int MW_FILTERMODE_EXCLUDE = 0;
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 1;
        public const uint MOD_CONTROL = 2;
        public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        public const int SW_HIDE = 0;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
        public const int RGN_OR = 2, RGN_DIFF = 4, RGN_COPY = 5;
        public const int NULLREGION = 1;
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        public const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_REORDER = 0x8004, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
        public const int OBJID_WINDOW = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MAGCOLOREFFECT
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
            public float[] transform;
        }

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("Magnification.dll")] public static extern bool MagInitialize();
        [DllImport("Magnification.dll")] public static extern bool MagUninitialize();
        [DllImport("Magnification.dll")] public static extern bool MagSetWindowSource(IntPtr hwnd, RECT rect);
        [DllImport("Magnification.dll")] public static extern bool MagSetColorEffect(IntPtr hwnd, ref MAGCOLOREFFECT effect);
        [DllImport("Magnification.dll")] public static extern bool MagSetWindowFilterList(IntPtr hwnd, int mode, int count, IntPtr[] list);

        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string title, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
        [DllImport("user32.dll")] public static extern bool UpdateWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] public static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
        [DllImport("gdi32.dll")] public static extern int OffsetRgn(IntPtr region, int x, int y);
        [DllImport("gdi32.dll")] public static extern bool EqualRgn(IntPtr a, IntPtr b);
        [DllImport("gdi32.dll")] public static extern int GetRgnBox(IntPtr region, out RECT box);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    }
}
