# Test run 0.5.4 (one session, game 0.9.1) - cards: × instead of right click, no fading

Before: game closed; `package keel --install --yes`; Radio one version back in `BepInEx\Keel-seen.txt` (What's new card, copy to `%TEMP%` first); `game.ps1 start`.

| # | Check | Pass when |
|---|---|---|
| 1 | Keel card has an × | Shot: × at the top right of the Keel card, title not cut into it |
| 2 | No fade | Both cards still there 90 s after they showed |
| 3 | Right click does nothing | Right click on What's new and on the Keel card: both stay |
| 4 | What's new × | Click on its ×: What's new closes, the Keel card glides down |
| 5 | Keel card click | Click on the card (not the ×) opens the report; Close closes it, the card stays |
| 6 | Keel card × | Click on its ×: the card goes, no report opens |
| 7 | Menu still takes clicks | After 6, a main-menu entry hovers/reacts (no stuck mouse) |

After: `Keel-seen.txt` left as Keel wrote it, game left open, lock off, results into `docs/test-2026-11-12-takeover.md`.
