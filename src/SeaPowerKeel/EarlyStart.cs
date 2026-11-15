using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace SeaPowerKeel;

internal static class EarlyStart
{
    private const string Timing = "LoadTiming", Legacy = "StartEarly";

    // Null means the author did not specify a timing. Anchor Chain timing is the default.
    public static bool? InInfoIni(string text)
    {
        bool inKeel = false;
        bool? timing = null, legacy = null;
        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.Split(';', '#')[0].Trim();
            if (line.StartsWith("["))
            {
                inKeel = line.Equals("[Keel]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (!inKeel || eq <= 0)
                continue;
            string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1);
            if (key.Equals(Timing, StringComparison.OrdinalIgnoreCase))
                timing = ParseTiming(value);
            else if (key.Equals(Legacy, StringComparison.OrdinalIgnoreCase))
                legacy = IsTrue(value);
        }
        return timing ?? legacy;
    }

    public static bool? FolderTiming(string folder)
    {
        try
        {
            string path = Path.Combine(folder, "_info.ini");
            return File.Exists(path) ? InInfoIni(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool? AssemblyTiming(AssemblyDefinition assembly)
    {
        string Value(string key) => assembly?.CustomAttributes
            .Where(a => a.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute" &&
                        a.ConstructorArguments.Count == 2 && a.ConstructorArguments[0].Value as string == key)
            .Select(a => a.ConstructorArguments[1].Value as string).FirstOrDefault();

        string legacy = Value("Keel." + Legacy);
        return ParseTiming(Value("Keel." + Timing)) ?? (IsTrue(legacy) ? true : (bool?)null);
    }

    private static bool? ParseTiming(string value) => value?.Trim().Trim('"').ToUpperInvariant() switch
    {
        "EARLY" => true,
        "ANCHORCHAIN" => false,
        _ => null,
    };

    private static bool IsTrue(string value) =>
        value?.Trim().Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase) == true || value?.Trim() == "1";

    /// <summary>Selects pending early plugins whose prerequisites can also start early.</summary>
    public static HashSet<PluginEntry> Set(IList<PluginEntry> all, Action<PluginEntry, string> note)
    {
        List<PluginEntry> pending = all.Where(p => p.State == PluginState.Pending).ToList();
        var prerequisites = Planner.Prerequisites(pending);
        var early = new HashSet<PluginEntry>(pending.Where(p => p.StartEarly));
        bool changed;
        do
        {
            changed = false;
            foreach (PluginEntry plugin in pending.Where(early.Contains))
            {
                PluginEntry late = prerequisites[plugin].FirstOrDefault(p => !early.Contains(p));
                if (late == null)
                    continue;
                early.Remove(plugin);
                changed = true;
                note?.Invoke(plugin, $"Starts at Anchor Chain timing because it must start after {late.Display}.");
            }
        } while (changed);
        return early;
    }
}
