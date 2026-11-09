using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SeaPowerKeel;

/// <summary>Puts every log that helps with a bug report into one zip: Keel's, BepInEx's and the game's. The files may be in use by the running game.</summary>
internal static class LogBundle
{
    /// <summary>Writes the zip and returns its path. <paramref name="bepInEx"/> is the game's BepInEx folder.</summary>
    public static string Save(string bepInEx, string folder = null)
    {
        folder ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string zip = Path.Combine(folder, $"Sea Power logs {DateTime.Now:yyyy-MM-dd HH-mm-ss}.zip");
        string gameData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Triassic Games", "Sea Power");
        string gameLogs = Path.Combine(Path.GetDirectoryName(bepInEx.TrimEnd('\\', '/')) ?? "", "Sea Power_Data", "Logs");

        using (FileStream stream = new FileStream(zip, FileMode.CreateNew))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (string pattern in new[] { "Keel-log*.txt", "Keel-report.txt", "Keel-crash*.txt", "Keel-session.txt", "Keel-mods.txt", "Keel-held.txt", "LogOutput.log" })
                AddAll(archive, Find(bepInEx, pattern), "BepInEx");
            AddAll(archive, Find(gameData, "Player*.log"), "Game");
            AddAll(archive, Newest(Path.Combine(gameData, "CrashDiagnostics"), "*.jsonl", 3), "Game/CrashDiagnostics");
            AddAll(archive, Newest(gameLogs, "*session*.html", 2), "Game/Logs");
        }
        return zip;
    }

    /// <summary>Shows the zip in Explorer, selected, ready to drag into a post.</summary>
    public static void Reveal(string zip) => Process.Start("explorer.exe", $"/select,\"{zip}\"")?.Dispose();

    private static IEnumerable<string> Find(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : new string[0];

    private static IEnumerable<string> Newest(string dir, string pattern, int count) =>
        Find(dir, pattern).Where(f => new FileInfo(f).Length > 0).OrderByDescending(File.GetLastWriteTimeUtc).Take(count);

    private static void AddAll(ZipArchive archive, IEnumerable<string> files, string folder)
    {
        foreach (string file in files)
        {
            try
            {
                using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (Stream target = archive.CreateEntry(folder + "/" + Path.GetFileName(file), CompressionLevel.Optimal).Open())
                    source.CopyTo(target);
            }
            catch (Exception)
            {
                // one unreadable file must not cost the player the others
            }
        }
    }
}
