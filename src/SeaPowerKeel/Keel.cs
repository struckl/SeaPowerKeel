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
        // Awake runs inside AddComponent, so what it needs is handed over first.
        KeelHost.PendingStubVersion = stubVersion;
        KeelHost.PendingPath = path;
        var go = new GameObject(Name);
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<KeelHost>();
    }
}

internal sealed class KeelHost : MonoBehaviour
{
    internal static string PendingStubVersion, PendingPath;
    public string StubVersion = PendingStubVersion;
    public string OwnPath = PendingPath;
    private bool noticeArmed;
    private bool wantNotice;

    private ManualLogSource log;
    private ConfigEntry<bool> loadAcMods;
    private ConfigEntry<string> versionCheck;
    private ConfigEntry<string> disabled;
    private ConfigEntry<string> hotkey;
    private ConfigEntry<bool> notice;
    private ConfigEntry<float> uiScale;
    private KeelLog keelLog;
    private CrashGuard crash;
    private ConfigEntry<bool> holdCrashers;
    private string crashNotice;
    private float nextFlush;
    private readonly List<string> notes = new();
    private bool done;
    private readonly List<SkippedDll> skippedDlls = new();
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
        Runner.GuardPatches = config.Bind("Loading", "RemoveBrokenPatches", true,
            "When the game throws 'Method not found' / 'Field not found' because a mod's Harmony patch uses something this game build lacks, Keel takes that mod's patch off the failing " +
            "game method again, names the mod in the report and keeps the game going (instead of the same error popup on every frame). Off: Keel only names the mod.").Value;
        holdCrashers = config.Bind("Loading", "HoldOutCrashers", true,
            "When the game dies while Keel is loading or starting a mod, or a crash report shows a mod's code on its stack, Keel names that mod at the next start and keeps it out until the mod " +
            @"is updated (the list is BepInEx\Keel-held.txt). Off: Keel only names the mod.");
        try
        {
            crash = new CrashGuard(Paths.BepInExRootPath, Path.Combine(Path.GetDirectoryName(Application.consoleLogPath) ?? "", "Player-prev.log"));
        }
        catch (Exception ex)
        {
            log.LogWarning($"No crash journal this session: {ex.Message}");
        }
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
        uiScale = config.Bind("Report", "Scale", 0f, "Size of the report window and notice; 0 = by screen height (2 on a 4K screen).");
        ParseHotkey(hotkey.Value);
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
        {
            if (mode == UnityEngine.SceneManagement.LoadSceneMode.Single) Loading.LoadingWindow.SceneLoadedTicks = DateTime.UtcNow.Ticks;
            crash?.Mark("scene", scene.name);
        };
        reportPath = Path.Combine(Paths.BepInExRootPath, "Keel-report.txt");
        Loading.LoadingWindow.Start(config, log);      // first, so it opens before the slow steps that follow (see docs/loading-window.md)
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
        else
        {
            log.LogInfo($"The game's mod list is there (waited {watch.Elapsed.TotalSeconds:0.0} s).");
        }

        // 2. On the globe build, after the start-up load screen: mods started during it can keep the unit data from loading.
        if (GameState.Globe)
        {
            watch.Restart();
            while (!GameState.StartupDone() && watch.Elapsed.TotalSeconds < 180)
                yield return new WaitForSecondsRealtime(0.25f);
            log.LogInfo($"Start-up load screen done (waited {watch.Elapsed.TotalSeconds:0.0} s).");
        }

        List<ModFolder> enabled = folders.Where(f => f.Enabled).ToList();
        log.LogInfo($"{enabled.Count} of {folders.Count} mod folders are on.");

        // 3. Find the plugins (metadata only).
        watch.Restart();
        Keel.Plugins.AddRange(Scanner.Scan(enabled, Warn, skippedDlls, Paths.ManagedPath, Paths.BepInExAssemblyDirectory));
        log.LogInfo($"Found {Keel.Plugins.Count} plugin(s) in {Keel.Plugins.Select(p => p.Dll).Distinct().Count()} DLL(s) ({watch.ElapsedMilliseconds} ms).");
        foreach (SkippedDll d in skippedDlls)
            log.LogInfo($"DLL left out: {d.Path} - {d.Reason}");
        // Before any mod is touched: what the last session left, so a mod that took the game down cannot do it again.
        crash?.Apply(Keel.Plugins, holdCrashers.Value, n =>
        {
            crashNotice = n;
            notes.Add(n);
            log.LogError(n);
        });
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
        watch.Restart();
        yield return Check();
        log.LogInfo($"Game-version check done ({watch.ElapsedMilliseconds} ms, frames included).");
        watch.Restart();

