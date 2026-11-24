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

    // Shapes of a load-screen postfix (the Alternative mods use the first).
    private static void FakeLoadScreenPostfix(bool __result) { }
    private static void FakeNeedsMore(bool __result, int something) { }

    private static void OtherMapBuild()
    {
        static Preflight.Result R(int uses, int classLoad = 0)
        {
            var r = new Preflight.Result();
            for (int i = 0; i < uses; i++) r.Uses.Add("LiveMap.Thing" + i);
            for (int i = 0; i < classLoad; i++) r.ClassLoad.Add("Gone" + i);
            return r;
        }
        string dir = Path.Combine(Path.GetTempPath(), "mod");
        string globe = Path.Combine(dir, "MyMod.dll"), flat = Path.Combine(dir, "MyModFlat.dll");
        var both = new Dictionary<string, Preflight.Result> { [globe] = R(0), [flat] = R(11) };
        Check(Preflight.MatchingMapBuild(flat, both[flat], both) == "MyMod.dll", "globe game: the flat build lacking 11 parts beside a clean globe build is the other map's build");
        Check(Preflight.MatchingMapBuild(globe, both[globe], both) == null, "the clean partner gets no note");
        var old = new Dictionary<string, Preflight.Result> { [globe] = R(7), [flat] = R(0) };
        Check(Preflight.MatchingMapBuild(globe, old[globe], old) == "MyModFlat.dll", "0.8.7: the globe build lacking parts beside a clean flat build gets the note");
        var broken = new Dictionary<string, Preflight.Result> { [globe] = R(2), [flat] = R(11) };
        Check(Preflight.MatchingMapBuild(flat, broken[flat], broken) == null, "both builds lack parts: still a warning");
        var alone = new Dictionary<string, Preflight.Result> { [flat] = R(11) };
        Check(Preflight.MatchingMapBuild(flat, alone[flat], alone) == null, "no partner in the folder: still a warning");
        var elsewhere = new Dictionary<string, Preflight.Result> { [Path.Combine(Path.GetTempPath(), "other", "MyMod.dll")] = R(0), [flat] = R(11) };
        Check(Preflight.MatchingMapBuild(flat, elsewhere[flat], elsewhere) == null, "a partner in another folder does not count");
    }

    // The folders switched on in a usersettings.ini text, in its order.
    private static List<string> ListOn(string settings) =>
        settings.Replace("\r\n", "\n").Split('\n').Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^Mod\d+Directory=([^,]+),True,"))
                .Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();

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
        Program.ResolveFrom(managed);
        string tmp = Path.Combine(Path.GetTempPath(), "keel-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            Console.WriteLine("Scanner: only the exact Anchor Chain DLL names are left out, and every one is listed");
            Console.WriteLine("_info.ini [Compatibility], by the game's rules");
            InfoIni();
            ScannerExclusions(tmp, managed, core);
            Console.WriteLine("Scanner: Anchor Chain plugin classes must be public (exported)");
            ExportedTypes(tmp, managed, core);
            Console.WriteLine("Entry point through the interface");
            EntryPoints();
            Console.WriteLine("The other map kind's build beside its partner: a note, not a warning");
            OtherMapBuild();
            Console.WriteLine("Sequence: load all first, start in plan order, drop dependants of a failed load, hand over to a running Anchor Chain");
            Sequence();
            LoadOrderWarnings();
            Console.WriteLine("Is Anchor Chain running: Keel's own load of its DLL does not count");
            AnchorChainRunning();
            Console.WriteLine("[ACConfig]");
            Config(tmp);
            Console.WriteLine("Mods that call AnchorChainLoader.FindFile (Sonar Audio Loader) when Keel runs the mods");
            LoaderShim(tmp);
            Console.WriteLine("[ACIncompatibility]: Keel's behaviour, said in the report");
            Incompatibility();
            Console.WriteLine("Patch guard: the member and the patched method come out of the error and its stack, (taking a patch off is run in the game: Harmony does not start outside it)");
            PatchGuard();
            CrashGuardChecks(tmp);
            PreventionChecks(tmp, managed, core);
            SetupChecks(tmp);
            WhatsNewChecks(tmp);
            TakeoverChecks(tmp);
            EuromodChecks(tmp);
            LoadScreenEndChecks();
            EarlyStartChecks();
            StartupDiscoveryChecks(tmp);
            RequiredItemChecks();
            ReportFactsChecks(tmp);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception) { }
        }
        Console.WriteLine();
        Console.WriteLine($"{checks - failures} of {checks} checks passed" + (failures > 0 ? $", {failures} FAILED" : ""));
        return failures > 0 ? 1 : 0;
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
        Console.WriteLine("What's new: what changed since the last start, and the authors' words for it");
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
        WhatsNew.AddNotes(news, id => id == 111 ? page : null);
        Check(alpha.Source == "Workshop" && alpha.Lines.Count == 3, "a Workshop mod gets its Workshop notes");
        NewsItem loc = news.First(n => n.Key == "MyLocal");
        Check(loc.Source == "CHANGELOG" && loc.Lines.Count == 3, "a local mod gets its CHANGELOG");
        var offline = WhatsNew.Compare(back, WhatsNew.Look(second, plugins2, ws), second);
        File.WriteAllText(Path.Combine(a, "CHANGELOG.md"), "## 1.1.0\n- from the file\n");
        WhatsNew.AddNotes(offline, _ => throw new System.Net.WebException("offline"));
        Check(offline.First(n => n.Key == "111").Lines.SequenceEqual(new[] { "from the file" }), "Steam unreachable: the CHANGELOG instead, no error");
        Check(WhatsNew.Text(news).Contains("UPDATED  Alpha Mod  1.0.0 → 1.1.0   (from Workshop)"), "the report text");
        WhatsNewNoteLines(page, local);
        WhatsNewStamps(ws, a, local, fresh, second, plugins2);

    }


    // How notes are read: Workshop page entries in a time span, Steam's and Markdown's marks, CHANGELOG sections.
    private static void WhatsNewNoteLines(string page, string local)
    {
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
    }

    // What counts as an update: Steam's time for a Workshop item, the files for a local mod, never a settings file the mod writes.
    private static void WhatsNewStamps(string ws, string a, string local, string fresh, List<ModFolder> second, List<PluginEntry> plugins2)
    {
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

    private static void LoadScreenEndChecks()
    {
        Console.WriteLine("Load screen end: mods that wait for it are called once it has passed");
        Check(LoadScreenEnd.Args(typeof(SelfTest).GetMethod(nameof(FakeLoadScreenPostfix), BindingFlags.NonPublic | BindingFlags.Static).GetParameters()) is { Length: 1 } a1 && (bool)a1[0] == false
              && LoadScreenEnd.Args(typeof(SelfTest).GetMethod(nameof(FakeNeedsMore), BindingFlags.NonPublic | BindingFlags.Static).GetParameters()) == null,
            "load screen end: a postfix with __result gets false; one that needs other arguments is left");

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
        Program.ResolveFrom(managed);
        Assembly game = Assembly.LoadFrom(Path.Combine(managed, "Seapower-Scripts.dll"));
        Console.WriteLine($"Game build: 3D globe map ({managed})");
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
        CrashText.Journal V(params string[] lines) => CrashText.ReadJournal(lines);
        Check(!V().Unclean, "no journal: nothing to say");
        Check(!V("session\tx", "start\ta", "done\t", "quit\t").Unclean, "a normal quit is clean");
        CrashText.Journal during = V("session\tx", "load\ta", "start\tb");
        Check(during.Unclean && during.Step == "start" && during.Guid == "b", "ended while starting b: names b");
        CrashText.Journal after = V("session\tx", "start\tb", "done\t", "scene\tmission");
        Check(after.Unclean && after.Guid == null && after.Scene == "mission", "ended after start-up: no mod from the journal, the scene is known");

        Check(CrashText.CrashBlock("all fine\nno problem") == null, "a log without a crash gives no crash block");
        var a = new PluginEntry { Guid = "a", TypeName = "AlphaMod.Plugin", Order = 0 };
        var b = new PluginEntry { Guid = "b", TypeName = "BetaMod.Plugin", Order = 1 };
        var c = new PluginEntry { Guid = "c", TypeName = "SeaPower.Plugin", Order = 2 };
        var mods = new[] { a, b, c };
        string block = CrashText.CrashBlock("before\r\nCrash!!!\r\n0x00 (Mono JIT code) BetaMod.Hooks:Prefix ()\r\n0x01 (Mono JIT code) AlphaMod.Other:Run ()");
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
        File.WriteAllLines(Path.Combine(caughtDir, KeelNames.JournalFile), new[] { "session\tx", "start\tb", "done\t" });
        File.WriteAllText(Path.Combine(caughtDir, KeelNames.StackFile), stack);
        var caughtNotes = new List<string>();
        var caught = new PluginEntry { Guid = "b", Name = "Beta", TypeName = "BetaMod.Plugin", Dll = betaDll };
        var caughtRun = new CrashGuard(caughtDir, null);
        caughtRun.Apply(new[] { caught }, true, caughtNotes.Add);
        caughtRun.Mark("quit", "");
        Check(caught.State == PluginState.NotStarted && caughtNotes.Count == 1 && caughtNotes[0].Contains("Beta"), "a crash after start-up with the mod on the catcher's stack: named and held out, no hunt needed");
        caughtRun.WriteModsFile(new[] { caught });
        Check(File.ReadAllText(Path.Combine(caughtDir, KeelNames.ModsFile)).Contains("b\tBeta\tBetaMod"), "the watcher's list has id, name and code root");

        string keptDir = Path.Combine(tmp, "crashkept");
        Directory.CreateDirectory(keptDir);
        File.WriteAllLines(Path.Combine(keptDir, KeelNames.JournalFile), new[] { "session\tx", "start\tb", "done\t" });
        File.WriteAllText(Path.Combine(keptDir, KeelNames.StackFile), stack);
        File.WriteAllText(Path.Combine(keptDir, KeelNames.KeepFile), "b");
        var kept = new PluginEntry { Guid = "b", Name = "Beta", TypeName = "BetaMod.Plugin", Dll = betaDll };
        var keptRun = new CrashGuard(keptDir, null);
        keptRun.Apply(new[] { kept }, true, _ => { });
        keptRun.Mark("quit", "");
        Check(kept.State == PluginState.Pending && !File.Exists(Path.Combine(keptDir, KeelNames.KeepFile)), "the player unticked 'keep it off': the mod runs, and the answer counts once");

        string dir = Path.Combine(tmp, "crashguard");
        Directory.CreateDirectory(dir);
        string dll = Path.Combine(dir, "beta.dll");
        File.WriteAllText(dll, "x");
        PluginEntry Mod() => new() { Guid = "b", Name = "Beta", TypeName = "BetaMod.Plugin", Dll = dll };
        File.WriteAllLines(Path.Combine(dir, KeelNames.JournalFile), new[] { "session\tx", "start\tb" });
        var notes = new List<string>();
        PluginEntry first = Mod();
        var run1 = new CrashGuard(dir, null);
        run1.Apply(new[] { first }, true, notes.Add);
        run1.Mark("quit", "");
        Check(first.State == PluginState.NotStarted && notes.Count == 1 && notes[0].Contains("Beta"), "the mod that took the game down is named and kept out at the next start");
        Check(File.Exists(Path.Combine(dir, KeelNames.CrashFile)), "Keel-crash.txt is written");
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
        Check(third.State == PluginState.Pending && !File.Exists(Path.Combine(dir, KeelNames.HeldFile)), "an updated mod gets another chance");
    }

    private static void EuromodChecks(string tmp)
    {
        Console.WriteLine("Euromod setup line: which of the three stages runs (Main / + AC Pack / + Extended Guidance)");
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

        // The report window's EG button writes the AC Pack's own switch, leaving every other line as it was.
        Check(ModSetups.SetSubmod(null, "euromod.extended-guidance", true) == "[Submods]\neuromod.extended-guidance=true\n", "EG on with no file: the section is made");
        string withEg = ModSetups.SetSubmod(ini, "euromod.extended-guidance", false);
        Check(withEg == ini.Replace("euromod.extended-guidance=true", "euromod.extended-guidance=false"), "EG off: only that line changes");
        string other = "[Other]\nX=1\n[Submods]\nTestRange=false\n\n[More]\nY=2\n";
        Check(ModSetups.SetSubmod(other, "euromod.extended-guidance", true) == "[Other]\nX=1\n[Submods]\nTestRange=false\neuromod.extended-guidance=true\n\n[More]\nY=2\n", "EG added at the end of [Submods], other sections kept");
        Check(ModSetups.ReadSubmods(ModSetups.SetSubmod("; note\n", "euromod.extended-guidance", true))["euromod.extended-guidance"], "what the button writes reads back as on");
    }

    private static void EarlyStartChecks()
    {
        Console.WriteLine("Start early: a mod that asks for it starts before the load screen ends");
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
        Check(EarlyStart.InInfoIni("[Keel]\nBlockingStart=true\n", EarlyStart.BlockingInfoKey) && !EarlyStart.InInfoIni("[Keel]\nStartEarly=true\n", EarlyStart.BlockingInfoKey)
              && !EarlyStart.InInfoIni("[Keel]\nBlockingStart=true\n"),
              "blocking start: only [Keel] BlockingStart=true counts for it, and it is not a plain StartEarly");

        var bA = new PluginEntry { Guid = "a", Name = "A", StartEarly = true, BlockingStart = true };
        var bB = new PluginEntry { Guid = "b", Name = "B", StartEarly = true, BlockingStart = true };
        bB.Dependencies.Add(new Dependency { Guid = "c" });
        var bC = new PluginEntry { Guid = "c", Name = "C", StartEarly = true };
        var blockSet = EarlyStart.Set(new List<PluginEntry> { bA, bB, bC }, null, blocking: true);
        var plainSet = EarlyStart.Set(new List<PluginEntry> { bA, bB, bC }, null);
        Check(blockSet.SetEquals(new[] { bA }) && plainSet.SetEquals(new[] { bA, bB, bC }),
              "blocking start: A blocks; B needs C, which does not block, so B starts early without holding the game; all three start early");
    }

    private static void StartupDiscoveryChecks(string tmp)
    {
        var folder = new ModFolder { Path = Directory.CreateDirectory(Path.Combine(tmp, "timing")).FullName };
        var other = new ModFolder { Path = Directory.CreateDirectory(Path.Combine(tmp, "ordinary")).FullName, Enabled = true };
        string ini = Path.Combine(other.Path, "_info.ini");
        File.WriteAllText(ini, "[Keel]\nStartEarly=true");
        File.WriteAllBytes(Path.Combine(other.Path, "native.dll"), new byte[] { 0 });
        var folders = new[] { other, folder };
        string dll = Path.Combine(Directory.CreateDirectory(Path.Combine(folder.Path, "plugins")).FullName, "ScreenMod.dll");
        using var module = Mono.Cecil.ModuleDefinition.CreateModule("ScreenMod", Mono.Cecil.ModuleKind.Dll);
        var ctor = typeof(AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) });
        var attribute = new Mono.Cecil.CustomAttribute(module.ImportReference(ctor));
        var text = module.TypeSystem.String;
        attribute.ConstructorArguments.Add(new Mono.Cecil.CustomAttributeArgument(text, EarlyStart.BlockingMetadataKey));
        attribute.ConstructorArguments.Add(new Mono.Cecil.CustomAttributeArgument(text, "true"));
        module.Assembly.CustomAttributes.Add(attribute);
        module.Write(dll);

        Check(EarlyStart.Discover(folders) == StartupTiming.Early, "a disabled DLL cannot override an enabled StartEarly INI");
        folder.Enabled = true;
        File.Delete(ini);
        Check(EarlyStart.Discover(folders) == StartupTiming.Blocking, "a nested DLL-only blocker requests the first-screen hook");
        attribute.ConstructorArguments[0] = new Mono.Cecil.CustomAttributeArgument(text, EarlyStart.MetadataKey);
        module.Write(dll);
        Check(EarlyStart.Discover(folders) == StartupTiming.Early, "an ordinary early DLL does not request the blocking hook");
        File.WriteAllText(ini, "[Keel]\nBlockingStart=true");
        Check(EarlyStart.Discover(folders) == StartupTiming.Blocking, "an INI blocker takes priority over an early DLL");
        Check(EarlyStart.Discover(Array.Empty<ModFolder>()) == StartupTiming.Normal, "no mod folders means no early start");
    }

    private static void RequiredItemChecks()
    {
        Console.WriteLine("Required items: a mod names the Workshop items it needs; the card offers one that is missing");
        // _info.ini [Keel] Requires=<Workshop id>, <plugin id>, <name>: any mod, any item.
        List<RequiredItem> named = RequiredItems.InInfoIni(
            "[Language_en]\nRequires=9, not.here, Wrong section\n\n[Keel]\nStartEarly=true\nRequires=3840000000, dev.x.lib, The Lib ; comment\n" +
            "requires2 = 3840000001,dev.y.lib\nRequires=0, zero.id, Zero\nRequires=abc, bad.id, Bad\nRequires=5\nRequiresMore=6, a.b, C\n");
        Check(named.Count == 2 && named[0].WorkshopId == 3840000000 && named[0].PluginId == "dev.x.lib" && named[0].Name == "The Lib"
              && named[1].WorkshopId == 3840000001 && named[1].Name == "dev.y.lib" && named[0].PageUrl == "steam://url/CommunityFilePage/3840000000",
              "required items: the [Keel] Requires lines count (several, any case, comments allowed); other sections, a zero or bad id and a short line do not");
        Check(RequiredItems.InInfoIni(null).Count == 0 && RequiredItems.OfFolder(@"Z:\\no\\such\\folder").Count == 0, "required items: no file, no items");

        var lib = new RequiredItem { WorkshopId = 77, PluginId = "dev.x.lib", Name = "The Lib" };
        PluginEntry needs = Ac("m.needs", "1.0");
        needs.Dependencies.Add(new Dependency { Guid = "dev.x.lib", Min = Version.Parse("1.0") });
        needs.RequiredItems.Add(lib);
        PluginEntry plain = Ac("m.plain", "1.0");
        plain.Dependencies.Add(new Dependency { Guid = "dev.y.lib" });
        Plan(new List<PluginEntry> { needs, plain });
        Check(needs.State == PluginState.NotStarted && needs.MissingItem == lib && needs.Reason.Contains("The Lib (dev.x.lib)") && needs.Reason.Contains("not installed or switched off"),
              "required items: a mod whose library is missing is kept out, the reason names the item and the card can offer it");
        Check(plain.State == PluginState.NotStarted && plain.MissingItem == null && plain.Reason.Contains("Needs dev.y.lib") && !plain.Reason.Contains("("),
              "required items: a mod that names no item keeps the plain reason and offers nothing");

        PluginEntry old = Ac("m.old", "1.0");
        old.Dependencies.Add(new Dependency { Guid = "dev.x.lib", Min = Version.Parse("1.2") });
        old.RequiredItems.Add(lib);
        PluginEntry oldLib = Ac("dev.x.lib", "1.1");
        Plan(new List<PluginEntry> { old, oldLib });
        Check(old.State == PluginState.NotStarted && old.MissingItem == lib && old.Reason.Contains("installed is 1.1"),
              "required items: a library that is too old is offered too (its item holds the newer one)");

        PluginEntry fine = Ac("m.fine", "1.0");
        fine.Dependencies.Add(new Dependency { Guid = "dev.x.lib", Min = Version.Parse("1.0") });
        fine.RequiredItems.Add(lib);
        PluginEntry there = Ac("dev.x.lib", "1.0");
        Plan(new List<PluginEntry> { fine, there });
        Check(fine.State == PluginState.Pending && fine.MissingItem == null && RequiredItems.Missing(new[] { fine, there }).Count == 0,
              "required items: nothing is offered while the library is there");

        PluginEntry second = Ac("m.second", "1.0");
        second.Dependencies.Add(new Dependency { Guid = "dev.x.lib" });
        second.RequiredItems.Add(lib);
        Plan(new List<PluginEntry> { needs, second });
        List<(RequiredItem item, int mods)> missing = RequiredItems.Missing(new[] { needs, second, plain });
        Check(missing.Count == 1 && missing[0].item == lib && missing[0].mods == 2, "required items: two mods waiting for one item are one offer with a count");

        // An item that is installed but switched off is switched on when a mod that is on needs it.
        var modOn = new ModFolder { Path = "modOn", Name = "modOn", Enabled = true };
        var modOff = new ModFolder { Path = "modOff", Name = "modOff", Enabled = false };
        var itemOff = new ModFolder { Path = "item77", Name = "77", SteamId = 77, Enabled = false };
        var otherOff = new ModFolder { Path = "item88", Name = "88", SteamId = 88, Enabled = false };
        var itemOn = new ModFolder { Path = "item99", Name = "99", SteamId = 99, Enabled = true };
        IEnumerable<RequiredItem> Wants(ModFolder f) => f == modOn || f == modOff ? new[] { lib, new RequiredItem { WorkshopId = 99, PluginId = "p", Name = "P" } } : new RequiredItem[0];
        List<ModFolder> toSwitch = RequiredItems.ToSwitchOn(new[] { modOn, modOff, itemOff, otherOff, itemOn }, Wants);
        Check(toSwitch.Count == 1 && toSwitch[0] == itemOff, "required items: the one off item a mod that is on needs is switched on; unneeded and already-on items are left");
        Check(RequiredItems.ToSwitchOn(new[] { modOff, itemOff }, Wants).Count == 0, "required items: a mod that is off switches nothing on");
    }

    private static void ReportFactsChecks(string tmp)
    {
        Console.WriteLine("Sent reports: the game build and a one-line summary, read from the report Keel wrote");
        // What a sent report says in one line (player reports 2026-11-12: game "?", every mail titled "Crash report").
        string rep = "R.A.T.S. Keel 0.5.0 (stub 1.0.0) – Sea Power 0.9.1-261112, 3D globe map – 2026-11-12 15:00\nEuromod setup: Euromod + AC Pack.\n41 running, 2 failed\n\n" +
                     "[FAILED] Taigei (x.taigei) – Workshop 1, T.dll\n[RUNNING] Some Mod 1.5.1 (com.example.somemod) – SomeMod\n[ERRORS] Automatic SAR (x.sar) – Workshop 2\n";
        Check(ReportFacts.Game("3D globe map", rep, "") == "0.9.1-261112", "game build from the report's first line, not the map name");
        Check(ReportFacts.Game("", "R.A.T.S. Keel 0.4.0 – Sea Power, 3D globe map – x\n17 running\n", "x Version 0.9.0-261111 y") == "0.9.0-261111", "an old report: the build from Keel's log");
        Check(ReportFacts.Summary(rep) == "41 running, 2 failed: Taigei, Automatic SAR", "summary: the counts line after a multi-line header, then the problem mods: " + ReportFacts.Summary(rep));
        // What Keel writes is what KeelWatcher reads back: both go by ReportFormat, so a reworded tag or counts line fails here.
        var folder = new ModFolder { Path = tmp, Name = "W" };
        var bad = new PluginEntry { Guid = "x.bad", Name = "Bad", Version = new Version(1, 2), Folder = folder, Dll = "B.dll" };
        bad.Stop(PluginState.RunningWithErrors, "threw");
        var off = new PluginEntry { Guid = "x.off", Name = "Off", Folder = folder, Dll = "O.dll" };
        off.Stop(PluginState.NotStarted, "needs y");
        var fine = new PluginEntry { Guid = "x.ok", Name = "Fine", Folder = folder, Dll = "F.dll", State = PluginState.Running };
        var all = new List<PluginEntry> { bad, off, fine };
        string written = Report.Text(all, "R.A.T.S. Keel 0.6.1 (stub 1.0.0) – Sea Power 0.9.3-261116\nEuromod setup: Euromod.", new string[0]);
        Check(ReportFacts.Summary(written) == "2 running, 1 with errors, 1 not started: Off 1.0, Bad 1.2", "a report Keel wrote reads back: counts line and problem mods: " + ReportFacts.Summary(written));
        Check(Enum.GetValues(typeof(PluginState)).Cast<PluginState>().All(s => ReportFormat.TryParseTag(ReportFormat.Tag(s), out PluginState back) && back == s),
              "every state's tag reads back as that state");

    }

    private static void TakeoverChecks(string tmp)
    {
        Console.WriteLine("Takeover: while Keel is on, other loaders do not start; Keel installs and updates its patcher");
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
        TakeoverRecordChecks(tmp);
        PatcherInstallChecks(tmp);
    }

    private static void TakeoverRecordChecks(string tmp)
    {
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
    }

    private static void PatcherInstallChecks(string tmp)
    {
        string keelDir = Path.Combine(tmp, "keelmod");
        string patchers = Path.Combine(tmp, "game", "BepInEx", "patchers");
        Directory.CreateDirectory(keelDir);
        string source = Path.Combine(keelDir, Takeover.PatcherFile);
        Check(Takeover.Install(source, patchers).Contains("missing") && !Directory.Exists(patchers), "no patcher in Keel's folder: nothing written, the reason given");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3 });
        Check(Takeover.Install(source, patchers) == "installed", "first start: the patcher is installed");
        Check(Takeover.Install(source, patchers) == "current", "next start: it is current, nothing written");
        File.WriteAllBytes(source, new byte[] { 4, 5, 6, 7 });
        string target = Path.Combine(patchers, Takeover.PatcherFile);
        string result;
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))   // as BepInEx holds a loaded patcher
            result = Takeover.Install(source, patchers);
        Check(result == "updated" && File.ReadAllBytes(target).SequenceEqual(new byte[] { 4, 5, 6, 7 }) && File.Exists(target + ".old"),
              "a Keel update brings a new patcher even while the old one is in use (it is set aside as .old)");
        Takeover.Install(source, patchers);
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
        File.WriteAllText(setupPatcher + ".old", "the copy Keel kept when it updated its patcher");
        s.Restore();
        Check(!File.Exists(setupPatcher + ".old"), "Restore also removes the old patcher copy Keel kept");
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
                foreach (string n in new[] { "winhttp.dll", "BepInEx/core/BepInEx.dll", "../escape.txt", "changelog.txt", "BepInEx/plugins/other.dll" })
                    using (var w = new StreamWriter(a.CreateEntry(n).Open()))
                        w.Write("x");
            zip = ms.ToArray();
        }
        var s = new SetupCore(game);
        Check(s.IsGameFolder && !s.HasBepInEx && !s.CanRestore, "a bare game: no BepInEx, nothing to restore");
        s.Install(stub, zip, null);
        Check(s.HasBepInEx && s.HasStub && s.CanRestore, "install on a bare game adds BepInEx and the starter file");
        Check(!File.Exists(Path.Combine(game, "..", "escape.txt")), "a zip entry cannot write outside the game folder");
        Check(!File.Exists(Path.Combine(game, "changelog.txt")) && !File.Exists(Path.Combine(game, "BepInEx", "plugins", "other.dll")), "only the Doorstop files and BepInEx's core files leave the release zip");
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "cache"));
        File.WriteAllText(Path.Combine(game, "BepInEx", "cache", "made-by-the-game.txt"), "keep");
        s.Install(stub, zip, null);
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
        s.Install(stub, zip, null);
        Check(!s.HasOtherPreloader && File.Exists(Path.Combine(plugins, "AnchorChain.Preloader.dll.keel-off")), "another loader's preloader is set aside, not deleted");
        string typeCache = Path.Combine(game, "BepInEx", "cache", "chainloader_typeloader.dat");
        File.WriteAllText(typeCache, "the takeover's entry");
        s.Restore();
        Check(!File.Exists(typeCache) && File.Exists(Path.Combine(game, "BepInEx", "cache", "made-by-the-game.txt")), "restore drops BepInEx's plugin cache, so the other loader is scanned again, and leaves the rest of the cache");
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

        // Issue keel-findobject-blame: a crash inside Unity's object search names neither its caller nor the last pack.
        string search = "Crash: access violation (0xC0000005) in UnityPlayer.dll\nFile: 900\tC:/x/workshop/content/1286220/77/a.ini\n" +
                        "0x01 (Mono JIT code) (wrapper managed-to-native) UnityEngine.Object:FindObjectsOfType (System.Type,bool)\n" +
                        "0x02 (Mono JIT code) UnityEngine.Object:FindObjectOfType (System.Type)\n" +
                        "0x03 (Mono JIT code) QuickStart.MenuRelabel:Update ()";
        Check(CrashText.InObjectSearch(search), "a crash with Unity's object search on top is an object-search crash");
        Check(CrashText.FirstHit(search, new[] { "QuickStart" }) == -1, "the mod that only called the search is not blamed");
        Check(!CrashText.LastPack(search, out _, out _), "nor is the pack loaded last");
        string notSearch = "Crash: x\n0x01 (Mono JIT code) UnityEngine.Object:Destroy (UnityEngine.Object)\n0x02 (Mono JIT code) QuickStart.MenuRelabel:Update ()";
        Check(!CrashText.InObjectSearch(notSearch) && CrashText.FirstHit(notSearch, new[] { "QuickStart" }) == 0, "another Unity call on top still blames the mod below it");
        string searchBelow = "Crash: x\n0x01 (Mono JIT code) QuickStart.Broken:Run ()\n0x02 (Mono JIT code) UnityEngine.Object:FindObjectOfType (System.Type)";
        Check(!CrashText.InObjectSearch(searchBelow), "a search below the mod's own frame does not clear the mod");
        Check(CrashText.InObjectSearch("  at UnityEngine.Object.FindObjectOfType (System.Type type) [0x00000]\n  at SeaPowerKeel.GameState.Find (System.Type type)"), "the managed 'at' form is read too");

        // Issue keel-build-suffix: lettered builds are matched whole.
        Check(ReportFacts.Build.Match("Sea Power 0.9.1-261112b – 3D globe map").Value == "0.9.1-261112b", "a lettered build keeps its letter");
        Check(ReportFacts.Build.Match("Sea Power 0.9.3-261116 – 3D globe map").Value == "0.9.3-261116", "a plain build is matched as before");

        // Issue keel-crash-without-data: a log that ends with Unity's normal shutdown is no crash.
        string shut = Path.Combine(tmp, "player-shut.log"), cut = Path.Combine(tmp, "player-cut.log");
        File.WriteAllText(shut, "lots\nInput System module state changed to: ShutdownInProgress.\nInput System polling thread exited.\nInput System module state changed to: Shutdown.\n");
        File.WriteAllText(cut, "lots\n[Info : Mod] starting\n");
        Check(CrashText.LogEndedNormally(shut) && !CrashText.LogEndedNormally(cut) && !CrashText.LogEndedNormally(Path.Combine(tmp, "none.log")), "a normal Unity shutdown is told from a log that just stops");
        Check(CrashText.CrashFileText("2026-11-18 10:00:00", null, null, "Menu", null).Contains("No crash record"), "a crash text without a record says so");

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
        File.WriteAllText(Path.Combine(tmp, KeelNames.PackOffFile), "77");
        Check(ModList.ApplyPackOff(tmp, ini) == "77" && ListOn(File.ReadAllText(ini)).SequenceEqual(new[] { "88" }) && !File.Exists(Path.Combine(tmp, KeelNames.PackOffFile)), "the crash window's choice switches the pack off in the game's list, once");
        string edited = ModList.SwitchOff("Mod1Directory=A,True,0\r\nMod2Directory=B,True,11\r\nOther=1\r\n", "B");
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

    // ---- exclusions ----

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

    // ---- exported types ----

    private static void ExportedTypes(string tmp, string managed, string core)
    {
        string dir = MakeFolder(tmp, "exp", "Mods.dll");
        List<PluginEntry> found = Scanner.Scan(new[] { Folder(dir) }, _ => { }, null, managed, core);
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

    // ---- entry point ----

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
        List<PluginEntry> all = ChainOfMods();
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
        SequenceFailedLoad();
        SequenceHandOver();
    }

    // A <- B <- C by dependency, plus D and E independent, in mod-list order D, C, B, A, E (so order is not the input order).
    private static List<PluginEntry> ChainOfMods()
    {
        var all = new List<PluginEntry> { Ac("d"), Ac("c", "1.0", "b"), Ac("b", "1.0", "a"), Ac("a"), Ac("e") };
        for (int i = 0; i < all.Count; i++)
            all[i].Order = i;
        return all;
    }

    // A cannot be loaded: B and C (which need it) do not start, D and E do.
    private static void SequenceFailedLoad()
    {
        List<PluginEntry> all = ChainOfMods();
        var log = new List<string>();
        var seq = new Sequencer
        {
            Prepare = p => { log.Add("P" + p.Guid); if (p.Guid == "a") p.Stop(PluginState.Failed, "no entry point"); },
            Start = p => { log.Add("S" + p.Guid); p.State = PluginState.Running; },
            Replan = () => Planner.Replan(all, new HashSet<string>()),
        };
        seq.Run(Plan(all)).Count();
        string started = string.Join("", log.Where(x => x[0] == 'S').Select(x => x.Substring(1)).OrderBy(x => x));
        PluginEntry b = all.First(p => p.Guid == "b");
        Check(started == "de", "a plugin that fails to load keeps its dependants out, the others start: started " + started);
        Check(b.State == PluginState.NotStarted && b.Reason.Contains("does not start") && all.First(p => p.Guid == "c").State == PluginState.NotStarted, "  dependants say why: " + b.Reason);
        Check(all.First(p => p.Guid == "a").State == PluginState.Failed, "  the failed one is FAILED, not RUNNING");
    }

    // Anchor Chain is running from the moment acRunning() says so: its pending mods are its own.
    private static Func<string, bool> HandOverWhen(List<PluginEntry> all, Func<bool> acRunning) => _ =>
    {
        if (!acRunning() || !all.Any(x => x.Kind == PluginKind.AnchorChain && x.State == PluginState.Pending))
            return false;
        foreach (PluginEntry p in all.Where(x => x.Kind == PluginKind.AnchorChain && x.State == PluginState.Pending))
            p.Stop(PluginState.LeftToAnchorChain, "Anchor Chain is running and starts this mod.");
        return true;
    };

    private static void SequenceHandOver()
    {
        // Anchor Chain appears after the loading phase: no Anchor Chain mod is started by Keel, a BepInEx plugin still is.
        List<PluginEntry> all = ChainOfMods();
        all.Add(new PluginEntry { Guid = "bep", Name = "bep", Kind = PluginKind.BepInEx, Folder = Folder("x"), TypeName = "T.bep", Dll = "x.dll", Order = 9 });
        var log = new List<string>();
        bool acRunning = false;
        var seq = new Sequencer
        {
            Prepare = p => log.Add("P" + p.Guid),
            Start = p => { log.Add("S" + p.Guid); p.State = PluginState.Running; },
            Replan = () => Planner.Replan(all, new HashSet<string>(all.Where(x => x.State == PluginState.LeftToAnchorChain).Select(x => x.Guid))),
            HandOver = HandOverWhen(all, () => acRunning),
            Prepared = p => { if (log.Count == all.Count(x => x.State == PluginState.Pending)) acRunning = true; },
        };
        seq.Run(Plan(all)).Count();
        Check(acRunning && !log.Any(x => x[0] == 'S' && x != "Sbep"), "Anchor Chain showed up before the first start: Keel started no Anchor Chain mod: " + string.Join(" ", log.Where(x => x[0] == 'S')));
        Check(log.Contains("Sbep"), "  but the BepInEx plugin still started");
        Check(all.Where(x => x.Kind == PluginKind.AnchorChain).All(x => x.State == PluginState.LeftToAnchorChain), "  and they are reported as left to Anchor Chain");

        // Anchor Chain appears in the middle of the start phase, after two starts.
        foreach (PluginEntry p in all) { p.State = PluginState.Pending; p.Reason = ""; }
        log.Clear();
        int startsDone = 0;
        var seq2 = new Sequencer
        {
            Prepare = p => { },
            Start = p => { log.Add("S" + p.Guid); p.State = PluginState.Running; startsDone++; },
            Replan = () => Planner.Replan(all, new HashSet<string>()),
            HandOver = HandOverWhen(all, () => startsDone >= 2),
        };
        seq2.Run(Plan(all)).Count();
        Check(log.Count(x => x[0] == 'S' && x != "Sbep") == 2,
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

    // ---- [ACConfig] ----

    /// <summary>The [ACConfig] checks' mod folder, config folder and the messages AcConfig gives.</summary>
    private sealed class ConfigFixture
    {
        public string Mods, CfgDir;
        public readonly List<string> Info = new(), Warn = new(), Err = new();
        public AcConfig Make(Type t, string dir = null) => new AcConfig(t, dir ?? CfgDir, new[] { Mods }, Info.Add, Warn.Add, Err.Add);
        public static PluginEntry Plugin(string guid, bool required = true) =>
            new PluginEntry { Guid = guid, Name = guid, UsesAcConfig = true, AcConfigRequired = required, Kind = PluginKind.AnchorChain };
    }

    private static void Config(string tmp)
    {
        var f = new ConfigFixture { Mods = Path.Combine(tmp, "cfg-mods"), CfgDir = Path.Combine(tmp, "cfg-streaming", AcConfig.ConfigFolder) };
        Directory.CreateDirectory(f.Mods);
        string mods = f.Mods, cfgDir = f.CfgDir;
        AcConfig Make(Type t, string dir = null) => f.Make(t, dir);
        PluginEntry Plugin(string guid, bool required = true) => ConfigFixture.Plugin(guid, required);

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

        ConfigFallbacks(tmp, f);
    }

    // A handler that lacks a member, has none, or throws; a player file that cannot be made.
    private static void ConfigFallbacks(string tmp, ConfigFixture f)
    {
        string mods = f.Mods, cfgDir = f.CfgDir;
        AcConfig Make(Type t, string dir = null) => f.Make(t, dir);
        PluginEntry Plugin(string guid, bool required = true) => ConfigFixture.Plugin(guid, required);
        List<string> warn = f.Warn;
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

    // ---- AnchorChainLoader.FindFile ----

    private static void LoaderShim(string tmp)
    {
        // the stand-in for AnchorChain.Preloader.dll: Anchor Chain's DLL implements its IPluginLoader on AnchorChainLoader
        Assembly stub = AcLoaderShim.BuildPreloaderStub();
        Type loaderInterface = stub.GetType("AnchorChain.Preloader.IPluginLoader");
        Check(stub.GetName().Name == "AnchorChain.Preloader" && stub.GetName().Version == new Version(1, 0, 0, 0), "stand-in assembly: " + stub.GetName().FullName);
        MethodInfo load = loaderInterface?.GetMethod("LoadPlugins");
        Check(loaderInterface != null && loaderInterface.IsInterface && loaderInterface.IsPublic && load != null && load.ReturnType == typeof(void) && load.GetParameters().Length == 0,
              "  with public interface AnchorChain.Preloader.IPluginLoader { void LoadPlugins(); }");

        string a = MakeFolder(tmp, "ff-a"), b = MakeFolder(tmp, "ff-b"), cfg = Path.Combine(tmp, "ff-config");
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(b, "Sonar-X.wav"), "x");
        File.WriteAllText(Path.Combine(cfg, "mod.x_user.ini"), "x");
        ShippedLoaderShim(stub, a, b, cfg);

        // Anchor Chain 1.2.0 (the source under anchor-chain\): FindFile reads the game's mod list itself, then _configPath
        string why = AcLoaderShim.Prime(typeof(AnchorChainLoader).Assembly, new[] { a, b }, cfg);
        Check(why == null && AnchorChainLoader.FindFile("mod.x_user.ini") == Path.Join(cfg, "mod.x_user.ini"),
              "1.2.0 shape: only _configPath is set, and FindFile finds a player config there (" + why + ")");
        string missing = AcLoaderShim.Prime(stub, new[] { a }, cfg);
        Check(missing != null && missing.Contains("is not in"), "an Anchor Chain DLL without AnchorChainLoader: reported, not thrown (" + missing + ")");
    }

    // Anchor Chain 1.1.0, the very library Keel ships (lib\AnchorChain.keel.bin, copied next to this tool): its FindFile searches a
    // folder list only its own loader fills. Run against the real file, so a library of another shape fails here.
    private static void ShippedLoaderShim(Assembly stub, string a, string b, string cfg)
    {
        string bin = Path.Combine(AppContext.BaseDirectory, "AnchorChain.keel.bin");
        ResolveEventHandler preloader = (_, e) => new AssemblyName(e.Name).Name == AcLoaderShim.PreloaderAssembly ? stub : null;
        AppDomain.CurrentDomain.AssemblyResolve += preloader;
        try
        {
            Assembly shipped = Assembly.LoadFile(bin);
            Type loader = shipped.GetType(AcLoaderShim.LoaderType, throwOnError: true);
            string FindFile(string name) => (string)loader.GetMethod("FindFile").Invoke(null, new object[] { name });
            Check(shipped.GetName().Version == new Version(1, 1, 0, 0), "the shipped library is Anchor Chain 1.1.0: " + shipped.GetName().FullName);
            Check(FindFile("Sonar-X.wav") == null, "before: FindFile finds nothing (Anchor Chain's loader never ran)");
            string why = AcLoaderShim.Prime(shipped, new[] { a, b }, cfg);
            Check(why == null && FindFile("Sonar-X.wav") == Path.Join(b, "Sonar-X.wav"), "after: FindFile finds a file in another mod's folder (" + why + ")");
            Check(FindFile("mod.x_user.ini") == Path.Join(cfg, "mod.x_user.ini"), "  and a player config in _configPath's folder, like Anchor Chain's Setup");
            AcLoaderShim.Prime(shipped, new[] { b }, cfg);
            int count = ((HashSet<DirectoryInfo>)loader.GetField("_allDirectories", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null)).Count;
            Check(count == 3, "  priming twice does not list a folder twice (" + count + " folders)");
        }
        catch (Exception ex)
        {
            Check(false, "the shipped Anchor Chain library could not be primed: " + (ex.InnerException ?? ex).Message);
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= preloader;
        }
    }

    // ---- incompatibility ----

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

    // The part of Anchor Chain 1.2.0's AnchorChainLoader that mods call (FindFile), as in anchor-chain\AnchorChain\Main.cs; the
    // game's checked mod folders it reads first are not there outside the game.
    public class AnchorChainLoader
    {
        private static string _configPath = string.Empty;

        public static string FindFile(string fileName) =>
            new[] { System.IO.Path.Join(_configPath, fileName) }.FirstOrDefault(System.IO.File.Exists);
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
