# Changelog

## Unreleased

- **Keel card and What's new go away again**: a right click hides them, and they fade on their own after `SummarySeconds` (players asked)
- **No SKIPPED row for a mod's backup entry**: a DLL with an Anchor Chain and a BepInEx entry is by design; the backup is left out quietly, dependencies on it still count (rgreat)
- **Start early, earlier**: with a start-early mod on, Keel reads the mod list from `usersettings.ini` at once, so those mods start before the first load-screen picture (rgreat)
- **Right click reaches the cards**: Keel listens for the right button itself, as the game often keeps it from the cards (found in the test run)
- **Tested in the game on 0.9.1**: early start before the load screen ended, no SKIPPED rows (were 5), right click hides both cards, both fade after 60 s, the report opens and closes
- Not yet run in the game

## 0.5.1 - 2026-11-12 - loading window at the top right

- **Loading window starts top right** on a new install (was in the middle of the screen)
- **Your own setting stays** (`Loading window.Spot`)
- **Start early, opt-in for mod authors** (asked for by rgreat): `[Keel] StartEarly=true` in `_info.ini`, or `[assembly: AssemblyMetadata("Keel.StartEarly", "true")]`
- **Early mods start before the load screen ends**; all others still wait (unit data, Noesis)
- **An early mod that needs a waiting mod waits too**; the report says why
- How to: `docs/for-mod-authors.md`

## 0.5.0 - 2026-11-12 - takeover, What's new, Euromod stages, better reports

### Keel takes over
- **Anchor Chain's and Alpine's loaders no longer start** while Keel is on: every mod starts once, under Keel
- **Their files stay untouched**: switch Keel off (or `Loading.TakeOver = false`) and they start again
- **Done by a small patcher** in `BepInEx\patchers`; Keel installs and updates it itself, KeelSetup too
- **First start after the update**: the old loader runs once more, Keel asks for one restart
- **The report names the loader kept out** (also `BepInEx\Keel-takeover.txt`)

### What's new since your last game
- **A card on the main menu** lists mods that are new, updated, gone or now broken
- **Born out of the Keel card** with a cell-split animation, at the bottom left
- **Always inside the menu panel**, between EXIT and the version text; fewer mods when tight
- **The author's own words**: Workshop change notes, else the mod's `CHANGELOG.md`
- **Short on the card, full in the report**: one line per mod; a click on a mod shows all its notes
- **Notes as plain text**: Markdown and BBCode marks removed

### Main menu cards drawn by the game
- **Keel card drawn by the game**, not a Windows window; the loading window hands over to it
- **Always visible** on the menu's top page; hides on menu pages and in a mission

