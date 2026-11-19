# Test 2026-11-18: Anchor Chain 1.3, the switch to Keel (run 1)

- Test VM, game 0.9.3: beta copy (0.9.3-261116b) and Steam install (build 26712472). Plan: [test-plan-anchor-chain-1.3.md](test-plan-anchor-chain-1.3.md).
- Starts made: 2 of 4 (A on beta, B on public); C and D did not happen because the box never came.
- Results: 1 GREEN, 2 RED, 14 not run.

| # | Check | Result |
|---|---|---|
| 1 | Anchor Chain 1.3.0 loads the mods (beta) | GREEN: Anchor Chain 1.3.0, Radio 1.5.1 and Topographic Map 1.6.2 loaded |
| 2 | The box (beta) | RED: main menu without a box; no line of the bridge after more than a minute |
| 3 | Not now | not run: no box; nothing in BepInEx changed |
| 4 | The box (public) | RED: Cities and Borders, Pop-Out, Radio, Sensor Views and Topographic Map started under 1.3.0; no box, also after Options and back |
| 5-7 | Switch, files, Restart | not run: no box |
| 8-11 | Start C (Keel runs, Anchor Chain off, no box, errors) | not run; start B's log has no Error line |
| 12 | Restore | not run: no switch, so no Keel folder and no Keel files |
| 13 | Start D | not run |
| 14-17 | Cancel, failed download, Later, 0.8.7 | not run |

## Cause and fix

- **Cause:** the bridge waited for `LoadScreenView.Instance` to become null. The game sets it once, when the load screen is built, and never clears it, so the wait never ended.
- **Fix (run 2):** the main menu counts as up when the unit data is loaded, the start-up load screen says "Done" (the same test as Anchor Chain's preloader and Keel), the main menu view exists, and no mission or campaign scene is loaded. The bridge now logs "Waiting for the main menu", "Main menu is up", and a warning after five minutes without one.

## Run 2 (with the main-menu fix)

- Test VM, same builds. Starts made: 4 of 4 (A beta, B public, C after the restart, D after the Restore), none repeated.
- Results: 12 GREEN, 1 RED, 4 not run.

| # | Check | Result |
|---|---|---|
| 1 | Anchor Chain 1.3.0 loads the mods (beta) | GREEN: Anchor Chain 1.3.0, Radio 1.5.1, Topographic Map 1.6.2 loaded |
| 2 | The box (beta) | GREEN: on the main menu about 30 s after the start; top bar `MESSAGE · ANCHOR CHAIN`, text and both buttons readable; log has "Main menu is up" and the offer line |
| 3 | Not now | GREEN: box closed, menu worked (Options open and closed); log line; plugins unchanged, patchers empty |
| 4 | The box (public) | GREEN: same box and log lines |
| 5 | Switch to Keel | GREEN: progress box with Cancel at once, about 16 s later "installed and switched on" with Later and Restart; all four log lines |
| 6 | Files | GREEN: stub and patcher in place, `keel-setup.txt` lists both, mod list has Keel on with its Workshop id |
| 7 | Restart | GREEN: game closed about 3 s after the click, new game process about 10 s after it |
| 8 | Keel runs | GREEN: `Keel-report.txt` 24 s after the start, 9 plugins of the five R.A.T.S. mods running; Keel 0.6.0 card on the main menu |
| 9 | Anchor Chain does not start | GREEN: no Anchor Chain load line; `Keel-takeover.txt` names its preloader |
| 10 | No box | GREEN: no offer line, no box after the News window |
| 11 | Errors | GREEN: 0 error lines in starts B and C |
| 12 | KeelSetup Restore | GREEN: stub, patcher and `keel-setup.txt` gone, Anchor Chain's preloader still there |
| 13 | Back to Anchor Chain after Restore | RED: BepInEx found no plugin to load, so no Anchor Chain, no mod, no box |
| 14-17 | Cancel, failed download, Later, 0.8.7 | not run |

### Row 13: a Keel bug, not the bridge

- **Cause:** Keel's takeover marks Anchor Chain's preloader as holding no plugin in `BepInEx\cache\chainloader_typeloader.dat`. Restore removed the patcher but not that entry, and the preloader kept its write time, so BepInEx trusted the stale entry and skipped it.
- **Live in Keel 0.6.0:** any player who runs Restore after Keel took over (from Anchor Chain or Alpine) gets no mods at the next start.
- **Fix:** Restore deletes that cache file (branch `keel-restore-cache`, PR #6; offline selftest 227 of 227). Row 13 is run again once KeelSetup has the fix.

## Row 13 again (2026-11-19), with the fixed KeelSetup (PR #6)

- Test VM, public 0.9.3 (build 26712472), 2 starts: Keel taking over, then the start after the fixed Restore.
- Results: 7 of 7 GREEN.

| # | Check | Result |
|---|---|---|
| 1 | Keel runs | GREEN: Keel 0.6.0 card, 9 mods running, no problems |
| 2 | Anchor Chain does not start | GREEN: no Anchor Chain load line; `Keel-takeover.txt` names its preloader |
| 3 | The cache holds the takeover entry | GREEN: `chainloader_typeloader.dat` (321 bytes) names Anchor Chain's preloader |
| 4 | Restore with the fixed setup | GREEN: stub, patcher, `keel-setup.txt` and the cache file gone; Anchor Chain's preloader and the Harmony cache stay |
| 5 | Anchor Chain is back | GREEN: Anchor Chain 1.3.0 loaded the five R.A.T.S. mods |
| 6 | The switch box comes again | GREEN: box on the main menu; Not now closed it |
| 7 | Errors | GREEN: no error line (two Harmony warnings, one Pop-Out warning) |

- **Seen on the side:** Restore left `SeaPowerKeel.Patcher.dll.old`, the copy Keel keeps when it updates its patcher; harmless (BepInEx loads only `.dll`), and PR #6 now removes it too.
