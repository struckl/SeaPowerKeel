# Test 2026-11-12: takeover and What's new (game 0.9.1, build 26612099)

Run in the game by the Keel session on his go. Screenshots stay on the developer PC (not in git: unscrubbed game shots).

| Run | Result |
|---|---|
| "Hang" at start (09:14 and later) | **Not a hang:** the game's own version question (BetterTacMap, Sinking Variety, VopSound not declared for 0.9.x) waited for a click behind his Remote Desktop window. Keel's loading window says "Waiting for you". |
| Patcher 1.0.0 (Harmony hook on `TypeLoader.FindPluginTypes<PluginInfo>`) | **RED:** with it in `BepInEx\patchers`, BepInEx never started (no LogOutput, no mods, no preloader error file). Cause: touching BepInEx's `PluginInfo` (it refers to `BaseUnityPlugin`, so to Unity) in a patcher loads UnityEngine before BepInEx has hooked it. |
| Patcher 1.1.0 (writes `BepInEx\cache\chainloader_typeloader.dat` itself; no Harmony, no BepInEx plugin types) | **GREEN:** BepInEx and Keel start; 17 mods running. |
| What's new card | **Shows**: BetterTacMap and R.A.T.S. Keel UPDATED, a click closes it. Flaws: no author lines under either; Keel listed without a version jump (same 0.4.0); BetterTacMap listed as UPDATED again at the next start with nothing changed. Handed to the What's new session. |
| Run 2: Alpine's preloader back in `BepInEx\plugins` | **GREEN:** "Not starting Alpine's Mod Loader", BepInEx loaded only Keel's stub, Keel ran all 17 mods, summary card "No problems". `Keel-takeover.txt` names it. |
| Run 4: Keel switched off in Mods | **GREEN:** the patcher took nothing out ("Keel is switched off"), Alpine 0.2.1 started as usual. |
| Run 3 (first start without the patcher), run 5 (patcher update while in use), Euromod stages | Not run yet. |

Put back: Alpine's file as `.keel-off`, his usersettings.ini, Keel on; game left at the main menu with 17 mods.
