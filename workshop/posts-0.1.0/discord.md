**R.A.T.S. Keel – new mod loader for Sea Power** ⚓
Anchor Chain's preloader doesn't start on the 3D globe beta, so no code mod loads there. **Keel does** – your Anchor Chain mods, unchanged.

• **Checks every mod against your game version first** – a mod made for the other version can't break your missions anymore
• **Right build for your version** – flat-map or globe DLL, only the matching one starts
• **Works on the globe beta today** (tested on build 261107, eight mods + a mission) and on 0.8.7 – and keeps working on the beta branch
• **Only switched-on mods load**, one broken mod can't stop the others, no frozen game
• **Faster start** – mods running at 37 s instead of 116 s in my test
• **Ctrl+Shift+K report**: what runs, what doesn't and why
• **Timestamped logs** that name the mod behind each error
• **Maintained**, updates come through the Workshop – the hand-copied file never changes

**Switch over:** subscribe → copy `SeaPowerKeel.Stub.dll` (from `copy-to-BepInEx-plugins` in Keel's Workshop folder) into `BepInEx\plugins` → delete `AnchorChain.Preloader.dll` → keep Anchor Chain subscribed.

Workshop: <https://steamcommunity.com/sharedfiles/filedetails/?id=3835527459>
Source: <https://github.com/struckl/SeaPowerKeel>
*R.A.T.S. – Ratte's Assorted Tactical Systems*
