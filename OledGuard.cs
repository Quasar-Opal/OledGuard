// OledGuard - per-screen OLED burn-in guard for Windows.
// Written by Claude (Anthropic). Provided as-is, without warranty; see LICENSE.
//
// Blanks each guarded screen with a black topmost overlay once that screen on its own has
// been idle for its timeout. Wake rules per screen: mouse travels 50 px on the screen,
// click/scroll on the screen, keys only if the focused window is on the screen (desktop
// excluded). Fullscreen windows on a screen keep it awake.
// No keyboard hook (Defender flags a hook + autostart as a keylogger). Key presses come from
// Raw Input (keyboard, background sink) and only key-down vs key-up is read, never the key.
// GetLastInputInfo is not used: on some machines virtual input devices refresh it constantly.
//
// Build: build.cmd (csc from .NET Framework 4.x, C# 5 - no runtime to install).
// Install: OledGuard.exe --install (or just run it) / --uninstall [--quiet]
// CLI to a running instance: OledGuard.exe blank|wake|toggle [screen#] | screens | exit
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("OledGuard")]
[assembly: AssemblyDescription("Per-screen OLED burn-in guard: blanks idle screens")]
[assembly: AssemblyProduct("OledGuard")]
[assembly: AssemblyCompany("OledGuard")]
[assembly: AssemblyCopyright("Written by Claude (Anthropic). MIT License, provided as-is.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    public const int WH_MOUSE_LL = 14;
    public const int WM_INPUT = 0xFF, WM_MOUSEMOVE = 0x200;
    public const uint RIDEV_INPUTSINK = 0x100, RID_INPUT = 0x10000003, RIM_TYPEKEYBOARD = 1;
    public const ushort RI_KEY_BREAK = 1;
    public const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
    public const int WM_MOUSEWHEEL = 0x20A, WM_XBUTTONDOWN = 0x20B, WM_MOUSEHWHEEL = 0x20E;
    public const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_DPICHANGED = 0x2E0, WM_APP = 0x8000;
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    public const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_MESSAGE = new IntPtr(-3);

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr hRaw, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr data);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern IntPtr CreateCursor(IntPtr hInst, int xHot, int yHot, int w, int h, byte[] andPlane, byte[] xorPlane);
    [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr hCursor);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, uint action, IntPtr info);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    public static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}

class Mon
{
    public string Id, Name, Device;
    public IntPtr Handle;
    public Native.RECT Rect;
    public int Index;
    public bool Enabled = true;
    public int TimeoutSec = 120;
    public long Last, BlankedAt;
    public double Travel;
    public bool Blanked;
    public Overlay Overlay;

    public string Label { get { return string.Format("{0}: {1} ({2}x{3})", Index, Name, Rect.Width, Rect.Height); } }
}

class Overlay : Form
{
    // 32x32 cursor with AND mask all 1s and XOR all 0s = fully transparent
    static readonly Cursor Invisible = new Cursor(Native.CreateCursor(IntPtr.Zero, 0, 0, 32, 32,
        Enumerable.Repeat((byte)0xFF, 128).ToArray(), new byte[128]));

    public Overlay()
    {
        Cursor = Invisible;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        Text = "OledGuard overlay";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = new IntPtr(Native.MA_NOACTIVATE); return; }
        if (m.Msg == Native.WM_DPICHANGED) return; // bounds are physical pixels; never let a DPI change resize us
        base.WndProc(ref m);
    }

    public void ShowOn(Native.RECT r)
    {
        Bounds = new Rectangle(r.Left, r.Top, r.Width, r.Height);
        Show();
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);
        // a still cursor wouldn't get WM_SETCURSOR until it moves, so hide it now
        Native.POINT p;
        if (Native.GetCursorPos(out p) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom)
            Native.SetCursor(Invisible.Handle);
    }
}

class ControlWindow : NativeWindow
{
    public const string Title = "OledGuard.Control";
    public const int WM_COMMAND_MSG = Native.WM_APP + 1;
    readonly App app;

