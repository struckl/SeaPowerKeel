using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SeaPowerKeel.Loading;

internal enum ModState { Running, RunningWithErrors, Skipped, NotStarted }

internal sealed class ModLine
{
    public string Name;
    public ModState State;
    public double TookSeconds;     // time since the event before it: roughly how long this mod held the game up
}

/// <summary>
/// What the overlay shows, built from log lines. Lines come from any thread (BepInEx's log, the game's own log, R.A.T.S. Keel),
/// the overlay reads from its own thread, so everything goes through one lock. No game or BepInEx types in here: the preview tool
/// in <c>tools/preview</c> feeds it by hand.
/// </summary>
internal sealed class Progress
{
    private readonly object gate = new object();
    private readonly List<ModLine> mods = new List<ModLine>();
    private readonly Func<double> clock;          // seconds since the game process started

    private string phase = "Starting up";
    private int total;                            // plugins Keel found (0 = unknown)
    private int handled;                          // plugins with a result so far
    private double lastEvent;
    private string lastEventText = "";
    private bool done;
    private string keelVersion = "";
    private int running, withErrors, skipped, notStarted;
    private double keelMs, doneElapsed, slowestSeconds;
    private string slowestName = "";
    private int startIndex;                      // "STARTING i/n" seen: the step Keel is in
    private string current;                       // the mod Keel is starting right now (null between two)
    private double currentSince;
    private double doneAt;
    private bool dismissed;

    public Progress(Func<double> clock)
    {
        this.clock = clock;
        lastEvent = clock();
    }

    public sealed class Snapshot
    {
        public string Phase;
        public int Total, Handled;
        public double Elapsed, Idle;
        public string LastEvent;
        public bool Done;
        // what the summary shows once every mod has started
        public string KeelVersion, SlowestName;
        public int Running, WithErrors, Skipped, NotStarted;
        public double KeelSeconds, DoneElapsed, SlowestSeconds;
        public string Current;                  // the mod being started now
        public double CurrentFor;                 // for how long
        public int CurrentIndex;
        public double SinceDone;
        public bool Dismissed;
        public readonly List<ModLine> Recent = new List<ModLine>();
    }

    public Snapshot Take(int lines)
    {
        lock (gate)
        {
            double now = clock();
            var s = new Snapshot
            {
                Phase = phase, Total = total, Handled = handled, Elapsed = now, Idle = now - lastEvent,
                LastEvent = lastEventText, Done = done, KeelVersion = keelVersion, SlowestName = slowestName,
                Running = running, WithErrors = withErrors, Skipped = skipped, NotStarted = notStarted,
                KeelSeconds = keelMs / 1000.0, DoneElapsed = doneElapsed, SlowestSeconds = slowestSeconds, Current = current, CurrentFor = current != null ? now - currentSince : 0, CurrentIndex = startIndex, SinceDone = done ? now - doneAt : 0, Dismissed = dismissed,
            };
            for (int i = Math.Max(0, mods.Count - lines); i < mods.Count; i++)
                s.Recent.Add(mods[i]);
            return s;
        }
    }

    public void Dismiss() { lock (gate) dismissed = true; }

    // ---- log lines ----------------------------------------------------------------------------------------------------

    private static readonly Regex ModResult = new Regex(@"^(RUNNING WITH ERRORS|RUNNING|NOT STARTED|SKIPPED|FAILED|LEFT TO ANCHOR CHAIN)\b[:\s]+(.+)$", RegexOptions.Compiled);
    private static readonly Regex Started = new Regex(@"^Started (\d+) plugin\(s\) in (\d+) ms", RegexOptions.Compiled);
    private static readonly Regex KeelLine = new Regex(@"^R\.A\.T\.S\. Keel (\d+\.\d+\.\d+)", RegexOptions.Compiled);
    private static readonly Regex Found =new Regex(@"^Found (\d+) plugin", RegexOptions.Compiled);
    private static readonly Regex FoldersOn = new Regex(@"^(\d+) of (\d+) mod folders are on", RegexOptions.Compiled);
    private static readonly Regex Starting = new Regex(@"^STARTING\s+(\d+)\s*/\s*(\d+)\s*:\s*(.+)$", RegexOptions.Compiled);   // a line Keel may log before each start (see docs/design.md)

    /// <summary>One log line: <paramref name="source"/> is the log source's name ("R.A.T.S. Keel", "Unity Log", "AnchorChain Preloader" ...).</summary>
    public void Feed(string source, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        int nl = text.IndexOf('\n');
        string line = (nl > 0 ? text.Substring(0, nl) : text).Trim();
        lock (gate)
        {
            try { Apply(source ?? "", line); }
            catch (Exception) { /* a log line never breaks the loading screen */ }
        }
    }

