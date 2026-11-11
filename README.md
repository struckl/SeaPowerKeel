# Sea Power: R.A.T.S. Keel

*R.A.T.S. – Reconnaissance, Acoustics, Terrain & Signals*

A mod loader for Sea Power's code mods and a replacement for Anchor Chain's loader (Anchor Chain mods keep working). It does the job a chainloader does (find the plugin DLLs in the game's mod
folders, sort out dependencies and start order, start them), and on top of that:

- **Checks every mod against your game version before loading it.** A mod built for the other game build used to be able
  to stop *every* mission from loading (one of its types built on something this build lacks makes the game's start-up
  throw). Keel leaves such a DLL out and says why. Smaller mismatches are listed as warnings, including Harmony patches and by-name
  reflection lookups (also of internal classes) whose target this build no longer has (a transpiler that no longer matches
  its target's code is not detected).
- **One broken mod cannot stop the others.** Each mod starts on its own; one that throws is marked and the next one starts.
  No endless load loop, no frozen game.
- **A mod that takes the game down is named and kept out.** A crash in native code ends the game and nothing inside the process
  can catch it, so Keel journals every step to disk. If the game dies while Keel loads or starts a mod, or a crash report shows
  a mod's code on its stack, the next start names that mod in a notice and in the report and keeps it out until the mod is
  updated (`BepInEx\Keel-held.txt`; details in `BepInEx\Keel-crash.txt`). A crash with no mod code on its stack and no mod file loading just before it is reported, but cannot be pinned on a mod.
- **A window that says what crashed.** Keel catches a native crash as it happens and records the code that was running; a small program of
  Keel's (`KeelWatcher.exe`, in Keel's folder, nothing to install) shows a window the moment the game dies, naming the mod when its code was running,
  with a tick box to keep it switched off for now, and offers a restart. The report window's *Let held mods run* releases it again. The game itself cannot be kept alive: a crash in native code ends the process.
- **Logs in one click.** *Save logs*, in the crash window and the report window, makes one zip of all logs on your Desktop, ready to attach to a bug report. The logs contain file paths, so also your Windows user name.
- **Send report.** Next to *Save logs*: packs the same logs with your Windows user name replaced by `%USERNAME%` in every path, shows the file list and asks *Send logs to the R.A.T.S. developer?*, with room for a short note. Only on *Send* does the zip go to the R.A.T.S. feedback board (hub.sewerlabs.dev), where only the developer can open it; you get a report number to mention in a post. No account. Optional: nothing is sent without that click.
- **Stops what it can see coming.** A patch whose parameters do not fit the game method is kept out, because it would corrupt memory. Empty or broken pictures and sounds in mods are listed in the report.
- **Crashes from packs get a name too:** if the game was loading a mod's file just before it crashed, the window says which one and can switch that mod off.
- **Says what happened.** Ctrl+Shift+K in the game shows every mod: running, running with errors, not started and why
  (missing dependency and the version it needs, a conflict, the wrong game build …). Errors a mod throws later are counted
  against it. The same report is in `BepInEx\Keel-report.txt`, and a short notice appears after start-up when something
  went wrong.
- **Always works on the beta branch.** Keel uses nothing of the game that changes between builds; on the 3D globe beta, where
  Anchor Chain's preloader does not start, Keel runs the Anchor Chain mods.
- **A log with times.** `BepInEx\Keel-log.txt` has every log line of the game, BepInEx and all mods with the time of day and
  the seconds since the game started; an exception gets a line saying which mod it came from, with its stack trace (also in
  LogOutput.log). BepInEx's own log has no times and is overwritten at every start; Keel keeps the last sessions too
  (`Keel-log.1.txt`, `.2.txt`, …).
- **One DLL for both game builds** (the flat map of 0.8.7 and the 3D globe build): Keel references no game assembly.
- **The hand-installed part never changes.** The stub in `BepInEx\plugins` only finds and starts Keel; every update comes
  through the Workshop.
