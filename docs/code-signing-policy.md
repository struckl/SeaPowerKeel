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

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

*Save logs* writes a zip to your own Desktop and sends it nowhere. The logs contain file paths, so also your Windows user name: keep that in mind if you share the file. The game itself has its own crash reporter, which is not part of Keel.

## Third-party components

`KeelSetup.exe` contains unmodified BepInEx and UnityDoorstop (GNU LGPL 2.1), see `lib/THIRD-PARTY.txt`.
