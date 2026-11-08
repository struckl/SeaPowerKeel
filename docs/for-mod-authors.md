# Writing a mod for R.A.T.S. Keel

Keel runs two kinds of plugins from the game's mod folders (Workshop items and `Sea Power_Data\StreamingAssets\<folder>`).
You do not need to reference Keel.

## 1. A plain BepInEx plugin (recommended)

```csharp
[BepInPlugin("com.you.mymod", "My Mod", "1.2.0")]
[BepInDependency("com.someone.library", "2.0.0")]          // hard dependency, 2.0.0 or newer
[BepInDependency("com.someone.extras", BepInDependency.DependencyFlags.SoftDependency)]
[BepInIncompatibility("com.someone.othermod")]
public class Plugin : BaseUnityPlugin
{
    private void Awake() { /* ... */ }
}
```

The same DLL works when a player copies it into `BepInEx\plugins` instead; Keel then sees the id is already running and
leaves the mod-folder copy alone. `Logger`, `Config` (`BepInEx\config\<id>.cfg`) and `Info` work as usual, and Keel adds
the plugin to `Chainloader.PluginInfos`, so other plugins find it the BepInEx way.

## 2. An Anchor Chain mod

`[ACPlugin]` on an `IAnchorChainMod`, with `[ACDependency]` / `[ACIncompatibility]`, as Anchor Chain describes. While
Anchor Chain runs, Keel leaves these to it. When Anchor Chain is not running (for example when its preloader cannot start
on a game build), Keel starts them itself: it takes the types from Anchor Chain's DLL (a subscribed copy, else the
one Keel ships) and calls `TriggerEntryPoint` through the `IAnchorChainMod` interface (an explicit implementation works). Like Anchor
Chain it loads every DLL and creates every mod before the first one starts, only looks at public plugin classes, and handles
`[ACConfig]` (default `<id>.ini` in your mod folder, the player's copy `<id>_user.ini` in `StreamingAssets/ACConfigs`, new keys
merged in, `[AnchorChain.ResetValues]` applied once; a missing default file keeps the mod from starting). `AnchorChainLoader.FindFile`
works too. Differences: `before`/`after` only order the start (an absent plugin named there does not keep yours from
starting); `[ACIncompatibility]` only keeps your mod out when a named mod is really installed (Anchor Chain drops every mod that
declares one, even when none is installed: an upstream bug); only mod folders that are switched on are loaded. The Keel report
lists every DLL it left out on purpose (Keel's own and Anchor Chain's `AnchorChain.dll` / `AnchorChain.Preloader.dll`).

A DLL that has both an `[ACPlugin]` and a `[BepInPlugin]` entry runs through its Anchor Chain entry only.

## Two game builds: `keel.ini`

Sea Power has two builds in players' hands (the flat map of 0.8.7 and the 3D globe build). If your mod ships one DLL per
build, say which is which, and Keel starts only the right one:

```ini
[Keel]
globe = MyMod.dll
flat  = MyModFlat.dll
```

DLLs not named there are scanned as usual. Without `keel.ini` every DLL is scanned and each mod has to decide itself.

## The game-version check

Before a DLL is loaded, Keel checks every type, method and field it uses from the game's assemblies against the running
build (the same check as `modtool compat`):

- **A type of your mod derives from, implements or has a field of a type this build lacks**: the DLL is not loaded.
  Loading it would make Unity's ECS start-up throw while it walks all types, and then no mission loads for anyone.
  Keep such types out of the DLL for the other build (see `docs/both-versions.md` in the workspace).
- **A method or field use this build lacks**: a warning in the report. Fine if your mod checks the build before that code
  runs; otherwise that part throws.
- **A type or method looked up by name that this build lacks**: a warning in the report. Keel reads `[HarmonyPatch(typeof(X), "Name", ...)]`,
  `AccessTools.Method/Field/...(typeof(X), "Name")`, `AccessTools.Method("Ns.X:Name")`, and the literal name in
  `AccessTools.TypeByName`, `Type.GetType` and `Assembly.GetType` (an internal class is usually reached this way; the Type is
  followed into later `AccessTools` calls through a local variable or a static field). Names built at run time and
  `TargetMethod()` are not checked, and neither is whether a transpiler still matches the target's code. A mod that tries
  several names on purpose (one per game build) will show a warning for the ones that miss; that is expected.

Players can switch to `Strict` (any missing use or patch target keeps the DLL out) or `Off` in `BepInEx\config\dev.sewerlabs.seapowerkeel.cfg`.

## Errors

Keel starts plugins one at a time. One that throws while starting is marked `FAILED` and the next one starts. Exceptions
your mod throws later (from `Update`, Harmony patches …) are counted against it by its namespaces in the stack trace and
shown in the report (Ctrl+Shift+K, `BepInEx\Keel-report.txt`), and the log gets a line naming your mod with the stack
trace. Ask players for `BepInEx\Keel-log.txt` (or `Keel-log.1.txt` for the session before): every line there has the time
of day and the seconds since the game started.
