# In-game test 2026-11-11: Mods menu Restart and Reload

Game build 26103404 (version text 0.8.7-261021), his mod list (17 code mods under Keel). Driven with `tools/game-driver`.

| Step | Result |
|---|---|
| First build: hook installed in `Awake` | **Crash** at start-up: patching `ModMenuViewModel`'s constructor ran its static constructor, which registers Noesis properties before the UI exists (access violation in `Noesis.DependencyObject.Extend`). Fixed: the hook is installed once the main menu exists (`HookModMenu`, `GameState.MenuUp`). |
| Fixed build: start | 17 mods running, "Mods menu: Restart button and Reload follow-up ready." |
| Mods, Accept | Dialog shows Cancel / Restart / Reload with Keel's line; the line is not doubled on a second open. |
| Automatic SAR on, Topo off, Reload | Keel: "1 switched on, 1 switched off"; Topo's two plugins turned off (0 + 3 patches removed); Automatic SAR started after the reload. No errors in the log. |
| Automatic SAR off, Topo on, Reload | Automatic SAR turned off (19 patches removed). Topo started again **but its flat-map plugin said "Already loaded, skipping"**: its static state from the first start survives. Fixed after the test: a mod switched off and on again in one game is not started a second time, the report says it starts after a restart (built, **not run in the game**). |
| Mods, Accept, Restart | Mod list saved, game quit after 3 s, Steam started it again 34 s later; the mod list matched the one before the test (the game itself dropped a missing Workshop item and listed Alpine as off, as its own Accept does). |

Not run: the "switched off and on again" fix (a Steam game update to build 26597865 began during the test); 0.8.7 flat map; a mission after a reload.
