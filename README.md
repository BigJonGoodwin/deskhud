# deskhud: a taskbar-free Windows desktop

A Windows 11 setup where windows tile themselves, a single bar across the top replaces the taskbar,
and the whole thing takes its colours from the wallpaper. It is three programs working together:

| Program | Job |
|---|---|
| [komorebi](https://github.com/LGUG2Z/komorebi) 0.1.41 | Tiling window manager: places windows in tiles, stacks, workspaces. |
| [YASB](https://github.com/amnweb/yasb) 2.0.7 | The top bar: window title, media, timer, weather/network, CPU/RAM, tray, wifi/volume/battery, clock, system icons. |
| **deskhud** (this folder) | A small C# app that glues them together: draws the bar's left island (workspaces and app icons, their right-click menus, a touch keyboard button in tablet posture), runs every hotkey, colours the bar and window borders from the wallpaper, and hides the Windows taskbar. |

deskhud idles at well under 1% of one CPU core and about 60 MB of RAM. It reacts to events instead of polling.

## How the pieces talk

```
 keyboard ─► deskhud KeyboardHook ─► komorebic.exe ─► komorebi ──(event pipe)──► deskhud Strip
                                                                                 (left island)
 wallpaper ─► deskhud Palette ─┬─► ~/.config/yasb/deskhud_colors.css ─► YASB (rest of the bar)
                               └─► komorebi border colours
 YASB buttons ─► deskhud.exe <verb> ─(WM_COPYDATA)─► running deskhud
```

- **Hotkeys** live in `~/.config/whkdrc`. deskhud reads that file (in [whkd](https://github.com/LGUG2Z/whkd)'s
  format, so YASB's hotkey popup can list it) and runs the commands from one low-level keyboard hook.
- **komorebi state** reaches deskhud through `komorebic subscribe-pipe`. Every change redraws the left island.
- **Colours**: deskhud picks the wallpaper's main hues, writes them as CSS variables that YASB's
  `styles.css` imports, and sets komorebi's border colours. It follows Windows' slideshow too.
- **YASB → deskhud**: bar buttons run `deskhud.exe <verb>`, which hands the command to the running
  daemon and exits.

## Where everything lives

| Path | What it is | Edit it to… |
|---|---|---|
| `~/komorebi.json` | komorebi config: gaps, borders, workspaces, apps that need special handling. JSON, so no comments. | Change window gaps (`default_container_padding`), borders, workspaces. Apply with **Win+Shift+O**. |
| `~/applications.json` | komorebi's community rules for awkward apps. | Leave it alone; refresh with `komorebic fetch-app-specific-configuration`. |
| `~/.config/whkdrc` | Every hotkey, one per line: `win + shift + left : komorebic move left`. | Add or change keys. deskhud reloads it on save. |
| `~/.config/yasb/config.yaml` | Which widgets the bar shows and what clicks do. Commented. | Add/remove widgets, change thresholds, clock format, pomodoro times. |
| `~/.config/yasb/styles.css` | How the bar looks. Commented. | Colours, sizes, fonts, glows. |
| `~/.config/yasb/deskhud_colors.css` | **Generated** by deskhud: the wallpaper's colours, and the bubble sizes for laptop or tablet posture (`--bubble-h`, `--icon-size`...). | Don't. It is overwritten on every wallpaper or posture change; sizes live in `YasbColors` (`Palette.cs`). |
| `~/.config/deskhud/settings.json` | deskhud's settings (see below). | Your apps, wallpaper folder, cheatsheet, bar size. Applied on save. |
| `~/.config/deskhud/src/*.cs` | deskhud's source. Every file starts with a header saying what it does. | Behaviour. Rebuild afterwards. |
| `%LOCALAPPDATA%\deskhud\` | deskhud's state: `deskhud.log`, palette, ticker, weather and stack caches, `cheatsheet.json` (where the cheatsheet window last sat, as shares of the screen), `workspaces.json` (which number each screen's workspaces hold). | Nothing; safe to delete, it is rebuilt. |
| `<your vault>/CheetSheets/Hotkeys.md` | The hotkey hub shown by **Win+/** (`cheatsheet` in settings.json): search, app filters, keyboard view and clashes over every app's keys. | Nothing; it draws from the sheets below. |
| `<your vault>/CheetSheets/Hotkeys/*.md` | One sheet per app, plain tables of keys and actions; `Desktop.md` mirrors whkdrc. `_hub/view.js` and `view.css` draw the hub. | Add or change keys; keep `Desktop.md` in step with whkdrc (the hub warns when it isn't). |
| `<your vault>/CheetSheets/cheatsheet-windows.md` | The guide to how the bar, deskhud and komorebi work. | Keep it in step when behaviour changes. |
| Task Scheduler `\desktop\` | Three logon tasks: `komorebi`, `YASB`, `deskhud`. | Turn autostart off or on. |

### Source files

| File | Responsibility |
|---|---|
| `Program.cs` | Entry point: starts the daemon, or forwards `deskhud <verb>` to it. |
| `Daemon.cs` | Wires everything together; UI-side commands (`Dispatch`); file watchers. |
| `Commands.cs` | Multi-step actions: stack/unstack with memory, snap, open app, focus, tile. |
| `Komorebi.cs` | komorebi's state model (every monitor and its workspaces), running `komorebic`, the live event feed, safe restart. |
| `Numbers.cs` | Workspace numbers that follow your screen: which komorebi workspace carries which number, and where a new number goes. |
| `Keyboard.cs` | Reads `whkdrc`; the keyboard hook; the command runner. |
| `Strip.cs` | Draws a left island (the biggest file; sizes are constants at the top), its glows, workspace numbers, and what each right-click menu holds. One island per screen, each showing its own monitor's workspaces; every one offers the minimized windows. |
| `Menus.cs` | How those menus look: dark, accent-lit hover, colour-coded glyphs, rounded accent border. |
| `Palette.cs` | Wallpaper colours → YASB CSS and komorebi borders; wallpaper stepping. |
| `Ticker.cs` | Weather and network text for YASB's ticker widget. |
| `Loose.cs` | Finds untiled windows and gives back the ones komorebi dropped. |
| `Cheatsheet.cs` | Win+/: Obsidian's own window (kept minimized with the hub open) brought to the current workspace at its remembered spot, or sent back to the tray. |
| `Background.cs` | Apps started at login straight into the tray bubble (`startup.minimized`), and keeping them there. |
| `Tray.cs`, `Effects.cs` | Minimized windows; app icons with their shaped glow; the soft bloom behind bubbles. |
| `Shell.cs` | App ids and icons from the shell, so packaged apps (Settings, Calculator...) show real icons. |
| `Taskbar.cs` | Hides the Windows taskbar while deskhud runs, in laptop and tablet posture alike. |
| `Touch.cs` | Tablet posture detection and the on-screen keyboard toggle. |
| `Yasb.cs` | Asks each YASB bar (UI Automation) where its islands start (the centred media island included), so the left islands never run under them; samples the album art's colour for the media glow. |
| `Ipc.cs`, `Native.cs`, `Paths.cs`, `Json.cs`, `Settings.cs` | Plumbing. |

## Build and run

```powershell
powershell -File ~/.config/deskhud/build.ps1
```

That compiles `bin\deskhud.exe` with the C# compiler built into Windows (.NET Framework 4.8). Nothing
needs installing (UI Automation and WIC's image decoders come from the framework's own `WPF` folder, which build.ps1 points at). It stops the running daemon first (so the taskbar comes back cleanly) and starts the
new one afterwards. The compiler only knows **C# 5**: no string interpolation (`$"..."`), no `?.`,
no `nameof`, no `=>` members, and no exception filters.

- **YASB changes**: `config.yaml` and `styles.css` reload on save. If YASB's file watcher has died,
  run `yasbc reload` (this restarts the bar, which takes a few seconds).
- **komorebi.json changes**: run `komorebic check` (komorebi refuses a config with errors and just
  quits), then press **Win+Shift+O** (or run `deskhud.exe restart-komorebi`). komorebi
  saves its layout when it stops and restores it on start, so nothing moves.
- **whkdrc and settings.json**: picked up on save.
- **Logs**: `%LOCALAPPDATA%\deskhud\deskhud.log`, `~/.config/yasb/yasb.log`,
  `%TEMP%\komorebi_plaintext.log.<date>`.

## deskhud commands

Run `deskhud.exe <verb>` from a terminal, a YASB button, or a `whkdrc` line (`deskhud <verb>`).

| Verb | Does |
|---|---|
| `stack <left\|right\|up\|down>` | Stack the window onto its neighbour; on a stack, pull the neighbour in. |
| `unstack` | Pop the front window out of its stack, back where it was. |
| `snap` | Cycle the main split: 1/2 → 2/3 → 1/3. |
| `open <app>` | Focus the app from `apps` in settings.json, or start it. |
| `minimize`, `restore [hwnd]`, `restore-all`, `tray <next\|previous>` | Minimize to the island's tray bubble, bring back, pick. |
| `tile <hwnd>` | Make komorebi tile an untiled window. |
| `focus-window <hwnd>` | Switch to the window's workspace and stack slot. |
| `workspace <n>`, `move-to-workspace <n>` | Win+N / Win+Shift+N: go to workspace number n, or take the focused window there: on its own screen if it is in use, else a new one on the focused screen (see "Several monitors"). |
| `window <hwnd> <action> [n]` | What the icon menu runs: `new`, `minimize`, `float` (toggle), `monocle`, `unstack`, `move <number>`, `location`, `close`. The komorebic ones focus the window first and are skipped (logged) if Windows won't let deskhud bring it forward. |
| `keyboard` | Show or hide the on-screen keyboard. |
| `wallpaper <next\|prev> [here]` | A new picture on one screen: the one under the cursor with `here` (the bar's button), else the one you are on (Win+Shift+W). Pictures come from `wallpapers.folder` (at random while the slideshow shuffles, else by name); `prev` is that screen's previous one. Windows' slideshow keeps running. |
| `cheatsheet` | Bring Obsidian (showing the hotkey hub) to the current workspace at its remembered spot; again, with it in front, back to the tray. |
| `restart-komorebi`, `start-komorebi` | Restart komorebi keeping the layout, or start it. Both run `komorebic check` first and refuse a `komorebi.json` that would not load (the offline bubble then shows its error; clicking it opens the file). |
| `reload` | Re-read settings and hotkeys, rebuild the palette. |
| `reload-yasb`, `settings`, `weather`, `start-menu`, `quit` | As named. |
| `komorebic <args…>` | Pass a command straight to komorebic. |

## settings.json

| Key | Meaning |
|---|---|
| `weather.city` | `"Lisbon"` or `"Springfield, Illinois"`. Empty hides the weather. |
| `weather.units`, `weather.refresh_hours` | `fahrenheit`/`celsius`; how often to refetch. |
| `wallpapers.folder` | Folder the wallpaper button steps through when Windows' slideshow is off. |
| `apps.<name>.exe` / `.start` | For `deskhud open <name>`: the exe to find among open windows, and a Start menu name, path or URI to launch. |
| `taskbar.hide_windows_taskbar` | Hide the Windows taskbar while deskhud runs. |
| `keyboard.win_tap` | What a lone Win tap does: `none`, `start`, or a combo like `alt+space`. |
| `touch.keyboard_button` | The island's touch keyboard bubble: `auto` (only in tablet posture), `always`, `never`. |
| `cheatsheet` | The note Win+/ opens (inside an Obsidian vault for the floating window). Now the hotkey hub. |
| `startup.minimized` | Names from `apps` started at login straight into the tray bubble (today: `notes`, Obsidian). Apps already open are left alone. |
| `bar.height`, `bar.rail`, `bar.island_radius`, `bar.edge_radius` | Bar shape. **Must match YASB** (see below). |
| `bar.strip_max_width` | How wide the left island may grow before its icons shrink. It also stops short of YASB's first island, wherever that is at the moment. |

## Recipes

**Add a hotkey.** Add a line to `~/.config/whkdrc` under a `## Section` header and save:
`win + shift + t : komorebic toggle-tiling`. Modifiers: `win`, `ctrl`, `alt`, `shift`. Keys: letters, digits,
`left`, `return`, `back`, `oem_4` (`[`), `f1`…`f24`, or `0x..` virtual-key codes (`HotkeyFile.Named` in
`Keyboard.cs`). Update the cheatsheet too.

**Make the bar taller or shorter.** The bar is 40 px. Change the same number in `dimensions.height` in
`config.yaml` and `bar.height` in `settings.json`, then rebuild deskhud and reload YASB. Bubble sizes are
the `Laptop` / `Tablet` rows in `YasbColors` (`Palette.cs`), which deskhud writes into `deskhud_colors.css`
as `--bubble-h`, `--inner-h`, `--icon-size`, `--icon-small` and `--text-size`; keep `BubbleH` in `Strip.cs`
at the laptop `--bubble-h` + 2 and `TabletZoom` at (tablet + 2) / BubbleH. The rail and radii live in
`.yasb-bar.adaptive` in `styles.css` and `bar.*` in `settings.json`.

**Several monitors.** Each screen gets its own left island showing that monitor's workspaces (komorebi
keeps a separate set per monitor). Numbers follow your screen, as in i3 (`Numbers.cs`): a workspace
with windows, or on show, holds a number unique across screens and keeps it on its screen. Win+N and
Win+Shift+N (`deskhud workspace` / `move-to-workspace`) go to a number in use on whichever screen holds
it; a free number is made on the screen you are on, from an empty workspace there (`komorebic
ensure-workspaces` adds one when none is left; an empty workspace on show just takes the new number
for Win+N). "The screen you are on" (`Native.ScreenYouAreOn`) is the front window's screen, the one
under the cursor when the desktop or a bar is in front (you clicked an empty screen), and komorebi's
focused monitor when nothing is (komorebi clears focus when it switches to an empty workspace, on any
screen, so keys never land in a hidden window). Empty workspaces out of sight hold no number and stay
off the islands, which list theirs in number order. A screen you plug in shows the lowest free number;
numbers are remembered per screen serial (or port, for screens without one) and slot in
`workspaces.json`, so a replugged screen gets its own back while they are free. The icon menu's "Move
to workspace" lists every number in use plus a new one on this screen. `komorebi.json` gives the
primary 7 workspaces and each other screen 1 to start with. Clicking or scrolling an island acts on its
own monitor (`focus-monitor-workspace`), and Win+arrows cross between monitors. The current workspace
glows fully on the focused monitor and at half strength on the others. Every island offers the
minimized windows (including ones minimized before deskhud started); untiled windows show on the
island of the screen they are on, and leave it as soon as they close.
Ten seconds after the screens change (and after komorebi comes up), deskhud checks that komorebi sees
the same screens as Windows (`Snapshot.MatchesScreens`); when it does not (komorebi 0.1.41 hot-plug
bugs, below), deskhud restarts komorebi once for that arrangement and logs why. A window that arrives on an island pops in
with a ring of its own colour; one that leaves for another workspace, screen or the tray leaves a ghost
sliding that way (`Strip.TrackMoves`; nothing animates while idle).
YASB puts a bar on every screen (`screens: ["*"]`). Its wallpaper button changes the picture of the screen
you click it on (`deskhud wallpaper next here`; right-click: that screen's previous picture), and
Win+Shift+W the screen you are on; the slideshow keeps running. With a different picture on each monitor (Settings >
Personalization > Background, right-click a picture), each island takes its own screen's palette; YASB
and the window borders follow the primary screen's picture, since YASB has one set of colour variables
for every bar (Windows' own "current wallpaper" copy is of whichever screen changed last, so it is not
used then).

**The media island.** What's playing sits in YASB's centre container, an island that exists only while
something plays. Its glow, the play button and the visualizer take the album art's colour: deskhud
samples the art from the screen (`Yasb.cs`, every 5 s while it shows) and writes it as `--media` /
`--media-rgb` into `deskhud_colors.css`. Before anything has played, it is the audio violet.

**Change the glow.** The light rising from behind the bar: `glow` in `PaletteBuilder.FromHues` (`Palette.cs`,
0.32 = 32% of the accent), and the gradient stops in `.yasb-bar` (`styles.css`) and `StripWindow.BarFill`
(`Strip.cs`), which must match. The stronger button glows: `.grouper.system .container` and
`.grouper.clock .container` in `styles.css`, and `StripWindow.Underglow` in `Strip.cs`. The soft bloom
around the island's bubbles is `Glow(rect, colour, strength, reach)` in `Strip.cs`: the focused workspace
glows wide in the focused app's colour and tight in the accent (`DeepBubble`), Home always a little,
others on hover; `LightPool` is the pool of colour under the focused icon. YASB has no blur, so its
hover glows are gradients into each bubble's bottom edge in its function colour (`:hover` rules and
the `--fn-*-rgb` variables in `styles.css`).

**Make an app float (or leave it alone).** In `komorebi.json`, add a rule to `floating_applications`
(komorebi manages it but never tiles it; `float_rule_placement: "Center"` centres it when it opens) or
to `ignore_rules` (komorebi never touches it; for overlays and popups). A rule is
`{ "kind": "Exe" | "Title" | "Class", "id": "...", "matching_strategy": "Equals" | "StartsWith" | ... }`,
or a list of them that must all match. Apply with Win+Shift+O. Rules apply to windows as they open;
float one that is already tiled with Win+F or the icon menu's "Float it". Today: Handy's window floats,
its "Recording" overlay is ignored, and Windows Settings and Vivaldi's settings window float.

**Add keys to the hotkey hub.** Edit the app's sheet in `<your vault>/CheetSheets/Hotkeys/`: one table row per
action under a `## Section`, every combo in its own code span (`` `Ctrl+Shift+T` ``, ranges `` `Win+1–7` ``,
chords `` `Ctrl+B D` ``). A new app is a new sheet with `app`, `color`, `order` and `scope` (`global` when
its keys work everywhere, or a `global:` list of just those) in its properties. The hub needs Dataview's
"Enable JavaScript queries" (on; `.new/plugins/dataview/data.json`, as the vault's config folder is `.new`).

**Add an icon to YASB.** Use Nerd Font Material Design glyphs (`nf-md-*`, written as a surrogate pair
such as `"󰗚"` for U+F05DA). Avoid Font Awesome's U+F000-U+F0FF: YASB draws those in the wrong font.

**Add a wallpaper colour to YASB.** Add a field to `PaletteColors`, compute it in `FromHues`, save and load it
in `PaletteStore`, add a `Var(...)` line in `YasbColors.Write`, then use `var(--your-name)` in `styles.css`.
`rgba(var(--name-rgb), 0.5)` works for transparency.

**Add a deskhud command.** Quick UI-side work: a `case` in `Daemon.Dispatch`. Anything that runs `komorebic`
or might block: a `case` in `Commands.Execute` plus a method. Bind it in `whkdrc` as `deskhud <verb>`.

**Window gaps.** `default_container_padding` in `komorebi.json`, applied with Win+Shift+O; or live per
workspace with `deskhud.exe komorebic container-padding <monitor> <workspace> <px>`.

**CPU/RAM warning levels.** `cpu_thresholds` and `memory_thresholds` in `config.yaml`; the warning colours are
`--load-high` and `--load-critical` in `styles.css`.

## Things learned the hard way

- **Never run `komorebic replace-configuration`.** In komorebi 0.1.41 it piles every window from every
  workspace onto one and breaks stacks. Restart komorebi instead (`deskhud restart-komorebi`).
- **komorebi drops windows that hide for an instant.** Its reaper checks every 20 ms and forgets any tiled
  window that is invisible at that moment, even if it comes straight back (OneCommander does this at startup).
  `Loose.cs` notices and re-announces the window with a synthetic show event, which runs komorebi's normal
  rules again.
- **komorebi decides "float or tile" only when a window first appears.** Title-based float rules miss apps
  that set their title a moment later (Obsidian pop-outs), so deskhud floats those itself.
- **Windows' wallpaper slideshow tells no one when it changes the picture.** deskhud watches the files Windows
  rewrites (`%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper`, and `Transcoded_000`, `_001`... one
  per monitor). Asked per monitor, Windows names the original picture file, while the main wallpaper is
  its own copy, so per-monitor palettes only kick in when the monitors really show different pictures.
- **YASB's hidden widgets still appear to UI Automation**, with an empty rectangle; the media widget only
  counts as showing when its rectangle is real.
- **YASB 2.0.7 can kill itself when screens change.** It restarts to rebuild its bars, the new copy gives
  up waiting for the old one ("Timeout waiting for previous instance" in yasb.log) and the old one quits.
  With no bar, nothing reserves the top of the screen, so windows slide under deskhud's islands and the
  islands' curves end in mid-air. deskhud now checks every 5 s and, after 15 s without YASB, starts it
  through its logon task (`Yasb.cs`, `Watch`).
- **komorebi hands workspace sets to hot-plugged screens unreliably** (one external got 5, another 1),
  which is part of why deskhud numbers workspaces itself rather than relying on komorebi's names.
- **A bad `komorebi.json` only shows at the next start**, and komorebi then quits silently on every
  start (its log ends at "creating window manager from static configuration file"; the island says
  offline). Every workspace needs a `name` in 0.1.41. Run `komorebic check` after editing it: it names
  the line ("missing field `name` at line 61").
- **komorebi 0.1.41 can list a hot-plugged screen twice**: two monitor entries with the same id, serial
  and rectangle. Counting both shifted every number after it (the laptop's second workspace showed as
  4 with two screens) and sent Win+3 to the phantom. `Snapshot.Numbered` keeps only the first entry
  per rectangle, the one `ForScreen` gives that screen's island.
- **komorebi 0.1.41 can also give a hot-plugged screen another screen's rectangle** (a third screen
  came in with the laptop's id and 0,0 2400x1600 after "Invalid monitor handle" in its log). That
  screen's island then finds no workspaces and shows no number, and komorebi would tile its windows
  over the laptop. It sometimes corrects itself; `Daemon.CheckScreens` restarts komorebi when it has
  not ten seconds after the screens settle.
- **Screens left of or above the main one have negative coordinates.** Code that used 0 for "unknown"
  (YASB's edge) threw the left screen's measurements away; edges are now distances from each screen's
  own left side.
- **Windows' slideshow only advances whichever screen is due next**: `AdvanceSlideshow` for a given
  monitor returns "not implemented". Setting one screen's picture (`SetWallpaper`) stops the
  slideshow, and setting it going again before Windows has finished stopping it (about 200 ms) puts the
  old picture back, so `Wallpapers.Resume` waits for the stop first.
- **komorebi may count a window focused while Windows has another in front** (a menu just closed, an
  app opened without focus). `Commands.FocusWindow` then did nothing and the icon menu's actions were
  skipped ("could not bring ... to the front"); it now insists with `ForceForeground`.
- **The tray list lives in memory**, so a deskhud restart used to lose every minimized window from the
  bar, and with no taskbar only Alt+Tab found them; `MinimizedTray.AddExisting` picks them up at start.
- **GDI+ cannot read WebP**, and Windows' slideshow shows WebP pictures happily. Asked per monitor,
  Windows names the original file, so a screen showing a `.webp` got the default blue palette.
  `PaletteBuilder` falls back to WIC (WPF's decoders, which use every codec Windows has).
- **UI Automation is slow to walk.** Finding YASB's containers from each bar's window cost about
  12 ms per bar, which with two bars made deskhud idle near 1%. `Yasb.cs` finds them once per bar and
  keeps them until they go stale; a probe is now under 1 ms per bar (0.3% idle with three screens).
- **YASB drops a whole widget if one option is invalid**, and only says so in `yasb.log`. YASB 2.0.7 has no
  glow or shadow effects; the glows here are gradients. One clock widget styles all its text alike, so date
  and time are two clocks in one bubble.
- **YASB renders Font Awesome's U+F000-U+F0FF glyphs in the wrong font** although JetBrainsMono NFP has
  them (the book showed as "Ḩ", chevrons as "5" and "♠", the ticker's arrows as a speaker). Higher Font
  Awesome code points and every `nf-md-*` glyph are fine.
- **Tablet posture has its own taskbar auto-hide setting** (`TaskbarAutoHideInTabletMode`). With it off,
  folding the keyboard away made Explorer reserve room for the touch taskbar, which deskhud keeps hidden,
  so every tiled window shrank around an empty strip. deskhud turns the setting on while it runs (and
  restores it on exit) and re-applies auto-hide on every posture change.
- **YASB's islands grow leftwards** (long window titles, media starting) into the space the left island
  grows into. A fixed `strip_max_width` let them overlap and cut icons off; `Yasb.cs` now measures
  YASB's first island every 5 s and after focus changes, and the island shrinks its icons to stay clear.
- **The shell's icons for packaged apps are not always premultiplied**, whatever the docs say. Treated as
  premultiplied, Settings' gear punched transparent pixels into the island; `Shell.cs` checks.
- **Only the foreground app may move focus.** The icon menu works because the open menu holds the
  foreground; `deskhud window <hwnd> ...` from a script may not, so those actions check and skip rather
  than letting komorebic act on whatever window happens to be focused.
- **Handy's recording overlay** is a Tauri window titled "Recording" with no frame; komorebi tiled it
  until `ignore_rules` named it. YASB's own popups (the WHKD keybinds list) tiled too; `yasb.exe` is
  ignored now.
- **Obsidian brings its main window up for every `obsidian://` link**, and a fresh Obsidian shows it again
  up to ten seconds into loading, so deskhud keeps putting it back for 20 s after starting it. The
  cheatsheet now uses that window itself: it floats (`floating_applications`), and a minimized window
  rejoins whatever workspace is current when restored, which is how Win+/ brings it to you. One open on
  another workspace is minimized and restored to move it. `float_rule_placement` is `None`, so komorebi
  leaves floating windows where they were (it used to centre them, which fought the remembered spot).
- **A background process may not take focus.** deskhud handles Win+/ while you are typing in another
  app, so it borrows that app's input (`AttachThreadInput`) to focus the cheatsheet; on an empty
  workspace there is no app to borrow from, so it sends itself an Alt press (marked as its own) first.
- **YASB 2.0.7's Pomodoro labels an idle timer `work`**, exactly like a running one, so CSS cannot hide
  the time only while idle.
- **Obsidian pop-out windows have a document of their own.** Dataview's `dv.view` CSS never reached the
  hub's pop-out, so `view.js` puts `view.css` into its own window's head, and builds its elements there.
- **whkd was replaced** because its keyboard hook waited on its command thread, which stalled all typing
  under load. deskhud's hook never waits.
- **Scripting komorebi from another program**: `komorebic` needs komorebi's AF_UNIX socket, which some
  sandboxed shells cannot reach. `deskhud.exe komorebic <args>` works from anywhere. A visible console window
  gets tiled by komorebi, so read state with a hidden window (for example a `.vbs` with `WScript.Shell.Run …, 0`).

## Security model

- **Nothing runs elevated.** The three logon tasks run as you at medium integrity. Keep it that way:
  deskhud runs commands from `whkdrc` (`cmd /c` for lines that are not `deskhud` or `komorebic`) and
  starts programs named in `settings.json`, both files you can write, so an elevated deskhud would let
  any program of yours gain admin rights through them.
- **IPC** (`deskhud <verb>`) is WM_COPYDATA to a message-only window. Windows' UIPI keeps lower-integrity
  (sandboxed) processes from sending it; deskhud never relaxes that, and ignores empty or oversized
  messages. Verbs act on komorebi, your windows and your own config; none runs arbitrary programs.
- **The keyboard hook** sees every key but only looks up hotkeys; the log never records keys, only
  commands and errors.
- **Network**: just the weather, over HTTPS to Open-Meteo, with the city name escaped. JSON is parsed
  into plain dictionaries (no type resolution).
- **Screen and windows**: the album-art colour is read off the screen and never saved; UI Automation
  only reads YASB's bars. App ids and paths handed to Explorer are quoted.

## Reusing this on another PC

Everything personal is in config, not code:

- `settings.json`: `apps` (your browser, IDE, notes app, terminal), `wallpapers.folder`, `cheatsheet`, `weather.city`.
- `whkdrc`: the app keys under `## Apps`.
- `komorebi.json`: workspace names, and `layered_applications` (apps that need it to tile).
- `config.yaml`: the absolute paths to `deskhud.exe` in the YASB button callbacks (they contain the user name).
- The Task Scheduler tasks, and the Obsidian vault path for the cheatsheet.

Build deskhud, start komorebi (`komorebic start`), YASB and deskhud, then add the three logon tasks.
