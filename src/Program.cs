// =================================================================================================
// Program.cs: the entry point.
//
//   deskhud                    starts the daemon (one per Windows session, guarded by a mutex)
//   deskhud <verb> [args...]   hands the command to the running daemon over IPC (Ipc.cs) and exits;
//                              with no daemon running it runs the command itself (Commands.cs)
//
// The verbs are listed in Daemon.Dispatch (quick, UI-side ones) and Commands.Execute (the rest).
// See ../README.md for the whole picture and ../AGENTS.md before editing.
// =================================================================================================

using System;
using System.Threading;
using System.Windows.Forms;

namespace DeskHud
{
    /// deskhud              runs the daemon (one per session)
    /// deskhud <command>    hands the command to the daemon, or runs it directly if none is running
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0) return RunCommand(args);

            bool created;
            using (new Mutex(true, @"Local\deskhud.daemon", out created))
            {
                if (!created) return 0;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Error("unhandled", e.Exception);

                Daemon daemon = null;
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Log.Write("fatal: " + e.ExceptionObject);
                    if (daemon != null) daemon.Dispose(); // gives the taskbar back
                };

                daemon = new Daemon();
                Application.Run(daemon);
            }
            return 0;
        }

        static int RunCommand(string[] args)
        {
            if (IpcWindow.Send(args)) return 0;
            try
            {
                new Commands(Settings.Load).Execute(args);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("command " + string.Join(" ", args), ex);
                return 1;
            }
        }
    }
}
