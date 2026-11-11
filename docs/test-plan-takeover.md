# Test plan: takeover and What's new (in the game)

For the build after 0.4.0 (takeover, self-installing patcher) together with the What's new card. Only on his go, game on
0.9.0, `.game-lock` taken (see `..\docs\game-testing.md` in the workspace). Screenshots go to `docs/test-shots-takeover/`
(4K grab, top 46 px cropped, Discord overlay scrubbed before anything leaves the PC).

## Before

- Note his current state: `BepInEx\plugins` (today: `SeaPowerKeel.Stub.dll`, `AnchorChain.Preloader.dll.keel-off`),
  `BepInEx\patchers` (empty), `usersettings.ini` copy.
- `python tools/modtool.py package keel --yes --install` (game closed) installs the new build into
  `StreamingAssets\SeaPowerKeel`.
- Euromod Main, AC Pack, Combat Cost Tracker, US Naval Aviation subscribed (2026-11-12) and downloaded; switch them on in
  Mods for runs 2-4. Anchor Chain stays unsubscribed.

## Run 1: What's new, takeover with no other loader

1. Start the game.
2. **Expect:** `BepInEx\patchers\SeaPowerKeel.Patcher.dll` appears (log: "Takeover patcher installed").
3. **Expect the What's new card** at the top right of the main menu: Keel updated (`0.4.0 → …`) with its CHANGELOG lines,
   the four Euromod items as new. **Screenshot.**
4. Click the card: it closes. Ctrl+Shift+K: the full list. **Screenshot.**

## Run 2: the other loader comes back (Alpine's preloader is set aside today)

1. Close the game. Rename `AnchorChain.Preloader.dll.keel-off` back to `AnchorChain.Preloader.dll` (Alpine's file, 0.2.1).
2. Start. **Expect:** the patcher (installed in run 1) keeps it from starting: LogOutput "Not starting Alpine's Mod
   Loader …", `Keel-takeover.txt` lists it, Keel's report says "Takeover: Alpine's Mod Loader is installed but was kept from
   starting", every mod RUNNING under Keel (no OTHER LOADER), Euromod's AC Pack included. **Screenshot of the report.**
3. Run a mission (Intro 01) briefly: no double start (no mod's log line twice).

## Run 2b: Euromod stages (his word 2026-11-12: test them too)

1. Euromod Main + AC Pack on, AC Pack settings: Extended Guidance off. Restart. **Expect** the report's top line
   `Euromod setup: Euromod + AC Pack (updated …), Extended Guidance off.` **Screenshot.**
2. AC Pack settings menu: Extended Guidance on, restart. **Expect** `+ Extended Guidance ON (experimental)` and EG's DLL RUNNING.
3. AC Pack off in Mods, restart: `Euromod setup: Euromod (Main Pack).`
4. Check `Mods\ACPack\submods.ini` really is where the menu saves (LocalLow\Triassic Games\Sea Power).

## Run 3: first start after the update (patcher not there yet)

1. Close. Delete `BepInEx\patchers\SeaPowerKeel.Patcher.dll`, keep Alpine's preloader active.
2. Start. **Expect:** Alpine runs this once, Keel leaves its mods to it (OTHER LOADER), the summary card and notice say
   "restart once", the patcher is back in `BepInEx\patchers`. **Screenshot.**
3. Restart with the report window's *Restart the game*. **Expect:** as run 2.

## Run 4: Keel switched off

1. Switch R.A.T.S. Keel off in Mods, restart.
2. **Expect:** the patcher takes nothing out (`Keel-takeover.txt`: `off`), Alpine starts its mods.

## Run 5: updating a patcher in use

1. With the game running, rebuild the patcher with a changed version and copy it into Keel's folder; restart.
2. **Expect:** `.old` beside the new file after the start that replaced it, gone at the next.
   (Proves Windows allows renaming a patcher Mono has loaded; if not, Keel logs the reason and the fallback is KeelWatcher
   replacing it after the game closes.)

## After

- Put back: `AnchorChain.Preloader.dll.keel-off`, his mod list, Keel on.
- Results into `docs/test-<date>-takeover.md`, README/CHANGELOG status lines, `mods.json` `tested_in_game`.
- Green: archive the "Mod changes news display" session (his word 2026-11-12).
- Red: fix it here, or hand a What's new fault to that session (it built the card) with the log and screenshots; test again,
  then archive (his word 2026-11-12).

- After green and the release: the install video (`docs/install-video-plan.md`).
