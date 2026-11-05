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

    /// <summary>The mod whose namespaces appear in this text (a message with a stack trace), or null.</summary>
    public string OwnerOf(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        foreach ((string[] namespaces, PluginEntry plugin) in owners)
            if (namespaces.Any(ns => text.Contains(ns)))
                return plugin.Display;
        return null;
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

    private Assembly Load(string path)
    {
        string full = Path.GetFullPath(path);
        if (!loaded.TryGetValue(full, out Assembly a))
            loaded[full] = a = Assembly.LoadFrom(full);
        return a;
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
            owners.Add((Namespaces(asm), p));
            if (p.Kind == PluginKind.BepInEx)
            {
                var plugin = (BaseUnityPlugin)host.AddComponent(type);
                p.Instance = plugin;
                if (plugin != null && plugin.Info != null && !Chainloader.PluginInfos.ContainsKey(p.Guid))
                    Chainloader.PluginInfos[p.Guid] = plugin.Info;   // so [BepInDependency] checks and other mods see it
            }
            else
            {
                object mod = Activator.CreateInstance(type);
                p.Instance = mod;
                type.GetMethod("TriggerEntryPoint", BindingFlags.Public | BindingFlags.Instance)?.Invoke(mod, null);
            }
            if (startErrors.Count > 0)
                p.Stop(PluginState.Failed, "Threw while starting: " + startErrors[0]);
            else
                p.State = PluginState.Running;
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex;
            p.Stop(PluginState.Failed, Explain(inner));
            log.LogError($"{p.Display} ({p.Guid}) failed to start: {inner}");
        }
        finally
        {
            starting = null;
            p.StartMs = (float)watch.Elapsed.TotalMilliseconds;
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
        foreach ((string[] namespaces, PluginEntry plugin) in owners)
        {
            if (!namespaces.Any(ns => stackTrace.Contains(ns)))
                continue;
            plugin.LaterErrors++;
            plugin.FirstLaterError ??= FirstLine(message);
            if (plugin.State == PluginState.Running)
                plugin.State = PluginState.RunningWithErrors;
            ModError?.Invoke(plugin, message, stackTrace);
            break;
        }
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOf('\n');
        string line = nl < 0 ? s : s.Substring(0, nl);
        return line.Length > 240 ? line.Substring(0, 240) + "…" : line.Trim();
    }

    // Namespaces a mod may put types into that are not its own: matching them would blame it for the game's errors.
    private static readonly HashSet<string> SharedNamespaces = new()
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
