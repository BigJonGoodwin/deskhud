// =================================================================================================
// Paths.cs: where deskhud's files live, atomic writes, and the log.
//
// Config (edited by hand): ~/.config/deskhud/settings.json
// State (written by deskhud): %LOCALAPPDATA%\deskhud\ (deskhud.log, palette.json, ticker.json,
//   weather.json, stacks.json, workspaces.json, resize\). Deleting the state folder is safe; it is rebuilt.
// =================================================================================================

using System;
using System.IO;
using System.Text;

namespace DeskHud
{
    /// File locations. Config is edited by hand; state belongs to deskhud and is safe to delete.
    static class Paths
    {
        static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// DESKHUD_CONFIG_DIR moves it, so a second deskhud can be tried without touching the installed one's settings.
        public static readonly string ConfigDir = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DESKHUD_CONFIG_DIR"))
            ? Path.Combine(Home, ".config", "deskhud")
            : Environment.GetEnvironmentVariable("DESKHUD_CONFIG_DIR");
        public static readonly string SettingsFile = Path.Combine(ConfigDir, "settings.json");
        public static readonly string YasbColors = Path.Combine(Home, ".config", "yasb", "deskhud_colors.css");

        /// DESKHUD_STATE_DIR moves it, so the offline tests never write into a running deskhud's log or caches.
        public static readonly string StateDir = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DESKHUD_STATE_DIR"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "deskhud")
            : Environment.GetEnvironmentVariable("DESKHUD_STATE_DIR");
        public static readonly string LogFile = Path.Combine(StateDir, "deskhud.log");
        public static readonly string TickerFile = Path.Combine(StateDir, "ticker.json");
        public static readonly string WeatherCache = Path.Combine(StateDir, "weather.json");
        public static readonly string PaletteCache = Path.Combine(StateDir, "palette.json");
        public static readonly string StacksFile = Path.Combine(StateDir, "stacks.json");
        public static readonly string WorkspacesFile = Path.Combine(StateDir, "workspaces.json");
        public static readonly string ResizeDir = Path.Combine(StateDir, "resize");

        static Paths()
        {
            Directory.CreateDirectory(StateDir);
            Directory.CreateDirectory(ResizeDir);
        }

        /// Writes through a temp file so readers (YASB polls some of these) never see half a file.
        public static void WriteAtomic(string path, string content)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (IOException)
            {
                // A reader holding the file open blocks Replace; a plain overwrite is good enough then.
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
        }
    }

    /// Appends to %LOCALAPPDATA%\deskhud\deskhud.log, starting over past 256 KB. Never throws.
    static class Log
    {
        const long MaxBytes = 256 * 1024;
        static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    var info = new FileInfo(Paths.LogFile);
                    if (info.Exists && info.Length > MaxBytes) info.Delete();
                    File.AppendAllText(Paths.LogFile,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ") + message + Environment.NewLine);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static void Error(string context, Exception ex)
        {
            Write(context + ": " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
