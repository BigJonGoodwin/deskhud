// =================================================================================================
// Ipc.cs: how `deskhud <verb>` (from YASB, a script or a terminal) reaches the running daemon:
// WM_COPYDATA to a hidden message-only window.
// =================================================================================================

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeskHud
{
    /// `deskhud <command>` from YASB or a terminal hands its arguments to the running daemon through
    /// WM_COPYDATA to this message-only window, then exits.
    sealed class IpcWindow : NativeWindow, IDisposable
    {
        const string Title = "deskhud.ipc";
        readonly Func<string[], bool> onCommand;

        public IpcWindow(Func<string[], bool> onCommand)
        {
            this.onCommand = onCommand;
            CreateHandle(new CreateParams { Caption = Title, Parent = Native.HWND_MESSAGE });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_COPYDATA)
            {
                var data = (Native.COPYDATASTRUCT)Marshal.PtrToStructure(m.LParam, typeof(Native.COPYDATASTRUCT));
                // Any program of this user at the same or higher integrity can send here (UIPI keeps
                // sandboxed ones out); ignore empty or oversized messages instead of trusting them.
                if (data.lpData == IntPtr.Zero || data.cbData < 2 || data.cbData > 64 * 1024)
                {
                    m.Result = IntPtr.Zero;
                    return;
                }
                string payload = Marshal.PtrToStringUni(data.lpData, data.cbData / 2);
                m.Result = onCommand(payload.Split('\n')) ? new IntPtr(1) : IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        /// False when no daemon is running.
        public static bool Send(string[] args)
        {
            IntPtr daemon = Native.FindWindowEx(Native.HWND_MESSAGE, IntPtr.Zero, null, Title);
            if (daemon == IntPtr.Zero) return false;

            string payload = string.Join("\n", args);
            IntPtr buffer = Marshal.StringToHGlobalUni(payload);
            try
            {
                var data = new Native.COPYDATASTRUCT { cbData = payload.Length * 2, lpData = buffer };
                Native.SendMessage(daemon, Native.WM_COPYDATA, IntPtr.Zero, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return true;
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }
}
