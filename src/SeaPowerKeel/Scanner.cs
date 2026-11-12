using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace SeaPowerKeel;

/// <summary>
/// Finds the plugins in the mod folders by reading the DLLs' metadata (Mono.Cecil) without loading them: nothing of a
/// mod runs, and no DLL gets locked, before Keel has decided to start it.
/// </summary>
internal static class Scanner
{
    public const string ManifestFile = "keel.ini";

    // Never plugins of their own: Keel itself and Anchor Chain's two loader DLLs (which Keel only uses as a library).
    // Only these exact names: a mod called AnchorChainSomething.dll is a mod.
    private static string ExcludedReason(string file)
    {
        string name = Path.GetFileName(file);
        if (name.StartsWith("SeaPowerKeel", StringComparison.OrdinalIgnoreCase))
            return "Keel itself.";
        if (name.Equals("AnchorChain.dll", StringComparison.OrdinalIgnoreCase))
            return "Anchor Chain's own library: Keel uses it as a library for Anchor Chain mods and never as a plugin.";
        if (name.Equals("AnchorChain.Preloader.dll", StringComparison.OrdinalIgnoreCase))
            return "Anchor Chain's preloader (the BepInEx stub that starts Anchor Chain): not a mod.";
        return null;
    }

    /// <summary>Scans without a list of skipped DLLs (the offline tools).</summary>
    public static List<PluginEntry> Scan(IList<ModFolder> folders, Action<string> warn, params string[] searchDirs) =>
        Scan(folders, warn, null, searchDirs);

    /// <param name="searchDirs">Where Cecil looks for assemblies a DLL's attributes refer to (an enum argument such as
    /// BepInDependency's flags needs BepInEx.dll): the game's Managed folder and BepInEx\core. The mod folders are added.</param>
    /// <param name="skipped">Receives every DLL left out on purpose, with the reason (may be null).</param>
    public static List<PluginEntry> Scan(IList<ModFolder> folders, Action<string> warn, List<SkippedDll> skipped, params string[] searchDirs)
    {
        var all = new List<PluginEntry>();
        int order = 0;
        using var resolver = new DefaultAssemblyResolver();
        foreach (string d in searchDirs.Concat(folders.Select(f => f.Path)).Where(Directory.Exists))
            resolver.AddSearchDirectory(d);
        foreach (ModFolder folder in folders)
        {
            Manifest manifest = Manifest.Read(folder.Path, warn);
            bool folderEarly = EarlyStart.FolderAsks(folder.Path);
            List<string> dlls;
            try
            {
                dlls = Directory.GetFiles(folder.Path, "*.dll", SearchOption.AllDirectories)
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                warn($"Cannot read mod folder {folder.Label}: {ex.Message}");
                continue;
            }
            foreach (string dll in dlls)
            {
                string why = ExcludedReason(dll);
                if (why != null)
                {
                    skipped?.Add(new SkippedDll { Path = dll, Folder = folder, Reason = why });
                    continue;
                }
                List<PluginEntry> found = ScanDll(dll, folder, warn, resolver);
                // Some mods carry both entries in one DLL: the Anchor Chain one for the Workshop, the BepInEx one for players
                // who copy the DLL into BepInEx\plugins. From a mod folder only the Anchor Chain entry is meant to run
                // (the one Anchor Chain uses, the one the author tested); starting both would run the mod twice.
                // A DLL with both is by design (the BepInEx entry is the author's backup): the backup is left out quietly, no row
                // and no warning (rgreat 2026-11-12); its GUID still counts for dependencies, through the Anchor Chain entry.
                PluginEntry acEntry = found.FirstOrDefault(p => p.Kind == PluginKind.AnchorChain && p.Exported);
                if (acEntry != null)
                {
                    foreach (PluginEntry p in found.Where(p => p.Kind == PluginKind.BepInEx).ToList())
                    {
                        if (p.Guid != acEntry.Guid)
                            acEntry.AlsoKnownAs.Add(p.Guid);
                        found.Remove(p);
                    }
                }
                string offBuild = manifest?.OtherBuildOnly(dll);
                foreach (PluginEntry p in found)
                {
                    p.Order = order++;
                    p.StartEarly |= folderEarly;
                    if (offBuild != null && p.State == PluginState.Pending)
                        p.Stop(PluginState.Skipped, $"{offBuild} build: this game has the {GameState.BuildName} (keel.ini)");
                    all.Add(p);
                }
            }
        }
        return all;
    }

