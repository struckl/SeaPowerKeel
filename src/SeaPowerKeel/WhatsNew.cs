using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SeaPowerKeel;

internal enum NewsKind { Problem, Updated, New, Gone }

/// <summary>One line of the What's new card: a mod that changed since the last start, and what its author says about it.</summary>
internal sealed class NewsItem
{
    public NewsKind Kind;
    public string Key;              // Workshop item id or StreamingAssets folder name
    public string Name;
    public string From, To;         // versions, either may be empty
    public ulong SteamId;
    public string Folder;
    public long PrevUpdated, Updated; // Steam's "timeupdated" of the item at the last start and now (0 = unknown)
    public string Source = "";      // where the lines come from: "Workshop", "CHANGELOG" or ""
    public readonly List<string> Lines = new();

    public string Tag => Kind switch
    {
        NewsKind.New => "NEW",
        NewsKind.Updated => "UPDATED",
        NewsKind.Gone => "GONE",
        _ => "PROBLEM",
    };

    public string Headline
    {
        get
        {
            if (Kind == NewsKind.Updated && From != To && From.Length > 0 && To.Length > 0)
                return $"{Name}  {From} → {To}";
            if (Kind != NewsKind.Gone && To.Length > 0)
                return $"{Name}  {To}";
            return Name;
        }
    }
}

/// <summary>What Keel saw of one mod folder at a start.</summary>
internal sealed class SeenMod
{
    public string Key;
    public string Name;
    public long Updated;            // Steam's timeupdated (Workshop items), 0 = not known
    public string Stamp;            // file count, total size and newest write time: any change to the folder changes it
    public readonly Dictionary<string, string> Versions = new();   // plugin id -> version
    public readonly HashSet<string> Ok = new();                    // plugin ids that ran without a problem
}

/// <summary>
/// What's new: Keel remembers the mods it loaded (BepInEx\Keel-seen.txt) and at the next start names the ones that are new,
/// updated, gone or newly in trouble, with the author's words for the update: the Workshop change notes for that update
/// (only the entries since the last start, by their time stamp), else the top of the mod's CHANGELOG.md, else just the
/// version jump. The first start (no file yet) says nothing. Pure logic here; KeelHost draws the card.
/// </summary>
internal static class WhatsNew
{
    public const string FileName = "Keel-seen.txt";

    // ---- what is there now ----

    /// <param name="ownDll">Keel's own DLL: its folder has no plugin entry, so Keel's version is added for it by hand.</param>
    public static Dictionary<string, SeenMod> Look(IEnumerable<ModFolder> folders, IEnumerable<PluginEntry> plugins, string workshopRoot,
                                                   string ownDll = null, string ownGuid = null, string ownVersion = null)
    {
        Dictionary<ulong, long> steamTimes = SteamTimes(workshopRoot);
        var mods = new Dictionary<string, SeenMod>(StringComparer.OrdinalIgnoreCase);
        string ownFolder = Full(ownDll == null ? null : Path.GetDirectoryName(ownDll));
        foreach (ModFolder f in folders)
        {
            var m = new SeenMod { Key = KeyOf(f), Name = NameOf(f.Path) ?? f.Label, Stamp = Stamp(f.Path) };
            if (f.SteamId != 0 && steamTimes.TryGetValue(f.SteamId, out long t))
                m.Updated = t;
            if (ownFolder != null && ownGuid != null && string.Equals(Full(f.Path), ownFolder, StringComparison.OrdinalIgnoreCase))
                m.Versions[ownGuid] = ownVersion ?? "";
            mods[m.Key] = m;
        }
        foreach (PluginEntry p in plugins)
        {
            if (p.Folder == null || !mods.TryGetValue(KeyOf(p.Folder), out SeenMod m))
                continue;
            m.Versions[p.Guid] = p.Version.ToString();
            if (NameOf(p.Folder.Path) == null && m.Name == p.Folder.Label)
                m.Name = p.Display;
        }
        return mods;
    }

    /// <summary>After the start: which plugins ran without a problem (a mod that breaks later is news next time).</summary>
    public static void MarkOk(Dictionary<string, SeenMod> now, IEnumerable<PluginEntry> plugins)
    {
        foreach (PluginEntry p in plugins)
            if (p.Folder != null && now.TryGetValue(KeyOf(p.Folder), out SeenMod m) && p.State == PluginState.Running)
                m.Ok.Add(p.Guid);
    }

    public static string KeyOf(ModFolder f) => f.SteamId != 0 ? f.SteamId.ToString(CultureInfo.InvariantCulture) : f.Name;

