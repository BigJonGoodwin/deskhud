// =================================================================================================
// Loose.cs: untiled windows.
//
// Finds open app windows that komorebi is not tiling (the strip shows them in a dashed bubble) and
// hands back the ones komorebi dropped by mistake, or never saw because it was paused.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskHud
{
    /// Open app windows that komorebi is not tiling. The strip shows them in a dashed bubble.
    ///
    /// komorebi drops a tiled window that is invisible for a single 20 ms check, even when it comes back
    /// at once (OneCommander hides itself for a moment while starting), and never takes it back.
    /// Announcing the window again, with a synthetic show event, runs komorebi's normal handling for new
    /// windows, rules included, so a window it ignores on purpose stays ignored.
    sealed class LooseWindows
    {
        readonly HashSet<long> dropped = new HashSet<long>();
        bool announceAll;

        public LooseWindows()
        {
            Windows = new List<long>();
        }

        public List<long> Windows { get; private set; }

        /// Called with every komorebi snapshot: remembers the windows komorebi let go of.
        public void Track(Snapshot previous, Snapshot next)
        {
            if (previous == null) return;
            foreach (long hwnd in previous.Hwnds)
                if (!next.Hwnds.Contains(hwnd)) dropped.Add(hwnd);
            // komorebi ignores everything while paused, so windows opened meanwhile were never seen.
            if (previous.Paused && !next.Paused) announceAll = true;
        }

        /// Re-reads the open windows and hands the dropped ones back to komorebi. Returns true when it
        /// announced any, so the caller can check again once komorebi has had a moment.
        public bool Refresh(Snapshot snapshot)
        {
            var found = snapshot == null ? new List<long>() : Find(snapshot.Hwnds);
            var announced = found.Where(h => announceAll || dropped.Contains(h)).ToList();
            foreach (long hwnd in announced)
                Native.NotifyWinEvent(Native.EVENT_OBJECT_SHOW, new IntPtr(hwnd), Native.OBJID_WINDOW, 0);
            announceAll = false;
            dropped.RemoveWhere(h => found.Contains(h) || (snapshot != null && snapshot.Hwnds.Contains(h)) || !Native.IsWindow(new IntPtr(h)));
            Windows = found.Except(announced).ToList();
            return announced.Count > 0;
        }

        static List<long> Find(HashSet<long> managed)
        {
            var found = new List<long>();
            Native.EnumWindows((hwnd, lParam) =>
            {
                if (!managed.Contains(hwnd.ToInt64()) && IsAppWindow(hwnd)) found.Add(hwnd.ToInt64());
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// A window you would see in Alt+Tab here and now: visible, not minimized, not cloaked (other
        /// virtual desktops, suspended apps), unowned, captioned, and not a tool window.
        static bool IsAppWindow(IntPtr hwnd)
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return false;
            long style = Native.WindowLong(hwnd, Native.GWL_STYLE), ex = Native.WindowLong(hwnd, Native.GWL_EXSTYLE);
            if ((style & Native.WS_CAPTION) != Native.WS_CAPTION || (ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)) != 0) return false;
            int cloaked;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            Native.RECT rect;
            if (!Native.GetWindowRect(hwnd, out rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;
            return Native.WindowTitle(hwnd).Length > 0;
        }
    }
}
