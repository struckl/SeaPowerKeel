using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SeaPowerKeel;

/// <summary>
/// What Keel can do about a crash. A crash in native code (Unity, the graphics driver, a bad transpiler) ends the process, and no
/// code inside it can catch that. So Keel keeps a journal on disk of what it is doing (<c>Keel-session.txt</c>, handed to the
/// operating system at every step, so it survives the process dying), and the next start reads it: a game that did not quit
/// normally while Keel was loading or starting a mod names that mod, and the mod is held out from then on, so the game starts.
/// A crash later on is pinned on a mod when the mod's code is on the crash's stack in Unity's Player-prev.log.
/// Kept apart from Unity so it can be tested offline.
/// </summary>
internal sealed class CrashGuard
{
    public sealed class Verdict
    {
        /// <summary>The last session has a journal and it never reached a normal quit.</summary>
        public bool Unclean;
        /// <summary>"load" or "start" when the session ended inside that step of a mod, else null.</summary>
        public string Step;
        public string Guid;
        public string Scene;
        public string Began;
    }

    private static readonly string[] CrashMarkers =
    {
        "Got a UNKNOWN while executing native code",
        "Native Crash Reporting",
        "Crash!!!",
        "A crash has been intercepted by the crash handler",
        "Received signal SIG",
    };

    private const int MaxCrashLines = 400;

    private readonly string dir;
    private readonly string journalPath;
    private readonly string heldPath;
    private readonly string playerPrev;
    private FileStream journal;

    public Verdict Previous { get; }

    /// <summary>One short line for the summary card, when the last game crashed.</summary>
    public string Short { get; private set; }

    /// <summary>A mod is being kept out in this game because of an earlier crash.</summary>
    public bool HeldActive { get; private set; }
    internal bool HasHeldPlugins => File.Exists(heldPath);

    /// <summary>The report's button: every held mod runs again from the next start.</summary>
    public void ClearHeld()
    {
        try
        {
            File.Delete(heldPath);
        }
        catch (Exception)
        {
            // the list stays as it is
        }
    }

