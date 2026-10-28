// =================================================================================================
// Keyboard.cs: all hotkeys.
//
// HotkeyFile: reads ~/.config/whkdrc (whkd's format, kept so YASB's hotkey list can show it).
// KeyboardHook: one low-level keyboard hook on its own thread. It matches keys, queues commands, and
//   stops a lone Win tap from opening Start. It must never block: Windows holds every keystroke until
//   the hook answers.
// HotkeyRunner: runs commands one at a time and drops duplicates, so a held key cannot pile up work.
//
// To add or change a hotkey: edit ~/.config/whkdrc. deskhud reloads it on save; no rebuild needed.
// =================================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeskHud
{
    /// Modifier keys, combined in a hotkey.
    [Flags]
    enum Mods { None = 0, Win = 1, Ctrl = 2, Alt = 4, Shift = 8 }

    /// One line of whkdrc: a key, its modifiers and the command it runs.
    sealed class Hotkey
    {
        public ushort Key;
        public Mods Mods;
        public string Command;
        /// Held keys keep firing only for continuous actions (resize, focus, move, cycling).
        public bool Repeats;
    }

    /// Reads the whkd-format hotkey file (~/.config/whkdrc), which stays the single list of keys and
    /// is what YASB's hotkey viewer shows. App-specific [ ] blocks are not supported.
    static class HotkeyFile
    {
        static readonly string[] RepeatingVerbs = { "resize-axis", "resize-edge", "focus", "move", "cycle-focus", "cycle-stack", "cycle-move" };

        public static string FilePath
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("WHKD_CONFIG_HOME");
                return string.IsNullOrEmpty(home)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "whkdrc")
                    : Path.Combine(home, "whkdrc");
            }
        }

        public static List<Hotkey> Load()
        {
            var hotkeys = new List<Hotkey>();
            if (!File.Exists(FilePath)) return hotkeys;

            bool inBlock = false;
            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (inBlock)
                {
                    if (line.StartsWith("]")) inBlock = false;
                    continue;
                }
                if (line.Length == 0 || line[0] == '#' || line[0] == '.') continue;
                if (line.EndsWith("["))
                {
                    Log.Write("hotkeys: app-specific blocks are not supported, skipped: " + line);
                    inBlock = true;
                    continue;
                }

                int colon = line.IndexOf(':');
                Hotkey hotkey = colon > 0 ? Parse(line.Substring(0, colon), line.Substring(colon + 1).Trim()) : null;
                if (hotkey == null) Log.Write("hotkeys: could not read line: " + line);
                else hotkeys.Add(hotkey);
            }
            return hotkeys;
        }

        static Hotkey Parse(string keys, string command)
        {
            if (command.Length == 0) return null;
            string[] parts = keys.Split('+').Select(p => p.Trim().ToUpperInvariant()).ToArray();
            var hotkey = new Hotkey { Command = command };
            for (int i = 0; i < parts.Length; i++)
            {
                Mods mod = Modifier(parts[i]);
                bool last = i == parts.Length - 1;
                if (!last)
                {
                    if (mod == Mods.None) return null;
                    hotkey.Mods |= mod;
                }
                else if (mod != Mods.None || !TryKey(parts[i], out hotkey.Key))
                {
                    return null;
                }
            }

            string[] words = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            hotkey.Repeats = words.Length > 1 && words[0].Equals("komorebic", StringComparison.OrdinalIgnoreCase)
                && RepeatingVerbs.Contains(words[1].ToLowerInvariant());
            return hotkey;
        }

        static Mods Modifier(string name)
        {
            switch (name)
            {
                case "WIN": case "LWIN": case "RWIN": return Mods.Win;
                case "CTRL": case "CONTROL": return Mods.Ctrl;
                case "ALT": case "MENU": return Mods.Alt;
                case "SHIFT": return Mods.Shift;
                default: return Mods.None;
            }
        }

        static readonly Dictionary<string, ushort> Named = new Dictionary<string, ushort>
        {
            { "LEFT", 0x25 }, { "UP", 0x26 }, { "RIGHT", 0x27 }, { "DOWN", 0x28 },
            { "RETURN", 0x0D }, { "ENTER", 0x0D }, { "SPACE", 0x20 }, { "TAB", 0x09 },
            { "BACK", 0x08 }, { "BACKSPACE", 0x08 }, { "ESCAPE", 0x1B }, { "ESC", 0x1B },
            { "DELETE", 0x2E }, { "INSERT", 0x2D }, { "HOME", 0x24 }, { "END", 0x23 },
            { "PRIOR", 0x21 }, { "PAGEUP", 0x21 }, { "NEXT", 0x22 }, { "PAGEDOWN", 0x22 },
            { "OEM_1", 0xBA }, { "OEM_PLUS", 0xBB }, { "OEM_COMMA", 0xBC }, { "OEM_MINUS", 0xBD },
            { "OEM_PERIOD", 0xBE }, { "OEM_2", 0xBF }, { "OEM_3", 0xC0 }, { "OEM_4", 0xDB },
            { "OEM_5", 0xDC }, { "OEM_6", 0xDD }, { "OEM_7", 0xDE }, { "OEM_8", 0xDF }, { "OEM_102", 0xE2 },
        };

        static bool TryKey(string name, out ushort vk)
        {
            if (Named.TryGetValue(name, out vk)) return true;
            if (name.Length == 1 && char.IsLetterOrDigit(name[0]))
            {
                vk = name[0];
                return true;
            }
            int f;
            if (name.Length > 1 && name[0] == 'F' && int.TryParse(name.Substring(1), out f) && f >= 1 && f <= 24)
            {
                vk = (ushort)(0x70 + f - 1);
                return true;
            }
            if (name.StartsWith("0X") && ushort.TryParse(name.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out vk)) return true;
            vk = 0;
            return false;
        }
    }

    /// The one low-level keyboard hook: runs the hotkeys and decides what a lone tap of the Windows key
    /// does. The callback never waits on anything; it looks the key up, queues the command and returns,
    /// because Windows holds every keystroke until each hook has answered.
    sealed class KeyboardHook : IDisposable
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        const int WM_QUIT = 0x0012, WM_APP_REPLAY = 0x8001;
        const uint LLKHF_INJECTED = 0x10;
        const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2;
        const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        // Unassigned key: pressing it between Win down and Win up is how Start is kept closed.
        const ushort SilentKey = 0xE8;
        // Marks input we inject ourselves, so the hook lets it through.
        static readonly IntPtr Signature = new IntPtr(0x44484B44);

        static readonly Dictionary<string, ushort> ComboNames = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            { "ctrl", VK_CONTROL }, { "alt", VK_MENU }, { "shift", VK_SHIFT }, { "win", VK_LWIN },
            { "space", 0x20 }, { "enter", 0x0D }, { "tab", 0x09 }, { "esc", 0x1B },
        };

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        // MOUSEINPUT is the largest member, so it sets the union's size the way SendInput expects.
        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        readonly HookProc proc;
        readonly Thread thread;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly Action<string> run;
        volatile Dictionary<int, Hotkey> hotkeys = new Dictionary<int, Hotkey>();
        volatile string tapAction;
        uint threadId;
        IntPtr hook;
        bool winDown, combined, consumed;

        /// `run` receives each hotkey's command; it is called on the hook thread and must only queue.
        public KeyboardHook(Action<string> run, string tapAction)
        {
            this.run = run;
            SetTapAction(tapAction);
            proc = OnKey;
            thread = new Thread(Loop) { IsBackground = true, Name = "keyboard-hook" };
            thread.Start();
            ready.WaitOne();
        }

        public void SetHotkeys(IEnumerable<Hotkey> list)
        {
            var map = new Dictionary<int, Hotkey>();
            foreach (var hotkey in list) map[Id(hotkey.Key, hotkey.Mods)] = hotkey;
            hotkeys = map;
        }

        /// "start" leaves Windows alone, "none" does nothing, anything else is a combo to press, such as "alt+space".
        public void SetTapAction(string value)
        {
            tapAction = string.IsNullOrWhiteSpace(value) ? "start" : value.Trim().ToLowerInvariant();
        }

        static int Id(ushort key, Mods mods) { return key | ((int)mods << 16); }

        void Loop()
        {
            threadId = GetCurrentThreadId();
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) Log.Write("keyboard: hook failed, error " + Marshal.GetLastWin32Error());
            ready.Set();

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_APP_REPLAY) Replay((ushort)msg.wParam.ToInt32(), msg.lParam != IntPtr.Zero);
            }
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        }

        IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0) return CallNextHookEx(hook, code, wParam, lParam);
            var key = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            if (key.dwExtraInfo == Signature || key.vkCode > 255) return CallNextHookEx(hook, code, wParam, lParam);

            int message = wParam.ToInt32();
            ushort vk = (ushort)key.vkCode;
            bool isWin = vk == VK_LWIN || vk == VK_RWIN;
            bool injected = (key.flags & LLKHF_INJECTED) != 0;

            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
            {
                // The async state is not updated for this event yet, so "already down" means auto-repeat.
                // Reading it here, not keeping our own table, survives key-ups we never see (Win+L).
                bool repeat = Held(vk);
                if (isWin)
                {
                    if (!repeat) combined = consumed = false;
                    winDown = true;
                    return CallNextHookEx(hook, code, wParam, lParam);
                }
                if (winDown) combined = true;

                Hotkey hotkey;
                Mods mods = CurrentMods();
                if (hotkeys.TryGetValue(Id(vk, mods), out hotkey))
                {
                    if (!repeat || hotkey.Repeats) run(hotkey.Command);
                    if ((mods & Mods.Win) != 0) consumed = true;
                    return new IntPtr(1);
                }
            }
            else if (message == WM_KEYUP || message == WM_SYSKEYUP)
            {
                if (isWin && winDown)
                {
                    winDown = false;
                    if (!injected)
                    {
                        bool tap = !combined && tapAction != "start";
                        if (tap || consumed)
                        {
                            // Hold the release back and replay it after the silent key, outside this callback,
                            // so Start stays closed; a tap also presses the configured combo.
                            PostThreadMessage(threadId, WM_APP_REPLAY, new IntPtr(vk), tap ? new IntPtr(1) : IntPtr.Zero);
                            return new IntPtr(1);
                        }
                    }
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        static Mods CurrentMods()
        {
            Mods mods = Mods.None;
            if (Held(VK_LWIN) || Held(VK_RWIN)) mods |= Mods.Win;
            if (Held(VK_CONTROL)) mods |= Mods.Ctrl;
            if (Held(VK_MENU)) mods |= Mods.Alt;
            if (Held(VK_SHIFT)) mods |= Mods.Shift;
            return mods;
        }

        static bool Held(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        void Replay(ushort win, bool tap)
        {
            var inputs = new List<INPUT> { Key(SilentKey, false), Key(SilentKey, true), Key(win, true) };
            List<ushort> combo = tap && tapAction != "none" ? ParseCombo(tapAction) : null;
            if (combo != null)
            {
                foreach (ushort vk in combo) inputs.Add(Key(vk, false));
                for (int i = combo.Count - 1; i >= 0; i--) inputs.Add(Key(combo[i], true));
            }
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        /// "alt+space", "ctrl+shift+f1"... Letters, digits, F-keys and the names above.
        static List<ushort> ParseCombo(string text)
        {
            var keys = new List<ushort>();
            foreach (string part in text.Split('+'))
            {
                string name = part.Trim();
                ushort vk;
                int f;
                if (ComboNames.TryGetValue(name, out vk)) keys.Add(vk);
                else if (name.Length == 1 && char.IsLetterOrDigit(name[0])) keys.Add(char.ToUpperInvariant(name[0]));
                else if (name.Length > 1 && char.ToLowerInvariant(name[0]) == 'f' && int.TryParse(name.Substring(1), out f) && f >= 1 && f <= 24) keys.Add((ushort)(0x70 + f - 1));
                else
                {
                    Log.Write("keyboard: \"" + text + "\" is not a key combo; use start, none, or keys like alt+space");
                    return null;
                }
            }
            return keys;
        }

        static INPUT Key(ushort vk, bool release)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = release ? KEYEVENTF_KEYUP : 0, dwExtraInfo = Signature } },
            };
        }

        public void Dispose()
        {
            PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// Runs hotkey commands one at a time on its own thread. A command that is already waiting is not
    /// queued again, so a held key can never build a backlog: you get as many resizes as komorebi can do.
    sealed class HotkeyRunner : IDisposable
    {
        readonly BlockingCollection<string> queue = new BlockingCollection<string>();
        readonly HashSet<string> pending = new HashSet<string>();
        readonly Func<string[], bool> deskhud;

        /// `deskhud` handles "deskhud ..." commands in-process instead of starting deskhud.exe again.
        public HotkeyRunner(Func<string[], bool> deskhud)
        {
            this.deskhud = deskhud;
            new Thread(Run) { IsBackground = true, Name = "hotkeys" }.Start();
        }

        public void Enqueue(string command)
        {
            lock (pending)
            {
                if (!pending.Add(command)) return;
            }
            try { queue.Add(command); }
            catch (InvalidOperationException) { } // shutting down
        }

        void Run()
        {
            foreach (string command in queue.GetConsumingEnumerable())
            {
                lock (pending) pending.Remove(command);
                try { Execute(command); }
                catch (Exception ex) { Log.Error("hotkey \"" + command + "\"", ex); }
            }
        }

        void Execute(string command)
        {
            string[] words = Split(Environment.ExpandEnvironmentVariables(command));
            if (words.Length == 0) return;
            string program = words[0].ToLowerInvariant();

            if (program == "deskhud" || program == "deskhud.exe") deskhud(words.Skip(1).ToArray());
            else if (program == "komorebic" || program == "komorebic.exe") Komorebic.Run(words.Skip(1).ToArray());
            else Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command) { UseShellExecute = false, CreateNoWindow = true });
        }

        /// Splits on spaces, keeping "quoted parts" together.
        static string[] Split(string text)
        {
            var words = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            foreach (char c in text)
            {
                if (c == '"') quoted = !quoted;
                else if (c == ' ' && !quoted)
                {
                    if (current.Length > 0) words.Add(current.ToString());
                    current.Clear();
                }
                else current.Append(c);
            }
            if (current.Length > 0) words.Add(current.ToString());
            return words.ToArray();
        }

        public void Dispose()
        {
            queue.CompleteAdding();
        }
    }
}
