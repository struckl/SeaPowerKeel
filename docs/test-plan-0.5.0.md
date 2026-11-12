# Test run 0.5.0 (one session, game 0.9.1)

One install, one game session, every point below; findings go in a list, then one fix run (rule 2026-11-12).

Before: game closed, `package keel --install --yes`, Radio set one version back in `BepInEx\Keel-seen.txt` (so the card shows),
Euromod items + AC Pack ticked, `game.ps1 start` (it clicks the version question).

| # | Check | Pass when |
|---|---|---|
| 1 | Version question | Top bar *MESSAGE · R.A.T.S. KEEL*, no Keel sentence in the list; the driver clicked Ignore by itself |
| 2 | Summary card | "Keel 0.5.0 started …" |
| 3 | What's new card | One line per mod; Radio's line ends in "…"; below the AC Pack gear |
| 4 | Card click / × | Click opens the report at WHAT'S NEW; × closes the card |
| 5 | Report rows | Two lines per mod; "click to read all" under Radio; a click opens all of Radio's notes, "click to close" closes them |
| 6 | Report header | `Keel-report.txt` line 1 has `Sea Power 0.9.1-261112, 3D globe map` |
| 7 | Euromod line + EG button | Setup line shows; *Turn EG on* → "EG on after restart" |
| 8 | ALTERNATIVE MODS | In the main menu; log names the three Alternative mods |
| 9 | Mods menu reload | Switch one code mod on, Accept, Reload: `Keel-report.txt` is rewritten while it runs ("reload in progress") and at the end |
| 10 | Send report | Send window opens; send ONE test report with note "test 0.5.0": board answers #N; the mail is titled *Report #N - 0.9.1-261112 - …* (needs the board deploy) |
| 11 | Restart from the report window | Game restarts; EG ON in the setup line |

After: put back Euromod/EG as before, leave the game open, release the lock, write results into `docs/test-2026-11-12-takeover.md`.
