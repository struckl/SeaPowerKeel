# Changelog

## 0.1.0 – 2026-11-05 (not released, not yet run in the game)

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
