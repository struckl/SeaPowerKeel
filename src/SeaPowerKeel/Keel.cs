using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace SeaPowerKeel;

/// <summary>
/// R.A.T.S. Keel: a mod loader for Sea Power. Started by the stub in BepInEx\plugins (see <see cref="Boot"/>), it reads the
/// game's mod list, finds the plugins in the enabled mod folders without loading them, checks each DLL against the
/// running game build, works out dependencies, conflicts and start order, and starts the plugins one by one, each on its
/// own so that one broken mod cannot stop the rest. It runs plain BepInEx plugins and Anchor Chain mods (the latter only
/// while Anchor Chain itself is not running). Ctrl+Shift+K shows what it did and why.
/// </summary>
public static class Keel
{
    public const string Guid = "dev.sewerlabs.seapowerkeel";
    public const string Name = "R.A.T.S. Keel";
    public static readonly string Version = typeof(Keel).Assembly.GetName().Version.ToString(3);

    internal static readonly List<PluginEntry> Plugins = new();

    /// <summary>For other mods: is this plugin id running under Keel?</summary>
    public static bool IsRunning(string guid) =>
        Plugins.Any(p => p.Guid == guid && p.State is PluginState.Running or PluginState.RunningWithErrors);

    /// <summary>Called by the stub (any version) with its own version and the path of this DLL.</summary>
    public static void Boot(string stubVersion, string path)
    {
        const string marker = Guid + ".booted";
        if (AppDomain.CurrentDomain.GetData(marker) != null)
            return;
        AppDomain.CurrentDomain.SetData(marker, path);
        var go = new GameObject(Name);
        UnityEngine.Object.DontDestroyOnLoad(go);
        var host = go.AddComponent<KeelHost>();
        host.StubVersion = stubVersion;
        host.OwnPath = path;
    }
}

internal sealed class KeelHost : MonoBehaviour
{
    public string StubVersion;
    public string OwnPath;

    private ManualLogSource log;
    private ConfigEntry<bool> loadAcMods;
    private ConfigEntry<string> versionCheck;
    private ConfigEntry<string> disabled;
    private ConfigEntry<string> hotkey;
    private ConfigEntry<bool> notice;
    private KeelLog keelLog;
    private float nextFlush;
    private readonly List<string> notes = new();
    private bool done;
    private float noticeUntil;
    private bool showWindow;
    private Rect window = new(60, 60, 960, 600);
    private Vector2 scroll;
    private string reportPath;
    private KeyCode key = KeyCode.K;
    private bool keyCtrl = true, keyShift = true, keyAlt;

