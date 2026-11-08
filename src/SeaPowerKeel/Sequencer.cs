using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerKeel;

/// <summary>
/// The order of work once the plan is made, kept apart from Unity so it can be tested offline:
/// <list type="number">
/// <item>every plugin is loaded, every Anchor Chain mod created (<see cref="Prepare"/>), one per step;</item>
/// <item>whatever dropped out meanwhile takes its dependants with it (<see cref="Replan"/>);</item>
/// <item>Anchor Chain is looked for once more (<see cref="HandOver"/>): if it is running, its mods are its own;</item>
/// <item>the rest starts in plan order (<see cref="Start"/>), one per step, and Anchor Chain is looked for before each of its mods.</item>
/// </list>
/// The game gets a frame between steps (the caller yields for each element this returns).
/// </summary>
internal sealed class Sequencer
{
    public Action<PluginEntry> Prepare;
    public Action<PluginEntry> Start;
    public Func<List<PluginEntry>> Replan;

    /// <summary>Is Anchor Chain running now? If so it marks the pending Anchor Chain mods <see cref="PluginState.LeftToAnchorChain"/> and returns true.</summary>
    public Func<string, bool> HandOver = _ => false;

    public Action<PluginEntry> Prepared = _ => { };
    public Action<PluginEntry> Started = _ => { };

    public int StartedCount;

    public IEnumerable<PluginEntry> Run(List<PluginEntry> order)
    {
        foreach (PluginEntry p in order)
        {
            Prepare(p);
            Prepared(p);
            yield return p;
        }
        if (order.Any(p => p.State != PluginState.Pending))
            order = Replan();

        if (HandOver("right before Keel started the mods"))
            order = Replan();
        foreach (PluginEntry p in order)
        {
            if (p.Kind == PluginKind.AnchorChain)
                HandOver("while Keel was starting mods");
            if (p.State != PluginState.Pending)
                continue;
            Start(p);
            StartedCount++;
            Started(p);
            yield return p;
        }
    }
}
