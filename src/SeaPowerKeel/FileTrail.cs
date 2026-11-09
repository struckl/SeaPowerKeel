using System;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace SeaPowerKeel;

/// <summary>
/// The last files the game opened from mod folders. Every file goes through <c>FileManager.GetFile</c> (on both game builds, with
/// different parameters), so a postfix there sees them. The crash catcher writes the list down, which is how a crash can be tied to
/// a pack that has no code of its own. It only stores a reference and a time: the game calls this very often.
/// </summary>
internal static class FileTrail
{
    private const int Size = 12;
    private static readonly string[] paths = new string[Size];
    private static readonly int[] times = new int[Size];
    private static int next;

    /// <summary>Hooks the game's file lookup. Null when done, else why not.</summary>
    public static string Install()
    {
        try
        {
            Type manager = AccessTools.TypeByName("SeaPower.FileManager");
            if (manager == null)
                return "SeaPower.FileManager is not in this build";
            var hook = new HarmonyMethod(typeof(FileTrail).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static));
            var harmony = new Harmony("dev.sewerlabs.seapowerkeel.filetrail");
            MethodInfo[] targets = AccessTools.GetDeclaredMethods(manager).Where(m => m.Name == "GetFile" && m.ReturnType == typeof(string)).ToArray();
            foreach (MethodInfo target in targets)
                harmony.Patch(target, postfix: hook);
            return targets.Length > 0 ? null : "FileManager.GetFile is not in this build";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void Postfix(string __result)
    {
        if (__result == null || CrashText.PackOf(__result) == null)
            return;
        int at = (next + 1) % Size;
        paths[at] = __result;
        times[at] = Environment.TickCount;
        next = at;
    }

    /// <summary>Appends the list, newest first, in the form <see cref="CrashText.LastPack"/> reads.</summary>
    public static void AppendTo(StringBuilder sb)
    {
        int now = Environment.TickCount, last = next;
        for (int i = 0; i < Size; i++)
        {
            int at = (last - i + Size) % Size;
            if (paths[at] != null)
                sb.Append(CrashText.FilePrefix).Append(unchecked(now - times[at])).Append('\t').Append(paths[at]).Append('\n');
        }
    }
}
