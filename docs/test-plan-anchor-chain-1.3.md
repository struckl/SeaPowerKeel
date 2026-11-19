# Test plan: Anchor Chain 1.3, the switch to Keel

What changed: Anchor Chain's `AnchorChain.dll` (source in `anchor-chain/`, now 1.3.0) loads every mod as 1.2 did and, once per
start on the main menu, shows a game message box that offers the switch to R.A.T.S. Keel (`anchor-chain/AnchorChain/KeelBridge.cs`):

- **Switch to Keel:**
  - subscribes to Keel (3835527459) and waits for Steam's download
  - switches Keel on in the game's mod list and saves the list
  - copies Keel's stub to `BepInEx\plugins` and its patcher to `BepInEx\patchers`, both recorded in `keel-setup.txt`
  - offers Restart (through `KeelWatcher.exe` from Keel's folder) or Later
- **Not now:** nothing changes; the box comes again at the next start.
- **No box:** when Keel's stub is in `BepInEx\plugins` already.

Where: the test VM (`docs/sandbox.md`), its own Steam account. Nothing goes to the Workshop: the new DLL is copied over the VM's
Anchor Chain Workshop copy by hand.

## Offline (done 2026-11-18)

| # | Check | Result |
|---|---|---|
| O1 | Build against the installed game (0.9.3 public) | GREEN, 0 errors |
| O2 | `compat-check --classload` against 0.9.3 public (26712472) and 0.9.3 beta (26712879) | GREEN, 179 references, 0 missing |
| O3 | Same against 0.9.2 (26651831) and 0.8.7 | GREEN on 0.9.2; 0.8.7: only `IniHandler.open` in `LoadConfigs`, missing there in 1.2 already |
| O4 | VirusTotal on the new `AnchorChain.dll` | see the PR |

## Starts: 4 (A, B, C on public, D on beta)

## Before (game closed, in the VM)

- His go, `.game-lock`. He cannot play Sea Power while the VM runs it (one copy in the family).
- Make the VM an Anchor Chain player:
  - `KeelSetup.exe` Restore, so Keel's stub and patcher are gone
  - unsubscribe from Keel: `Workshop.exe unsubscribe 3835527459 --yes`
  - subscribe to Anchor Chain: `Workshop.exe subscribe 3380210757 --yes`
  - `AnchorChain.Preloader.dll` into `BepInEx\plugins`: build of `anchor-chain/AnchorChain.Preloader`, same code as the 1.2 release
  - copy the new `AnchorChain.dll` (1.3.0) over the one in the VM's `workshop\content\1286220\3380210757`, in both installs
  - mod list: Anchor Chain on, and at least two Anchor Chain mods on (two R.A.T.S. mods)
- Keep copies of `usersettings.ini` and `BepInEx\plugins` in `%TEMP%`.

## Start A: beta install (`C:\Games\SeaPower-Beta`), Anchor Chain player

| # | Check | Pass when |
|---|---|---|
| 1 | Anchor Chain loads the mods | `LogOutput.log`: `Loaded AnchorChain V1.3.0!`; the two mods start |
| 2 | The box on the main menu | It comes after the load screen and its questions; top bar `MESSAGE · Anchor Chain`; text and buttons `Switch to Keel` / `Not now` readable; log `Switch offer shown.` |
| 3 | Not now | Box closes, the menu works; log `Not now: Anchor Chain stays this time.`; no file changed in `BepInEx` |

Then quit the beta install.

## Start B: public install, Anchor Chain player

| # | Check | Pass when |
|---|---|---|
| 4 | The box again | Same as row 2 |
| 5 | Switch to Keel | Progress box `Getting R.A.T.S. Keel ...` with a percentage, then `installed and switched on`; log `Subscribed`, `Keel is on disk`, `switched on`, `stub and takeover patcher` |
| 6 | Files | `BepInEx\plugins\SeaPowerKeel.Stub.dll`, `BepInEx\patchers\SeaPowerKeel.Patcher.dll`; `keel-setup.txt` lists both; `usersettings.ini` `[LoadOrder]` has Keel's folder `,True,3835527459` |
| 7 | Restart | The game closes and starts again by itself (`game.ps1 ignore` for the version question) |

## Start C: public install, the start after the restart

| # | Check | Pass when |
|---|---|---|
| 8 | Keel runs | Keel's loading window and the Keel card on the main menu; `Keel-report.txt` lists the mods as running |
| 9 | Anchor Chain does not start | No `Loaded AnchorChain V1.3.0!` in `LogOutput.log`; `Keel-takeover.txt` names Anchor Chain's preloader |
| 10 | No box | No `Switch offer shown.` |
| 11 | Errors | `grep -c "^\[Error"` not above start B |

## Without a game start

| # | Check | Pass when |
|---|---|---|
| 12 | `KeelSetup.exe` Restore | Stub, patcher and `keel-setup.txt` are gone; Anchor Chain's preloader is still in place |

## Start D: public install after the Restore

| # | Check | Pass when |
|---|---|---|
| 13 | Back to Anchor Chain | `Loaded AnchorChain V1.3.0!`, the mods start, the box comes again |

## Not run (say so in the record)

- Cancel while downloading, a download that fails or takes too long, Later on the restart box.
- 0.8.7.
- A real Workshop update of Anchor Chain: only after he sends the build out.

## After

- Put back the VM's mod list and `BepInEx\plugins` from `%TEMP%`, or `reset-vm.ps1 -Yes`.
- Delete `.game-lock`.
