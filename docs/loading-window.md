# The loading window (0.1.5)

With a few dozen mods the game sits on a blank or frozen screen for a minute or two while it starts. Keel opens a small window of its own
that shows what is going on, and closes it when every mod has started. Written 2026-11-08; **drawn offline from a real `Keel-log.txt` and the
window thread smoke-tested, not yet run in the game.**

## Where the start-up time goes

From the start at 21:14:03 (3D-globe build 261107, 16 mod folders on, Keel 0.1.4, Anchor Chain's preloader still in `BepInEx\plugins`).
Times are seconds since the game process started.

| From | To | What | Notes |
| --- | --- | --- | --- |
| 0 | 3.7 | Unity and BepInEx start | nothing of ours can run yet |
| 3.7 | 16.4 | **Anchor Chain's preloader** | 12.7 s, ending in "Could not find AnchorChain chainloader" (no Anchor Chain item installed). Its `FindLoader` runs `Directory.GetFiles(folder, "AnchorChain.dll", SearchOption.AllDirectories)` over every StreamingAssets mod folder and every Workshop item folder: a recursive disk scan. A likely cause, not proven; deleting the preloader (README, "Switching from Anchor Chain") should show it |
| 16.4 | 22.4 | Sentry, Vuplex web view | the engine coming up |
| 22.4 | 38.8 | the game's own start-up load screen | Keel waits for it on purpose (mods started during it can break the unit data on the globe build) |
| 38.8 | 88.6 | Keel starts 19 plugins "one per frame" | 49.8 s: **VopSound 15.4 s** in one call, then **Dynamic Campaign Mod ~23 s** of its own Harmony patching after Keel logged it as running; the rest 0.03 to 1.8 s |

So the "freeze" is three things: a disk scan before the game has a window, the game's own load screen, and two mods whose start-up code blocks
the main thread. Only the last cannot be covered by anything drawn inside the game: **while a mod's code runs on the main thread nothing in
Unity redraws**, IMGUI included (Keel's own report window and notice are IMGUI for that reason: they come after).

## How it works

- **A window of its own.** `Loading/Overlay.cs`: a tool-window, never-activated Win32 popup (user32/gdi32 through P/Invoke) on a thread
  of its own, drawn 30 times a second into a memory bitmap. It keeps counting while the main thread is stuck in a mod. It never takes the
  keyboard focus. Windows only.
- **Above the game, not above everything.** It is not topmost. As soon as the game's window exists (class `UnityWndClass` of this process,
  looked for every 0.2 s) that window becomes its *owner*: an owned window stays above its owner, goes when the game is minimized and sits
  behind a program you put in front. The game's window rectangle also sizes and places it (scale by its height: 1.5 on 4K, so the 460 x 276
  window fits the marker's band, never below the system's DPI scale).
- **On the marker.** Default `Spot = Marker`: centred at 58.8 % of the game's width and 77 % of its height, the blank band under the main
  menu's news box (the spot the author marked in a screenshot); or a corner.
- **A summary instead of vanishing.** After Keel's "Done:" plus `SecondsAfterLastMod` the full window shrinks and glides to the bottom left, above
  the game's version text (0.9 s, ease out; the full window is squeezed along with StretchBlt, then the card's own content takes over). The card
  says "Keel 0.1.5 started 19 mods in 40 s", "Game ready after 1:00" (+ skipped), and either the number of mods with problems or the slowest mod;
  a click on it opens Keel's report (Keel's host reads a flag on the main thread and opens the IMGUI window), its × closes it (the game-drawn card); it stays until then
  (no fading on its own since 0.5.4).
- **Opens early.** `KeelHost.Awake` starts it, which runs when the stub does. BepInEx starts plugins in GUID order
  (`dev.sewerlabs.seapowerkeel.stub` < `io.github.seapower_modders.anchorchain_preloader`, as the log shows), so it is there before the
  12.7 s preloader.
- **Reads log lines only.** `Loading/LoadingWindow.cs` adds a BepInEx log listener; `Loading/Progress.cs` turns the lines into a phase, a list
  of mods and a count. Nothing in the game or in any other mod is touched, so a mod that logs oddly cannot break it (a bad line is ignored).
- **Names the slow mod while it is slow.** Keel now logs `STARTING i/n: name version (folder)` before each start (`Sequencer.Starting`);
  `i/n` counts the mods still waiting when the start phase began. The window shows "Starting 17/19: VopSound 1.96.29  8 s" and the bar
  fills exactly. The line is in `Keel-log.txt` too, so a log shows which mod was running when the game stood still. (A mod that goes on
  working after its start returns, like Dynamic Campaign Mod's patching, delays the *next* step: the window then names the next mod.)
- **Ends by itself.** When the game draws the card (hand-over), or on its × there; the full window also ends after the longest time (300 s from the process start).

Lines it understands: Keel's `RUNNING / RUNNING WITH ERRORS / NOT STARTED / SKIPPED: name version (folder)`, `STARTING i/n: name`,
`Found N plugin(s)`, `N of M mod folders are on`, `The game's mod list is there`, `Start-up load screen done`, `Done:`; Anchor Chain's preloader's
"started" line; the game's `FileManager.OnStart`, `FileManager.CheckFileValidity`, `(C) Triassic Games` and `3D WebView` lines for the early steps.

## Startup preparation for mods

A mod can include one or more DLLs ending in `.Preloader.dll` anywhere in
its mod folder. Keel finds them in enabled folders that fit the game version.
Each DLL needs one public type with a public static
`void Prepare(string streamingAssets, string[] enabledMods, Action<string> report)`
method and must be independent of Unity, Sea Power and the mod loaders. Keel
calls each DLL on a worker when it sets up the loading window. A failed callback
is logged and Keel continues with the next DLL. Use this for read-only file work
that can run during the splash. The paths come from the saved mod list. Check
them against the live game before using the results. Keep Unity and game API
calls in the normal plugin entry point. The callback can report short status
text for the loading window.

`Keel.GetStartupTimings()` gives the timestamps of window setup, the first
coroutine frame and early plugin dispatch in seconds since launch. A mod drawing replacement progress
inside the game can call `Keel.HandOffStartupWindow()` once its display is
visible. Keel still draws its own summary on the menu.

## Testing without the game

`tools/loading-preview` (net8, no game): `dotnet run --project tools/loading-preview -c Release -- <Keel-log.txt> <out folder> [scale]`
replays a log through the window's model (adding the `STARTING` lines older logs lack) and draws the window into BMP files at chosen seconds;
`... -- live` opens the real window thread far off every monitor, feeds a few lines and checks it closes itself after "Done".

## Not done

- Run in the game. Only the game can show: the window over the game's borderless window at 4K, that it really opens before the preloader,
  and the early phase (0 to 16 s).
- The preloader's 12.7 s are outside Keel (`tools/anchorchain-fix`); worth a look on its own.
- Exclusive full screen hides the window; the game's "fullscreen window" mode shows it.
- A mod that works on after its start returns (Dynamic Campaign Mod) is not named for that work.
