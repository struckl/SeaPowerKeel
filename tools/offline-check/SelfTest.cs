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
            Console.WriteLine("0. _info.ini [Compatibility], by the game's rules");
            InfoIni();
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
            Console.WriteLine("9. Patch guard: the member and the patched method come out of the error and its stack, (taking a patch off is run in the game: Harmony does not start outside it)");
            PatchGuard();
            CrashGuardChecks(tmp);
            CrashHuntChecks(tmp);
            PreventionChecks(tmp, managed, core);
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


    private static void CrashGuardChecks(string tmp)
    {
        CrashGuard.Verdict V(params string[] lines) => CrashGuard.Read(lines);
        Check(!V().Unclean, "no journal: nothing to say");
        Check(!V("session\tx", "start\ta", "done\t", "quit\t").Unclean, "a normal quit is clean");
        CrashGuard.Verdict during = V("session\tx", "load\ta", "start\tb");
        Check(during.Unclean && during.Step == "start" && during.Guid == "b", "ended while starting b: names b");
        CrashGuard.Verdict after = V("session\tx", "start\tb", "done\t", "scene\tmission");
        Check(after.Unclean && after.Guid == null && after.Scene == "mission", "ended after start-up: no mod from the journal, the scene is known");

        Check(CrashGuard.CrashBlock("all fine\nno problem") == null, "a log without a crash gives no crash block");
        var a = new PluginEntry { Guid = "a", TypeName = "AlphaMod.Plugin", Order = 0 };
        var b = new PluginEntry { Guid = "b", TypeName = "BetaMod.Plugin", Order = 1 };
        var c = new PluginEntry { Guid = "c", TypeName = "SeaPower.Plugin", Order = 2 };
        var mods = new[] { a, b, c };
        string block = CrashGuard.CrashBlock("before\r\nCrash!!!\r\n0x00 (Mono JIT code) BetaMod.Hooks:Prefix ()\r\n0x01 (Mono JIT code) AlphaMod.Other:Run ()");
        Check(block != null && block.StartsWith("Crash!!!"), "the crash block starts at the crash");
        Check(CrashGuard.Blame(block, mods) == b, "the mod highest on the crash's stack is blamed");
        Check(CrashGuard.Blame("Crash!!!\n0x00 (Mono JIT code) SeaPower.GameMain:Update ()\nBetaMod.Hooks is mentioned but not a frame", mods) == null, "the game's frames and non-frame lines blame nobody");
        Check(CrashGuard.Blame("Crash!!!\n0x00 (Mono JIT code) NotBetaMod.Hooks:Run ()", mods) == null, "a longer name that ends the same is another mod");

        string stack = "Crash: access violation (0xC0000005) in UnityPlayer.dll\n  at SeaPowerKeel.CrashCatcher.Compose (IntPtr p)\n  at UnityEngine.Diagnostics.Utils.ForceCrash (Int32 c)\n  at BetaMod.Hooks.Boom ()\n  at AlphaMod.Other.Run ()";
        Check(CrashText.CrashLine(stack) == "access violation (0xC0000005) in UnityPlayer.dll", "the catcher's first line says what crashed and where");
        Check(CrashGuard.Blame(stack, mods) == b, "the catcher's stack names the mod highest on it, not Keel's own frames");
        Check(CrashText.CrashLine("Crash!!!") == null, "a block that is not the catcher's has no such line");
        string caughtDir = Path.Combine(tmp, "crashcaught");
        Directory.CreateDirectory(caughtDir);
        string betaDll = Path.Combine(caughtDir, "beta.dll");
        File.WriteAllText(betaDll, "x");
        File.WriteAllLines(Path.Combine(caughtDir, "Keel-session.txt"), new[] { "session\tx", "start\tb", "done\t" });
        File.WriteAllText(Path.Combine(caughtDir, CrashText.StackFile), stack);
        var caughtNotes = new List<string>();
        var caught = new PluginEntry { Guid = "b", Name = "Beta", TypeName = "BetaMod.Plugin", Dll = betaDll };
        var caughtRun = new CrashGuard(caughtDir, null);
        caughtRun.Apply(new[] { caught }, true, caughtNotes.Add);
        caughtRun.Mark("quit", "");
        Check(caught.State == PluginState.NotStarted && caughtNotes.Count == 1 && caughtNotes[0].Contains("Beta") && !caughtRun.Unnamed, "a crash after start-up with the mod on the catcher's stack: named and held out, no hunt needed");
        caughtRun.WriteModsFile(new[] { caught });
        Check(File.ReadAllText(Path.Combine(caughtDir, CrashText.ModsFile)).Contains("b\tBeta\tBetaMod"), "the watcher's list has id, name and code root");

        string keptDir = Path.Combine(tmp, "crashkept");
        Directory.CreateDirectory(keptDir);
        File.WriteAllLines(Path.Combine(keptDir, "Keel-session.txt"), new[] { "session\tx", "start\tb", "done\t" });
        File.WriteAllText(Path.Combine(keptDir, CrashText.StackFile), stack);
        File.WriteAllText(Path.Combine(keptDir, CrashText.KeepFile), "b");
        var kept = new PluginEntry { Guid = "b", Name = "Beta", TypeName = "BetaMod.Plugin", Dll = betaDll };
        var keptRun = new CrashGuard(keptDir, null);
        keptRun.Apply(new[] { kept }, true, _ => { });
        keptRun.Mark("quit", "");
        Check(kept.State == PluginState.Pending && !File.Exists(Path.Combine(keptDir, CrashText.KeepFile)), "the player unticked 'keep it off': the mod runs, and the answer counts once");

        string dir = Path.Combine(tmp, "crashguard");
        Directory.CreateDirectory(dir);
        string dll = Path.Combine(dir, "beta.dll");
        File.WriteAllText(dll, "x");
        PluginEntry Mod() => new() { Guid = "b", Name = "Beta", TypeName = "BetaMod.Plugin", Dll = dll };
        File.WriteAllLines(Path.Combine(dir, "Keel-session.txt"), new[] { "session\tx", "start\tb" });
        var notes = new List<string>();
        PluginEntry first = Mod();
        var run1 = new CrashGuard(dir, null);
        run1.Apply(new[] { first }, true, notes.Add);
        run1.Mark("quit", "");
        Check(first.State == PluginState.NotStarted && notes.Count == 1 && notes[0].Contains("Beta"), "the mod that took the game down is named and kept out at the next start");
        Check(File.Exists(Path.Combine(dir, "Keel-crash.txt")), "Keel-crash.txt is written");
        PluginEntry second = Mod();
        var run2 = new CrashGuard(dir, null);
        run2.Apply(new[] { second }, true, notes.Add);
        run2.Mark("quit", "");
        Check(second.State == PluginState.NotStarted && notes.Count == 1, "after a normal quit it stays out, without a new crash note");
        File.WriteAllText(dll, "updated");
        Check(run2.HeldActive, "a held mod is known to the report, for its button");
        PluginEntry third = Mod();
        var run3 = new CrashGuard(dir, null);
        run3.Apply(new[] { third }, true, notes.Add);
        run3.Mark("quit", "");
        Check(third.State == PluginState.Pending && !File.Exists(Path.Combine(dir, "Keel-held.txt")), "an updated mod gets another chance");
    }

    private static void PreventionChecks(string tmp, string managed, string core)
    {
        Check(CrashText.PackOf(@"C:\Steam\steamapps\workshop\content\1286220\123\vessels\a.ini") == "123", "a Workshop file belongs to its item");
        Check(CrashText.PackOf(@"E:\G\Sea Power_Data\StreamingAssets\MyPack\vessels\a.ini") == "MyPack", "a local mod's file belongs to its folder");
        Check(CrashText.PackOf(@"E:\G\Sea Power_Data\StreamingAssets\original\vessels\a.ini") == null, "the game's own files are nobody's pack");
        Check(CrashText.PackOf(@"E:\G\Sea Power_Data\StreamingAssets\SeaPowerKeel\x.ini") == null, "Keel never blames itself");
        string block = "Crash: access violation (0xC0000005) in UnityPlayer.dll\nFile: 900\tC:/x/workshop/content/1286220/77/assets/plan_j-35.ini\nFile: 2000\tC:/x/workshop/content/1286220/88/b.ini\n  at UnityEngine.Object.Destroy ()";
        Check(CrashText.LastPack(block, out string f, out string p) && f == "77" && p.EndsWith("plan_j-35.ini"), "the newest mod file before the crash names the pack");
        Check(!CrashText.LastPack("Crash: x\nFile: 60000\tC:/x/workshop/content/1286220/77/a.ini", out _, out _), "a file opened a minute before the crash says nothing");

        string dir = Path.Combine(tmp, "packcheck");
        Directory.CreateDirectory(dir);
        void Bytes(string name, params byte[] b) => File.WriteAllBytes(Path.Combine(dir, name), b);
        Bytes("empty.png");
        Bytes("text.jpg", 0x68, 0x65, 0x6C, 0x6C, 0x6F);
        Bytes("good.png", 0x89, 0x50, 0x4E, 0x47, 0, 0);
        Bytes("jpeg-named-png.png", 0xFF, 0xD8, 0xFF, 0);
        Bytes("bad.wav", 1, 2, 3, 4);
        Bytes("good.wav", 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45);
        Bytes("notes.ini");
        var folder = new ModFolder { Path = dir, Name = "Pack" };
        string cache = Path.Combine(tmp, "packcheck.txt");
        List<string> found = PackCheck.Scan(new[] { folder }, cache);
        Check(found.Count == 3 && found.Any(x => x.Contains("empty.png")) && found.Any(x => x.Contains("text.jpg")) && found.Any(x => x.Contains("bad.wav")), "broken pictures and sounds are found, good ones and other files are not");
        File.Delete(Path.Combine(dir, "empty.png"));
        File.Delete(Path.Combine(dir, "text.jpg"));
        File.Delete(Path.Combine(dir, "bad.wav"));
        Check(PackCheck.Scan(new[] { folder }, cache).Count == 0, "a repaired folder is clean");

        using var pre = new Preflight(managed, core);
        Preflight.Result r = pre.Check(ThisDll);
        Check(r.Fatal.Any(x => x.Contains("BadResultPatch") && x.Contains("__result")), "a patch taking __result as int on a float method is caught");
        Check(r.Fatal.Any(x => x.Contains("BadArgumentPatch")), "a patch taking a float argument as int is caught");
        Check(!r.Fatal.Any(x => x.Contains("GoodPatch")), "patches that fit, or take object, are left alone");

        string ini = Path.Combine(tmp, "packoff.ini");
        File.WriteAllText(ini, "Mod1Directory=77,True,77\r\nMod2Directory=88,True,88\r\n");
        File.WriteAllText(Path.Combine(tmp, CrashText.PackOffFile), "77");
        Check(CrashHunt.ApplyPackOff(tmp, ini) == "77" && CrashHunt.ListOn(File.ReadAllText(ini)).SequenceEqual(new[] { "88" }) && !File.Exists(Path.Combine(tmp, CrashText.PackOffFile)), "the crash window's choice switches the pack off in the game's list, once");
    }

    private static void CrashHuntChecks(string tmp)
    {
        string dir = Path.Combine(tmp, "crashhunt");
        Directory.CreateDirectory(dir);
        string ini = Path.Combine(dir, "usersettings.ini");
        string Start() => "[LoadOrder]\r\nMod1Directory=SeaPowerKeel,True,0\r\nMod2Directory=A,True,0\r\nMod3Directory=B,True,11\r\nMod4Directory=C,True,0\r\nMod5Directory=D,True,0\r\nMod6Directory=Off,False,0\r\nNumberOfModFiles=6\r\nOther=1\r\n";
        File.WriteAllText(ini, Start());
        Check(CrashHunt.ListOn(Start()).SequenceEqual(new[] { "SeaPowerKeel", "A", "B", "C", "D" }), "the folders that are on, in order");
        string edited = CrashHunt.SetFolders(Start(), new[] { "B", "C" }, new[] { "B" });
        Check(edited.Contains("Mod3Directory=B,False,11") && edited.Contains("Mod4Directory=C,True,0") && edited.Contains("Other=1") && edited.Contains("\r\n"), "one folder off, its id and every other line kept");

        var crashed = new CrashGuard.Verdict { Unclean = true };
        var clean = new CrashGuard.Verdict();
        CrashHunt Next(CrashGuard.Verdict v) { var h = new CrashHunt(dir, ini); h.Advance(v); return h; }
        List<string> On() => CrashHunt.ListOn(File.ReadAllText(ini));

        new CrashHunt(dir, ini).Advance(clean);
        Check(On().Count == 5, "no hunt: the mod list is not touched");
        new CrashHunt(dir, ini).Arm();
        Check(On().Count == 5, "arming changes nothing until the next start");
        CrashHunt h1 = Next(clean);
        Check(h1.Running && On().SequenceEqual(new[] { "SeaPowerKeel", "A", "B" }), "round 1: half of the suspects off, Keel never");
        CrashHunt h2 = Next(crashed);
        Check(h2.Running && On().SequenceEqual(new[] { "SeaPowerKeel", "A", "C", "D" }), "a crash: the culprit is in the half that was on; the cleared mods are back on");
        CrashHunt h3 = Next(clean);
        Check(!h3.Running && h3.Message.Contains("B") && On().SequenceEqual(new[] { "SeaPowerKeel", "A", "C", "D" }), "no crash: the half that was off; one left: named, it stays off, the rest is back");
        Check(File.ReadAllText(ini).Contains("Mod6Directory=Off,False,0"), "a folder that was off before stays off");
        Check(!File.Exists(Path.Combine(dir, "Keel-hunt.txt")), "the hunt is over");

        File.WriteAllText(ini, Start());
        new CrashHunt(dir, ini).Arm();
        Next(clean);
        new CrashHunt(dir, ini).Stop();
        CrashHunt stopped = Next(clean);
        Check(!stopped.Running && On().Count == 5, "stopped: the whole list is back at the next start");

        File.WriteAllText(ini, Start());
        new CrashHunt(dir, ini).Arm();
        Next(clean);
        CrashHunt named = Next(new CrashGuard.Verdict { Unclean = true, Step = "start", Guid = "x" });
        Check(!named.Running && On().Count == 5, "a crash while a mod starts ends the hunt: Keel can name that mod itself");

        File.WriteAllText(ini, "[LoadOrder]\r\nMod1Directory=SeaPowerKeel,True,0\r\nMod2Directory=A,True,0\r\n");
        new CrashHunt(dir, ini).Arm();
        CrashHunt tooFew = Next(clean);
        Check(!tooFew.Running && tooFew.Message != null, "fewer than two suspects: no hunt, and it says so");
    }

    private static void PatchGuard()
    {
        Check(Runner.MissingKey("MissingFieldException: Field not found: SeaPower.ObjectBase SeaPower.AI._objectToDestroy\nDue to: Could not find field in class") == "SeaPower.AI::_objectToDestroy",
              "'Field not found: ObjectBase SeaPower.AI._objectToDestroy' -> SeaPower.AI::_objectToDestroy");
        Check(Runner.MissingKey("MissingMethodException: Method not found: SeaPower.IniHandler SeaPower.Utils.openIniFile(string,bool,bool)") == "SeaPower.Utils::openIniFile",
              "'Method not found: IniHandler SeaPower.Utils.openIniFile(string,bool,bool)' -> SeaPower.Utils::openIniFile");
        Check(Runner.MissingKey("MissingFieldException: Field not found: SeaPower.ObjectBase SeaPower.AI._objectToDestroy Due to: Could not find field in class") == "SeaPower.AI::_objectToDestroy",
              "the same message on ONE line (as Unity logs it, with 'Due to: ...' after it) -> SeaPower.AI::_objectToDestroy");
        Check(Runner.MissingKey("NullReferenceException: Object reference not set") == null, "another exception gives no member");
        var patched = Runner.PatchedMethodsIn("(wrapper dynamic-method) SeaPower.GameMain.DMD<SeaPower.GameMain::FixedUpdate>(SeaPower.GameMain)\nUnityEngine.DebugLogHandler:LogException(Exception, Object)");
        Check(patched.Count == 1 && patched[0].type == "SeaPower.GameMain" && patched[0].method == "FixedUpdate", "the stack's DMD<SeaPower.GameMain::FixedUpdate> gives the patched game method");
        Check(Runner.PatchedMethodsIn("at SomeMod.Thing.Run ()").Count == 0, "a stack without a patched method gives none");
    }

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

    private static void InfoIni()
    {
        string[] Ini(string body) => ("[Language_en]\nName=X\n[Compatibility]\n;comment\n" + body).Split('\n');
        bool Off(string body, string app = "0.8.7") => InfoVersion.Mismatch(Ini(body), app) != null;
        Check(!Off("ApproximateVersion=0.8.4"), "approximate: older patch of this minor fits (the game's rule)");
        Check(Off("ApproximateVersion=0.7.9"), "approximate: another minor is flagged");
        Check(Off("ApproximateVersion=0.8.9"), "approximate: a newer patch than the game is flagged");
        Check(Off("ExactVersion=0.8.6"), "exact: another version is flagged");
        Check(!Off("ExactVersion=0.8.7"), "exact: this version fits");
        Check(!Off("ExactVersion=0.8.7\nExcludeVersions=0.8.7"), "exact decides before the exclude list (the game's order)");
        Check(Off("ExcludeVersions=0.8.5, 0.8.7"), "exclude list: this version is flagged");
        Check(Off("GreaterThanVersion=0.8.7"), "greater than: equal is flagged");
        Check(!Off("GreaterThanEqualToVersion=0.8.7"), "greater or equal: equal fits");
        Check(Off("LessThanVersion=0.8.7"), "less than: equal is flagged");
        Check(Off("LessThanEqualToVersion=0.8.6"), "less or equal: older limit is flagged");
        Check(!Off(""), "no statement: no verdict");
        Check(!Off("ApproximateVersion=nonsense"), "unreadable statement: no verdict");
        Check(!Off("ApproximateVersion=0.7.0", "not-a-version"), "unreadable game version: no verdict");
        Check(!Off("ApproximateVersion=0.7.0".Replace("Approximate", "Other")), "other keys are ignored");
        Check(InfoVersion.Mismatch(new[] { "[Other]", "ApproximateVersion=0.1.0" }, "0.8.7") == null, "only the [Compatibility] section counts");
        Check(InfoVersion.Mismatch(Ini("ApproximateVersion=0.7.9"), "0.8.7").Contains("0.7.9"), "the warning names the version");
        Check(InfoVersion.MismatchInFolder(@"Z:\\no\\such\\folder", "0.8.7") == null, "a missing file is no verdict");
    }

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

// Harmony patches for the preflight's signature check: read through Mono.Cecil, never applied.
[HarmonyLib.HarmonyPatch("UnityEngine.Time", "get_timeScale")]
public static class BadResultPatch { public static void Postfix(ref int __result) { } }
[HarmonyLib.HarmonyPatch("UnityEngine.Mathf", "Repeat")]
public static class BadArgumentPatch { public static void Prefix(int t) { } }
[HarmonyLib.HarmonyPatch("UnityEngine.Mathf", "Repeat")]
public static class GoodPatch { public static void Prefix(float t, object length) { } }
