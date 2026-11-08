using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SeaPowerKeel;

// SeaPowerKeel.OfflineCheck <Managed folder of a game build> <BepInEx core folder> <mod folder or folder of mod folders>...
// Scans the given mod folders the way Keel does in the game, checks every plugin DLL against the given game build and prints
// the plan and the report Keel would show (nothing is loaded or started). The game build is told by the Managed folder: the
// game type Keel uses to tell the maps apart is looked up there.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: SeaPowerKeel.OfflineCheck <Managed> <BepInEx core> <mod folder | folder of mod folders> ...");
            return 2;
        }
        string managed = args[0], core = args[1];
        // Let Type.GetType find the game's Seapower-Scripts in that Managed folder: that is how Keel tells the builds apart.
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string p = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        Console.WriteLine($"Game build: {GameState.BuildName} ({managed})");

        var folders = new List<ModFolder>();
        foreach (string root in args.Skip(2))
        {
            bool isMod = Directory.GetFiles(root, "*.dll", SearchOption.TopDirectoryOnly).Any() || File.Exists(Path.Combine(root, "_info.ini"));
            IEnumerable<string> dirs = isMod ? new[] { root } : Directory.GetDirectories(root);
            foreach (string d in dirs)
            {
                string name = Path.GetFileName(d);
                folders.Add(new ModFolder { Path = d, Name = name, SteamId = ulong.TryParse(name, out ulong id) ? id : 0, Enabled = true, Order = folders.Count });
            }
        }

        var notes = new List<string>();
        List<PluginEntry> all = Scanner.Scan(folders, notes.Add, managed, core);
        using (var pre = new Preflight(managed, core))
        {
            foreach (IGrouping<string, PluginEntry> dll in all.Where(p => p.State == PluginState.Pending).GroupBy(p => p.Dll))
            {
                Preflight.Result r = pre.Check(dll.Key);
                foreach (PluginEntry p in dll)
                {
                    if (r.ClassLoad.Count > 0)
                        p.Stop(PluginState.NotStarted, $"Made for another game version: this build has no {Report.Short(r.ClassLoad)}.");
                    else
                    {
                        if (r.Uses.Count > 0)
                            p.Warnings.Add($"Uses {r.Uses.Count} thing(s) this game build lacks, e.g. {Report.Short(r.Uses)}.");
                        if (r.Lookups.Count > 0)
                            p.Warnings.Add($"Looks up {r.Lookups.Count} type(s)/method(s) by name that this game build lacks: {string.Join("; ", r.Lookups)}.");
                    }
                }
            }
            Console.WriteLine($"Type index for by-name lookups: {(pre.IndexMs > 0 ? pre.IndexMs + " ms" : "not needed")}");
        }
        List<PluginEntry> order = Planner.Plan(all, new HashSet<string>(), new HashSet<string>(), "Sea Power");
        foreach (PluginEntry p in order)
            p.State = PluginState.Running;   // would start; nothing is run here
        Console.WriteLine("Start order: " + string.Join(" > ", order.Select(p => p.Display)));
        Console.WriteLine();
        Console.WriteLine(Report.Text(all, "Offline check (nothing started; RUNNING = would start)", notes));
        return 0;
    }
}
