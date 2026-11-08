using System;
using System.Collections.Generic;

namespace SeaPowerKeel;

internal enum PluginKind
{
    BepInEx,        // a plain BepInEx plugin ([BepInPlugin] on a BaseUnityPlugin) in a mod folder
    AnchorChain,    // an Anchor Chain mod ([ACPlugin] on an IAnchorChainMod)
}

internal enum PluginState
{
    Pending,
    Running,            // started, no error so far
    RunningWithErrors,  // started, but threw while starting or later
    Failed,             // threw while starting
    NotStarted,         // a rule kept it from starting: missing dependency, conflict, wrong game version ...
    Skipped,            // on purpose and harmless: duplicate copy, other game version's build, switched off
    LeftToAnchorChain,  // an Anchor Chain mod while Anchor Chain itself is running
}

internal sealed class Dependency
{
    public string Guid;
    public Version Min;
    public Version Max;     // inclusive upper bound (Anchor Chain's ranges); null = any
    public bool Hard = true;

    public bool Accepts(Version v) => (Min == null || v >= Min) && (Max == null || v <= Max);

    public string Range => Min == null && Max == null ? "" : Max == null ? $" {Min} or newer" : Min == null ? $" up to {Max}" : $" {Min} to {Max}";
}

internal sealed class PluginEntry
{
    public string Guid;
    public string Name;
    public Version Version = new(1, 0);
    public PluginKind Kind;
    public string TypeName;
    public string Dll;
    public string AssemblyName;
    public ModFolder Folder;
    public int Order;                       // folder position, then file, then type: the tie-breaker everywhere

    public readonly List<Dependency> Dependencies = new();
    public readonly List<string> LoadBefore = new();   // plugins that must start after this one (Anchor Chain "before")
    public readonly List<string> Incompatible = new();
    public readonly List<string> Processes = new();    // [BepInProcess]: empty = any
    public bool UsesAcConfig;
    public bool AcConfigRequired = true;               // [ACConfig(required)]: a config file that cannot be made keeps the mod from starting
    public bool Exported = true;                        // Anchor Chain only sees public (exported) plugin classes

    public PluginState State = PluginState.Pending;
    public string Reason = "";
    public readonly List<string> Warnings = new();
    public int LaterErrors;
    public string FirstLaterError;
    public float StartMs;
    public object Instance;

    public string Display => string.IsNullOrEmpty(Name) ? Guid : Name;

    public void Stop(PluginState state, string reason)
    {
        State = state;
        Reason = reason;
    }
}

/// <summary>A DLL the scanner did not read as a plugin source, and why (shown in the report).</summary>
internal sealed class SkippedDll
{
    public string Path;
    public ModFolder Folder;
    public string Reason;
}
