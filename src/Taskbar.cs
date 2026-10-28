// =================================================================================================
// Taskbar.cs: hides the Windows taskbar while deskhud runs, and gives it back when deskhud exits.
// Switch it off with "taskbar.hide_windows_taskbar": false in settings.json.
//
// Tablet posture has its own auto-hide setting in Windows 11 ("Automatically hide the taskbar when using
// your device as a tablet"). With it off, folding the keyboard away makes Explorer reserve room for the
// touch taskbar again, so every tiled window shrinks around a taskbar nobody can see. TaskbarHider turns
// it on while deskhud runs (and puts it back on exit), and Reassert runs on every posture change.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeskHud
{
    /// Hides the Windows taskbar while deskhud runs and gives it back on exit. Auto-hide releases the
    /// screen space it reserves; hiding the window stops it sliding up. Explorer re-shows it now and
    /// then, so a show hook hides it again.
    sealed class TaskbarHider : IDisposable
    {
        const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        const string TabletAutoHide = "TaskbarAutoHideInTabletMode";

        readonly Native.WinEventProc onShow;
        IntPtr hook;
        IntPtr previousState;
        // The tablet auto-hide value before deskhud changed it; null when it was not set at all.
        object previousTabletAutoHide;
        bool active;

        public TaskbarHider()
        {
            onShow = OnShow;
        }

        public void Apply(bool hide)
        {
            if (hide && !active) Hide();
            else if (!hide && active) Restore();
        }

        /// Explorer restarted: new taskbar windows, new process.
        public void Reattach()
        {
            if (!active) return;
            Unhook();
            SetVisible(false);
            Hook();
        }

        /// The posture changed (keyboard folded away or back). Explorer swaps in the touch taskbar and
        /// re-reads its auto-hide setting for the new posture; ask again so the space stays free.
        public void Reassert()
        {
            if (!active) return;
            var data = AppBarData();
            data.lParam = new IntPtr(Native.ABS_AUTOHIDE);
            Native.SHAppBarMessage(Native.ABM_SETSTATE, ref data);
            SetVisible(false);
        }

        void Hide()
        {
            var data = AppBarData();
            previousState = Native.SHAppBarMessage(Native.ABM_GETSTATE, ref data);
            data.lParam = new IntPtr(Native.ABS_AUTOHIDE);
            Native.SHAppBarMessage(Native.ABM_SETSTATE, ref data);
            previousTabletAutoHide = ReadTabletAutoHide();
            WriteTabletAutoHide(1);
            SetVisible(false);
            Hook();
            active = true;
        }

        void Restore()
        {
            Unhook();
            SetVisible(true);
            var data = AppBarData();
            data.lParam = previousState;
            Native.SHAppBarMessage(Native.ABM_SETSTATE, ref data);
            WriteTabletAutoHide(previousTabletAutoHide);
            active = false;
        }

        static object ReadTabletAutoHide()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(AdvancedKey))
                    return key == null ? null : key.GetValue(TabletAutoHide);
            }
            catch (Exception ex)
            {
                Log.Error("taskbar: reading " + TabletAutoHide, ex);
                return null;
            }
        }

        /// Writes the tablet auto-hide value (null deletes it) and tells Explorer, the way Settings does.
        static void WriteTabletAutoHide(object value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(AdvancedKey))
                {
                    if (value == null) key.DeleteValue(TabletAutoHide, false);
                    else key.SetValue(TabletAutoHide, value, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Log.Error("taskbar: writing " + TabletAutoHide, ex);
                return;
            }
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null), ignored;
            if (tray != IntPtr.Zero)
                Native.SendMessageTimeout(tray, Native.WM_SETTINGCHANGE, IntPtr.Zero, "TraySettings", Native.SMTO_ABORTIFHUNG, 500, out ignored);
        }

        void Hook()
        {
            IntPtr primary = Native.FindWindow("Shell_TrayWnd", null);
            if (primary == IntPtr.Zero) return;
            uint explorer;
            Native.GetWindowThreadProcessId(primary, out explorer);
            hook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, onShow, explorer, 0, Native.WINEVENT_OUTOFCONTEXT);
        }

        void Unhook()
        {
            if (hook == IntPtr.Zero) return;
            Native.UnhookWinEvent(hook);
            hook = IntPtr.Zero;
        }

        void OnShow(IntPtr hookHandle, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != Native.OBJID_WINDOW) return;
            string cls = Native.ClassName(hwnd);
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") Native.ShowWindow(hwnd, Native.SW_HIDE);
        }

        static void SetVisible(bool visible)
        {
            foreach (IntPtr bar in TaskbarWindows()) Native.ShowWindow(bar, visible ? Native.SW_SHOWNA : Native.SW_HIDE);
        }

        static IEnumerable<IntPtr> TaskbarWindows()
        {
            IntPtr primary = Native.FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero) yield return primary;
            IntPtr secondary = IntPtr.Zero;
            while ((secondary = Native.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                yield return secondary;
        }

        static Native.APPBARDATA AppBarData()
        {
            return new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = Native.FindWindow("Shell_TrayWnd", null) };
        }

        public void Dispose()
        {
            if (active) Restore();
        }
    }
}
