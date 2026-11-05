# Sea Power: R.A.T.S. Keel

*R.A.T.S. – Reconnaissance, Acoustics, Terrain & Signals*

A mod loader for Sea Power's code mods. It does the job a chainloader does (find the plugin DLLs in the game's mod
folders, sort out dependencies and start order, start them), and on top of that:

- **Checks every mod against your game version before loading it.** A mod built for the other game build used to be able
  to stop *every* mission from loading (one of its types built on something this build lacks makes the game's start-up
  throw). Keel leaves such a DLL out and says why. Smaller mismatches are listed as warnings.
- **One broken mod cannot stop the others.** Each mod starts on its own; one that throws is marked and the next one starts.
  No endless load loop, no frozen game.
- **Says what happened.** Ctrl+Shift+K in the game shows every mod: running, running with errors, not started and why
  (missing dependency and the version it needs, a conflict, the wrong game build …). Errors a mod throws later are counted
  against it. The same report is in `BepInEx\Keel-report.txt`, and a short notice appears after start-up when something
  went wrong.
- **One DLL for both game builds** (the flat map of 0.8.7 and the 3D globe build): Keel references no game assembly.
- **The hand-installed part never changes.** The stub in `BepInEx\plugins` only finds and starts Keel; every update comes
  through the Workshop.
- **Runs plain BepInEx plugins and Anchor Chain mods.** While Anchor Chain runs, Keel leaves its mods to it, so nothing
  starts twice. When Anchor Chain is not running (its preloader cannot start on the 3D globe build yet), Keel starts those
  mods itself.

Status: **0.1.0, not yet run in the game.** Checked offline: builds, 0 missing references on both game builds, and the
scanner, game-version check and planner run over the installed Workshop mods (see *Development*).

## Install

1. Subscribe to R.A.T.S. Keel on the Workshop and switch it on in the game's Mods menu.
2. Once: copy `SeaPowerKeel.Stub.dll` (from the GitHub release) into `Sea Power\BepInEx\plugins`.
3. Start the game. Ctrl+Shift+K shows the report.

Keel works next to Anchor Chain. You can keep both; Keel only takes Anchor Chain mods over when Anchor Chain itself is not
running.

## Settings

`BepInEx\config\dev.sewerlabs.seapowerkeel.cfg` (made on the first start):

| Setting | Default | What |
|---|---|---|
| `Loading.AnchorChainMods` | `true` | Start Anchor Chain mods when Anchor Chain is not running |
| `Loading.GameVersionCheck` | `Standard` | `Standard`: leave out DLLs that would stop missions from loading, warn about the rest; `Strict`: leave out any DLL with a missing part; `Off` |
| `Loading.DisabledPlugins` | empty | Plugin ids Keel does not start although their folder is on |
| `Report.Hotkey` | `Ctrl+Shift+K` | Report window |
| `Report.NoticeOnProblems` | `true` | Notice after start-up when a mod did not start or threw |

## How it works

1. The **stub** (`BepInEx\plugins\SeaPowerKeel.Stub.dll`) reads the game's mod list from `usersettings.ini`, finds
   `SeaPowerKeel.dll` in an enabled mod folder (newest version wins) and starts it.
2. Keel waits for the game's own mod list (on the globe build also for the end of the start-up load screen, as mods
   started during it can keep the unit data from loading).
3. It reads the plugin attributes of every DLL in the enabled folders **without loading them** (Mono.Cecil, which BepInEx
   ships), and checks each DLL against the game's `Managed` assemblies.
4. Duplicates (newest copy wins), plugins already running from `BepInEx\plugins`, switched-off ids, dependencies with
   version ranges and conflicts are worked out until nothing changes; the rest starts in dependency order, otherwise in the
   game's mod-list order.

Writing a mod for it: [docs/for-mod-authors.md](docs/for-mod-authors.md) (BepInEx plugins, Anchor Chain mods, `keel.ini`
for mods with one DLL per game build).

## Development

```
dotnet build src/SeaPowerKeel/SeaPowerKeel.csproj -c Release          # -p:Deploy=true copies it into StreamingAssets\SeaPowerKeel
dotnet build src/SeaPowerKeel.Stub/SeaPowerKeel.Stub.csproj -c Release
```

`-p:GameDir=...` for another install. `tools/offline-check` runs Keel's scanner, game-version check and planner over mod
folders on disk against a game build's `Managed` folder, without the game:

```
dotnet run -c Release --project tools/offline-check -- "<Managed>" "<Sea Power>\BepInEx\core" "<steamapps>\workshop\content\1286220"
```

## Credits

Written from scratch. Anchor Chain (SeaPower-Modders, MIT) is the community chainloader Sea Power code mods are built on;
Keel reads Anchor Chain mods through their public attributes and interface so players can use both.

MIT license.

*R.A.T.S. – Ratte's Assorted Tactical Systems*
