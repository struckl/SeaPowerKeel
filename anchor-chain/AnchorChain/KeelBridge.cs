using BepInEx;
using BepInEx.Logging;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;
using Steamworks;
using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AnchorChain;

/// <summary>
/// Anchor Chain continues as R.A.T.S. Keel (agreed with Anchor Chain's authors, November 2026). Once per game start, on the
/// main menu, a game message box offers the switch:
/// <list type="bullet">
/// <item>Switch to Keel: subscribes to Keel's Workshop item, waits for Steam to download it, puts Keel's stub into
/// BepInEx\plugins and its takeover patcher into BepInEx\patchers (recorded in keel-setup.txt, so Keel's setup can undo it),
/// switches Keel on in the game's mod list and offers a restart.</item>
/// <item>Not now: nothing changes; the box comes again at the next start.</item>
/// </list>
/// Anchor Chain itself loads every mod as before at this start. Nothing here can stop that: every step is caught, and a game
/// build whose UI differs only loses the box. Players who have Keel already (its stub is in BepInEx\plugins) never see it.
/// </summary>
internal sealed class KeelBridge : MonoBehaviour
{
	internal const ulong KeelItem = 3835527459;
	private const string StubName = "SeaPowerKeel.Stub.dll";
	private const string StubFolder = "copy-to-BepInEx-plugins";
	private const string PatcherName = "SeaPowerKeel.Patcher.dll";
	private const string WatcherName = "KeelWatcher.exe";
	private const string ManifestName = "keel-setup.txt";
	private const string Header = "MESSAGE  ·  Anchor Chain";
	private const float DownloadSeconds = 300f;

	private const string OfferText =
		"Anchor Chain continues as R.A.T.S. Keel, made together with Anchor Chain's authors.\n\n" +
		"• Your mods keep working: Keel runs every Anchor Chain mod as it is\n" +
		"• Keel checks each mod against your game version and names the one that breaks\n" +
		"• Switch to Keel: subscribes to it, installs it and restarts the game. Keel's setup (KeelSetup.exe) can undo it\n" +
		"• Not now: Anchor Chain keeps working, and this asks again at the next start";

	private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Anchor Chain → Keel");
	private static bool begun;

	private object? box;
	private object? context;
	private bool cancelled;

	/// <summary>Called by the loader once its folder is switched on. Never throws.</summary>
	internal static void Begin()
	{
		if (begun) { return; }
		begun = true;
		try {
			if (File.Exists(Path.Combine(Paths.PluginPath, StubName))) {
				Log.LogInfo("R.A.T.S. Keel is installed already: no switch offer.");
				return;
			}
			var host = new GameObject("AnchorChain.KeelBridge");
			DontDestroyOnLoad(host);
			host.AddComponent<KeelBridge>();
		}
		catch (Exception e) {
			Log.LogWarning($"No switch offer: {e.Message}");
		}
	}

	private void Start() => StartCoroutine(OfferWhenMenuIsUp());

	private IEnumerator OfferWhenMenuIsUp()
	{
		// The settled main menu: after the load screen and its questions, so the box does not land behind them.
		Log.LogInfo("Waiting for the main menu to offer the switch.");
		float stable = 0f, waited = 0f;
		bool told = false;
		while (stable < 2f) {
			stable = OnMainMenu() ? stable + Time.unscaledDeltaTime : 0f;
			waited += Time.unscaledDeltaTime;
			if (!told && waited > 300f) {
				told = true;
				Log.LogWarning("No main menu after five minutes; still waiting to offer the switch.");
			}
			yield return null;
		}
		Log.LogInfo("Main menu is up.");
		try {
			Show(OfferText, "Switch to Keel", Switch, "Not now", () => Log.LogInfo("Not now: Anchor Chain stays this time."));
			Log.LogInfo("Switch offer shown.");
		}
		catch (Exception e) {
			Log.LogWarning($"The switch offer could not be shown: {e.Message}");
		}
	}

	private static bool OnMainMenu()
	{
		try {
			return MenuUp();
		}
		catch (Exception) {
			return false;   // a game build without these views: no box
		}
	}

	// The start-up load screen is done (the same test as Anchor Chain's preloader and Keel) and the main menu view exists, with no
	// mission or campaign scene loaded. LoadScreenView.Instance cannot tell it: the game sets it once and never clears it (VM test 2026-11-18).
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static bool MenuUp() =>
		Singleton<UnitMetadataProvider>.InstanceExists() && Singleton<UnitMetadataProvider>.Instance.UnitData != null &&
		Singleton<LoadScreen>.InstanceExists() && Singleton<LoadScreen>.Instance.LoadText == "Done" &&
		MainMenuView.Instance != null &&
		!UnityEngine.SceneManagement.SceneManager.GetSceneByName("mission").isLoaded &&
		!UnityEngine.SceneManagement.SceneManager.GetSceneByName("linear-campaign").isLoaded;

