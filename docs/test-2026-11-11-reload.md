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

## Second run, game 0.9.0 (build 261111, Steam 26597865), same day

The game now asks at every start (and after a reload) about mods "not declared to be compatible"; clicked Ignore each time.

| Step | Result |
|---|---|
| Start | 17 running, hook ready after the main menu. |
| Topo off, Reload | Both Topo plugins turned off (3 + 0 patches). |
| Topo on again, Reload | Not started a second time: "Did not start: Sea Power Topographic Map ..." with the restart reason. |
| Mods, Accept, Restart | Old game gone after 1 s, new one after 7 s; after Ignore on the game's dialog, 17 running, Topo back. |

Not run: 0.8.7 flat map; a mission after a reload.
