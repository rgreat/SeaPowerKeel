using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    private static ManualLogSource log;
    /// <summary>Keel's one log source; every part of Keel writes through it.</summary>
    internal static ManualLogSource Log => log ??= BepInEx.Logging.Logger.CreateLogSource(Name);

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

internal sealed partial class KeelHost : MonoBehaviour
{
    internal static string PendingStubVersion, PendingPath;
    public string StubVersion = PendingStubVersion;
    public string OwnPath = PendingPath;
    private bool noticeArmed;
    private bool wantNotice;

    private ManualLogSource log;
    private ConfigEntry<bool> loadAcMods;
    private ConfigEntry<bool> takeOver;
    private Takeover.Record takeoverRecord;     // what the patcher did this start (null: it did not run)
    private string takeoverInstall;            // "installed", "updated", "current" or why not
    private bool restartForTakeover;            // the patcher arrived this start while another loader runs: one restart finishes it
    private ConfigEntry<string> versionCheck;
    private ConfigEntry<string> disabled;
    private ConfigEntry<string> hotkey;
    private ConfigEntry<bool> notice;
    private ConfigEntry<bool> whatsNewOn, workshopNotes;
    private ConfigEntry<float> whatsNewSeconds;
    private ConfigEntry<float> uiScale;
    private ConfigEntry<bool> timedLog;
    private ConfigEntry<int> keepSessions;
    private KeelLog keelLog;
    private CrashGuard crash;
    private ConfigEntry<bool> holdCrashers;
    private string crashNotice;
    private bool heldClicked;
    private float nextFlush;
    private readonly List<string> notes = new();
    private bool done;
    private readonly List<SkippedDll> skippedDlls = new();
    private float noticeUntil;
    private bool showWindow;
    private Rect window;                         // sized and centred by OnGUI when it opens
    private Vector2 scroll;
    private string reportPath;
    private KeyCode key = KeyCode.K;
    private bool keyCtrl = true, keyShift = true, keyAlt;

    private void Awake()
    {
        log = Keel.Log;
        var config = new ConfigFile(Path.Combine(Paths.ConfigPath, Takeover.ConfigFile), true);
        BindSettings(config);
        InstallTakeover();
        // As early as possible: a crash in the game's own start-up load is caught too, and the files it opens are on the list.
        InstallCrashCatchers();
        string compat = CompatDialog.Install();
        if (compat != null)
            log.LogInfo($"Game version dialog stays the game's own: {compat}");
        string loadEnd = LoadScreenEnd.Install();
        if (loadEnd != null)
            log.LogInfo($"Keel cannot tell mods that wait for the load screen's end: {loadEnd}");
        // Here, in Awake: the game has not read its mod list yet.
        SwitchPackOff();
        StartTimedLog();
        ParseHotkey(hotkey.Value);
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        reportPath = Path.Combine(Paths.BepInExRootPath, KeelNames.ReportFile);
        Loading.LoadingWindow.Start(config, log);      // first, so it opens before the slow steps that follow (see docs/loading-window.md)
        string hello = $"{Keel.Name} {Keel.Version} (stub {StubVersion}) on the {GameState.BuildName}, from {OwnPath}";
        log.LogInfo(hello);
        Window?.KeelStarted(Keel.Version, hello);
        StartCoroutine(Pump());
    }

    private void BindSettings(ConfigFile config)
    {
        loadAcMods = config.Bind(Takeover.ConfigSection, "AnchorChainMods", true,
            "Start Anchor Chain mods. Keel runs them itself; Anchor Chain is not needed.");
        takeOver = config.Bind(Takeover.ConfigSection, Takeover.ConfigKey, true,
            @"While Keel is switched on, other mod loaders (Anchor Chain's preloader, Alpine's Mod Loader) do not start, so Keel runs every mod and " +
            @"nothing starts twice. Keel keeps its patcher in BepInEx\patchers for this; the other loaders' files are not changed. Off: another " +
            "loader that is installed starts its mods, and Keel leaves those mods to it.");
        versionCheck = config.Bind(Takeover.ConfigSection, "GameVersionCheck", "Standard", new ConfigDescription(
            "Standard: a DLL that would stop missions from loading on this game build (a type of it is built on something this build " +
            "lacks) is not loaded; other missing parts are a warning. Strict: any missing part keeps the DLL from loading. Off: no check.",
            new AcceptableValueList<string>("Standard", "Strict", "Off")));
        Runner.GuardPatches = config.Bind(Takeover.ConfigSection, "RemoveBrokenPatches", true,
            "When the game throws 'Method not found' / 'Field not found' because a mod's Harmony patch uses something this game build lacks, Keel takes that mod's patch off the failing " +
            "game method again, names the mod in the report and keeps the game going (instead of the same error popup on every frame). Off: Keel only names the mod.").Value;
        holdCrashers = config.Bind(Takeover.ConfigSection, "HoldOutCrashers", true,
            "When the game dies while Keel is loading or starting a mod, or a crash report shows a mod's code on its stack, Keel names that mod at the next start and keeps it out until the mod " +
            @"is updated (the list is BepInEx\Keel-held.txt). Off: Keel only names the mod.");
        disabled = config.Bind(Takeover.ConfigSection, "DisabledPlugins", "",
            "Plugin ids (comma separated) Keel does not start, even though their mod folder is on.");
        hotkey = config.Bind("Report", "Hotkey", "Ctrl+Shift+K", "Shows and hides the report window.");
        notice = config.Bind("Report", "NoticeOnProblems", true,
            "After start-up, show a short notice in the corner when a mod did not start or threw errors.");
        uiScale = config.Bind("Report", "Scale", 0f, "Size of the report window and notice; 0 = by screen height (2 on a 4K screen).");
        whatsNewOn = config.Bind("WhatsNew", "Show", true,
            "After a start where mods changed since the last one (new, updated, gone, or newly in trouble), show a What's new card at the " +
            @"top right of the main menu with what each author wrote about the update. Keel remembers the mods in BepInEx\Keel-seen.txt.");
        workshopNotes = config.Bind("WhatsNew", "WorkshopNotes", true,
            "For updated Workshop mods, read the author's change notes from the item's Steam Workshop changelog page " +
            "(steamcommunity.com, only for mods that changed; Keel sends nothing else). Off: the mod's CHANGELOG.md or just the version.");
        whatsNewSeconds = config.Bind("WhatsNew", "Seconds", 0f,
            "How long the card stays; 0 = until clicked or a mission starts.");
        timedLog = config.Bind("Logging", "TimestampedLog", true,
            @"Write BepInEx\Keel-log.txt: every log line of the game, BepInEx and all mods with time of day and seconds since the game " +
            "started, and the mod an exception came from (with its stack trace).");
        keepSessions = config.Bind("Logging", "KeepSessions", 3,
            "How many earlier sessions' logs to keep (Keel-log.1.txt is the last one).");
    }

    private void InstallCrashCatchers()
    {
        try
        {
            crash = new CrashGuard(Paths.BepInExRootPath, Path.Combine(Path.GetDirectoryName(Application.consoleLogPath) ?? "", "Player-prev.log"));
        }
        catch (Exception ex)
        {
            log.LogWarning($"No crash journal this session: {ex.Message}");
            return;
        }
        string catcher = CrashCatcher.Install(Path.Combine(Paths.BepInExRootPath, KeelNames.StackFile));
        if (catcher != null)
            log.LogWarning($"Crashes will not be caught this session: {catcher}");
        string trail = FileTrail.Install();
        if (trail != null)
            log.LogWarning($"Keel will not see which file the game was loading in a crash: {trail}");
        StartWatcher();
    }

    // A pack that took the game down last session is switched off in the game's mod list before the game reads it.
    private void SwitchPackOff()
    {
        try
        {
            string packOff = ModList.ApplyPackOff(Paths.BepInExRootPath, ModFolders.UserSettings);
            if (packOff == null)
                return;
            string off = $"{FolderLabel(packOff)} was switched off after the last crash. Switch it on again in the Mod Manager to try it after an update.";
            crashNotice = crashNotice == null ? off : crashNotice + "\n" + off;
            notes.Add(off);
            log.LogWarning(off);
        }
        catch (Exception ex)
        {
            log.LogWarning($"Could not switch the pack off: {ex.Message}");
        }
    }

    private void StartTimedLog()
    {
        if (!timedLog.Value)
            return;
        try
        {
            keelLog = new KeelLog(Math.Max(0, Math.Min(20, keepSessions.Value)));
            BepInEx.Logging.Logger.Listeners.Add(keelLog);
        }
        catch (Exception ex)
        {
            log.LogWarning($"Cannot write the timestamped log: {ex.Message}");
        }
    }

