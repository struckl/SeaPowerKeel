using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using AnchorChain;
using SeaPowerKeel;

// Keel's own tests, run outside the game: dotnet run -c Release --project tools/offline-check -- --selftest <Managed> <BepInEx core>
// The mods tested are classes of this very assembly (see the AnchorChain namespace below): the scanner reads DLLs through
// Mono.Cecil without running them, so a copy of this DLL in a temp folder is a mod folder. Nothing of the game is started.
internal static class SelfTest
{
    private static int failures, checks;

    private static void Check(bool ok, string what)
    {
        checks++;
        if (!ok)
            failures++;
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
    }

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: --selftest <Managed> <BepInEx core>");
            return 2;
        }
        string managed = args[0], core = args[1];
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string p = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        string tmp = Path.Combine(Path.GetTempPath(), "keel-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            Console.WriteLine("1. Scanner: only the exact Anchor Chain DLL names are left out, and every one is listed");
            ScannerExclusions(tmp, managed, core);
            Console.WriteLine("3. Scanner: Anchor Chain plugin classes must be public (exported)");
            ExportedTypes(tmp, managed, core);
            Console.WriteLine("2. Entry point through the interface");
            EntryPoints();
            Console.WriteLine("4+6. Sequence: load all first, start in plan order, drop dependants of a failed load, hand over to a running Anchor Chain");
            Sequence();
            Console.WriteLine("6. Is Anchor Chain running: Keel's own load of its DLL does not count");
            AnchorChainRunning();
            Console.WriteLine("5. [ACConfig]");
            Config(tmp);
            Console.WriteLine("8. Mods that call AnchorChainLoader.FindFile (Sonar Audio Loader) when Keel runs the mods");
            LoaderShim(tmp);
            Console.WriteLine("7. [ACIncompatibility]: Keel's behaviour, said in the report");
            Incompatibility();
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception) { }
        }
        Console.WriteLine();
        Console.WriteLine($"{checks - failures} of {checks} checks passed" + (failures > 0 ? $", {failures} FAILED" : ""));
        return failures > 0 ? 1 : 0;
    }

    /// <summary>
    /// Does the game's real <c>SeaPower.IniHandler</c> have what [ACConfig] needs? One Managed folder per run (the game
    /// assembly cannot be loaded twice in one process). Reads the types by reflection only; nothing of the game runs.
    /// </summary>
    public static int IniCheck(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine("usage: --inicheck <Managed of one game build>");
            return 2;
        }
        string managed = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string p = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        Assembly game = Assembly.LoadFrom(Path.Combine(managed, "Seapower-Scripts.dll"));
        Console.WriteLine($"Game build: {(game.GetType("SeapowerUI.MapDisplayState") != null ? "3D globe map" : "flat map (0.8.7)")} ({managed})");
        Type ini = game.GetType("SeaPower.IniHandler");
        if (ini == null)
        {
            Console.WriteLine("  FAIL  SeaPower.IniHandler not found");
            return 1;
        }
        var api = new IniApi(ini);
        Console.WriteLine("  open overload used:  " + api.OpenSignature);
        Console.WriteLine("  can merge configs:   " + api.CanMerge + ";  can apply ResetValues: " + api.CanReset);
        Console.WriteLine("  missing members:     " + (api.Missing.Count == 0 ? "none" : string.Join("; ", api.Missing)));
        FieldInfo streaming = game.GetType("SeaPower.Globals")?.GetField("_streamingAssetsPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Console.WriteLine("  Globals._streamingAssetsPath: " + (streaming == null ? "MISSING (Keel falls back to Application.streamingAssetsPath)" : streaming.FieldType.Name));
        Console.WriteLine("  Data return type:    " + (ini.GetProperty("Data")?.PropertyType.ToString() ?? "(none)"));
        return api.Missing.Any(m => !m.Contains("string overload")) ? 1 : 0;
    }

    // ---- helpers ----

    private static string ThisDll => typeof(SelfTest).Assembly.Location;

    private static ModFolder Folder(string path, int order = 0) =>
        new ModFolder { Path = path, Name = Path.GetFileName(path), SteamId = 111, Enabled = true, Order = order };

    private static string MakeFolder(string tmp, string name, params string[] dllNames)
    {
        string dir = Path.Combine(tmp, name);
        Directory.CreateDirectory(dir);
        foreach (string n in dllNames)
            File.Copy(ThisDll, Path.Combine(dir, n), true);
        return dir;
    }

    private static PluginEntry Ac(string guid, string version = "1.0", params string[] after) =>
        new PluginEntry { Guid = guid, Name = guid, Kind = PluginKind.AnchorChain, Version = Version.Parse(version), Folder = Folder("x"), TypeName = "T." + guid, Dll = "x.dll" }
            .WithAfter(after);

    private static PluginEntry WithAfter(this PluginEntry p, string[] after)
    {
        foreach (string a in after)
            p.Dependencies.Add(new Dependency { Guid = a });
        return p;
    }

    private static List<PluginEntry> Plan(List<PluginEntry> all, params string[] elsewhere) =>
        Planner.Plan(all, new HashSet<string>(elsewhere), new HashSet<string>(), "Sea Power");

    // ---- 1. exclusions ----

    private static void ScannerExclusions(string tmp, string managed, string core)
    {
        string dir = MakeFolder(tmp, "excl", "AnchorChain.dll", "AnchorChain.Preloader.dll", "anchorchain.dll.bak.dll", "AnchorChainExpansion.dll",
                                "AnchorChain.Sonar.dll", "Other.dll", "SeaPowerKeel.dll");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.Copy(ThisDll, Path.Combine(dir, "sub", "AnchorChain.dll"), true);   // the exact name also counts in a sub folder
        var skipped = new List<SkippedDll>();
        var notes = new List<string>();
        List<PluginEntry> found = Scanner.Scan(new[] { Folder(dir) }, notes.Add, skipped, managed, core);
        string[] skippedNames = skipped.Select(s => Path.GetFileName(s.Path)).OrderBy(n => n).ToArray();
        Check(string.Join(",", skippedNames) == "AnchorChain.dll,AnchorChain.dll,AnchorChain.Preloader.dll,SeaPowerKeel.dll",
              "left out: " + string.Join(", ", skippedNames));
        Check(skipped.All(s => !string.IsNullOrWhiteSpace(s.Reason)), "every left-out DLL has a reason");
        var fromDlls = found.Select(p => Path.GetFileName(p.Dll)).Distinct().OrderBy(n => n).ToArray();
        Check(fromDlls.Contains("AnchorChainExpansion.dll") && fromDlls.Contains("AnchorChain.Sonar.dll") && fromDlls.Contains("Other.dll")
              && fromDlls.Contains("anchorchain.dll.bak.dll"),
              "AnchorChainExpansion.dll, AnchorChain.Sonar.dll and look-alike names are scanned as mods");
        Check(!fromDlls.Contains("AnchorChain.dll") && !fromDlls.Contains("AnchorChain.Preloader.dll") && !fromDlls.Contains("SeaPowerKeel.dll"),
              "no plugin comes from a left-out DLL");
        string report = Report.Text(found, "header", notes, skipped);
        Check(report.Contains("DLLs left out on purpose (4)") && report.Contains("[SKIPPED DLL] AnchorChain.Preloader.dll")
              && report.Contains("Anchor Chain's own library"), "the report lists the left-out DLLs with the reason");
        Check(Report.Text(found, "h", notes).IndexOf("DLLs left out", StringComparison.Ordinal) < 0, "no section when nothing was left out (no list given)");
    }

    // ---- 3. exported types ----

    private static void ExportedTypes(string tmp, string managed, string core)
    {
        string dir = MakeFolder(tmp, "exp", "Mods.dll");
        List<PluginEntry> found = Scanner.Scan(new[] { Folder(dir) }, _ => { }, managed, core);
        PluginEntry Get(string guid) => found.Single(p => p.Guid == guid);
        Check(Get("test.public").State == PluginState.Pending && Get("test.public").Exported, "public class: a plugin");
        Check(Get("test.nested.public").State == PluginState.Pending, "public class nested in a public class: a plugin (exported)");
        Check(Get("test.internal").State == PluginState.Skipped && !Get("test.internal").Exported, "internal class: not a plugin");
        Check(Get("test.internal").Reason.Contains("not public"), "  with a reason a mod author can act on: " + Get("test.internal").Reason);
        Check(Get("test.nested.internal").State == PluginState.Skipped, "internal class nested in a public class: not a plugin");
        Check(Get("test.nested.in.internal").State == PluginState.Skipped, "public class nested in an internal class: not a plugin (not exported)");
        Check(Get("test.explicit").State == PluginState.Pending, "explicit entry point class: a plugin");
        Check(Get("test.config").UsesAcConfig && Get("test.config").AcConfigRequired, "[ACConfig] without argument: required");
        Check(Get("test.config.optional").UsesAcConfig && !Get("test.config.optional").AcConfigRequired, "[ACConfig(false)]: not required");
        Check(Get("test.incompat").Incompatible.SequenceEqual(new[] { "test.absent" }), "[ACIncompatibility] read");
        // the Planner must not see a class Anchor Chain would not
        List<PluginEntry> order = Plan(found);
        Check(!order.Any(p => p.Guid == "test.internal" || p.Guid == "test.nested.internal" || p.Guid == "test.nested.in.internal"), "non-exported classes are not in the start order");
    }

    // ---- 2. entry point ----

    private static void EntryPoints()
    {
        var implicitMod = new ImplicitMod();
        AcEntry.Invoke(implicitMod);
        Check(implicitMod.Called, "public implementation is called");
        var explicitMod = new ExplicitMod();
        Check(explicitMod.GetType().GetMethod("TriggerEntryPoint", BindingFlags.Public | BindingFlags.Instance) == null,
              "(an explicit implementation has no public method of that name - what the old GetMethod lookup needed)");
        AcEntry.Invoke(explicitMod);
        Check(explicitMod.Called, "explicit interface implementation is called");
        var inherited = new InheritedMod();
        AcEntry.Invoke(inherited);
        Check(inherited.Called, "implementation inherited from a base class is called");
        var plain = new NotAnAcMod();
        bool threw = false;
        try { AcEntry.Invoke(plain); } catch (MissingMethodException m) { threw = m.Message.Contains("entry point cannot be found"); }
        Check(threw && !plain.Called, "no interface: MissingMethodException (a public method of that name is no entry point), nothing called");
        Check(AcEntry.FindEntryPoint(typeof(NotAnAcMod)) == null && AcEntry.FindEntryPoint(typeof(ExplicitMod)) != null, "FindEntryPoint tells the two apart");
        bool original = false;
        try { AcEntry.Invoke(new ThrowingMod()); } catch (InvalidTimeZoneException) { original = true; } catch (Exception) { }
        Check(original, "an exception the mod throws arrives unwrapped (TargetInvocationException is removed)");
    }

    // ---- 4 + 6. sequence ----

    private static void Sequence()
    {
        // A <- B <- C by dependency, plus D and E independent, in mod-list order D, C, B, A, E (so order is not the input order).
        PluginEntry d = Ac("d"), c = Ac("c", "1.0", "b"), b = Ac("b", "1.0", "a"), a = Ac("a"), e = Ac("e");
        var all = new List<PluginEntry> { d, c, b, a, e };
        for (int i = 0; i < all.Count; i++)
            all[i].Order = i;
        List<PluginEntry> order = Plan(all);
        string plan = string.Join("", order.Select(p => p.Guid));
        Check(plan == "dabce" || plan == "daebc" || plan == "dabec", "plan keeps dependencies first: " + plan);

        var log = new List<string>();
        var seq = new Sequencer
        {
            Prepare = p => log.Add("P" + p.Guid),
            Start = p => { log.Add("S" + p.Guid); p.State = PluginState.Running; },
            Replan = () => Planner.Replan(all, new HashSet<string>()),
        };
        int steps = seq.Run(order).Count();
        string text = string.Join(" ", log);
        int lastPrepare = log.FindLastIndex(x => x[0] == 'P'), firstStart = log.FindIndex(x => x[0] == 'S');
        Check(lastPrepare < firstStart, "every plugin is loaded/created before the first one starts: " + text);
        Check(string.Join("", log.Where(x => x[0] == 'S').Select(x => x.Substring(1))) == plan, "starts in plan order (dependencies first)");
        Check(steps == 2 * order.Count && seq.StartedCount == order.Count, "one step per load and per start (the game gets a frame in between)");

        // A cannot be loaded: B and C (which need it) do not start, D and E do.
        foreach (PluginEntry p in all) { p.State = PluginState.Pending; p.Reason = ""; }
        log.Clear();
        var seq2 = new Sequencer
        {
            Prepare = p => { log.Add("P" + p.Guid); if (p.Guid == "a") p.Stop(PluginState.Failed, "no entry point"); },
            Start = p => { log.Add("S" + p.Guid); p.State = PluginState.Running; },
            Replan = () => Planner.Replan(all, new HashSet<string>()),
        };
        seq2.Run(Plan(all)).Count();
        string started = string.Join("", log.Where(x => x[0] == 'S').Select(x => x.Substring(1)).OrderBy(x => x));
        Check(started == "de", "a plugin that fails to load keeps its dependants out, the others start: started " + started);
        Check(b.State == PluginState.NotStarted && b.Reason.Contains("does not start") && c.State == PluginState.NotStarted, "  dependants say why: " + b.Reason);
        Check(a.State == PluginState.Failed, "  the failed one is FAILED, not RUNNING");

        // Anchor Chain appears after the loading phase: no Anchor Chain mod is started by Keel, a BepInEx plugin still is.
        foreach (PluginEntry p in all) { p.State = PluginState.Pending; p.Reason = ""; }
        PluginEntry bep = new PluginEntry { Guid = "bep", Name = "bep", Kind = PluginKind.BepInEx, Folder = Folder("x"), TypeName = "T.bep", Dll = "x.dll", Order = 9 };
        all.Add(bep);
        log.Clear();
        bool acRunning = false;
        var seq3 = new Sequencer
        {
            Prepare = p => log.Add("P" + p.Guid),
            Start = p => { log.Add("S" + p.Guid); p.State = PluginState.Running; },
            Replan = () => Planner.Replan(all, new HashSet<string>(all.Where(x => x.State == PluginState.LeftToAnchorChain).Select(x => x.Guid))),
            HandOver = _ =>
            {
                if (!acRunning || !all.Any(x => x.Kind == PluginKind.AnchorChain && x.State == PluginState.Pending))
                    return false;
                foreach (PluginEntry p in all.Where(x => x.Kind == PluginKind.AnchorChain && x.State == PluginState.Pending))
                    p.Stop(PluginState.LeftToAnchorChain, "Anchor Chain is running and starts this mod.");
                return true;
            },
            Prepared = p => { if (log.Count == all.Count(x => x.State == PluginState.Pending)) acRunning = true; },
        };
        seq3.Run(Plan(all)).Count();
        Check(acRunning && !log.Any(x => x[0] == 'S' && x != "Sbep"), "Anchor Chain showed up before the first start: Keel started no Anchor Chain mod: " + string.Join(" ", log.Where(x => x[0] == 'S')));
        Check(log.Contains("Sbep"), "  but the BepInEx plugin still started");
        Check(all.Where(x => x.Kind == PluginKind.AnchorChain).All(x => x.State == PluginState.LeftToAnchorChain), "  and they are reported as left to Anchor Chain");

        // Anchor Chain appears in the middle of the start phase.
        foreach (PluginEntry p in all) { p.State = PluginState.Pending; p.Reason = ""; }
        log.Clear();
        int startsDone = 0;
        bool acNow = false;
        var seq4 = new Sequencer
        {
            Prepare = p => { },
            Start = p => { log.Add("S" + p.Guid); p.State = PluginState.Running; if (++startsDone == 2) acNow = true; },
            Replan = () => Planner.Replan(all, new HashSet<string>()),
            HandOver = _ =>
            {
                if (!acNow || !all.Any(x => x.Kind == PluginKind.AnchorChain && x.State == PluginState.Pending))
                    return false;
                foreach (PluginEntry p in all.Where(x => x.Kind == PluginKind.AnchorChain && x.State == PluginState.Pending))
                    p.Stop(PluginState.LeftToAnchorChain, "x");
                return true;
            },
        };
        seq4.Run(Plan(all)).Count();
        Check(seq4.StartedCount == 2 + (log.Contains("Sbep") ? 0 : 0) || log.Count(x => x[0] == 'S') <= 3,
              "Anchor Chain appearing mid-way: the remaining Anchor Chain mods are not started by Keel (" + string.Join(" ", log) + ")");
        Check(all.Count(x => x.State == PluginState.Running) == startsDone, "  no mod is both started by Keel and left to Anchor Chain");
    }

    private static void AnchorChainRunning()
    {
        AssemblyBuilder fake = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("AnchorChain"), AssemblyBuilderAccess.Run);
        Check(GameState.AnchorChainRunning(null), "an AnchorChain assembly nobody claims: Anchor Chain is running");
        Check(!GameState.AnchorChainRunning(a => a.IsDynamic && a.FullName == fake.FullName), "the same assembly claimed as loaded by Keel (its library use): not running");
        Check(GameState.AnchorChainRunning(a => false), "claimed by nobody: running");
    }

    // ---- 5. [ACConfig] ----

    private static void Config(string tmp)
    {
        string mods = Path.Combine(tmp, "cfg-mods"), streaming = Path.Combine(tmp, "cfg-streaming");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(streaming);
        string cfgDir = Path.Combine(streaming, AcConfig.ConfigFolder);
        var info = new List<string>();
        var warn = new List<string>();
        var err = new List<string>();
        AcConfig Make(Type t, string dir = null) => new AcConfig(t, dir ?? cfgDir, new[] { mods }, info.Add, warn.Add, err.Add);
        PluginEntry Plugin(string guid, bool required = true) =>
            new PluginEntry { Guid = guid, Name = guid, UsesAcConfig = true, AcConfigRequired = required, Kind = PluginKind.AnchorChain };

        // the game's real signature (a recent build): open has an extra optional parameter
        var members = new IniApi(typeof(SelfTestFakes.GlobeIni));
        Check(members.Missing.Count == 0 && members.CanReset, "a game with every member Keel needs: nothing missing (" + members.OpenSignature + ")");
        var flat = new IniApi(typeof(SelfTestFakes.FlatIni));
        Check(flat.Missing.Count == 0 && flat.OpenSignature.Contains("forceDisk") && !flat.OpenSignature.Contains("applyModifiers"), "0.8.7-style open (five parameters) is found too: " + flat.OpenSignature);

        // no default config: the mod does not start (Anchor Chain skips it as well)
        PluginEntry p1 = Plugin("mod.one");
        Check(!Make(typeof(SelfTestFakes.GlobeIni)).Load(p1, out string why) && why.Contains("mod.one.ini"), "no default config file: mod kept out with a reason");

        // first start: the player's file is made from the default
        File.WriteAllText(Path.Combine(mods, "mod.one.ini"), "[Main]\nSpeed = 5\nColor = red\n[AnchorChain.ResetValues]\nMain = Color\n");
        Check(Make(typeof(SelfTestFakes.GlobeIni)).Load(p1, out _), "default config present: the mod starts");
        string user = Path.Combine(cfgDir, "mod.one_user.ini");
        Check(File.Exists(user), "the player's config is created in ACConfigs");
        string userText = File.ReadAllText(user);
        Check(userText.Contains("Speed") && userText.Contains("Color"), "  with the default's keys");
        string defText = File.ReadAllText(Path.Combine(mods, "mod.one.ini"));
        Check(defText.Contains("hasReset") && defText.Contains("True"), "the default remembers [AnchorChain.State] hasReset = True");

        // an update of the mod adds a key and a section; the player's own value stays, ResetValues is not applied again
        File.WriteAllText(user, "[Main]\nSpeed = 99\nColor = blue\n");
        File.WriteAllText(Path.Combine(mods, "mod.one.ini"), "[Main]\nSpeed = 5\nColor = red\nNew = 1\n[Extra]\nA = b\n[AnchorChain.State]\nhasReset = True\n[AnchorChain.ResetValues]\nMain = Color\n");
        Check(Make(typeof(SelfTestFakes.GlobeIni)).Load(Plugin("mod.one"), out _), "mod update: merged");
        userText = File.ReadAllText(user);
        Check(userText.Contains("Speed = 99") && userText.Contains("Color = blue"), "  the player's values stay");
        Check(userText.Contains("New = 1") && userText.Contains("[Extra]") && userText.Contains("A = b"), "  the new key and section are added");

        // reset values: applied once for a default that has not reset yet
        File.WriteAllText(Path.Combine(mods, "mod.one.ini"), "[Main]\nSpeed = 5\nColor = green\n[AnchorChain.ResetValues]\nMain = Color\n");
        Check(Make(typeof(SelfTestFakes.GlobeIni)).Load(Plugin("mod.one"), out _) && File.ReadAllText(user).Contains("Color = green") && File.ReadAllText(user).Contains("Speed = 99"),
              "[AnchorChain.ResetValues] overwrites the listed user values (once)");

        // 0.8.7-style handler works the same
        File.WriteAllText(Path.Combine(mods, "mod.flat.ini"), "[S]\nK = 1\n");
        Check(Make(typeof(SelfTestFakes.FlatIni)).Load(Plugin("mod.flat"), out _) && File.ReadAllText(Path.Combine(cfgDir, "mod.flat_user.ini")).Contains("K = 1"), "the same on a handler with the 0.8.7 open signature");

        // a member this build lacks: handled with a fallback and said so
        File.WriteAllText(Path.Combine(mods, "mod.drift.ini"), "[S]\nK = 1\n");
        warn.Clear();
        PluginEntry drift = Plugin("mod.drift");
        var driftApi = new IniApi(typeof(SelfTestFakes.DriftedIni));
        Check(driftApi.Missing.Any(m => m.Contains("Data")) && !driftApi.CanMerge, "a handler without Data: reported as missing (" + string.Join("; ", driftApi.Missing) + ")");
        Check(Make(typeof(SelfTestFakes.DriftedIni)).Load(drift, out _) && File.Exists(Path.Combine(cfgDir, "mod.drift_user.ini")), "  the mod still starts and gets its player file (copied)");
        Check(warn.Any(w => w.Contains("lacks")) && drift.Warnings.Any(w => w.Contains("not merged")), "  logged (" + warn.FirstOrDefault() + ") and shown on the mod");
        PluginEntry nonType = Plugin("mod.notype");
        File.WriteAllText(Path.Combine(mods, "mod.notype.ini"), "[S]\nK = 1\n");
        Check(Make(null).Load(nonType, out _) && File.Exists(Path.Combine(cfgDir, "mod.notype_user.ini")) && Make(null).MissingMembers.Count == 1, "no IniHandler type at all: copy only, with a warning");

        // a handler that throws while merging does not take the mod down
        File.WriteAllText(Path.Combine(mods, "mod.boom.ini"), "[S]\nK = 1\n");
        PluginEntry boom = Plugin("mod.boom");
        Check(Make(typeof(SelfTestFakes.BrokenIni)).Load(boom, out _) && boom.Warnings.Any(w => w.Contains("merge failed")), "a failing merge: the mod starts, with a warning");

        // the player's file cannot be created: required -> no start; optional -> start
        string blocked = Path.Combine(tmp, "blocked");
        File.WriteAllText(blocked, "i am a file, not a folder");
        File.WriteAllText(Path.Combine(mods, "mod.req.ini"), "[S]\nK = 1\n");
        File.WriteAllText(Path.Combine(mods, "mod.opt.ini"), "[S]\nK = 1\n");
        PluginEntry req = Plugin("mod.req", true), opt = Plugin("mod.opt", false);
        Check(!Make(typeof(SelfTestFakes.GlobeIni), blocked).Load(req, out string reqWhy) && reqWhy.Contains("required"), "[ACConfig] required and no player file possible: not started");
        Check(Make(typeof(SelfTestFakes.GlobeIni), blocked).Load(opt, out _), "[ACConfig(false)] and no player file possible: still starts");
    }

    // ---- 8. AnchorChainLoader.FindFile ----

    private static void LoaderShim(string tmp)
    {
        // the stand-in for AnchorChain.Preloader.dll: Anchor Chain's DLL implements its IPluginLoader on AnchorChainLoader
        Assembly stub = AcLoaderShim.BuildPreloaderStub();
        Type loaderInterface = stub.GetType("AnchorChain.Preloader.IPluginLoader");
        Check(stub.GetName().Name == "AnchorChain.Preloader" && stub.GetName().Version == new Version(1, 0, 0, 0), "stand-in assembly: " + stub.GetName().FullName);
        MethodInfo load = loaderInterface?.GetMethod("LoadPlugins");
        Check(loaderInterface != null && loaderInterface.IsInterface && loaderInterface.IsPublic && load != null && load.ReturnType == typeof(void) && load.GetParameters().Length == 0,
              "  with public interface AnchorChain.Preloader.IPluginLoader { void LoadPlugins(); }");

        // FindFile finds nothing until Keel fills the folder list; then it finds a file of another mod, as under Anchor Chain
        string a = MakeFolder(tmp, "ff-a"), b = MakeFolder(tmp, "ff-b"), cfg = Path.Combine(tmp, "ff-config");
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(b, "Sonar-X.wav"), "x");
        Check(AnchorChainLoader.FindFile("Sonar-X.wav") == null, "before: FindFile finds nothing (Anchor Chain's loader never ran)");
        string why = AcLoaderShim.Prime(typeof(AnchorChainLoader).Assembly, new[] { a, b }, cfg);
        Check(why == null, "Prime reports success");
        Check(AnchorChainLoader.FindFile("Sonar-X.wav") == Path.Join(b, "Sonar-X.wav"), "after: FindFile finds a file in another mod's folder");
        Check(AnchorChainLoader.ConfigPath == cfg, "  _configPath is set like Anchor Chain's Setup does");
        AcLoaderShim.Prime(typeof(AnchorChainLoader).Assembly, new[] { b }, cfg);
        Check(AnchorChainLoader.FolderCount == 3, "  priming twice does not list a folder twice (" + AnchorChainLoader.FolderCount + " folders)");
        string missing = AcLoaderShim.Prime(stub, new[] { a }, cfg);
        Check(missing != null && missing.Contains("is not in"), "an Anchor Chain DLL without AnchorChainLoader: reported, not thrown (" + missing + ")");
    }

    // ---- 7. incompatibility ----

    private static void Incompatibility()
    {
        PluginEntry lonely = Ac("lonely");
        lonely.Incompatible.Add("absent.mod");
        PluginEntry x = Ac("x"), y = Ac("y");
        y.Incompatible.Add("x");
        var all = new List<PluginEntry> { lonely, x, y };
        for (int i = 0; i < all.Count; i++)
            all[i].Order = i;
        List<PluginEntry> order = Plan(all);
        Check(order.Contains(lonely) && lonely.State == PluginState.Pending, "[ACIncompatibility] naming an absent mod: the mod starts (Keel's correct behaviour)");
        Check(lonely.Warnings.Any(w => w.Contains("Anchor Chain's loader would not") && w.Contains("upstream bug")), "  the report says Anchor Chain would not start it (upstream bug)");
        Check(y.State == PluginState.NotStarted && y.Reason.Contains("Does not work together with"), "a named mod that is present: skipped with the reason");
        Check(x.State == PluginState.Pending && !x.Warnings.Any(), "  the other one is untouched, no misleading note");
        Check(!y.Warnings.Any(w => w.Contains("upstream")), "  and the upstream note is only for mods that start");
    }
}