	// ---- the game's message box ----

	// The game's message box class is internal: built and filled by name (the same names on every 0.9 build).
	[MethodImpl(MethodImplOptions.NoInlining)]
	private void Show(string message, string okLabel, Action ok, string? cancelLabel = null, Action? cancel = null)
	{
		CloseBox();
		Type type = typeof(MessageBoxType).Assembly.GetType("SeapowerUI.ViewModels.MessageBoxViewModel", true)!;
		object b = Activator.CreateInstance(type, true)!;
		void Set(string name, object? value) => type.GetProperty(name, Any)!.SetValue(b, value);
		Set("MessageBoxType", MessageBoxType.Info);
		Set("Header", Header);
		Set("Message", message);
		Set("AcknowledgeCommandLabel", okLabel);
		Set("AcknowledgeCommand", new DelegateCommand(_ => { CloseBox(); Run(ok); }));
		if (cancelLabel != null) {
			Set("CancelCommandLabel", cancelLabel);
			Set("CancelCommand", new DelegateCommand(_ => { CloseBox(); if (cancel != null) { Run(cancel); } }));
		}
		context = MainMenuView.Instance;
		box = b;
		WindowHostCommands.OpenWindowCommand.Execute(b, (Noesis.UIElement)context);
	}

	private const System.Reflection.BindingFlags Any =
		System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