    private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        bool single = mode == UnityEngine.SceneManagement.LoadSceneMode.Single;
        if (single)
            Loading.LoadingWindow.SceneLoadedTicks = DateTime.UtcNow.Ticks;
        crash?.Mark("scene", scene.name);
        // The game's Reload in the Mods menu loads the first scene again: then Keel follows the new mod list.
        if (single && ModMenuRestart.ReloadRequested && done && !hotLoading)
        {
            ModMenuRestart.ReloadRequested = false;
            StartCoroutine(HotLoad());
        }
    }

    // How long Keel waits for the game, and how often it looks.
    private const float ModListSeconds = 60, LoadScreenSeconds = 180, PollSeconds = 0.25f;

    // While an early start is under way the coroutine does not give frames away: Unity would not run its next step until the
    // game's first frame, and by then the load screen is showing. Waits (WaitForSecondsRealtime) still wait.
    // Keel's start-up is one coroutine, stepped from two places: by Unity, a step per frame, and by LoadScreenGate, which runs it on
    // right before the game shows its first load screen until the blocking mods have started. Nested coroutines are run here, not by
    // Unity, so both places move the same state.
    private readonly Stack<IEnumerator> runStack = new();
    private bool advancing, blockingDone = true;

    private bool Advance(out object current)
    {
        current = null;
        advancing = true;
        try
        {
            while (runStack.Count > 0)
            {
                IEnumerator it = runStack.Peek();
                if (!it.MoveNext())
                {
                    runStack.Pop();
                    continue;
                }
                if (it.Current is IEnumerator nested)
                {
                    runStack.Push(nested);
                    continue;
                }
                current = it.Current;
                return true;
            }
            return false;
        }
        finally
        {
            advancing = false;
        }
    }

    private IEnumerator Pump()
    {
        runStack.Push(Run());
        while (Advance(out object current))
            yield return current;
    }

    // The game is about to show its first load screen (LoadScreenGate): what is left before the blocking mods have started is done
    // now, without waiting for the game's frames, and the game waits for it.
    private void RunBlockingNow()
    {
        if (blockingDone || advancing)
            return;
        log.LogInfo("The game starts its load screen: Keel starts the mods that asked for a blocking start now.");
        var watch = Stopwatch.StartNew();
        try
        {
            while (!blockingDone && Advance(out object _))
            {
            }
        }
        catch (Exception ex)
        {
            blockingDone = true;
            log.LogError($"The blocking start failed: {ex}");
        }
        log.LogInfo($"The game waited {watch.ElapsedMilliseconds} ms for the blocking start.");
    }

    private IEnumerator Run()
    {
        listed = EarlyModList(out bool blockingAsked);
        if (blockingAsked)
        {
            // A mod asked for a blocking start (BlockingStart): it starts right before the game's first load screen (see
            // LoadScreenGate), or earlier when Keel's own steps get there first.
            blockingDone = false;
            string why = LoadScreenGate.Install(RunBlockingNow, Warn);
            if (why != null)
            {
                blockingDone = true;
                Warn($"A mod asked for a blocking start, but Keel cannot hook the load screen ({why}): it starts early, without holding the game.");
            }
        }
        // One frame first: every plugin in BepInEx\plugins (Anchor Chain's preloader too) has had its Awake by then.
        yield return null;

        // 1. The game's mod list, which the game has only after a while; failing that, the same list from disk.
        if (listed == null)
            yield return WaitForModList(startUp: true);
        List<ModFolder> folders = listed;
        // A required item that is installed but off is switched on before the plugins are looked for (Steam gives a new required
        // item to new subscribers only); one that is not installed is offered on the card.
        RequiredItems.SwitchOn(folders, log.LogInfo, log.LogWarning);
        List<ModFolder> enabled = folders.Where(f => f.Enabled).ToList();

        // 2. Find the plugins (metadata only, no mod code runs): before the wait, so mods that ask to start early are known.
        var watch = Stopwatch.StartNew();
        Keel.Plugins.AddRange(Scanner.Scan(enabled, Warn, skippedDlls, Paths.ManagedPath, Paths.BepInExAssemblyDirectory));
        log.LogInfo($"Found {Keel.Plugins.Count} plugin(s) in {Keel.Plugins.Select(p => p.Dll).Distinct().Count()} DLL(s) ({watch.ElapsedMilliseconds} ms).");
        Window?.Found(Keel.Plugins.Count);
        foreach (SkippedDll d in skippedDlls)
            log.LogInfo($"DLL left out: {d.Path} - {d.Reason}");

        // 3. After the start-up load screen: mods started during it can keep the unit data from loading.
        // A mod that asked to start early (EarlyStart) changes that: Keel then prepares everything now, starts the early mods,
        // and waits for the load screen right before the others.
        bool early = Keel.Plugins.Any(p => p.StartEarly && p.State == PluginState.Pending);
        if (!early)
        {
            blockingDone = true;
            yield return WaitForLoadScreen(say: true);
        }

        Say($"{enabled.Count} of {folders.Count} mod folders are on.", (w, l) => w.FoldersOn(l));
        LookBeforeStart(enabled, early);
        yield return null;

        liveRunner = NewRunner(enabled);
        liveFolders = enabled;
        RuleAnchorChain(enabled, liveRunner);

        // 4. Check, plan and start (see StartPlugins).
        var pass = new Pass { Enabled = enabled, All = folders, Running = new HashSet<string>(), Early = early };
        yield return StartPlugins(pass);
        foreach (string line in LoadScreenEnd.Replay(pass.LoadScreenHooks))
            log.LogInfo(line);
        foreach (PluginEntry p in Keel.Plugins.Where(p => p.State is PluginState.NotStarted))
            LogResult(p);
        FinishStartUp(pass);
    }

    // A mod that asked to start early (rgreat 2026-11-12: "could be a bit earlier", he replaces the load-screen pictures) does not
    // wait for the game's own list, which comes about 8 s after Keel: Keel reads the same list from usersettings.ini at once, so
    // early mods start before the first load-screen picture. Everything else still waits for the load screen. Null: no mod asked.
    private List<ModFolder> EarlyModList(out bool blocking)
    {
        blocking = false;
        List<ModFolder> disk;
        try
        {
            disk = ModFolders.FromDisk();
        }
        catch (Exception ex)
        {
            log.LogWarning($"Could not read the mod list from usersettings.ini early: {ex.Message}");
            return null;
        }
        var timing = EarlyStart.Discover(disk);
        blocking = timing == StartupTiming.Blocking;
        if (timing == StartupTiming.Normal)
            return null;
        log.LogInfo("A mod asked to start early: Keel takes the mod list from usersettings.ini now instead of waiting for the game's.");
        return disk;
    }

    private List<ModFolder> listed;      // what WaitForModList found

    /// <summary>The game's mod list into <see cref="listed"/>; after <see cref="ModListSeconds"/> the same list from disk.</summary>
    private IEnumerator WaitForModList(bool startUp)
    {
        var watch = Stopwatch.StartNew();
        while ((listed = GameState.GameModList()) == null && watch.Elapsed.TotalSeconds < ModListSeconds)
            yield return new WaitForSecondsRealtime(PollSeconds);
        if (listed != null)
        {
            if (startUp)
                Say($"The game's mod list is there (waited {watch.Elapsed.TotalSeconds:0.0} s).", (w, l) => w.ModListThere(l));
            yield break;
        }
        listed = ModFolders.FromDisk();
        if (startUp)
            notes.Add($"The game did not list its mod folders within {ModListSeconds:0} s; Keel used usersettings.ini instead.");
    }

    /// <summary>Waits (up to <see cref="LoadScreenSeconds"/>) until the game's start-up load screen is done.</summary>
    private IEnumerator WaitForLoadScreen(bool say)
    {
        var wait = Stopwatch.StartNew();
        while (!GameState.StartupDone() && wait.Elapsed.TotalSeconds < LoadScreenSeconds)
            yield return new WaitForSecondsRealtime(PollSeconds);
        if (say)
            Say($"Start-up load screen done (waited {wait.Elapsed.TotalSeconds:0.0} s).", (w, l) => w.LoadScreenDone(l));
    }

    // Before any mod is touched: the mod setups, What's new, what the last session left (so a mod that took the game down cannot
    // do it again), and the pack check on its own thread (on a first start it reads the head of every picture and sound).
    private void LookBeforeStart(List<ModFolder> enabled, bool early)
    {
        if (!early)
            LookBeforeStartLate(enabled);
        crash?.Apply(Keel.Plugins, holdCrashers.Value, n =>
        {
            crashNotice = crashNotice == null ? n : crashNotice + "\n" + n;
            notes.Add(n);
            log.LogError(n);
        });
        if (crash?.QuietNote != null)
            log.LogInfo(crash.QuietNote);
        if (crash != null)
        {
            if (crash.Short != null)
                Loading.LoadingWindow.Alert = crash.Short;
            crash.WriteModsFile(Keel.Plugins);
        }
    }

    // Nothing here changes which mods start or in which order, so with early mods it waits until they run (their start is the one
    // thing that must not wait: they replace the load screen).
    private void LookBeforeStartLate(List<ModFolder> enabled)
    {
        DetectSetups(enabled);
        if (whatsNewOn.Value)
            StartWhatsNew(enabled);
        string packCache = Path.Combine(Paths.BepInExRootPath, "Keel-packcheck.txt");
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                packProblems = PackCheck.Scan(enabled, packCache);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Pack check failed, broken files are not listed this start: {ex.Message}");
                packProblems = new List<string>();
            }
        });
    }

    private Runner NewRunner(List<ModFolder> enabled)
    {
        var runner = new Runner(log, gameObject, enabled);
        if (keelLog != null)
            keelLog.OwnerOf = runner.OwnerOf;
        // Unity hands exceptions to BepInEx without saying which mod threw; Keel adds a line that does, with the stack trace,
        // to LogOutput.log and Keel-log.txt alike.
        runner.ModError = (p, message, stack) =>
            log.LogError($"The exception above came from {p.Display} {p.Version} ({p.Guid}, {p.Folder.Label}): {message}{Environment.NewLine}{stack}");
        return runner;
    }

    /// <summary>One run of <see cref="StartPlugins"/>: what it works on, and what it found.</summary>
    private sealed class Pass
    {
        public List<ModFolder> Enabled, All;
        public HashSet<string> Running;         // ids already running in this game (a reload): never started twice
        public bool Early;                      // start-up with mods that asked to start early
        public bool Reload;
        public int Started;
        public long Ms;
        public HashSet<string> LoadScreenHooks = new();
    }

    /// <summary>
    /// The steps every new set of plugins goes through, at start-up and after a Mods menu reload alike: check each DLL against this
    /// game build, plan, set up what Anchor Chain's own loader would have, then load and start in order (see Sequencer).
    /// </summary>
    private IEnumerator StartPlugins(Pass pass)
    {
        var watch = Stopwatch.StartNew();
        // Early mods: only the blocking ones are checked now (the others are checked in the first frames, the rest during the load
        // screen).
        void note(PluginEntry p, string why)
        {
            p.Warnings.Add(why);
            log.LogInfo($"{p.Display}: {why}");
        }
        HashSet<PluginEntry> blockSet = pass.Early ? EarlyStart.Set(Keel.Plugins, note, blocking: true) : null;
        HashSet<PluginEntry> earlySet = pass.Early ? EarlyStart.Set(Keel.Plugins, note) : null;
        if (pass.Early && blockSet.Count == 0)
            blockingDone = true;
        if (!pass.Early || blockSet.Count > 0)
            yield return Check(blockSet == null ? null : p => blockSet.Contains(p));
        if (!pass.Reload)
            log.LogInfo($"Game-version check done ({watch.ElapsedMilliseconds} ms, frames included).");
        watch.Restart();

        var elsewhere = new HashSet<string>(Chainloader.PluginInfos.Keys);
        elsewhere.UnionWith(pass.Running);
        elsewhere.UnionWith(Keel.Plugins.Where(p => p.State == PluginState.LeftToAnchorChain).Select(p => p.Guid));
        List<PluginEntry> order = Planner.Plan(Keel.Plugins, elsewhere, DisabledIds(), Process.GetCurrentProcess().ProcessName);

        // Anchor Chain may have started in the meantime (the version check took frames): then it starts its mods, not Keel.
        if (LeaveToAnchorChain(liveRunner, elsewhere, "before Keel loaded the mods"))
            order = Planner.Replan(Keel.Plugins, elsewhere);

        // What Anchor Chain's own loader would have set up for its mods: the folders behind AnchorChainLoader.FindFile, then the
        // [ACConfig] mods' config files (Anchor Chain's first pass).
        PrimeAnchorChainLoader(liveRunner, pass.Enabled, pass.All, order);
        PrepareAcConfigs(pass.Enabled, pass.All, order);
        if (order.Any(p => p.State != PluginState.Pending))
            order = Planner.Replan(Keel.Plugins, elsewhere);

        Sequencer sequence = NewSequence(pass, elsewhere);
        if (pass.Early)
        {
            yield return StartEarlyMods(sequence, order, blockSet, earlySet, pass);
            order = sequence.Replan();
        }
        // Taken after the early mods: their load-screen hooks see the real end and are not called again.
        pass.LoadScreenHooks = LoadScreenEnd.Owners();
        foreach (PluginEntry _ in sequence.Run(order))
            yield return null;
        pass.Started += sequence.StartedCount;
        pass.Ms = watch.ElapsedMilliseconds;
    }

    // Load every DLL and create every Anchor Chain mod before any of them starts (as Anchor Chain does: a mod that looks for another
    // mod's assembly when it starts finds it); then start in dependency order. One step per frame, which changes nothing about the
    // order (see Sequencer). Anchor Chain is looked for again right before the first start.
    private Sequencer NewSequence(Pass pass, HashSet<string> elsewhere) => new()
    {
        Prepare = p =>
        {
            crash?.Mark("load", p.Guid);
            liveRunner.Prepare(p);
        },
        Start = p =>
        {
            crash?.Mark("start", p.Guid);
            liveRunner.Start(p);
        },
        // Logged before the start: a mod that holds the game up for seconds is named while it does (the loading window shows it).
        Starting = (p, index, total) => AnnounceStart(p, index, total, pass.Reload),
        Replan = () => Planner.Replan(Keel.Plugins, elsewhere),
        HandOver = when => LeaveToAnchorChain(liveRunner, elsewhere, when),
        Prepared = p =>
        {
            if (p.State == PluginState.Failed)
                LogResult(p);
        },
        Started = p =>
        {
            LogResult(p);
            // A reload writes the report after every mod: a report sent (or a crash) in the middle of it shows how far it got
            // (a player's report 2026-11-12 showed 3 mods while the log had started 27 of 30).
            if (pass.Reload)
                WriteReport();
        },
    };

    private void AnnounceStart(PluginEntry p, int index, int total, bool reload)
    {
        if (reload)
        {
            log.LogInfo($"STARTING (after reload) {index}/{total}: {p.Display} {p.Version} ({p.Folder.Label})");
            reloadProgress = $"Mods menu reload in progress: starting {index} of {total} ({p.Display}).";
            return;
        }
        log.LogInfo($"STARTING {index}/{total}: {p.Display} {p.Version} ({p.Folder.Label})");
        Window?.Starting(index, total, $"{p.Display} {p.Version}");
    }

    // The mods that asked to start early, now: the blocking ones first (see RunBlockingNow); the others in the first
    // frames, one per frame next to the game; then the rest of the look-around; then the load screen's end; then the sequence goes
    // on with the other mods.
    private IEnumerator StartEarlyMods(Sequencer sequence, List<PluginEntry> order, HashSet<PluginEntry> blockSet, HashSet<PluginEntry> earlySet, Pass pass)
    {
        string names = string.Join(", ", earlySet.Select(p => p.Display).Distinct());
        log.LogInfo($"Starting {earlySet.Count} plugin(s) that asked to start before the load screen ends: {names}.");
        if (earlySet.Count > 0)
            notes.Add($"Started before the load screen ended (the mod asked for it): {names}.");
        Func<List<PluginEntry>> replanAll = sequence.Replan;
        if (blockSet.Count > 0)
        {
            log.LogInfo($"Blocking start (before the game's first load screen, the game waits): {string.Join(", ", blockSet.Select(p => p.Display).Distinct())}.");
            sequence.Replan = () => replanAll().Where(blockSet.Contains).ToList();
            foreach (PluginEntry _ in sequence.Run(order.Where(blockSet.Contains).ToList()))
                yield return null;
        }
        blockingDone = true;
        yield return null;
        var parallel = new HashSet<PluginEntry>(earlySet.Except(blockSet));
        if (parallel.Count > 0)
        {
            yield return Check(p => parallel.Contains(p));
            sequence.Replan = () => replanAll().Where(parallel.Contains).ToList();
            foreach (PluginEntry _ in sequence.Run(replanAll().Where(parallel.Contains).ToList()))
                yield return null;
        }
        // Now what the early start left for later: the setup look, What's new, the pack check, the version check of the others.
        LookBeforeStartLate(pass.Enabled);
        yield return Check(p => !earlySet.Contains(p));
        yield return WaitForLoadScreen(say: true);
        sequence.Replan = () => replanAll().Where(p => !earlySet.Contains(p)).ToList();
    }

    private void FinishStartUp(Pass pass)
    {
        crash?.Mark("done", "");
        done = true;
        UpdateLoadOrderWarnings();
        log.LogInfo($"Started {pass.Started} plugin(s) in {pass.Ms} ms, one per frame.");
        Window?.StartedIn(pass.Ms);
        string summary = $"Done: {Report.Summary(Keel.Plugins)}. Report: {reportPath} or {hotkey.Value} in the game.";
        log.LogInfo(summary);
        Window?.Done(summary);
        WriteReport();
        // Shown once the main menu is up (the start-up load screen can last minutes), see Update.
        // The summary card says it, and a click on it opens the report; the notice is for players who switched that window off.
        if (restartForTakeover)
            Loading.LoadingWindow.Alert ??= "Keel set up its takeover: restart once, then Keel runs every mod";
        wantNotice = notice.Value && !Loading.LoadingWindow.Shown && (crashNotice != null || restartForTakeover || Keel.Plugins.Any(Report.IsProblem));
        // Errors that come later show up in the window; the file is rewritten when the window opens and at quit.
    }

    private HashSet<string> DisabledIds() => new(disabled.Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));

    private Runner liveRunner;
    private List<ModFolder> liveFolders;
    private bool hotLoading;
    private readonly HashSet<string> turnedOffDlls = new(StringComparer.OrdinalIgnoreCase);

    private static bool SameFolder(ModFolder a, ModFolder b) => a != null && b != null && ModFolders.SamePath(a.Path, b.Path);

    /// <summary>
    /// After the game's Reload (Mods menu): code mods in folders switched on since start-up go through the same steps as at
    /// start-up (<see cref="StartPlugins"/>), mods in folders switched off are taken out as far as the running game allows
    /// (<see cref="Runner.TurnOff"/>). A mod that only works when started with the game is better served by Restart; the dialog says so.
    /// </summary>
    private IEnumerator HotLoad()
    {
        hotLoading = true;
        yield return null;
        yield return null;
        yield return WaitForModList(startUp: false);
        List<ModFolder> folders = listed;
        yield return WaitForLoadScreen(say: false);
        List<ModFolder> now = folders.Where(f => f.Enabled).ToList();
        List<ModFolder> added = now.Where(f => !liveFolders.Any(o => SameFolder(o, f))).ToList();
        List<ModFolder> removed = liveFolders.Where(o => !now.Any(f => SameFolder(o, f))).ToList();
        log.LogInfo($"Reload from the Mods menu: {added.Count} mod folder(s) switched on, {removed.Count} switched off.");

        int turnedOff = TurnOff(removed);
        liveFolders = now;
        UpdateLoadOrderWarnings();

        var pass = new Pass { Enabled = now, All = folders, Reload = true };
        var fresh = new List<PluginEntry>();
        if (added.Count > 0)
        {
            liveRunner.AddFolders(added);
            pass.Running = new HashSet<string>(Keel.Plugins.Where(p => ReportFormat.IsRunning(p.State)).Select(p => p.Guid));
            fresh = ScanAdded(added, pass.Running);
            RuleAnchorChain(now, liveRunner);
            yield return StartPlugins(pass);
            reloadProgress = null;
            crash?.Mark("done", "");
            crash?.WriteModsFile(Keel.Plugins);
        }

        UpdateLoadOrderWarnings();
        string missed = string.Join(", ", fresh.Where(Report.IsProblem).Select(p => p.Display).Distinct());
        string note = $"Mods menu reload: started {pass.Started} code mod(s), switched {turnedOff} off." +
                      (missed.Length > 0 ? $" Did not start: {missed} (see below)." : "") +
                      (turnedOff > 0 ? " A switched-off mod is fully gone only after a restart." : "");
        notes.Add(note);
        log.LogInfo(note);
        WriteReport();
        hotLoading = false;
    }

    // Folders switched off in the Mods menu: their mods out as far as possible. Returns how many running mods were turned off.
    private int TurnOff(List<ModFolder> removed)
    {
        int turnedOff = 0;
        foreach (PluginEntry p in Keel.Plugins.Where(p => removed.Any(f => SameFolder(f, p.Folder))).ToList())
        {
            if (ReportFormat.IsRunning(p.State))
            {
                int patches = liveRunner.TurnOff(p);
                turnedOff++;
                turnedOffDlls.Add(Path.GetFullPath(p.Dll));
                p.Stop(PluginState.Skipped, $"Switched off in the running game: Keel took {patches} patch(es) off{(p.Kind == PluginKind.BepInEx ? " and stopped the plugin" : "")}. What the mod set up itself stays until the game restarts.");
                log.LogInfo($"TURNED OFF: {p.Display} {p.Version} ({p.Folder.Label}) - {patches} patch(es) removed");
            }
            else if (p.State == PluginState.Pending)
            {
                p.Stop(PluginState.Skipped, "Its folder was switched off in the Mods menu.");
            }
        }
        return turnedOff;
    }

    // Folders switched on in the Mods menu: their plugins, minus those already running and those switched off earlier in this game.
    private List<PluginEntry> ScanAdded(List<ModFolder> added, HashSet<string> running)
    {
        List<PluginEntry> fresh = Scanner.Scan(added, Warn, skippedDlls, Paths.ManagedPath, Paths.BepInExAssemblyDirectory);
        foreach (PluginEntry p in fresh)
        {
            if (running.Contains(p.Guid))
                p.Stop(PluginState.Skipped, "Already running from another folder.");
            // Its code from the first start is still loaded, with whatever it remembers: a second start in the same game
            // is not a clean one (seen with a mod: "already loaded, skipping"). A restart is.
            else if (turnedOffDlls.Contains(Path.GetFullPath(p.Dll)))
                p.Stop(PluginState.NotStarted, "Switched off and on again in this game: its code from the first start is still loaded, so it starts again only after a restart.");
        }
        // An entry of a mod switched off earlier in this game makes room for the new one.
        Keel.Plugins.RemoveAll(old => old.State == PluginState.Skipped && fresh.Any(n => n.Guid == old.Guid));
        Keel.Plugins.AddRange(fresh);
        return fresh;
    }

    private void UpdateLoadOrderWarnings()
    {
        Planner.UpdateLoadOrderWarnings(Keel.Plugins, liveFolders);
        Loading.LoadingWindow.Progress?.SetOrderWarnings(Report.CountOrderWarnings(Keel.Plugins));
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

    // The takeover patcher comes with Keel and is kept current by Keel: players never copy it by hand. What it did this start is
    // in BepInEx\Keel-takeover.txt (only a record written by this game process counts).
    private void InstallTakeover()
    {
        try
        {
            takeoverInstall = Takeover.Install(Path.Combine(Path.GetDirectoryName(OwnPath) ?? "", Takeover.PatcherFile), Paths.PatcherPluginPath);
            if (takeoverInstall is "installed" or "updated")
                log.LogInfo($"Takeover patcher {takeoverInstall} in {Paths.PatcherPluginPath}: it works from the next start.");
            else if (takeoverInstall != "current")
                log.LogWarning($"Takeover patcher not installed: {takeoverInstall}.");
        }
        catch (Exception ex)
        {
            takeoverInstall = ex.Message;
            log.LogWarning($"Takeover patcher not installed: {ex.Message}");
        }
        Takeover.Record r = Takeover.Record.Read(Path.Combine(Paths.BepInExRootPath, Takeover.RecordFile));
        DateTime started = DateTime.UtcNow;
        try
        {
            started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            // keep now: an older record then never counts
        }
        takeoverRecord = r != null && r.WrittenUtc >= started.AddSeconds(-KeelNames.TimeSlackSeconds) ? r : null;
        if (takeoverRecord != null)
        {
            foreach (Takeover.Blocked b in takeoverRecord.Kept)
                log.LogInfo($"Takeover: {b.Name} ({b.File}) was kept from starting; Keel runs its mods.");
            if (takeoverRecord.Error != null)
                log.LogWarning($"Takeover did not work this start: {takeoverRecord.Error}");
        }
    }

    // Another loader is running although Keel is on: say why and what happens next.
    private string OtherLoaderNote(string other)
    {
        if (!takeOver.Value)
            return $"{other} is running: its mods are left to it, because TakeOver is off in Keel's settings.";
        if (takeoverRecord?.Error != null)
            return $"{other} is running: its mods are left to it. Keel's takeover did not work this start ({takeoverRecord.Error}); please send a report.";
        if (takeoverRecord == null && takeoverInstall is "installed" or "updated")
        {
            restartForTakeover = true;
            return $"{other} is running: this time its mods are left to it, so nothing starts twice. Keel has now set up its takeover: " +
                   "restart the game once, and from then on Keel runs every mod.";
        }
        if (takeoverRecord == null)
            return $"{other} is running: its mods are left to it. Keel could not set up its takeover ({takeoverInstall}). " +
                   "Close the game and run KeelSetup.exe from Keel's Workshop folder.";
        return $"{other} is running: its mods are left to it ({takeoverRecord.Off ?? "the takeover did not catch it"}).";
    }

    private void RuleAnchorChain(List<ModFolder> enabled, Runner runner)
    {
        List<PluginEntry> acMods = Keel.Plugins.Where(p => p.Kind == PluginKind.AnchorChain && p.State == PluginState.Pending).ToList();
        if (takeoverRecord?.Kept.Count > 0 && !notes.Any(n => n.StartsWith("Takeover:")))
            notes.Add("Takeover: " + string.Join(", ", takeoverRecord.Kept.Select(b => b.Name).Distinct()) +
                      " is installed but was kept from starting, so Keel runs every mod. Its files are untouched: switch Keel off in the Mods menu to use it again.");
        if (acMods.Count == 0)
            return;
        if (runner.AnchorChainRunning())
        {
            foreach (PluginEntry p in acMods)
                p.Stop(PluginState.LeftToAnchorChain, $"{runner.OtherLoader()} is running and starts this mod.");
            notes.Add(OtherLoaderNote(runner.OtherLoader()));
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
        // A preloader the takeover kept from starting is still a file in BepInEx\plugins, where BepInEx's own resolver would find
        // it for Anchor Chain's library (Alpine's is named the same and is not Anchor Chain's): Keel's stand-in is loaded first.
        if (takeoverRecord?.Kept.Any(b => Path.GetFileName(b.File).Equals(KeelNames.AnchorChainPreloaderFile, StringComparison.OrdinalIgnoreCase)) == true)
            runner.UsePreloaderStandIn();
        string acNote = $"Keel starts the Anchor Chain mods itself (types from {acDll}).";
        if (!notes.Contains(acNote))
            notes.Add(acNote);          // a Mods menu reload runs this again: the note once (test 2026-11-12)
    }

    // Anchor Chain started after Keel's first look: its mods are its own from then on (never started twice).
    private bool LeaveToAnchorChain(Runner runner, HashSet<string> elsewhere, string when)
    {
        List<PluginEntry> mine = Keel.Plugins.Where(p => p.Kind == PluginKind.AnchorChain && p.State == PluginState.Pending).ToList();
        if (mine.Count == 0 || !runner.AnchorChainRunning())
            return false;
        foreach (PluginEntry p in mine)
        {
            p.Stop(PluginState.LeftToAnchorChain, $"{runner.OtherLoader()} is running and starts this mod.");
            elsewhere.Add(p.Guid);
        }
        Warn($"{runner.OtherLoader()} started {when}: its {mine.Count} mod(s) are left to it.");
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

    private IEnumerator Check(Func<PluginEntry, bool> only = null)
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
            List<IGrouping<string, PluginEntry>> dlls = Keel.Plugins.Where(p => p.State == PluginState.Pending && (only == null || only(p))).GroupBy(p => p.Dll).ToList();
            var results = new Dictionary<string, Preflight.Result>(StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, PluginEntry> dll in dlls)
            {
                try
                {
                    results[dll.Key] = preflight.Check(dll.Key);
                }
                catch (Exception ex)
                {
                    Warn($"Could not check {dll.Key}: {ex.Message}");
                }
                yield return null;
            }
            foreach (IGrouping<string, PluginEntry> dll in dlls)
            {
                if (!results.TryGetValue(dll.Key, out Preflight.Result r))
                    continue;
                string matching = Preflight.MatchingMapBuild(dll.Key, r, results);
                foreach (PluginEntry p in dll)
                    Preflight.Judge(p, r, matching, mode == "Strict", InfoVersion.MismatchInFolder(p.Folder?.Path, Application.version));
            }
            if (preflight.IndexMs > 0)
                log.LogInfo($"Index of the game's type names for by-name lookups: {preflight.IndexMs} ms.");
        }
    }

    private static Loading.Progress Window => Loading.LoadingWindow.Progress;

    // A step the loading window shows: logged, and handed to the window as the same line.
    private void Say(string line, Action<Loading.Progress, string> tell)
    {
        log.LogInfo(line);
        if (Window != null)
            tell(Window, line);
    }

    // A mod's result: the log line, and the same result to the loading window as data.
    private void LogResult(PluginEntry p)
    {
        log.Log(p.State == PluginState.Running ? LogLevel.Info : p.State == PluginState.NotStarted ? LogLevel.Warning : LogLevel.Error, Report.ResultLine(p));
        Window?.Result($"{p.Display} {p.Version}", p.State);
    }

    private void Warn(string message)
    {
        notes.Add(message);
        log.LogWarning(message);
    }

    private string setupLine;   // e.g. "Euromod setup: Euromod + AC Pack + Extended Guidance ON (experimental)." or null
    private ModSetups.Euromod euromod;      // what DetectSetups found, for the Extended Guidance button
    private bool? egNext;                   // Extended Guidance as switched in the report window, for the next start; null = not touched

    private volatile string reloadProgress;     // set while a Mods menu reload starts mods

    private string Header() =>
        $"{Keel.Name} {Keel.Version} (stub {StubVersion}) – Sea Power {Application.version}, {GameState.BuildName} – {DateTime.Now:yyyy-MM-dd HH:mm}" +
        (setupLine != null ? Environment.NewLine + setupLine : "") +
        (reloadProgress != null ? Environment.NewLine + reloadProgress : "");

    private void DetectSetups(List<ModFolder> enabled)
    {
        try
        {
            string submods = ModSetups.SubmodsIni(Application.persistentDataPath);
            euromod = ModSetups.Detect(enabled, File.Exists(submods) ? File.ReadAllText(submods) : null,
                WhatsNew.SteamTimes(ModFolders.WorkshopRoot), Application.version);
            setupLine = euromod.Line();
            if (setupLine != null)
                log.LogInfo(setupLine);
            string warning = euromod.Warning();
            if (warning != null)
            {
                log.LogWarning(warning);
                notes.Add(warning);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning($"Could not tell the Euromod setup: {ex.Message}");
        }
    }

    /// <summary>
    /// The report window's Extended Guidance button: writes the AC Pack's own switch (Mods\ACPack\submods.ini, the file its menu
    /// writes) for the next start. The running game keeps what it started with; Restart the game makes it count.
    /// </summary>
    private void EgButton()
    {
        if (!FreshClick() || euromod is not { AcPack: true })
            return;
        bool on = !(egNext ?? euromod.Eg);
        try
        {
            string path = ModSetups.SubmodsIni(Application.persistentDataPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, ModSetups.SetSubmod(File.Exists(path) ? File.ReadAllText(path) : null, ModSetups.EgId, on));
            egNext = on;
            log.LogInfo($"Extended Guidance switched {(on ? "ON" : "off")} for the next start ({path}).");
        }
        catch (Exception ex)
        {
            notes.Add($"Could not switch Extended Guidance: {ex.Message}");
        }
    }

    private string EgLabel() => egNext is bool next && next != euromod.Eg
        ? $"EG {(next ? "on" : "off")} after restart"
        : euromod.Eg ? "Turn EG off" : "Turn EG on";

    private void WriteReport()
    {
        try
        {
            string text = Report.Text(Keel.Plugins, Header(), notes, skippedDlls);
            if (news is { Count: > 0 })
                text += Environment.NewLine + "WHAT'S NEW SINCE THE LAST START" + Environment.NewLine + WhatsNew.Text(news).Replace("\n", Environment.NewLine);
            File.WriteAllText(reportPath, text);
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
    private bool devicesProbed;
    private DeviceInput hotkeyDevice, pointerDevice;     // each dropped on its first error
    private float guiScale = 1f;

    private bool HotkeyHeld() =>
        hotkeyDevice.Held(key)
        && (!keyCtrl || hotkeyDevice.Held(DeviceInput.Modifier.Ctrl))
        && (!keyShift || hotkeyDevice.Held(DeviceInput.Modifier.Shift))
        && (!keyAlt || hotkeyDevice.Held(DeviceInput.Modifier.Alt));

    private void PollHotkey()
    {
        if (hotkeyDevice == null)
            return;
        bool down;
        try
        {
            down = HotkeyHeld();
        }
        catch (Exception)
        {
            down = false;
            hotkeyDevice = null;
        }
        if (down && !keyWasDown)
            ToggleWindow();
        keyWasDown = down;
    }

    // The report's and the cards' clicks and Esc, read from the device and hit-tested here (see DeviceInput).
    private void PollReportInput()
    {
        if (pointerDevice == null || (!showWindow && !NewsVisible && !CardVisible))
            return;
        try
        {
            (bool click, Vector2 pos, bool esc) = pointerDevice.Pointer();
            var gui = new Vector2(pos.x / guiScale, (Screen.height - pos.y) / guiScale);       // the pointer's y runs from the bottom
            if (!showWindow)
            {
                // The cards at the bottom left: What's new (its × closes it, a click elsewhere opens the full list) and the Keel card.
                if (click && !NewsClick(gui))
                    CardClick(gui, 0);
                return;
            }
            if (esc)
                showWindow = false;
            else if (click)
                ReportClick(gui);
        }
        catch (Exception)
        {
            pointerDevice = null;
        }
    }

    private void ReportClick(Vector2 gui)
    {
        foreach ((Rect spot, Action act) in buttonSpots)
        {
            if (spot.Contains(gui))
            {
                act();
                return;
            }
        }
        if (new Rect(window.x + newsScrollRect.x, window.y + newsScrollRect.y, newsScrollRect.width, newsScrollRect.height).Contains(gui))
        {
            // Screen GUI units -> the scroll view's content: minus the window, the scroll view's place in it, plus the scroll.
            var inContent = new Vector2(gui.x - window.x - newsScrollRect.x + scroll.x, gui.y - window.y - newsScrollRect.y + scroll.y);
            foreach (KeyValuePair<NewsItem, Rect> row in newsRowRects)
                if (row.Value.Contains(inContent)) { ToggleNews(row.Key); break; }
        }
    }

    private float nextMissionCheck;

    private void Update()
    {
        if (!devicesProbed)
        {
            devicesProbed = true;
            hotkeyDevice = pointerDevice = DeviceInput.Probe();
        }
        PollReportInput();
        PollHotkey();
        if (Time.unscaledTime > nextMissionCheck)
        {
            nextMissionCheck = Time.unscaledTime + 0.5f;
            Loading.LoadingWindow.MissionRunning = GameState.InMission();
            // the summary belongs to the main menu's top page only; in a mission there is no menu to ask about
            // A campaign screen counts as a page away from the menu's top page too: the cards stay off there.
            Loading.LoadingWindow.OnMenuSubPage = !Loading.LoadingWindow.MissionRunning && (GameState.InCampaignOrMission() || GameState.OnMenuSubPage());
        }
        if (Loading.LoadingWindow.ReportRequested)      // a click on the loading window's summary
        {
            Loading.LoadingWindow.ReportRequested = false;
            ToggleWindow();
        }
        if (Time.unscaledTime > nextFlush)
        {
            nextFlush = Time.unscaledTime + 1f;
            EverySecond();
        }
    }

    private void EverySecond()
    {
        CrashCatcher.KeepInstalled();
        TakePackProblems();
        keelLog?.Flush();
        HookModMenu();
        FinishWhatsNew();
        if (wantNotice && !noticeArmed && GameState.StartupDone())
        {
            noticeArmed = true;
            noticeUntil = Time.realtimeSinceStartup + NoticeSeconds;
        }
    }

    private const float NoticeSeconds = 30;

    private bool modMenuHooked;

    // Not in Awake: patching the Mods menu runs its static constructor, which registers UI properties with Noesis, and before the
    // game's UI is up that kills the game in native code (seen 2026-11-11). Once the main menu exists the UI is ready.
    private void HookModMenu()
    {
        if (modMenuHooked || !done || !GameState.StartupDone() || !GameState.MenuUp())
            return;
        modMenuHooked = true;
        ModMenuRestart.Restart = RestartGame;
        string why = ModMenuRestart.Install();
        if (why != null)
            log.LogWarning($"No Restart button in the Mods menu: {why}");
        else
            log.LogInfo("Mods menu: Restart button and Reload follow-up ready.");
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

    private bool windowWasShown;

    private void OnGUI()
    {
        Event e = Event.current;
        // Keel closes its window on the mouse press (the polled click), so the button under the mouse never gets its release and
        // keeps GUIUtility.hotControl. Alternative Game Interface hands mouse releases to the game's interface only while
        // hotControl is 0: the whole menu stopped taking clicks after the report was closed (board post #3, 2026-11-12).
        // So once the window is gone, Keel lets go of the mouse.
        if (windowWasShown && !showWindow)
        {
            GUIUtility.hotControl = 0;
            GUIUtility.keyboardControl = 0;
        }
        windowWasShown = showWindow;
        if (e.type == EventType.KeyDown && e.keyCode == key && e.control == keyCtrl && e.shift == keyShift && e.alt == keyAlt)
        {
            ToggleWindow();
            e.Use();
        }
        if (showWindow && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            showWindow = false;
            e.Use();
        }
        // IMGUI draws in screen pixels: scale it so the report reads the same on 1080p and 4K (Report.Scale, 0 = by screen height).
        float scale = uiScale.Value > 0 ? uiScale.Value : Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        guiScale = scale;
        float screenW = Screen.width / scale, screenH = Screen.height / scale;

        if (!showWindow)
        {
            if (Time.realtimeSinceStartup < noticeUntil)
                DrawNotice(screenH);
            windowCentered = false;
            DrawMenuCards(screenW, screenH);
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
        window = GUI.Window(0x4B45454C, window, DrawWindow, GUIContent.none, windowStyle);
    }

    // For players who switched the loading window off: a game-style message above the cards at the bottom left of the main menu
    // (the Keel card, What's new), which are there for the same half minute. Taller with a crash note or the takeover note.
    private const float NoticeHeight = 70, NoticeHeightTakeover = 120, NoticeHeightCrash = 170;

    private void DrawNotice(float screenH)
    {
        int bad = Keel.Plugins.Count(Report.IsProblem);
        float height = crashNotice != null ? NoticeHeightCrash : restartForTakeover ? NoticeHeightTakeover : NoticeHeight;
        float above = StackTop;
        var r = new Rect(20, (float.IsNaN(above) ? screenH - 135 : above - 8) - height, 470, height);
        EnsureStyles();
        if (Event.current.type == EventType.Repaint)
        {
            DrawBody(r);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 23), headerBack);
        }
        GUI.Label(new Rect(r.x + 8, r.y + 1, r.width - 16, 23), CompatDialog.Branded(null).ToUpperInvariant(), headStyle);
        GUI.Label(new Rect(r.x + 10, r.y + 31, r.width - 20, r.height - 36),
                  (crashNotice != null ? $"<b>{crashNotice}</b>\n" : "") +
                  (restartForTakeover ? "<b>Keel set up its takeover: restart the game once, then Keel runs every mod.</b>\n" : "") +
                  $"{bad} mod(s) did not start or threw errors. {Report.Summary(Keel.Plugins)}.\n<b>{hotkey.Value}</b> opens the report.", noteStyle);
    }

    // ---- What's new (see WhatsNew.cs) ----

    private Dictionary<string, SeenMod> seenLast, seenNow;
    private volatile List<NewsItem> newsFound;      // new, updated and gone mods with their notes, from the worker thread
    private volatile bool newsLooked;
    private bool newsSaved;
    private List<NewsItem> news;                     // what the card and the report show
    private bool newsClosed, newsArmed;
    private float newsUntil;
    private long newsSceneTicks;
    private Rect newsRect, newsCloseRect;
    private GUIStyle newsClose;
    private GUIStyle newsTitle, newsName, newsLine, newsFoot;
    private readonly Dictionary<NewsKind, GUIStyle> newsPills = new();

    /// <summary>Right after the scan, on a worker thread: what changed since the last start, and the authors' notes for it.</summary>
    private void StartWhatsNew(List<ModFolder> enabled)
    {
        string path = Path.Combine(Paths.BepInExRootPath, WhatsNew.FileName);
        List<PluginEntry> plugins = Keel.Plugins.ToList();
        string workshopRoot = ModFolders.WorkshopRoot;
        bool fetch = workshopNotes.Value;
        string ownPath = OwnPath;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                seenLast = WhatsNew.Read(path);
                seenNow = WhatsNew.Look(enabled, plugins, workshopRoot, ownPath, Keel.Guid, Keel.Version);
                List<NewsItem> found = WhatsNew.Compare(seenLast, seenNow, enabled);
                WhatsNew.AddNotes(found, fetch ? id => WhatsNew.FetchWorkshopPage(id, 6000) : null);
                newsFound = found;
                foreach (NewsItem n in found.Where(n => n.Kind is NewsKind.Updated or NewsKind.New))
                    log.LogInfo($"What's new: {n.Name}: {n.Lines.Count} line(s) from {(n.Source.Length > 0 ? n.Source : "nowhere")}" +
                                (n.SteamId != 0 ? $" (Workshop {n.SteamId}, Steam time {n.PrevUpdated} -> {n.Updated})" : ""));
                List<string> errors = WhatsNew.FetchErrors.ToList();
                if (errors.Count == 1)
                    log.LogWarning($"What's new could not read a Workshop changelog page: {errors[0]}");
                else if (errors.Count > 1)
                    log.LogWarning($"What's new could not read {errors.Count} Workshop changelog pages (CHANGELOG used instead), first: {errors[errors.Count - 1]}");
            }
            catch (Exception ex)
            {
                newsFound = new List<NewsItem>();
                log.LogWarning($"What's new: {ex.Message}");
            }
            newsLooked = true;
        });
    }

    /// <summary>Once the mods are started and the notes are in: the card's list, the report, and this start remembered for the next.</summary>
    private void FinishWhatsNew()
    {
        if (newsSaved || !done || !newsLooked)
            return;
        newsSaved = true;
        if (seenNow == null)
            return;
        news = WhatsNew.Problems(seenLast, Keel.Plugins).Concat(newsFound ?? new List<NewsItem>()).ToList();
        try
        {
            WhatsNew.MarkOk(seenNow, Keel.Plugins);
            WhatsNew.Write(Path.Combine(Paths.BepInExRootPath, WhatsNew.FileName), seenNow);
        }
        catch (Exception ex)
        {
            log.LogWarning($"Cannot remember this start's mods for What's new: {ex.Message}");
        }
        if (news.Count == 0)
            return;
        log.LogInfo($"What's new since the last start ({news.Count}):{Environment.NewLine}{WhatsNew.Text(news).TrimEnd()}");
        WriteReport();
    }

    private bool NewsVisible
    {
        get
        {
            if (news is not { Count: > 0 } || newsClosed || !GameState.StartupDone() || !GameState.MenuUp())
                return false;
            if (!newsArmed)
            {
                newsArmed = true;
                newsSceneTicks = Loading.LoadingWindow.SceneLoadedTicks;
                newsUntil = whatsNewSeconds.Value > 0 ? Time.realtimeSinceStartup + whatsNewSeconds.Value : float.MaxValue;
            }
            // It belongs to the main menu: gone for good once a mission or another scene loads, or its time is up.
            if (Loading.LoadingWindow.MissionRunning || Loading.LoadingWindow.SceneLoadedTicks != newsSceneTicks || Time.realtimeSinceStartup > newsUntil)
            {
                newsClosed = true;
                return false;
            }
            return !Loading.LoadingWindow.OnMenuSubPage;
        }
    }

    private void EnsureNewsStyles()
    {
        if (newsTitle != null)
            return;
        EnsureStyles();
        newsTitle = Text(13, KeelPalette.Muted);
        newsName = Text(14, KeelPalette.Text, below: 1);
        newsLine = Text(12, KeelPalette.Muted, below: 1);
        newsFoot = Text(11, KeelPalette.Muted);
        newsFoot.margin = new RectOffset(0, 0, 6, 0);
        newsClose = new GUIStyle(buttonStyle) { padding = new RectOffset() };
        foreach ((NewsKind kind, uint color) in new[]
        {
            (NewsKind.Problem, KeelPalette.Red), (NewsKind.Updated, KeelPalette.Blue),
            (NewsKind.New, KeelPalette.Green), (NewsKind.Gone, KeelPalette.Muted),
        })
            newsPills[kind] = StatusText(color);
    }

    // His layout 2026-11-12: the card is a glance (one line per mod), the report window shows two lines per mod and a click on a
    // mod opens all its notes there.
    private const int NewsMods = 5, ReportNewsLines = 2;
    private readonly HashSet<NewsItem> newsOpen = new();                      // mods opened in the report window
    private readonly Dictionary<NewsItem, Rect> newsRowRects = new();         // their rows, in the scroll view's content
    private Rect newsScrollRect;                                              // the scroll view, in the window

    // A click at a GUI position: the X closes the card, anywhere else on it opens the report window at its What's new list
    // (every changed mod, old -> new version, all of the author's lines). The card stays until its X is clicked. A click reaches
    // Keel through IMGUI and through the device check alike: it counts once.
    private bool NewsClick(Vector2 gui, int button = 0)
    {
        if (!NewsVisible || !newsRect.Contains(gui) || button != 0)
            return false;
        if (!FreshClick())
            return true;
        if (newsCloseRect.Contains(gui))      // the × closes it
        {
            newsClosed = true;
            return true;
        }
        scroll = Vector2.zero;          // What's new is the first section of the list
        if (!showWindow)
            ToggleWindow();
        return true;
    }

    /// <summary>One mod in the report window's What's new list: two lines of its notes, all of them once clicked open.</summary>
    private void NewsRow(NewsItem n)
    {
        const int cut = 160;
        bool open = newsOpen.Contains(n);
        int maxLines = open ? int.MaxValue : ReportNewsLines;
        // Clickable when anything is hidden: more lines than shown, or a shown line cut short.
        bool more = n.Lines.Count > ReportNewsLines || n.Lines.Take(ReportNewsLines).Any(l => l.Length > cut);
        EnsureNewsStyles();
        GUILayout.BeginHorizontal(rowStyle);
        GUILayout.Label(n.Tag, newsPills[n.Kind], GUILayout.Width(74), GUILayout.Height(18));
        GUILayout.BeginVertical();
        GUILayout.Label(n.VersionText.Length > 0 ? $"{n.Name}  <color=#{KeelPalette.Muted:X6}>{n.VersionText}</color>" : n.Name, newsName);
        foreach (string line in n.Lines.Take(maxLines))
            GUILayout.Label(maxLines < int.MaxValue && line.Length > cut ? line.Substring(0, cut - 3) + "…" : line, newsLine);
        if (n.Lines.Count > maxLines)
            Hint($"… {n.Lines.Count - maxLines} more line(s): click to read all");
        if (open && more)
            Hint("click to close");
        else if (more && n.Lines.Count <= ReportNewsLines)
            Hint("click to read all");
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
        if (more)
            NewsRowClick(n);
        GUILayout.Space(6);
    }

    private void Hint(string text) => GUILayout.Label($"<color=#{KeelPalette.Faint:X6}>{text}</color>", newsLine);

    private void NewsRowClick(NewsItem n)
    {
        Rect r = GUILayoutUtility.GetLastRect();
        if (Event.current.type == EventType.Repaint)
            newsRowRects[n] = r;
        else if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
        {
            ToggleNews(n);
            Event.current.Use();
        }
    }

    private void ToggleNews(NewsItem n)
    {
        if (!FreshClick())
            return;
        if (!newsOpen.Remove(n))
            newsOpen.Add(n);
    }

    private bool HeldOffered => crash is { HeldActive: true };

    private volatile List<string> packProblems;
    private float lastButton = -10f;
    private bool restarting;

    // A click arrives through IMGUI and through the device check alike: it counts once. A button that starts a program
    // (KeelWatcher's windows) waits longer, so an impatient second click does not open a second window.
    private const float ClickSeconds = 0.7f, ProgramClickSeconds = 3f;

    private bool FreshClick() => Cooled(ref lastButton, ClickSeconds);

    private static bool Cooled(ref float at, float seconds)
    {
        if (Time.realtimeSinceStartup - at < seconds)
            return false;
        at = Time.realtimeSinceStartup;
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

    private bool restartArmed;

    // Two clicks: a mission in progress is lost with the restart.
    private void RestartButton()
    {
        if (!FreshClick())
            return;
        if (!restartArmed)
        {
            restartArmed = true;
            return;
        }
        RestartGame();
    }

    private float logsAt = -10f;

    // The zip is made outside the game, by the same program as the crash window.
    private void SaveLogs()
    {
        if (!Cooled(ref logsAt, ProgramClickSeconds))
            return;
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

    private float sendAt = -10f;

    // Send report: KeelWatcher packs the logs without the Windows user name, shows them and sends only on the player's click.
    private void SendReport()
    {
        if (!Cooled(ref sendAt, ProgramClickSeconds))
            return;
        WriteReport();
        keelLog?.Flush();
        try
        {
            string exe = Path.Combine(Path.GetDirectoryName(OwnPath) ?? "", "KeelWatcher.exe");
            Process.Start(new ProcessStartInfo(exe, $"--send \"{Paths.BepInExRootPath}\" \"{Application.version}\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            notes.Add("A Keel window opens over the game: it shows what gets sent, and sends only when you click Send.");
        }
        catch (Exception ex)
        {
            notes.Add($"Could not open the send window: {ex.Message}");
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
    private readonly Dictionary<PluginState, GUIStyle> statusStyles = new();
    private Texture2D shade, track, thumb, headerBack;

    private static UnityEngine.Color Rgb(uint value, float alpha = 1f) =>
        new((value >> 16) / 255f, (value >> 8 & 255) / 255f, (value & 255) / 255f, alpha);

    private static Texture2D Solid(uint color, float alpha = 1f)
    {
        var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        t.SetPixel(0, 0, Rgb(color, alpha));
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

    private static GUIStyle Text(int size, uint color, FontStyle font = FontStyle.Normal, int below = 0)
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = font, wordWrap = true, richText = true, margin = new RectOffset(0, 0, 0, below), padding = new RectOffset(0, 0, 0, 0) };
        Paint(style, null, Rgb(color));
        return style;
    }

    private static GUIStyle StatusText(uint color)
    {
        var style = Text(11, color);
        style.wordWrap = false;
        style.alignment = TextAnchor.MiddleLeft;
        style.margin.right = 10;
        return style;
    }

    private void EnsureStyles()
    {
        if (windowStyle != null)
            return;
        shade = Solid(0, 0.35f);
        track = Solid(KeelPalette.Header);
        thumb = Solid(KeelPalette.Hover);
        headerBack = track;
        windowStyle = new GUIStyle(GUI.skin.window) { padding = new RectOffset(14, 14, 42, 12), border = new RectOffset(0, 0, 0, 0), contentOffset = Vector2.zero };
        Paint(windowStyle, Solid(KeelPalette.Panel), UnityEngine.Color.white);
        headStyle = Text(14, KeelPalette.Muted);
        headStyle.alignment = TextAnchor.MiddleLeft;
        metaStyle = Text(12, KeelPalette.Muted);
        metaStyle.alignment = TextAnchor.MiddleRight;
        metaStyle.wordWrap = false;
        statsStyle = Text(14, KeelPalette.Text, below: 8);
        lineStyle = new GUIStyle { margin = new RectOffset(0, 0, 0, 8) };
        lineStyle.normal.background = Solid(KeelPalette.Divider);
        alertStyle = Text(13, KeelPalette.Amber, below: 8);
        alertStyle.padding = new RectOffset(12, 12, 9, 9);
        alertStyle.normal.background = Solid(KeelPalette.Header);
        noteStyle = Text(12, KeelPalette.Muted, below: 6);
        groupStyle = Text(12, KeelPalette.Muted, below: 3);
        groupStyle.padding = new RectOffset(8, 8, 4, 4);
        groupStyle.normal.background = headerBack;
        rowStyle = new GUIStyle { padding = new RectOffset(8, 8, 6, 6), margin = new RectOffset(0, 0, 0, 2) };
        rowStyle.normal.background = Solid(KeelPalette.Row);
        nameStyle = Text(14, KeelPalette.Text, below: 1);
        sourceStyle = Text(11, KeelPalette.Muted);
        bodyStyle = Text(12, KeelPalette.Red);
        bodyStyle.margin = new RectOffset(0, 0, 5, 0);
        warnStyle = Text(12, KeelPalette.Amber);
        warnStyle.margin = new RectOffset(0, 0, 5, 0);
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, alignment = TextAnchor.MiddleCenter, border = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 8, 0, 0), padding = new RectOffset(8, 8, 0, 0) };
        Paint(buttonStyle, Solid(KeelPalette.Control), Rgb(KeelPalette.Text));
        buttonStyle.hover.background = buttonStyle.onHover.background = Solid(KeelPalette.Hover);
        buttonStyle.active.background = buttonStyle.onActive.background = Solid(KeelPalette.Pressed);
        foreach ((PluginState state, uint color) in new[]
        {
            (PluginState.Running, KeelPalette.Green), (PluginState.RunningWithErrors, KeelPalette.Amber),
            (PluginState.Failed, KeelPalette.Red), (PluginState.NotStarted, KeelPalette.Red),
            (PluginState.Skipped, KeelPalette.Muted), (PluginState.LeftToAnchorChain, KeelPalette.Blue),
            (PluginState.Pending, KeelPalette.Muted),
        })
            statusStyles[state] = StatusText(color);
    }

    private static string Coloured(List<(string text, Tone tone)> parts) => string.Join("   ·   ", parts.Select(part =>
    {
        uint color = part.tone switch
        {
            Tone.Problem => KeelPalette.Red,
            Tone.Warning => KeelPalette.Amber,
            Tone.OtherLoader => KeelPalette.Blue,
            _ => KeelPalette.Green,
        };
        return $"<color=#{color:X6}>{part.text}</color>";
    }));

    private static int GroupOf(PluginEntry p) => Report.IsProblem(p) ? 0 : p.State == PluginState.Running ? 1 : 2;

    /// <summary>Opens a required item's Workshop page in the Steam client (the card's and the report's "Get it").</summary>
    private void OpenItem(RequiredItem item)
    {
        try
        {
            Application.OpenURL(item.PageUrl);
            log.LogInfo($"Get it: opened the Workshop page of {item.Name} ({item.WorkshopId}).");
        }
        catch (Exception ex)
        {
            log.LogWarning($"Could not open the Workshop page of {item.Name}: {ex.Message}");
        }
    }

    private void Row(PluginEntry p)
    {
        GUILayout.BeginHorizontal(rowStyle);
        GUILayout.Label(ReportFormat.Tag(p.State), statusStyles[p.State], GUILayout.Width(96), GUILayout.Height(18));
        GUILayout.BeginVertical();
        GUILayout.Label($"{p.Display}  <color=#{KeelPalette.Muted:X6}>{p.Version}</color>", nameStyle);
        GUILayout.Label($"{p.Folder?.Label}  ·  {Path.GetFileName(p.Dll)}", sourceStyle);
        if (!string.IsNullOrEmpty(p.Reason))
            GUILayout.Label(p.Reason, bodyStyle);
        foreach (string w in p.Warnings)
            GUILayout.Label(w, warnStyle);
        if (p.LoadOrderWarning != null)
            GUILayout.Label(p.LoadOrderWarning, warnStyle);
        if (p.MissingItem != null && Report.IsProblem(p))
            RowButton("Get it", 90, () => OpenItem(p.MissingItem));
        if (p.LaterErrors > 0)
            GUILayout.Label($"{p.LaterErrors} error(s) since it started; first: {p.FirstLaterError}", warnStyle);
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
    }

    // The report's buttons as last drawn (GUI units), each with what it does: a click read from the device hits the same buttons.
    private readonly List<(Rect spot, Action act)> buttonSpots = new();

    private void Button(string text, float width, Action act)
    {
        bool clicked = GUILayout.Button(text, buttonStyle, GUILayout.Width(width), GUILayout.Height(27));
        if (Event.current.type == EventType.Repaint)
        {
            Rect r = GUILayoutUtility.GetLastRect();
            buttonSpots.Add((new Rect(window.x + r.x, window.y + r.y, r.width, r.height), act));
        }
        if (clicked)
            act();
    }

    /// <summary>
    /// A button inside the report list. The list is a scroll view, so its layout rect is in the scroll view's content: the spot the
    /// device click reads adds the scroll view's place in the window and takes the scroll off (the click on "Get it" did nothing before,
    /// 2026-11-21: the spot sat at the content position). A button scrolled out of the view gets no spot.
    /// </summary>
    private void RowButton(string text, float width, Action act)
    {
        bool clicked = GUILayout.Button(text, buttonStyle, GUILayout.Width(width), GUILayout.Height(27));
        if (Event.current.type == EventType.Repaint)
        {
            Rect r = GUILayoutUtility.GetLastRect();
            var inWindow = new Rect(window.x + newsScrollRect.x + r.x - scroll.x, window.y + newsScrollRect.y + r.y - scroll.y, r.width, r.height);
            var view = new Rect(window.x + newsScrollRect.x, window.y + newsScrollRect.y, newsScrollRect.width, newsScrollRect.height);
            float left = Mathf.Max(inWindow.xMin, view.xMin), right = Mathf.Min(inWindow.xMax, view.xMax);
            float top = Mathf.Max(inWindow.yMin, view.yMin), bottom = Mathf.Min(inWindow.yMax, view.yMax);
            if (right > left && bottom > top)
                buttonSpots.Add((Rect.MinMaxRect(left, top, right, bottom), act));
        }
        if (clicked)
            act();
    }

    private const float HeaderHeight = 28;

    private void DrawWindow(int id)
    {
        if (Event.current.type == EventType.Repaint)
            buttonSpots.Clear();
        var close = new Rect(window.width - 25, 2, 23, 24);
        DrawCaption(close);
        GUILayout.Label(done ? Coloured(Report.SummaryParts(Keel.Plugins)) : "Still starting mods …", statsStyle);
        GUILayout.Box(GUIContent.none, lineStyle, GUILayout.Height(1), GUILayout.ExpandWidth(true));
        foreach (string n in notes)
            GUILayout.Label(n, crashNotice != null && crashNotice.Contains(n) ? alertStyle : noteStyle);
        if (setupLine != null)
            GUILayout.Label(setupLine + (egNext is bool next && next != euromod.Eg
                ? $" Next start: Extended Guidance {(next ? "ON" : "off")} (Restart the game to switch now)." : ""), noteStyle);

        // The skin is shared with other mods: its scroll bar is restyled only while this window draws.
        GUIStyle bar = GUI.skin.verticalScrollbar, knob = GUI.skin.verticalScrollbarThumb;
        Texture2D barWas = bar.normal.background, knobWas = knob.normal.background;
        float barWidth = bar.fixedWidth, knobWidth = knob.fixedWidth;
        bar.normal.background = track;
        knob.normal.background = thumb;
        bar.fixedWidth = knob.fixedWidth = 8;
        scroll = GUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, bar, GUIStyle.none);
        DrawReportList();
        GUILayout.EndScrollView();
        if (Event.current.type == EventType.Repaint)
            newsScrollRect = GUILayoutUtility.GetLastRect();
        bar.normal.background = barWas;
        knob.normal.background = knobWas;
        bar.fixedWidth = barWidth;
        knob.fixedWidth = knobWidth;

        GUILayout.Space(6);
        GUILayout.Box(GUIContent.none, lineStyle, GUILayout.Height(1), GUILayout.ExpandWidth(true));
        DrawButtonRow();
        GUI.DragWindow(new Rect(0, 0, close.x, HeaderHeight));
    }

    private void DrawCaption(Rect close)
    {
        if (Event.current.type == EventType.Repaint)
        {
            GUI.DrawTexture(new Rect(0, 0, window.width, HeaderHeight), headerBack);
            buttonSpots.Add((new Rect(window.x + close.x, window.y + close.y, close.width, close.height), CloseWindow));
        }
        GUI.Label(new Rect(9, 0, window.width - 45, HeaderHeight), "R.A.T.S. KEEL  ·  MOD REPORT", headStyle);
        GUI.Label(new Rect(0, 0, window.width - 34, HeaderHeight), $"{Keel.Version}  ·  {GameState.BuildName}", metaStyle);
        if (GUI.Button(close, "×", buttonStyle))
            CloseWindow();
    }

    private void CloseWindow() => showWindow = false;

    // What's new, the plugins by group, the DLLs left out, inside the scroll view.
    private void DrawReportList()
    {
        if (news is { Count: > 0 })
        {
            GUILayout.Label($"WHAT'S NEW SINCE THE LAST START  ({news.Count})", groupStyle);
            foreach (NewsItem n in news)
                NewsRow(n);
            GUILayout.Space(8);
        }
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
                GUILayout.Label($"<color=#{KeelPalette.Light:X6}>{Path.GetFileName(d.Path)}</color>  ({d.Folder?.Label}): {d.Reason}", noteStyle);
        }
        GUILayout.Space(8);
        GUILayout.Label($"The same report is in {reportPath}", sourceStyle);
    }

    // The row of buttons; on a narrow window the second half goes on a line of its own.
    private const float ButtonRowWidth = 710, ButtonRowWidthWithEg = 890;

    private void DrawButtonRow()
    {
        GUILayout.BeginHorizontal();
        Button("Copy report", 120, () => GUIUtility.systemCopyBuffer = Report.Text(Keel.Plugins, Header(), notes, skippedDlls));
        Button("Save logs", 110, SaveLogs);
        Button("Send report", 120, SendReport);
        if (window.width < (euromod is { AcPack: true } ? ButtonRowWidthWithEg : ButtonRowWidth))
        {
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
        }
        if (HeldOffered)
            Button(heldClicked ? "Restart the game" : "Let held mods run", 160, HeldButton);
        else
            Button(restartArmed ? "Click again to restart" : "Restart the game", 180, RestartButton);
        if (euromod is { AcPack: true })
            Button(EgLabel(), 170, EgButton);
        GUILayout.FlexibleSpace();
        Button("Close", 100, CloseWindow);
        GUILayout.EndHorizontal();
    }
}