namespace SelfTestFakes
{
    // Fake game IniHandlers (a file-based mini version of what the game's one does for [ACConfig]).
    public abstract class IniBase
    {
        public Dictionary<string, Dictionary<string, string>> Store = new();
        protected string path;

        protected void DoOpen(string p)
        {
            path = p;
            Store = new Dictionary<string, Dictionary<string, string>>();
            string section = null;
            foreach (string raw in File.ReadAllLines(p))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[")) { section = line.Trim('[', ']'); Store[section] = new Dictionary<string, string>(); continue; }
                int eq = line.IndexOf('=');
                if (section != null && eq > 0) Store[section][line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }

        public void saveFile(bool overwrite = false)
        {
            var lines = new List<string>();
            foreach (var s in Store)
            {
                lines.Add("[" + s.Key + "]");
                lines.AddRange(s.Value.Select(kv => kv.Key + " = " + kv.Value));
            }
            File.WriteAllLines(path, lines);
        }

        public bool doesSectionExist(string s) => Store.ContainsKey(s);
        public bool doesKeyExist(string s, string k) => Store.TryGetValue(s, out var d) && d.ContainsKey(k);
        public void AddSection(string s, Dictionary<string, string> d) => Store[s] = d;
        public void writeValue(string s, string k, string v) { if (!Store.ContainsKey(s)) Store[s] = new(); Store[s][k] = v; }
        public void writeValue(string s, string k, bool v) => writeValue(s, k, v ? "True" : "False");
        public void writeValue(string s, string k, int v) => writeValue(s, k, v.ToString());
        public bool readValue(string s, string k, bool d, bool performLogging = true) => doesKeyExist(s, k) ? bool.Parse(Store[s][k]) : d;
        public bool? readValue(string s, string k, bool? d, bool performLogging = true) => d;
        public string readValue(string s, string k, string d, bool performLogging = true) => doesKeyExist(s, k) ? Store[s][k] : d;
        public int readValue(string s, string k, int d, bool performLogging = true) => d;
        public Dictionary<string, string> GetSectionKeyValues(string s) => Store[s];
    }

    // a recent game build: open(path, ..., applyModifiers)
    public sealed class GlobeIni : IniBase
    {
        public Dictionary<string, Dictionary<string, string>> Data => Store;
        public void open(string p, bool useCaching = true, bool usePersistentPath = false, bool errorOnMissingFile = true, bool forceDisk = false, bool applyModifiers = true) => DoOpen(p);
    }

    // 0.8.7: five parameters
    public sealed class FlatIni : IniBase
    {
        public Dictionary<string, Dictionary<string, string>> Data => Store;
        public void open(string p, bool useCaching = true, bool usePersistentPath = false, bool errorOnMissingFile = true, bool forceDisk = false) => DoOpen(p);
    }

    // a build where Data is gone and GetSectionKeyValues is renamed: Keel must degrade, not fail
    public sealed class DriftedIni
    {
        public void open(string p, bool useCaching = true) { }
        public void saveFile(bool overwrite = false) { }
        public bool doesSectionExist(string s) => false;
        public bool doesKeyExist(string s, string k) => false;
        public void AddSection(string s, Dictionary<string, string> d) { }
        public void writeValue(string s, string k, string v) { }
    }

    // open works, the merge blows up
    public sealed class BrokenIni : IniBase
    {
        public Dictionary<string, Dictionary<string, string>> Data => throw new InvalidOperationException("the game changed");
        public void open(string p, bool useCaching = true) => DoOpen(p);
    }
}

// Fake Anchor Chain types: the scanner matches attributes and the interface by full name, wherever they come from.
namespace AnchorChain
{
    [AttributeUsage(AttributeTargets.Class)] public class ACPlugin : Attribute { public ACPlugin(string guid, string name, string version, string[] before = null, string[] after = null) { } }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)] public class ACDependency : Attribute { public ACDependency(string guid, string min, string max) { } }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)] public class ACIncompatibility : Attribute { public ACIncompatibility(string guid) { } }
    [AttributeUsage(AttributeTargets.Class)] public class ACConfig : Attribute { public ACConfig(bool required = true) { } }
    public interface IAnchorChainMod { void TriggerEntryPoint(); }

    // The part of Anchor Chain's AnchorChainLoader that mods call (FindFile), as in its source.
    public class AnchorChainLoader
    {
        private static HashSet<System.IO.DirectoryInfo> _allDirectories = new();
        private static string _configPath = string.Empty;
        public static string ConfigPath => _configPath;
        public static int FolderCount => _allDirectories.Count;

        public static string FindFile(string fileName)
        {
            foreach (System.IO.DirectoryInfo dir in _allDirectories)
            {
                string path = System.IO.Path.Join(dir.FullName, fileName);
                if (System.IO.File.Exists(path)) return path;
            }
            return null;
        }
    }
}

