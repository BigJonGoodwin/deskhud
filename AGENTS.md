# Notes for AI agents editing this setup

Read `README.md` first: it maps every file, command and setting. This file is the rules.

## Build and check
- Build: `powershell -File build.ps1`. It stops a daemon running from `bin\deskhud.exe` (which restores
  the Windows taskbar), compiles, and restarts it. A clean build prints `built ...\deskhud.exe`.
  `build.ps1 -Offline` builds `bin\offline\deskhud.exe` and touches no running deskhud: use it whenever
  someone's desktop is live.
- Tests: `powershell -File tests\run.ps1` (offline build, then tests on the JSON in `tests\fixtures`; it
  never talks to a window manager and moves deskhud's state folder to `%TEMP%\deskhud-tests`). Add a
  fixture and a test for every change to how a window manager's state is read.
- Compiler: Windows' built-in `csc.exe` for .NET Framework 4.8, which is **C# 5 only**. Do not use string
  interpolation, `?.`, `??=`, `nameof`, expression-bodied members (`=> ` on members), exception filters
  (`when`), tuples, `out var`, or local functions. Lambdas, LINQ and generics are fine. No NuGet packages.
- After a change, read `%LOCALAPPDATA%\deskhud\deskhud.log` for new errors, and look at the bar
  (a screenshot from a DPI-aware process; the screen is 2400x1600 at 175%).
- YASB: edits to `~/.config/yasb/config.yaml` / `styles.css` reload on save; otherwise `yasbc reload`
  (restarts the bar). An invalid widget option makes YASB silently drop that widget: check
  `~/.config/yasb/yasb.log` for `Failed to validate widget`.

## Rules
- **Two window managers, one model.** GlazeWM (default) and komorebi both fill `Snapshot` (Snapshot.cs);
  everything else reads only that and calls `Wm.Current` (IWindowManager). Never call `Komorebic` or
  `GlazeIpc` outside their backend files; a new action is a method on IWindowManager implemented in both.
- **GlazeWM IPC**: one request at a time on `GlazeIpc`'s shared socket (worker threads only), the feed
  has its own socket. Messages are split on whitespace by GlazeWM, so no argument may contain spaces.
  Commands that target one window use `command --id <container id>`, so nothing needs focusing first.
- **GlazeWM's tree vs the model**: tiled splits flatten to one tile per window; a `"layout": "tabbed"`
  split is one tile of tabs (PLAN contract with the deskhud-stacks fork). Keep the tolerant fallbacks in
  `GlazeWorkspace.ActiveChild` until the fork's JSON is final, and update `tests\fixtures` from real
  `query monitors` output when it is.
- **Tab strips are never topmost**: they sit just above their active window (`TabStripWindow.Restack`) and
  are re-stacked on every foreground change. Never make one owned by an app window: a cross-process owner
  ties deskhud's UI thread to that app's input queue.
- **Never run `komorebic replace-configuration`.** It scatters every window onto one workspace in
  komorebi 0.1.41. Apply `komorebi.json` with `deskhud.exe restart-komorebi` (keeps the layout).
- **After editing `komorebi.json`, run `komorebic check` and restart komorebi** to prove it loads. An
  invalid config (a workspace without `name`, say) is only noticed at the next start, where komorebi
  quits silently, so leaving it unapplied hides the error until the next boot.
- **The keyboard hook must never block** (`KeyboardHook.OnKey`). It only looks the key up and queues.
  Windows holds every keystroke until the hook returns.
- **UI thread vs workers**: `Daemon`, `StripWindow`, `TabStrips` and their timers run on the UI thread.
  Window manager calls and anything slow go through `CommandQueue` (STA) or a background thread, never
  the UI thread (`IWindowManager.PlanWorkspace` is the one exception: it only reads the snapshot).
- **Stay light**: event-driven, no polling loops, no animations that run while idle. Measure CPU of
  anything left running (deskhud sits around 0.1 to 0.5% of one core).
- **Keep shared numbers in sync**: bar height and shape are in `settings.json` (`bar.*`), YASB's
  `config.yaml` (`dimensions.height`) and `styles.css` (`.yasb-bar.adaptive`), bubble sizes in
  `YasbColors` (`Palette.cs`, written to `deskhud_colors.css`), and `Strip.cs` (`BubbleH`, `TabletZoom`).
- **Several monitors**: every StripWindow belongs to one screen and reads its own `MonitorView`; anything
  it sends must name that monitor (`go-to <monitor> <workspace>`, or `focus-monitor` first), because
  plain window manager commands act on the focused monitor. Workspace numbers come from `Numbers.cs`
  (`WorkspaceView.Number`, or `Snapshot.FindNumber` / `WorkspaceNumbers.Place` for where one goes),
  never from slot positions, and only `Snapshot.Numbered()` monitors count (komorebi can list a screen
  twice). Test with a fake two-monitor state offline: `Snapshot.FromState` numbers what it builds, and
  only the daemon (`WorkspaceNumbers.Load`) writes `workspaces.json`. The bar glow's gradient stops are in both `styles.css`
  (`.yasb-bar`) and `Strip.cs` (`BarFill`). YASB's `-qproperty-grouppadding` is `YasbGroupPadding` in
  `Strip.cs` (it keeps the island clear of YASB's).
- **Coordinates can be negative** (screens left of or above the main one): never use 0 or below as
  "unknown" for an x or y; use NaN, a flag, or an offset from the screen's own corner.
- **Never make deskhud (or its logon task) run elevated**: it runs commands from user-writable config
  (see README, "Security model").
- **YASB icons**: use `nf-md-*` glyphs (surrogate pairs). Font Awesome's U+F000-U+F0FF render in the
  wrong font in YASB.
- **komorebic acts on the focused window.** On komorebi, anything that targets one window by handle must
  focus it first and check it really is in front (`KomorebiWm.WindowAction`); Windows only lets the
  foreground app move focus. GlazeWM commands name the window by id instead.
- **Glows and pixels**: draw on the island with source-over only, and feed GDI+ valid premultiplied
  pixels (`Format32bppPArgb` only when colour ≤ alpha). One bad pixel shows as a hole in the bar.
- **Generated file**: `~/.config/yasb/deskhud_colors.css` is rewritten by deskhud. Change colours in
  `Palette.cs` (`FromHues`, `YasbColors.Write`) or override them in `styles.css`.
- **Keys**: hotkeys live in `~/.config/whkdrc` (the packaged default is `packaging/defaults/whkdrc`). When
  you change one on the owner's machine, update their hotkey hub's Desktop sheet and their guide too (the
  owner may have edited either, so read them first). Mouse actions on the island are listed in both. With
  `keyboard.hook` off deskhud reads no hotkeys at all.
- **Public repo**: no user names, emails, vault or wallpaper paths, monitor serials or home towns in code,
  docs, fixtures or commit metadata. `git grep -i <owner's user name>` must be empty before a push.
- **The hotkey hub** is `CheetSheets/Hotkeys.md` plus one sheet per app in `Hotkeys/`; the sheets are its
  data, so keep their shape (frontmatter `app`/`color`/`scope`/`order`, `## Section` headings, tables of
  `` `combo` `` | action). Test `Hotkeys/_hub/view.js` in Node with a fake `dv` before trusting Obsidian.
- **Testing against the live desktop**: the window manager tiles any visible window you open, including
  console windows, which skews what you measure. Send commands with `deskhud.exe glazewm <message>` (or
  `komorebic <args>`) and read state with a hidden process. Close or move back anything you open, and keep tests short: the owner
  may be using the machine.
- **Testing the island without touching the desktop**: load `deskhud.exe` with reflection, build a
  `StripWindow`, call `SetScale`, set `snapshot` (from `Snapshot.FromGlazeWm` or `Snapshot.FromState` on
  a JSON string) and run
  `Compose` with `g` set to a bitmap's Graphics, as `Render` does. `PrintWindow(strip, dc, 2)` reads the
  live island's pixels, alpha included.

## Style
- Comments are plain prose: a `///` line on each type and on any member whose purpose or reason isn't
  obvious from its name. Explain *why* (the komorebi or Windows quirk being worked around), not what the
  next line does.
- Each source file starts with a header block: what the file does, its main types, and how to make the
  common edits. Keep it true when you change the file.
- Match the surrounding code: small methods, early returns, no speculative options or abstractions.
- Document new settings, commands and gotchas in `README.md`.

## Working with the owner
- Propose a plan and wait for a go-ahead before building features or changing config. Questions get
  answers and options, not implementations. Explicit bug-fix requests can go ahead.
