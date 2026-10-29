// =================================================================================================
// Settings.cs: ~/.config/deskhud/settings.json, re-read whenever the file is saved.
//
// To add a setting: add a field with its default below, read it in Load, react to changes in
// Daemon.ReloadSettings if needed, and document it in README.md.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace DeskHud
{
    /// An app for `deskhud open <name>`: the exe to look for among open windows, and how to start it.
    sealed class AppEntry
    {
        public string Exe;
        public string Start;
    }

    /// ~/.config/deskhud/settings.json. Missing keys fall back to the defaults below.
    sealed class Settings
    {
        /// "glazewm" or "komorebi" (WindowManager.cs). Read at start; a change takes a deskhud restart.
        public string WindowManager = "glazewm";
        /// How `start-wm` starts GlazeWM: a name on PATH or a full path.
        public string GlazeWmExe = "glazewm.exe";

        /// deskhud's own keyboard hook runs the hotkey file and owns the Win key. Off: no deskhud hotkeys
        /// at all, and Win combos are left to Windows (and to GlazeWM's own keybindings).
        public bool KeyboardHook = true;

        /// The tab strip over each tabbed container, in logical pixels.
        public int TabsHeight = 26;

        public string WeatherCity = "";
        public bool Fahrenheit = true;
        public double WeatherRefreshHours = 8;

        /// Stepped through by the wallpaper button when Windows' slideshow is off.
        public string WallpaperFolder = "";

        public bool HideWindowsTaskbar = true;

        /// What tapping the Windows key alone does: "start", "none", or a key combo to press.
        public string WinTap = "alt+space";

        /// When the strip shows its touch keyboard bubble: "auto" (in tablet posture), "always" or "never".
        public string KeyboardButton = "auto";

        /// Apps (names under "apps") started at login straight into the tray bubble: running, out of the way.
        public readonly List<string> StartMinimized = new List<string>();

        /// Toggled by `deskhud cheatsheet` (Win+/): a floating Obsidian pop-out when the file is in a vault,
        /// otherwise the file's default app.
        public string Cheatsheet = @"%USERPROFILE%\.config\deskhud\cheatsheet.md";

        // Must match the YASB bar: dimensions.height in config.yaml and the
        // -qproperty values on .yasb-bar.adaptive in styles.css.
        public int BarHeight = 34;
        public int RailHeight = 3;
        public int IslandRadius = 12;
        public int EdgeRadius = 10;
        /// Widest the left island may grow (logical pixels) before workspace icons shrink to fit.
        public int StripMaxWidth = 480;

        public readonly Dictionary<string, AppEntry> Apps = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        public string WallpaperFolderExpanded
        {
            get { return Environment.ExpandEnvironmentVariables(WallpaperFolder ?? ""); }
        }

        public static Settings Load()
        {
            var settings = new Settings();
            if (!File.Exists(Paths.SettingsFile))
            {
                Log.Write("settings.json not found, using defaults");
                return settings;
            }

            try
            {
                object root = Json.Parse(File.ReadAllText(Paths.SettingsFile));

                var wm = Json.Obj(root, "window_manager");
                settings.WindowManager = Json.Str(wm, "backend", settings.WindowManager).Trim().ToLowerInvariant();
                settings.GlazeWmExe = Json.Str(wm, "glazewm_exe", settings.GlazeWmExe).Trim();
                settings.KeyboardHook = Json.Bool(Json.Obj(root, "keyboard"), "hook", settings.KeyboardHook);
                settings.TabsHeight = Math.Max(16, (int)Json.Long(Json.Obj(root, "tabs"), "height", settings.TabsHeight));

                var weather = Json.Obj(root, "weather");
                settings.WeatherCity = Json.Str(weather, "city", settings.WeatherCity).Trim();
                settings.Fahrenheit = !string.Equals(Json.Str(weather, "units", "fahrenheit"), "celsius", StringComparison.OrdinalIgnoreCase);
                settings.WeatherRefreshHours = Math.Max(0.25, Json.Double(weather, "refresh_hours", settings.WeatherRefreshHours));

                settings.WallpaperFolder = Json.Str(Json.Obj(root, "wallpapers"), "folder", settings.WallpaperFolder).Trim();

                settings.HideWindowsTaskbar = Json.Bool(Json.Obj(root, "taskbar"), "hide_windows_taskbar", settings.HideWindowsTaskbar);
                settings.WinTap = Json.Str(Json.Obj(root, "keyboard"), "win_tap", settings.WinTap);
                settings.KeyboardButton = Json.Str(Json.Obj(root, "touch"), "keyboard_button", settings.KeyboardButton).Trim().ToLowerInvariant();
                settings.Cheatsheet = Json.Str(root, "cheatsheet", settings.Cheatsheet);
                foreach (object name in Json.Arr(Json.Obj(root, "startup"), "minimized"))
                    if (name is string) settings.StartMinimized.Add((string)name);

                var bar = Json.Obj(root, "bar");
                settings.BarHeight = (int)Json.Long(bar, "height", settings.BarHeight);
                settings.RailHeight = (int)Json.Long(bar, "rail", settings.RailHeight);
                settings.IslandRadius = (int)Json.Long(bar, "island_radius", settings.IslandRadius);
                settings.EdgeRadius = (int)Json.Long(bar, "edge_radius", settings.EdgeRadius);
                settings.StripMaxWidth = (int)Json.Long(bar, "strip_max_width", settings.StripMaxWidth);

                var apps = Json.Obj(root, "apps");
                if (apps != null)
                {
                    foreach (var pair in apps)
                    {
                        string exe = Json.Str(pair.Value, "exe", null);
                        string start = Json.Str(pair.Value, "start", null);
                        if (!string.IsNullOrEmpty(exe) || !string.IsNullOrEmpty(start))
                            settings.Apps[pair.Key] = new AppEntry { Exe = exe, Start = start };
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("settings.json is not valid JSON, using defaults", ex);
                return new Settings();
            }
            return settings;
        }
    }
}
