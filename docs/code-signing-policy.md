# Code signing policy

`KeelSetup.exe` and `KeelWatcher.exe` are built from this repository's source by the [Build workflow](../.github/workflows/build.yml), which uses `tools/ci-build.ps1`.

**Status: not signed yet.** Windows may show "unknown publisher" for these files. An application for free open-source code signing with SignPath Foundation is being prepared.

## Team roles

- Committers and reviewers: [struckl](https://github.com/struckl)
- Approvers: [struckl](https://github.com/struckl)

Every signed release is approved by hand. Multi-factor authentication is required for everyone with these roles, on GitHub and on the signing service.

## What the programs do to your system

`KeelSetup.exe` says what it will change before you click Install: it adds BepInEx if the game has none, installs Keel's starter file, and renames another loader's preloader so only one loader is active. **Restore**, in the same window, undoes exactly those changes.

`KeelWatcher.exe` waits for the game to end, and shows a window if it did not end normally.

## Privacy policy

This program transfers information to other networked systems in two cases only: *Send report*, when you click *Send*, and the Workshop change notes of *What's new*, which reads public Steam pages and can be switched off. Both are described below; nothing else leaves your PC.

*Save logs* writes a zip to your own Desktop and sends it nowhere. The logs contain file paths, so also your Windows user name: keep that in mind if you share the file. The game itself has its own crash reporter, which is not part of Keel.

*Send report* is the only part of Keel that sends your data anywhere, and only when you click *Send* in its window, after it has shown you the list of files. It sends the same logs as *Save logs*, with your Windows user name replaced by `%USERNAME%` in every path, plus the note you typed, Keel's version and the game build, to the R.A.T.S. feedback board at hub.sewerlabs.dev. There they are kept for the developer only, never shown in public, and deleted when no longer needed. The board stores a keyed hash of your IP address for its rate limit, never the address itself.

*What's new* compares the mods with the last start on your PC. To show what the authors wrote about an update, Keel reads the public Steam Workshop changelog page (steamcommunity.com) of each Workshop mod that changed since the last start, and only of those. The request carries the item number and nothing about you beyond what any page view does (your IP address reaches Steam, as when you open the page in a browser). It is on by default; `WhatsNew.WorkshopNotes = false` in `BepInEx\config\dev.sewerlabs.seapowerkeel.cfg` switches it off, and then Keel uses the mod's own `CHANGELOG.md` instead.

## Third-party components

`KeelSetup.exe` contains unmodified BepInEx and UnityDoorstop (GNU LGPL 2.1), see `lib/THIRD-PARTY.txt`.