    private void Event(string text)
    {
        lastEvent = clock();
        lastEventText = text;
    }

    private void Apply(string source, string line)
    {
        bool keel = source.IndexOf("Keel", StringComparison.OrdinalIgnoreCase) >= 0;
        if (keel)
        {
            Match m;
            if ((m = ModResult.Match(line)).Success)
            {
                string name = TrimFolder(m.Groups[2].Value);
                ModState state = m.Groups[1].Value switch
                {
                    "RUNNING" => ModState.Running,
                    "RUNNING WITH ERRORS" => ModState.RunningWithErrors,
                    "SKIPPED" or "LEFT TO ANCHOR CHAIN" => ModState.Skipped,
                    _ => ModState.NotStarted,
                };
                double now = clock();
                mods.Add(new ModLine { Name = name, State = state, TookSeconds = current != null ? now - currentSince : now - lastEvent });
                handled = startIndex > 0 ? startIndex : handled + 1;
                current = null;
                double took = mods[mods.Count - 1].TookSeconds;
                if (state == ModState.Running) running++;
                else if (state == ModState.RunningWithErrors) { running++; withErrors++; }
                else if (state == ModState.Skipped) skipped++;
                else notStarted++;
                if (took > slowestSeconds) { slowestSeconds = took; slowestName = name; }
                phase = "Starting mods";
                Event(name);
            }
            else if ((m = Starting.Match(line)).Success)
            {
                startIndex = int.Parse(m.Groups[1].Value);
                total = int.Parse(m.Groups[2].Value);          // the plan's own count: the bar fills exactly (skipped mods are not in it)
                handled = startIndex - 1;
                current = TrimFolder(m.Groups[3].Value);
                currentSince = clock();
                phase = "Starting mods";
                Event("starting " + current);
            }
            else if ((m = Found.Match(line)).Success)
            {
                if (startIndex == 0) total = int.Parse(m.Groups[1].Value);
                phase = "Starting mods";
                Event("found " + m.Groups[1].Value + " mods");
            }
            else if ((m = FoldersOn.Match(line)).Success)
            {
                phase = "Looking for mods";
                Event(line);
            }
            else if (line.StartsWith("Done:", StringComparison.Ordinal))
            {
                done = true;
                current = null;
                doneAt = clock();
                doneElapsed = doneAt;
                phase = "All mods started";
                Event(line);
            }
            else if (line.StartsWith("Start-up load screen done", StringComparison.Ordinal)) { phase = "Looking for mods"; Event(line); }
            else if (line.StartsWith("The game's mod list is there", StringComparison.Ordinal)) { phase = "Waiting for the game's load screen"; Event(line); }
            else if ((m = Started.Match(line)).Success) keelMs = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            else if (line.StartsWith("R.A.T.S. Keel ", StringComparison.Ordinal))
            {
                Match v = KeelLine.Match(line);
                if (v.Success) keelVersion = v.Groups[1].Value;
                phase = "Mod loader (Keel) started";
                Event(line);
            }
            return;
        }

        if (source.IndexOf("AnchorChain", StringComparison.OrdinalIgnoreCase) >= 0 || source.IndexOf("Anchor Chain", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (line.Contains("started")) phase = "Anchor Chain: searching the mod folders";
            else if (line.StartsWith("Could not find", StringComparison.Ordinal)) phase = "Starting up";
            Event(line);
            return;
        }

        // the game's own start-up, from its log
        if (line.Contains("(C) Triassic Games")) { phase = "Game starting"; Event("game engine is up"); }
        else if (line.Contains("FileManager.CheckFileValidity")) { phase = "Checking game files"; Event("checking game files"); }
        else if (line.Contains("FileManager.OnStart")) { phase = "Reading the mod list"; Event("reading the mod list"); }
        else if (line.Contains("3D WebView")) { phase = "Starting the game's web view"; Event("web view"); }
    }

    // "Name 1.2 (Workshop 123456)" -> "Name 1.2"; "Name 1.2 (SeaPowerTopo)" -> "Name 1.2"
    private static string TrimFolder(string s)
    {
        int paren = s.LastIndexOf(" (", StringComparison.Ordinal);
        int dash = s.IndexOf(" - ", StringComparison.Ordinal);
        string name = paren > 0 ? s.Substring(0, paren) : s;
        if (dash > 0 && dash < name.Length) name = name.Substring(0, dash);
        return name.Trim();
    }
}
