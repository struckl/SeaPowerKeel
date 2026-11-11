using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SeaPowerKeel;

/// <summary>
/// Big mod sets that players stack in stages, named in one line at the top of the report, the crash files and Send report, so a
/// bug report says which stage it came from. Today: Euromod. Players speak of three stages: Euromod (the Main Pack), Euromod +
/// AC Pack (Chevron 9's Anchorchain Expansion Pack, code), and the AC Pack with its experimental Extended Guidance (EG) on.
/// The AC Pack keeps its submod switches in <c>&lt;persistentDataPath&gt;\Mods\ACPack\submods.ini</c>, section
/// <c>[Submods]</c>, <c>Id=true|false</c>; a missing entry is on, except ExtendedGuidance, which is off.
/// </summary>
internal static class ModSetups
{
    public const ulong EuromodMain = 3629144864;
    public const ulong EuromodAcPack = 3784474738;
    public const string AcPackMarker = "ACPackSettings.dll";
    public const string EgId = "ExtendedGuidance";

    public static string SubmodsIni(string persistentDataPath) => Path.Combine(persistentDataPath, "Mods", "ACPack", "submods.ini");

    public sealed class Euromod
    {
        public bool Main, AcPack, Eg;
        public string AcPackUpdated;               // "2026-11-11" from Steam's update time, or null
        public readonly List<string> SubmodsOff = new();

        public string Line()
        {
            if (!Main && !AcPack)
                return null;
            string stage = !AcPack ? "Euromod (Main Pack)"
                         : (Main ? "Euromod + AC Pack" : "AC Pack without the Euromod Main Pack")
                           + (AcPackUpdated != null ? $" (updated {AcPackUpdated})" : "")
                           + (Eg ? " + Extended Guidance ON (experimental)" : ", Extended Guidance off");
            return "Euromod setup: " + stage + (SubmodsOff.Count > 0 ? $"; AC Pack submods off: {string.Join(", ", SubmodsOff)}" : "") + ".";
        }
    }

    /// <summary>The <c>[Submods]</c> switches: id to on/off. Invalid lines are skipped.</summary>
    public static Dictionary<string, bool> ReadSubmods(string text)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text))
            return map;
        bool inSection = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                continue;
            if (line.StartsWith("["))
            {
                inSection = line.Equals("[Submods]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (!inSection || eq <= 0)
                continue;
            if (bool.TryParse(line.Substring(eq + 1).Trim(), out bool on))
                map[line.Substring(0, eq).Trim()] = on;
        }
        return map;
    }

    /// <param name="enabled">The mod folders switched on.</param>
    /// <param name="submodsText">The AC Pack's submods.ini, or null when there is none.</param>
    /// <param name="steamTimes">Steam's update time per Workshop item (unix seconds), may be null.</param>
    public static Euromod Detect(IEnumerable<ModFolder> enabled, string submodsText, IDictionary<ulong, long> steamTimes)
    {
        var e = new Euromod();
        foreach (ModFolder f in enabled)
        {
            string name = WhatsNew.NameOf(f.Path) ?? "";
            if (f.SteamId == EuromodMain || name.Equals("Euromod - Main Pack", StringComparison.OrdinalIgnoreCase))
                e.Main = true;
            if (f.SteamId == EuromodAcPack || name.StartsWith("Euromod - Anchorchain Expansion Pack", StringComparison.OrdinalIgnoreCase)
                || File.Exists(Path.Combine(f.Path, AcPackMarker)))
            {
                e.AcPack = true;
                if (f.SteamId != 0 && steamTimes != null && steamTimes.TryGetValue(f.SteamId, out long t) && t > 0)
                    e.AcPackUpdated = DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
        if (e.AcPack)
        {
            Dictionary<string, bool> sw = ReadSubmods(submodsText);
            e.Eg = sw.TryGetValue(EgId, out bool eg) && eg;
            e.SubmodsOff.AddRange(sw.Where(kv => !kv.Value && !kv.Key.Equals(EgId, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        }
        return e;
    }
}
