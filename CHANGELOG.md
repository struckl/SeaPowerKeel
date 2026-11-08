# Changelog

## Unreleased - Anchor Chain compatibility (offline-checked only, not run in the game)

Reports on Discord said big Anchor Chain mods (Euromod's Anchorchain Expansion Pack, sonar systems) did not work under Keel.
Compared with Anchor Chain's loader (released 1.1.0 DLL and the current source), these differences are fixed:
- Only the exact `AnchorChain.dll` and `AnchorChain.Preloader.dll` are left out of the scan, not every DLL whose name starts with
  "AnchorChain"; every DLL left out is listed with the reason in the report (window, file, log).
- The entry point is called through `IAnchorChainMod`, so explicit implementations work; a mod whose entry point cannot be found is
  FAILED, not RUNNING.
- Only public (exported) Anchor Chain plugin classes count, as in Anchor Chain; others are listed as SKIPPED with the reason.
- Every DLL is loaded and every Anchor Chain mod created before the first one starts, then they start in dependency order (still one
  per frame); a mod that cannot be loaded keeps the mods that need it out.
- `[ACConfig]` is implemented (it was refused while Anchor Chain was not running): default and player config files, merge of new keys,
  `[AnchorChain.ResetValues]`, `Required`. The game's `IniHandler` is bound by reflection (checked on 0.8.7, 26223125, 261107), with a
  logged fallback when a member is missing.
- Anchor Chain is looked for again right before Keel starts mods (and before each Anchor Chain mod), so the two cannot both start them;
  Keel's own load of Anchor Chain's DLL as a library does not count as "running".
- `[ACIncompatibility]`: Anchor Chain drops every mod that declares one even when the named mod is absent (upstream bug). Keel keeps
  its correct behaviour and now says so in the report.
- Mods that call `AnchorChainLoader.FindFile` (Euromod's Sonar Audio Loader, for audio in other mods' folders) could not load that class
  without Anchor Chain's `AnchorChain.Preloader` assembly (removed when switching to Keel) and then found nothing: Keel provides the one
  interface needed and fills the folder list.
- `tools/offline-check --selftest` (71 checks) and `--inicheck`.

## 0.1.0 – 2026-11-07 (not released; run in the game on 0.8.7)

Fixed after the run on the 3D globe beta 261107 (2026-11-08):
- Ctrl+Shift+K did not reach the report window on the globe build: the key is now also read from the keyboard (Input System,
  else the old Input class), counted once per press.
- A mod logging the same line every frame (IdentifyExpanded on that beta) grew Keel-log.txt by ~1 MB a minute: repeats are
  counted ("the line above repeated N more times"), about 20 times less.
- The report window has a solid background (the menu showed through) – not yet seen in the game.

Fixed after the first run in the game (2026-11-07):
- The scanner could not read `[BepInDependency]` (Cecil needs BepInEx.dll for the enum argument) and dropped the whole DLL;
  it now resolves from Managed, BepInEx\core and the mod folders, and one unreadable type no longer hides the others.
- Errors were pinned on the first plugin of a DLL (shared namespace); now the plugin's own class wins, topmost frame first.
- The stub's version and Keel's path were missing from the first log line.
- The notice ran out during the start-up load screen; it now appears when the main menu is up.
- The report window was tiny on 4K: scaled by screen height (`Report.Scale`).
- Phase timings in the log; the summary counts failed and not started apart.


- Stub 1.0.0 for `BepInEx\plugins`: finds `SeaPowerKeel.dll` in the enabled mod folders (newest wins) and starts it.
- Loader: the game's mod list (or `usersettings.ini`), plugin discovery without loading (Mono.Cecil), game-version check
  against the running build (class-load problems keep a DLL out, other gaps are warnings; `Strict`/`Off`), duplicates,
  dependencies with version ranges, conflicts, process filter, start order; each plugin started on its own.
- BepInEx plugins from mod folders (registered in `Chainloader.PluginInfos`) and Anchor Chain mods (only while Anchor
  Chain is not running; a DLL with both entries runs its Anchor Chain entry only).
- `keel.ini` for mods with one DLL per game build.
- Report: Ctrl+Shift+K window, `BepInEx\Keel-report.txt`, notice after start-up, later errors counted per mod.
- `BepInEx\Keel-log.txt`: every log line with time of day and seconds since the game started, the mod an error belongs to,
  the last sessions kept (`Logging.TimestampedLog`, `Logging.KeepSessions`). Exceptions Unity logs get a line naming the
  mod they came from, with the stack trace, in LogOutput.log too.
- `tools/offline-check`: the same scan, check and plan outside the game.
