# Test plan 0.5.5 - the setup carries the official BepInEx

What changed: `KeelSetup.exe` carries the official BepInEx 5.4.23.2 zip (hash pinned in `tools/bepinex.ps1`) instead of the copy
from the developer's game folder; the mod list text (`_info.ini`) was corrected. No change to Keel's own code.

## Offline (done 2026-11-13)

| # | Check | Result |
|---|---|---|
| O1 | The files in the setup's payload equal the official zip, file by file (SHA-256) | GREEN |
| O2 | `offline-check --selftest` | GREEN, 207 of 207 |
| O3 | `modtool compat keel` | GREEN against the 0.9.2 refs (Steam build 26651831, renewed 2026-11-13) |
| O4 | `modtool check keel` | GREEN (lint clean, CHANGELOG 0.5.5 / plugin 0.5.5) |

## Before

- His go, `.game-lock`.
- A clean game copy without BepInEx (Flotilla copy), game closed. The Steam install already has BepInEx, so the setup would add nothing there.
- `python tools/modtool.py package keel --install --yes` into that copy.

## In the game (not run)

| # | Check | Pass when |
|---|---|---|
| 1 | Run `KeelSetup.exe` on the clean copy | "Added BepInEx.", starter file and takeover installed; `keel-setup.txt` lists every file |
| 2 | Start the game, switch R.A.T.S. Keel on, restart | `BepInEx\LogOutput.log` shows BepInEx 5.4.23.2 and Keel 0.5.5; the Keel card is on the main menu |
| 3 | Mods start under the official BepInEx | `Started N plugin(s)` with the usual mods on, no new `[Error` lines against a 0.5.4 start |
| 4 | Mod list text | The Mods menu shows the new description: no "delete AnchorChain.Preloader.dll" |
| 5 | Restore in `KeelSetup.exe` | BepInEx, starter file and takeover are gone again, the game starts without mods |

## After

- Remove the clean copy or run Restore; delete `.game-lock`; leave the game open.