    /// <summary>The mod's own name from its _info.ini (<c>Name=</c>, English section first), or null.</summary>
    public static string NameOf(string folder)
    {
        try
        {
            string ini = Path.Combine(folder ?? "", "_info.ini");
            if (!File.Exists(ini))
                return null;
            string first = null;
            bool english = false;
            foreach (string raw in File.ReadAllLines(ini))
            {
                string line = raw.Trim();
                if (line.StartsWith("["))
                {
                    english = line.Equals("[Language_en]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase))
                    continue;
                string value = line.Substring(5).Trim();
                if (value.Length == 0)
                    continue;
                if (english)
                    return value;
                first ??= value;
            }
            return first;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Full(string path)
    {
        try
        {
            return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Files a mod (or the game) writes into its own folder while it runs: they change at every start and are no update.
    // BetterTacMap rewrites settings.cfg in its Workshop folder each time (found in the game test 2026-11-12).
    private static readonly string[] RuntimeFiles = { ".cfg", ".log", ".tmp", ".bak", ".lock" };

    public const string StampVersion = "s2:";

    internal static string Stamp(string folder)
    {
        try
        {
            long count = 0, size = 0, newest = 0;
            foreach (FileInfo fi in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (RuntimeFiles.Any(x => fi.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                    continue;
                count++;
                size += fi.Length;
                newest = Math.Max(newest, fi.LastWriteTimeUtc.Ticks);
            }
            return $"{StampVersion}{count}:{size}:{newest}";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Steam's <c>timeupdated</c> per installed Workshop item, from steamapps\workshop\appworkshop_&lt;app&gt;.acf.</summary>
    public static Dictionary<ulong, long> SteamTimes(string workshopRoot)
    {
        var times = new Dictionary<ulong, long>();
        try
        {
            string acf = Path.GetFullPath(Path.Combine(workshopRoot, "..", "..", $"appworkshop_{ModFolders.SteamAppId}.acf"));
            if (!File.Exists(acf))
                return times;
            ulong current = 0;
            bool installed = false;
            int depth = 0;
            foreach (string raw in File.ReadAllLines(acf))
            {
                string line = raw.Trim();
                if (line == "{") { depth++; continue; }
                if (line == "}") { depth--; if (depth <= 1) installed = false; continue; }
                MatchCollection q = Regex.Matches(line, "\"([^\"]*)\"");
                if (q.Count == 1 && depth == 1)
                    installed = q[0].Groups[1].Value == "WorkshopItemsInstalled";
                else if (q.Count == 1 && depth == 2 && installed)
                    ulong.TryParse(q[0].Groups[1].Value, out current);
                else if (q.Count == 2 && depth == 3 && installed && current != 0 && q[0].Groups[1].Value == "timeupdated"
                         && long.TryParse(q[1].Groups[1].Value, out long t))
                    times[current] = t;
            }
        }
        catch (Exception)
        {
            // no manifest: Workshop items are compared by their files alone
        }
        return times;
    }

    // ---- the file ----

    public static Dictionary<string, SeenMod> Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var mods = new Dictionary<string, SeenMod>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(path))
            {
                string[] c = line.Split('\t');
                if (c[0] == "mod" && c.Length >= 5)
                    mods[c[1]] = new SeenMod { Key = c[1], Name = c[2], Updated = long.TryParse(c[3], out long t) ? t : 0, Stamp = c[4] };
                else if (c[0] == "plugin" && c.Length >= 5 && mods.TryGetValue(c[1], out SeenMod m))
                {
                    m.Versions[c[2]] = c[3];
                    if (c[4] == "ok")
                        m.Ok.Add(c[2]);
                }
            }
            return mods;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Write(string path, Dictionary<string, SeenMod> mods)
    {
        var sb = new StringBuilder();
        sb.Append("# R.A.T.S. Keel: the mods of the last start, for What's new. Delete it to start over.\n");
        foreach (SeenMod m in mods.Values)
        {
            sb.Append($"mod\t{m.Key}\t{Clean(m.Name)}\t{m.Updated}\t{m.Stamp}\n");
            foreach (KeyValuePair<string, string> v in m.Versions)
                sb.Append($"plugin\t{m.Key}\t{v.Key}\t{v.Value}\t{(m.Ok.Contains(v.Key) ? "ok" : "-")}\n");
        }
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString());
        if (File.Exists(path))
            File.Delete(path);
        File.Move(tmp, path);
    }

    private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    // ---- the difference ----

    /// <summary>New, updated and gone mods (problems come later, see <see cref="Problems"/>); null last time = first start, nothing to say.</summary>
    public static List<NewsItem> Compare(Dictionary<string, SeenMod> last, Dictionary<string, SeenMod> now, IEnumerable<ModFolder> folders)
    {
        var news = new List<NewsItem>();
        if (last == null)
            return news;
        var byKey = folders.GroupBy(KeyOf, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (SeenMod m in now.Values)
        {
            byKey.TryGetValue(m.Key, out ModFolder f);
            var item = new NewsItem
            {
                Key = m.Key, Name = m.Name, To = MainVersion(m, null), SteamId = f?.SteamId ?? 0, Folder = f?.Path, Updated = m.Updated,
            };
            if (!last.TryGetValue(m.Key, out SeenMod was))
            {
                item.Kind = NewsKind.New;
                item.From = "";
                news.Add(item);
                continue;
            }
            // A Workshop item changes only when Steam updates it: its update time decides. Else the folder's files do, compared
            // only between stamps of the same kind (an older Keel's stamp counted every file, settings the mod writes included).
            bool steamKnown = m.Updated != 0 && was.Updated != 0;
            bool filesChanged = steamKnown
                ? m.Updated != was.Updated
                : was.Stamp != m.Stamp && was.Stamp.StartsWith(StampVersion) && m.Stamp.StartsWith(StampVersion);
            bool changed = filesChanged || m.Versions.Any(v => !was.Versions.TryGetValue(v.Key, out string old) || old != v.Value);
            if (!changed)
                continue;
            item.Kind = NewsKind.Updated;
            item.PrevUpdated = was.Updated;
            item.To = MainVersion(m, was);
            item.From = MainVersion(was, m);
            news.Add(item);
        }
        foreach (SeenMod was in last.Values.Where(w => !now.ContainsKey(w.Key)))
            news.Add(new NewsItem { Kind = NewsKind.Gone, Key = was.Key, Name = was.Name, From = MainVersion(was, null), To = "" });
        return news;
    }

    /// <summary>The version to show: of the plugin whose version changed against <paramref name="other"/>, else the first one.</summary>
    private static string MainVersion(SeenMod m, SeenMod other)
    {
        if (m.Versions.Count == 0)
            return "";
        if (other != null)
            foreach (KeyValuePair<string, string> v in m.Versions)
                if (other.Versions.TryGetValue(v.Key, out string o) && o != v.Value)
                    return v.Value;
        return m.Versions.Values.First();
    }

    /// <summary>Plugins that ran fine last time and have a problem now: these lead the card.</summary>
    public static List<NewsItem> Problems(Dictionary<string, SeenMod> last, IEnumerable<PluginEntry> plugins)
    {
        var news = new List<NewsItem>();
        if (last == null)
            return news;
        foreach (PluginEntry p in plugins.Where(Report.IsProblem))
        {
            if (p.Folder == null || !last.TryGetValue(KeyOf(p.Folder), out SeenMod was) || !was.Ok.Contains(p.Guid))
                continue;
            var item = new NewsItem { Kind = NewsKind.Problem, Key = KeyOf(p.Folder), Name = p.Display, From = "", To = p.Version.ToString() };
            item.Lines.Add($"{Report.Tag(p.State)}: {(p.Reason.Length > 0 ? p.Reason : p.FirstLaterError ?? "threw errors")}");
            news.Add(item);
        }
        return news;
    }

    // ---- what the author says ----

    /// <summary>Fills each updated or new mod's lines: Workshop notes (when <paramref name="fetch"/> gives the page), else CHANGELOG.md.</summary>
    public static void AddNotes(IEnumerable<NewsItem> news, Func<ulong, string> fetch)
    {
        foreach (NewsItem n in news.Where(n => n.Kind is NewsKind.Updated or NewsKind.New))
        {
            if (n.SteamId != 0 && fetch != null)
            {
                string page = null;
                try
                {
                    page = fetch(n.SteamId);
                }
                catch (Exception)
                {
                    // offline or Steam unreachable: the CHANGELOG below
                }
                List<string> lines = page == null ? new List<string>() : WorkshopNotes(page, n.Kind == NewsKind.New ? 0 : n.PrevUpdated, n.Updated);
                if (lines.Count > 0)
                {
                    n.Lines.AddRange(lines);
                    n.Source = "Workshop";
                    continue;
                }
            }
            List<string> log = ChangelogNotes(n.Folder, n.Kind == NewsKind.New ? "" : n.From);
            if (log.Count > 0)
            {
                n.Lines.AddRange(log);
                n.Source = "CHANGELOG";
                continue;
            }
            // Nothing from the author: say at least what kind of change it was.
            if (n.Kind == NewsKind.Updated)
                n.Lines.Add(n.From.Length > 0 && n.From == n.To ? "Same version number, new files (a new build)."
                            : n.From.Length == 0 && n.To.Length == 0 ? "Its files changed." : "No change notes from the author.");
        }
    }

    /// <summary>
    /// The change notes of a Workshop item's changelog page (steamcommunity.com/sharedfiles/filedetails/changelog/&lt;id&gt;) written
    /// after <paramref name="since"/> and not after <paramref name="until"/> (unix seconds; each note's <c>&lt;p id&gt;</c> is its time).
    /// since = 0: the newest note only. Newest first, one line per line break.
    /// </summary>
    public static List<string> WorkshopNotes(string html, long since, long until)
    {
        var lines = new List<string>();
        var entries = new List<(long time, string text)>();
        foreach (Match m in Regex.Matches(html ?? "", "<p id=\"(\\d{9,11})\"[^>]*>(.*?)</p>", RegexOptions.Singleline))
            if (long.TryParse(m.Groups[1].Value, out long t))
                entries.Add((t, m.Groups[2].Value));
        entries = entries.OrderByDescending(e => e.time).ToList();
        IEnumerable<(long time, string text)> pick = since <= 0
            ? entries.Take(1)
            : entries.Where(e => e.time > since && (until <= 0 || e.time <= until + 600));
        foreach ((long _, string text) in pick)
        {
            string plain = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            plain = Regex.Replace(plain, "<[^>]+>", "");
            plain = WebUtility.HtmlDecode(plain);
            lines.AddRange(plain.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        }
        return lines;
    }

    /// <summary>
    /// The newest entries of the mod's CHANGELOG.md (in its folder or one level below) above the version it had last time:
    /// every <c>## </c> section whose heading names a version newer than <paramref name="since"/>; since empty = the top section.
    /// Bullet points and plain lines, Markdown marks removed.
    /// </summary>
    public static List<string> ChangelogNotes(string folder, string since)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(folder))
            return lines;
        string file = null;
        foreach (string name in new[] { "CHANGELOG.md", "changelog.md", "CHANGELOG.txt", "Changelog.md" })
        {
            file = ModFolders.FindFile(folder, name).FirstOrDefault();
            if (file != null)
                break;
        }
        if (file == null)
            return lines;
        string[] text;
        try
        {
            text = File.ReadAllLines(file);
        }
        catch (Exception)
        {
            return lines;
        }
        return ChangelogLines(text, since);
    }

    internal static List<string> ChangelogLines(IEnumerable<string> text, string since)
    {
        var lines = new List<string>();
        Version old = Scanner.ParseVersion(since ?? "");
        bool inside = false;
        int sections = 0;
        foreach (string raw in text)
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("## "))
            {
                Match v = Regex.Match(line, @"(\d+\.\d+(?:\.\d+)?)");
                Version heading = v.Success ? Scanner.ParseVersion(v.Groups[1].Value) : null;
                if (old == null ? sections > 0 : heading != null && heading <= old)
                    break;                  // reached what the player had (or, with no old version, the second section)
                if (sections >= 5)
                    break;
                // With an old version: only sections that name a newer one ("Unreleased" is not in the player's build).
                inside = old == null || heading != null;
                if (inside)
                    sections++;
                continue;
            }
            if (!inside || line.Trim().Length == 0 || line.StartsWith("#"))
                continue;
            string plain = Regex.Replace(line.Trim(), @"^[-*+]\s+", "");
            plain = plain.Replace("**", "").Replace("`", "");
            plain = Regex.Replace(plain, @"\[([^\]]+)\]\([^)]+\)", "$1");
            if (plain.Length > 0)
                lines.Add(plain);
        }
        return lines;
    }

    /// <summary>Why pages could not be read (for the log): in the game the request runs on Unity's Mono, not .NET.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentBag<string> FetchErrors = new();

    /// <summary>The Workshop changelog page of an item, or null (any failure). Runs on a worker thread.</summary>
    public static string FetchWorkshopPage(ulong id, int timeoutMs)
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create($"https://steamcommunity.com/sharedfiles/filedetails/changelog/{id}?l=english");
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "RATS-Keel";
            using var resp = (HttpWebResponse)req.GetResponse();
            using var reader = new StreamReader(resp.GetResponseStream() ?? Stream.Null, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            FetchErrors.Add($"Workshop {id}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Plain text for the report file.</summary>
    public static string Text(IReadOnlyList<NewsItem> news)
    {
        var sb = new StringBuilder();
        foreach (NewsItem n in news)
        {
            sb.Append($"  {n.Tag,-8} {n.Headline}{(n.Source.Length > 0 ? $"   (from {n.Source})" : "")}\n");
            foreach (string l in n.Lines)
                sb.Append($"           {l}\n");
        }
        return sb.ToString();
    }
}
