using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SeaPowerKeel;

/// <summary>
/// Finds the mod behind a crash nothing can pin on a mod. The player starts it from the report window. Each game then runs with half
/// of the suspect mod folders switched off in the game's own mod list, which covers code mods and packs without code alike: a crash
/// means the culprit is in the half that was on, a normal exit that it is in the half that was off. At the end the mod list is put
/// back, with the one folder left switched off.
/// <para>The list is only ever changed in <see cref="Advance"/>, called when Keel starts: before the game reads usersettings.ini, and
/// the game writes its list back at exit, so a change made during a game would be lost.</para>
/// Kept apart from Unity so it can be tested offline.
/// </summary>
internal sealed class CrashHunt
{
    private enum Phase { None, Armed, Round, Stopping }

    // Keel itself, Anchor Chain's library and preloader, and the config folder: without them the mods cannot start at all.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "SeaPowerKeel", "3835527459", "3380210757", "3832006068", "ACConfigs",
    };

    private static readonly Regex ModLine = new(@"^(Mod\d+Directory=)([^,\r\n]+),(True|False),(.*)$", RegexOptions.Compiled);

    private readonly string statePath;
    private readonly string settingsPath;
    private readonly Func<string, string> labelOf;
    private Phase phase;
    private int round;
    private List<string> allOn = new(), suspects = new(), off = new();

    /// <summary>What the player should be told at this start, or null.</summary>
    public string Message { get; private set; }

    /// <summary>One short line for the summary card.</summary>
    public string Short { get; private set; }

    /// <summary>A round is running in this game.</summary>
    public bool Running => phase == Phase.Round;

    public CrashHunt(string dir, string settingsPath, Func<string, string> labelOf = null)
    {
        statePath = Path.Combine(dir, "Keel-hunt.txt");
        this.settingsPath = settingsPath;
        this.labelOf = labelOf ?? (n => n);
        Load();
    }

    /// <summary>The report's button: the hunt begins at the next start.</summary>
    public void Arm()
    {
        phase = Phase.Armed;
        Save();
    }

    /// <summary>The report's button: the mod list is put back at the next start.</summary>
    public void Stop()
    {
        phase = Phase.Stopping;
        Save();
    }

    /// <summary>Judges the game that just ended and sets the mod list for this one.</summary>
    public void Advance(CrashGuard.Verdict previous)
    {
        switch (phase)
        {
            case Phase.Stopping:
                Finish(null, "The crash hunt was stopped and your mod list is back as it was.");
                break;
            case Phase.Armed:
                allOn = ListOn(ReadSettings()).Where(n => !Protected.Contains(n)).ToList();
                if (allOn.Count < 2)
                {
                    Finish(null, "The crash hunt needs at least two mods that can be switched off.");
                    break;
                }
                suspects = allOn.ToList();
                round = 0;
                NextRound();
                break;
            case Phase.Round:
                if (previous.Guid != null)
                {
                    // Keel named the mod itself: it died while being started (CrashGuard holds it out).
                    Finish(null, "The crash hunt ended: the game died while a mod was starting, so Keel can name it (see below). Your mod list is back as it was.");
                    break;
                }
                suspects = previous.Unclean ? suspects.Except(off).ToList() : off.ToList();
                if (suspects.Count == 1)
                    Finish(suspects[0], $"Crash hunt finished: the crash comes from {labelOf(suspects[0])}. It stays switched off, the rest of your mod list is back. Switch it on again in the Mod Manager to try it after an update.");
                else
                    NextRound();
                break;
        }
    }

    private void NextRound()
    {
        round++;
        off = suspects.Skip((suspects.Count + 1) / 2).ToList();
        WriteList(off);
        phase = Phase.Round;
        Save();
        int left = (int)Math.Ceiling(Math.Log(suspects.Count, 2));
        Short = $"Crash hunt: {suspects.Count} mods still suspect";
        Message = $"Crash hunt, about {left} more restart(s): {suspects.Count} mods are suspects, {off.Count} of them switched off for this game. Do what crashed the game. " +
                  "If it crashes, start the game again. If it does not, exit from the main menu and start again. Your saves may miss those mods until the hunt ends.";
    }

    private void Finish(string culprit, string message)
    {
        if (allOn.Count > 0)
            WriteList(culprit != null ? new List<string> { culprit } : new List<string>());
        phase = Phase.None;
        Message = message;
        Short = culprit != null ? $"Crash hunt found: {labelOf(culprit)}" : "Crash hunt ended";
        try
        {
            File.Delete(statePath);
        }
        catch (Exception)
        {
            // an old state file would only restart from the same round
        }
    }

    private void WriteList(List<string> switchedOff)
    {
        string text = ReadSettings();
        if (text == null)
            return;
        try
        {
            File.WriteAllText(settingsPath, SetFolders(text, allOn, switchedOff));
        }
        catch (Exception)
        {
            // settings locked: this game runs with the list as it is
        }
    }

    private string ReadSettings()
    {
        try
        {
            return File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Carries out the crash window's choice to keep a pack off. Called when Keel starts, like every change to the list. Returns the folder, or null.</summary>
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

    private void Save()
    {
        try
        {
            File.WriteAllLines(statePath, new[]
            {
                "phase=" + phase,
                "round=" + round,
                "allOn=" + string.Join("|", allOn),
                "suspects=" + string.Join("|", suspects),
                "off=" + string.Join("|", off),
            });
        }
        catch (Exception)
        {
            // without the state file the hunt simply does not go on
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(statePath))
                return;
            foreach (string line in File.ReadAllLines(statePath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = line.Substring(0, eq), value = line.Substring(eq + 1);
                List<string> list = value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                switch (key)
                {
                    case "phase": Enum.TryParse(value, out phase); break;
                    case "round": int.TryParse(value, out round); break;
                    case "allOn": allOn = list; break;
                    case "suspects": suspects = list; break;
                    case "off": off = list; break;
                }
            }
        }
        catch (Exception)
        {
            phase = Phase.None;
        }
    }
}
