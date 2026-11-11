using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeaPowerKeel;

/// <summary>
/// Keel's takeover: while Keel is switched on, no other mod loader starts, so Keel runs every mod and nothing starts twice.
/// The patcher (BepInEx\patchers\SeaPowerKeel.Patcher.dll) applies it before BepInEx starts any plugin; Keel installs that
/// patcher and reads what it did. Other loaders are recognized by their plugin id only: their files are never changed,
/// moved or read. Shared source: compiled into the loader, the patcher and the offline checks (no Unity, no BepInEx here).
/// </summary>
internal static class Takeover
{
    public const string PatcherFile = "SeaPowerKeel.Patcher.dll";
    public const string PatcherAssembly = "SeaPowerKeel.Patcher";
    public const string RecordFile = "Keel-takeover.txt";
    public const string StubGuid = "dev.sewerlabs.seapowerkeel.stub";
    public const string LoaderFile = "SeaPowerKeel.dll";
    public const string ConfigFile = "dev.sewerlabs.seapowerkeel.cfg";
    public const string ConfigKey = "TakeOver";

    /// <summary>Plugin ids of other mod loaders, with the name the report uses.</summary>
    public static readonly IReadOnlyDictionary<string, string> OtherLoaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["io.github.seapower_modders.anchorchain_preloader"] = "Anchor Chain's preloader",
        ["io.github.seapower_modders.anchorchain"] = "Anchor Chain",
        ["alpine.seapower.modloader"] = "Alpine's Mod Loader",
        ["alpine.seapower.modloader.core"] = "Alpine's Mod Loader",
    };

    public static bool IsOtherLoader(string guid) => guid != null && OtherLoaders.ContainsKey(guid);

    /// <summary>
    /// Whether the takeover applies this start: the stub is among the plugins BepInEx will start, Keel's folder is on in the
    /// game's mod list, and the player has not switched it off (Loading / TakeOver = false in Keel's config).
    /// </summary>
    public static string WhyNot(IEnumerable<string> pluginGuids, bool keelEnabled, string configText)
    {
        if (!pluginGuids.Any(g => string.Equals(g, StubGuid, StringComparison.OrdinalIgnoreCase)))
            return "Keel's starter file is not in BepInEx\\plugins";
        if (!keelEnabled)
            return "R.A.T.S. Keel is switched off in the game's mod list";
        if (SwitchedOff(configText))
            return "TakeOver is off in Keel's settings";
        return null;
    }

    /// <summary><c>TakeOver = false</c> under <c>[Loading]</c> in Keel's BepInEx config.</summary>
    public static bool SwitchedOff(string configText)
    {
        if (string.IsNullOrEmpty(configText))
            return false;
        bool inLoading = false;
        foreach (string raw in configText.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("["))
            {
                inLoading = line.Equals("[Loading]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (inLoading && eq > 0 && line.Substring(0, eq).Trim().Equals(ConfigKey, StringComparison.OrdinalIgnoreCase))
                return line.Substring(eq + 1).Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>One loader the patcher kept from starting: <c>guid \t name \t file</c> in the record.</summary>
    public sealed class Blocked
    {
        public string Guid, Name, File;
        public override string ToString() => $"{Guid}\t{Name}\t{File}";
    }

    /// <summary>
    /// The record the patcher writes at every start: first line the time (UTC ticks), then either <c>off\t&lt;why&gt;</c>,
    /// <c>error\t&lt;message&gt;</c> or one line per loader kept from starting (none: no other loader was there).
    /// </summary>
    public sealed class Record
    {
        public DateTime WrittenUtc;
        public string Off;
        public string Error;
        public readonly List<Blocked> Kept = new();

        public string Write() => string.Join("\n", new[] { WrittenUtc.Ticks.ToString() }
            .Concat(Off != null ? new[] { "off\t" + Off } : Error != null ? new[] { "error\t" + Error } : Kept.Select(b => b.ToString())));

        public static Record Read(string path)
        {
            try
            {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length == 0 || !long.TryParse(lines[0], out long ticks))
                    return null;
                var r = new Record { WrittenUtc = new DateTime(ticks, DateTimeKind.Utc) };
                foreach (string[] parts in lines.Skip(1).Select(l => l.Split('\t')))
                {
                    if (parts[0] == "off" && parts.Length > 1)
                        r.Off = parts[1];
                    else if (parts[0] == "error" && parts.Length > 1)
                        r.Error = parts[1];
                    else if (parts.Length >= 3)
                        r.Kept.Add(new Blocked { Guid = parts[0], Name = parts[1], File = parts[2] });
                }
                return r;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Copies the patcher from Keel's folder into BepInEx\patchers when it is missing or differs. A patcher BepInEx loaded this
    /// session is in use: it is renamed to <c>.old</c> (Windows allows that) and the new file takes its place for the next start.
    /// Returns "installed", "updated", "current", or the reason it could not.
    /// </summary>
    public static string Install(string source, string patchersFolder, out bool changed)
    {
        changed = false;
        if (!File.Exists(source))
            return $"{PatcherFile} is missing from Keel's folder";
        string target = Path.Combine(patchersFolder, PatcherFile);
        string old = target + ".old";
        try
        {
            if (File.Exists(old))
                File.Delete(old);
        }
        catch (Exception)
        {
            // still in use from the session that renamed it: next time
        }
        byte[] wanted = File.ReadAllBytes(source);
        bool had = File.Exists(target);
        if (had && File.ReadAllBytes(target).SequenceEqual(wanted))
            return "current";
        try
        {
            Directory.CreateDirectory(patchersFolder);
            try
            {
                File.WriteAllBytes(target, wanted);
            }
            catch (IOException) when (had)
            {
                if (File.Exists(old))
                    File.Delete(old);
                File.Move(target, old);
                File.WriteAllBytes(target, wanted);
            }
            changed = true;
            return had ? "updated" : "installed";
        }
        catch (Exception ex)
        {
            return $"could not write {target}: {ex.Message}";
        }
    }
}
