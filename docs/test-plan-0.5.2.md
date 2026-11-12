# Test run 0.5.2 (one session, game 0.9.1) - player reports of 2026-11-12

Before: game closed; `package keel --install --yes`; Pop-Out's installed `_info.ini` gets `[Keel]` `StartEarly=true` (copy to
`%TEMP%` first); Radio one version back in `BepInEx\Keel-seen.txt` (What's new card); `game.ps1 start`.

| # | Check | Pass when |
|---|---|---|
| 1 | Earlier start (rgreat) | Log: "A mod asked to start early: Keel takes the mod list from usersettings.ini now"; Pop-Out's `STARTING` comes before `Start-up load screen done`; the game reaches the main menu and missions' unit data is fine (Encyclopedia opens) |
| 2 | Backup entry (rgreat) | `Keel-report.txt` has no `[SKIPPED]` row for the Alternative mods or Sinking Variety (was 5) |
| 3 | What's new: right click | Right click on the card hides it; the Keel card glides down |
| 4 | Keel card: right click | Right click hides the Keel card |
| 5 | Fade (both) | Next start: both cards fade on their own after `SummarySeconds` (60 s) without a click |
| 6 | Report window (Hussar) | Click on the Keel card opens the report; Close closes it and nothing stays on screen; Ctrl+Shift+K toggles it |

After: Pop-Out's `_info.ini` back, `Keel-seen.txt` left as Keel wrote it, game left open, lock off, results into `docs/test-2026-11-12-takeover.md`.