    public ControlWindow(App app)
    {
        this.app = app;
        CreateHandle(new CreateParams { Caption = Title, Parent = Native.HWND_MESSAGE });
        Native.ChangeWindowMessageFilterEx(Handle, WM_COMMAND_MSG, 1 /* MSGFLT_ALLOW */, IntPtr.Zero); // CLI from non-elevated shortcuts
        var rid = new[] { new Native.RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = Native.RIDEV_INPUTSINK, Target = Handle } };
        if (!Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
            App.Log("raw keyboard input registration failed, error " + Marshal.GetLastWin32Error());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_COMMAND_MSG) { app.Command(m.WParam.ToInt32(), m.LParam.ToInt32()); return; }
        if (m.Msg == Native.WM_INPUT && IsKeyDown(m.LParam)) app.OnKey();
        base.WndProc(ref m); // WM_INPUT must reach DefWindowProc
    }

    // RAWKEYBOARD follows the header: MakeCode (ushort), Flags (ushort), ...
    static bool IsKeyDown(IntPtr hRaw)
    {
        uint headerSize = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTHEADER)), size = 0;
        Native.GetRawInputData(hRaw, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size < headerSize + 4) return false;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(hRaw, Native.RID_INPUT, buf, ref size, headerSize) == uint.MaxValue) return false;
            var h = (Native.RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(Native.RAWINPUTHEADER));
            if (h.Type != Native.RIM_TYPEKEYBOARD) return false;
            ushort flags = (ushort)Marshal.ReadInt16(buf, (int)headerSize + 2);
            return (flags & Native.RI_KEY_BREAK) == 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

class App : ApplicationContext
{
    const double TravelPx = 50;
    const long TravelGapMs = 1000;   // travel resets after this long without motion (ignores slow jitter)
    const long GraceMs = 700;        // input right after blanking (e.g. the tray click) doesn't wake
    const long FullscreenRecheckMs = 2000;
    public static readonly int[] Timeouts = { 30, 60, 120, 300, 600, 900, 1800 };
    static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static long Now { get { return Clock.ElapsedMilliseconds; } }

    public static readonly string CfgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OledGuard");
    public static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OledGuard");
    static readonly string CfgPath = Path.Combine(CfgDir, "OledGuard.conf");
    static readonly string LogPath = Path.Combine(LogDir, "OledGuard.log");

    readonly Dictionary<string, string> cfg = new Dictionary<string, string>();
    List<Mon> mons = new List<Mon>();
    ScreenPicker picker;
    Dictionary<IntPtr, Mon> byHandle = new Dictionary<IntPtr, Mon>();
    bool skipFullscreen = true;

    readonly Native.HookProc mouseProc; // kept alive for the hook
    IntPtr mouseHook;
    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer rebuildTimer = new System.Windows.Forms.Timer();
    readonly Control ui;
    readonly ControlWindow control;
    readonly uint myPid = (uint)Process.GetCurrentProcess().Id;
    IntPtr iconHandle;

    Native.POINT lastPt;
    Mon lastMon;
    long lastMoveAt;
    bool hasLastPt;

    public App()
    {
        ui = new Control();
        ui.CreateControl(); // installs the WinForms sync context so SystemEvents land on this thread
        LoadConfig();
        Log("start, " + Application.ExecutablePath);
        Rebuild("startup");

        mouseProc = MouseProc;
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, Native.GetModuleHandle(null), 0);
        if (mouseHook == IntPtr.Zero) Log("mouse hook install failed, error " + Marshal.GetLastWin32Error());

        control = new ControlWindow(this);