    public CrashGuard(string dir, string playerPrevLog)
    {
        this.dir = dir;
        playerPrev = playerPrevLog;
        journalPath = Path.Combine(dir, "Keel-session.txt");
        heldPath = Path.Combine(dir, "Keel-held.txt");
        Previous = Read(ReadLines(journalPath));
        KeepLastStack();
        try
        {
            journal = new FileStream(journalPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception)
        {
            // no journal this session: the next start simply has nothing to say
        }
        Mark("session", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    // The catcher empties its file when it is installed, which is before this game's mods are known.
    private void KeepLastStack()
    {
        try
        {
            string stack = Path.Combine(dir, CrashText.StackFile), kept = Path.Combine(dir, CrashText.StackPrevFile);
            if (File.Exists(stack) && new FileInfo(stack).Length > 0)
                File.Copy(stack, kept, true);
            else if (File.Exists(kept))
                File.Delete(kept);
        }
        catch (Exception)
        {
            // then the next start has less to say
        }
    }

    /// <summary>One step, handed to the operating system at once. Never throws.</summary>
    public void Mark(string kind, string detail)
    {
        if (journal == null)
            return;
        try
        {
            byte[] line = Encoding.UTF8.GetBytes(kind + "\t" + detail + Environment.NewLine);
            journal.Write(line, 0, line.Length);
            journal.Flush();
        }
        catch (Exception)
        {
            journal = null;
        }
    }

    /// <summary>
    /// Reads a journal. A "load" or "start" stays open until the next one or "done": the game gets a frame after each step, and a
    /// BepInEx plugin's own Start() runs in that frame, so a crash there still counts against the mod.
    /// </summary>
    internal static Verdict Read(IEnumerable<string> lines)
    {
        var v = new Verdict();
        bool any = false, quit = false;
        foreach (string line in lines)
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;
            any = true;
            string kind = line.Substring(0, tab), detail = line.Substring(tab + 1);
            switch (kind)
            {
                case "session": v.Began = detail; break;
                case "load":
                case "start": v.Step = kind; v.Guid = detail; break;
                case "done": v.Step = null; v.Guid = null; break;
                case "scene": v.Scene = detail; break;
                case "quit": quit = true; break;
            }
        }
        v.Unclean = any && !quit;
        if (!v.Unclean)
        {
            v.Step = null;
            v.Guid = null;
        }
        return v;
    }

    /// <summary>The crash part of a Unity player log, or null when the log shows no crash.</summary>
    internal static string CrashBlock(string log)
    {
        if (string.IsNullOrEmpty(log))
            return null;
        int at = CrashMarkers.Select(m => log.IndexOf(m, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        if (at < 0)
            return null;
        int lineStart = log.LastIndexOf('\n', at) + 1;
        return string.Join("\n", log.Substring(lineStart).Replace("\r\n", "\n").Split('\n').Take(MaxCrashLines));
    }

    /// <summary>The first part of a mod's main class name, or null when that says nothing about whose code it is.</summary>
    internal static string RootOf(PluginEntry p)
    {
        if (string.IsNullOrEmpty(p.TypeName))
            return null;
        int dot = p.TypeName.IndexOf('.');
        string root = dot > 0 ? p.TypeName.Substring(0, dot) : p.TypeName;
        return root.Length > 2 && !Runner.SharedNamespaces.Contains(root) ? root : null;
    }

    /// <summary>The mod whose code is highest on the crash's stack, or null when only the game's and Unity's frames are there.</summary>
    internal static PluginEntry Blame(string crashBlock, IReadOnlyCollection<PluginEntry> plugins)
    {
        List<PluginEntry> named = Blamable(plugins);
        int hit = CrashText.FirstHit(crashBlock, named.Select(RootOf).ToList());
        return hit >= 0 ? named[hit] : null;
    }

    /// <summary>The mods a stack can be pinned on, in the order blame goes by.</summary>
    internal static List<PluginEntry> Blamable(IEnumerable<PluginEntry> plugins) => plugins.OrderBy(p => p.Order).Where(p => RootOf(p) != null).ToList();

    /// <summary>For the watcher, which runs outside the game: id, name and code root of every mod.</summary>
    public void WriteModsFile(IEnumerable<PluginEntry> plugins)
    {
        try
        {
            File.WriteAllLines(Path.Combine(dir, CrashText.ModsFile), Blamable(plugins).Select(p => $"{p.Guid}\t{p.Display}\t{RootOf(p)}"));
        }
        catch (Exception)
        {
            // the watcher then names mods by their id, or not at all
        }
    }

    private static string Stamp(string dll)
    {
        try
        {
            var f = new FileInfo(dll);
            return f.LastWriteTimeUtc.Ticks + ":" + f.Length;
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Reads what the last session left and acts on it: says what happened, and holds out the mod it can name (when <paramref name="hold"/>).
    /// A held mod stays out until its DLL changes (an update of the mod) or its line is removed from Keel-held.txt.
    /// </summary>
    public void Apply(IReadOnlyCollection<PluginEntry> plugins, bool hold, Action<string> note)
    {
        var held = new Dictionary<string, (string stamp, string reason)>();
        foreach (string line in ReadLines(heldPath))
        {
            string[] parts = line.Split('\t');
            if (parts.Length == 3)
                held[parts[0]] = (parts[1], parts[2]);
        }

        var keptOn = new HashSet<string>(ReadLines(Path.Combine(dir, CrashText.KeepFile)).Select(l => l.Trim()));
        if (Previous.Unclean)
        {
            string caught = ReadAll(Path.Combine(dir, CrashText.StackPrevFile));
            string block = !string.IsNullOrWhiteSpace(caught) ? caught : CrashBlock(ReadAll(playerPrev));
            string detail = CrashText.CrashLine(block);
            WriteCrashFile(block);
            string where = Previous.Scene != null ? $" (last scene: {Previous.Scene})" : "";
            PluginEntry culprit = null;
            string how = null;
            if (Previous.Guid != null)
            {
                culprit = plugins.FirstOrDefault(p => p.Guid == Previous.Guid);
                if (culprit != null)
                    how = Previous.Step == "load" ? "while Keel was loading it" : "while Keel was starting it";
            }
            if (culprit == null)
            {
                culprit = Blame(block, plugins);
                how = "with its code on the crash's stack";
            }
            if (culprit != null)
            {
                string why = $"The last game ended unexpectedly {how}.";
                Short = $"Last game crashed: {culprit.Display}";
                note($"The last game did not end normally{where}: {culprit.Display} {culprit.Version} ({how}). Details: Keel-crash.txt.");
                if (hold && !keptOn.Contains(culprit.Guid))
                    held[culprit.Guid] = (Stamp(culprit.Dll), why);
            }
            else
            {
                Short = "Last game crashed: cause unknown";
                string loading = "";
                if (CrashText.LastPack(block, out string packFolder, out string packFile))
                {
                    Short = $"Last game crashed loading {Path.GetFileName(packFile)}";
                    loading = $" It was loading {Path.GetFileName(packFile)} from mod folder {packFolder} just before.";
                }
                note($"The last game did not end normally{where}, and Keel cannot tell for certain which mod did it{(detail != null ? $" ({detail})" : "")}.{loading} Details: Keel-crash.txt.");
            }
        }

        try
        {
            File.Delete(Path.Combine(dir, CrashText.KeepFile));
        }
        catch (Exception)
        {
            // only an answer to the last crash
        }

        var keep = new List<string>();
        foreach (KeyValuePair<string, (string stamp, string reason)> h in held)
        {
            PluginEntry p = plugins.FirstOrDefault(x => x.Guid == h.Key);
            if (p == null)
            {
                keep.Add($"{h.Key}\t{h.Value.stamp}\t{h.Value.reason}");   // its folder is off for now: the hold stays
                continue;
            }
            if (Stamp(p.Dll) != h.Value.stamp)
                continue;   // the mod was updated: it gets another chance
            keep.Add($"{h.Key}\t{h.Value.stamp}\t{h.Value.reason}");
            if (hold && p.State == PluginState.Pending)
            {
                p.Stop(PluginState.NotStarted, h.Value.reason + " Keel keeps it out so the game starts, until the mod is updated. To try it anyway, use 'Let held mods run' in this window.");
                HeldActive = true;
            }
        }
        try
        {
            if (keep.Count > 0)
                File.WriteAllLines(heldPath, keep);
            else if (File.Exists(heldPath))
                File.Delete(heldPath);
        }
        catch (Exception)
        {
            // the hold list could not be saved: next start decides again from the journal
        }
    }

    private void WriteCrashFile(string block)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"The game session started {Previous.Began} did not end normally.");
            sb.AppendLine($"Keel was {(Previous.Guid != null ? $"at the '{Previous.Step}' step of {Previous.Guid}" : "not loading or starting a mod")}; last scene: {Previous.Scene ?? "none"}.");
            sb.AppendLine();
            sb.AppendLine(block ?? "Player-prev.log shows no crash report.");
            File.WriteAllText(Path.Combine(dir, "Keel-crash.txt"), sb.ToString());
        }
        catch (Exception)
        {
            // only a convenience
        }
    }

    private static string ReadAll(string path)
    {
        try
        {
            if (path == null || !File.Exists(path))
                return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        string text = ReadAll(path);
        return text == null ? Array.Empty<string>() : text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToArray();
    }
}
