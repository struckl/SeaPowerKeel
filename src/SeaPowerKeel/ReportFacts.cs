using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SeaPowerKeel;

/// <summary>
/// What a sent report says in one line, for the feedback board's mail (player reports 2026-11-12: 7 of 12 mails said game "?",
/// every one was titled "Crash report" though 7 had no crash). Read from Keel-report.txt, so the crash window (KeelWatcher, outside
/// the game) can tell it too. Shared with KeelWatcher.
/// </summary>
internal static class ReportFacts
{
    /// <summary>The game's version text, e.g. 0.9.1-261112.</summary>
    public static readonly Regex Build = new(@"\b\d+\.\d+\.\d+-\d{6}\b");

    private static readonly Regex Problem = new(@"^\[(FAILED|ERRORS|NOT STARTED|CRASHED)\] (.+?) \(", RegexOptions.Multiline);

    /// <summary>The game build: <paramref name="given"/> when it is one, else from the report's first line, else from Keel's log.</summary>
    public static string Game(string given, string reportText, string logText)
    {
        if (!string.IsNullOrEmpty(given) && Build.IsMatch(given))
            return Build.Match(given).Value;
        string first = (reportText ?? "").Split('\n').FirstOrDefault() ?? "";
        Match m = Build.Match(first);
        if (!m.Success)
            m = Build.Match(logText ?? "");
        return m.Success ? m.Value : given ?? "";
    }

    /// <summary>"17 running, 1 failed" plus the names of the mods with problems, at most 300 characters.</summary>
    public static string Summary(string reportText)
    {
        string[] lines = (reportText ?? "").Replace("\r\n", "\n").Split('\n');
        // The counts line ("17 running, 1 failed") follows the header, which can have more lines (Euromod setup, reload progress).
        string counts = lines.Skip(1).Take(5).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && char.IsDigit(l[0])) ?? "";
        List<string> problems = Problem.Matches(reportText ?? "").Cast<Match>().Select(m => m.Groups[2].Value).Distinct().Take(6).ToList();
        string s = counts + (problems.Count > 0 ? (counts.Length > 0 ? ": " : "") + string.Join(", ", problems) : "");
        return s.Length > 300 ? s.Substring(0, 297) + "..." : s;
    }

    /// <summary>Reads both from a BepInEx folder; empty strings when the files are not there.</summary>
    public static (string game, string summary) Read(string bepInEx, string given)
    {
        string report = TryRead(Path.Combine(bepInEx ?? "", "Keel-report.txt"));
        string log = TryRead(Path.Combine(bepInEx ?? "", "Keel-log.txt"), 200_000);
        return (Game(given, report, log), Summary(report));
    }

    private static string TryRead(string path, int limit = 400_000)
    {
        try
        {
            if (!File.Exists(path))
                return "";
            string text = File.ReadAllText(path);
            return text.Length > limit ? text.Substring(0, limit) : text;
        }
        catch (Exception)
        {
            return "";
        }
    }
}
