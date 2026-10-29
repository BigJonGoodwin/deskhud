// =================================================================================================
// Native.cs: Win32 declarations (P/Invoke) and a few small wrappers. Add new Windows API calls here.
// =================================================================================================

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskHud
{
    static class Native
    {
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        public const long WS_CAPTION = 0x00C00000;
        public const uint GW_OWNER = 4;
        public const uint GW_HWNDPREV = 3;
        public const int DWMWA_CLOAKED = 14;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const int WM_CLOSE = 0x0010;
        public const int WM_SETTINGCHANGE = 0x001A;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_COPYDATA = 0x004A;
        public const int WM_GETICON = 0x007F;
        public const int WM_DPICHANGED = 0x02E0;
        public const int MA_NOACTIVATE = 3;

        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_OVER = 0;
        public const byte AC_SRC_ALPHA = 1;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_SHOWMINNOACTIVE = 7;
        public const int SW_SHOWNA = 8;
        public const int SW_RESTORE = 9;

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        public const uint EVENT_OBJECT_DESTROY = 0x8001;
        public const uint EVENT_OBJECT_SHOW = 0x8002;
        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const int OBJID_WINDOW = 0;

        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int ICON_BIG = 1;
        public const int ICON_SMALL2 = 2;
        public const int GCLP_HICON = -14;
        public const int GCLP_HICONSM = -34;
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        public const uint SPI_SETDESKWALLPAPER = 0x0014;
        public const uint SPI_GETDESKWALLPAPER = 0x0073;
        public const uint SPIF_UPDATEINIFILE = 0x01;
        public const uint SPIF_SENDCHANGE = 0x02;

        public const byte VK_LWIN = 0x5B, VK_MENU = 0x12;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public const uint ABM_GETSTATE = 0x04;
        public const uint ABM_SETSTATE = 0x0A;
        public const int ABS_AUTOHIDE = 0x01;

        /// 0 while a convertible is in tablet posture (keyboard folded away or detached).
        public const int SM_CONVERTIBLESLATEMODE = 0x2003;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int Width, Height;
            public SIZE(int width, int height) { Width = width; Height = height; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetClassLongW")] static extern uint GetClassLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT point, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint to, bool on);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] public static extern void NotifyWinEvent(uint eventType, IntPtr hwnd, int idObject, int idChild);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

        public static IntPtr GetClassLongPtr(IntPtr hwnd, int index)
        {
            return IntPtr.Size == 8 ? GetClassLongPtr64(hwnd, index) : new IntPtr(GetClassLong32(hwnd, index));
        }

        public static long WindowLong(IntPtr hwnd, int index)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index).ToInt64() : GetWindowLong32(hwnd, index);
        }

        public static string ClassName(IntPtr hwnd)
        {
            var name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            return name.ToString();
        }

        public static string WindowTitle(IntPtr hwnd)
        {
            var text = new StringBuilder(512);
            GetWindowText(hwnd, text, text.Capacity);
            return text.ToString();
        }

        public static string ProcessPath(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return null;
            try
            {
                var path = new StringBuilder(1024);
                int size = path.Capacity;
                return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        /// True when the window covers its whole monitor (games, video, F11). Returns that monitor.
        public static bool IsFullscreen(IntPtr hwnd, out IntPtr monitor)
        {
            monitor = IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return false;
            string cls = ClassName(hwnd);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false;

            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) return false;
            monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            return rect.Left <= info.rcMonitor.Left && rect.Top <= info.rcMonitor.Top
                && rect.Right >= info.rcMonitor.Right && rect.Bottom >= info.rcMonitor.Bottom;
        }

        /// The screen you are on (physical pixels): the one with the window in front, or, when the front
        /// is the desktop or a bar (clicking an empty screen), the one under the cursor. Empty when no
        /// window has focus, which is how komorebi leaves a switch to an empty workspace (on any
        /// screen): its focused monitor is the answer then.
        public static System.Drawing.Rectangle ScreenYouAreOn()
        {
            IntPtr front = GetForegroundWindow();
            if (front == IntPtr.Zero) return System.Drawing.Rectangle.Empty;
            if (!IsIconic(front) && !IsShellOrBar(front)) return System.Windows.Forms.Screen.FromHandle(front).Bounds;
            return ScreenAtCursor();
        }

        public static System.Drawing.Rectangle ScreenAtCursor()
        {
            POINT cursor;
            GetCursorPos(out cursor);
            return System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).Bounds;
        }

        static bool IsShellOrBar(IntPtr hwnd)
        {
            string cls = ClassName(hwnd);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return true;
            string exe = ProcessPath(hwnd);
            return exe != null && exe.EndsWith(@"\yasb.exe", StringComparison.OrdinalIgnoreCase);
        }

        /// The display scale (1.75 at 175%) of the monitor showing the centre of `bounds`; 0 if unknown.
        public static float ScaleAt(System.Drawing.Rectangle bounds)
        {
            var centre = new POINT(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            uint dpiX, dpiY;
            IntPtr monitor = MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);
            return GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) == 0 ? dpiX / 96f : 0;
        }

        /// SetForegroundWindow from a process the user is not typing into (a hotkey handled in the
        /// background): Windows lets a thread that shares input with the foreground window move focus.
        public static void ForceForeground(IntPtr hwnd)
        {
            IntPtr current = GetForegroundWindow();
            uint pid, mine = GetCurrentThreadId();
            uint theirs = current == IntPtr.Zero ? 0 : GetWindowThreadProcessId(current, out pid);
            bool attached = theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(mine, theirs, false);
            }
            if (GetForegroundWindow() == hwnd) return;
            // Nothing to borrow focus from (an empty workspace): a key press of our own makes deskhud
            // the last process with input, which Windows lets move focus. Marked as ours, so deskhud's
            // keyboard hook lets it through.
            var ours = new UIntPtr(0x44484B44);
            keybd_event(VK_MENU, 0, 0, ours);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, ours);
            SetForegroundWindow(hwnd);
        }

        /// A tap of the Windows key, which opens Start.
        public static void TapWinKey()
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}
