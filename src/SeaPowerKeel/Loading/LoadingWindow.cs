using System;
using System.Diagnostics;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace SeaPowerKeel.Loading;

/// <summary>
/// The loading window: a window of its own (Win32, own thread) that shows the start-up steps and the mods as Keel starts them, so the
/// first minute of the game is not a frozen screen; when every mod has started it shrinks into a summary card in the bottom left corner.
/// Keel opens it in <c>KeelHost.Awake</c>, which runs when the stub does, before Anchor Chain's preloader (BepInEx starts plugins in GUID
/// order). It reads only log lines; see docs/loading-window.md.
/// </summary>
internal static class LoadingWindow
{
    /// <summary>Set by a click on the summary card; Keel's host (main thread) opens its report window.</summary>
    public static volatile bool ReportRequested;

    /// <summary>True while a mission is running (set by Keel's host about twice a second): the summary card belongs to the main menu only.</summary>
    public static volatile bool MissionRunning;
    /// <summary>The main menu shows a page other than its top one: the summary card hides and comes back with the top page.</summary>
    public static volatile bool OnMenuSubPage;
    /// <summary>When (UTC ticks) the game last loaded a scene on its own (a mission, the editor, a campaign), not added to the menu.</summary>
    public static long SceneLoadedTicks;

    /// <summary>One short line about a crash or a crash hunt: the summary card turns amber and shows it. Set before the card appears.</summary>
    internal static volatile string Alert;
    /// <summary>The game waits for the player (a dialog in the game window): shown instead of "still loading".</summary>
    internal static volatile string WaitingFor;
    /// <summary>The window and its summary card are up, so nothing else has to announce problems.</summary>
    internal static volatile bool Shown;
    internal static volatile bool ExternalProgress;
    private static volatile string preparationStatus;

    internal static void ReportPreparation(string text) => preparationStatus = text;
    internal static string PreparationStatus => preparationStatus;

    // ---- hand-over to the game (rule 2026-11-12: every window is drawn by the game) ---------------------------------------
    // The own window exists only because the game's main thread can stand still while a mod starts. Once it has glided to its
    // summary spot the game draws again: the overlay publishes where it is, Keel's GUI draws the same card there and says so,
    // and the overlay fades out. If the game-drawn card never says so, the overlay keeps its own summary (the old way).

    /// <summary>The start-up progress (counts, times) that both summary cards show.</summary>
    internal static Progress Progress;
    /// <summary>The overlay sits on its summary spot: left, top, width, height in pixels of the game window, and its scale.</summary>
    internal static volatile bool SummaryReady;
    internal static int SummaryX, SummaryY, SummaryW, SummaryH;
    internal static float SummaryScale = 1f;
    /// <summary>The overlay window is gone (faded, clicked away, handed over); set by its thread.</summary>
    internal static volatile bool OverlayGone;
    /// <summary>Keel's GUI draws the summary card now: the overlay fades out.</summary>
    internal static volatile bool GameDrawsSummary;
    /// <summary>The overlay's settings the game-drawn card follows.</summary>
    internal static bool ClickHides = true;
    internal const string ProfileUrl = "https://steamcommunity.com/profiles/76561198169349193";

    public static void Start(ConfigFile config, ManualLogSource log)
    {
        try
        {
            ConfigEntry<bool> show = config.Bind("Loading window", "Show", true,
                "Show a small window with the start-up steps and the mods as they start. When every mod has started it shrinks into a summary in the bottom left corner.");
            ConfigEntry<string> spot = config.Bind("Loading window", "Spot", "TopRight",
                new ConfigDescription("Where the window sits while the game starts. Marker = the blank band under the main menu's news box; or a corner of the game's window.",
                    new AcceptableValueList<string>("Marker", "TopLeft", "TopRight", "BottomLeft", "BottomRight")));
            ConfigEntry<float> scale = config.Bind("Loading window", "Scale", 0f,
                new ConfigDescription("Size of the window; 0 = by the height of the game's window (1.5 on a 4K screen).", new AcceptableValueRange<float>(0f, 4f)));
            ConfigEntry<float> opacity = config.Bind("Loading window", "Opacity", 0.92f,
                new ConfigDescription("0.4 = see-through, 1 = solid.", new AcceptableValueRange<float>(0.4f, 1f)));
            ConfigEntry<float> linger = config.Bind("Loading window", "SecondsAfterLastMod", 2f,
                new ConfigDescription("The full window stays this long after Keel reports that every mod has started, then glides to the corner.", new AcceptableValueRange<float>(0f, 60f)));
            ConfigEntry<float> max = config.Bind("Loading window", "LongestSeconds", 300f,
                new ConfigDescription("Whatever happens, the full window is done this many seconds after the game process started.", new AcceptableValueRange<float>(30f, 1800f)));
            ConfigEntry<bool> click = config.Bind("Loading window", "ClickHides", true,
                "A click on the full window hides it. A click on the summary opens Keel's report; the cards on the main menu close with their x. (The window never takes the keyboard focus from the game.)");
            if (!show.Value)
                return;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return;

            DateTime started;
            try { started = Process.GetCurrentProcess().StartTime; }
            catch (Exception) { started = DateTime.Now; }
            var progress = new Progress(() => (DateTime.Now - started).TotalSeconds);
            Progress = progress;
            ClickHides = click.Value;
            BepInEx.Logging.Logger.Listeners.Add(new Feed(progress));
            if (!Enum.TryParse(spot.Value, out Spot s)) s = Spot.TopRight;
            new Overlay(progress, new OverlayOptions
            {
                Spot = s, Scale = scale.Value, Opacity = opacity.Value, LingerSeconds = linger.Value, SummarySeconds = 0,
                MaxSeconds = max.Value, ClickToHide = click.Value, OnSummaryClick = () => ReportRequested = true,
            }).Start(m => log.LogWarning(m));
            Shown = true;
        }
        catch (Exception ex)
        {
            log.LogWarning("Loading window not started: " + ex.Message);    // never take the game down with it
        }
    }

    /// <summary>Every BepInEx log line (the game's and the mods' too) goes to the progress model.</summary>
    private sealed class Feed : ILogListener
    {
        private readonly Progress progress;
        public Feed(Progress progress) => this.progress = progress;
        public void LogEvent(object sender, LogEventArgs e) => progress.Feed(e.Source?.SourceName, e.Data?.ToString());
        public void Dispose() { }
    }
}