        menu.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };
        tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) BlankAll("tray"); };
        UpdateIcon();

        tick.Interval = 500;
        tick.Tick += (s, e) => Tick();
        tick.Start();

        rebuildTimer.Interval = 1500;
        rebuildTimer.Tick += (s, e) => { rebuildTimer.Stop(); Rebuild("display change"); };

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.PowerModeChanged += OnPowerChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        if (!cfg.ContainsKey("Configured")) ui.BeginInvoke(new Action(ShowPicker)); // first run: pick screens
    }

    // ---------- screen picker ----------

    public void ShowPicker()
    {
        if (picker != null && !picker.IsDisposed) { picker.Activate(); return; }
        picker = new ScreenPicker(mons, ApplyScreens);
        picker.Show();
        picker.Activate();
    }

    void ApplyScreens(Dictionary<Mon, bool> enabled, Dictionary<Mon, int> timeouts)
    {
        foreach (var m in mons)
        {
            bool on;
            int secs;
            if (enabled.TryGetValue(m, out on)) { m.Enabled = on; SetValue("Monitor." + m.Id + ".Enabled", on ? "1" : "0"); }
            if (timeouts.TryGetValue(m, out secs)) { m.TimeoutSec = secs; SetValue("Monitor." + m.Id + ".Timeout", secs.ToString()); }
            m.Last = Now;
            if (!m.Enabled) Wake(m, "unguarded");
        }
        SetValue("Configured", "1");
        Log("screens: " + string.Join(", ", mons.Select(m => m.Index + (m.Enabled ? "=" + m.TimeoutSec + "s" : "=off")).ToArray()));
        UpdateIcon();
    }

    // ---------- monitors ----------

    void Rebuild(string why)
    {
        var found = new List<Mon>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref Native.RECT rc, IntPtr d) =>
        {
            var mi = new Native.MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX));
            if (Native.GetMonitorInfo(h, ref mi)) found.Add(new Mon { Handle = h, Rect = mi.rcMonitor, Device = mi.szDevice });
            return true;
        }, IntPtr.Zero);

        found = found.OrderBy(m => m.Rect.Left).ThenBy(m => m.Rect.Top).ToList();
        var names = WmiNames();
        var seen = new HashSet<string>();
        for (int i = 0; i < found.Count; i++)
        {
            var m = found[i];
            m.Index = i + 1;
            Identify(m, names);
            if (!seen.Add(m.Id)) { m.Id += "_" + m.Index; seen.Add(m.Id); }
            m.Enabled = GetBool("Monitor." + m.Id + ".Enabled", true);
            m.TimeoutSec = GetInt("Monitor." + m.Id + ".Timeout", 120);
            m.Last = Now;
        }

        // carry state over by id; overlays of screens that vanished are closed
        var restore = new List<Mon>();
        foreach (var old in mons)
        {
            var n = found.FirstOrDefault(m => m.Id == old.Id);
            if (n != null)
            {
                n.Last = old.Last;
                n.Overlay = old.Overlay;
                if (old.Blanked) restore.Add(n);
                else if (n.Overlay != null) n.Overlay.Hide();
            }
            else if (old.Overlay != null) old.Overlay.Close();
        }

        mons = found;
        byHandle = found.ToDictionary(m => m.Handle);
        hasLastPt = false;
        Log(why + ": " + string.Join(" | ", found.Select(m => string.Format("{0} [{1}] @{2},{3} guard={4} timeout={5}s",
            m.Label, m.Id, m.Rect.Left, m.Rect.Top, m.Enabled ? 1 : 0, m.TimeoutSec)).ToArray()));
        foreach (var m in restore) Blank(m, "restore after " + why);
        if (tray != null) UpdateIcon();
    }

    static Dictionary<string, string> WmiNames()
    {
        var d = new Dictionary<string, string>();
        try
        {
            using (var q = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID"))
                foreach (ManagementObject o in q.Get())
                {
                    string inst = ((string)o["InstanceName"] ?? "").ToUpperInvariant();
                    int us = inst.LastIndexOf('_');
                    if (us > 0) inst = inst.Substring(0, us);
                    var raw = o["UserFriendlyName"] as ushort[];
                    if (raw == null) continue;
                    string name = new string(raw.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
                    if (name.Length > 0) d[inst] = name;
                }
        }
        catch (Exception ex) { Log("WMI monitor names: " + ex.Message); }
        return d;
    }

    static void Identify(Mon m, Dictionary<string, string> names)
    {
        m.Id = m.Device.Trim('\\', '.');
        m.Name = m.Device;
        var dd = new Native.DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
        // flag 1 = EDD_GET_DEVICE_INTERFACE_NAME: \\?\DISPLAY#ABC1234#5&12345678&0&UID4352#{guid}
        if (!Native.EnumDisplayDevices(m.Device, 0, ref dd, 1)) return;
        if (!string.IsNullOrEmpty(dd.DeviceString)) m.Name = dd.DeviceString;
        var parts = (dd.DeviceID ?? "").Split('#');
        if (parts.Length < 3) return;
        string model = parts[1], inst = parts[2];
        m.Id = model + "_" + inst.Substring(inst.LastIndexOf('&') + 1);
        string friendly;
        if (names.TryGetValue(("DISPLAY\\" + model + "\\" + inst).ToUpperInvariant(), out friendly)) m.Name = friendly;
    }

    Mon MonAt(Native.POINT pt)
    {
        Mon m;
        byHandle.TryGetValue(Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONULL), out m);
        return m;
    }

    // ---------- input ----------

    IntPtr MouseProc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            try
            {
                var h = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(Native.MSLLHOOKSTRUCT));
                OnMouse(w.ToInt32(), h.pt);
            }
            catch (Exception ex) { Log("mouse hook: " + ex.Message); }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    void OnMouse(int msg, Native.POINT pt)
    {
        Mon m = MonAt(pt);
        if (msg == Native.WM_MOUSEMOVE)
        {
            long now = Now;
            if (m != null)
            {
                if (hasLastPt && m == lastMon && now - lastMoveAt < TravelGapMs)
                {
                    double dx = pt.X - lastPt.X, dy = pt.Y - lastPt.Y;
                    m.Travel += Math.Sqrt(dx * dx + dy * dy);
                }
                else m.Travel = 0;
                if (m.Travel >= TravelPx) Activity(m, "mouse");
            }
            lastPt = pt; lastMon = m; lastMoveAt = now; hasLastPt = true;
        }
        else if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN ||
                 msg == Native.WM_XBUTTONDOWN || msg == Native.WM_MOUSEWHEEL || msg == Native.WM_MOUSEHWHEEL)
        {
            if (m != null) Activity(m, msg == Native.WM_MOUSEWHEEL || msg == Native.WM_MOUSEHWHEEL ? "scroll" : "click");
        }
    }

    public void OnKey()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        string cls = Native.ClassOf(fg);
        if (cls == "Progman" || cls == "WorkerW") return; // the desktop spans every screen
        Mon m;
        if (byHandle.TryGetValue(Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST), out m)) Activity(m, "key");
    }

    void Activity(Mon m, string why)
    {
        m.Travel = 0;
        if (m.Blanked)
        {
            if (Now - m.BlankedAt < GraceMs) return;
            Wake(m, why);
        }
        m.Last = Now;
    }

    // ---------- blanking ----------

    void Tick()
    {
        long now = Now;
        foreach (var m in mons)
        {
            if (m.Blanked)
            {
                Native.SetWindowPos(m.Overlay.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                continue;
            }
            if (!m.Enabled) continue;
            long timeoutMs = m.TimeoutSec * 1000L;
            if (now - m.Last < timeoutMs) continue;
            if (skipFullscreen && IsFullscreenOn(m)) { m.Last = now - timeoutMs + FullscreenRecheckMs; continue; }
            Blank(m, "idle " + m.TimeoutSec + "s");
        }
    }

    // First real window in z-order that fully covers the screen decides: captionless = fullscreen
    // (video, game, F11 browser); a captioned (maximized) window hides anything below it.
    bool IsFullscreenOn(Mon m)
    {
        bool result = false;
        var r0 = m.Rect;
        Native.EnumWindows((h, d) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == myPid) return true;
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE)) != 0) return true;
            int cloaked;
            if (Native.DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out cloaked, 4) == 0 && cloaked != 0) return true;
            if (ShellClasses.Contains(Native.ClassOf(h))) return true;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return true;
            if (r.Left <= r0.Left && r.Top <= r0.Top && r.Right >= r0.Right && r.Bottom >= r0.Bottom)
            {
                result = (Native.GetWindowLong(h, Native.GWL_STYLE) & Native.WS_CAPTION) != Native.WS_CAPTION;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    void Blank(Mon m, string why)
    {
        if (m.Blanked) return;
        if (m.Overlay == null) m.Overlay = new Overlay();
        m.Overlay.ShowOn(m.Rect);
        m.Blanked = true;
        m.BlankedAt = Now;
        m.Travel = 0;
        Log("blank " + m.Label + " (" + why + ")");
        UpdateIcon();
    }

    void Wake(Mon m, string why)
    {
        if (!m.Blanked) return;
        m.Overlay.Hide();
        m.Blanked = false;
        m.Last = Now;
        Log("wake " + m.Label + " (" + why + ")");
        UpdateIcon();
    }

    void BlankAll(string why) { foreach (var m in mons.Where(x => x.Enabled)) Blank(m, why); }
    void WakeAll(string why) { foreach (var m in mons) Wake(m, why); }

    // wParam: 1 blank, 2 wake, 3 toggle, 4 exit, 5 screen picker; lParam: screen number, 0 = all guarded screens
    public void Command(int cmd, int index)
    {
        if (cmd == 4) { ExitApp(); return; }
        if (cmd == 5) { ShowPicker(); return; }
        var targets = index == 0 ? mons.Where(m => m.Enabled).ToList() : mons.Where(m => m.Index == index).ToList();
        if (cmd == 3) cmd = targets.Any(m => m.Blanked) ? 2 : 1;
        foreach (var m in targets)
        {
            if (cmd == 1) Blank(m, "command");
            else if (cmd == 2) Wake(m, "command");
        }
    }

    // ---------- system events ----------

    void OnDisplayChanged(object s, EventArgs e) { rebuildTimer.Stop(); rebuildTimer.Start(); }

    void OnPowerChanged(object s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) { WakeAll("resume"); foreach (var m in mons) m.Last = Now; }
    }

    void OnSessionSwitch(object s, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
        {
            WakeAll("unlock");
            foreach (var m in mons) m.Last = Now;
        }
    }

    // ---------- tray ----------

    void BuildMenu()
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem("Choose screens...", null, (s, e) => ShowPicker()));
        menu.Items.Add(new ToolStripSeparator());
        foreach (var mm in mons)
        {
            var m = mm;
            var sub = new ToolStripMenuItem(m.Label + (m.Blanked ? "  - blanked" : m.Enabled ? "" : "  - not guarded"));
            sub.DropDownItems.Add(new ToolStripMenuItem("Blank now", null, (s, e) => Blank(m, "menu")));
            sub.DropDownItems.Add(new ToolStripMenuItem("Wake", null, (s, e) => Wake(m, "menu")) { Enabled = m.Blanked });
            sub.DropDownItems.Add(new ToolStripSeparator());
            sub.DropDownItems.Add(new ToolStripMenuItem("Guard this screen", null, (s, e) =>
            {
                m.Enabled = !m.Enabled;
                m.Last = Now;
                SetValue("Monitor." + m.Id + ".Enabled", m.Enabled ? "1" : "0");
                Log("guard " + m.Label + " = " + m.Enabled);
            }) { Checked = m.Enabled });
            var to = new ToolStripMenuItem("Blank after");
            foreach (int t in Timeouts)
            {
                int secs = t;
                to.DropDownItems.Add(new ToolStripMenuItem(secs < 60 ? secs + " s" : secs / 60 + " min", null, (s, e) =>
                {
                    m.TimeoutSec = secs;
                    m.Last = Now;
                    SetValue("Monitor." + m.Id + ".Timeout", secs.ToString());
                    Log("timeout " + m.Label + " = " + secs + "s");
                }) { Checked = m.TimeoutSec == secs });
            }
            sub.DropDownItems.Add(to);
            menu.Items.Add(sub);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Blank all now", null, (s, e) => BlankAll("menu")) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Wake all", null, (s, e) => WakeAll("menu")) { Enabled = mons.Any(m => m.Blanked) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Keep awake under fullscreen apps", null, (s, e) =>
        {
            skipFullscreen = !skipFullscreen;
            SetValue("SkipFullscreen", skipFullscreen ? "1" : "0");
        }) { Checked = skipFullscreen });
        bool autostart = Installer.TaskExists();
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (s, e) =>
        {
            int rc = autostart ? Installer.DeleteTask() : Installer.CreateTask(Application.ExecutablePath);
            Log("autostart = " + !autostart + " (schtasks exit " + rc + ")");
            if (rc != 0) MessageBox.Show("Changing the logon task failed. OledGuard needs to run as administrator for that.", "OledGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }) { Checked = autostart });
        menu.Items.Add(new ToolStripMenuItem("Open log", null, (s, e) => { try { Process.Start(LogPath); } catch { } }));
        menu.Items.Add(new ToolStripMenuItem("About OledGuard", null, (s, e) => Installer.ShowAbout()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitApp()));
    }

    void UpdateIcon()
    {
        bool anyBlanked = mons.Any(m => m.Blanked);
        int size = SystemInformation.SmallIconSize.Width;
        using (var bmp = CatIcon.Draw(size, anyBlanked))
        {
            IntPtr h = bmp.GetHicon();
            tray.Icon = Icon.FromHandle(h);
            if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
            iconHandle = h;
        }
        string tip = "OledGuard\n" + string.Join("\n", mons.Select(m => m.Index + ": " + (m.Blanked ? "blanked" : m.Enabled ? "guarded, " + m.TimeoutSec + "s" : "off")).ToArray());
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    void ExitApp()
    {
        Log("exit");
        tick.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.PowerModeChanged -= OnPowerChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        foreach (var m in mons) if (m.Overlay != null) m.Overlay.Close();
        if (picker != null && !picker.IsDisposed) picker.Close();
        tray.Visible = false;
        tray.Dispose();
        control.DestroyHandle();
        ExitThread();
    }

    // ---------- config / log ----------

    void LoadConfig()
    {
        try
        {
            if (File.Exists(CfgPath))
                foreach (var line in File.ReadAllLines(CfgPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && !line.StartsWith("#")) cfg[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
        }
        catch (Exception ex) { Log("config load: " + ex.Message); }
        skipFullscreen = GetBool("SkipFullscreen", true);
    }

    public void SetValue(string key, string value)
    {
        cfg[key] = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CfgPath));
            File.WriteAllLines(CfgPath, cfg.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToArray());
        }
        catch (Exception ex) { Log("config save: " + ex.Message); }
    }

    bool GetBool(string key, bool def)
    {
        string v;
        return cfg.TryGetValue(key, out v) ? v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) : def;
    }

    int GetInt(string key, int def)
    {
        string v;
        int n;
        return cfg.TryGetValue(key, out v) && int.TryParse(v, out n) && n >= 5 ? n : def;
    }

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 256 * 1024)
            {
                var text = File.ReadAllText(LogPath);
                File.WriteAllText(LogPath, text.Substring(text.Length / 2));
            }
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + msg + Environment.NewLine);
        }
        catch { }
    }
}

