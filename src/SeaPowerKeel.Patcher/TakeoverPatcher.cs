using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace SeaPowerKeel.Patcher;

/// <summary>
/// A BepInEx patcher that patches no game assembly. BepInEx runs it before it looks for plugins. While R.A.T.S. Keel is switched
/// on, it marks the DLLs of other mod loaders in BepInEx's own plugin cache (BepInEx\cache\chainloader_typeloader.dat) as holding
/// no plugin, so the chainloader does not start them. It writes that cache file itself, and nothing is
/// hooked or patched: if anything here does not fit, BepInEx scans the folder as usual and the other loader starts (Keel then leaves
/// its mods to it). Their files stay as they are; with Keel switched off the entries are taken out again and BepInEx reads the
/// files afresh at the same start.
/// It must not touch BepInEx's plugin types (PluginInfo, BaseUnityPlugin) or anything else that refers to Unity: that loads
/// UnityEngine before BepInEx has hooked it, and BepInEx then does not start at all (found in the game 2026-11-12).
/// </summary>
public static class TakeoverPatcher
{
    private const string CacheName = "chainloader";

    public static IEnumerable<string> TargetDLLs { get; } = Array.Empty<string>();

    public static void Patch(AssemblyDefinition assembly)
    {
    }

    private static ManualLogSource log;

    public static void Initialize()
    {
        var record = new Takeover.Record();
        try
        {
            log = Logger.CreateLogSource("R.A.T.S. Keel takeover");
            Run(record);
        }
        catch (Exception ex)
        {
            record = new Takeover.Record { Error = ex.GetType().Name + ": " + ex.Message };
            log?.LogWarning($"Takeover not applied this start (other loaders start as usual): {ex}");
        }
        WriteRecord(record);
    }

    private static void Run(Takeover.Record record)
    {
        // The same paths the chainloader builds its cache keys from.
        string pluginDir = Path.GetFullPath(Paths.PluginPath);
        var guids = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string dll in Directory.Exists(pluginDir) ? Directory.GetFiles(pluginDir, "*.dll", SearchOption.AllDirectories) : Array.Empty<string>())
            guids[dll] = PluginGuids(dll);
        List<string> others = guids.Where(kv => kv.Value.Any(Takeover.IsOtherLoader)).Select(kv => kv.Key).ToList();

        bool keelOn = ModFolders.FromDisk().Any(f => f.Enabled && ModFolders.FindFile(f.Path, Takeover.LoaderFile).Any());
        string configPath = Path.Combine(Paths.ConfigPath, Takeover.ConfigFile);
        string config = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        record.Off = Takeover.WhyNot(guids.Values.SelectMany(g => g), keelOn, config);

        string cacheFile = Path.Combine(Paths.CachePath, CacheName + "_typeloader.dat");
        if (record.Off == null && others.Count > 0 && !CacheOn())
        {
            record.Off = "BepInEx's assembly cache is off (EnableAssemblyCache in BepInEx.cfg)";
            log.LogWarning($"Takeover needs BepInEx's assembly cache: {record.Off}.");
        }
        if (record.Off != null || others.Count == 0)
        {
            // Nothing to keep out: a fresh scan, so an entry written at an earlier start cannot hide a loader now.
            if (others.Count > 0 && File.Exists(cacheFile))
                File.Delete(cacheFile);
            if (others.Count > 0)
                log.LogInfo($"Other mod loaders may start: {record.Off}.");
            return;
        }
        // The cache as BepInEx reads it (TypeLoader.LoadAssemblyCache): count, then per DLL its full path, its write time (UTC
        // ticks) and how many plugins it holds. Only the other loaders' DLLs are listed, each with none; BepInEx scans every other
        // DLL afresh and writes the full cache itself. Written by hand: BepInEx's own PluginInfo type refers to Unity, and a patcher
        // that touches it makes Mono load UnityEngine before BepInEx has hooked it, so BepInEx would not start at all.
        Directory.CreateDirectory(Paths.CachePath);
        using (var w = new BinaryWriter(File.Create(cacheFile)))
        {
            w.Write(others.Count);
            foreach (string dll in others)
            {
                w.Write(dll);
                w.Write(File.GetLastWriteTimeUtc(dll).Ticks);
                w.Write(0);
            }
        }
        foreach (string dll in others)
            foreach (string g in guids[dll].Where(Takeover.IsOtherLoader).Distinct())
            {
                record.Kept.Add(new Takeover.Blocked { Guid = g, Name = Takeover.OtherLoaders[g], File = dll });
                log.LogMessage($"Not starting {Takeover.OtherLoaders[g]} ({dll}): R.A.T.S. Keel runs the mods. Its files stay as they are; " +
                               "switch Keel off in the game's mod list to use it again.");
            }
    }

    // [Caching] EnableAssemblyCache in BepInEx.cfg (default on).
    private static bool CacheOn()
    {
        string cfg = Path.Combine(Paths.ConfigPath, "BepInEx.cfg");
        if (!File.Exists(cfg))
            return true;
        bool inCaching = false;
        foreach (string raw in File.ReadAllLines(cfg))
        {
            string line = raw.Trim();
            if (line.StartsWith("["))
                inCaching = line.Equals("[Caching]", StringComparison.OrdinalIgnoreCase);
            else if (inCaching && line.StartsWith("EnableAssemblyCache", StringComparison.OrdinalIgnoreCase) && line.Contains("="))
                return !line.Substring(line.IndexOf('=') + 1).Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    // The ids of the [BepInPlugin] attributes in a DLL, read without loading it.
    private static List<string> PluginGuids(string dll)
    {
        var ids = new List<string>();
        try
        {
            using AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { ReadSymbols = false });
            foreach (TypeDefinition t in asm.MainModule.Types)
                foreach (CustomAttribute a in t.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInPlugin" && a.ConstructorArguments.Count > 0))
                    if (a.ConstructorArguments[0].Value is string g)
                        ids.Add(g);
        }
        catch (Exception)
        {
            // not a .NET DLL, or unreadable: BepInEx decides about it as usual
        }
        return ids;
    }

    private static void WriteRecord(Takeover.Record record)
    {
        try
        {
            record.WrittenUtc = DateTime.UtcNow;
            File.WriteAllText(Path.Combine(Paths.BepInExRootPath, Takeover.RecordFile), record.Write());
        }
        catch (Exception ex)
        {
            log?.LogWarning($"Could not write {Takeover.RecordFile}: {ex.Message}");
        }
    }
}