        // 5. Plan, then load everything, then start in order.
        var elsewhere = new HashSet<string>(Chainloader.PluginInfos.Keys);
        elsewhere.UnionWith(Keel.Plugins.Where(p => p.State == PluginState.LeftToAnchorChain).Select(p => p.Guid));
        var off = new HashSet<string>(disabled.Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
        List<PluginEntry> order = Planner.Plan(Keel.Plugins, elsewhere, off, Process.GetCurrentProcess().ProcessName);

        // Anchor Chain may have started in the meantime (the version check took frames): then it starts its mods, not Keel.
        if (LeaveToAnchorChain(runner, elsewhere, "before Keel loaded the mods"))
            order = Planner.Replan(Keel.Plugins, elsewhere);

        // 5a. What Anchor Chain's own loader would have set up for its mods: the folder list behind AnchorChainLoader.FindFile,
        // then the [ACConfig] mods' config files (Anchor Chain's first pass).
        PrimeAnchorChainLoader(runner, enabled, folders, order);
        PrepareAcConfigs(enabled, folders, order);
        if (order.Any(p => p.State != PluginState.Pending))
            order = Planner.Replan(Keel.Plugins, elsewhere);

        // 5b. Load every DLL and create every Anchor Chain mod before any of them starts (as Anchor Chain does: a mod that looks
        // for another mod's assembly when it starts finds it); then start in dependency order. One step per frame, which changes
        // nothing about the order (see Sequencer). Anchor Chain is looked for again right before the first start.
        var sequence = new Sequencer
        {
            Prepare = p =>
            {
                crash?.Mark("load", p.Guid);
                runner.Prepare(p);
            },
            Start = p =>
            {
                crash?.Mark("start", p.Guid);
                runner.Start(p);
            },
            // Logged before the start: a mod that holds the game up for seconds is named while it does (the loading window shows it).
            Starting = (p, index, total) => log.LogInfo($"STARTING {index}/{total}: {p.Display} {p.Version} ({p.Folder.Label})"),
            Replan = () => Planner.Replan(Keel.Plugins, elsewhere),
            HandOver = when => LeaveToAnchorChain(runner, elsewhere, when),
            Prepared = p =>
            {
                if (p.State == PluginState.Failed)
                    log.LogError($"{Report.Tag(p.State)}: {p.Display} {p.Version} ({p.Folder.Label}) - {p.Reason}");
            },
            Started = p => log.Log(p.State == PluginState.Running ? LogLevel.Info : LogLevel.Error,
                                   $"{Report.Tag(p.State)}: {p.Display} {p.Version} ({p.Folder.Label}){(p.Reason.Length > 0 ? " - " + p.Reason : "")}"),
        };
        foreach (PluginEntry _ in sequence.Run(order))
            yield return null;
        int started = sequence.StartedCount;
        foreach (PluginEntry p in Keel.Plugins.Where(p => p.State is PluginState.NotStarted))
            log.LogWarning($"NOT STARTED: {p.Display} {p.Version} ({p.Folder.Label}) – {p.Reason}");

        crash?.Mark("done", "");
        done = true;
        log.LogInfo($"Started {started} plugin(s) in {watch.ElapsedMilliseconds} ms, one per frame.");
        log.LogInfo($"Done: {Report.Summary(Keel.Plugins)}. Report: {reportPath} or {hotkey.Value} in the game.");
        WriteReport();
        // Shown once the main menu is up (the start-up load screen can last minutes), see Update.
        wantNotice = notice.Value && (crashNotice != null || Keel.Plugins.Any(Report.IsProblem));
        // Errors that come later show up in the window; the file is rewritten when the window opens and at quit.
    }

