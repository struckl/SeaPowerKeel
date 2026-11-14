using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SeaPowerKeel;

internal static class Report
{
    public static string Tag(PluginState s) => s switch
    {
        PluginState.Running => "RUNNING",
        PluginState.RunningWithErrors => "ERRORS",
        PluginState.Failed => "FAILED",
        PluginState.NotStarted => "NOT STARTED",
        PluginState.Skipped => "SKIPPED",
        PluginState.LeftToAnchorChain => "OTHER LOADER",
        _ => "WAITING",
    };

    public static bool IsProblem(PluginEntry p) =>
        p.State is PluginState.Failed or PluginState.NotStarted or PluginState.RunningWithErrors;

    public static int CountOrderWarnings(IEnumerable<PluginEntry> all) => all.Count(p => p.LoadOrderWarning != null);

    public static string Summary(IReadOnlyCollection<PluginEntry> all)
    {
        int running = all.Count(p => p.State is PluginState.Running or PluginState.RunningWithErrors);
        int errors = all.Count(p => p.State == PluginState.RunningWithErrors);
        int failed = all.Count(p => p.State == PluginState.Failed);
        int notStarted = all.Count(p => p.State == PluginState.NotStarted);
        int ac = all.Count(p => p.State == PluginState.LeftToAnchorChain);
        int orderWarnings = CountOrderWarnings(all);
        var parts = new List<string> { $"{running} running" };
        if (errors > 0) parts.Add($"{errors} with errors");
        if (failed > 0) parts.Add($"{failed} failed");
        if (notStarted > 0) parts.Add($"{notStarted} not started");
        if (ac > 0) parts.Add($"{ac} left to another loader");
        if (orderWarnings > 0) parts.Add($"{orderWarnings} with load order warnings");
        return string.Join(", ", parts);
    }

    public static string Text(IReadOnlyCollection<PluginEntry> all, string header, IEnumerable<string> notes, IReadOnlyCollection<SkippedDll> skipped = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine(Summary(all));
        foreach (string n in notes)
            sb.AppendLine("Note: " + n);
        sb.AppendLine();
        foreach (PluginEntry p in Sorted(all))
        {
            sb.AppendLine($"[{Tag(p.State)}] {p.Display} {p.Version} ({p.Guid}) – {p.Folder?.Label}, {System.IO.Path.GetFileName(p.Dll)}, {p.Kind}" +
                          (p.State is PluginState.Running or PluginState.RunningWithErrors ? $", started in {p.StartMs:0} ms" : ""));
            if (!string.IsNullOrEmpty(p.Reason))
                sb.AppendLine("    " + p.Reason);
            foreach (string w in p.Warnings)
                sb.AppendLine("    warning: " + w);
            if (p.LoadOrderWarning != null)
                sb.AppendLine("    warning: " + p.LoadOrderWarning);
            if (p.MissingUses.Count > 2)
                sb.AppendLine("    all missing in this build: " + string.Join("; ", p.MissingUses.Take(60)) + (p.MissingUses.Count > 60 ? $" (and {p.MissingUses.Count - 60} more)" : ""));
            if (p.LaterErrors > 0)
                sb.AppendLine($"    {p.LaterErrors} error(s) since it started, first: {p.FirstLaterError}");
        }
        if (skipped != null && skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"DLLs left out on purpose ({skipped.Count}):");
            foreach (SkippedDll d in skipped)
                sb.AppendLine($"[SKIPPED DLL] {System.IO.Path.GetFileName(d.Path)} – {d.Folder?.Label}, {d.Path}{Environment.NewLine}    {d.Reason}");
        }        sb.AppendLine();
        sb.AppendLine("R.A.T.S. Keel - Kanalratte");
        return sb.ToString();
    }

    /// <summary>Problems first, then running, then the rest; each group in start/mod-list order.</summary>
    public static IEnumerable<PluginEntry> Sorted(IEnumerable<PluginEntry> all) =>
        all.OrderBy(p => p.State switch
        {
            PluginState.Failed => 0,
            PluginState.NotStarted => 1,
            PluginState.RunningWithErrors => 2,
            PluginState.Running => 3,
            PluginState.LeftToAnchorChain => 4,
            _ => 5,
        }).ThenBy(p => p.Order);

    public static string Short(IList<string> items, int n = 2) =>
        string.Join("; ", items.Take(n)) + (items.Count > n ? $" (and {items.Count - n} more)" : "");
}
