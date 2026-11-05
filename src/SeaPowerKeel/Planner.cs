using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerKeel;

/// <summary>
/// Decides which plugins start and in which order. Every "no" gets a reason a player can act on. Rules, in this order:
/// duplicates (the newest copy wins), already running elsewhere, switched off by the player, wrong process, then
/// dependencies and conflicts until nothing changes, then a start order that keeps every dependency before its user and
/// otherwise follows the game's mod list.
/// </summary>
internal static class Planner
{
    public static List<PluginEntry> Plan(List<PluginEntry> all, ISet<string> runningElsewhere, ISet<string> disabledByPlayer,
                                         string processName)
    {
        List<PluginEntry> live() => all.Where(p => p.State == PluginState.Pending).ToList();

        foreach (PluginEntry p in live().Where(p => string.IsNullOrEmpty(p.Guid)))
            p.Stop(PluginState.NotStarted, "Its plugin attribute has no id.");

        // One GUID, several copies (a Workshop copy and a local one, or two folders shipping the same library): newest
        // version, then the one higher up in the mod list.
        foreach (IGrouping<string, PluginEntry> g in live().GroupBy(p => p.Guid).Where(g => g.Count() > 1))
        {
            PluginEntry keep = g.OrderByDescending(p => p.Version).ThenBy(p => p.Order).First();
            foreach (PluginEntry other in g.Where(p => p != keep))
                other.Stop(PluginState.Skipped, $"Another copy runs: {keep.Version} from {keep.Folder.Label}.");
        }

        foreach (PluginEntry p in live())
        {
            if (runningElsewhere.Contains(p.Guid))
                p.Stop(PluginState.Skipped, "Already running (installed in BepInEx\\plugins).");
            else if (disabledByPlayer.Contains(p.Guid))
                p.Stop(PluginState.Skipped, "Switched off in Keel's settings (DisabledPlugins).");
            else if (p.Processes.Count > 0 && !p.Processes.Any(n => string.Equals(StripExe(n), processName, StringComparison.OrdinalIgnoreCase)))
                p.Stop(PluginState.Skipped, $"Made for another program ({string.Join(", ", p.Processes)}).");
        }

        // Dependencies and conflicts; a plugin that cannot start can take others with it, so repeat until stable.
        for (bool changed = true; changed;)
        {
            changed = false;
            Dictionary<string, PluginEntry> starting = live().ToDictionary(p => p.Guid);
            foreach (PluginEntry p in starting.Values)
            {
                string why = null;
                foreach (Dependency d in p.Dependencies.Where(d => d.Hard))
                {
                    if (runningElsewhere.Contains(d.Guid))
                        continue;
                    if (!starting.TryGetValue(d.Guid, out PluginEntry dep))
                    {
                        PluginEntry known = all.FirstOrDefault(x => x.Guid == d.Guid);
                        why = known == null
                            ? $"Needs {d.Guid}{d.Range}, which is not installed or switched off in the mod list."
                            : $"Needs {known.Display}{d.Range}, which does not start: {known.Reason}";
                        break;
                    }
                    if (!d.Accepts(dep.Version))
                    {
                        why = $"Needs {dep.Display}{d.Range}; installed is {dep.Version}.";
                        break;
                    }
                }
                if (why == null)
                {
                    string clash = p.Incompatible.FirstOrDefault(g => starting.ContainsKey(g) || runningElsewhere.Contains(g));
                    if (clash != null)
                        why = $"Does not work together with {(starting.TryGetValue(clash, out PluginEntry c) ? c.Display : clash)}, which is running.";
                }
                if (why != null)
                {
                    p.Stop(PluginState.NotStarted, why);
                    changed = true;
                }
            }
        }

        return Order(live());
    }

    /// <summary>Dependencies (hard and soft) and "load before" first, ties in mod-list order; a cycle is broken in mod-list order.</summary>
    private static List<PluginEntry> Order(List<PluginEntry> plugins)
    {
        Dictionary<string, PluginEntry> byGuid = plugins.ToDictionary(p => p.Guid);
        var after = plugins.ToDictionary(p => p, _ => new HashSet<PluginEntry>());   // p -> must start before p
        foreach (PluginEntry p in plugins)
        {
            foreach (Dependency d in p.Dependencies)
                if (byGuid.TryGetValue(d.Guid, out PluginEntry dep) && dep != p)
                    after[p].Add(dep);
            foreach (string g in p.LoadBefore)
                if (byGuid.TryGetValue(g, out PluginEntry later) && later != p)
                    after[later].Add(p);
        }
        var result = new List<PluginEntry>();
        var done = new HashSet<PluginEntry>();
        var rest = plugins.OrderBy(p => p.Order).ToList();
        while (rest.Count > 0)
        {
            PluginEntry next = rest.FirstOrDefault(p => after[p].All(done.Contains));
            if (next == null)
            {
                next = rest[0];
                next.Warnings.Add("Its start order is circular with: " +
                                  string.Join(", ", after[next].Where(x => !done.Contains(x)).Select(x => x.Display)) + ". Started in mod-list order.");
            }
            result.Add(next);
            done.Add(next);
            rest.Remove(next);
        }
        return result;
    }

    private static string StripExe(string n) => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n;
}
