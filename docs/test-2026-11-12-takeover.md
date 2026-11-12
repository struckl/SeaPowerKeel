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
| Mission with Euromod + EG | **Not run:** the driver's menu clicks miss once Euromod adds its EUROMOD category, and he took the PC back. |
| Run 3 (first start without the patcher) | Not run. |
| What's new card with × (85ff4e1), 13:55 | **GREEN:** card with × and "click for every mod and every line · × closes". A click on the card opens the report window at WHAT'S NEW (2); closing the window leaves the card up; × closes the card without opening anything. Card forced by setting Radio and Topo one version back in `Keel-seen.txt` (the six hotfixes had been seen by an earlier start). Gap: our own mods show "No change notes from the author": none of them ships `CHANGELOG.md` in its mod folder (only Keel does). |

Left as: Euromod Main, US Naval Aviation, Cost Tracker and AC Pack ticked in his Mod Manager, `submods.ini` with EG on, game at the Scenarios screen. Alpine's file as `.keel-off`, Keel on.