### Euromod
- **Report names your stage**: Main Pack, + AC Pack, + Extended Guidance
- **Extended Guidance button** in the report window (AC Pack's own switch, from the next start)
- **Outdated AC Pack flagged** on game 0.9.x

### Reports and dialogs
- **Send report says crash or not**, the game build and a one-line summary
- **Report stays current during a Mods menu reload**
- **Message boxes say R.A.T.S. Keel in their top bar** when Keel adds to them
- **ALTERNATIVE MODS menu entry back on every start** (mods that wait for the end of the load screen)

### Fixes
- **No false UPDATED** for mods that write settings into their own folder, or for a changed `CHANGELOG.md`
- **Keel's own version jump shown**
- **Anchor Chain note once** in the report

### Other
- **For mod authors**: list R.A.T.S. Keel as your required item (`docs/for-mod-authors.md`)
- **License**: code stays MIT; name, logo, pictures and texts are not part of it; no re-uploads

### Tested
- **Game 0.9.1**: 17 and 43 mods, Euromod + AC Pack + Extended Guidance in a mission, Alpine kept out, reload and restart
- **Offline**: 205 checks pass

## 0.4.0 - 2026-11-11 - game 0.9.0, Mods menu Restart and Reload, Send report

- **Declared for game 0.9.0.** The mod's `_info.ini` now says it fits 0.8.7 up to every 0.9.x, so the game no longer lists it as "not declared to be compatible", and Steam hands this and later updates to 0.9.0 players (the game skips Workshop updates whose declaration does not fit its version). Keel references no game assembly; its by-name hooks (the Mods menu dialog, the file trail, the mod list) are all in 0.9.0 (build 261111); run in the game on 0.9.0: start-up with 17 mods, Reload and Restart.
- **Code mods follow the Mods menu without a restart.** After the game's *Reload* (Mods, Apply), Keel starts the code mods in folders you switched on, with the same checks, dependencies and order as at start-up, and takes mods in folders you switched off out as far as the running game allows: their Harmony patches come off and a BepInEx plugin is stopped. The game cannot unload a mod's code, so what a mod set up itself stays until a restart; the report and the dialog say so.
- **Restart from the Mods menu.** The same dialog gets a *Restart* button: it saves the mod list as the game's own button does, closes the game and starts it again, the clean way when a mod only works when started with the game. The report window (Ctrl+Shift+K) has *Restart the game* too (two clicks: a mission in progress is lost).
- **Send report.** A button next to *Save logs* in the crash window and in the report window sends the logs to the R.A.T.S. developer, no account and no file to attach. KeelWatcher.exe packs the same zip as *Save logs* with the Windows user name replaced by `%USERNAME%` in every path (`C:\Users\<name>`, also with forward slashes and as JSON writes it), keeps only the last 6 M characters of a very long log, shows the file list with a box for a short note and asks *Send logs to the R.A.T.S. developer?*. Only on *Send* does it upload to the feedback board (hub.sewerlabs.dev, at most 5 MB), which answers with a report number. Offline: a real bundle from the developer PC went up and arrived on the board as report #1; the user name check passed on every path form. The window itself not clicked yet.
- **A clearer game-version dialog.** Game 0.9.0 asks at every start about mods whose `_info.ini` names another game version, as one long list with full paths. Keel shows short names instead, code mods apart from data mods, with the version each one says it was made for; the game's own question and buttons stay. Keel's loading window says it is waiting for your click instead of counting seconds.
- **Anchor Chain's library is now the official 1.1.0 file**, byte for byte. Some mods (the ROC set) check its SHA-256 before they run and refused Keel's own build of it. Run in the game on 0.9.0: all 17 mods of the test list started, Anchor Chain mods included.
- Send report was run in the game on 0.9.0: the report window's button, the send window, report #2 arrived on the board with no user name in any file.
- A mod switched off and then on again in the same game is not started a second time: its code from the first start is still loaded (Topo said "already loaded, skipping"), so the report says it starts again after a restart.
- Fix: the Mods menu hook is installed once the main menu is up. In `Awake` it ran the menu's static constructor before the game's UI existed and crashed the game at start-up (found in the test).
- Run in the game on build 26103404 on 2026-11-11 (docs/test-2026-11-11-reload.md): the dialog, Reload with a mod switched on and one off, and Restart. The off-and-on-again rule and Restart were run again on game 0.9.0 the same day.
- **README: Linux (Steam Proton).** Switch Anchor Chain off in the game's Mods menu *and* unsubscribe from it on Steam, as a player reported; switched off in the Mods menu alone, Anchor Chain still loads its mods and Keel steps aside. Not run on Linux by us.

## 0.3.1 - 2026-11-09

Run in the game on the globe beta (build 261107) on 2026-11-09 with Alpine's Mod Loader 0.2.1 installed: with Alpine active, Keel started none of the 17 mods and left them to it; after KeelSetup.exe switched over, Keel ran all 17. 0.8.7 not run.

- **Alpine's Mod Loader is recognized.** When it is active, Keel leaves its mods to it and says so, so nothing starts twice. The report calls such mods "left to another loader". KeelSetup.exe sets Alpine's preloader aside, so Keel runs the mods, and Restore switches back.

## 0.3.0 - 2026-11-09

- **KeelSetup.exe.** One window and one file: finds Sea Power, adds BepInEx if the game has none, installs the starter file and sets another loader's preloader aside. Restore undoes exactly what it changed. Run on a clean copy of the game (globe beta 261107) with no BepInEx: Install added it, Keel 0.2.0 started 8 mods, and Restore removed what setup had added.

## 0.2.0 - 2026-11-09

Run in the game on the globe beta (build 261107) on 2026-11-09: named crashes from a mod's code, from a mod starting and from a pack file; the crash window with its tick box and restart; a crash with no mod to name; the patch check; the broken-file list; Save logs; Let held mods run and Restart the game. All with test mods that crash on purpose on 18 mods. Not run: a real crash in Unity or a driver, a large mod list, 0.8.7.

- **Crash catcher and crash window.** Keel catches a native crash as it happens (a Windows unhandled-exception filter, chained to the game's own crash reporter) and writes what crashed and the managed stack to `BepInEx\Keel-crash-stack.txt`. `KeelWatcher.exe`, which Keel starts from its own folder, waits outside the game and shows a window the moment the game dies: which mod did it (or that Keel cannot tell) and a restart. A mod on the stack is held out at the next start. The window has a tick box, 'Keep <mod> switched off for now' (on by default); unticked, the mod runs at the next start. The report window's 'Let held mods run' button releases held mods. Run in the game on the globe beta (build 261107) on 2026-11-09 with a forced native crash: the catcher wrote the test mod's frames, the window named it right away, its restart started the game through Steam, and the mod was held out. Unticking the box left the mod running at the next start, and 'Let held mods run' released a held mod. Not run: a crash inside Unity or the driver with no mod code on the stack (the window's 'cannot tell' text), 0.8.7.
- **Crashes show on the status card.** After a crash the card at the bottom left of the main menu turns amber and says so in one line; a click opens the report with the details. The separate notice box only appears when the loading window is switched off. Run in the game on the globe beta (build 261107) on 2026-11-09 after a forced crash: the card showed the crash and a click opened the report.
- **Save logs.** A button in the crash window and in the report window puts Keel's, BepInEx's and the game's logs into one zip on the Desktop and shows it in Explorer. The logs contain file paths, so also the Windows user name. Run in the game on the globe beta (build 261107) on 2026-11-09: both buttons made the zip while the crash window was open and while the game was running.
- **The report window is redone:** centred, opaque, with a clear header, coloured status labels, mods grouped by what needs attention, and flat buttons. Run in the game on the globe beta (build 261107) on 2026-11-09: Save logs, Let held mods run and Close clicked.
- **Crashes from packs.** Keel remembers the last files the game opened from mod folders. When no mod's code was running but a mod file was opened within 10 seconds before the crash, the crash window names that pack, and its tick box switches the folder off in the game's mod list. The catcher now starts with Keel, so a crash in the game's start-up load is caught too.
- **'Restart the game' restarts the game** after 'Let held mods run'.
- **Checks before loading.** A Harmony patch whose parameters do not fit the game method (an int for a float, a struct for an object) would read or write the wrong memory, so Keel leaves that mod out and says why. Pictures and sounds in mod folders that are empty or not what they claim are listed in the report; that scan runs in the background and remembers clean folders.
- **A mod that takes the game down is named and kept out.** Keel journals each step to `BepInEx\Keel-session.txt`. When the last game did not quit normally, the next start says so in a notice and in the report and writes `BepInEx\Keel-crash.txt` with the crash part of Unity's `Player-prev.log`. If the game died while Keel was loading or starting a mod (the frame after the start included), or a mod's code is on the crash's stack, that mod is named and held out until its DLL changes (`BepInEx\Keel-held.txt`, setting `HoldOutCrashers`). A later crash with no mod code on its stack, or from a pack without code, is reported without a name.

## 0.1.6 - 2026-11-09

Run in the game on the globe beta (build 261107) on 2026-11-09 with a test mod that patches `GameMain.FixedUpdate` with code reading a field the game no longer has (the error a player reported from Dynamic Campaign / Real Air Combat Tactics): Keel named the test mod and took its patch off (the mod's patch count on that method went from 1 to 0). Not run with those two mods themselves; 0.8.7 not run.

- **A mod's broken patch is taken off instead of throwing on every frame.** When the game throws 'Method not found' / 'Field not found' (for example a mod that patches `GameMain.FixedUpdate` and reads a field the new game build turned into a property: a popup on every frame), Keel finds the mod whose patch sits on the failing game method (from the stack's `DMD<Type::Method>` and the patches Harmony holds for it), takes that mod's patch off that method, names the mod in the report and the log, and the game goes on. The rest of the mod keeps running. Before, Keel only named the first mod that uses the member, which could be a mod that guards the call and is innocent. When the stack does not show which patch it was, Keel names every mod that uses the member and changes nothing. Setting `Loading / RemoveBrokenPatches` (on by default); off = only names the mod. Offline checks cover the error and stack parsing (95 of 95 pass). **Fixed on the way:** Unity logs the error on ONE line (`… _objectToDestroy Due to: Could not find field in class`), and the parser took the last word, 'class', for the member, so 0.1.5's naming of such errors did not work either; it cuts the ' Due to:' part off now.

## 0.1.5 - 2026-11-09

Run in the game on the globe beta (build 261107) on 2026-11-09: the loading summary card on the main menu and the 18 mods starting one by one under Keel, then a mission. Not run: the report window's Close button and Esc, the 'Method not found' naming, 0.8.7.

- **Loading window.** While the game starts, a small window of its own (Windows, own thread, so it keeps going while a mod blocks the game's main thread) shows the start-up step, the mods as Keel starts them with the time each took, a bar, and the name of the mod being started and for how long. It opens when Keel does, before Anchor Chain's preloader and the mods, on the main menu's blank band (setting `Spot`), only reads log lines and never takes the keyboard focus. It belongs to the game's window, so it stays above the game but not above other programs. When every mod has started it shrinks and glides to the bottom left above the game's version text as a summary card (mods started and how long Keel took, time until the game was ready, skipped mods, problems or the slowest mod); a click on the card opens Keel's report, a right click hides it, it fades after `SummarySeconds` (60). Settings under `Loading window` (show, spot, scale, opacity, seconds after the last mod, summary seconds, longest time, click hides). Drawn offline from a real log and its thread smoke-tested; not yet run in the game. [docs/loading-window.md](docs/loading-window.md) has the timeline that led to it: a 12.7 s disk scan in Anchor Chain's preloader, the game's own load screen, and two mods that block the main thread for 15 s and 23 s.
- **The summary card is for the main menu only:** it fades out as soon as a mission starts or the game loads a scene of its own (a mission, the editor, a campaign), instead of staying on the map for up to a minute.
- **Main menu top page only:** the summary disappears at once when you open a page of the menu (Scenarios, Options, Campaign ...) and is back at once when you return to the top page (its time starts again); a mission or another scene still ends it. Keel reads the menu's current page from the game (`MenuSystemViewModel.CurrentWindow`).
- **Loading window:** a dash between the mod's name and the seconds it has been starting ('Starting 5/19: Mod  -  8 s').
- **Report window, mouse and Esc read from the device:** the globe build does not reliably hand clicks to the on-screen report, so Close (and Copy report) did nothing. Keel now reads the left button, the pointer and Esc itself every frame (Input System, else the old Input) and hit-tests the two buttons. The loading window's title is 'R.A.T.S. Keel Loading'.
- **Report window:** the Close button works. The long 'Also in <path>' text next to it took the whole row and pushed Close out of the window; the text has a fixed width now.
- **A mod that breaks a game screen is named.** When the game throws 'Method not found' / 'Field not found' and the stack holds no mod (a Harmony patch had rewritten a game method, which is how the Dynamic Campaign Mod broke the Campaign menu on the globe build: the beta's `FileManager.GetDirectories` gained a parameter), Keel looks the member up in the lists its preflight check made and names the mod that calls it: a warning in the report, a log line, and the mod counts as running with errors. The report file now lists everything a mod uses that this build lacks, not only two examples.
- **Loading summary:** a little lower (it covered the game's version text before, now sits above it with a gap), smoother glide (a 16 ms timer instead of 33 ms, 0.8 s), a quiet 'Made by Kanalratte' line under the text that opens the Steam profile on a click. **Report:** Esc closes it, and a second click on the summary closes it too.
- **Loading window hint reworded:** 'Waiting 8 s after <last message> - the next step is slow' read as if the mod named were the slow one. It says 'Still loading - 8 s since the last message: ...' now: it states what is known (time since the last message, and which one) and does not guess why.
- **Loading summary:** sits higher (it covered the game's version text), no 'Slowest: ...' line any more; the name Kanalratte stays out of the window and the Workshop text; it is only a quiet last line of the report file.
- **`STARTING i/n: name` in the log.** Keel logs it before each mod starts, so `Keel-log.txt` names the mod that was running when the game stood still.
- Version 0.1.5; one DLL for both game builds as before (`modtool compat` clean).
- **Docs:** README and Workshop description now say that the game must be restarted after Keel or any mod is switched on or off, and what the "switched off in the game's mod list" log line means.

## 0.1.4 – 2026-11-08

- **Euromod and other big Anchor Chain mods.** Run in the game on globe beta 261107 with Euromod's Expansion Pack: 25 mods started and a mission ran. The Sonar Audio Loader can search other mods' folders again (`AnchorChainLoader.FindFile`). 0.8.7 was checked offline only.
- **Keel reads `_info.ini` too.** The `[Compatibility]` section (Exact, Approximate, GreaterThan, LessThan, Exclude), judged by the game's own rules, is a backup for what the DLL check cannot see, such as a transpiler that no longer matches. A mod that says it does not fit gets a warning in the report; `GameVersionCheck = Strict` leaves it out. An unreadable statement is ignored. Checked offline, not yet run in the game.
- **`[ACConfig]` mods no longer need Anchor Chain** (checked offline; no mod in the game run used it).

### Anchor Chain compatibility in detail

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