- **Runs plain BepInEx plugins and Anchor Chain mods.** While Anchor Chain runs, Keel leaves its mods to it, so nothing
  starts twice (checked again right before Keel starts them). When Anchor Chain is not running (its preloader cannot start
  on the 3D globe build yet), Keel starts those mods itself, the way Anchor Chain does: every DLL is loaded and every mod
  created before the first one starts, `TriggerEntryPoint` is called through the `IAnchorChainMod` interface (explicit
  implementations work), `[ACConfig]` config files are prepared, and `AnchorChainLoader.FindFile` works for mods that call it.

Status: **0.1.4 (Anchor Chain compatibility run in the game with Euromod on globe beta 261107; the `_info.ini` check offline only). 0.1.0 was run in the game on 0.8.7 (flat map, 2026-11-07) and on the 3D globe public beta build 261107 (2026-11-08)**,
a mission on each; 0.1.1 on that beta only; 0.1.2/0.1.3 (Harmony target and by-name lookup checks) were run offline against the installed game and mods, not yet in the game. Test notes: [docs/test-2026-11-07.md](docs/test-2026-11-07.md), [docs/test-2026-11-08-globe.md](docs/test-2026-11-08-globe.md).

## Install

**The easy way:** run `KeelSetup.exe` from Keel's Workshop folder (Steam: right-click Sea Power, Manage, Browse local files, then `..\..\workshop\content\1286220\3835527459`), or download it from the GitHub release. It finds the game, adds BepInEx if needed, installs the starter file and sets another loader's preloader aside; **Restore** undoes it. Windows may say "unknown publisher" because the file is not signed yet: *More info*, *Run anyway*. The manual steps:

1. Subscribe to R.A.T.S. Keel on the Workshop and switch it on in the game's Mods menu.
2. Once: copy `SeaPowerKeel.Stub.dll` from `copy-to-BepInEx-plugins` in Keel's Workshop folder (or from the GitHub release)
   into `Sea Power\BepInEx\plugins`.
3. Start the game. Ctrl+Shift+K shows the report.

**Restart after every change to the mod list.** The stub runs once, while the game starts, and reads the mod list then. **Or let Keel follow:** after the game's *Reload* in the Mods menu (Mods, Apply), Keel starts the code mods you switched on and takes switched-off ones out as far as the running game allows (their patches come off, a BepInEx plugin is stopped; what a mod set up itself stays until a restart, as the game cannot unload code). The same dialog has a *Restart* button (saves the list, closes the game and starts it again), and the report window (Ctrl+Shift+K) has *Restart the game* (click twice). If Keel
(or any mod) was switched off at that moment, nothing loads, and switching it on in the running game does not start it: restart the
game. The same goes for subscribing to, removing or reinstalling a mod, and for Steam re-downloading Keel's folder. If the game
log says "R.A.T.S. Keel is switched off in the game's mod list", switch Keel on in Mods and restart.

**Switching from Anchor Chain:** delete `AnchorChain.Preloader.dll` from `BepInEx\plugins`; Keel then loads the Anchor Chain
mods. You no longer need the Anchor Chain item: Keel ships Anchor Chain's library (MIT) and uses it when no Anchor Chain folder
is switched on (run in the game on the globe beta 261107; on 0.8.7 keep Anchor Chain subscribed, that is the tested way there). Keel now handles ACConfig files itself (checked offline; no mod in the game test used it). With the preloader left in place, Anchor Chain loads its mods as before and Keel steps aside for them.

**Linux (Steam Proton):** follow the manual steps above, and switch Anchor Chain off in **both** places: in the game's Mods menu
*and* by unsubscribing from it on the Steam Workshop. Anchor Chain loads the DLLs of every folder the game lists, switched on
or not, so turning it off in the Mods menu alone leaves it running; Keel then steps aside for its mods. A player got Keel
working on Linux this way (reported 2026-11-10); we have not run it on Linux ourselves.

## Code signing policy

See [docs/code-signing-policy.md](docs/code-signing-policy.md): who may approve a signed release, what the programs change on your PC, and the privacy statement.

## Settings

`BepInEx\config\dev.sewerlabs.seapowerkeel.cfg` (made on the first start):

