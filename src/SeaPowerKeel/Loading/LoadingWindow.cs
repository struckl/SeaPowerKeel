using System;
using System.Diagnostics;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace SeaPowerKeel.Loading;

/// <summary>
/// The loading window: a window of its own (Win32, own thread) that shows the start-up steps and the mods as Keel starts them, so the
/// first minute of the game is not a frozen screen. Keel opens it in <c>KeelHost.Awake</c>, which runs when the stub starts, before
/// Anchor Chain's preloader (BepInEx starts plugins in GUID order). It reads only log lines; see docs/loading-window.md.
/// </summary>
internal static class LoadingWindow
{
    public static void Start(ConfigFile config, ManualLogSource log)
    {
        try
        {
            ConfigEntry<bool> show = config.Bind("Loading window", "Show", true,
                "Show a small window with the start-up steps and the mods as they start, until every mod has started.");
            ConfigEntry<string> corner = config.Bind("Loading window", "Corner", "TopLeft",
                new ConfigDescription("Corner of the main monitor the window sits in.", new AcceptableValueList<string>("TopLeft", "TopRight", "BottomLeft", "BottomRight")));
            ConfigEntry<float> opacity = config.Bind("Loading window", "Opacity", 0.92f,
                new ConfigDescription("0.4 = see-through, 1 = solid.", new AcceptableValueRange<float>(0.4f, 1f)));
            ConfigEntry<float> linger = config.Bind("Loading window", "SecondsAfterLastMod", 2.5f,
                new ConfigDescription("The window closes this long after Keel reports that every mod has started.", new AcceptableValueRange<float>(0f, 60f)));
            ConfigEntry<float> max = config.Bind("Loading window", "LongestSeconds", 300f,
                new ConfigDescription("Whatever happens, the window is gone this many seconds after the game process started.", new AcceptableValueRange<float>(30f, 1800f)));
            ConfigEntry<bool> click = config.Bind("Loading window", "ClickHides", true,
                "A click on the window closes it (it never takes the keyboard focus from the game).");
            if (!show.Value)
                return;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return;

            DateTime started;
            try { started = Process.GetCurrentProcess().StartTime; }
            catch (Exception) { started = DateTime.Now; }
            var progress = new Progress(() => (DateTime.Now - started).TotalSeconds);
            BepInEx.Logging.Logger.Listeners.Add(new Feed(progress));
            if (!Enum.TryParse(corner.Value, out Corner c)) c = Corner.TopLeft;
            new Overlay(progress, new OverlayOptions
            {
                Corner = c, Opacity = opacity.Value, LingerSeconds = linger.Value, MaxSeconds = max.Value, ClickToHide = click.Value,
            }).Start(m => log.LogWarning(m));
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
