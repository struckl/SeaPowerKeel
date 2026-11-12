# Idea: What's new splits off the loading card (later version)

His wish 2026-11-12, for a later Keel version. Not built yet.

## Today

- The loading window sits at the top right while mods start, then shrinks and glides to the bottom left as the summary card
  ("Keel 0.x started N mods in N s").
- The What's new card is a separate card at the top right of the main menu.

## The idea

- **One card becomes two.** When the loading window has glided down to the bottom left and there is something new, the What's new
  card is born out of it, like a cell dividing:
  - The summary card bulges, pinches in the middle and splits in two ("bubbly birth" / *Zellteilung*): soft, round, a little elastic.
  - The new half is the What's new card.
- **Both stay at the bottom left, stacked.**
  - What's new grows from the bottom up, in the bottom left corner.
  - It pushes the Keel summary card up above it.
- **Hard limit:** the summary card must never cover the main menu's **EXIT** entry (nor any other menu entry). If there is not
  enough room, What's new gets shorter (fewer mods, "+N more") instead of pushing the summary card higher.
- No What's new (nothing changed) → no split, the summary card stays as today.

## What stays as today

- × on What's new closes it; a click on it opens the full list in Keel's report window.
- A click on the summary card opens the report; a mission start takes both away.

## Notes for building it

- Both cards are IMGUI and drawn every frame, so the split is a few hundred ms of tweened shapes: two rounded rectangles
  growing out of one, with a narrowing neck drawn between them (a few small circles along the seam that shrink to nothing).
- EXIT's position: from the main menu's view (`SeapowerUI.MainMenuView`), the lowest menu entry's screen rect; fall back to a fixed
  share of the screen height if it cannot be found.
- Keep the top right corner free: mods put their own buttons there (the AC Pack's settings gear).
- Test at 1080p and 4K, with 1 and with 10+ changed mods.
