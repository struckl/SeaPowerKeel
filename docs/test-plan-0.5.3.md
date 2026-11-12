# Test run 0.5.3 (game 0.9.1) - board post #3

Before: game closed; `package keel --install --yes`; tick **Alternative Game Interface** (Workshop 3813683442, subscribed) in the Mod
Manager (new subscription: tick in the game, then restart); `game.ps1 start`.

| # | Check | Pass when |
|---|---|---|
| 1 | Report open/close with AGI | Click the Keel card (or Ctrl+Shift+K), click Close; then SCENARIOS opens and Back works |
| 2 | Same with Esc and the hotkey | Report closed with Esc and with Ctrl+Shift+K; the menu still takes clicks each time |
| 3 | Mods menu with AGI | MOD MANAGER opens and Back works after the report was open |

After: untick AGI in the Mod Manager (game closed or through the menu), leave the game open, lock off, results into `docs/test-2026-11-12-takeover.md`.
