# Run on the 3D globe public beta – 2026-11-08, build 261107

Same mods switched on as on 0.8.7 (BetterTacMap, Anchor Chain loader, IdentifyExpanded, Radio, Topographic Map, Cities and
Borders) plus a folder with the globe-only test DLL. His 2024 Anchor Chain preloader stayed in BepInEx\plugins.

- Anchor Chain's preloader: `Could not find AnchorChain chainloader` (AnchorChain#19) – no Anchor Chain mod would load.
- Keel: mod list at once, waited 18.7 s for the start-up load screen, found 9 plugins in 9 DLLs (1.9 s), checked them
  (0.8 s), started all 9 (3.7 s). The 8 Anchor Chain mods ran under Keel; our mods' gates picked the globe builds.
- The globe-only test DLL (refused on 0.8.7 as a class-load breaker) ran, as it should.
- Intro 01 loaded; Topographic Map and Cities and Borders added their map layers and drew on the globe (screenshot).
- The version check's warnings came true: IdentifyExpanded logged `Method not found: GeoPosition.CourseTo` every frame, Radio's
  fuel calls and Topo's cursor readout failed on fields this beta removed; Keel's log named the mod each time.
- BepInEx's LogOutput.log got 49 lines (the game's Unity log does not reach it on this build); Keel-log.txt had everything.

Fixed from it: hotkey (did not reach IMGUI on this build), repeated-line folding (log 1.2 MB in 2 min → 135 KB in a whole
session), solid window background (not yet seen in the game).

Not tested: `keel.ini`, `DisabledPlugins`, `Strict`/`Off`, "Copy report". The game's Esc did not open the pause menu in the
mission on this build; the game was closed with Alt+F4 (no save).