    private void RuleAnchorChain(List<ModFolder> enabled, Runner runner)
    {
        List<PluginEntry> acMods = Keel.Plugins.Where(p => p.Kind == PluginKind.AnchorChain && p.State == PluginState.Pending).ToList();
        if (acMods.Count == 0)
            return;
        if (runner.AnchorChainRunning())
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
        string acDll = enabled.SelectMany(f => ModFolders.FindFile(f.Path, "AnchorChain.dll")).FirstOrDefault() ?? BundledAnchorChain();
        if (acDll == null)
        {
            foreach (PluginEntry p in acMods)
                p.Stop(PluginState.NotStarted,
                       "An Anchor Chain mod, but Keel's copy of Anchor Chain's library is missing from its folder: verify Keel's files on the Workshop (unsubscribe and subscribe again).");
            return;
        }
        runner.AnchorChainDll = acDll;
        notes.Add($"Anchor Chain is not running: Keel starts its mods (types from {acDll}).");
    }

    // Anchor Chain started after Keel's first look: its mods are its own from then on (never started twice).
    private bool LeaveToAnchorChain(Runner runner, HashSet<string> elsewhere, string when)
    {
        List<PluginEntry> mine = Keel.Plugins.Where(p => p.Kind == PluginKind.AnchorChain && p.State == PluginState.Pending).ToList();
        if (mine.Count == 0 || !runner.AnchorChainRunning())
            return false;
        foreach (PluginEntry p in mine)
        {
            p.Stop(PluginState.LeftToAnchorChain, "Anchor Chain is running and starts this mod.");
            elsewhere.Add(p.Guid);
        }
        Warn($"Anchor Chain started {when}: its {mine.Count} mod(s) are left to it.");
        return true;
    }

    private void PrimeAnchorChainLoader(Runner runner, List<ModFolder> enabled, List<ModFolder> all, List<PluginEntry> order)
    {
        if (!order.Any(p => p.Kind == PluginKind.AnchorChain && p.State == PluginState.Pending))
            return;
        string streaming = GameState.StreamingAssets(out _);
        string why = runner.PrimeAnchorChainLoader(enabled.Concat(all.Except(enabled)).Select(f => f.Path), Path.Combine(streaming, AcConfig.ConfigFolder));
        if (why == null)
            log.LogInfo("AnchorChainLoader.FindFile now searches the mod folders (as under Anchor Chain).");
        else
            Warn("AnchorChainLoader.FindFile will find nothing, so mods that use it for files in other mods' folders (Sonar Audio Loader's fallback) cannot find them: " + why);
    }

    private void PrepareAcConfigs(List<ModFolder> enabled, List<ModFolder> all, List<PluginEntry> order)
    {
        List<PluginEntry> withConfig = order.Where(p => p.Kind == PluginKind.AnchorChain && p.UsesAcConfig && p.State == PluginState.Pending).ToList();
        if (withConfig.Count == 0)
            return;
        string streaming = GameState.StreamingAssets(out string how);
        var config = new AcConfig(GameState.IniHandlerType, Path.Combine(streaming, AcConfig.ConfigFolder),
                                  enabled.Concat(all.Except(enabled)).Select(f => f.Path),
                                  log.LogInfo, log.LogWarning, log.LogError);
        log.LogInfo($"[ACConfig] {withConfig.Count} mod(s) use config files; player files go to {config.ConfigDir} (from {how}).");
        if (config.MissingMembers.Count > 0)
            Warn("[ACConfig] This game build's IniHandler lacks: " + string.Join(", ", config.MissingMembers) + ". Keel handles it with a fallback (see the mods' warnings).");
        foreach (PluginEntry p in withConfig)
        {
            if (!config.Load(p, out string reason))
                p.Stop(PluginState.NotStarted, reason);
        }
    }

