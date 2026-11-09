using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeaPowerKeel;

/// <summary>Reading a crash: shared source, compiled into Keel and into the watcher, so both name the same mod.</summary>
internal static class CrashText
{
    public const string CrashPrefix = "Crash: ";
    public const string FilePrefix = "File: ";
    /// <summary>The last game's stack, kept when Keel installs the catcher (which empties the stack file) at the start of the next one.</summary>
    public const string StackPrevFile = "Keel-crash-stack.prev.txt";
    /// <summary>Written by the crash window: this pack folder is to be switched off in the game's mod list.</summary>
    public const string PackOffFile = "Keel-packoff.txt";
    /// <summary>How long before a crash a file may have been opened to count as the likely cause.</summary>
    public const int PackWindowMs = 10000;
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

    private static readonly string[] NotPacks = { "original", "user", "EntityScenes", "SeaPowerKeel", "ACConfigs", "3835527459", "3380210757", "3832006068" };

    /// <summary>The mod folder a full path lies in (a Workshop id or a StreamingAssets folder), or null for the game's own files.</summary>
    public static string PackOf(string path)
    {
        string p = path.Replace('\\', '/');
        foreach (string marker in new[] { "/workshop/content/1286220/", "/StreamingAssets/" })
        {
            int at = p.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                continue;
            int start = at + marker.Length, end = p.IndexOf('/', start);
            if (end < 0)
                return null;
            string folder = p.Substring(start, end - start);
            return NotPacks.Contains(folder, StringComparer.OrdinalIgnoreCase) ? null : folder;
        }
        return null;
    }

    /// <summary>The mod file the game opened last, when that was shortly before the crash. The block is the catcher's.</summary>
    public static bool LastPack(string block, out string folder, out string fullPath)
    {
        folder = fullPath = null;
        if (block == null)
            return false;
        foreach (string line in block.Replace("\r\n", "\n").Split('\n').Where(l => l.StartsWith(FilePrefix, StringComparison.Ordinal)))
        {
            string[] parts = line.Substring(FilePrefix.Length).Split('\t');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int age) || age < 0 || age > PackWindowMs)
                continue;
            folder = PackOf(parts[1]);
            if (folder == null)
                continue;
            fullPath = parts[1];
            return true;
        }
        return false;
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