	private void CloseBox()
	{
		if (box == null) { return; }
		try {
			CloseCore();
		}
		catch (Exception) {
			// already gone with its view
		}
		box = null;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private void CloseCore() => WindowHostCommands.CloseWindowCommand.Execute(box, (Noesis.UIElement)context!);

	private void SetText(string message)
	{
		try {
			box?.GetType().GetProperty("Message", Any)?.SetValue(box, message);
		}
		catch (Exception) {
			// the box is gone; the log still says what happens
		}
	}

	private static void Run(Action action)
	{
		try {
			action();
		}
		catch (Exception e) {
			Log.LogError($"Switch step failed: {e}");
		}
	}

	// ---- the switch ----

	private void Switch() => StartCoroutine(SwitchSteps());

	private IEnumerator SwitchSteps()
	{
		Log.LogInfo("Switch to Keel: getting it from the Workshop.");
		cancelled = false;
		Show("Getting R.A.T.S. Keel from the Steam Workshop ...", "Cancel", () => { cancelled = true; Log.LogInfo("Switch cancelled while downloading."); });

		string? folder = null;
		string? failure = null;
		try {
			folder = Installed();
			if (folder == null) {
				Subscribe();
			}
		}
		catch (Exception e) {
			failure = e.Message;
		}

		float until = Time.realtimeSinceStartup + DownloadSeconds;
		while (folder == null && failure == null && !cancelled && Time.realtimeSinceStartup < until) {
			yield return new WaitForSecondsRealtime(0.5f);
			try {
				folder = Installed();
				if (folder == null) { SetText($"Getting R.A.T.S. Keel from the Steam Workshop ... {Progress()}"); }
			}
			catch (Exception e) {
				failure = e.Message;
			}
		}
		if (cancelled) { yield break; }
		if (folder == null) {
			Log.LogWarning($"Keel was not downloaded: {failure ?? "Steam took too long"}.");
			Show("Steam did not finish downloading R.A.T.S. Keel" + (failure != null ? $" ({failure})" : "") + ".\n\n" +
			     "• Subscribe to it on its Workshop page, then run KeelSetup.exe from its folder\n" +
			     "• Anchor Chain keeps working meanwhile",
			     "Open the Workshop page", () => Application.OpenURL($"steam://url/CommunityFilePage/{KeelItem}"), "Close");
			yield break;
		}

		Log.LogInfo($"Keel is on disk: {folder}");
		try {
			SwitchOn(folder);   // first: if the game's list cannot take it, nothing is copied yet
			Install(folder);
		}
		catch (Exception e) {
			Log.LogError($"Keel could not be installed: {e}");
			Show($"R.A.T.S. Keel is downloaded but could not be installed ({e.Message}).\n\n" +
			     "• Run KeelSetup.exe from Keel's Workshop folder instead\n" +
			     "• Anchor Chain keeps working meanwhile",
			     "Open the Workshop page", () => Application.OpenURL($"steam://url/CommunityFilePage/{KeelItem}"), "Close");
			yield break;
		}
		Log.LogInfo("Keel is installed and switched on; offering the restart.");
		Show("R.A.T.S. Keel is installed and switched on.\n\n" +
		     "• Restart: the game closes and starts again with Keel\n" +
		     "• Later: Keel takes over the next time you start the game",
		     "Restart", () => Restart(folder), "Later", () => Log.LogInfo("Restart later."));
	}

	/// <summary>Keel's Workshop folder once Steam has it complete, else null.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static string? Installed()
	{
		var item = new PublishedFileId_t(KeelItem);
		var state = (EItemState)SteamUGC.GetItemState(item);
		if (!state.HasFlag(EItemState.k_EItemStateSubscribed) || !state.HasFlag(EItemState.k_EItemStateInstalled) ||
		    state.HasFlag(EItemState.k_EItemStateDownloading) || state.HasFlag(EItemState.k_EItemStateDownloadPending) ||
		    state.HasFlag(EItemState.k_EItemStateNeedsUpdate)) {
			return null;
		}
		if (!SteamUGC.GetItemInstallInfo(item, out _, out string path, 1024u, out _) || !Directory.Exists(path)) { return null; }
		return File.Exists(Path.Combine(path, StubFolder, StubName)) ? path : null;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void Subscribe()
	{
		if (!SteamManager.Initialized) { throw new InvalidOperationException("Steam is not running"); }
		var item = new PublishedFileId_t(KeelItem);
		SteamUGC.SubscribeItem(item);
		SteamUGC.DownloadItem(item, true);
		Log.LogInfo("Subscribed to R.A.T.S. Keel; Steam downloads it.");
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static string Progress()
	{
		return SteamUGC.GetItemDownloadInfo(new PublishedFileId_t(KeelItem), out ulong done, out ulong total) && total > 0
			? $"{done * 100 / total} %"
			: "";
	}

	/// <summary>What KeelSetup.exe does when BepInEx is there already: Keel's stub and takeover patcher, recorded for its Restore.</summary>
	private static void Install(string keelFolder)
	{
		string game = Paths.GameRootPath;
		string manifest = Path.Combine(game, ManifestName);
		List<string> lines = File.Exists(manifest) ? File.ReadAllLines(manifest).Where(l => l.Length > 0).ToList() : new List<string>();
		void Note(string entry)
		{
			if (!lines.Contains(entry)) { lines.Add(entry); }
			File.WriteAllLines(manifest, lines);     // after every step: an interrupted install can still be undone
		}

		Directory.CreateDirectory(Paths.PluginPath);
		string stub = Path.Combine(Paths.PluginPath, StubName);
		if (!File.Exists(stub)) { Note("created\t" + @"BepInEx\plugins\" + StubName); }
		File.Copy(Path.Combine(keelFolder, StubFolder, StubName), stub, true);

		string patcherSource = Path.Combine(keelFolder, PatcherName);
		if (File.Exists(patcherSource)) {
			Directory.CreateDirectory(Paths.PatcherPluginPath);
			string patcher = Path.Combine(Paths.PatcherPluginPath, PatcherName);
			if (!File.Exists(patcher)) { Note("created\t" + @"BepInEx\patchers\" + PatcherName); }
			File.Copy(patcherSource, patcher, true);
		}
		Log.LogInfo("Keel's stub and takeover patcher are in BepInEx (recorded in keel-setup.txt).");
	}

	/// <summary>Keel switched on in the game's mod list and the list saved, the way the Mods menu does it.</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void SwitchOn(string keelFolder)
	{
		FileManager files = Singleton<FileManager>.Instance;
		TrulyObservableCollection<SearchDirectory> list = files.Directories;
		SearchDirectory? keel = list.FirstOrDefault(d => d.steamId.m_PublishedFileId == KeelItem);
		if (keel == null) {
			keel = new SearchDirectory(new DirectoryInfo(keelFolder), isActive: true, isEditable: true, new PublishedFileId_t(KeelItem));
			// Last among the mods, ahead of the game's own base layer, which the game always lists last (Keel holds no game
			// data, so its place does not matter). Only members every build since 0.8.7 has.
			list.Insert(Math.Max(0, list.Count - 1), keel);
		}
		keel.IsChecked = true;
		files.SaveDirectories(list);
		Log.LogInfo("Keel is switched on in the game's mod list.");
	}

	private void Restart(string keelFolder)
	{
		string watcher = Path.Combine(keelFolder, WatcherName);
		try {
			Process self = Process.GetCurrentProcess();
			string exe = self.MainModule?.FileName ?? "";
			Process.Start(new ProcessStartInfo(watcher, $"--relaunch {self.Id} \"{exe}\"") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
		}
		catch (Exception e) {
			Log.LogWarning($"Could not restart the game by itself: {e.Message}");
			Show("The game could not restart by itself.\n\n• Close it and start it again: Keel takes over then", "OK", () => { });
			return;
		}
		Log.LogInfo("Restarting the game for Keel.");
		Application.Quit();
	}
}