[ACPlugin("test.public", "Public", "1.0")]
public class PublicMod : IAnchorChainMod { public void TriggerEntryPoint() { } }

[ACPlugin("test.internal", "Internal", "1.0")]
internal class InternalMod : IAnchorChainMod { public void TriggerEntryPoint() { } }

[ACPlugin("test.explicit", "Explicit", "1.0")]
public class ExplicitMod : IAnchorChainMod
{
    public bool Called;
    void IAnchorChainMod.TriggerEntryPoint() => Called = true;
}

[ACPlugin("test.config", "Config", "1.0"), ACConfig]
public class ConfigMod : IAnchorChainMod { public void TriggerEntryPoint() { } }

[ACPlugin("test.config.optional", "Config optional", "1.0"), ACConfig(false)]
public class ConfigOptionalMod : IAnchorChainMod { public void TriggerEntryPoint() { } }

[ACPlugin("test.incompat", "Incompat", "1.0"), ACIncompatibility("test.absent")]
public class IncompatMod : IAnchorChainMod { public void TriggerEntryPoint() { } }

public class OuterPublic
{
    [ACPlugin("test.nested.public", "Nested public", "1.0")]
    public class Inner : IAnchorChainMod { public void TriggerEntryPoint() { } }

    [ACPlugin("test.nested.internal", "Nested internal", "1.0")]
    internal class InnerHidden : IAnchorChainMod { public void TriggerEntryPoint() { } }
}

internal class OuterInternal
{
    [ACPlugin("test.nested.in.internal", "Nested in internal", "1.0")]
    public class Inner : IAnchorChainMod { public void TriggerEntryPoint() { } }
}

public class ImplicitMod : IAnchorChainMod { public bool Called; public void TriggerEntryPoint() => Called = true; }
public class BaseMod { public bool Called; public void TriggerEntryPoint() => Called = true; }
public class InheritedMod : BaseMod, IAnchorChainMod { }
public class NotAnAcMod { public bool Called; public void TriggerEntryPoint() => Called = true; }
public class ThrowingMod : IAnchorChainMod { public void TriggerEntryPoint() => throw new InvalidTimeZoneException("boom"); }

