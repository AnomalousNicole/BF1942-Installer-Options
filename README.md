# BF1942-Installer-Options

**Battlefield 1942 Options**: the app that [BF1942-Installer](https://github.com/AnomalousNicole/BF1942-Installer) installs next to the game, so players can turn the fixes and extras on or off after install, without the setup file.

This repository is the app's source. BF1942-Installer includes it as a git submodule at `installer\BF1942Options`, and its `build.ps1` builds the app into every installer. To build an installer, start from **[BF1942-Installer](https://github.com/AnomalousNicole/BF1942-Installer)**.

![Battlefield 1942 Options, Fixes and extras tab: the graphics fix, game fixes, player name, CD key, extras and display options, with Play, Open game folder and the troubleshooting guide on the left](docs/screenshot.png)

![The BF42++ settings tab: HUD, chat and console, 3D map markers, controls, performance, and graphics and files, with the explanation of Smoother gameplay showing](docs/screenshot-bf42pp.png)

---

## What it does

One window, in a Battlefield 1942 theme, with two tabs. **Fixes and extras** has every option the installer can switch on one page:

| Option | What it changes |
|---|---|
| Graphics fix | DXVK or dgVoodoo2. **Check my graphics card** runs the same Vulkan check as Setup and picks the right one. Switching swaps the DLLs; `dxvk.conf` and `dgVoodoo.conf` stay in the folder with the player's settings (each fix reads only its own) |
| BF42++ and 3D positional audio | Each on or off on its own. With BF42++ on, DSOAL sits behind BF42++'s `dsound.dll` as `dsound_next.dll`; without it, DSOAL is `dsound.dll` |
| Font size | One of the `Font.rfa` files the installer ships |
| Higher resolution UI, Battle of Britain siren | The modified or the original `menu.rfa` / `Battle_of_Britain.rfa` |
| Borderless1942 | Its exe, a desktop shortcut and windowed mode in `VideoDefault.con` |
| Compatibility Profile | Registers or removes `BF1942.sdb` with `sdbinst` |
| Skip the intro videos | `+restart 1` on the desktop shortcuts |
| Resolution | **Use my screen's resolution** sets every `Video*.con` to the main screen |
| Player name | **Use this name** works like Setup's *Set your player name* in BF1942-Installer: it copies the profile the game starts with to `Settings\Profiles\<name>` (spaces and characters a folder name can't have become `_`), sets the name in it and makes it the one the game starts with |
| CD key | **Generate a CD key**, only when no valid key is registered |

**BF42++ settings** has BF42++'s own settings from `bf42++.ini`, in cards: HUD, chat and console, 3D map markers, controls, performance, and graphics and files (screenshot format, round replays, unpacked game files). Each is a switch, a list or a text box, and the buddy colours are swatches in the order BF42++ hands them out: **+** adds one from a palette (or any colour with **Other colour...**), and clicking a swatch moves it or takes it out. Pointing at a setting's name shows what it does and BF42++'s default. The list, with BF42++'s defaults and ranges, is in `BF42ppSettings.cs`; it follows [sila-skandia/bf42plus](https://github.com/sila-skandia/bf42plus) (Casqade's bf42++ with round replays). Apply writes only the settings that changed, each in place of its own line, so the file's comments, buddy colours and ignore list stay. A colour or number BF42++ wouldn't take keeps Apply off until it is fixed. When the game folder has no `bf42++.ini`, the tab shows the library's copy, which Apply puts there.

The left side has **Play**, **Join** (when the installer has a server shortcut), **Join our Discord** (when it has a Discord invite), **Open game folder** and the **Troubleshooting guide**, and the game folder with **Browse...** to pick another one. With Borderless1942 on, **Play** and **Join** start the game through it, like its desktop shortcut. The app runs as administrator, but it starts the game, folders and links as the signed-in user.

## How it works

- **The library.** Setup keeps a copy of every fix the app can switch, plus the game's original files that the extras replace, in `{app}\Options`, and installs the app in `{app}\Options\App`.
- **The game folder.** Below the folder's path on the left, **Browse...** picks another Battlefield 1942 folder. It must have `BF1942.exe` in it, and the app then uses the library in its `Options` folder. A folder with a library is remembered in the state key (`GameFolder`) and opens the next time. Without a remembered folder, the app opens the folder it is installed in, or else the one in the game's registry entry (`GAMEDIR`, which Setup writes too), whichever has `BF1942.exe`. So a copy of the app downloaded on its own, from this repository's Releases, finds the game, or can be pointed at it.
- **Both tabs fit.** The window is sized for the tab that needs more room, and the cards on both tabs end on the same line, so switching tabs never scrolls.
- **What is on** is read from the game folder every time: a file counts as on when it is identical (SHA-256) to its copy in the library. Extras that the installer was built without have no copy, and show as *Not included in this installer* (Borderless1942 on 32-bit Windows as *Only available on 64-bit Windows*); a font size without a copy is greyed out in the list.
- **`options.json`** next to the exe tells the app what the installer was built with. BF1942-Installer's `build.ps1` writes it from `config.json`:

  | Key | Meaning |
  |---|---|
  | `stateKey` | The registry key Setup keeps its state in (`registryStateKey`), under `HKLM\SOFTWARE\WOW6432Node` on 64-bit Windows (`HKLM\SOFTWARE` on 32-bit) |
  | `generateSerial` | Whether the CD key card is shown (`generateSerial`) |
  | `serverShortcut`, `serverAddress` | The server shortcut's name and `ip:port`, for **Join** (`serverShortcutName`, `serverAddress`) |
  | `discordUrl` | An invite for **Join our Discord** (`discordUrl`) |
  | `separatePrograms` | The separate programs the installer can add (DataField42, Battlefield Rich Presence, PunkBuster: the ones the build includes), named in the note at the bottom. An empty list hides the note |

- **`cover.bmp`** next to the exe, when there is one, is shown at the top of the left side. `build.ps1` copies your largest `branding\WizardImage*.bmp`; without it the side shows the game's name.
- **Administrator rights.** The manifest asks for them, because the app writes to the game folder and `HKLM`, and runs `sdbinst`. Every change is logged to `{app}\Options\Options.log`.

## Building

`build.ps1` in BF1942-Installer does this for you. By hand:

```powershell
dotnet publish BF1942Options.csproj -c Release -o publish -p:GameIcon="C:\path\to\Battlefield 1942\bf1942.ico"
```

- Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`winget install --id Microsoft.DotNet.SDK.10 --exact`).
- WinUI 3 (Windows App SDK), 32-bit, self-contained and trimmed: players need no .NET or Windows App SDK, and it runs on Windows 10 version 1809 or later and Windows 11. The folder is about 70 MB.
- `GameIcon` is optional; it sets the exe and window icon to the game's.

## License

MIT - see [LICENSE](LICENSE). The published app includes the .NET runtime and the Windows App SDK (both MIT, by Microsoft).