| Setting | Default | What |
|---|---|---|
| `Loading.AnchorChainMods` | `true` | Start Anchor Chain mods when Anchor Chain is not running |
| `Loading.GameVersionCheck` | `Standard` | `Standard`: leave out DLLs that would stop missions from loading, warn about the rest; `Strict`: leave out any DLL with a missing part (also a missing Harmony patch target or by-name lookup); `Off` |
| `Loading.RemoveBrokenPatches` | `true` | When the game throws 'Method not found' / 'Field not found' because a mod's Harmony patch uses something this build lacks, Keel takes that mod's patch off the failing game method again and names the mod (0.1.6); `false`: Keel only names the mod |
| `Loading.HoldOutCrashers` | `true` | Keep out a mod that took the game down, until it is updated (0.2.0); `false`: Keel only names it |
| `Loading.DisabledPlugins` | empty | Plugin ids Keel does not start although their folder is on |
| `Logging.TimestampedLog` | `true` | Write `BepInEx\Keel-log.txt` with times and the mod each exception came from |
| `Logging.KeepSessions` | `3` | Earlier sessions' logs to keep |
| `Report.Hotkey` | `Ctrl+Shift+K` | Report window |
| `Report.NoticeOnProblems` | `true` | Notice after start-up when a mod did not start or threw |
| `Loading window.Show` | `true` | The loading window ([docs/loading-window.md](docs/loading-window.md)) |
| `Loading window.Spot` | `Marker` | `Marker` (the blank band under the main menu's news box), `TopLeft`, `TopRight`, `BottomLeft`, `BottomRight` of the game's window |
| `Loading window.Scale` | `0` | Size; 0 = by the game window's height (1.5 on a 4K screen) |
| `Loading window.Opacity` | `0.92` | 0.4 to 1 |
| `Loading window.SecondsAfterLastMod` | `2` | The full window stays this long after every mod has started, then glides to the corner |
| `Loading window.SummarySeconds` | `60` | How long the summary stays in the corner before it fades (0 = until you right click it) |
| `Loading window.LongestSeconds` | `300` | The full window is done this long after the game process started, whatever happens |
| `Loading window.ClickHides` | `true` | A click hides the full window, a right click the summary; a click on the summary opens Keel's report |

## Loading window

While the game starts, a small window of its own shows the start-up step, the mods as Keel starts them (newest eight, with the time each took), a
bar, and, when a mod holds the game up, its name and for how long ("Starting 17/19: VopSound 1.96.29  8 s"; when a mod keeps the game busy after
it was started, "Waiting 19 s after Dynamic Campaign Mod"). It is a Windows window on its own thread, so it keeps going while a mod blocks the
game's main thread, and it opens before the slow steps (Anchor Chain's preloader, the mods). It sits on the main menu's blank band by default.
It belongs to the game's window: it stays above the game but not above other programs, and goes when the game is minimized. When every mod has
started it shrinks and glides to the bottom left, above the game's version text, as a summary card ("Keel 0.1.5 started 19 mods in 40 s", the
time until the game was ready, skipped mods, problems or the slowest mod); a click on the card opens Keel's report, a right click hides it, and
it fades after a minute. It only reads log lines and never takes the keyboard focus. Keel also logs a
`STARTING i/n: name` line before each mod now, so `Keel-log.txt` names the mod that was running when the game stood still. New in 0.1.5: drawn
offline from a real log and its thread smoke-tested, **not yet run in the game**. Where the start-up time goes and how it works:
[docs/loading-window.md](docs/loading-window.md). `tools/loading-preview` draws the window from a log without the game.

## How it works

1. The **stub** (`BepInEx\plugins\SeaPowerKeel.Stub.dll`) reads the game's mod list from `usersettings.ini`, finds
   `SeaPowerKeel.dll` in an enabled mod folder (newest version wins) and starts it.
2. Keel waits for the game's own mod list (on the globe build also for the end of the start-up load screen, as mods
   started during it can keep the unit data from loading).
3. It reads the plugin attributes of every DLL in the enabled folders **without loading them** (Mono.Cecil, which BepInEx
   ships), and checks each DLL against the game's `Managed` assemblies.
4. Duplicates (newest copy wins), plugins already running from `BepInEx\plugins`, switched-off ids, dependencies with
   version ranges and conflicts are worked out until nothing changes. Then every DLL is loaded and every Anchor Chain mod
   created (a mod that cannot be loaded, or has no `IAnchorChainMod.TriggerEntryPoint`, is FAILED and takes the mods that
   need it with it), and only then the rest starts in dependency order, otherwise in the game's mod-list order.

