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

## 0.5.0 test run (split, game-drawn cards), 2026-11-12 15:35-15:55, game 0.9.1

Run from TESTING.md, plan `docs/test-plan-0.5.0.md`. Two runs and one fix run between them.

| # | Check | Result |
|---|---|---|
| 1 | Version question | GREEN: top bar *MESSAGE · R.A.T.S. KEEL*, list without the Keel sentence (driver picture) |
| 2 | Keel card | GREEN: "Keel 0.5.0 started 41 mods in 19 s" |
| 3 | Hand-over and split | GREEN: hand-over without a blink, split seen live by him ("very cool animation"); frames of the later phases in the burst |
| 3a | Inside the grey panel, under EXIT | run 1 RED (cards wider than the panel, notes wrapping) → fixed → run 2 GREEN: panel width, one line each, above the version text |
| 3b | Behaviour | GREEN: × closes What's new and the Keel card glides down; right click leaves it (always visible); Scenarios hides both |
| 4/5 | Card click, report rows | GREEN: report opens at WHAT'S NEW; Radio row opens all notes, "click to close" |
| 6 | Report line 1 | GREEN: `Sea Power 0.9.1-261112, 3D globe map` |
| 7 | EG button | GREEN: "EG on after restart"; 11: after *Restart the game* the setup line says Extended Guidance ON |
| 8 | ALTERNATIVE MODS | GREEN |
| 9 | Mods menu reload | GREEN at the end (report rewritten, "41 running, 1 failed" = Automatic SAR, outdated); the mid-reload state not caught (one mod) |
| 10 | Send report | not run: the feedback board change is not deployed |

Findings fixed after the runs (built, self-tests 205/205, **not yet run in the game**): the Keel card comes back after a Mods menu
reload (it was gone); the Anchor Chain note once in the report; the reload dialog says R.A.T.S. Keel in its top bar, not in the text;
every game message box is logged (`Game message box:`); What's new's footer without "× closes". Fixed in the first fix run and seen
in run 2: panel width and one-line text; the Keel card no longer fades. Driver and TESTING.md fixes: see the workspace repo (db54ab9).

## Evening 2026-11-12: player reports (test plan `test-plan-0.5.2.md`)

| # | Check | Result |
|---|---|---|
| 1 | Earlier start | **GREEN:** Pop-Out (marked StartEarly for the test) started 38 s before the load screen ended; menu fine |
| 2 | Backup entry | **GREEN:** 0 `[SKIPPED]` rows (were 5: Alternative mods, Sinking Variety) |
| 3/4 | Right click on What's new / Keel card | **RED, fixed, GREEN:** the game never handed the right click to the cards; Keel now polls the right button itself. Retest: What's new hides, the Keel card glides down, then hides |
| 5 | Fade | **GREEN:** both cards gone 68 s after they showed |
| 6 | Report window (Hussar) | **GREEN:** opens from the card, Close closes it, nothing stays |

## Board post #3: Alternative Game Interface (test plan `test-plan-0.5.3.md`, followed TESTING.md)

| # | Check | Result |
|---|---|---|
| 1 | Report from the card, Close, then SCENARIOS | **GREEN:** Scenarios opened, Back worked (AGI 2.0.0 running, 18 mods) |
| 2 | Report closed by Esc / by Ctrl+Shift+K, then the menu | **GREEN:** Mod Manager and Scenarios opened after each |
| 3 | Mod Manager with AGI after the report | **GREEN** |

- Not reproduced on the old build first (the cause is read from AGI's code: it holds back mouse releases while `GUIUtility.hotControl` is set).
- Put back: AGI unticked through the Mod Manager + Keel's Restart; game left open.
