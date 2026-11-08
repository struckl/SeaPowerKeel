Title: [Mod] R.A.T.S. Keel – a new mod loader for Sea Power: loads your Anchor Chain mods on the 3D globe beta, checks every mod against your game version first

Hi all,

Since the 3D globe beta, Anchor Chain's preloader can't find its loader, so no code mod starts there. I wrote a new mod loader from scratch: **R.A.T.S. Keel**. It loads your existing Anchor Chain mods (nothing to change in them) and a lot more carefully.

**What it does better**

- **A mod made for the other game version can't break your missions anymore.** Before anything is loaded, Keel checks every mod against the game version you are running. A DLL that would stop missions from loading is left out, and you're told why.
- **The right build for your game version.** Mods that ship a flat-map DLL and a globe DLL get only the matching one started.
- **Your mods on the 3D globe beta, today** – run in the game on the public beta (build 261107) with eight Anchor Chain mods and a mission. Also run on 0.8.7.
- **Always works on the beta branch.** Keel uses nothing of the game that changes between builds and is run on new betas as they come out.
- **Only the mods you switched on** in the Mods menu load. Switched-off mods stay off.
- **One broken mod can't take the others down** – no endless load loop, no frozen game.
- **Faster start:** in my test the mods were running 37 s after launch, against 116 s with Anchor Chain (which also loaded the switched-off ones).
- **See what happened:** Ctrl+Shift+K shows every mod – running, errors, not started and why (missing mod and the version it needs, a conflict, wrong game version).
- **Logs with timestamps**, naming the mod each error came from. On the globe build BepInEx's own log misses the game's lines – Keel's has them. Old sessions are kept.
- **Maintained, updates by themselves** through the Workshop. The one file you copy by hand never changes again.

**Install / switch over**

1. Subscribe to R.A.T.S. Keel and switch it on in Mods.
2. Copy `SeaPowerKeel.Stub.dll` from the `copy-to-BepInEx-plugins` folder in Keel's Workshop folder into `Sea Power\BepInEx\plugins` (once).
3. Delete `AnchorChain.Preloader.dll` from that folder.
4. Keep Anchor Chain subscribed – its mods are built against its files, Keel uses them.

Keel changes nothing in missions or saves; it only decides which code mods start and tells you about it.

Credit where it's due: Anchor Chain is what Sea Power code mods grew up on. Keel reads Anchor Chain mods through their public attributes, so both keep working. Mod makers: plain BepInEx plugins work too, docs in the source.

Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3835527459
Source (MIT): https://github.com/struckl/SeaPowerKeel

Feedback welcome – the Ctrl+Shift+K report has a "Copy report" button, paste it here if something doesn't start.

*R.A.T.S. – Ratte's Assorted Tactical Systems*
