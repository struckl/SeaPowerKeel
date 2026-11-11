using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;

namespace SeaPowerKeel.Patcher;

/// <summary>
/// A BepInEx patcher that patches no game assembly. BepInEx runs it before any plugin; it hooks the chainloader's plugin search
/// and, while R.A.T.S. Keel is switched on, takes other mod loaders out of the list BepInEx starts. Their files stay as they are,
/// so switching Keel off in the game's mod list (or TakeOver = false) gives them back at the next start.
/// </summary>
public static class TakeoverPatcher
{
    public static IEnumerable<string> TargetDLLs { get; } = Array.Empty<string>();

    public static void Patch(AssemblyDefinition assembly)
    {
    }

    private static ManualLogSource log;

    public static void Initialize()
    {
        log = Logger.CreateLogSource("R.A.T.S. Keel takeover");
        try
        {
            MethodInfo find = typeof(TypeLoader).GetMethod(nameof(TypeLoader.FindPluginTypes), BindingFlags.Public | BindingFlags.Static)
                              ?.MakeGenericMethod(typeof(PluginInfo));
            if (find == null)
            {
                WriteRecord(new Takeover.Record { Error = "BepInEx has no TypeLoader.FindPluginTypes" });
                return;
            }
            new Harmony("dev.sewerlabs.seapowerkeel.takeover").Patch(find, postfix: new HarmonyMethod(typeof(TakeoverPatcher), nameof(AfterFind)));
        }
        catch (Exception ex)
        {
            log.LogWarning($"Takeover not active this start: {ex.Message}");
            WriteRecord(new Takeover.Record { Error = ex.Message });
        }
    }

    // The chainloader's search for plugins in BepInEx\plugins (its cache name is "chainloader").
    private static void AfterFind(string cacheName, object __result)
    {
        if (cacheName != "chainloader" || __result is not Dictionary<string, List<PluginInfo>> found)
            return;
        var record = new Takeover.Record();
        try
        {
            bool keelOn = ModFolders.FromDisk().Any(f => f.Enabled && ModFolders.FindFile(f.Path, Takeover.LoaderFile).Any());
            string configPath = Path.Combine(Paths.ConfigPath, Takeover.ConfigFile);
            string config = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
            record.Off = Takeover.WhyNot(found.Values.SelectMany(l => l).Select(p => p.Metadata?.GUID), keelOn, config);
            if (record.Off != null)
            {
                log.LogInfo($"Other mod loaders may start: {record.Off}.");
            }
            else
            {
                foreach (string file in found.Keys.ToList())
                {
                    List<PluginInfo> plugins = found[file];
                    List<PluginInfo> other = plugins.Where(p => Takeover.IsOtherLoader(p.Metadata?.GUID)).ToList();
                    if (other.Count == 0)
                        continue;
                    foreach (PluginInfo p in other)
                    {
                        record.Kept.Add(new Takeover.Blocked { Guid = p.Metadata.GUID, Name = Takeover.OtherLoaders[p.Metadata.GUID], File = file });
                        log.LogMessage($"Not starting {p.Metadata.Name} {p.Metadata.Version} ({file}): R.A.T.S. Keel runs the mods. " +
                                       "Its files stay as they are; switch Keel off in the game's mod list to use it again.");
                    }
                    found[file] = plugins.Except(other).ToList();
                }
            }
        }
        catch (Exception ex)
        {
            // Nothing taken out: the other loader starts and Keel leaves its mods to it, as before.
            record = new Takeover.Record { Error = ex.Message };
            log.LogWarning($"Takeover not applied this start: {ex}");
        }
        WriteRecord(record);
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
