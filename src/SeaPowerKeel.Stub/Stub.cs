using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace SeaPowerKeel.Stub;

/// <summary>
/// The only part of R.A.T.S. Keel that goes into <c>BepInEx\plugins</c>. It finds <c>SeaPowerKeel.dll</c> in the game's
/// enabled mod folders (newest version wins) and starts it. It also finds startup preparation DLLs during the splash.
/// </summary>
[BepInPlugin(Guid, "R.A.T.S. Keel stub", "1.1.0")]
public class Stub : BaseUnityPlugin
{
    public const string Guid = "dev.sewerlabs.seapowerkeel.stub";
    private const string LoaderFile = "SeaPowerKeel.dll";
    private const string LoaderType = "SeaPowerKeel.Keel";

    private void Awake()
    {
        try
        {
            List<ModFolder> folders = ModFolders.FromDisk();
            var candidates = new List<(string path, Version version, ModFolder folder)>();
            foreach (ModFolder folder in folders.Where(f => f.Enabled))
            {
                foreach (string path in ModFolders.FindFile(folder.Path, LoaderFile))
                {
                    try
                    {
                        candidates.Add((path, AssemblyName.GetAssemblyName(path).Version, folder));
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"Not a usable {LoaderFile}: {path} ({ex.Message})");
                    }
                }
            }
            if (candidates.Count == 0)
            {
                bool off = folders.Any(f => !f.Enabled && ModFolders.FindFile(f.Path, LoaderFile).Any());
                Logger.LogWarning(off
                    ? "R.A.T.S. Keel is switched off in the game's mod list: no Keel mod loads. Switch it on in Mods and restart."
                    : "R.A.T.S. Keel is not installed (subscribe to it on the Workshop): no Keel mod loads.");
                return;
            }
            // Newest first; on a tie the one higher up in the game's mod list.
            (string path, Version version, ModFolder folder) pick =
                candidates.OrderByDescending(c => c.version).ThenBy(c => c.folder.Order).First();
            Logger.LogInfo($"Starting R.A.T.S. Keel {pick.version} from {pick.folder.Label} ({pick.path})");
            Assembly loader = Assembly.LoadFrom(pick.path);
            var entry = loader.GetType(LoaderType, throwOnError: true);
            string version = Info.Metadata.Version.ToString();
            var withPreparation = entry.GetMethod("BootWithPreloader", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(string), typeof(Action<Action<string>>) }, null);
            if (withPreparation != null)
            {
                void Prepare(Action<string> report) => StartupPreparation.Queue(Application.version,
                    Application.streamingAssetsPath, ModFolders.WorkshopRoot, ModFolders.UserSettings, report, Logger);
                withPreparation.Invoke(null, new object[] { version, pick.path, (Action<Action<string>>)Prepare });
                return;
            }
            MethodInfo boot = entry.GetMethod("Boot", BindingFlags.Public | BindingFlags.Static);
            if (boot == null)
            {
                Logger.LogError($"{pick.path} has no {LoaderType}.Boot: not a Keel loader this stub can start.");
                return;
            }
            boot.Invoke(null, new object[] { version, pick.path });
        }
        catch (Exception ex)
        {
            Logger.LogError($"Could not start R.A.T.S. Keel: {(ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex)}");
        }
    }
}
