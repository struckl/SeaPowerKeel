using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Logging;
using Mono.Cecil;

namespace SeaPowerKeel;

internal static class StartupPreparation
{
    internal static bool IsPreparationDll(string file)
    {
        string name = Path.GetFileName(file);
        return name.EndsWith(".Preloader.dll", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("AnchorChain.Preloader.dll", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("SeaPowerKeel", StringComparison.OrdinalIgnoreCase);
    }

    internal static void Queue(string version, string streamingAssets, string workshop, string settings, ManualLogSource log) =>
        _ = Task.Run(() =>
        {
            string[] enabled = ModFolders.FromDisk(streamingAssets, workshop, settings)
                .Where(folder => folder.Enabled).Select(folder => folder.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string folder in enabled)
            {
                try
                {
                    if (InfoVersion.MismatchInFolder(folder, version) != null) continue;
                    foreach (string dll in Candidates(folder))
                        RunFile(dll, streamingAssets, enabled, Loading.LoadingWindow.ReportPreparation, log);
                }
                catch (Exception error)
                {
                    log.LogWarning($"Cannot find startup preparation DLLs in {folder}: {error.Message}");
                }
            }
        });

    internal static IEnumerable<string> Candidates(string folder) =>
        Directory.EnumerateFiles(folder, "*.Preloader.dll", SearchOption.AllDirectories)
            .Where(IsPreparationDll).OrderBy(file => file, StringComparer.OrdinalIgnoreCase);

    internal static void RunFile(string dll, string streamingAssets, string[] enabled, Action<string> report, ManualLogSource log)
    {
        try
        {
            string[] types;
            using (var metadata = AssemblyDefinition.ReadAssembly(dll))
            {
                if (metadata.MainModule.AssemblyReferences.Any(reference =>
                    reference.Name.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) ||
                    reference.Name.StartsWith("AnchorChain", StringComparison.OrdinalIgnoreCase) ||
                    reference.Name is "Seapower-Scripts" or "Noesis.NoesisGUI" or "BepInEx" or "0Harmony" or "SeaPowerKeel"))
                    throw new InvalidOperationException("The preparation assembly must be independent of the game and mod loaders.");
                types = metadata.MainModule.Types.Where(type => type.IsPublic && !type.HasGenericParameters
                    && type.Methods.Any(method => method.Name == "Prepare" && method.IsPublic && method.IsStatic && !method.HasGenericParameters
                        && method.ReturnType.FullName == "System.Void" && method.Parameters.Select(parameter => parameter.ParameterType.FullName)
                            .SequenceEqual(new[] { "System.String", "System.String[]", "System.Action`1<System.String>" })))
                    .Select(type => type.FullName).ToArray();
            }
            if (types.Length != 1) throw new InvalidOperationException("Expected one public static Prepare callback.");
            var assembly = Assembly.LoadFrom(dll);
            if (!Path.GetFullPath(assembly.Location).Equals(Path.GetFullPath(dll), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Assembly name already in use by {assembly.Location}.");
            var type = assembly.GetType(types[0], throwOnError: true);
            var method = type.GetMethod("Prepare", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly, null,
                new[] { typeof(string), typeof(string[]), typeof(Action<string>) }, null);
            log.LogInfo($"Preparing startup data with {Path.GetFileName(dll)}.");
            method.Invoke(null, new object[] { streamingAssets, (string[])enabled.Clone(), report });
        }
        catch (Exception error)
        {
            log.LogWarning($"Startup preparation failed for {dll}: {error.GetBaseException().Message}");
        }
        finally { report(null); }
    }
}
