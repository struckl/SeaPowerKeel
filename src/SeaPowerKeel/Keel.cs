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
    private bool heldClicked;
    private Rect heldRect;
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
        // As early as possible: a crash in the game's own start-up load is caught too, and the files it opens are on the list.
        if (crash != null)
        {
            string catcher = CrashCatcher.Install(Path.Combine(Paths.BepInExRootPath, CrashText.StackFile));
            if (catcher != null)
                log.LogWarning($"Crashes will not be caught this session: {catcher}");
            string trail = FileTrail.Install();
            if (trail != null)
                log.LogWarning($"Keel will not see which file the game was loading in a crash: {trail}");
            StartWatcher();
        }
        // Here, in Awake: the game has not read its mod list yet.
        try
        {
            string packOff = ModList.ApplyPackOff(Paths.BepInExRootPath, ModFolders.UserSettings);
            if (packOff != null)
            {
                string off = $"{FolderLabel(packOff)} was switched off after the last crash. Switch it on again in the Mod Manager to try it after an update.";
                crashNotice = crashNotice == null ? off : crashNotice + "\n" + off;
                notes.Add(off);
                log.LogWarning(off);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning($"Could not switch the pack off: {ex.Message}");
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
            crashNotice = crashNotice == null ? n : crashNotice + "\n" + n;
            notes.Add(n);
            log.LogError(n);
        });
        if (crash != null)
        {
            if (crash.Short != null)
                Loading.LoadingWindow.Alert = crash.Short;
            crash.WriteModsFile(Keel.Plugins);
        }
        // On its own thread: on a first start it reads the head of every picture and sound in every mod.
        string packCache = Path.Combine(Paths.BepInExRootPath, "Keel-packcheck.txt");
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                packProblems = PackCheck.Scan(enabled, packCache);
            }
            catch (Exception)
            {
                packProblems = new List<string>();
            }
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
        // The summary card says it, and a click on it opens the report; the notice is for players who switched that window off.
        wantNotice = notice.Value && !Loading.LoadingWindow.Shown && (crashNotice != null || Keel.Plugins.Any(Report.IsProblem));
        // Errors that come later show up in the window; the file is rewritten when the window opens and at quit.
    }

    // The window a player sees when the game dies has to come from outside the game.
    private void StartWatcher()
    {
        try
        {
            string exe = Path.Combine(Path.GetDirectoryName(OwnPath) ?? "", "KeelWatcher.exe");
            if (!File.Exists(exe))
            {
                log.LogWarning($"No crash window this session: {exe} is missing.");
                return;
            }
            string game = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            Process.Start(new ProcessStartInfo(exe, $"{Process.GetCurrentProcess().Id} \"{Paths.BepInExRootPath}\" \"{game}\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (Exception ex)
        {
            log.LogWarning($"No crash window this session: {ex.GetType().Name}: {ex.Message}");
        }
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
                    else if (r.Fatal.Count > 0)
                        p.Stop(PluginState.NotStarted,
                               $"A patch of this mod does not fit the game and would read or write the wrong memory: {Report.Short(r.Fatal)}. That can crash the game, so Keel leaves it out.");
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
            else if (logsRect.Contains(gui)) SaveLogs();
            else if (HeldOffered && heldRect.Contains(gui)) HeldButton();
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
            CrashCatcher.KeepInstalled();
            TakePackProblems();
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
        if (!showWindow)
        {
            windowCentered = false;
            return;
        }
        window.width = Mathf.Min(900, screenW - 40);
        window.height = Mathf.Min(660, screenH - 40);
        if (!windowCentered)
        {
            windowCentered = true;
            window.x = (screenW - window.width) / 2f;
            window.y = (screenH - window.height) / 2f;
        }
        window.x = Mathf.Clamp(window.x, 0, screenW - window.width);
        window.y = Mathf.Clamp(window.y, 0, screenH - window.height);
        EnsureStyles();
        GUI.DrawTexture(new Rect(0, 0, screenW, screenH), shade);
        GUI.DrawTexture(new Rect(window.x - 1, window.y - 1, window.width + 2, window.height + 2), edge);
        window = GUI.Window(0x4B45454C, window, DrawWindow, GUIContent.none, windowStyle);
    }

    private bool HeldOffered => crash is { HeldActive: true };

    private volatile List<string> packProblems;
    private float lastButton = -10f;
    private bool restarting;

    // A click can arrive through IMGUI and through the device check alike.
    private bool FreshClick()
    {
        if (Time.realtimeSinceStartup - lastButton < 0.7f)
            return false;
        lastButton = Time.realtimeSinceStartup;
        return true;
    }

    private void TakePackProblems()
    {
        List<string> found = packProblems;
        if (found == null)
            return;
        packProblems = null;
        foreach (string problem in found.Take(20))
            Warn("Broken file: " + problem);
        if (found.Count > 20)
            Warn($"... and {found.Count - 20} more broken file(s).");
        if (found.Count > 0 && done)
            WriteReport();
    }

    private void RestartGame()
    {
        if (restarting)
            return;
        restarting = true;
        try
        {
            string exe = Path.Combine(Path.GetDirectoryName(OwnPath) ?? "", "KeelWatcher.exe");
            string game = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            Process.Start(new ProcessStartInfo(exe, $"--relaunch {Process.GetCurrentProcess().Id} \"{game}\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (Exception ex)
        {
            log.LogWarning($"Could not restart the game by itself: {ex.Message}");
            notes.Add("Keel could not restart the game: please exit and start it yourself.");
            restarting = false;
            return;
        }
        Application.Quit();
    }

    private void HeldButton()
    {
        if (!HeldOffered || !FreshClick())
            return;
        if (heldClicked)
        {
            RestartGame();
            return;
        }
        heldClicked = true;
        crash.ClearHeld();
        notes.Add("The held mods run again from the next start: exit and start the game.");
    }

    private Rect logsRect;
    private float logsAt = -10f;

    // The zip is made outside the game, by the same program as the crash window.
    private void SaveLogs()
    {
        if (Time.realtimeSinceStartup - logsAt < 3f)
            return;     // the game may deliver the click through IMGUI and through the device check alike
        logsAt = Time.realtimeSinceStartup;
        WriteReport();
        keelLog?.Flush();
        try
        {
            string exe = Path.Combine(Path.GetDirectoryName(OwnPath) ?? "", "KeelWatcher.exe");
            Process.Start(new ProcessStartInfo(exe, $"--logs \"{Paths.BepInExRootPath}\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            notes.Add("Your logs are being saved as a zip on your Desktop: attach that file to your bug report.");
        }
        catch (Exception ex)
        {
            notes.Add($"Could not save the logs: {ex.Message}");
        }
    }

    // The name a mod folder shows in the game's mod list.
    private static string FolderLabel(string folder)
    {
        foreach (string root in new[] { ModFolders.WorkshopRoot, Application.streamingAssetsPath })
        {
            try
            {
                string ini = Path.Combine(root, folder, "_info.ini");
                string name = File.Exists(ini) ? File.ReadLines(ini).FirstOrDefault(l => l.StartsWith("Name=", StringComparison.OrdinalIgnoreCase))?.Substring(5).Trim() : null;
                if (!string.IsNullOrEmpty(name))
                    return $"{name} ({folder})";
            }
            catch (Exception)
            {
                // the folder name will do
            }
        }
        return folder;
    }

    // ---- the look of the report ----

    private bool windowCentered;
    private GUIStyle windowStyle, headStyle, metaStyle, statsStyle, lineStyle, alertStyle, noteStyle, groupStyle, rowStyle, nameStyle, sourceStyle, bodyStyle, warnStyle, buttonStyle;
    private readonly Dictionary<PluginState, GUIStyle> pills = new();
    private Texture2D shade, edge, track, thumb;

    private static UnityEngine.Color Hex(string html) => ColorUtility.TryParseHtmlString(html, out UnityEngine.Color c) ? c : UnityEngine.Color.magenta;

    private static Texture2D Solid(string html, float alpha = 1f)
    {
        var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Color c = Hex(html);
        c.a = alpha;
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    private static void Paint(GUIStyle style, Texture2D background, UnityEngine.Color text)
    {
        foreach (GUIStyleState state in new[] { style.normal, style.hover, style.active, style.focused, style.onNormal, style.onHover, style.onActive, style.onFocused })
        {
            state.background = background;
            state.textColor = text;
        }
    }

    private static GUIStyle Text(int size, string color, FontStyle font = FontStyle.Normal, int below = 0)
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = font, wordWrap = true, richText = true, margin = new RectOffset(0, 0, 0, below), padding = new RectOffset(0, 0, 0, 0) };
        Paint(style, null, Hex(color));
        return style;
    }

    private void EnsureStyles()
    {
        if (windowStyle != null && edge != null)
            return;
        shade = Solid("#000000", 0.5f);
        edge = Solid("#39424f");
        track = Solid("#1b2028");
        thumb = Solid("#465160");
        windowStyle = new GUIStyle(GUI.skin.window) { padding = new RectOffset(22, 22, 18, 18), border = new RectOffset(0, 0, 0, 0), contentOffset = Vector2.zero };
        Paint(windowStyle, Solid("#14181f"), UnityEngine.Color.white);
        headStyle = Text(20, "#f2f5f8", FontStyle.Bold);
        metaStyle = Text(12, "#8b95a3");
        metaStyle.alignment = TextAnchor.MiddleRight;
        metaStyle.wordWrap = false;
        statsStyle = Text(14, "#d7dde5", FontStyle.Normal, 10);
        lineStyle = new GUIStyle { margin = new RectOffset(0, 0, 0, 12) };
        lineStyle.normal.background = edge;
        alertStyle = Text(13, "#ffd98a", FontStyle.Normal, 8);
        alertStyle.padding = new RectOffset(12, 12, 9, 9);
        alertStyle.normal.background = Solid("#3a2f17");
        noteStyle = Text(12, "#8b95a3", FontStyle.Normal, 6);
        groupStyle = Text(12, "#8b95a3", FontStyle.Bold, 6);
        rowStyle = new GUIStyle { padding = new RectOffset(12, 12, 9, 9), margin = new RectOffset(0, 8, 0, 6) };
        rowStyle.normal.background = Solid("#1d232c");
        nameStyle = Text(14, "#f2f5f8", FontStyle.Normal, 1);
        sourceStyle = Text(11, "#7f8a98");
        bodyStyle = Text(12, "#ffb4ae");
        bodyStyle.margin = new RectOffset(0, 0, 5, 0);
        warnStyle = Text(12, "#e8c56f");
        warnStyle.margin = new RectOffset(0, 0, 5, 0);
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, alignment = TextAnchor.MiddleCenter, border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 8, 0, 0), padding = new RectOffset(8, 8, 0, 0) };
        Paint(buttonStyle, Solid("#2a323d"), Hex("#e8ecf1"));
        buttonStyle.hover.background = buttonStyle.onHover.background = Solid("#3a4552");
        buttonStyle.active.background = buttonStyle.onActive.background = Solid("#4a5766");
        pills.Clear();
        foreach ((PluginState state, string color) in new[]
        {
            (PluginState.Running, "#2c7a4d"), (PluginState.RunningWithErrors, "#a87a12"), (PluginState.Failed, "#b83c36"), (PluginState.NotStarted, "#b83c36"),
            (PluginState.Skipped, "#4a5361"), (PluginState.LeftToAnchorChain, "#2f6aa8"), (PluginState.Pending, "#4a5361"),
        })
        {
            var pill = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, margin = new RectOffset(0, 12, 1, 0) };
            pill.normal.background = Solid(color);
            pill.normal.textColor = UnityEngine.Color.white;
            pills[state] = pill;
        }
    }

    private static string Coloured(string summary) => string.Join("   ·   ", summary.Split(new[] { ", " }, StringSplitOptions.None).Select(part =>
    {
        string color = part.Contains("failed") || part.Contains("not started") ? "#ff8f88"
                     : part.Contains("errors") ? "#e8c56f"
                     : part.Contains("Anchor") ? "#8cc0f2"
                     : "#86d99b";
        return $"<color={color}>{part}</color>";
    }));

    private static int GroupOf(PluginEntry p) => Report.IsProblem(p) ? 0 : p.State == PluginState.Running ? 1 : 2;

    private void Row(PluginEntry p)
    {
        GUILayout.BeginHorizontal(rowStyle);
        GUILayout.Label(Report.Tag(p.State), pills.TryGetValue(p.State, out GUIStyle pill) ? pill : pills[PluginState.Pending], GUILayout.Width(100), GUILayout.Height(22));
        GUILayout.BeginVertical();
        GUILayout.Label($"<b>{p.Display}</b>  <color=#8b95a3>{p.Version}</color>", nameStyle);
        GUILayout.Label($"{p.Folder?.Label}  ·  {Path.GetFileName(p.Dll)}", sourceStyle);
        if (!string.IsNullOrEmpty(p.Reason))
            GUILayout.Label(p.Reason, bodyStyle);
        foreach (string w in p.Warnings)
            GUILayout.Label(w, warnStyle);
        if (p.LaterErrors > 0)
            GUILayout.Label($"{p.LaterErrors} error(s) since it started; first: {p.FirstLaterError}", warnStyle);
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
    }

    private bool Button(string text, float width, ref Rect rect)
    {
        bool clicked = GUILayout.Button(text, buttonStyle, GUILayout.Width(width), GUILayout.Height(32));
        if (Event.current.type == EventType.Repaint)
        {
            Rect r = GUILayoutUtility.GetLastRect();
            rect = new Rect(window.x + r.x, window.y + r.y, r.width, r.height);
        }
        return clicked;
    }

    private void DrawWindow(int id)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("R.A.T.S. Keel", headStyle);
        GUILayout.FlexibleSpace();
        GUILayout.Label($"{Keel.Version}  ·  {GameState.BuildName}", metaStyle, GUILayout.Height(26));
        GUILayout.EndHorizontal();
        GUILayout.Space(4);
        GUILayout.Label(done ? Coloured(Report.Summary(Keel.Plugins)) : "Still starting mods …", statsStyle);
        GUILayout.Box(GUIContent.none, lineStyle, GUILayout.Height(1), GUILayout.ExpandWidth(true));

        foreach (string n in notes)
            GUILayout.Label(n, crashNotice != null && crashNotice.Contains(n) ? alertStyle : noteStyle);

        // The skin is shared with other mods: its scroll bar is restyled only while this window draws.
        GUIStyle bar = GUI.skin.verticalScrollbar, knob = GUI.skin.verticalScrollbarThumb;
        Texture2D barWas = bar.normal.background, knobWas = knob.normal.background;
        float barWidth = bar.fixedWidth, knobWidth = knob.fixedWidth;
        bar.normal.background = track;
        knob.normal.background = thumb;
        bar.fixedWidth = knob.fixedWidth = 8;
        scroll = GUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, bar, GUIStyle.none);
        int group = -1;
        foreach (PluginEntry p in Report.Sorted(Keel.Plugins))
        {
            if (GroupOf(p) != group)
            {
                group = GroupOf(p);
                if (group != 0)
                    GUILayout.Space(8);
                int count = Keel.Plugins.Count(x => GroupOf(x) == group);
                GUILayout.Label($"{(group == 0 ? "NEEDS ATTENTION" : group == 1 ? "RUNNING" : "OTHER")}  ({count})", groupStyle);
            }
            Row(p);
        }
        if (Keel.Plugins.Count == 0)
            GUILayout.Label(done ? "No mod with a plugin is switched on." : "", noteStyle);
        if (skippedDlls.Count > 0)
        {
            GUILayout.Space(8);
            GUILayout.Label($"DLLS LEFT OUT ON PURPOSE  ({skippedDlls.Count})", groupStyle);
            foreach (SkippedDll d in skippedDlls)
                GUILayout.Label($"<color=#d7dde5>{Path.GetFileName(d.Path)}</color>  ({d.Folder?.Label}): {d.Reason}", noteStyle);
        }
        GUILayout.Space(8);
        GUILayout.Label($"The same report is in {reportPath}", sourceStyle);
        GUILayout.EndScrollView();
        bar.normal.background = barWas;
        knob.normal.background = knobWas;
        bar.fixedWidth = barWidth;
        knob.fixedWidth = knobWidth;

        GUILayout.Space(12);
        GUILayout.BeginHorizontal();
        if (Button("Copy report", 120, ref copyRect))
            GUIUtility.systemCopyBuffer = Report.Text(Keel.Plugins, Header(), notes, skippedDlls);
        if (Button("Save logs", 110, ref logsRect))
            SaveLogs();
        if (HeldOffered && Button(heldClicked ? "Restart the game" : "Let held mods run", 160, ref heldRect))
            HeldButton();
        GUILayout.FlexibleSpace();
        if (Button("Close", 100, ref closeRect))
            showWindow = false;
        GUILayout.EndHorizontal();
        GUI.DragWindow(new Rect(0, 0, 10000, 56));
    }
}
