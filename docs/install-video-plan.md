# Install video for the Workshop page (planned)

His request 2026-11-12: once the takeover and What's new are tested in the game and the build is released, an installation
video for Keel's Workshop page that shows the steps on a clean copy of the game.

## When

After `test-plan-takeover.md` is green and the release is on the Workshop, so the video shows what players get.

## Setup

- **Clean copy:** a fresh copy of the game folder through R.A.T.S. Flotilla (no BepInEx, no mods), so KeelSetup's
  "adds BepInEx" step shows. His own install stays untouched.
- **Recording:** ffmpeg (`gdigrab`, installed via winget) records only the window being shown (Steam page, Explorer, KeelSetup,
  the game), at 1920x1080, 30 fps. Never the whole desktop: no other windows and no notifications in the picture.
  Steam's Discord overlay is off for the takes.
- Only on his go, like every game test; he does not work on the PC while it records.

## Steps shown (the README's Install section, in order)

1. Keel's Workshop page: Subscribe.
2. Steam: Sea Power, Manage, Browse local files, up to `workshop\content\1286220\3835527459`.
3. `KeelSetup.exe`: the game it found, Install, the list of what it did.
4. Start the game: the Mods menu, switch R.A.T.S. Keel on, Restart (Keel's button in the reload dialog).
5. The loading window with the mods starting, the summary card, Ctrl+Shift+K report.
6. Optional: KeelSetup's Restore.

## Finishing

- Short title cards in the R.A.T.S. look (`..\..\BRANDING.md`): "R.A.T.S. Keel – install in one click", one line per
  step, the footer line. No voice, no music needed; 60-90 s.
- Output `workshop/video/keel-install.mp4`, shown to him first.
- The Workshop takes only a YouTube link. **He uploads it to YouTube** (his account); then
  `python tools/modtool.py workshop keel video <link> --yes` puts it on the page, and `workshop keel check` confirms it.
