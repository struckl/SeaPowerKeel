using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using UnityEngine;

namespace SeaPowerKeel;

/// <summary>
/// Loads and starts plugins one at a time. A plugin that throws is marked and the next one starts: one broken mod never
/// stops the others or freezes the game. Errors a mod throws later in the session are counted against it too (matched by
/// the mod's namespaces in the stack trace), so the report shows which mod a red line in the log belongs to.
/// </summary>
internal sealed class Runner
{
    private readonly ManualLogSource log;
    private readonly GameObject host;
    private readonly Dictionary<string, Assembly> loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> libraryIndex = new(StringComparer.OrdinalIgnoreCase);  // assembly name -> path
    private readonly List<(string[] namespaces, PluginEntry plugin)> owners = new();
    private PluginEntry starting;
    private readonly List<string> startErrors = new();

    public string AnchorChainDll;

    /// <summary>Called for every exception Unity logs that Keel can pin on a mod: the mod, the message, the stack trace.</summary>
    public Action<PluginEntry, string, string> ModError;

    /// <summary>The mod whose code appears in this text (a message with a stack trace), or null.</summary>
    public string OwnerOf(string text) => Find(text)?.Display;

    /// <summary>
    /// The plugin a stack trace belongs to. A plugin's own class beats a namespace match (several plugins of one DLL share
    /// the namespace), and the topmost frame wins (a mod calling into another mod's code is not the one that threw).
    /// </summary>
    private PluginEntry Find(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        PluginEntry best = null;
        int bestAt = int.MaxValue;
        foreach ((string[] namespaces, PluginEntry plugin) in owners)
        {
            string type = plugin.TypeName.Replace('+', '/');
            foreach (string probe in new[] { plugin.TypeName + ".", plugin.TypeName + ":", type + ".", type + ":" })
            {
                int at = text.IndexOf(probe, StringComparison.Ordinal);
                if (at >= 0 && at < bestAt)
                {
                    best = plugin;
                    bestAt = at;
                }
            }
        }
        if (best != null)
            return best;
        foreach ((string[] namespaces, PluginEntry plugin) in owners)
        {
            foreach (string ns in namespaces)
            {
                int at = text.IndexOf(ns, StringComparison.Ordinal);
                if (at >= 0 && at < bestAt)
                {
                    best = plugin;
                    bestAt = at;
                }
            }
        }
        return best;
    }

    public Runner(ManualLogSource log, GameObject host, IEnumerable<ModFolder> folders)
    {
        this.log = log;
        this.host = host;
        // Libraries a mod ships (and Anchor Chain's DLL, for Anchor Chain mods): found by assembly name when a mod asks
        // for one, first in mod-list order.
        foreach (ModFolder f in folders)
        {
            try
            {
                foreach (string dll in Directory.GetFiles(f.Path, "*.dll", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(dll);
                    if (!libraryIndex.ContainsKey(name))
                        libraryIndex[name] = dll;
                }
            }
            catch (Exception)
            {
                // unreadable folder: the scanner already said so
            }
        }
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        Application.logMessageReceived += OnLog;
    }

    public bool HasLibrary(string assemblyName) => libraryIndex.ContainsKey(assemblyName);

    private Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        Assembly already = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
        if (already != null)
            return already;
        string path = name == "AnchorChain" && AnchorChainDll != null ? AnchorChainDll
                    : libraryIndex.TryGetValue(name, out string p) ? p : null;
        if (path == null && name == AcLoaderShim.PreloaderAssembly && AnchorChainDll != null)
            return PreloaderStub();   // Anchor Chain's DLL needs its preloader's interface to load AnchorChainLoader (see AcLoaderShim)
        if (path == null)
            return null;
        try
        {
            return Load(path);
        }
        catch (Exception ex)
        {
            log.LogWarning($"Could not load {path} for {args.Name}: {ex.Message}");
            return null;
        }
    }

    private Assembly preloaderStub;

    private Assembly PreloaderStub()
    {
        if (preloaderStub == null)
        {
            try
            {
                preloaderStub = AcLoaderShim.BuildPreloaderStub();
                keelLoaded.Add(preloaderStub);
                log.LogInfo("Anchor Chain's preloader is not installed: Keel provides the one interface of it that Anchor Chain's DLL needs (AnchorChain.Preloader.IPluginLoader).");
            }
            catch (Exception ex)
            {
                log.LogWarning($"Could not make the AnchorChain.Preloader stand-in: {ex.Message}");
            }
        }
        return preloaderStub;
    }

