using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace SeaPowerKeel;

/// <summary>
/// Opt-in "start early" (asked for by rgreat, author of the Alternative mods, 2026-11-12). On the 3D globe build Keel starts mods
/// only after the game's start-up load screen is done, because mods started during it can keep the unit data from loading (and
/// can crash in Noesis). A mod that knows it is safe there, and needs to be running while the game loads, asks for it, without
/// referencing Keel:
/// <list type="bullet">
/// <item>in its <c>_info.ini</c>: a <c>[Keel]</c> section with <c>StartEarly=true</c> (every plugin in that mod folder), or</item>
/// <item>in its DLL: <c>[assembly: System.Reflection.AssemblyMetadata("Keel.StartEarly", "true")]</c> (every plugin in that DLL).</item>
/// </list>
/// Early mods start as soon as Keel has checked them, before the load screen ends; every other mod waits as before. An early mod
/// that needs (or must start after) a mod that waits starts with the others, and the report says why.
/// </summary>
internal static class EarlyStart
{
    public const string InfoKey = "StartEarly", MetadataKey = "Keel.StartEarly";

    /// <summary>True when <paramref name="infoIni"/> (the text of a mod's _info.ini) has [Keel] StartEarly=true.</summary>
    public static bool InInfoIni(string infoIni)
    {
        bool inKeel = false;
        foreach (string raw in (infoIni ?? "").Split('\n'))
        {
            string line = raw.Split(';', '#')[0].Trim();
            if (line.StartsWith("["))
            {
                inKeel = line.Equals("[Keel]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (inKeel && eq > 0 && line.Substring(0, eq).Trim().Equals(InfoKey, StringComparison.OrdinalIgnoreCase))
                return IsTrue(line.Substring(eq + 1));
        }
        return false;
    }

    /// <summary>True when the folder's _info.ini asks for an early start (false when there is none or it cannot be read).</summary>
    public static bool FolderAsks(string folder)
    {
        try
        {
            string path = Path.Combine(folder ?? "", "_info.ini");
            return File.Exists(path) && InInfoIni(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when the assembly carries AssemblyMetadata("Keel.StartEarly", "true").</summary>
    public static bool AssemblyAsks(AssemblyDefinition assembly) =>
        assembly?.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute"
                                            && a.ConstructorArguments.Count == 2
                                            && a.ConstructorArguments[0].Value as string == MetadataKey
                                            && IsTrue(a.ConstructorArguments[1].Value as string)) == true;

    private static bool IsTrue(string v) => (v ?? "").Trim().Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase) || (v ?? "").Trim() == "1";

    /// <summary>
    /// The plugins that start early: those that asked and are still to start, less every one that depends on, or must start after,
    /// a plugin Keel starts later (repeated until stable). <paramref name="note"/> gets one line per plugin moved to the later start.
    /// </summary>
    public static HashSet<PluginEntry> Set(IList<PluginEntry> all, Action<PluginEntry, string> note)
    {
        var early = new HashSet<PluginEntry>(all.Where(p => p.StartEarly && p.State == PluginState.Pending));
        var byGuid = all.Where(p => p.State == PluginState.Pending).GroupBy(p => p.Guid).ToDictionary(g => g.Key, g => g.First());
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (PluginEntry p in early.ToList())
            {
                // what must be running before p starts: its dependencies, and mods that say they load before it
                string late = p.Dependencies.Select(d => d.Guid)
                               .Concat(all.Where(x => x.LoadBefore.Contains(p.Guid)).Select(x => x.Guid))
                               .Where(g => byGuid.TryGetValue(g, out PluginEntry x) && !early.Contains(x))
                               .Select(g => byGuid[g].Display).FirstOrDefault();
                if (late == null)
                    continue;
                early.Remove(p);
                changed = true;
                note?.Invoke(p, $"Asked to start early, but it needs {late}, which starts after the load screen: it starts with the others.");
            }
        }
        return early;
    }
}
