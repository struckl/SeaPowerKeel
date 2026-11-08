Title: [Mod] R.A.T.S. Keel 0.1.4 – Euromod now runs under Keel, plus better version checks

Hi all,

Some of you reported that Euromod's Anchorchain Expansion Pack and its sonar systems didn't work properly under Keel. Thanks for that. I went through how Keel starts mods, compared it with Anchor Chain, and fixed every difference I found.

**Tested on the 3D globe beta (build 261107) with Euromod's Main Pack and Expansion Pack:** all 25 mods in that setup started, and a mission ran without errors from Keel. On 0.8.7 these changes passed the offline checks, but I haven't run them in the game yet.

**What changed**

- **Big Anchor Chain packs start properly.** Keel now loads every DLL first and then starts the mods in dependency order, as Anchor Chain does. A mod that can't load keeps only the mods that need it out.
- **Sonar audio:** Euromod's Sonar Audio Loader can search other mods' folders again.
- **More mods are recognised.** Only Anchor Chain's own two DLLs are skipped now, not everything with "AnchorChain" in its name. Mods with an explicit entry point work too, and anything Keel leaves out is listed with the reason.
- **ACConfig files** are now handled by Keel itself.
- **Better version checks.** Since 0.1.2 and 0.1.3 Keel also finds Harmony patches and by-name lookups aimed at things your game build no longer has.
- **New in 0.1.4: Keel reads a mod's `_info.ini` too.** If the mod's own version statement doesn't fit your game, you get a warning. It's a backup for the cases a DLL check can't see, such as transpilers.

**One thing to expect with Euromod:** on the current beta a few of its features switch themselves off because the game changed underneath them (parts of EnhancedDecoyMod, MissileControlMod, Optimal Target Distance and Test Range). The game keeps running and those features fall back to normal behaviour. That's for the Euromod team to update, and Keel's report shows you which ones.

Keel updates by itself through the Workshop.

**Having problems? Please reach out.** I want to fix these, so add me on Steam or Discord and I'll tackle it with you:

- Steam: https://steamcommunity.com/profiles/76561198169349193
- Discord: DISCORD-NAME

Press Ctrl+Shift+K in the game, hit "Copy report" and send me that. It tells me nearly everything I need.

Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3835527459
Source (MIT): https://github.com/struckl/SeaPowerKeel

*R.A.T.S. – Ratte's Assorted Tactical Systems*
