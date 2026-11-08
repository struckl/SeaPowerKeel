Title: [Mod] R.A.T.S. Keel – faster, safer modding on every game version (your mods run on the 3D globe beta today)

Hi all,

**R.A.T.S. Keel replaces Anchor Chain** as the loader for Sea Power's code mods. Your existing mods work with it as they are. Not sure yet? Both can run side by side – Keel then simply steps aside for Anchor Chain's mods – but Anchor Chain's loader is no longer needed.

**Updated regularly**: Keel follows every game update and every beta build, and updates reach you through the Workshop by themselves.

**What you get**

- **Your mods work on the new 3D globe beta – today.** Anchor Chain can't start there, so no code mod loads. With Keel they do: tested on the public beta (build 261107) with eight mods and a mission, and on 0.8.7.
- **Outdated mods can't break your missions anymore.** Keel checks every mod against your game version *before* loading it. One that would stop missions from loading is left out, and you're told which one and why.
- **Faster start.** In my test the mods were ready 37 seconds after launch instead of 116.
- **One broken mod can't freeze your game** or stop the others from loading.
- **Only the mods you ticked** in the Mods menu load – switched-off mods really stay off.
- **Picks the right version of a mod for your game** when the mod ships one for the flat map and one for the globe.
- **Press Ctrl+Shift+K** to see every mod: running, problems, not started – and why, in plain words.
- **Logs with timestamps** that name the mod behind each error – handy when you ask a mod maker for help.

**Install**

1. Subscribe to R.A.T.S. Keel and switch it on in Mods.
2. Once: copy `SeaPowerKeel.Stub.dll` from the `copy-to-BepInEx-plugins` folder in Keel's Workshop folder into `Sea Power\BepInEx\plugins`.
3. To switch fully: delete `AnchorChain.Preloader.dll` from that folder. (Leave Anchor Chain subscribed – your mods list it as a requirement and Keel uses its files.)

Keel changes nothing in missions or saves; it only decides which mods start and tells you about it.

Thanks to Anchor Chain, which Sea Power code mods grew up on. Mod makers: Anchor Chain mods and plain BepInEx plugins both work, details in the source.

Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3835527459
Source (MIT): https://github.com/struckl/SeaPowerKeel

If something doesn't start, press Ctrl+Shift+K, hit "Copy report" and paste it here.

*R.A.T.S. – Ratte's Assorted Tactical Systems*
