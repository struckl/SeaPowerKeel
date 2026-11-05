using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace SeaPowerKeel;

/// <summary>One mod folder as the game's mod list has it: a StreamingAssets sub folder or a Workshop item.</summary>
internal sealed class ModFolder
{
    public string Path;
    public string Name;           // folder name: the Workshop item id or the StreamingAssets folder
    public ulong SteamId;
    public bool Enabled;
    public int Order;             // position in the game's mod list, 0 = top

    public string Label => SteamId != 0 ? $"Workshop {SteamId}" : Name;
}

/// <summary>
/// The game's mod list read from disk, the way the game builds it (StreamingAssets sub folders and Workshop items, ordered
/// and switched on or off by <c>usersettings.ini</c> <c>[LoadOrder]</c>; folders not in that list are off). Used by the
/// stub, which runs before the game has listed anything, and by the loader when the game's own list does not show up.
/// Shared source: compiled into both the stub and the loader.
/// </summary>
internal static class ModFolders
{
    public const ulong SteamAppId = 1286220;

    // StreamingAssets folders that are the game's own, never mods (the game skips the same names).
    private static readonly string[] GameFolders = { "original", "user", "EntityScenes" };

    public static string WorkshopRoot =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(Paths.GameRootPath, "..", "..", "workshop", "content", SteamAppId.ToString()));

    public static string UserSettings => System.IO.Path.Combine(Application.persistentDataPath, "usersettings.ini");

    public static List<ModFolder> FromDisk()
    {
        var found = new List<ModFolder>();
        try
        {
            foreach (string dir in Directory.GetDirectories(Application.streamingAssetsPath))
            {
                string name = System.IO.Path.GetFileName(dir);
                if (!GameFolders.Any(g => name.EndsWith(g, StringComparison.Ordinal)))
                    found.Add(new ModFolder { Path = dir, Name = name });
            }
        }
        catch (Exception)
        {
            // no StreamingAssets: nothing local
        }
        try
        {
            if (Directory.Exists(WorkshopRoot))
            {
                foreach (string dir in Directory.GetDirectories(WorkshopRoot))
                {
                    string name = System.IO.Path.GetFileName(dir);
                    if (ulong.TryParse(name, out ulong id))
                        found.Add(new ModFolder { Path = dir, Name = name, SteamId = id });
                }
            }
        }
        catch (Exception)
        {
            // no Workshop folder: nothing subscribed
        }

        var ordered = new List<ModFolder>();
        foreach ((string dirName, bool on, ulong id) in ReadLoadOrder())
        {
            ModFolder match = (id != 0 ? found.FirstOrDefault(f => f.SteamId == id) : null)
                              ?? found.FirstOrDefault(f => f.Name == dirName);
            if (match == null)
                continue;
            match.Enabled = on;
            ordered.Add(match);
            found.Remove(match);
        }
        ordered.AddRange(found);          // not in the game's list yet: off, like the game treats them
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].Order = i;
        return ordered;
    }

    /// <summary><c>[LoadOrder]</c> of usersettings.ini: <c>ModNDirectory=folder,enabled,steamId</c>.</summary>
    private static IEnumerable<(string, bool, ulong)> ReadLoadOrder()
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(UserSettings);
        }
        catch (Exception)
        {
            yield break;
        }
        var entries = new SortedDictionary<int, string>();
        bool inSection = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith("["))
            {
                inSection = line.Equals("[LoadOrder]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (!inSection || eq < 0)
                continue;
            string key = line.Substring(0, eq).Trim();
            if (key.StartsWith("Mod", StringComparison.Ordinal) && key.EndsWith("Directory", StringComparison.Ordinal)
                && int.TryParse(key.Substring(3, key.Length - 3 - "Directory".Length), out int n))
                entries[n] = line.Substring(eq + 1);
        }
        foreach (string value in entries.Values)
        {
            string[] parts = value.Split(',');
            string dirName = parts[0].Trim();
            bool on = true;
            if (parts.Length > 1)
                bool.TryParse(parts[1].Trim(), out on);
            ulong id = 0;
            if (parts.Length > 2)
                ulong.TryParse(parts[2].Trim(), out id);
            if (dirName.Length > 0 || id != 0)
                yield return (dirName, on, id);
        }
    }

    /// <summary>A file in the mod folder or one level below (some mods keep their DLL in a sub folder).</summary>
    public static IEnumerable<string> FindFile(string folder, string fileName)
    {
        string top = System.IO.Path.Combine(folder, fileName);
        if (File.Exists(top))
            yield return top;
        string[] subs;
        try
        {
            subs = Directory.GetDirectories(folder);
        }
        catch (Exception)
        {
            yield break;
        }
        foreach (string sub in subs)
        {
            string inSub = System.IO.Path.Combine(sub, fileName);
            if (File.Exists(inSub))
                yield return inSub;
        }
    }
}