// Black cat: open amber eyes = all screens awake, closed eyes = something is blanked.
// A light outline keeps the black silhouette visible on a dark taskbar.
static class CatIcon
{
    static readonly RectangleF Head = new RectangleF(4.5f, 9f, 23f, 20f);
    static readonly PointF[] LeftEar = { new PointF(5.5f, 15f), new PointF(7f, 2.5f), new PointF(14.5f, 10f) };
    static readonly PointF[] RightEar = { new PointF(26.5f, 15f), new PointF(25f, 2.5f), new PointF(17.5f, 10f) };

    public static Bitmap Draw(int size, bool asleep)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float k = size / 32f; // shapes are on a 32-unit grid
            g.ScaleTransform(k, k);

            // stroke all shapes, then fill on top, so only the outer edge of the outline shows
            float outline = 2f * Math.Max(1.1f, size / 26f) / k;
            using (var pen = new Pen(Color.FromArgb(205, 205, 205), outline) { LineJoin = LineJoin.Round })
            {
                g.DrawEllipse(pen, Head);
                g.DrawPolygon(pen, LeftEar);
                g.DrawPolygon(pen, RightEar);
            }
            using (var fur = new SolidBrush(Color.FromArgb(16, 16, 16)))
            {
                g.FillEllipse(fur, Head);
                g.FillPolygon(fur, LeftEar);
                g.FillPolygon(fur, RightEar);
            }

