# Changelog

## 0.1.3 – 2026-11-08

- The by-name check now reaches **internal classes**. A mod cannot write `typeof(X)` for a class the game keeps internal, so it looks the class up by name; those lookups had no reference Keel could see. Keel now reads the literal name in `AccessTools.TypeByName("Ns.X")`, `Type.GetType("Ns.X, Assembly")` and `Assembly.GetType("Ns.X")`, checks that the type exists in the game's assemblies, and follows the Type into later `AccessTools.Method/Field/PropertyGetter/...(thatType, "Name")` calls (through a local variable, a static field, or directly nested). Also `AccessTools.Method("Ns.X:Name")` and `[HarmonyPatch("Ns.X", "Name")]`. Only names in the game's own namespaces are judged; `System.*` and other mods' names are left alone.
- The index of the game's type names (about 28,000 types in 183 assemblies) is built only when a mod does such a lookup: 60-70 ms on the dev PC, logged as "Index of the game's type names ... ms".
- The warning is now worded "Looks up N type(s)/method(s) by name that this game build lacks" (it also covers plain reflection lookups, not only Harmony patches); `Strict` still keeps such a mod out.
- Still not checked: a Type held only on the evaluation stack (the compiler does that for back-to-back uses without a null check), names built at run time, `TargetMethod()`, and whether a transpiler matches its target's IL. Run offline against a test mod (11 planted misses found, valid and non-game names not flagged) and the installed Workshop mods; not yet run in the game.

## 0.1.2 – 2026-11-08

- The game-version check now also looks at **Harmony patch targets**. A patch names its target as a string, so a method that this game build renamed or removed passed the old check and only failed when the patch was applied. Keel now reads `[HarmonyPatch(typeof(X), "Name", ...)]` (class and method level merged, getters/setters/constructors, argument types) and `AccessTools.Method/DeclaredMethod/Field/DeclaredField/PropertyGetter/PropertySetter(typeof(X), "Name")` with literal names, and looks each target up in the game's assemblies. A missing target is a warning in the report ("Patches N method(s) this game build lacks"), and keeps the DLL out under `GameVersionCheck = Strict`. Not checked: targets built at run time, `TargetMethod()`, `"Type:Method"` strings, and whether a transpiler's code still matches the target's IL. Checked offline with a test mod (5 planted missing targets found, existing ones not flagged) and over the installed Workshop mods; not yet run in the game.

## 0.1.1 – 2026-11-08

- Anchor Chain mods no longer need the Anchor Chain Workshop item: Keel ships Anchor Chain's library (MIT, `lib\AnchorChain.keel.bin`) and uses it when no Anchor Chain folder is on. Mods that use Anchor Chain's ACConfig still need Anchor Chain. Run in the game on globe beta 261107 on 2026-11-08 with no AnchorChain.dll on the machine: 7 plugins started (Sensor Views, Radio, Topo, Shot Director). On 0.8.7 that path was not run: there Anchor Chain's own item is the tested way.

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