Writing a mod for it: [docs/for-mod-authors.md](docs/for-mod-authors.md) (BepInEx plugins, Anchor Chain mods, `keel.ini`
for mods with one DLL per game build).

## Differences from Anchor Chain's loader

Keel is meant to run the same mods; where it deliberately does not behave like Anchor Chain 1.1.0 / 1.2.0, the report says so:

- **Only the exact `AnchorChain.dll` and `AnchorChain.Preloader.dll` are left out** (and Keel's own DLLs); every DLL left out is
  listed with the reason in the report. A mod called `AnchorChainSomething.dll` is scanned like any other.
- **Only public classes count** as Anchor Chain plugins, as in Anchor Chain (`GetExportedTypes`); a non-public plugin class is
  listed as SKIPPED with that reason.
- **`[ACIncompatibility]`:** Anchor Chain drops *every* mod that declares one, whether or not the named mod is installed (its
  `toRemove.Add` sits outside the `if`, in the released 1.1.0 and in the current source: an upstream bug). Keel skips a mod only
  when a named mod is really there; a mod that starts only because of this carries a warning in the report.
- **`before` / `after`** only order the start; an absent plugin named there does not keep the mod from starting (Anchor Chain
  treats them as dependencies).
- **Switched-off mod folders** are not loaded. (Anchor Chain loads the DLLs of every folder the game lists, switched on or
  not; mods that check the Mod Manager themselves, like Euromod's, behave the same either way.)
- **`[ACConfig]`:** like Anchor Chain (default `<id>.ini` in the mod folder, the player's `<id>_user.ini` in
  `StreamingAssets\ACConfigs`, missing keys merged in, `[AnchorChain.ResetValues]` applied once). The game's `IniHandler` is
  used by reflection; it has everything needed on 0.8.7, the globe build 26223125 and 261107 (`tools/offline-check --inicheck`).
  Should a build lack a member, Keel copies the default file, says what it could not do, and starts the mod.
- **`AnchorChainLoader.FindFile`** (Euromod's Sonar Audio Loader uses it for audio in other mods' folders) works: Keel
  provides the one interface of Anchor Chain's preloader that Anchor Chain's DLL needs to load that class, and fills the
  class's folder list.

## Development

```
dotnet build src/SeaPowerKeel/SeaPowerKeel.csproj -c Release          # -p:Deploy=true copies it into StreamingAssets\SeaPowerKeel
dotnet build src/SeaPowerKeel.Stub/SeaPowerKeel.Stub.csproj -c Release
```

`-p:GameDir=...` for another install. `tools/package.ps1` (or `python tools/modtool.py package keel --yes` in the workspace) stages
`dist\SeaPowerKeel\` for the Workshop. Workshop item 3835527459 (private for now); page text and picture in `workshop/`. `tools/offline-check` runs Keel's scanner, game-version check and planner over mod
folders on disk against a game build's `Managed` folder, without the game:

```
dotnet run -c Release --project tools/offline-check -- "<Managed>" "<Sea Power>\BepInEx\core" "<steamapps>\workshop\content\1286220"
```

Keel's own tests (the scanner, entry point, start order, `[ACConfig]`, Anchor Chain hand-over ... on stand-in mods and a stand-in
`IniHandler`; no game needed), and the check that the game's real `IniHandler` has what `[ACConfig]` needs (one build per run):

```
dotnet run -c Release --project tools/offline-check -- --selftest "<Managed>" "<Sea Power>\BepInEx\core"
dotnet run -c Release --project tools/offline-check -- --inicheck "<Managed>"
```

The Anchor Chain compatibility changes (CHANGELOG, "Unreleased") are checked offline only; they have not been run in the game.

## Credits

Written from scratch. Anchor Chain (SeaPower-Modders, MIT) is the community chainloader Sea Power code mods are built on;
Keel reads Anchor Chain mods through their public attributes and interface so players can use both.

MIT license.

*R.A.T.S. – Ratte's Assorted Tactical Systems*
