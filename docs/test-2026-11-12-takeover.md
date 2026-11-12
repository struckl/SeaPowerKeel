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
| What's new after the fixes (994c40c) | **GREEN:** Keel UPDATED with 11 CHANGELOG lines; BetterTacMap no longer flagged; the four Euromod items NEW with their authors' Workshop notes. Small: Workshop notes show raw markup (`### Fixes ###`). |
| Patcher update while in use (run 5) | **GREEN** in passing: "Takeover patcher updated" at a start where the old one was loaded. |
| Euromod run A: Main + US Naval Aviation + Cost Tracker + AC Pack, EG off | **GREEN:** 41 mods running, "No problems", `Euromod setup: Euromod + AC Pack (updated 2026-11-11), Extended Guidance off.` Only warning: Alternative Weapons Control "Gun fixes failed: Value cannot be null" (also without Euromod). New subscriptions have to be ticked in the Mod Manager; an edit of usersettings.ini is not enough. |
| Euromod run B: Extended Guidance on (`Mods\ACPack\submods.ini`, `ExtendedGuidance=true`, written by hand in the AC Pack's format) | **GREEN at start-up:** `+ Extended Guidance ON (experimental)`, ExtendedGuidance 0.10.33 RUNNING, 41 running. Not checked: that the AC Pack's own menu writes that file (only its strings say so). |
| Mission with Euromod + EG, 14:43 | **GREEN:** EG switched on with Keel's new button, restart, `euromod.extended-guidance` patched the game, Red Sea Breakout ran (missiles fired), 0 errors. Found on the way: the AC Pack's key is `euromod.extended-guidance`, not `ExtendedGuidance` (fixed). |
| Run 3 (first start without the patcher), 14:47 | **GREEN:** patcher removed, Alpine's file back in `BepInEx\plugins`, plugin cache cleared, Alpine ticked: Alpine ran 42 mods, Keel only the one Alpine left (BetterTacMap, nothing twice), installed the patcher and said *restart once*. After Keel's *Restart the game*: Alpine kept from starting, Keel ran all 43. |
| What's new card with × (85ff4e1), 13:55 | **GREEN:** card with × and "click for every mod and every line · × closes". A click on the card opens the report window at WHAT'S NEW (2); closing the window leaves the card up; × closes the card without opening anything. Card forced by setting Radio and Topo one version back in `Keel-seen.txt` (the six hotfixes had been seen by an earlier start). Gap: our own mods show "No change notes from the author": none of them ships `CHANGELOG.md` in its mod folder (only Keel does). |

Left as: Euromod Main, US Naval Aviation, Cost Tracker and AC Pack ticked in his Mod Manager, `submods.ini` with EG on, game at the Scenarios screen. Alpine's file as `.keel-off`, Keel on.

## Afternoon 2026-11-12 (game 0.9.1)

| What | Result |
|---|---|
| AC Pack settings gear writes `submods.ini` | **Yes:** a click on *Enable Extended Guidance* wrote `euromod.extended-guidance=true` at once. |
| Mod Manager+ (3716097076) and Boost Loader (3606934378), both Anchor Chain mods by sebase11 | **Start under Keel**, 43 mods "No problems". Boost Loader (last update 2025-11) calls `FileManager.GetAllFiles(String, String)`, gone in 0.9.x: Keel warns, its part that uses it will fail. Mod Manager+ patches the game's compatibility check and Mod Manager; Keel's Restart button still showed (Mod Manager+'s own menu not looked at). Both subscribed for the test, unticked afterwards. |
| ALTERNATIVE MODS menu entry missing on some starts | **Found and fixed:** the Alternative mods add it when the load screen's coroutine ends; Keel started them after that end. Keel now calls such a postfix once (`LoadScreenEnd`); in the game the entry is there and the log names the three mods. |
| What's new | Author notes clean (*Fixes: fixed RAM launch angles…*); card below the AC Pack's gear; CHANGELOG lines of our own mods shown. |
| Version question top bar | *MESSAGE · R.A.T.S. KEEL*, list without the Keel sentence. |
| AC Pack OptimalTargetDist | Its two transpilers do not fit 0.9.1 ("Unrecognized launch layout", "Unknown AAW rejection layout"); under Alpine it is a dialog, under Keel two log lines. The AC Pack's own issue. |

Left as before the tests: Euromod items, Alpine unticked, Alpine's file `.keel-off`, no `submods.ini`; Mod Manager+ and Boost Loader subscribed but unticked.
