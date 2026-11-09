using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SeaPowerKeel;

/// <summary>
/// The game's own mod list in usersettings.ini. Keel only changes it when it starts: before the game reads the file, and the game
/// writes its list back at exit, so a change made during a game would be lost. Kept apart from Unity so it can be tested offline.
/// </summary>
internal static class ModList
{
    private static readonly Regex ModLine = new(@"^(Mod\d+Directory=)([^,\r\n]+),(True|False),(.*)$", RegexOptions.Compiled);

    /// <summary>Carries out the crash window's choice to keep a pack off. Returns the folder, or null.</summary>
    internal static string ApplyPackOff(string dir, string settingsPath)
    {
        string file = Path.Combine(dir, CrashText.PackOffFile);
        try
        {
            if (!File.Exists(file))
                return null;
            string folder = File.ReadAllText(file).Trim();
            File.Delete(file);
            if (folder.Length == 0 || !File.Exists(settingsPath))
                return null;
            var one = new[] { folder };
            File.WriteAllText(settingsPath, SetFolders(File.ReadAllText(settingsPath), one, one));
            return folder;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The folders switched on in the game's mod list, in its order.</summary>
    internal static List<string> ListOn(string settings)
    {
        var on = new List<string>();
        if (settings == null)
            return on;
        foreach (string line in settings.Replace("\r\n", "\n").Split('\n'))
        {
            Match m = ModLine.Match(line);
            if (m.Success && m.Groups[3].Value == "True")
                on.Add(m.Groups[2].Value);
        }
        return on;
    }

    /// <summary>The settings with every folder of <paramref name="managed"/> switched on, except those in <paramref name="switchedOff"/>. Other lines stay as they are.</summary>
    internal static string SetFolders(string settings, ICollection<string> managed, ICollection<string> switchedOff)
    {
        string newline = settings.Contains("\r\n") ? "\r\n" : "\n";
        IEnumerable<string> lines = settings.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            Match m = ModLine.Match(line);
            if (!m.Success || !managed.Contains(m.Groups[2].Value))
                return line;
            return $"{m.Groups[1].Value}{m.Groups[2].Value},{(switchedOff.Contains(m.Groups[2].Value) ? "False" : "True")},{m.Groups[4].Value}";
        });
        return string.Join(newline, lines);
    }
}
