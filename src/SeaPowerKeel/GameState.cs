using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SeaPowerKeel;

/// <summary>
/// What Keel needs to know about the running game, read by name so that one Keel DLL works on every game build: which
/// map the game has, its own mod list, and whether start-up has finished.
/// </summary>
internal static class GameState
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>The 3D globe build (public beta from 26 Oct 2026, the default from 11 Nov) has the map display state; 0.8.7 does not.</summary>
    public static readonly bool Globe = Type.GetType("SeapowerUI.MapDisplayState, Seapower-Scripts") != null;

    public static string BuildName => Globe ? "3D globe map" : "flat map (0.8.7)";

    private static Type T(string name) => Type.GetType(name + ", Seapower-Scripts");

    private static UnityEngine.Object Find(Type type)
    {
        if (type == null)
            return null;
        try
        {
#pragma warning disable CS0618 // FindObjectOfType: the one both game builds' Unity versions have
            return UnityEngine.Object.FindObjectOfType(type);
#pragma warning restore CS0618
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The game's own mod list once it has made one (the private <c>directories</c> field of <c>FileManager</c> on both
    /// builds; reading the <c>Directories</c> property would make the game build it early). Null while there is none.
    /// </summary>
    public static List<ModFolder> GameModList()
    {
        UnityEngine.Object fileManager = Find(T("SeaPower.FileManager"));
        if (fileManager == null)
            return null;
        if (fileManager.GetType().GetField("directories", Any)?.GetValue(fileManager) is not IEnumerable dirs)
            return null;
        var list = new List<ModFolder>();
        string streaming = Path.GetFullPath(Application.streamingAssetsPath).TrimEnd('\\', '/');
        foreach (object d in dirs)
        {
            if (d == null)
                continue;
            Type t = d.GetType();
            if (t.GetProperty("DirectoryInfo", Any)?.GetValue(d) is not DirectoryInfo info)
                continue;
            // The globe build lists its base data, the user layer and runtime mounts (missions, overlays) too: no mods.
            // 0.8.7 has no kinds.
            string kind = t.GetProperty("Kind", Any)?.GetValue(d)?.ToString();
            if (kind != null && kind != "Mod")
                continue;
            string full = info.FullName.TrimEnd('\\', '/');
            if (string.Equals(full, streaming, StringComparison.OrdinalIgnoreCase) || info.Name == "original" || info.Name == "user")
                continue;
            ulong steamId = 0;
            object id = t.GetField("steamId", Any)?.GetValue(d);
            if (id?.GetType().GetField("m_PublishedFileId", Any)?.GetValue(id) is ulong u)
                steamId = u;
            bool on = t.GetProperty("IsChecked", Any)?.GetValue(d) is bool b && b;
            list.Add(new ModFolder { Path = info.FullName, Name = info.Name, SteamId = steamId, Enabled = on, Order = list.Count });
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Start-up done: the unit data is loaded and the start-up load screen says "Done". Mods started in the middle of it
    /// can keep the unit data from loading on the globe build. True when the game has none of these types.
    /// </summary>
    public static bool StartupDone()
    {
        try
        {
            Type unitsType = T("SeaPower.UnitMetadataProvider");
            Type screenType = T("SeaPower.LoadScreen");
            if (unitsType == null || screenType == null)
                return true;
            UnityEngine.Object units = Find(unitsType);
            UnityEngine.Object screen = Find(screenType);
            if (units == null || screen == null)
                return false;
            object data = unitsType.GetProperty("UnitData", Any)?.GetValue(units) ?? unitsType.GetField("UnitData", Any)?.GetValue(units);
            object text = screenType.GetProperty("LoadText", Any)?.GetValue(screen) ?? screenType.GetField("LoadText", Any)?.GetValue(screen);
            return data != null && text as string == "Done";
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static System.Reflection.FieldInfo playerTaskforce;
    /// <summary>A mission is running: the player's taskforce exists (<c>Globals._playerTaskforce</c>).</summary>
    public static bool InMission()
    {
        try
        {
            playerTaskforce ??= T("SeaPower.Globals")?.GetField("_playerTaskforce", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return playerTaskforce != null && playerTaskforce.GetValue(null) != null;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// True while the main menu shows a page other than its top one (Scenarios, Options, Campaign ...): the game keeps one
    /// <c>MenuSystemViewModel</c> whose <c>CurrentWindow</c> is the page. False on the top page, on a blank page and when unknown.
    /// </summary>
    public static bool OnMenuSubPage()
    {
        try
        {
            object menu = Find(T("SeapowerUI.ViewModels.MenuSystemViewModel"));
            object page = menu?.GetType().GetProperty("CurrentWindow", Any)?.GetValue(menu);
            string name = page?.GetType().Name;
            return name != null && name != "MainMenuView" && name != "BlankMenuView";
        }
        catch (Exception) { return false; }
    }

    /// <summary>The game's <c>SeaPower.IniHandler</c> type (null if this build has none under that name).</summary>
    public static Type IniHandlerType => T("SeaPower.IniHandler");

    /// <summary>
    /// The game's StreamingAssets folder as the game itself has it (<c>Globals._streamingAssetsPath</c>, which Anchor Chain
    /// builds its <c>ACConfigs</c> folder on), else Unity's path.
    /// </summary>
    public static string StreamingAssets(out string how)
    {
        try
        {
            object path = T("SeaPower.Globals")?.GetField("_streamingAssetsPath", Any)?.GetValue(null);
            if (path is DirectoryInfo info)
            {
                how = "Globals._streamingAssetsPath";
                return info.FullName;
            }
        }
        catch (Exception)
        {
            // fall through
        }
        how = "Application.streamingAssetsPath (Globals._streamingAssetsPath not found)";
        return Path.GetFullPath(Application.streamingAssetsPath);
    }

    /// <summary>
    /// Anchor Chain's loader is in the game (its preloader found and started it): an assembly named AnchorChain that
    /// <paramref name="ours"/> does not claim. Keel's own load of Anchor Chain's DLL as a library does not count.
    /// </summary>
    public static bool AnchorChainRunning(Func<Assembly, bool> ours) =>
        AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "AnchorChain" && (ours == null || !ours(a)));
}