            if (asleep)
            {
                using (var lid = new Pen(Color.FromArgb(170, 170, 170), Math.Max(1.5f, 1.3f / k)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawArc(lid, 8.5f, 16f, 6f, 4.5f, 20, 140);
                    g.DrawArc(lid, 17.5f, 16f, 6f, 4.5f, 20, 140);
                }
            }
            else
            {
                using (var iris = new SolidBrush(Color.FromArgb(255, 196, 36)))
                {
                    g.FillEllipse(iris, 8.6f, 15.2f, 5.8f, 6.8f);
                    g.FillEllipse(iris, 17.6f, 15.2f, 5.8f, 6.8f);
                }
                using (var pupil = new SolidBrush(Color.Black))
                {
                    g.FillEllipse(pupil, 10.75f, 15.8f, 1.5f, 5.6f);
                    g.FillEllipse(pupil, 19.75f, 15.8f, 1.5f, 5.6f);
                }
            }
            if (size >= 24)
                using (var nose = new SolidBrush(Color.FromArgb(230, 130, 150)))
                    g.FillPolygon(nose, new[] { new PointF(14.8f, 23.4f), new PointF(17.2f, 23.4f), new PointF(16f, 25f) });
        }
        return bmp;
    }

    // Multi-size .ico with PNG frames, embedded by build.cmd via /win32icon.
    public static void WriteIco(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var frames = new List<byte[]>();
        foreach (int s in sizes)
            using (var bmp = Draw(s, false))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                frames.Add(ms.ToArray());
            }
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(frames[i].Length); w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) w.Write(f);
        }
    }
}

