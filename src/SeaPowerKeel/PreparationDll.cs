using System;
using System.IO;

namespace SeaPowerKeel;

internal static class PreparationDll
{
    // The stub and loader are separate assemblies. The callback is optional during Boot.
    internal const string BootCallbackKey = "dev.sewerlabs.seapowerkeel.prepare-startup";

    internal static void DuringBoot(Action boot, Action<Action<string>> prepare)
    {
        var domain = AppDomain.CurrentDomain;
        domain.SetData(BootCallbackKey, prepare);
        try { boot(); }
        finally { domain.SetData(BootCallbackKey, null); }
    }

    internal static void RunIfAvailable(Action<string> report)
    {
        if (AppDomain.CurrentDomain.GetData(BootCallbackKey) is Action<Action<string>> prepare)
            prepare(report);
    }

    internal static bool Matches(string file)
    {
        string name = Path.GetFileName(file);
        return name.EndsWith(".Preloader.dll", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("AnchorChain.Preloader.dll", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("SeaPowerKeel", StringComparison.OrdinalIgnoreCase);
    }
}