    private static List<PluginEntry> ScanDll(string dll, ModFolder folder, Action<string> warn, IAssemblyResolver resolver)
    {
        var result = new List<PluginEntry>();
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(dll));
            using ModuleDefinition module = ModuleDefinition.ReadModule(stream, new ReaderParameters { ReadSymbols = false, AssemblyResolver = resolver });
            bool dllEarly = EarlyStart.AssemblyAsks(module.Assembly);
            foreach (TypeDefinition type in module.GetTypes())
            {
                if (!type.IsClass || type.IsAbstract || !type.HasCustomAttributes)
                    continue;
                PluginEntry entry = null;
                try
                {
                    CustomAttribute bep = Attr(type, "BepInEx.BepInPlugin");
                    CustomAttribute ac = Attr(type, "AnchorChain.ACPlugin");
                    if (bep != null && Inherits(type, "BepInEx.BaseUnityPlugin"))
                        entry = FromBepInEx(type, bep);
                    else if (ac != null && Implements(type, "AnchorChain.IAnchorChainMod"))
                    {
                        entry = FromAnchorChain(type, ac);
                        // Anchor Chain looks at the DLL's exported (public) types only; a plugin class that is not public never runs there.
                        if (!IsExported(type))
                        {
                            entry.Exported = false;
                            entry.Stop(PluginState.Skipped, "The plugin class is not public: Anchor Chain only looks at public classes and would not start it, so neither does Keel.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // one unreadable type must not hide the DLL's other plugins
                    warn($"Cannot read the plugin attributes of {type.FullName} in {Path.GetFileName(dll)} ({folder.Label}): {ex.Message}");
                }
                if (entry == null)
                    continue;
                entry.Dll = dll;
                entry.AssemblyName = module.Assembly.Name.Name;
                entry.TypeName = type.FullName.Replace('/', '+');
                entry.Folder = folder;
                entry.StartEarly = dllEarly;
                result.Add(entry);
            }
        }
        catch (BadImageFormatException)
        {
            // a native DLL next to the mod (audio, codecs ...): not ours to look at
        }
        catch (Exception ex)
        {
            warn($"Cannot read {Path.GetFileName(dll)} in {folder.Label}: {ex.Message}");
        }
        return result;
    }

    private static PluginEntry FromBepInEx(TypeDefinition type, CustomAttribute plugin)
    {
        var e = new PluginEntry
        {
            Kind = PluginKind.BepInEx,
            Guid = Str(plugin, 0),
            Name = Str(plugin, 1),
            Version = ParseVersion(Str(plugin, 2)) ?? new Version(1, 0),
        };
        foreach (CustomAttribute a in Attrs(type, "BepInEx.BepInDependency"))
        {
            var d = new Dependency { Guid = Str(a, 0) };
            if (a.ConstructorArguments.Count > 1)
            {
                object second = a.ConstructorArguments[1].Value;
                if (second is string min)
                    d.Min = ParseVersion(min);
                else if (second is int flags)
                    d.Hard = (flags & 1) != 0;   // DependencyFlags: 1 hard, 2 soft
            }
            e.Dependencies.Add(d);
        }
        foreach (CustomAttribute a in Attrs(type, "BepInEx.BepInIncompatibility"))
            e.Incompatible.Add(Str(a, 0));
        foreach (CustomAttribute a in Attrs(type, "BepInEx.BepInProcess"))
            e.Processes.Add(Str(a, 0));
        return e;
    }

    private static PluginEntry FromAnchorChain(TypeDefinition type, CustomAttribute plugin)
    {
        var e = new PluginEntry
        {
            Kind = PluginKind.AnchorChain,
            Guid = Str(plugin, 0),
            Name = Str(plugin, 1),
            Version = ParseVersion(Str(plugin, 2)) ?? new Version(1, 0),
        };
        // "before"/"after" only order the start; Keel does not refuse a mod because a plugin it wants to run around is absent.
        e.LoadBefore.AddRange(StrArray(plugin, 3));
        foreach (string after in StrArray(plugin, 4))
            e.Dependencies.Add(new Dependency { Guid = after, Hard = false });
        foreach (CustomAttribute a in Attrs(type, "AnchorChain.ACDependency"))
            e.Dependencies.Add(new Dependency { Guid = Str(a, 0), Min = ParseVersion(Str(a, 1)), Max = ParseVersion(Str(a, 2)) });
        foreach (CustomAttribute a in Attrs(type, "AnchorChain.ACIncompatibility"))
            e.Incompatible.Add(Str(a, 0));
        CustomAttribute config = Attr(type, "AnchorChain.ACConfig");
        e.UsesAcConfig = config != null;
        if (config != null && config.ConstructorArguments.Count > 0 && config.ConstructorArguments[0].Value is bool required)
            e.AcConfigRequired = required;
        return e;
    }

    /// <summary>What <c>Assembly.GetExportedTypes</c> returns: a public type, or a public type nested in exported types only.</summary>
    internal static bool IsExported(TypeDefinition t)
    {
        for (; t != null; t = t.DeclaringType)
        {
            if (t.DeclaringType == null)
                return t.IsPublic;
            if (!t.IsNestedPublic)
                return false;
        }
        return true;
    }

    private static CustomAttribute Attr(TypeDefinition t, string fullName) => Attrs(t, fullName).FirstOrDefault();

    private static IEnumerable<CustomAttribute> Attrs(TypeDefinition t, string fullName) =>
        t.CustomAttributes.Where(a => a.AttributeType.FullName == fullName);

    private static string Str(CustomAttribute a, int i) =>
        i < a.ConstructorArguments.Count ? a.ConstructorArguments[i].Value as string : null;

    private static IEnumerable<string> StrArray(CustomAttribute a, int i)
    {
        if (i < a.ConstructorArguments.Count && a.ConstructorArguments[i].Value is CustomAttributeArgument[] items)
            return items.Select(x => x.Value as string).Where(s => !string.IsNullOrEmpty(s));
        return Enumerable.Empty<string>();
    }

    // Walks base types inside the mod's own module; stops at the first one from another assembly.
    private static bool Inherits(TypeDefinition type, string baseName)
    {
        TypeReference b = type.BaseType;
        for (int depth = 0; b != null && depth < 16; depth++)
        {
            if (b.FullName == baseName)
                return true;
            if (b.Scope != type.Module && b.Scope != type.Module.Assembly?.Name)
                return false;
            TypeDefinition def = b.Resolve();
            if (def == null)
                return false;
            b = def.BaseType;
        }
        return false;
    }

    private static bool Implements(TypeDefinition type, string interfaceName)
    {
        for (TypeDefinition t = type; t != null;)
        {
            if (t.Interfaces.Any(i => i.InterfaceType.FullName == interfaceName))
                return true;
            TypeReference b = t.BaseType;
            if (b == null || b.Scope != type.Module && b.Scope != type.Module.Assembly?.Name)
                return false;
            t = b.Resolve();
        }
        return false;
    }

    public static Version ParseVersion(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        string clean = new string(s.Trim().TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (clean.Length == 0)
            return null;
        if (!clean.Contains('.'))
            clean += ".0";
        return Version.TryParse(clean, out Version v) ? v : null;
    }
}

/// <summary>
/// <c>keel.ini</c> in a mod folder (optional): which DLLs are for which game map. Without it every DLL is scanned and each
/// mod decides itself which build runs.
/// <code>
/// [Keel]
/// globe = MyMod.dll        ; only on the 3D globe build
/// flat  = MyModFlat.dll    ; only on the flat map (0.8.7)
/// </code>
/// </summary>
internal sealed class Manifest
{
    private readonly HashSet<string> globe = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> flat = new(StringComparer.OrdinalIgnoreCase);
    private string folder;

    public static Manifest Read(string folder, Action<string> warn)
    {
        string path = Path.Combine(folder, Scanner.ManifestFile);
        if (!File.Exists(path))
            return null;
        var m = new Manifest { folder = folder };
        bool inSection = false;
        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Split(';', '#')[0].Trim();
                if (line.StartsWith("["))
                {
                    inSection = line.Equals("[Keel]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (!inSection || eq < 0)
                    continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                IEnumerable<string> files = line.Substring(eq + 1).Split(',').Select(f => f.Trim().Replace('/', '\\')).Where(f => f.Length > 0);
                if (key == "globe")
                    m.globe.UnionWith(files);
                else if (key == "flat")
                    m.flat.UnionWith(files);
            }
        }
        catch (Exception ex)
        {
            warn($"Cannot read {path}: {ex.Message}");
            return null;
        }
        return m;
    }

    /// <summary>"Flat map" / "3D globe" when the DLL is listed only for the other map, else null.</summary>
    public string OtherBuildOnly(string dll)
    {
        string rel = dll.Substring(folder.Length).TrimStart('\\', '/').Replace('/', '\\');
        bool forGlobe = globe.Contains(rel) || globe.Contains(Path.GetFileName(dll));
        bool forFlat = flat.Contains(rel) || flat.Contains(Path.GetFileName(dll));
        if (GameState.Globe && forFlat && !forGlobe)
            return "Flat map";
        if (!GameState.Globe && forGlobe && !forFlat)
            return "3D globe";
        return null;
    }
}
