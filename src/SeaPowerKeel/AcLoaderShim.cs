using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using TypeAttributes = Mono.Cecil.TypeAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;

namespace SeaPowerKeel;

/// <summary>
/// What a mod that touches Anchor Chain's <c>AnchorChainLoader</c> class needs when Keel, not Anchor Chain, runs the mods.
/// Euromod's Sonar Audio Loader (CustomAudioLoader.dll) calls <c>AnchorChainLoader.FindFile</c> to find audio and mapping
/// files that live in OTHER mods' folders. Two things are then missing:
/// <list type="bullet">
/// <item>The class implements <c>AnchorChain.Preloader.IPluginLoader</c>, which lives in <c>AnchorChain.Preloader.dll</c>, a
/// BepInEx plugin. Without that DLL (Keel's setup removes it) the class cannot be loaded at all: a <c>TypeLoadException</c> at
/// the first call. <see cref="BuildPreloaderStub"/> makes an assembly of that name with just that interface.</item>
/// <item><c>FindFile</c> searches a private static list of mod folders that only Anchor Chain's own loader fills.
/// <see cref="Prime"/> fills it (and <c>_configPath</c>) the way Anchor Chain's <c>Setup</c> does.</item>
/// </list>
/// </summary>
internal static class AcLoaderShim
{
    public const string PreloaderAssembly = "AnchorChain.Preloader";
    public const string LoaderType = "AnchorChain.AnchorChainLoader";

    /// <summary>An assembly named AnchorChain.Preloader (1.0.0.0, what Anchor Chain 1.1.0 references) with <c>AnchorChain.Preloader.IPluginLoader { void LoadPlugins(); }</c>.</summary>
    public static Assembly BuildPreloaderStub()
    {
        AssemblyDefinition asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(PreloaderAssembly, new Version(1, 0, 0, 0)), PreloaderAssembly, ModuleKind.Dll);
        ModuleDefinition module = asm.MainModule;
        var iface = new TypeDefinition("AnchorChain.Preloader", "IPluginLoader",
                                       TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.AutoClass | TypeAttributes.AnsiClass);
        iface.Methods.Add(new MethodDefinition("LoadPlugins",
                                               MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract | MethodAttributes.Virtual,
                                               module.TypeSystem.Void));
        module.Types.Add(iface);
        using var stream = new MemoryStream();
        asm.Write(stream);
        return Assembly.Load(stream.ToArray());
    }

    private static string Norm(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    /// <summary>
    /// Fills <c>AnchorChainLoader._allDirectories</c> (the folders <c>FindFile</c> searches, top level only) and <c>_configPath</c>.
    /// Returns null when done, else why it could not (the mods that call <c>FindFile</c> then find nothing).
    /// </summary>
    public static string Prime(Assembly anchorChain, IEnumerable<string> folders, string configDir)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        Type loader = anchorChain.GetType(LoaderType, throwOnError: false);
        if (loader == null)
            return $"{LoaderType} is not in {anchorChain.GetName().Name} {anchorChain.GetName().Version}";
        if (loader.GetField("_allDirectories", all)?.GetValue(null) is not HashSet<DirectoryInfo> set)
            return "AnchorChainLoader has no _allDirectories list (this Anchor Chain version differs)";
        void Add(string folder)
        {
            if (!set.Any(d => string.Equals(Norm(d.FullName), Norm(folder), StringComparison.OrdinalIgnoreCase)))
                set.Add(new DirectoryInfo(folder));
        }
        foreach (string folder in folders.Where(f => !string.IsNullOrEmpty(f)))
            Add(folder);
        if (!string.IsNullOrEmpty(configDir) && Directory.Exists(configDir))
            Add(configDir);
        FieldInfo config = loader.GetField("_configPath", all);
        if (config != null && config.FieldType == typeof(string) && !string.IsNullOrEmpty(configDir))
            config.SetValue(null, configDir);
        return null;
    }
}