// Big screen number shown on every screen while the picker is open.
class NumberTag : Form
{
    public NumberTag(Mon m)
    {
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(24, 24, 24);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        int size = Math.Min(m.Rect.Width, m.Rect.Height) / 4;
        Bounds = new Rectangle(m.Rect.Left + (m.Rect.Width - size) / 2, m.Rect.Top + (m.Rect.Height - size) / 2, size, size);
        var label = new Label
        {
            Dock = DockStyle.Fill,
            Text = m.Index.ToString(),
            ForeColor = Color.FromArgb(255, 196, 36),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", size * 0.45f, FontStyle.Bold, GraphicsUnit.Pixel)
        };
        Controls.Add(label);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) return;
        base.WndProc(ref m);
    }
}

class ScreenPicker : Form
{
    readonly List<NumberTag> tags = new List<NumberTag>();

    public ScreenPicker(List<Mon> mons, Action<Dictionary<Mon, bool>, Dictionary<Mon, int>> apply)
    {
        Text = "OledGuard - choose screens";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(14);
        TopMost = true;
        using (var bmp = CatIcon.Draw(32, false)) Icon = Icon.FromHandle(bmp.GetHicon());

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        table.Controls.Add(new Label
        {
            Text = "Tick the screens OledGuard should blank after they sit idle.\nEach screen is showing its number right now.",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);
        table.SetColumnSpan(table.GetControlFromPosition(0, 0), 2);

        var boxes = new Dictionary<Mon, CheckBox>();
        var combos = new Dictionary<Mon, ComboBox>();
        int row = 1;
        foreach (var m in mons)
        {
            bool oled = m.Name.IndexOf("OLED", StringComparison.OrdinalIgnoreCase) >= 0;
            var cb = new CheckBox { Text = m.Label + (oled ? "  (OLED)" : ""), Checked = m.Enabled, AutoSize = true, Margin = new Padding(0, 6, 16, 6) };
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
            foreach (int t in App.Timeouts) combo.Items.Add("after " + (t < 60 ? t + " s" : t / 60 + " min"));
            int idx = Array.IndexOf(App.Timeouts, m.TimeoutSec);
            combo.SelectedIndex = idx >= 0 ? idx : Array.IndexOf(App.Timeouts, 120);
            combo.Enabled = cb.Checked;
            cb.CheckedChanged += (s, e) => combo.Enabled = cb.Checked;
            table.Controls.Add(cb, 0, row);
            table.Controls.Add(combo, 1, row);
            boxes[m] = cb;
            combos[m] = combo;
            row++;
        }

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (s, e) =>
        {
            apply(boxes.ToDictionary(kv => kv.Key, kv => kv.Value.Checked),
                  combos.ToDictionary(kv => kv.Key, kv => App.Timeouts[kv.Value.SelectedIndex]));
            Close();
        };
        cancel.Click += (s, e) => Close();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        table.Controls.Add(buttons, 0, row);
        table.SetColumnSpan(buttons, 2);
        Controls.Add(table);
        AcceptButton = save;
        CancelButton = cancel;

        foreach (var m in mons) tags.Add(new NumberTag(m));
        Shown += (s, e) => { foreach (var t in tags) t.Show(); };
        FormClosed += (s, e) => { foreach (var t in tags) t.Close(); };
    }
}

// Self-installer: one exe that copies itself to Program Files, registers an elevated logon task,
// a Start Menu shortcut and an Apps & features entry. Program Files (admin-only writable) matters:
// the task runs the exe elevated, so it must not live where a normal process could replace it.
static class Installer
{
    public const string Version = "1.0.0";
    const string TaskName = "OledGuard";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OledGuard";
    public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OledGuard");
    public static readonly string InstalledExe = Path.Combine(InstallDir, "OledGuard.exe");
    static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "OledGuard.lnk");

