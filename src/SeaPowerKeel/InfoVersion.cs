using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SeaPowerKeel;

/// <summary>
/// What a mod's <c>_info.ini</c> <c>[Compatibility]</c> section says about the game version, judged by the game's own rules
/// (<c>SeaPower.ModCompatibility.Matches</c>). A backup for what the DLL check cannot see, such as a transpiler that no
/// longer matches its target: the author's statement is the only sign of those. A statement Keel cannot read is no verdict.
/// </summary>
internal static class InfoVersion
{
    /// <summary>The sentence for the report when the mod's folder says it does not fit this game; otherwise null.</summary>
    public static string MismatchInFolder(string folder, string appVersion)
    {
        try
        {
            string ini = Path.Combine(folder ?? "", "_info.ini");
            return File.Exists(ini) ? Mismatch(File.ReadAllLines(ini), appVersion) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string Mismatch(IEnumerable<string> iniLines, string appVersion)
    {
        int[] app = Parse(appVersion);
        if (app == null)
            return null;
        Dictionary<string, string> c = Section(iniLines);
        string Get(string key) => c.TryGetValue(key, out string v) ? v : "";
        string Against(string statement) => $"Its _info.ini says {statement}, and this game is {appVersion}. Keel can't see every incompatibility in a DLL, so expect errors or parts that do nothing.";

        string exact = Get("ExactVersion");
        if (exact != "")
        {
            int[] v = Parse(exact);
            return v == null || Compare(app, v) == 0 ? null : Against($"exactly {exact}");
        }
        string approx = Get("ApproximateVersion");
        if (approx != "")
        {
            int[] v = Parse(approx);
            return v == null || (app[0] == v[0] && app[1] == v[1] && app[2] >= v[2]) ? null : Against($"about {approx}");
        }
        foreach (string excluded in Get("ExcludeVersions").Split(',').Select(s => s.Trim()))
        {
            int[] v = Parse(excluded);
            if (v != null && Compare(app, v) == 0)
                return Against($"not {excluded}");
        }
        string lower = Get("GreaterThanVersion"), lowerEq = Get("GreaterThanEqualToVersion");
        if (lower != "")
        {
            int[] v = Parse(lower);
            if (v != null && Compare(app, v) <= 0)
                return Against($"newer than {lower}");
        }
        else if (lowerEq != "")
        {
            int[] v = Parse(lowerEq);
            if (v != null && Compare(app, v) < 0)
                return Against($"{lowerEq} or newer");
        }
        string upper = Get("LessThanVersion"), upperEq = Get("LessThanEqualToVersion");
        if (upper != "")
        {
            int[] v = Parse(upper);
            if (v != null && Compare(app, v) >= 0)
                return Against($"older than {upper}");
        }
        else if (upperEq != "")
        {
            int[] v = Parse(upperEq);
            if (v != null && Compare(app, v) > 0)
                return Against($"{upperEq} or older");
        }
        return null;
    }

    private static Dictionary<string, string> Section(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inside = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                continue;
            if (line[0] == '[')
            {
                inside = line.Equals("[Compatibility]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (inside && eq > 0)
                values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return values;
    }

    private static int[] Parse(string version)
    {
        Match m = Regex.Match(version ?? "", @"^\s*v?(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success)
            return null;
        try
        {
            return new[] { int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0 };
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static int Compare(int[] a, int[] b)
    {
        for (int i = 0; i < 3; i++)
            if (a[i] != b[i])
                return a[i].CompareTo(b[i]);
        return 0;
    }
}
