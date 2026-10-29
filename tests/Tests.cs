// =================================================================================================
// Tests.cs: offline tests for deskhud, run by tests\run.ps1. Nothing here talks to a window manager,
// YASB or the desktop: the GlazeWM and komorebi states are JSON fixtures in tests\fixtures (the GlazeWM
// ones follow its IPC serializers: wm-common's dtos and ipc.rs), and deskhud's state folder is moved
// to a temp folder (DESKHUD_STATE_DIR) before deskhud's code first runs.
//
// To add a test: a method below taking no arguments, listed in Main; use Check / Equal.
// =================================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace DeskHud.Tests
{
    static class Program
    {
        static string fixtures;
        static int failed, passed;

        static int Main(string[] args)
        {
            fixtures = args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "fixtures");
            Run("GlazeWM monitors and workspaces", GlazeMonitors);
            Run("GlazeWM tiles, tabs and floating windows", GlazeContainers);
            Run("GlazeWM active tab fallbacks", GlazeActiveTab);
            Run("GlazeWM fork's recorded tabbed split", GlazeForkSample);
            Run("GlazeWM workspace numbers follow names", GlazeNumbers);
            Run("GlazeWM Win+N and Win+Shift+N commands", GlazeWorkspaceCommands);
            Run("komorebi state still parses", KomorebiState);
            Run("hotkey file in GlazeWM dialect", Hotkeys);
            Run("settings: window manager, keyboard.hook, tabs", SettingsKeys);
            Run("tab strip renders offline (tests\\bin\\tabstrip.png)", TabStripRender);
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        static void Run(string name, Action test)
        {
            int before = failed;
            try { test(); }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("  threw " + ex);
            }
            if (failed == before) passed++;
            Console.WriteLine((failed == before ? "ok    " : "FAIL  ") + name);
        }

        static void Check(bool condition, string what)
        {
            if (condition) return;
            failed++;
            Console.WriteLine("  not true: " + what);
        }

        static void Equal<T>(T expected, T actual, string what)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual)) return;
            failed++;
            Console.WriteLine("  " + what + ": expected " + expected + ", got " + actual);
        }

        static object Fixture(string name) { return Json.Parse(File.ReadAllText(Path.Combine(fixtures, name))); }

        /// The `data` of a recorded `query monitors` reply, as GlazeFeed hands it on.
        static Snapshot Glaze()
        {
            return Snapshot.FromGlazeWm(Json.Obj(Fixture("glazewm-monitors.json"), "data"), false);
        }

        static void GlazeMonitors()
        {
            Snapshot s = Glaze();
            Equal(2, s.Monitors.Count, "monitors");
            Equal(0, s.FocusedMonitor, "focused monitor");
            Equal("TST0001", s.Monitors[0].Key, "primary key (hardware id)");
            Equal(@"\\.\DISPLAY2", s.Monitors[1].Key, "left screen key (device name, no hardware id)");
            Equal(new Rectangle(-1920, 0, 1920, 1080), s.Monitors[1].Bounds, "left screen bounds");
            Equal(0, s.Monitors[0].FocusedWorkspace, "primary shows workspace 1");
            Equal(0, s.Monitors[1].FocusedWorkspace, "left screen shows workspace 2");
            Equal("1", s.Current.Name, "current workspace");
            Check(s.ForScreen(new Rectangle(-1920, 0, 1920, 1080)) == s.Monitors[1], "ForScreen finds the left screen");
            Equal(2, s.Numbered().Count, "numbered monitors");
            Check(s.Numbered()[0] == s.Monitors[0], "primary numbers first");
            Check(s.MatchesScreens(new[] { new Rectangle(0, 0, 2400, 1600), new Rectangle(-1920, 0, 1920, 1080) }), "matches the screens");
        }

        static void GlazeContainers()
        {
            Snapshot s = Glaze();
            WorkspaceView ws = s.Monitors[0].Workspaces[0];
            Equal(2, ws.Containers.Count, "tiles on workspace 1");
            Equal(ws.Containers.Count, ws.Tiles.Count, "one tile rectangle per container");
            Equal(1001L, ws.Containers[0].Windows.Single().Hwnd, "first tile");
            Check(!ws.Containers[0].Tabbed, "a lone window is not tabbed");
            Equal("WindowsTerminal.exe", ws.Containers[0].Windows[0].Exe, "process name gains .exe");
            Equal("win-a", ws.Containers[0].Windows[0].Id, "window id");

            ContainerView tabs = ws.Containers[1];
            Check(tabs.Tabbed, "the tabbed split is one tabbed tile");
            Equal("split-tabs", tabs.Id, "tabbed split id");
            Equal(new Rectangle(1200, 40, 1200, 1560), tabs.Bounds, "tabbed split bounds");
            Equal("1002,1003", string.Join(",", tabs.Windows.Select(w => w.Hwnd)), "tabs in order");
            Equal(1, tabs.Focused, "active tab from activeChildId");
            Equal(1003L, tabs.Visible.Hwnd, "visible tab");

            Equal("1004", string.Join(",", ws.Floating.Select(w => w.Hwnd)), "floating windows");
            Check(s.Hwnds.Contains(1005), "minimized window still counts as managed");
            Check(s.Window(1005) == null, "minimized window is not on the island");
            Equal(1003L, s.FocusedHwnd, "focused window");
            Equal(1, ws.FocusedContainer, "focused tile");
            Check(s.FocusedContainer == tabs, "Snapshot.FocusedContainer");
            Check(s.FindByExe("msedge.exe") != null, "FindByExe with the added extension");

            WorkspaceView two = s.Monitors[1].Workspaces[0];
            Equal(2, two.Containers.Count, "a tiled split is flattened into its windows");
            Equal(1, two.FocusedContainer, "focused tile follows childFocusOrder into the split");
            Equal("Spotify.exe", two.Containers[1].Windows[0].Exe, "an exe name is kept as it is");
            Equal("Horizontal", two.Layout, "workspace tiling direction");

            WorkspaceView web = s.Monitors[1].Workspaces[1];
            Check(web.Monocle, "a fullscreen window counts as monocle");
            Equal("Vertical", web.Layout, "vertical workspace");
        }

        static void GlazeActiveTab()
        {
            Snapshot s = Snapshot.FromGlazeWm(Fixture("glazewm-tabs-index.json"), true);
            Check(s.Paused, "paused is passed through");
            WorkspaceView ws = s.Current;
            Equal(2, ws.Containers.Count, "two tabbed splits");
            Equal("11,12,13,14", string.Join(",", ws.Containers[0].Windows.Select(w => w.Hwnd)), "a nested split's windows are tabs too");
            Equal(3, ws.Containers[0].Focused, "activeChildIndex picks the active tab");
            Equal(1, ws.Containers[1].Focused, "without an active field, the last focused child is active");
        }

        /// The tabbed split from the fork's own IPC test (HANDOVER-glazewm.md), placed in a workspace.
        static void GlazeForkSample()
        {
            Snapshot s = Snapshot.FromGlazeWm(Fixture("glazewm-fork-sample.json"), false);
            ContainerView tabs = s.Current.Containers.Single();
            Check(tabs.Tabbed, "the fork's tabbed split is a tab stack");
            Equal("a73be94b-1071-435a-9337-1d1d0621cb1d", tabs.Id, "split id");
            Equal(new Rectangle(0, 0, 1680, 1000), tabs.Bounds, "the split's full area, where the strip goes");
            Equal(1, tabs.Focused, "active tab from activeChildId");
            Equal("notepad.exe", tabs.Visible.Exe, "active tab's exe");
        }

        static void GlazeNumbers()
        {
            Snapshot s = Glaze();
            Equal(1, s.Monitors[0].Workspaces[0].Number, "workspace \"1\"");
            Equal(3, s.Monitors[0].Workspaces[1].Number, "workspace \"3\"");
            Equal(2, s.Monitors[1].Workspaces[0].Number, "workspace \"2\"");
            Equal(4, s.Monitors[1].Workspaces[1].Number, "a named workspace takes the lowest free number");
            Equal(5, s.FreeNumber(), "free number");
            int monitor, workspace;
            Check(s.FindNumber(2, out monitor, out workspace) && monitor == 1 && workspace == 0, "number 2 is on the left screen");
        }

        static void GlazeWorkspaceCommands()
        {
            Snapshot s = Glaze();
            Equal("focus --workspace 3", Join(GlazeWm.WorkspaceCommands(s, 3, false, 1)), "a number in use: wherever it is");
            Equal("focus --workspace 2", Join(GlazeWm.WorkspaceCommands(s, 2, false, 0)), "a number on another screen: go there");
            Equal("focus --monitor 1; focus --workspace 5", Join(GlazeWM_Free(s, 1)), "a free number on the screen you are on");
            Equal("focus --workspace 5", Join(GlazeWM_Free(s, 0)), "a free number on the focused screen");
            Equal("focus --workspace 5", Join(GlazeWM_Free(s, -1)), "screen unknown: GlazeWM's focused one");
            Equal("focus --workspace web", Join(GlazeWm.WorkspaceCommands(s, 4, false, 0)), "a number held by a named workspace");
            Equal("move --workspace 2; focus --workspace 2", Join(GlazeWm.WorkspaceCommands(s, 2, true, 0)), "move to a number in use");
            Equal("move --workspace 6; focus --workspace 6", Join(GlazeWm.WorkspaceCommands(s, 6, true, 1)), "move to a free number");
        }

        static List<string> GlazeWM_Free(Snapshot s, int yours) { return GlazeWm.WorkspaceCommands(s, 5, false, yours); }

        static string Join(IEnumerable<string> commands) { return string.Join("; ", commands); }

        static void KomorebiState()
        {
            Snapshot s = Snapshot.FromState(Fixture("komorebi-state.json"));
            Equal(1, s.Monitors.Count, "monitors");
            Equal(2, s.Current.Containers.Count, "containers");
            Equal(1, s.Current.FocusedContainer, "focused container");
            Equal(3L, s.FocusedContainer.Visible.Hwnd, "stack front");
            Check(!s.FocusedContainer.Tabbed, "komorebi stacks draw no tab strip");
            Equal("BSP", s.Current.Layout, "layout");
            Equal(1, s.Current.Number, "first workspace numbered 1");
        }

        static void Hotkeys()
        {
            Environment.SetEnvironmentVariable("WHKD_CONFIG_HOME", Path.Combine(fixtures, "whkd"));
            List<Hotkey> keys = HotkeyFile.Load();
            Equal(7, keys.Count, "lines read");
            Hotkey tabs = keys.Single(k => k.Key == 'T');
            Equal(Mods.Win, tabs.Mods, "Win+T modifiers");
            Equal("glazewm command toggle-tabbed", tabs.Command, "Win+T command");
            Check(!tabs.Repeats, "toggle-tabbed fires once per press");
            Check(keys.Single(k => k.Key == 0x25 && k.Mods == Mods.Win).Repeats, "glazewm focus repeats");
            Check(!keys.Single(k => k.Key == 0x25 && k.Mods == (Mods.Win | Mods.Alt)).Repeats, "deskhud stack fires once");
            Check(keys.Single(k => k.Key == 0x27).Repeats, "glazewm resize repeats");
            Check(keys.Single(k => k.Key == 0xDD).Repeats, "deskhud cycle-tab repeats");
            Check(!keys.Single(k => k.Key == '3').Repeats, "deskhud workspace fires once");
            Check(keys.Single(k => k.Key == 'Z').Repeats == false, "komorebic cycle-layout fires once");
        }

        /// Draws the workspace's tab strip into a bitmap the way Render does, without making a window,
        /// and saves it for a look. The active (second) tab is lit in the accent, the other is not.
        static void TabStripRender()
        {
            ContainerView tabs = Glaze().Monitors[0].Workspaces[0].Containers[1];
            PaletteColors colors = PaletteColors.Default();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            using (var icons = new IconCache())
            using (var strip = new TabStripWindow(icons, args => true))
            using (var bitmap = new Bitmap(1200, 46, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bitmap))
            {
                typeof(TabStripWindow).GetField("container", flags).SetValue(strip, tabs);
                typeof(TabStripWindow).GetField("colors", flags).SetValue(strip, colors);
                typeof(TabStripWindow).GetField("scale", flags).SetValue(strip, 1.75f);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                typeof(TabStripWindow).GetMethod("Draw", flags).Invoke(strip, new object[] { g, bitmap.Width, bitmap.Height });
                string png = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tabstrip.png");
                bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);

                // The accent underline sits just above the bottom padding of each tab.
                int underline = bitmap.Height - (int)Math.Round(1.75f * 2) - 2;
                Color active = bitmap.GetPixel(900, underline), idle = bitmap.GetPixel(300, underline);
                Check(Near(active, colors.Accent), "active tab underlined in the accent (got " + active + ")");
                Check(!Near(idle, colors.Accent), "other tab not underlined (got " + idle + ")");
                Check(bitmap.GetPixel(600, 23).A > 200, "strip background is opaque between tabs");
            }
        }

        static bool Near(Color a, Color b) { return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 40; }

        static void SettingsKeys()
        {
            string root = Path.Combine(fixtures, "..", "..");
            Settings example = Settings.Load(Path.Combine(root, "settings.example.json"));
            Equal("glazewm", example.WindowManager, "default backend");
            Check(example.KeyboardHook, "hook on by default");
            Equal(26, example.TabsHeight, "tab height");
            Equal("glazewm.exe", example.GlazeWmExe, "GlazeWM exe");

            Settings off = Settings.Load(Path.Combine(fixtures, "settings-hook-off.json"));
            Equal("komorebi", off.WindowManager, "komorebi backend");
            Check(!off.KeyboardHook, "hook off");
            Equal(30, off.TabsHeight, "tab height override");
            Equal("start", off.WinTap, "win tap");
        }
    }
}