    public static bool IsElevated
    {
        get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
    }

    public static bool RunningFromInstallDir
    {
        get { return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase); }
    }

    static int Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false };
        using (var p = Process.Start(psi)) { p.WaitForExit(15000); return p.HasExited ? p.ExitCode : -1; }
    }

    public static bool TaskExists() { return Run("schtasks.exe", "/query /tn " + TaskName) == 0; }
    public static int RunTask() { return Run("schtasks.exe", "/run /tn " + TaskName); }
    public static int DeleteTask() { return Run("schtasks.exe", "/delete /f /tn " + TaskName); }

    // Elevated logon task: a non-elevated OledGuard can't see input going to elevated windows
    // (UIPI), so the screen of an admin terminal would blank while in use. No time limit (the
    // default 72 h would kill it), runs on battery.
    public static int CreateTask(string exe)
    {
        string user = SecurityElement(Environment.UserDomainName + "\\" + Environment.UserName);
        string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
            "<RegistrationInfo><Description>OledGuard - per-screen OLED burn-in guard</Description></RegistrationInfo>" +
            "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>" +
            "<Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>" +
            "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement(exe) + "</Command></Exec></Actions></Task>";
        string tmp = Path.Combine(Path.GetTempPath(), "OledGuard.task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try { return Run("schtasks.exe", "/create /f /tn " + TaskName + " /xml \"" + tmp + "\""); }
        finally { File.Delete(tmp); }
    }

    static string SecurityElement(string s) { return System.Security.SecurityElement.Escape(s); }

    static int RunElevated(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Application.ExecutablePath, args) { UseShellExecute = true, Verb = "runas" });
            return 0;
        }
        catch (System.ComponentModel.Win32Exception) { return 1; } // UAC prompt declined
    }

    // Running a copy that isn't the installed one: offer to install (or update). -1 = run portable.
    public static int Offer()
    {
        bool installed = File.Exists(InstalledExe);
        string text = installed
            ? "OledGuard is already installed.\n\nUpdate the installed copy with this one?\n\nYes: update\nNo: just run this copy\nCancel: quit"
            : "Install OledGuard?\n\nIt will be copied to Program Files and start at every logon. It runs with administrator rights so it can see input in admin windows too; Windows will ask for permission once.\n\nYes: install\nNo: just run this copy now\nCancel: quit";
        switch (MessageBox.Show(text, "OledGuard " + Version, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question))
        {
            case DialogResult.Yes: return RunElevated("--install");
            case DialogResult.No: return -1;
            default: return 0;
        }
    }

    public static int Install()
    {
        if (!IsElevated) return RunElevated("--install");
        try
        {
            StopRunning();
            Directory.CreateDirectory(InstallDir);
            if (!RunningFromInstallDir) File.Copy(Application.ExecutablePath, InstalledExe, true);
            int rc = CreateTask(InstalledExe);
            if (rc != 0) throw new Exception("creating the logon task failed (schtasks exit " + rc + ")");
            CreateShortcut(StartMenuLink, InstalledExe);
            using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "OledGuard");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "OledGuard (written by Claude, Anthropic)");
                k.SetValue("DisplayIcon", InstalledExe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + InstalledExe + "\" --uninstall --quiet");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024) + 1, RegistryValueKind.DWord);
            }
            App.Log("installed " + Version + " to " + InstallDir);
            RunTask();
            MessageBox.Show("OledGuard " + Version + " is installed and running.\n\nLook for the black cat in the tray (it may be under the ^ overflow arrow). " +
                "If this is the first install, a window opens where you pick the screens to guard.",
                "OledGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Installation failed: " + ex.Message, "OledGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    public static int Uninstall(bool quiet)
    {
        if (!IsElevated) return RunElevated("--uninstall" + (quiet ? " --quiet" : ""));
        if (!quiet && MessageBox.Show("Uninstall OledGuard?", "OledGuard", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
        StopRunning();
        DeleteTask();
        try { File.Delete(StartMenuLink); } catch { }
        try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }
        if (!quiet && MessageBox.Show("Also delete OledGuard's settings and log?", "OledGuard", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            try { Directory.Delete(App.CfgDir, true); } catch { }
            try { Directory.Delete(App.LogDir, true); } catch { }
        }
        // the running exe can't delete itself: a detached cmd removes the folder once we've exited
        if (Directory.Exists(InstallDir))
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        if (!quiet) MessageBox.Show("OledGuard has been removed.", "OledGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    // Ask a running instance to exit and wait for it, so its exe can be replaced.
    static void StopRunning()
    {
        IntPtr w = Program.FindInstance();
        if (w == IntPtr.Zero) return;
        Native.PostMessage(w, ControlWindow.WM_COMMAND_MSG, new IntPtr(4), IntPtr.Zero);
        for (int i = 0; i < 50 && Program.FindInstance() != IntPtr.Zero; i++) Thread.Sleep(100);
        Thread.Sleep(300);
    }

    static void CreateShortcut(string link, string target)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(t);
        try
        {
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "OledGuard - choose screens / start" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            Marshal.FinalReleaseComObject(lnk);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    public static void ShowAbout()
    {
        MessageBox.Show("OledGuard " + Version + "\n\nPer-screen OLED burn-in guard: blanks each screen after it sits idle, wakes it when you use it.\n\n" +
            "Written by Claude (Anthropic).\n\n" +
            "Provided \"as is\", without warranty of any kind. Use at your own risk. MIT License.",
            "About OledGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--make-icon") { CatIcon.WriteIco(args[1]); return 0; }

        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } // per-monitor v2: every coordinate is physical pixels
        catch (EntryPointNotFoundException) { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string first = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (first == "--install") return Installer.Install();
        if (first == "--uninstall") return Installer.Uninstall(args.Contains("--quiet"));
        if (args.Length > 0) return SendCommand(args); // commands go to the running instance

        if (!Installer.RunningFromInstallDir)
        {
            int r = Installer.Offer();
            if (r >= 0) return r;
        }
        // already running (e.g. Start Menu shortcut): open its screen picker instead
        if (FindInstance() != IntPtr.Zero) return SendCommand(new[] { "screens" });
        // installed but started without elevation: start it through the elevated logon task
        if (Installer.RunningFromInstallDir && !Installer.IsElevated && Installer.TaskExists() && Installer.RunTask() == 0) return 0;

        bool created;
        using (var mutex = new Mutex(true, @"Local\OledGuard", out created))
        {
            if (!created) return 0;
            Application.Run(new App());
            return 0;
        }
    }

    public static IntPtr FindInstance()
    {
        return Native.FindWindowEx(Native.HWND_MESSAGE, IntPtr.Zero, null, ControlWindow.Title);
    }

    // exit codes: 0 sent, 1 not running, 2 bad arguments
    static int SendCommand(string[] args)
    {
        int cmd;
        switch (args[0].ToLowerInvariant())
        {
            case "blank": cmd = 1; break;
            case "wake": cmd = 2; break;
            case "toggle": cmd = 3; break;
            case "exit": cmd = 4; break;
            case "screens": cmd = 5; break;
            default: return 2;
        }
        int index = 0;
        if (args.Length > 1 && !int.TryParse(args[1], out index)) return 2;
        IntPtr w = FindInstance();
        if (w == IntPtr.Zero) return 1;
        Native.PostMessage(w, ControlWindow.WM_COMMAND_MSG, new IntPtr(cmd), new IntPtr(index));
        return 0;
    }
}
