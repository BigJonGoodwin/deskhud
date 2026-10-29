# deskhud-suite

A keyboard-driven Windows 11 desktop with no taskbar: windows tile on their own, a slim bar sits at the
top of every screen, and its colours follow your wallpaper. Three programs, one installer:

- **[GlazeWM](https://github.com/BigJonGoodwin/glazewm/tree/deskhud-stacks)** (the `deskhud-stacks`
  fork) tiles the windows, with numbered workspaces and **stacked tabs**: several windows share one
  tile, with a tab strip to switch between them.
- **[YASB](https://github.com/amnweb/yasb)** draws the bar, with the deskhud theme
  ([source](https://github.com/BigJonGoodwin/yasb/tree/deskhud/themes/deskhud)): media, timer, weather
  and network, CPU/RAM/GPU, tray, Wi-Fi, Bluetooth, volume, battery, clock, notifications.
- **[deskhud](https://github.com/BigJonGoodwin/deskhud)** draws the bar's left island (workspaces,
  stacks, minimized windows), runs the hotkeys, colours the bar and window borders from the wallpaper,
  draws the tab strips, hides the Windows taskbar and keeps YASB running.

![The desktop](screenshots/desktop.png)
*(screenshots to come)*

## Install

Needs Windows 11 (10 may work), PowerShell 5.1 (built in) and a [Nerd Font](https://www.nerdfonts.com/)
for the bar's icons (JetBrainsMono Nerd Font Propo works well). No admin rights.

1. Download `deskhud-suite-<version>-x64.zip` from the [releases](https://github.com/BigJonGoodwin/deskhud/releases) and unzip it.
2. If you use komorebi or another tiling manager, quit it and turn off its autostart first.
3. In the unzipped folder, see what the installer would do:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1 -WhatIf
   ```
4. Install:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

What it does:

| Where | What |
|---|---|
| `%LOCALAPPDATA%\Programs\deskhud-suite\bin` | `deskhud.exe`, `glazewm.exe` and its CLI. Added to your PATH. |
| `C:\Program Files\YASB` | YASB, if you have it already. Otherwise the installer downloads the pinned YASB build, checks its SHA-256 and unpacks it to `%LOCALAPPDATA%\Programs\deskhud-suite\yasb` (no admin). The theme needs YASB's preview build or 2.0.8+. |
| `~\.glzr\glazewm\config.yaml` | GlazeWM: workspaces 1-9, gaps, borders, which windows float. |
| `~\.config\yasb\` | The bar: `config.yaml`, `styles.css`, `deskhud_colors.css` (rewritten by deskhud). |
| `~\.config\deskhud\settings.json` | deskhud: your apps, wallpaper folder, weather city, keyboard hook. |
| `~\.config\whkdrc` | The hotkeys (table below). |
| Task Scheduler `\deskhud-suite\` | Logon tasks `GlazeWM`, `YASB`, `deskhud`, running as you, not elevated. |

Config files that are already there are copied to `~\.deskhud-suite-backups\<date-time>\` first.
Options: `-KeepConfigs` (don't replace existing configs), `-NoStart`, `-NoKeyboardHook` (see below),
`-YasbMsi <file>`, `-GlazeWmZip <zip or URL>`. `Get-Help .\install.ps1 -Detailed` lists them all.

Then open `~\.config\deskhud\settings.json` and set your apps (`apps`), wallpaper folder
(`wallpapers.folder`) and city (`weather.city`). deskhud applies it on save.

### The Windows key

deskhud's keyboard hook owns the Win combos below (Windows reserves them, so ordinary hotkeys cannot
have them). Win combos not in the table keep doing what Windows does. With `"keyboard": { "hook": false }`
(or `install.ps1 -NoKeyboardHook`) deskhud runs no hotkeys, the Win key stays with Windows, and GlazeWM's
own Alt-based keybindings apply instead (`defaults\glazewm\keybindings-alt.yaml`).

## Hotkeys

Edit them in `~\.config\whkdrc`; deskhud picks up changes on save. The bar's keyboard button (or
Win+Shift+/) shows them all.

| Keys | Does |
|---|---|
| Win+Arrows | Focus the window that way |
| Win+Shift+Arrows | Move the window that way |
| **Win+T** | Stack the windows next to this one into tabs, or lay a stack out again |
| Win+Alt+Arrows | Stack onto the window that way (on a stack: pull that window in) |
| Win+[ / Win+] | Previous / next tab |
| Win+Alt+Backspace | Take the window out of its stack |
| Win+Ctrl+Arrows | Resize |
| Win+Z | Snap the window into place |
| Win+Shift+Z | Switch the direction new windows open in |
| Win+Q | Close the window |
| Win+M / Win+Shift+M | Minimize to the bar / restore |
| Win+Ctrl+M | Bring back the next minimized window |
| Win+F / Win+Shift+F | Float / fullscreen |
| Win+1 ... Win+9 | Go to workspace N (a free number opens on the screen you are on) |
| Win+Shift+1 ... 9 | Move the window to workspace N |
| Win+Enter / B / J / O | Terminal / browser / IDE / notes (focus it, or start it) |
| Win+/ | Hotkey cheatsheet |
| Win+Shift+W | Next wallpaper |
| Win+Shift+P | Pause tiling |
| Win+Shift+R | Redraw all windows |
| Win+Shift+O | Reload the GlazeWM config |
| Win+Ctrl+Shift+O | Reload deskhud |

The bar: click the picture button for a new wallpaper on that screen (right-click: the previous one),
the weather to refresh it, the clock for a calendar, the sliders for quick settings.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

Stops the suite's programs (the Windows taskbar comes back), removes the logon tasks, takes the suite off
your PATH and deletes `%LOCALAPPDATA%\Programs\deskhud-suite`. Your configs stay; add `-RestoreBackup` to
put back the ones the installer replaced. A YASB in Program Files stays installed (remove it in
Settings > Apps). `-WhatIf` shows what it would do.

## Building a release

From the deskhud repo, with the GlazeWM fork built next to it (`..\glazewm`, `cargo build --release`)
and the YASB fork at `..\yasb`:

```powershell
powershell -ExecutionPolicy Bypass -File .\packaging\build-release.ps1 -Version v1.0.0
```

It writes `packaging\dist\deskhud-suite-v1.0.0-x64.zip` and its `.sha256`. Test an install without
touching your own setup with `install.ps1 -TargetProfile <empty folder>` (everything goes under that
folder; tasks and PATH are written as files there; nothing starts).

## Credits and licenses

| Part | License | Upstream |
|---|---|---|
| deskhud | GPL-3.0 | [BigJonGoodwin/deskhud](https://github.com/BigJonGoodwin/deskhud) |
| GlazeWM (deskhud-stacks fork) | GPL-3.0 | [glzr-io/glazewm](https://github.com/glzr-io/glazewm) |
| YASB and the deskhud theme | MIT | [amnweb/yasb](https://github.com/amnweb/yasb) |

The release zip carries each license in `licenses\`. Thanks to the GlazeWM and YASB authors; this
package only configures and connects their work.
