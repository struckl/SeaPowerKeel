using System;
using System.IO;

namespace SeaPowerKeel;

internal static class PreparationDll
{
    internal static bool Matches(string file)
    {
        string name = Path.GetFileName(file);
        return name.EndsWith(".Preloader.dll", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("AnchorChain.Preloader.dll", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("SeaPowerKeel", StringComparison.OrdinalIgnoreCase);
    }
}
