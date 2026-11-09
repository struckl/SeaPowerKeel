using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeaPowerKeel;

/// <summary>
/// Looks through the switched-on mod folders for image and sound files that cannot be what they claim to be: empty, or not starting
/// the way such a file must. Reads only the first bytes. A folder found clean is remembered until it changes, so only a new or
/// updated mod costs any time. It says what it found; it does not switch anything off, a broken picture does not have to be fatal.
/// </summary>
internal static class PackCheck
{
    private static bool StartsWith(byte[] head, params byte[] magic) => head.Length >= magic.Length && !magic.Where((b, i) => head[i] != b).Any();

    private static bool IsImage(byte[] h) =>
        StartsWith(h, 0x89, 0x50, 0x4E, 0x47) || StartsWith(h, 0xFF, 0xD8) || StartsWith(h, 0x44, 0x44, 0x53, 0x20);   // PNG, JPEG, DDS: Unity reads the content, not the name

    private static bool IsWave(byte[] h) =>
        StartsWith(h, 0x52, 0x49, 0x46, 0x46) && h.Length >= 12 && h[8] == 0x57 && h[9] == 0x41 && h[10] == 0x56 && h[11] == 0x45;

    /// <summary>What is wrong with one file, or null.</summary>
    internal static string Problem(string path)
    {
        string kind = Path.GetExtension(path).ToLowerInvariant();
        Func<byte[], bool> ok = kind is ".png" or ".jpg" or ".jpeg" or ".dds" ? IsImage : kind == ".wav" ? IsWave : null;
        if (ok == null)
            return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
            return "it is empty";
        var head = new byte[12];
        int read = stream.Read(head, 0, head.Length);
        return ok(head.Take(read).ToArray()) ? null : $"it is not a valid {kind.Substring(1)} file";
    }

    /// <summary>One line per broken file, as "folder label: path in the folder - why".</summary>
    public static List<string> Scan(IEnumerable<ModFolder> folders, string cachePath)
    {
        var clean = new Dictionary<string, string>();
        var found = new List<string>();
        try
        {
            if (File.Exists(cachePath))
                foreach (string[] parts in File.ReadAllLines(cachePath).Select(l => l.Split('\t')).Where(p => p.Length == 2))
                    clean[parts[0]] = parts[1];
        }
        catch (Exception)
        {
            // no memory of earlier scans: this one takes longer
        }

        foreach (ModFolder folder in folders)
        {
            try
            {
                string stamp = Directory.GetLastWriteTimeUtc(folder.Path).Ticks.ToString();
                if (clean.TryGetValue(folder.Path, out string known) && known == stamp)
                    continue;
                int before = found.Count;
                foreach (string file in Directory.GetFiles(folder.Path, "*", SearchOption.AllDirectories))
                {
                    string why = null;
                    try
                    {
                        why = Problem(file);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        why = "it cannot be read";
                    }
                    if (why != null)
                        found.Add($"{folder.Label}: {file.Substring(folder.Path.Length).TrimStart('\\', '/')} - {why}");
                }
                if (found.Count == before)
                    clean[folder.Path] = stamp;
                else
                    clean.Remove(folder.Path);
            }
            catch (Exception)
            {
                // a folder that cannot be listed: the scanner already said so
            }
        }

        try
        {
            File.WriteAllLines(cachePath, clean.Select(c => c.Key + "\t" + c.Value));
        }
        catch (Exception)
        {
            // only a shortcut for next time
        }
        return found;
    }
}
