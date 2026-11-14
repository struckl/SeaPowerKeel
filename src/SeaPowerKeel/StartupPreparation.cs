using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Logging;
using Mono.Cecil;

namespace SeaPowerKeel;

internal static class StartupPreparation
{
    internal static void Queue(string version, string streamingAssets, string workshop, string settings, ManualLogSource log) =>
        _ = Task.Run(() =>
        {
            string[] enabled = ModFolders.FromDisk(streamingAssets, workshop, settings)
                .Where(folder => folder.Enabled).Select(folder => folder.Path).Distinct().ToArray();
            foreach (string folder in enabled)
            {
                try
                {
                    string ini = Path.Combine(folder, "_info.ini");
                    if (!File.Exists(ini)) continue;
                    string[] lines = File.ReadAllLines(ini);
                    if (Hook(lines, version) is not { } hook) continue;
                    string dll = Path.Combine(folder, hook.Dll);
                    using (var metadata = AssemblyDefinition.ReadAssembly(dll))
                    {
                        if (metadata.MainModule.AssemblyReferences.Any(reference =>
                            reference.Name.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) ||
                            reference.Name.StartsWith("AnchorChain", StringComparison.OrdinalIgnoreCase) ||
                            reference.Name is "Seapower-Scripts" or "Noesis.NoesisGUI" or "BepInEx" or "0Harmony"))
                            throw new InvalidOperationException("The preparation assembly must be independent of the game and mod loaders.");
                    }
                    var type = Assembly.LoadFrom(dll).GetType(hook.Type, throwOnError: true);
                    var method = type.GetMethod("Prepare", BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(string), typeof(string[]), typeof(Action<string>) }, null);
                    if (method == null || method.ReturnType != typeof(void))
                        throw new MissingMethodException(type.FullName, "Prepare");
                    log.LogInfo($"Preparing startup data for {Path.GetFileName(folder)}.");
                    method.Invoke(null, new object[] { streamingAssets, (string[])enabled.Clone(),
                        (Action<string>)Loading.LoadingWindow.ReportPreparation });
                }
                catch (Exception error)
                {
                    log.LogWarning($"Startup preparation failed for {Path.GetFileName(folder)}: {error.GetBaseException().Message}");
                }
                finally { Loading.LoadingWindow.ReportPreparation(null); }
            }
        });

    internal static (string Dll, string Type)? Hook(string[] lines, string version)
    {
        string section = "", exact = "", prepare = "";
        foreach (string raw in lines)
        {
            string line = raw.Split(';', '#')[0].Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                section = line;
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 1) continue;
            string name = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();
            if (section.Equals("[Compatibility]", StringComparison.OrdinalIgnoreCase) &&
                name.Equals("ExactVersion", StringComparison.OrdinalIgnoreCase)) exact = value;
            if (section.Equals("[Keel]", StringComparison.OrdinalIgnoreCase) &&
                name.Equals("PrepareStartup", StringComparison.OrdinalIgnoreCase)) prepare = value;
        }
        if (!exact.Equals(version, StringComparison.Ordinal) || !EarlyStart.InInfoIni(string.Join("\n", lines))) return null;
        string[] parts = prepare.Split('|').Select(part => part.Trim()).ToArray();
        if (parts.Length != 2 || parts[1].Length == 0 || !parts[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            parts[0].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || parts[0].IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return null;
        return (parts[0], parts[1]);
    }
}
