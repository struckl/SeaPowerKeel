using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeaPowerKeel;

/// <summary>Reading a crash: shared source, compiled into Keel and into the watcher, so both name the same mod.</summary>
internal static class CrashText
{
    public const string CrashPrefix = "Crash: ";
    public const string StackFile = "Keel-crash-stack.txt";
    public const string ModsFile = "Keel-mods.txt";
    public const string JournalFile = "Keel-session.txt";
    public const string HuntFile = "Keel-hunt.txt";
    /// <summary>Written by the crash window: the player chose to keep this mod on.</summary>
    public const string KeepFile = "Keel-keep.txt";

    public static bool IsManagedFrame(string line)
    {
        string t = line.TrimStart();
        return t.StartsWith("at ", StringComparison.Ordinal) || t.StartsWith("(wrapper", StringComparison.Ordinal) || line.Contains("(Mono JIT code)");
    }

    public static bool HasRoot(string line, string root)
    {
        string probe = root + ".";
        for (int at = line.IndexOf(probe, StringComparison.Ordinal); at >= 0; at = line.IndexOf(probe, at + 1, StringComparison.Ordinal))
            if (at == 0 || !(char.IsLetterOrDigit(line[at - 1]) || line[at - 1] == '.' || line[at - 1] == '_'))
                return true;
        return false;
    }

    /// <summary>The index of the root whose code is highest on the stack, or -1.</summary>
    public static int FirstHit(string block, IList<string> roots)
    {
        if (block == null)
            return -1;
        foreach (string line in block.Replace("\r\n", "\n").Split('\n').Where(IsManagedFrame))
            for (int i = 0; i < roots.Count; i++)
                if (HasRoot(line, roots[i]))
                    return i;
        return -1;
    }

    /// <summary>"access violation (0xC0000005) in UnityPlayer.dll", when the block is the catcher's; else null.</summary>
    public static string CrashLine(string block)
    {
        if (block == null || !block.StartsWith(CrashPrefix, StringComparison.Ordinal))
            return null;
        int nl = block.IndexOf('\n');
        return (nl < 0 ? block : block.Substring(0, nl)).Substring(CrashPrefix.Length).Trim();
    }

    public static string ReadAll(string path)
    {
        try
        {
            if (path == null || !File.Exists(path))
                return null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
