# OledGuard

**Per-screen OLED burn-in guard for Windows.** When a screen sits idle, OledGuard covers it with black. As soon as you use that screen again, the black goes away. Each screen keeps its own idle timer, so the screen you're working on stays on while the others go dark.

![Tray icon: a black cat. Open eyes = all screens awake, closed eyes = a screen is blanked](cat.png)

## What it does

- **Every screen has its own idle timer.** You choose which screens to guard and how long each waits: 30 s, 1, 2, 5, 10, 15 or 30 min.
- **A screen wakes only when you use it:**
  - moving the mouse about 50 px on that screen;
  - clicking or scrolling on it;
  - typing in a window that is on that screen.
- **Fullscreen video, games and F11 browsers keep their screen on.**
- **The mouse pointer is hidden** over a blanked screen.
- **It adapts to your monitor setup:** any number of monitors, mixed resolutions, rotated screens, different scaling per screen, and monitors being plugged in or unplugged.
- **Resuming from sleep or unlocking the PC** wakes every screen.
- **Tray icon:** a black cat that closes its eyes while any screen is blanked. Left-click it to blank your screens right away. Right-click it for the menu.

## Requirements

- Windows 10 version 1703 or later, or Windows 11.
- .NET Framework 4.8, which is already part of Windows 10 and 11. There is nothing extra to install.
- **Install from an administrator account.** The logon task is created for the account that runs the installer. A standard user who installs by typing another account's admin password would set up the task for that other account. In that case, use **No** (run without installing) or install from the admin account.

## Install

1. Download `OledGuard.exe` from [Releases](../../releases) (or [build it yourself](#build-from-source)).
2. Double-click it and choose **Yes** to install. Windows asks once for administrator permission.
3. A window opens with each screen showing its number. Tick the screens to guard, choose the timeouts, and click **Save**.

The installer does the following:

| What | Where |
|---|---|
| Copies the program | `C:\Program Files\OledGuard\OledGuard.exe` |
| Starts it at every logon | A Task Scheduler task named `OledGuard`, run with administrator rights and no UAC prompt |
| Adds a shortcut | Start Menu → **OledGuard**. Opening it while OledGuard is running brings up the screen picker. |
| Adds an uninstall entry | Settings → Apps → Installed apps → **OledGuard** |

To run it without installing, choose **No** when asked. It then runs from wherever the exe is, until you log off.

### Why administrator rights?

Windows doesn't let a normal program see keyboard or mouse input that goes to an administrator window. Without administrator rights, OledGuard would think a screen was idle while you typed in an admin terminal on it, and would blank it. Running OledGuard elevated avoids that. Installing to Program Files makes sure that only an administrator can replace the program that runs elevated.

### SmartScreen and antivirus

The exe isn't code-signed, so Windows SmartScreen may show "Windows protected your PC" the first time. Click **More info → Run anyway**.

OledGuard watches the mouse and keyboard and starts at logon, so security software may treat it as suspicious. Two design choices reduce this:
- it doesn't use a keyboard hook;
- it never reads which keys you press.

If your antivirus still flags it, you can read every line of the [source](OledGuard.cs) and build the exe yourself.

## Use

| Action | How |
|---|---|
| Blank all guarded screens now | Left-click the cat in the tray (it may be under the **^** overflow arrow) |
| Choose screens and timeouts | Tray menu → **Choose screens...**, or the Start Menu shortcut |
| Blank or wake one screen, guard it or not, set its timeout | Tray menu → the screen's own submenu |
| Allow blanking under fullscreen apps | Tray menu → untick **Keep awake under fullscreen apps** |
| Turn autostart off or on | Tray menu → **Start with Windows** |
| See what it did | Tray menu → **Open log** |

### Command line

OledGuard can be controlled from shortcuts, scripts or hotkey tools. Each command is passed to the copy that is already running:

```text
OledGuard.exe blank [n]     blank screen n, or all guarded screens
OledGuard.exe wake [n]      wake screen n, or all screens
OledGuard.exe toggle [n]    blank if awake, wake if blanked
OledGuard.exe screens       open the screen picker
OledGuard.exe exit          stop OledGuard
OledGuard.exe --install     install, or update an existing install
OledGuard.exe --uninstall [--quiet]
```

Screens are numbered left to right. The picker shows each screen's number on that screen. Exit codes: 0 = command sent, 1 = OledGuard isn't running, 2 = bad arguments.

### Files

- **Settings:** `%APPDATA%\OledGuard\OledGuard.conf`. It is a plain `key=value` file. Settings are stored per monitor using the monitor's own ID, so they stay with the monitor even if Windows renumbers the displays.
- **Log:** `%LOCALAPPDATA%\OledGuard\OledGuard.log`. Once it passes 256 KB, the older half is removed.

## Uninstall

Go to Settings → Apps → Installed apps → **OledGuard** → Uninstall, or run `OledGuard.exe --uninstall`. You'll be asked whether to delete your settings and log as well.

## Privacy

OledGuard makes no network connections and collects no data. It notes that a key was pressed, but never which key. It stores mouse positions only as a running "distance moved" count for each screen. The log records only blanks, wakes and their reasons.

## How it works

- **Overlays.** A blanked screen is covered by a black, borderless window that stays on top. It never takes focus away from your windows, and it hides the mouse pointer.
- **Mouse.** A low-level mouse hook adds up how far the pointer moves on each screen. The count resets after 1 s without movement, so slight sensor jitter never wakes a screen. Clicks and scrolls are matched to the screen under the pointer.
- **Keyboard.** OledGuard uses Raw Input from the keyboard. It sees whether a key went down or up, but not which key. The key press is counted for the screen showing the focused window.
- **Fullscreen.** A screen is treated as fullscreen when the topmost real window covering all of it has no title bar.
- **Accuracy.** Coordinates are in physical pixels (per-monitor DPI aware), so the overlays line up exactly at any scaling.

Everything is in one C# file, [`OledGuard.cs`](OledGuard.cs), which uses only WinForms and Win32 calls.

## Build from source

Run `build.cmd`. It uses the C# compiler that comes with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), so there's no SDK or Visual Studio to install. The build has two passes:
1. a throwaway exe draws the cat icon into `OledGuard.ico`;
2. the real exe is built with that icon embedded.

If OledGuard is running, the build stops it first, because Windows locks a running exe.

## Author

**Written by Claude**, Anthropic's AI model, in a Claude Code session: design, code, installer, icon and this README. The repository owner commissioned it, tested it on their own multi-monitor OLED setup, and directed the work.

## Disclaimer

This software is provided **"as is", without warranty of any kind**, express or implied. That includes, but is not limited to, the warranties of merchantability, fitness for a particular purpose and non-infringement. It is a convenience tool. It lowers the chance of OLED burn-in, but it does not guarantee to prevent it, and it does not replace your monitor's own panel-care features. In no event shall the authors or copyright holders be liable for any claim, damages or other liability arising from the use of this software, including any damage to displays. Use at your own risk. See [LICENSE](LICENSE).