    private void Awake()
    {
        log = BepInEx.Logging.Logger.CreateLogSource(Keel.Name);
        var config = new ConfigFile(Path.Combine(Paths.ConfigPath, Keel.Guid + ".cfg"), true);
        loadAcMods = config.Bind("Loading", "AnchorChainMods", true,
            "Start Anchor Chain mods when Anchor Chain itself is not running (for example when its preloader cannot start on this game " +
            "build). While Anchor Chain runs, Keel always leaves its mods to it, so nothing starts twice.");
        versionCheck = config.Bind("Loading", "GameVersionCheck", "Standard", new ConfigDescription(
            "Standard: a DLL that would stop missions from loading on this game build (a type of it is built on something this build " +
            "lacks) is not loaded; other missing parts are a warning. Strict: any missing part keeps the DLL from loading. Off: no check.",
            new AcceptableValueList<string>("Standard", "Strict", "Off")));
        disabled = config.Bind("Loading", "DisabledPlugins", "",
            "Plugin ids (comma separated) Keel does not start, even though their mod folder is on.");
        hotkey = config.Bind("Report", "Hotkey", "Ctrl+Shift+K", "Shows and hides the report window.");
        notice = config.Bind("Report", "NoticeOnProblems", true,
            "After start-up, show a short notice in the corner when a mod did not start or threw errors.");
        ConfigEntry<bool> timedLog = config.Bind("Logging", "TimestampedLog", true,
            @"Write BepInEx\Keel-log.txt: every log line of the game, BepInEx and all mods with time of day and seconds since the game " +
            "started, and the mod an exception came from (with its stack trace).");
        ConfigEntry<int> keep = config.Bind("Logging", "KeepSessions", 3,
            "How many earlier sessions' logs to keep (Keel-log.1.txt is the last one).");
        if (timedLog.Value)
        {
            try
            {
                keelLog = new KeelLog(Math.Max(0, Math.Min(20, keep.Value)));
                BepInEx.Logging.Logger.Listeners.Add(keelLog);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Cannot write the timestamped log: {ex.Message}");
            }
        }
        ParseHotkey(hotkey.Value);
        reportPath = Path.Combine(Paths.BepInExRootPath, "Keel-report.txt");
        log.LogInfo($"{Keel.Name} {Keel.Version} (stub {StubVersion}) on the {GameState.BuildName}, from {OwnPath}");
        StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        // One frame first: every plugin in BepInEx\plugins (Anchor Chain's preloader too) has had its Awake by then.
        yield return null;

        // 1. The game's mod list. 0.8.7 has it straight away, the globe build after a while; failing that, the same list from disk.
        var watch = Stopwatch.StartNew();
        List<ModFolder> folders;
        while ((folders = GameState.GameModList()) == null && watch.Elapsed.TotalSeconds < 60)
            yield return new WaitForSecondsRealtime(0.25f);
        if (folders == null)
        {
            folders = ModFolders.FromDisk();
            notes.Add("The game did not list its mod folders within 60 s; Keel used usersettings.ini instead.");
        }

        // 2. On the globe build, after the start-up load screen: mods started during it can keep the unit data from loading.
        if (GameState.Globe)
        {
            watch.Restart();
            while (!GameState.StartupDone() && watch.Elapsed.TotalSeconds < 180)
                yield return new WaitForSecondsRealtime(0.25f);
        }

        List<ModFolder> enabled = folders.Where(f => f.Enabled).ToList();
        log.LogInfo($"{enabled.Count} of {folders.Count} mod folders are on.");

        // 3. Find the plugins (metadata only).
        Keel.Plugins.AddRange(Scanner.Scan(enabled, Warn));
        yield return null;

        var runner = new Runner(log, gameObject, enabled);
        if (keelLog != null)
            keelLog.OwnerOf = runner.OwnerOf;
        // Unity hands exceptions to BepInEx without saying which mod threw; Keel adds a line that does, with the stack trace,
        // to LogOutput.log and Keel-log.txt alike.
        runner.ModError = (p, message, stack) =>
            log.LogError($"The exception above came from {p.Display} {p.Version} ({p.Guid}, {p.Folder.Label}): {message}{Environment.NewLine}{stack}");
        RuleAnchorChain(enabled, runner);

        // 4. Check every DLL that may start against this game build.
        yield return Check();

        // 5. Plan and start.
        var elsewhere = new HashSet<string>(Chainloader.PluginInfos.Keys);
        elsewhere.UnionWith(Keel.Plugins.Where(p => p.State == PluginState.LeftToAnchorChain).Select(p => p.Guid));
        var off = new HashSet<string>(disabled.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
        List<PluginEntry> order = Planner.Plan(Keel.Plugins, elsewhere, off, Process.GetCurrentProcess().ProcessName);
        foreach (PluginEntry p in order)
        {
            runner.Start(p);
            log.Log(p.State == PluginState.Running ? LogLevel.Info : LogLevel.Error,
                    $"{Report.Tag(p.State)}: {p.Display} {p.Version} ({p.Folder.Label}){(p.Reason.Length > 0 ? " – " + p.Reason : "")}");
            yield return null;
        }
        foreach (PluginEntry p in Keel.Plugins.Where(p => p.State is PluginState.NotStarted))
            log.LogWarning($"NOT STARTED: {p.Display} {p.Version} ({p.Folder.Label}) – {p.Reason}");

        done = true;
        log.LogInfo($"Done: {Report.Summary(Keel.Plugins)}. Report: {reportPath} or {hotkey.Value} in the game.");
        WriteReport();
        if (notice.Value && Keel.Plugins.Any(Report.IsProblem))
            noticeUntil = Time.realtimeSinceStartup + 25;
        // Errors that come later show up in the window; the file is rewritten when the window opens and at quit.
    }

    private void RuleAnchorChain(List<ModFolder> enabled, Runner runner)
    {
        List<PluginEntry> acMods = Keel.Plugins.Where(p => p.Kind == PluginKind.AnchorChain && p.State == PluginState.Pending).ToList();
        if (acMods.Count == 0)
            return;
        if (GameState.AnchorChainRunning(null))
        {
            foreach (PluginEntry p in acMods)
                p.Stop(PluginState.LeftToAnchorChain, "Anchor Chain is running and starts this mod.");
            notes.Add("Anchor Chain is running: its mods are left to it.");
            return;
        }
        if (!loadAcMods.Value)
        {
            foreach (PluginEntry p in acMods)
                p.Stop(PluginState.Skipped, "An Anchor Chain mod; Keel's AnchorChainMods setting is off and Anchor Chain is not running.");
            return;
        }
        string acDll = enabled.SelectMany(f => ModFolders.FindFile(f.Path, "AnchorChain.dll")).FirstOrDefault();
        if (acDll == null)
        {
            foreach (PluginEntry p in acMods)
                p.Stop(PluginState.NotStarted,
                       "An Anchor Chain mod: subscribe to Anchor Chain on the Workshop and switch it on. Keel runs the mod, but its types live in Anchor Chain's DLL.");
            return;
        }
        runner.AnchorChainDll = acDll;
        notes.Add($"Anchor Chain is not running: Keel starts its mods (types from {acDll}).");
        foreach (PluginEntry p in acMods.Where(p => p.UsesAcConfig))
            p.Stop(PluginState.NotStarted, "Uses Anchor Chain's config files, which only Anchor Chain provides; it needs Anchor Chain running.");
    }

    private IEnumerator Check()
    {
        string mode = versionCheck.Value;
        if (mode == "Off")
            yield break;
        Preflight preflight;
        try
        {
            preflight = new Preflight(Paths.ManagedPath, Paths.BepInExAssemblyDirectory);
        }
        catch (Exception ex)
        {
            Warn($"Game version check unavailable: {ex.Message}");
            yield break;
        }
        using (preflight)
        {
            foreach (IGrouping<string, PluginEntry> dll in Keel.Plugins.Where(p => p.State == PluginState.Pending).GroupBy(p => p.Dll).ToList())
            {
                Preflight.Result r;
                try
                {
                    r = preflight.Check(dll.Key);
                }
                catch (Exception ex)
                {
                    Warn($"Could not check {dll.Key}: {ex.Message}");
                    continue;
                }
                foreach (PluginEntry p in dll)
                {
                    if (r.ClassLoad.Count > 0)
                        p.Stop(PluginState.NotStarted,
                               $"Made for another game version: this build has no {Report.Short(r.ClassLoad)}. Loading it would stop missions from loading, so Keel leaves it out.");
                    else if (r.Uses.Count > 0 && mode == "Strict")
                        p.Stop(PluginState.NotStarted, $"Made for another game version: this build has no {Report.Short(r.Uses)} (GameVersionCheck = Strict).");
                    else if (r.Uses.Count > 0)
                        p.Warnings.Add($"Uses {r.Uses.Count} thing(s) this game build lacks, e.g. {Report.Short(r.Uses)}. Fine if the mod checks the game version itself; " +
                                       "otherwise expect errors when that part runs.");
                }
                yield return null;
            }
        }
    }

    private void Warn(string message)
    {
        notes.Add(message);
        log.LogWarning(message);
    }

    private string Header() =>
        $"{Keel.Name} {Keel.Version} (stub {StubVersion}) – Sea Power, {GameState.BuildName} – {DateTime.Now:yyyy-MM-dd HH:mm}";

    private void WriteReport()
    {
        try
        {
            File.WriteAllText(reportPath, Report.Text(Keel.Plugins, Header(), notes));
        }
        catch (Exception ex)
        {
            log.LogWarning($"Cannot write {reportPath}: {ex.Message}");
        }
    }

    private void Update()
    {
        if (keelLog != null && Time.unscaledTime > nextFlush)
        {
            keelLog.Flush();
            nextFlush = Time.unscaledTime + 1f;
        }
    }

    private void OnApplicationQuit()
    {
        if (done)
            WriteReport();
        keelLog?.Flush();
    }

    private void ParseHotkey(string text)
    {
        keyCtrl = keyShift = keyAlt = false;
        foreach (string part in text.Split('+').Select(s => s.Trim()))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) keyCtrl = true;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) keyShift = true;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) keyAlt = true;
            else if (Enum.TryParse(part, true, out KeyCode k)) key = k;
        }
    }

    // ---- in-game report (IMGUI: drawn on top of the game's own UI, needs nothing from the game) ----

    private GUIStyle wrap, small, title;

    private void OnGUI()
    {
        Event e = Event.current;
        if (e.type == EventType.KeyDown && e.keyCode == key && e.control == keyCtrl && e.shift == keyShift && e.alt == keyAlt)
        {
            showWindow = !showWindow;
            noticeUntil = 0;
            if (showWindow && done)
                WriteReport();
            e.Use();
        }
        wrap ??= new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true };
        small ??= new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true, fontSize = 11 };
        title ??= new GUIStyle(GUI.skin.label) { richText = true, fontSize = 15 };

        if (!showWindow && Time.realtimeSinceStartup < noticeUntil)
        {
            int bad = Keel.Plugins.Count(Report.IsProblem);
            var r = new Rect(20, Screen.height - 90, 470, 70);
            GUI.Box(r, GUIContent.none);
            GUI.Label(new Rect(r.x + 10, r.y + 6, r.width - 20, r.height - 12),
                      $"<b>{Keel.Name}</b>: {bad} mod(s) did not start or threw errors.\n{Report.Summary(Keel.Plugins)}.\n<b>{hotkey.Value}</b> shows why.", wrap);
        }
        if (showWindow)
            window = GUI.Window(0x4B45454C, window, DrawWindow, $"{Keel.Name} {Keel.Version} – {GameState.BuildName}");
    }

    private static string Color(PluginState s) => s switch
    {
        PluginState.Running => "#7fd67f",
        PluginState.RunningWithErrors => "#f0c050",
        PluginState.Failed or PluginState.NotStarted => "#ff6b6b",
        PluginState.LeftToAnchorChain => "#7fb8f0",
        _ => "#a0a0a0",
    };

    private void DrawWindow(int id)
    {
        GUILayout.Label(done ? $"<b>{Report.Summary(Keel.Plugins)}</b>" : "Still starting mods …", title);
        foreach (string n in notes)
            GUILayout.Label(n, small);
        scroll = GUILayout.BeginScrollView(scroll);
        foreach (PluginEntry p in Report.Sorted(Keel.Plugins))
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color={Color(p.State)}><b>{Report.Tag(p.State)}</b></color>", wrap, GUILayout.Width(110));
            GUILayout.BeginVertical();
            GUILayout.Label($"<b>{p.Display}</b> {p.Version}   <size=11><color=#a0a0a0>{p.Folder?.Label} · {Path.GetFileName(p.Dll)} · {p.Guid}</color></size>", wrap);
            if (!string.IsNullOrEmpty(p.Reason))
                GUILayout.Label(p.Reason, small);
            foreach (string w in p.Warnings)
                GUILayout.Label($"<color=#f0c050>{w}</color>", small);
            if (p.LaterErrors > 0)
                GUILayout.Label($"<color=#f0c050>{p.LaterErrors} error(s) since it started; first: {p.FirstLaterError}</color>", small);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
        }
        if (Keel.Plugins.Count == 0)
            GUILayout.Label(done ? "No mod with a plugin is switched on." : "", wrap);
        GUILayout.EndScrollView();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Copy report", GUILayout.Width(140)))
            GUIUtility.systemCopyBuffer = Report.Text(Keel.Plugins, Header(), notes);
        GUILayout.Label($"<size=11>Also in {reportPath}</size>", wrap);
        if (GUILayout.Button("Close", GUILayout.Width(100)))
            showWindow = false;
        GUILayout.EndHorizontal();
        GUI.DragWindow(new Rect(0, 0, 10000, 22));
    }
}
