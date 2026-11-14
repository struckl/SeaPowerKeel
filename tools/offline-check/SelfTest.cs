using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using AnchorChain;
using SeaPowerKeel;
using SeaPowerKeel.Stub;

// Keel's own tests, run outside the game: dotnet run -c Release --project tools/offline-check -- --selftest <Managed> <BepInEx core>
// The mods tested are classes of this very assembly (see the AnchorChain namespace below): the scanner reads DLLs through
// Mono.Cecil without running them, so a copy of this DLL in a temp folder is a mod folder. Nothing of the game is started.
internal static class SelfTest
{
    private static int failures, checks;

    // Shapes of a load-screen postfix (the Alternative mods use the first).
    private static void FakeLoadScreenPostfix(bool __result) { }
    private static void FakeNeedsMore(bool __result, int something) { }

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
            StartupPreparationChecks(tmp);
            Console.WriteLine("3. Scanner: Anchor Chain plugin classes must be public (exported)");
            ExportedTypes(tmp, managed, core);
            Console.WriteLine("2. Entry point through the interface");
            EntryPoints();
            Console.WriteLine("4+6. Sequence: load all first, start in plan order, drop dependants of a failed load, hand over to a running Anchor Chain");
            Sequence();
            LoadOrderWarnings();
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
            PreventionChecks(tmp, managed, core);
            SetupChecks(tmp);
            WhatsNewChecks(tmp);
            TakeoverChecks(tmp);
            EuromodChecks(tmp);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception) { }
        }
        Console.WriteLine();
        Console.WriteLine($"{checks - failures} of {checks} checks passed" + (failures > 0 ? $", {failures} FAILED" : ""));
        return failures > 0 ? 1 : 0;
    }

    private static void StartupPreparationChecks(string tmp)
    {
        string folder = Path.Combine(tmp, "preparation");
        string nested = Path.Combine(folder, "nested");
        Directory.CreateDirectory(nested);
        string first = Path.Combine(folder, "a.Preloader.dll");
        string second = Path.Combine(nested, "b.PRELOADER.DLL");
        string broken = Path.Combine(folder, "bad.Preloader.dll");
        File.WriteAllText(broken, "not a managed DLL");
        File.WriteAllText(Path.Combine(folder, "AnchorChain.Preloader.dll"), "");
        File.WriteAllText(Path.Combine(folder, "regular.dll"), "");
        Build(first, "First", "first");
        Build(second, "Second", "second");
        var found = StartupPreparation.Candidates(folder).ToArray();
        Check(found.SequenceEqual(new[] { first, broken, second }), "startup preparation finds every matching DLL, including nested files");
        var reports = new List<string>();
        var log = new BepInEx.Logging.ManualLogSource("Startup preparation self-test");
        foreach (string dll in found)
            StartupPreparation.RunFile(dll, folder, new[] { folder }, text => { if (text != null) reports.Add(text); }, log);
        Check(reports.SequenceEqual(new[] { "first", "second" }), "both startup callbacks run even when an earlier DLL is invalid");

        static void Build(string path, string name, string result)
        {
            using var assembly = Mono.Cecil.AssemblyDefinition.CreateAssembly(
                new Mono.Cecil.AssemblyNameDefinition(name, new Version(1, 0)), name, Mono.Cecil.ModuleKind.Dll);
            var module = assembly.MainModule;
            var type = new Mono.Cecil.TypeDefinition("Fixture", "Startup", Mono.Cecil.TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            var method = new Mono.Cecil.MethodDefinition("Prepare", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            type.Methods.Add(method);
            method.Parameters.Add(new Mono.Cecil.ParameterDefinition(module.TypeSystem.String));
            method.Parameters.Add(new Mono.Cecil.ParameterDefinition(new Mono.Cecil.ArrayType(module.TypeSystem.String)));
            method.Parameters.Add(new Mono.Cecil.ParameterDefinition(module.ImportReference(typeof(Action<string>))));
            var il = method.Body.GetILProcessor();
            il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_2);
            il.Emit(Mono.Cecil.Cil.OpCodes.Ldstr, result);
            il.Emit(Mono.Cecil.Cil.OpCodes.Callvirt, module.ImportReference(typeof(Action<string>).GetMethod("Invoke")));
            il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
            assembly.Write(path);
        }
    }

    private static void LoadOrderWarnings()
    {
        var a = new PluginEntry { Guid = "a", Order = 0, Folder = Folder("a", 0) };
        var b = new PluginEntry { Guid = "b", Order = 1, Folder = Folder("b", 1) };
        var c = new PluginEntry { Guid = "c", Order = 2, Folder = Folder("c", 2) };
        var all = new List<PluginEntry> { a, b, c };
        var folders = all.Select(p => p.Folder).ToArray();
        a.Dependencies.Add(new Dependency { Guid = b.Guid });
        b.LoadBefore.Add(c.Guid);
        var ordered = Planner.Plan(all, new HashSet<string>(), new HashSet<string>(), "Sea Power");
        Check(ordered.SequenceEqual(new[] { b, a, c }), "Keel puts the dependency first and preserves the valid load-before rule");
        all.ForEach(p => p.State = PluginState.Running);
        Planner.UpdateLoadOrderWarnings(all, folders);
        Check(a.LoadOrderWarning?.Contains("above b") == true && b.LoadOrderWarning == null && c.LoadOrderWarning == null,
            "The report names the misplaced mod and its dependency");
        var progress = new SeaPowerKeel.Loading.Progress(() => 0);
        progress.SetOrderWarnings(Report.CountOrderWarnings(all));
        var summary = progress.Take(0);
        Check(Report.Summary(all).Contains("1 with load order warnings") && summary.HasProblems && summary.SummaryLine(null).Contains("Load order warnings"),
            "Both the report and startup card warn even when all mods started successfully");

        foreach (PluginEntry disabled in new[] { a, b })
        {
            disabled.Folder.Enabled = false;
            Planner.UpdateLoadOrderWarnings(all, folders);
            Check(Report.CountOrderWarnings(all) == 0 && a.LoadOrderWarning == null,
                $"A reload clears the warning when {disabled.Display} is switched off");
            disabled.Folder.Enabled = true;
            Planner.UpdateLoadOrderWarnings(all, folders);
        }

        a.Folder.Order = 2;
        c.Folder.Order = 3;
        Planner.UpdateLoadOrderWarnings(all, folders);
        Check(Report.CountOrderWarnings(all) == 0, "Correcting enabled folder order clears the warning without restarting");
        a.Folder = b.Folder;
        b.Dependencies.Add(new Dependency { Guid = a.Guid });
        Planner.UpdateLoadOrderWarnings(all, folders);
        Check(Report.CountOrderWarnings(all) == 0, "A cycle within a single folder is not a mod list order warning");
        c.Folder.Order = 0;
        Planner.UpdateLoadOrderWarnings(all, folders);
        Check(c.LoadOrderWarning?.Contains("above b") == true && Report.CountOrderWarnings(all) == 1,
            "A load-before rule warns only when its enabled folders are reversed");
    }

    private static void WhatsNewChecks(string tmp)
    {
        Console.WriteLine("10. What's new: what changed since the last start, and the authors' words for it");
        string root = Path.Combine(tmp, "wn");
        string ws = Path.Combine(root, "steamapps", "workshop", "content", "1286220");
        string a = Path.Combine(ws, "111"), b = Path.Combine(ws, "222"), local = Path.Combine(root, "MyLocal");
        foreach (string d in new[] { a, b, local }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(a, "_info.ini"), "[Language_de]\nName=Alpha DE\n[Language_en]\nName=Alpha Mod\n");
        File.WriteAllText(Path.Combine(a, "a.dll"), "v1");
        File.WriteAllText(Path.Combine(b, "b.ini"), "data");
        File.WriteAllText(Path.Combine(local, "CHANGELOG.md"), "# Changelog\n\n## 1.2.0 - x\n\n- **Bold** new thing\n- `code` [link](http://x)\n\n## 1.1.0\n\n- older\n\n## 1.0.0\n\n- first\n");
        string acf = Path.Combine(root, "steamapps", "workshop", "appworkshop_1286220.acf");
        void Acf(long ta) => File.WriteAllText(acf, "\"AppWorkshop\"\n{\n\t\"appid\"\t\t\"1286220\"\n\t\"WorkshopItemsInstalled\"\n\t{\n\t\t\"111\"\n\t\t{\n\t\t\t\"size\"\t\t\"1\"\n\t\t\t\"timeupdated\"\t\t\"" + ta + "\"\n\t\t}\n\t}\n\t\"WorkshopItemDetails\"\n\t{\n\t\t\"222\"\n\t\t{\n\t\t\t\"timeupdated\"\t\t\"5\"\n\t\t}\n\t}\n}\n");
        Acf(1794001000);
        Check(WhatsNew.SteamTimes(ws).TryGetValue(111, out long t1) && t1 == 1794001000 && !WhatsNew.SteamTimes(ws).ContainsKey(222), "Steam's timeupdated is read for installed items only");
        Check(WhatsNew.NameOf(a) == "Alpha Mod", "the mod's English name from _info.ini");

        var fa = new ModFolder { Path = a, Name = "111", SteamId = 111, Enabled = true };
        var fb = new ModFolder { Path = b, Name = "222", SteamId = 222, Enabled = true };
        var fl = new ModFolder { Path = local, Name = "MyLocal", Enabled = true };
        PluginEntry Plug(ModFolder f, string guid, string v, PluginState s) => new() { Guid = guid, Name = guid, Folder = f, Version = new Version(v), State = s };

        var first = new List<ModFolder> { fa, fb, fl };
        var plugins1 = new List<PluginEntry> { Plug(fa, "alpha", "1.0.0", PluginState.Running), Plug(fl, "loc", "1.0.0", PluginState.Running) };
        var seen1 = WhatsNew.Look(first, plugins1, ws);
        Check(WhatsNew.Compare(null, seen1, first).Count == 0, "the very first start says nothing");
        WhatsNew.MarkOk(seen1, plugins1);
        string file = Path.Combine(root, WhatsNew.FileName);
        WhatsNew.Write(file, seen1);
        var back = WhatsNew.Read(file);
        Check(back.Count == 3 && back["111"].Updated == 1794001000 && back["111"].Ok.Contains("alpha") && back["MyLocal"].Versions["loc"] == "1.0.0", "the file reads back what was written");
        Check(WhatsNew.Compare(back, WhatsNew.Look(first, plugins1, ws), first).Count == 0, "nothing changed: no news");

        // Second start: alpha updated on the Workshop, b unsubscribed, local updated, a new local mod, loc now fails.
        File.WriteAllText(Path.Combine(a, "a.dll"), "v2 longer");
        Acf(1794002000);
        Directory.Delete(b, true);
        string fresh = Path.Combine(root, "Fresh");
        Directory.CreateDirectory(fresh);
        var fn = new ModFolder { Path = fresh, Name = "Fresh", Enabled = true };
        var second = new List<ModFolder> { fa, fl, fn };
        var plugins2 = new List<PluginEntry>
        {
            Plug(fa, "alpha", "1.1.0", PluginState.Running), Plug(fl, "loc", "1.2.0", PluginState.Failed), Plug(fn, "fresh", "0.1.0", PluginState.Running),
        };
        plugins2[1].Reason = "needs X";
        var news = WhatsNew.Compare(back, WhatsNew.Look(second, plugins2, ws), second);
        NewsItem alpha = news.FirstOrDefault(n => n.Key == "111");
        Check(alpha is { Kind: NewsKind.Updated, From: "1.0.0", To: "1.1.0", PrevUpdated: 1794001000, Updated: 1794002000, Name: "Alpha Mod" }, "a Workshop update: old and new version, both Steam times");
        Check(news.Any(n => n.Key == "222" && n.Kind == NewsKind.Gone), "an unsubscribed mod is gone");
        Check(news.Any(n => n.Key == "Fresh" && n.Kind == NewsKind.New && n.To == "0.1.0"), "a new mod is new");
        Check(news.Any(n => n.Key == "MyLocal" && n.Kind == NewsKind.Updated && n.From == "1.0.0" && n.To == "1.2.0"), "a local mod's version jump");
        var problems = WhatsNew.Problems(back, plugins2);
        Check(problems.Count == 1 && problems[0].Name == "loc" && problems[0].Lines[0].Contains("needs X"), "a mod that ran last time and fails now is a problem, with the reason");

        // Notes: Workshop page entries between the two Steam times; else the CHANGELOG above the old version.
        string page = "<p id=\"1794002000\">1.1.0: new radar<br>fix &quot;x&quot;</p> <p id=\"1794001500\">1.0.5 <b>beta</b></p> <p id=\"1794000900\">1.0.0 old</p>";
        var w = WhatsNew.WorkshopNotes(page, 1794001000, 1794002000);
        Check(w.SequenceEqual(new[] { "1.1.0: new radar", "fix \"x\"", "1.0.5 beta" }), "Workshop notes: every update since the last start, newest first, HTML made plain");
        Check(WhatsNew.WorkshopNotes(page, 0, 1794002000).SequenceEqual(new[] { "1.1.0: new radar", "fix \"x\"" }), "a new mod: only its newest note");
        Check(WhatsNew.WorkshopNotes(page, 1794003000, 1794004000).Count == 0, "no note after the last start: none");
        // Real notes (Euromod Main, 2026-11): Markdown headings pasted into Steam's box show raw there.
        string euro = "<p id=\"1794246861\">### Fixes ### fixed RAM launch angles</p> <p id=\"1793322763\">### Minor additions ###<br><br>- Added 152mm Bofors<br><br>### Fixes ###<br><br>- Added missing name entry</p>";
        Check(WhatsNew.WorkshopNotes(euro, 1793000000, 1794246861).SequenceEqual(new[] { "Fixes: fixed RAM launch angles", "Minor additions:", "Added 152mm Bofors", "Fixes:", "Added missing name entry" }),
            "Workshop notes: '### X ###' headings become 'X:', bullets out: " + string.Join(" | ", WhatsNew.WorkshopNotes(euro, 1793000000, 1794246861)));
        Check(WhatsNew.CleanNoteLine("[b]New[/b] [url=https://x.y]page[/url] **bold** `code`") == "New page bold code", "BBCode and Markdown marks out");
        Check(WhatsNew.CleanNoteLine("[*] item") == "item" && WhatsNew.CleanNoteLine("1. first") == "first" && WhatsNew.CleanNoteLine("== Changes ==") == "Changes:", "list items and = headings");
        Check(WhatsNew.CleanNoteLine("-----") == "" && WhatsNew.CleanNoteLine("[hr][/hr]") == "" && WhatsNew.CleanNoteLine("###") == "", "rules and empty headings dropped");
        Check(WhatsNew.CleanNoteLine("v.117: Assigned CVW-8") == "v.117: Assigned CVW-8" && WhatsNew.CleanNoteLine("C# fix") == "C# fix", "plain lines stay");
        var log = WhatsNew.ChangelogNotes(local, "1.0.0");
        Check(log.SequenceEqual(new[] { "Bold new thing", "code link", "older" }), "CHANGELOG: the sections above the old version, Markdown removed");
        Check(WhatsNew.ChangelogNotes(local, "").SequenceEqual(new[] { "Bold new thing", "code link" }), "CHANGELOG with no old version: the top section");
        Check(WhatsNew.ChangelogLines(new[] { "## Unreleased", "- not yet", "## 1.2.0", "- yes", "## 1.1.0", "- no" }, "1.1.0").SequenceEqual(new[] { "yes" }), "an Unreleased section is not in the player's build");
        Check(WhatsNew.ChangelogLines(new[] { "## Unreleased", "- not yet", "## 0.2.2 – 2026-11-12", "- yes", "## 0.2.1", "- no" }, "").SequenceEqual(new[] { "yes" }),
            "a mod without a version: its newest released section, not Unreleased (Clean Screen, 2026-11-12)");
        Check(LoadScreenEnd.Args(typeof(SelfTest).GetMethod(nameof(FakeLoadScreenPostfix), BindingFlags.NonPublic | BindingFlags.Static).GetParameters()) is { Length: 1 } a1 && (bool)a1[0] == false
              && LoadScreenEnd.Args(typeof(SelfTest).GetMethod(nameof(FakeNeedsMore), BindingFlags.NonPublic | BindingFlags.Static).GetParameters()) == null,
            "load screen end: a postfix with __result gets false; one that needs other arguments is left");

        WhatsNew.AddNotes(news, id => id == 111 ? page : null);
        Check(alpha.Source == "Workshop" && alpha.Lines.Count == 3, "a Workshop mod gets its Workshop notes");
        NewsItem loc = news.First(n => n.Key == "MyLocal");
        Check(loc.Source == "CHANGELOG" && loc.Lines.Count == 3, "a local mod gets its CHANGELOG");
        var offline = WhatsNew.Compare(back, WhatsNew.Look(second, plugins2, ws), second);
        File.WriteAllText(Path.Combine(a, "CHANGELOG.md"), "## 1.1.0\n- from the file\n");
        WhatsNew.AddNotes(offline, _ => throw new System.Net.WebException("offline"));
        Check(offline.First(n => n.Key == "111").Lines.SequenceEqual(new[] { "from the file" }), "Steam unreachable: the CHANGELOG instead, no error");
        Check(WhatsNew.Text(news).Contains("UPDATED  Alpha Mod  1.0.0 → 1.1.0   (from Workshop)"), "the report text");

        // Game test 2026-11-12: BetterTacMap rewrote settings.cfg in its Workshop folder at every start and was UPDATED every time.
        var steady = WhatsNew.Look(second, plugins2, ws);
        File.WriteAllText(Path.Combine(a, "settings.cfg"), "written by the mod " + Guid.NewGuid());
        File.WriteAllText(Path.Combine(local, "MyLocal.cfg"), "written by the mod " + Guid.NewGuid());
        Check(WhatsNew.Compare(steady, WhatsNew.Look(second, plugins2, ws), second).Count == 0, "a settings file the mod writes into its own folder is no update");
        File.WriteAllText(Path.Combine(a, "extra.dll"), "files changed, Steam time not");
        Check(WhatsNew.Compare(steady, WhatsNew.Look(second, plugins2, ws), second).Count == 0, "a Workshop item: Steam's update time decides, not its files");
        File.WriteAllText(Path.Combine(fresh, "pack.ini"), "data changed");
        var dataNews = WhatsNew.Compare(steady, WhatsNew.Look(second, plugins2, ws), second);
        Check(dataNews.Count == 1 && dataNews[0].Key == "Fresh", "a local mod's changed files are an update");
        WhatsNew.AddNotes(dataNews, null);
        Check(dataNews[0].Lines.SequenceEqual(new[] { "Same version number, new files (a new build)." }), "same version, new files: said so when the author wrote nothing");
        var oldStamps = WhatsNew.Look(second, plugins2, ws);
        foreach (SeenMod m in oldStamps.Values) m.Stamp = "3:100:5";      // a file written by the first What's new build
        File.WriteAllText(Path.Combine(fresh, "pack2.ini"), "x");
        Check(WhatsNew.Compare(oldStamps, WhatsNew.Look(second, plugins2, ws), second).Count == 0, "a stamp from the older Keel is not compared: no UPDATED for all after the update");

        // Keel's own folder has no plugin entry: its version is added by hand, so a new Keel shows its version jump.
        string own = Path.Combine(local, "SeaPowerKeel.dll");
        var k1 = WhatsNew.Look(second, plugins2, ws, own, "dev.sewerlabs.seapowerkeel", "0.4.0");
        var k2 = WhatsNew.Look(second, plugins2, ws, own, "dev.sewerlabs.seapowerkeel", "0.5.0");
        NewsItem keelNews = WhatsNew.Compare(k1, k2, second).FirstOrDefault(n => n.Key == "MyLocal");
        Check(keelNews is { From: "0.4.0", To: "0.5.0" }, "Keel's own folder: its version jump");
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
        Check(caught.State == PluginState.NotStarted && caughtNotes.Count == 1 && caughtNotes[0].Contains("Beta"), "a crash after start-up with the mod on the catcher's stack: named and held out, no hunt needed");
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

    private static void EuromodChecks(string tmp)
    {
        Console.WriteLine("12. Euromod setup line: which of the three stages runs (Main / + AC Pack / + Extended Guidance)");
        string main = Path.Combine(tmp, "eu", ModSetups.EuromodMain.ToString());
        string ac = Path.Combine(tmp, "eu", ModSetups.EuromodAcPack.ToString());
        string local = Path.Combine(tmp, "eu", "MyAcPackCopy");
        foreach (string d in new[] { main, ac, local })
            Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(local, ModSetups.AcPackMarker), "x");
        var fMain = new ModFolder { Path = main, Name = main, SteamId = ModSetups.EuromodMain, Enabled = true };
        var fAc = new ModFolder { Path = ac, Name = ac, SteamId = ModSetups.EuromodAcPack, Enabled = true };
        var fLocal = new ModFolder { Path = local, Name = "MyAcPackCopy", Enabled = true };
        var times = new Dictionary<ulong, long> { [ModSetups.EuromodAcPack] = new DateTimeOffset(2026, 11, 11, 20, 26, 0, TimeSpan.Zero).ToUnixTimeSeconds() };

        Check(ModSetups.Detect(new ModFolder[0], null, null).Line() == null, "no Euromod: no line");
        Check(ModSetups.Detect(new[] { fMain }, null, null).Line() == "Euromod setup: Euromod (Main Pack).", "stage 1: Main Pack only");
        string s2 = ModSetups.Detect(new[] { fMain, fAc }, null, times).Line();
        Check(s2.StartsWith("Euromod setup: Euromod + AC Pack (updated 2026-11-1") && s2.Contains("Extended Guidance off"), "stage 2: + AC Pack, its update date, EG off when submods.ini is missing: " + s2);
        string ini = "; AC Pack submods.\n[Submods]\neuromod.extended-guidance=true\nTestRange=false\nHullDamage=true\nBroken=maybe\n";
        string s3 = ModSetups.Detect(new[] { fMain, fAc }, ini, times).Line();
        Check(s3.Contains("+ Extended Guidance ON (experimental)") && s3.EndsWith("AC Pack submods off: TestRange."), "stage 3: EG on, switched-off submods listed: " + s3);
        Check(!ModSetups.Detect(new[] { fMain, fAc }, "[Submods]\neuromod.extended-guidance=false\n", null).Eg, "EG=false: off");
        Check(!ModSetups.Detect(new[] { fMain, fAc }, "[Other]\neuromod.extended-guidance=true\n", null).Eg, "only [Submods] counts");
        Check(ModSetups.Detect(new[] { fLocal }, null, null).Line().Contains("AC Pack without the Euromod Main Pack"), "a local AC Pack copy is recognized by ACPackSettings.dll, and a missing Main Pack is said");
        Check(ModSetups.SubmodsIni(@"C:\LocalLow\Triassic Games\Sea Power").EndsWith(@"Mods\ACPack\submods.ini"), "the AC Pack's switches are read from Mods\\ACPack\\submods.ini");

        var oldTimes = new Dictionary<ulong, long> { [ModSetups.EuromodAcPack] = ModSetups.AcPackFix09 - 86400 };
        var old = ModSetups.Detect(new[] { fMain, fAc }, null, oldTimes, "0.9.1-261112");
        Check(old.AcPackOld && old.Warning() != null && old.Line().Contains(", OLD)"), "an AC Pack older than its 0.9 fix on game 0.9.1: warned, OLD in the line: " + old.Line());
        Check(ModSetups.Detect(new[] { fMain, fAc }, null, times, "0.9.1-261112").Warning() == null, "the fixed AC Pack (2026-11-11 20:26 UTC and later): no warning");
        Check(ModSetups.Detect(new[] { fMain, fAc }, null, oldTimes, "0.8.7").Warning() == null && ModSetups.Detect(new[] { fMain, fAc }, null, oldTimes, null).Warning() == null,
            "an old AC Pack on 0.8.7 or an unknown game version: no warning");
        Check(ModSetups.Detect(new[] { fLocal }, null, oldTimes, "0.9.1").Warning() == null, "a local AC Pack copy (no Steam time): no warning");

        // Start early (opt-in, rgreat 2026-11-12): _info.ini [Keel] StartEarly=true; a mod that needs a late one waits too.
        Check(EarlyStart.InInfoIni("[Language_en]\nName=X\n\n[Keel]\nStartEarly=true\n") && !EarlyStart.InInfoIni("[Language_en]\nStartEarly=true\n")
              && !EarlyStart.InInfoIni("[Keel]\nStartEarly=false\n") && EarlyStart.InInfoIni("[keel]\r\nstartearly = TRUE ; yes\r\n"),
              "start early: only [Keel] StartEarly=true counts (any case, comments allowed)");
        var eA = new PluginEntry { Guid = "a", Name = "A", StartEarly = true };
        var eB = new PluginEntry { Guid = "b", Name = "B", StartEarly = true };
        eB.Dependencies.Add(new Dependency { Guid = "c" });
        var eC = new PluginEntry { Guid = "c", Name = "C" };
        var eD = new PluginEntry { Guid = "d", Name = "D", StartEarly = true };
        var eE = new PluginEntry { Guid = "e", Name = "E" };
        eE.LoadBefore.Add("d");
        var whyLate = new List<string>();
        var earlySet = EarlyStart.Set(new List<PluginEntry> { eA, eB, eC, eD, eE }, (p, why) => whyLate.Add(p.Guid));
        Check(earlySet.SetEquals(new[] { eA }) && whyLate.Count == 2,
              "start early: A early; B waits (needs C, which waits); D waits (E must load before it)");
        // What a sent report says in one line (player reports 2026-11-12: game "?", every mail titled "Crash report").
        string rep = "R.A.T.S. Keel 0.5.0 (stub 1.0.0) – Sea Power 0.9.1-261112, 3D globe map – 2026-11-12 15:00\nEuromod setup: Euromod + AC Pack.\n41 running, 2 failed\n\n" +
                     "[FAILED] Taigei (x.taigei) – Workshop 1, T.dll\n[RUNNING] R.A.T.S. Radio 1.5.1 (dev.sewerlabs.seapowerradio) – SeaPowerRadio\n[ERRORS] Automatic SAR (x.sar) – Workshop 2\n";
        Check(ReportFacts.Game("3D globe map", rep, "") == "0.9.1-261112", "game build from the report's first line, not the map name");
        Check(ReportFacts.Game("", "R.A.T.S. Keel 0.4.0 – Sea Power, 3D globe map – x\n17 running\n", "x Version 0.9.0-261111 y") == "0.9.0-261111", "an old report: the build from Keel's log");
        Check(ReportFacts.Summary(rep) == "41 running, 2 failed: Taigei, Automatic SAR", "summary: the counts line after a multi-line header, then the problem mods: " + ReportFacts.Summary(rep));

        // The report window's EG button writes the AC Pack's own switch, leaving every other line as it was.
        Check(ModSetups.SetSubmod(null, "euromod.extended-guidance", true) == "[Submods]\neuromod.extended-guidance=true\n", "EG on with no file: the section is made");
        string withEg = ModSetups.SetSubmod(ini, "euromod.extended-guidance", false);
        Check(withEg == ini.Replace("euromod.extended-guidance=true", "euromod.extended-guidance=false"), "EG off: only that line changes");
        string other = "[Other]\nX=1\n[Submods]\nTestRange=false\n\n[More]\nY=2\n";
        Check(ModSetups.SetSubmod(other, "euromod.extended-guidance", true) == "[Other]\nX=1\n[Submods]\nTestRange=false\neuromod.extended-guidance=true\n\n[More]\nY=2\n", "EG added at the end of [Submods], other sections kept");
        Check(ModSetups.ReadSubmods(ModSetups.SetSubmod("; note\n", "euromod.extended-guidance", true))["euromod.extended-guidance"], "what the button writes reads back as on");
    }

    private static void TakeoverChecks(string tmp)
    {
        Console.WriteLine("11. Takeover: while Keel is on, other loaders do not start; Keel installs and updates its patcher");
        string[] withStub = { "io.github.seapower_modders.anchorchain_preloader", Takeover.StubGuid };
        Check(Takeover.WhyNot(withStub, true, "") == null, "Keel on, stub there: other loaders are kept from starting");
        Check(Takeover.WhyNot(new[] { "alpine.seapower.modloader" }, true, "") != null, "no stub in BepInEx\\plugins: nothing is taken out");
        Check(Takeover.WhyNot(withStub, false, "") != null, "Keel switched off in the mod list: the other loader starts");
        Check(Takeover.WhyNot(withStub, true, "[Loading]\nTakeOver = false\n") != null, "TakeOver = false in Keel's settings: the other loader starts");
        Check(Takeover.WhyNot(withStub, true, "[Report]\nTakeOver = false\n[Loading]\nTakeOver = true\n") == null, "only [Loading] TakeOver counts");
        Check(Takeover.IsOtherLoader("alpine.seapower.modloader") && Takeover.IsOtherLoader("io.github.seapower_modders.anchorchain_preloader")
              && Takeover.IsOtherLoader("IO.GITHUB.SEAPOWER_MODDERS.ANCHORCHAIN") && !Takeover.IsOtherLoader("dev.sewerlabs.seapowerkeel.stub")
              && !Takeover.IsOtherLoader("anchorchain.sonaraudio") && !Takeover.IsOtherLoader(null),
              "Anchor Chain and Alpine are recognized by plugin id; Keel and ordinary mods are not");

        var rec = new Takeover.Record { WrittenUtc = new DateTime(2026, 11, 11, 20, 0, 0, DateTimeKind.Utc) };
        rec.Kept.Add(new Takeover.Blocked { Guid = "alpine.seapower.modloader", Name = "Alpine's Mod Loader", File = @"C:\g\BepInEx\plugins\AnchorChain.Preloader.dll" });
        string recPath = Path.Combine(tmp, "Keel-takeover.txt");
        File.WriteAllText(recPath, rec.Write());
        Takeover.Record back = Takeover.Record.Read(recPath);
        Check(back != null && back.WrittenUtc == rec.WrittenUtc && back.Kept.Count == 1 && back.Kept[0].Name == "Alpine's Mod Loader"
              && back.Kept[0].File.EndsWith("AnchorChain.Preloader.dll") && back.Off == null && back.Error == null, "the record says which loader was kept from starting");
        File.WriteAllText(recPath, new Takeover.Record { WrittenUtc = rec.WrittenUtc, Off = "R.A.T.S. Keel is switched off" }.Write());
        back = Takeover.Record.Read(recPath);
        Check(back.Off == "R.A.T.S. Keel is switched off" && back.Kept.Count == 0, "and why it took nothing out");
        File.WriteAllText(recPath, "garbage");
        Check(Takeover.Record.Read(recPath) == null && Takeover.Record.Read(Path.Combine(tmp, "none.txt")) == null, "a broken or missing record counts as none");

        string keelDir = Path.Combine(tmp, "keelmod");
        string patchers = Path.Combine(tmp, "game", "BepInEx", "patchers");
        Directory.CreateDirectory(keelDir);
        string source = Path.Combine(keelDir, Takeover.PatcherFile);
        Check(Takeover.Install(source, patchers, out bool changed).Contains("missing") && !changed, "no patcher in Keel's folder: nothing written, the reason given");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3 });
        Check(Takeover.Install(source, patchers, out changed) == "installed" && changed, "first start: the patcher is installed");
        Check(Takeover.Install(source, patchers, out changed) == "current" && !changed, "next start: it is current, nothing written");
        File.WriteAllBytes(source, new byte[] { 4, 5, 6, 7 });
        string target = Path.Combine(patchers, Takeover.PatcherFile);
        string result;
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))   // as BepInEx holds a loaded patcher
            result = Takeover.Install(source, patchers, out changed);
        Check(result == "updated" && File.ReadAllBytes(target).SequenceEqual(new byte[] { 4, 5, 6, 7 }) && File.Exists(target + ".old"),
              "a Keel update brings a new patcher even while the old one is in use (it is set aside as .old)");
        Takeover.Install(source, patchers, out _);
        Check(!File.Exists(target + ".old"), "the .old copy is cleared at the next start");
        Check(Directory.GetFiles(patchers, "*.dll").Length == 1, "BepInEx sees one patcher DLL only");

        // Setup installs it too, and Restore takes it away again.
        string game = Path.Combine(tmp, "setup-takeover", "steamapps", "common", "Sea Power");
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(game, SetupCore.ExeName), "x");
        File.WriteAllText(Path.Combine(game, "winhttp.dll"), "theirs");
        File.WriteAllText(Path.Combine(game, "BepInEx", "core", "BepInEx.dll"), "theirs");
        var s = new SetupCore(game);
        s.Install(new byte[] { 1 }, Array.Empty<byte>(), new byte[] { 9, 9 });
        string setupPatcher = Path.Combine(game, "BepInEx", "patchers", Takeover.PatcherFile);
        Check(File.Exists(setupPatcher), "KeelSetup installs the takeover patcher");
        s.Restore();
        Check(!File.Exists(setupPatcher) && File.Exists(Path.Combine(game, "winhttp.dll")), "Restore removes it and leaves BepInEx that was there");
    }

    private static void SetupChecks(string tmp)
    {
        string game = Path.Combine(tmp, "steamapps", "common", "Sea Power");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, SetupCore.ExeName), "x");
        byte[] stub = { 1, 2, 3 };
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var a = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                foreach (string n in new[] { "winhttp.dll", "BepInEx/core/BepInEx.dll", "../escape.txt" })
                    using (var w = new StreamWriter(a.CreateEntry(n).Open()))
                        w.Write("x");
            zip = ms.ToArray();
        }
        var s = new SetupCore(game);
        Check(s.IsGameFolder && !s.HasBepInEx && !s.CanRestore, "a bare game: no BepInEx, nothing to restore");
        s.Install(stub, zip);
        Check(s.HasBepInEx && s.HasStub && s.CanRestore, "install on a bare game adds BepInEx and the starter file");
        Check(!File.Exists(Path.Combine(game, "..", "escape.txt")), "a zip entry cannot write outside the game folder");
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "cache"));
        File.WriteAllText(Path.Combine(game, "BepInEx", "cache", "made-by-the-game.txt"), "keep");
        s.Install(stub, zip);
        Check(File.ReadAllLines(Path.Combine(game, "keel-setup.txt")).Distinct().Count() == File.ReadAllLines(Path.Combine(game, "keel-setup.txt")).Length, "installing twice records nothing twice");
        s.Restore();
        Check(!s.HasBepInEx && !s.HasStub && !s.CanRestore, "restore removes what setup added");
        Check(File.Exists(Path.Combine(game, "BepInEx", "cache", "made-by-the-game.txt")) && File.Exists(Path.Combine(game, SetupCore.ExeName)), "restore leaves files setup did not create");

        string plugins = Path.Combine(game, "BepInEx", "plugins");
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(game, "winhttp.dll"), "theirs");
        File.WriteAllText(Path.Combine(game, "BepInEx", "core", "BepInEx.dll"), "theirs");
        File.WriteAllText(Path.Combine(plugins, "AnchorChain.Preloader.dll"), "other");
        s.Install(stub, zip);
        Check(!s.HasOtherPreloader && File.Exists(Path.Combine(plugins, "AnchorChain.Preloader.dll.keel-off")), "another loader's preloader is set aside, not deleted");
        s.Restore();
        Check(s.HasOtherPreloader && s.HasBepInEx && File.ReadAllText(Path.Combine(game, "winhttp.dll")) == "theirs", "restore brings it back and leaves a BepInEx setup did not install");
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
        Check(ModList.ApplyPackOff(tmp, ini) == "77" && ModList.ListOn(File.ReadAllText(ini)).SequenceEqual(new[] { "88" }) && !File.Exists(Path.Combine(tmp, CrashText.PackOffFile)), "the crash window's choice switches the pack off in the game's list, once");
        string edited = ModList.SetFolders("Mod1Directory=A,True,0\r\nMod2Directory=B,True,11\r\nOther=1\r\n", new[] { "B" }, new[] { "B" });
        Check(edited.Contains("Mod2Directory=B,False,11") && edited.Contains("Mod1Directory=A,True,0") && edited.Contains("Other=1\r\n"), "one folder off, its id and every other line kept");
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
