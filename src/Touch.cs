// =================================================================================================
// Touch.cs: tablet posture and the touch keyboard.
//
// Touch.IsTablet: whether the laptop is in tablet posture (keyboard folded away or detached). Windows
//   announces a change with WM_SETTINGCHANGE; StripWindow watches for it and raises PostureChanged.
// Touch.ToggleKeyboard: opens or closes the on-screen keyboard, for the keyboard bubble the strip shows
//   in tablet posture (the Windows taskbar, which normally has that button, is hidden).
//
// When the keyboard bubble shows: "touch.keyboard_button" in settings.json ("auto", "always", "never").
// =================================================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DeskHud
{
    /// Tablet posture, and the on-screen keyboard.
    static class Touch
    {
        /// True while the keyboard is folded away or detached (Surface Laptop Studio's tablet and stage postures).
        public static bool IsTablet
        {
            get { return Native.GetSystemMetrics(Native.SM_CONVERTIBLESLATEMODE) == 0; }
        }

        /// Opens the on-screen keyboard, or closes it if it is up. Windows has no API for this; the taskbar's
        /// own button talks to TabTip through this COM interface, which needs TabTip running first.
        public static void ToggleKeyboard()
        {
            if (TryToggle()) return;
            string tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), @"microsoft shared\ink\TabTip.exe");
            if (!File.Exists(tabTip))
            {
                Log.Write("keyboard: TabTip.exe not found at " + tabTip);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(tabTip) { UseShellExecute = true }).Dispose();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Error("keyboard: starting TabTip", ex);
                return;
            }
            // TabTip registers its COM object a moment after it starts.
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(150);
                if (TryToggle()) return;
            }
            Log.Write("keyboard: TabTip did not answer");
        }

        static bool TryToggle()
        {
            object tip = null;
            try
            {
                tip = Activator.CreateInstance(Type.GetTypeFromCLSID(TipInvocationClass));
                ((ITipInvocation)tip).Toggle(Native.GetDesktopWindow());
                return true;
            }
            catch (COMException)
            {
                return false;
            }
            finally
            {
                if (tip != null) Marshal.ReleaseComObject(tip);
            }
        }

        static readonly Guid TipInvocationClass = new Guid("4CE576FA-83DC-4F88-951C-9D0782B4E376");
    }

    /// TabTip's toggle: the same call the taskbar's keyboard button makes.
    [ComImport, Guid("37C994E7-432B-4834-A2F7-DCE1F13B834B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITipInvocation
    {
        void Toggle(IntPtr window);
    }
}