    /// <summary>
    /// Mods that call <c>AnchorChainLoader.FindFile</c> (Sonar Audio Loader does, for audio in other mods' folders) need its list
    /// of mod folders, which only Anchor Chain's own loader fills. Null when done, else why not.
    /// </summary>
    public string PrimeAnchorChainLoader(IEnumerable<string> folders, string configDir)
    {
        if (AnchorChainDll == null)
            return "Anchor Chain's DLL was not found";
        try
        {
            return AcLoaderShim.Prime(Load(AnchorChainDll), folders, configDir);
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex;
            return $"{inner.GetType().Name}: {inner.Message}";
        }
    }

    private readonly HashSet<Assembly> keelLoaded = new();
    private readonly HashSet<PluginEntry> ownerRegistered = new();

    private static string LocationOf(Assembly a)
    {
        try
        {
            return a.IsDynamic ? null : a.Location;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private Assembly Load(string path)
    {
        string full = Path.GetFullPath(path);
        if (!loaded.TryGetValue(full, out Assembly a))
        {
            // Already in the game (someone else loaded that very file): use that one, it is not ours.
            a = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => string.Equals(LocationOf(x), full, StringComparison.OrdinalIgnoreCase));
            if (a == null)
            {
                a = Assembly.LoadFrom(full);
                keelLoaded.Add(a);
            }
            loaded[full] = a;
        }
        return a;
    }

    // Everything that gets loaded while Keel loads or starts a plugin is Keel's doing (an AnchorChain.dll copy next to a mod,
    // found by the runtime's own probing, included): it must not make Keel think Anchor Chain is running.
    private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
    {
        if (starting != null)
            keelLoaded.Add(e.LoadedAssembly);
    }

    /// <summary>Did Keel itself load this assembly (as opposed to Anchor Chain's preloader, BepInEx or the game)?</summary>
    public bool OwnsAssembly(Assembly a) => keelLoaded.Contains(a);

    /// <summary>Is an Anchor Chain loader in the game that Keel did not bring in itself?</summary>
    public bool AnchorChainRunning() => GameState.AnchorChainRunning(OwnsAssembly);

    /// <summary>The name of the other loader that is running, or null.</summary>
    public string OtherLoader() => GameState.OtherLoader(OwnsAssembly);

    private void RegisterOwner(PluginEntry p, Assembly asm)
    {
        if (ownerRegistered.Add(p))
            owners.Add((Namespaces(asm), p));
    }

    /// <summary>
    /// First phase for every plugin: load its DLL and, for an Anchor Chain mod, create the instance (Anchor Chain does this
    /// for all mods before any of them starts, so a mod's start already sees every other mod's DLL loaded). Nothing of the
    /// mod's entry point runs. A mod that cannot be loaded or created, or has no <c>IAnchorChainMod.TriggerEntryPoint</c>,
    /// is marked <see cref="PluginState.Failed"/>.
    /// </summary>
    public void Prepare(PluginEntry p)
    {
        var watch = Stopwatch.StartNew();
        starting = p;
        startErrors.Clear();
        try
        {
            Assembly asm = Load(p.Dll);
            Type type = asm.GetType(p.TypeName, throwOnError: true);
            RegisterOwner(p, asm);
            if (p.Kind == PluginKind.AnchorChain)
            {
                if (AcEntry.FindEntryPoint(type) == null)
                {
                    p.Stop(PluginState.Failed, $"Entry point not found: {type.FullName} does not implement {AcEntry.InterfaceName}.{AcEntry.MethodName}() (the DLL's copy of Anchor Chain's interface differs?).");
                    log.LogError($"{p.Display} ({p.Guid}): {p.Reason}");
                    return;
                }
                p.Instance = Activator.CreateInstance(type);
                if (startErrors.Count > 0)
                    p.Stop(PluginState.Failed, "Threw while being created: " + startErrors[0]);
            }
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex;
            p.Stop(PluginState.Failed, Explain(inner));
            log.LogError($"{p.Display} ({p.Guid}) failed to load: {inner}");
        }
        finally
        {
            starting = null;
            p.StartMs += (float)watch.Elapsed.TotalMilliseconds;
        }
    }

    public void Start(PluginEntry p)
    {
        var watch = Stopwatch.StartNew();
        starting = p;
        startErrors.Clear();
        try
        {
            Assembly asm = Load(p.Dll);
            Type type = asm.GetType(p.TypeName, throwOnError: true);
            RegisterOwner(p, asm);
            if (p.Kind == PluginKind.BepInEx)
            {
                var plugin = (BaseUnityPlugin)host.AddComponent(type);
                p.Instance = plugin;
                if (plugin != null && plugin.Info != null && !Chainloader.PluginInfos.ContainsKey(p.Guid))
                    Chainloader.PluginInfos[p.Guid] = plugin.Info;   // so [BepInDependency] checks and other mods see it
            }
            else
            {
                p.Instance ??= Activator.CreateInstance(type);
                AcEntry.Invoke(p.Instance);   // through the interface: explicit implementations work; a missing entry point throws
            }
            if (startErrors.Count > 0)
                p.Stop(PluginState.Failed, "Threw while starting: " + startErrors[0]);
            else
                p.State = PluginState.Running;
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex;
            p.Stop(PluginState.Failed, inner is MissingMethodException && inner.Message.Contains(AcEntry.MethodName) ? "Entry point not found: " + inner.Message : Explain(inner));
            log.LogError($"{p.Display} ({p.Guid}) failed to start: {inner}");
        }
        finally
        {
            starting = null;
            p.StartMs += (float)watch.Elapsed.TotalMilliseconds;
        }
    }

    private static string Explain(Exception ex) => ex switch
    {
        MissingMethodException or MissingFieldException or TypeLoadException or FileNotFoundException =>
            $"Made for another game version or missing a library: {ex.Message}",
        ReflectionTypeLoadException r => "Could not load its types: " + (r.LoaderExceptions.FirstOrDefault()?.Message ?? r.Message),
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    // Unity logs exceptions from Awake/Start/Update instead of throwing them at us.
    private bool inLog;

    private void OnLog(string message, string stackTrace, LogType type)
    {
        if (type != LogType.Exception && type != LogType.Error || inLog)
            return;
        inLog = true;
        try
        {
            Attribute(message, stackTrace, type);
        }
        finally
        {
            inLog = false;
        }
    }

    private void Attribute(string message, string stackTrace, LogType type)
    {
        if (starting != null)
        {
            if (type == LogType.Exception)
            {
                startErrors.Add(FirstLine(message));
                ModError?.Invoke(starting, message, stackTrace);
            }
            return;
        }
        if (type != LogType.Exception || string.IsNullOrEmpty(stackTrace))
            return;
        PluginEntry plugin = Find(stackTrace) ?? GuardMissingMember(message, stackTrace);
        if (plugin == null)
            return;
        plugin.LaterErrors++;
        plugin.FirstLaterError ??= FirstLine(message);
        if (plugin.State == PluginState.Running)
            plugin.State = PluginState.RunningWithErrors;
        ModError?.Invoke(plugin, message, stackTrace);
    }

    private readonly HashSet<string> blamed = new();

    /// <summary>Remove the patches that throw a "not found" error (setting <c>RemoveBrokenPatches</c>); off = only name the mod.</summary>
    public static bool GuardPatches = true;

    /// <summary>
    /// The member a "Method not found" / "Field not found" message names, as <c>Namespace.Type::Member</c> (the form of
    /// <see cref="PluginEntry.MissingUses"/>), or null when the message is not of that kind.
    /// </summary>
    internal static string MissingKey(string message)
    {
        if (message == null || !(message.StartsWith("MissingMethodException") || message.StartsWith("MissingFieldException")))
            return null;
        int at = message.IndexOf(" not found: ", StringComparison.Ordinal);
        if (at < 0)
            return null;
        string sig = FirstLine(message.Substring(at + 12));          // e.g. List`1<string> SeaPower.FileManager.GetDirectories(string)
        int due = sig.IndexOf(" Due to:", StringComparison.Ordinal);   // Mono adds " Due to: Could not find field in class" (on the same line in the Unity log)
        if (due > 0)
            sig = sig.Substring(0, due);
        int paren = sig.IndexOf('(');
        string head = paren < 0 ? sig : sig.Substring(0, paren);
        int space = head.LastIndexOf(' ');
        if (space >= 0)
            head = head.Substring(space + 1);
        int dot = head.LastIndexOf('.');
        if (dot <= 0)
            return null;
        return head.Substring(0, dot).Replace('+', '/') + "::" + head.Substring(dot + 1);
    }

    /// <summary>The game methods a stack trace shows as patched ("SeaPower.GameMain.DMD&lt;SeaPower.GameMain::FixedUpdate&gt;"): type name and method name.</summary>
    internal static List<(string type, string method)> PatchedMethodsIn(string stackTrace)
    {
        var found = new List<(string, string)>();
        if (string.IsNullOrEmpty(stackTrace))
            return found;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(stackTrace, @"DMD<([^>\s]+?)::([^>\s]+?)>"))
            if (!found.Contains((m.Groups[1].Value, m.Groups[2].Value)))
                found.Add((m.Groups[1].Value, m.Groups[2].Value));
        return found;
    }

    /// <summary>
    /// A "Method not found" / "Field not found" error whose stack holds no mod (a Harmony patch rewrote a game method, so the
    /// trace shows only the game's own frames). Finds the mods whose DLL uses that member (from the preflight's list of what this
    /// build lacks); the one whose patch sits on the method the stack shows is the culprit, and Keel takes that patch off again
    /// so the error stops (a popup on every frame otherwise). The rest of the mod keeps running. When the patch cannot be found,
    /// the candidates are only named.
    /// </summary>
    private PluginEntry GuardMissingMember(string message, string stackTrace)
    {
        string key = MissingKey(message);
        if (key == null)
            return null;
        List<PluginEntry> candidates = owners.Select(o => o.plugin)
            .Where(p => p.MissingUses.Any(u => u == key || u.StartsWith(key + "(", StringComparison.Ordinal))).ToList();
        if (candidates.Count == 0)
            return null;
        string what = FirstLine(message.Substring(message.IndexOf(" not found: ", StringComparison.Ordinal) + 12));
        int dueAt = what.IndexOf(" Due to:", StringComparison.Ordinal);
        if (dueAt > 0)
            what = what.Substring(0, dueAt);
        var culprits = new List<PluginEntry>();
        var where = new Dictionary<PluginEntry, (string method, int removed)>();
        if (GuardPatches)
        {
            try
            {
                foreach ((string typeName, string methodName) in PatchedMethodsIn(stackTrace))
                {
                    Type type = HarmonyLib.AccessTools.TypeByName(typeName);
                    if (type == null)
                        continue;
                    foreach (MethodBase original in HarmonyLib.AccessTools.GetDeclaredMethods(type).Where(m => m.Name == methodName).Cast<MethodBase>().ToList())
                    {
                        HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(original);
                        if (info == null)
                            continue;
                        var patches = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).ToList();
                        foreach (HarmonyLib.Patch patch in patches)
                        {
                            string asm = patch.PatchMethod?.DeclaringType?.Assembly.GetName().Name;
                            PluginEntry owner = candidates.FirstOrDefault(c => c.AssemblyName == asm);
                            if (owner == null)
                                continue;
                            new HarmonyLib.Harmony("dev.sewerlabs.seapowerkeel.guard").Unpatch(original, patch.PatchMethod);
                            if (!culprits.Contains(owner))
                                culprits.Add(owner);
                            where.TryGetValue(owner, out var w);
                            where[owner] = (typeName + "." + methodName, w.removed + 1);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"Keel could not take a failing patch off: {ex.GetType().Name}: {ex.Message}");
            }
        }
        foreach (PluginEntry p in culprits)
        {
            if (!blamed.Add(p.Guid + "|" + key + "|guard"))
                continue;
            (string method, int removed) = where[p];
            string why = $"The game threw 'not found: {what}'. {p.Display} calls it and this game build does not have it: the mod was made for another game version. " +
                         $"Its patch on {method} caused the error; Keel took {removed} patch(es) of the mod off that method so the game can go on. The rest of the mod keeps running (see its warnings).";
            p.Warnings.Add(why);
            log.LogWarning(why);
        }
        if (culprits.Count > 0)
            return culprits[0];
        if (candidates.Count == 1)
        {
            PluginEntry p = candidates[0];
            if (blamed.Add(p.Guid + "|" + key))
            {
                string why = $"The game threw 'not found: {what}'. {p.Display} calls it and this game build does not have it: the mod was made for another game version (see its warnings in the report).";
                p.Warnings.Add(why);
                log.LogWarning(why);
            }
            return p;
        }
        if (blamed.Add("many|" + key))
        {
            string names = string.Join(", ", candidates.Select(c => c.Display));
            string why = $"The game threw 'not found: {what}'. These mods call it and this game build does not have it: {names}. One of them was made for another game version " +
                         "(Keel could not tell which one from the stack; switching one off at a time finds it).";
            foreach (PluginEntry c in candidates)
                c.Warnings.Add(why);
            log.LogWarning(why);
        }
        return null;
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOf('\n');
        string line = nl < 0 ? s : s.Substring(0, nl);
        return line.Length > 240 ? line.Substring(0, 240) + "…" : line.Trim();
    }

    // Namespaces a mod may put types into that are not its own: matching them would blame it for the game's errors.
    internal static readonly HashSet<string> SharedNamespaces = new()
    {
        "SeaPower", "SeapowerUI", "Seapower", "UnityEngine", "Unity", "System", "BepInEx", "HarmonyLib", "MonoMod", "Microsoft", "AnchorChain",
    };

    // "MyMod." for every top-level namespace of the mod (types without one count by their own name).
    private static string[] Namespaces(Assembly asm)
    {
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException r)
        {
            types = r.Types.Where(t => t != null).ToArray();
        }
        return types.Select(t => string.IsNullOrEmpty(t.Namespace) ? t.Name : t.Namespace.Split('.')[0])
                    .Where(n => n.Length > 2 && !n.StartsWith("<") && !SharedNamespaces.Contains(n))
                    .Distinct().Select(n => n + ".").ToArray();
    }
}
