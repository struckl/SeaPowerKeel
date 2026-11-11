using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SeaPowerKeel;

/// <summary>Puts every log that helps with a bug report into one zip: Keel's, BepInEx's and the game's. The files may be in use by the running game.</summary>
internal static class LogBundle
{
    /// <summary>
    /// Writes the zip and returns its path. <paramref name="bepInEx"/> is the game's BepInEx folder. <paramref name="scrub"/> (for Send
    /// report) puts %USERNAME% in place of the Windows user name in every path and keeps only the end of a very long log.
    /// </summary>
    public static string Save(string bepInEx, string folder = null, bool scrub = false)
    {
        folder ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string zip = Path.Combine(folder, $"Sea Power logs {DateTime.Now:yyyy-MM-dd HH-mm-ss}.zip");
        string gameData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Triassic Games", "Sea Power");
        string gameLogs = Path.Combine(Path.GetDirectoryName(bepInEx.TrimEnd('\\', '/')) ?? "", "Sea Power_Data", "Logs");

        Regex user = scrub ? UserName() : null;
        using (FileStream stream = new FileStream(zip, FileMode.CreateNew))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (string pattern in new[] { "Keel-log*.txt", "Keel-report.txt", "Keel-crash*.txt", "Keel-session.txt", "Keel-mods.txt", "Keel-held.txt", "LogOutput.log" })
                AddAll(archive, Find(bepInEx, pattern), "BepInEx", user);
            AddAll(archive, Find(gameData, "Player*.log"), "Game", user);
            AddAll(archive, Newest(Path.Combine(gameData, "CrashDiagnostics"), "*.jsonl", 3), "Game/CrashDiagnostics", user);
            AddAll(archive, Newest(gameLogs, "*session*.html", 2), "Game/Logs", user);
        }
        return zip;
    }

    /// <summary>The Windows user name right after "Users\", "Users/" or "Users\\" (as JSON writes it), in any case.</summary>
    internal static Regex UserName()
    {
        string[] names = new[] { Environment.UserName, Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/')) }
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length).ToArray();
        if (names.Length == 0)
            return null;
        return new Regex(@"(?<=\busers(?:\\\\|\\|/))(?:" + string.Join("|", names.Select(Regex.Escape)) + @")(?![\w.\-])",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static string Scrub(string text, Regex user) => user == null ? text : user.Replace(text, "%USERNAME%");

    private const int KeepChars = 6 * 1024 * 1024;     // a sent log keeps its last 6 M characters: the end is where the crash is

    /// <summary>Shows the zip in Explorer, selected, ready to drag into a post.</summary>
    public static void Reveal(string zip) => Process.Start("explorer.exe", $"/select,\"{zip}\"")?.Dispose();

    private static IEnumerable<string> Find(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : new string[0];

    private static IEnumerable<string> Newest(string dir, string pattern, int count) =>
        Find(dir, pattern).Where(f => new FileInfo(f).Length > 0).OrderByDescending(File.GetLastWriteTimeUtc).Take(count);

    private static void AddAll(ZipArchive archive, IEnumerable<string> files, string folder, Regex user)
    {
        foreach (string file in files)
        {
            try
            {
                using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (user == null)
                    {
                        using (Stream target = archive.CreateEntry(folder + "/" + Path.GetFileName(file), CompressionLevel.Optimal).Open())
                            source.CopyTo(target);
                        continue;
                    }
                    string text;
                    using (var reader = new StreamReader(source, Encoding.UTF8, true))
                        text = reader.ReadToEnd();
                    if (text.Length > KeepChars)
                        text = $"[Keel: only the last {KeepChars / 1024 / 1024} M characters of this log were sent]\n" + text.Substring(text.Length - KeepChars);
                    using (var target = new StreamWriter(archive.CreateEntry(folder + "/" + Path.GetFileName(file), CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
                        target.Write(Scrub(text, user));
                }
            }
            catch (Exception)
            {
                // one unreadable file must not cost the player the others
            }
        }
    }
}