    // Anchor Chain's library (MIT), shipped with Keel so its mods run without the Anchor Chain item; a subscribed copy is preferred.
    private static string BundledAnchorChain()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Keel).Assembly.Location) ?? "", "lib", "AnchorChain.keel.bin");
        return System.IO.File.Exists(path) ? path : null;
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
                    string declared = InfoVersion.MismatchInFolder(p.Folder?.Path, Application.version);
                    if (declared != null && mode == "Strict" && r.ClassLoad.Count == 0 && r.Uses.Count == 0 && r.Lookups.Count == 0)
                        p.Stop(PluginState.NotStarted, $"Made for another game version. {declared} (GameVersionCheck = Strict.)");
                    else if (r.ClassLoad.Count > 0)
                        p.Stop(PluginState.NotStarted,
                               $"Made for another game version: this build has no {Report.Short(r.ClassLoad)}. Loading it would stop missions from loading, so Keel leaves it out.");
                    else if ((r.Uses.Count > 0 || r.Lookups.Count > 0) && mode == "Strict")
                        p.Stop(PluginState.NotStarted, $"Made for another game version: this build has no {Report.Short(r.Uses.Concat(r.Lookups).ToList())} (GameVersionCheck = Strict).");
                    else
                    {
                        if (declared != null)
                            p.Warnings.Add(declared);
                        p.MissingUses.AddRange(r.Uses);
                        if (r.Uses.Count > 0)
                            p.Warnings.Add($"Uses {r.Uses.Count} thing(s) this game build lacks, e.g. {Report.Short(r.Uses)}. Fine if the mod checks the game version itself; " +
                                           "otherwise expect errors when that part runs.");
                        if (r.Lookups.Count > 0)
                            p.Warnings.Add($"Looks up {r.Lookups.Count} type(s)/method(s) by name that this game build lacks, e.g. {Report.Short(r.Lookups)} (Harmony patch targets, AccessTools and reflection lookups). " +
                                           "That code cannot find its target here: expect errors from the mod, or a part of it that does nothing. (Keel does not check whether a transpiler still matches.)");
                    }
                }
                yield return null;
            }
            if (preflight.IndexMs > 0)
                log.LogInfo($"Index of the game's type names for by-name lookups: {preflight.IndexMs} ms.");
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
            File.WriteAllText(reportPath, Report.Text(Keel.Plugins, Header(), notes, skippedDlls));
        }
        catch (Exception ex)
        {
            log.LogWarning($"Cannot write {reportPath}: {ex.Message}");
        }
    }

    private void ToggleWindow()
    {
        // The key can arrive both through IMGUI and through the polled keyboard in the same frame: count it once.
        if (Time.frameCount == lastToggleFrame)
            return;
        lastToggleFrame = Time.frameCount;
        showWindow = !showWindow;
        noticeUntil = 0;
        if (showWindow && done)
            WriteReport();
    }

    private int lastToggleFrame = -1;
    private bool keyWasDown;
    private Func<bool> hotkeyDown;
    private bool hotkeyProbed;

    /// <summary>
    /// The globe build does not always hand keys to IMGUI, so the hotkey is also read from the keyboard: through the Input
    /// System package where the game has it, else the old Input class. Found by name, so Keel references neither.
    /// </summary>
    private Func<bool> ProbeHotkey()
    {
        const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
        try
        {
            Type kbType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
            PropertyInfo current = kbType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            if (current != null)
            {
                string keyName = key.ToString();
                keyName = char.ToLowerInvariant(keyName[0]) + keyName.Substring(1) + "Key";
                bool Pressed(object kb, string name) =>
                    kb.GetType().GetProperty(name, pub)?.GetValue(kb) is object control &&
                    control.GetType().GetProperty("isPressed", pub)?.GetValue(control) is bool b && b;
                return () =>
                {
                    object kb = current.GetValue(null);
                    return kb != null && Pressed(kb, keyName)
                        && (!keyCtrl || Pressed(kb, "ctrlKey")) && (!keyShift || Pressed(kb, "shiftKey")) && (!keyAlt || Pressed(kb, "altKey"));
                };
            }
        }
        catch (Exception)
        {
            // no Input System package: try the old input
        }
        try
        {
            MethodInfo getKey = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule")?.GetMethod("GetKey", new[] { typeof(KeyCode) });
            if (getKey != null)
            {
                bool Down(KeyCode k) => (bool)getKey.Invoke(null, new object[] { k });
                Down(key);   // throws right away when the game has the old input switched off
                return () => Down(key)
                             && (!keyCtrl || Down(KeyCode.LeftControl) || Down(KeyCode.RightControl))
                             && (!keyShift || Down(KeyCode.LeftShift) || Down(KeyCode.RightShift))
                             && (!keyAlt || Down(KeyCode.LeftAlt) || Down(KeyCode.RightAlt));
            }
        }
        catch (Exception)
        {
            // old input switched off
        }
        return null;
    }

    // ---- the report's mouse and Esc, read from the device ----
    // Like the hotkey: the globe build does not reliably hand clicks and keys to IMGUI, so the report's buttons did nothing. Update
    // reads the mouse button, the pointer and Esc straight from the Input System (else the old Input) and hit-tests the two
    // buttons itself; IMGUI's own handling stays as it was.

    private Rect closeRect, copyRect;      // in the report's GUI units (screen pixels / scale), set while the report draws
    private float guiScale = 1f;
    private Func<(bool click, Vector2 pos, bool esc)> pointer;
    private bool pointerProbed;

    private Func<(bool click, Vector2 pos, bool esc)> ProbePointer()
    {
        const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
        try
        {
            Type mouseType = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
            Type kbType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
            PropertyInfo mouseNow = mouseType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            PropertyInfo kbNow = kbType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            if (mouseNow != null && kbNow != null)
            {
                return () =>
                {
                    object m = mouseNow.GetValue(null), k = kbNow.GetValue(null);
                    bool click = false, esc = false;
                    Vector2 pos = default;
                    if (m != null)
                    {
                        object left = m.GetType().GetProperty("leftButton", pub)?.GetValue(m);
                        click = left?.GetType().GetProperty("wasPressedThisFrame", pub)?.GetValue(left) is bool c && c;
                        object p = m.GetType().GetProperty("position", pub)?.GetValue(m);
                        if (p?.GetType().GetMethod("ReadValue", Type.EmptyTypes)?.Invoke(p, null) is Vector2 v) pos = v;
                    }
                    if (k != null)
                    {
                        object escKey = k.GetType().GetProperty("escapeKey", pub)?.GetValue(k);
                        esc = escKey?.GetType().GetProperty("wasPressedThisFrame", pub)?.GetValue(escKey) is bool e && e;
                    }
                    return (click, pos, esc);
                };
            }
        }
        catch (Exception)
        {
            // no Input System package: try the old input
        }
        try
        {
            Type input = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule");
            MethodInfo mouseDown = input?.GetMethod("GetMouseButtonDown", new[] { typeof(int) });
            MethodInfo keyDown = input?.GetMethod("GetKeyDown", new[] { typeof(KeyCode) });
            PropertyInfo mousePos = input?.GetProperty("mousePosition", BindingFlags.Public | BindingFlags.Static);
            if (mouseDown != null && keyDown != null && mousePos != null)
            {
                mouseDown.Invoke(null, new object[] { 0 });      // throws right away when the old input is switched off
                return () => ((bool)mouseDown.Invoke(null, new object[] { 0 }), (Vector3)mousePos.GetValue(null),
                              (bool)keyDown.Invoke(null, new object[] { KeyCode.Escape }));
            }
        }
        catch (Exception)
        {
            // old input switched off
        }
        return null;
    }

    private void PollReportInput()
    {
        if (!showWindow) return;
        if (!pointerProbed) { pointerProbed = true; pointer = ProbePointer(); }
        if (pointer == null) return;
        try
        {
            var (click, pos, esc) = pointer();
            if (esc) { showWindow = false; return; }
            if (!click) return;
            var gui = new Vector2(pos.x / guiScale, (Screen.height - pos.y) / guiScale);       // the pointer's y runs from the bottom
            if (closeRect.Contains(gui)) showWindow = false;
            else if (copyRect.Contains(gui)) GUIUtility.systemCopyBuffer = Report.Text(Keel.Plugins, Header(), notes, skippedDlls);
        }
        catch (Exception) { pointer = null; }
    }

    private float nextMissionCheck;

    private void Update()
    {
        PollReportInput();
        if (!hotkeyProbed)
        {
            hotkeyProbed = true;
            hotkeyDown = ProbeHotkey();
        }
        if (hotkeyDown != null)
        {
            bool down;
            try
            {
                down = hotkeyDown();
            }
            catch (Exception)
            {
                down = false;
                hotkeyDown = null;
            }
            if (down && !keyWasDown)
                ToggleWindow();
            keyWasDown = down;
        }
        if (Time.unscaledTime > nextMissionCheck)
        {
            nextMissionCheck = Time.unscaledTime + 0.5f;
            Loading.LoadingWindow.MissionRunning = GameState.InMission();
            Loading.LoadingWindow.OnMenuSubPage = GameState.OnMenuSubPage();     // the summary belongs to the main menu's top page only
        }
        if (Loading.LoadingWindow.ReportRequested)      // a click on the loading window's summary
        {
            Loading.LoadingWindow.ReportRequested = false;
            ToggleWindow();      // a click on the summary opens the report, the next one closes it
        }
        if (Time.unscaledTime > nextFlush)
        {
            nextFlush = Time.unscaledTime + 1f;
            keelLog?.Flush();
            if (wantNotice && !noticeArmed && GameState.StartupDone())
            {
                noticeArmed = true;
                noticeUntil = Time.realtimeSinceStartup + 30;
            }
        }
    }

    private void OnApplicationQuit()
    {
        crash?.Mark("quit", "");
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
            ToggleWindow();
            e.Use();
        }
        if (showWindow && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { showWindow = false; e.Use(); }      // Esc closes the report
        // IMGUI draws in screen pixels: scale it so the report reads the same on 1080p and 4K (Report.Scale, 0 = by screen height).
        float scale = uiScale.Value > 0 ? uiScale.Value : Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        guiScale = scale;
        float screenW = Screen.width / scale, screenH = Screen.height / scale;
        wrap ??= new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true };
        small ??= new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true, fontSize = 11 };
        title ??= new GUIStyle(GUI.skin.label) { richText = true, fontSize = 15 };

        if (!showWindow && Time.realtimeSinceStartup < noticeUntil)
        {
            int bad = Keel.Plugins.Count(Report.IsProblem);
            // Above the loading summary card, which sits at the bottom left of the main menu for the same half minute.
            float height = crashNotice != null ? 170 : 70;
            var r = new Rect(20, screenH - 135 - height, 470, height);
            GUI.Box(r, GUIContent.none);
            GUI.Label(new Rect(r.x + 10, r.y + 6, r.width - 20, r.height - 12),
                      (crashNotice != null ? $"<b>{crashNotice}</b>\n" : "") +
                      $"<b>{Keel.Name}</b>: {bad} mod(s) did not start or threw errors.\n{Report.Summary(Keel.Plugins)}.\n<b>{hotkey.Value}</b> shows why.", wrap);
        }
        if (showWindow)
        {
            window.width = Mathf.Min(960, screenW - 40);
            window.height = Mathf.Min(600, screenH - 40);
            window.x = Mathf.Clamp(window.x, 0, screenW - window.width);
            window.y = Mathf.Clamp(window.y, 0, screenH - window.height);
        }
        if (showWindow)
        {
            // IMGUI's window is see-through; the game's menu text behind it made the report hard to read.
            Color before = GUI.color;
            GUI.color = new Color(0.07f, 0.09f, 0.12f, 0.97f);
            GUI.DrawTexture(window, Texture2D.whiteTexture);
            GUI.color = before;
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
        if (skippedDlls.Count > 0)
        {
            GUILayout.Space(6);
            GUILayout.Label($"<b>DLLs left out on purpose ({skippedDlls.Count})</b>", wrap);
            foreach (SkippedDll d in skippedDlls)
                GUILayout.Label($"{Path.GetFileName(d.Path)} <color=#a0a0a0>({d.Folder?.Label})</color>: {d.Reason}", small);
        }
        GUILayout.EndScrollView();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Copy report", GUILayout.Width(140)))
            GUIUtility.systemCopyBuffer = Report.Text(Keel.Plugins, Header(), notes, skippedDlls);
        if (Event.current.type == EventType.Repaint)
        {
            Rect r = GUILayoutUtility.GetLastRect();
            copyRect = new Rect(window.x + r.x, window.y + r.y, r.width, r.height);
        }
        // A fixed width: a wrapping label in a row takes all the width a long path wants and pushed Close out of the window.
        GUILayout.Label($"<size=11>Also in {reportPath}</size>", small, GUILayout.Width(Mathf.Max(60f, window.width - 140f - 100f - 40f)));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Close", GUILayout.Width(100)))
            showWindow = false;
        if (Event.current.type == EventType.Repaint)
        {
            Rect r = GUILayoutUtility.GetLastRect();
            closeRect = new Rect(window.x + r.x, window.y + r.y, r.width, r.height);
        }
        GUILayout.EndHorizontal();
        GUI.DragWindow(new Rect(0, 0, 10000, 22));
    }
}
