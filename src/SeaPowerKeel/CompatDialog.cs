using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace SeaPowerKeel;

/// <summary>
/// Game 0.9.0 asks at every start about switched-on mods whose <c>_info.ini</c> names another game version, as one long list of names
/// and full paths. Keel rewrites that list: short names, code mods apart from data mods, the version each one says it was made for,
/// and what Keel does about code mods. The game's own first line (in the player's language) and its buttons stay. While the dialog
/// waits for a click, Keel's loading window says so instead of counting the seconds since the last message.
/// Hooked on the message box's Message setter, a plain class with no UI statics, so it is safe from Awake on every build; a build
/// without the dialog never sets that text.
/// </summary>
internal static class CompatDialog
{
    private static string header;       // the game's own first line, LoadScreen/WarningMods
    private static object box;          // the dialog Keel rewrote, until it is answered
    private static Type commandType;
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>Null when hooked, else why not.</summary>
    public static string Install()
    {
        try
        {
            Type boxType = AccessTools.TypeByName("SeapowerUI.ViewModels.MessageBoxViewModel");
            commandType = AccessTools.TypeByName("SeaPower.DelegateCommand");
            if (boxType == null || commandType == null)
                return "the game's message box is not in this build";
            var harmony = new Harmony("dev.sewerlabs.seapowerkeel.compatdialog");
            harmony.Patch(AccessTools.PropertySetter(boxType, "Message"), prefix: new HarmonyMethod(typeof(CompatDialog).GetMethod(nameof(MessagePrefix), Any)));
            MethodInfo headerSetter = AccessTools.PropertySetter(boxType, "Header");
            if (headerSetter != null)
                harmony.Patch(headerSetter, prefix: new HarmonyMethod(typeof(CompatDialog).GetMethod(nameof(HeaderPrefix), Any)));
            foreach (string command in new[] { "AcknowledgeCommand", "CancelCommand" })
            {
                MethodInfo setter = AccessTools.PropertySetter(boxType, command);
                if (setter != null)
                    harmony.Patch(setter, prefix: new HarmonyMethod(typeof(CompatDialog).GetMethod(nameof(CommandPrefix), Any)));
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void MessagePrefix(object __instance, ref string value)
    {
        try
        {
            // Every message box the game shows, in one log line: tests (and the loading window) see a box that waits for a
            // click, whoever opened it (the game's version question, a mod's error box). 2026-11-12: boxes went unclicked.
            if (!string.IsNullOrEmpty(value))
            {
                string first = value.Replace("\r", "").Replace("\n", " / ");
                BepInEx.Logging.Logger.CreateLogSource(Keel.Name).LogInfo("Game message box: " + (first.Length > 140 ? first.Substring(0, 140) + "..." : first));
            }
            header ??= Text("LoadScreen", "WarningMods");
            if (string.IsNullOrEmpty(header) || value == null || !value.StartsWith(header, StringComparison.Ordinal))
            {
                if (__instance == box && box != null)
                {
                    // The game shows another message in the same box: the brand goes.
                    box = null;
                    PropertyInfo old = __instance.GetType().GetProperty("Header", Any);
                    if (old?.CanWrite == true && old.GetValue(__instance) is string was && was.EndsWith("  ·  " + Keel.Name))
                        old.SetValue(__instance, was.Substring(0, was.Length - ("  ·  " + Keel.Name).Length));
                }
                return;
            }
            string rewritten = Rewrite(header);
            if (rewritten == null)
                return;
            value = rewritten;
            box = __instance;
            // The top bar says where the list comes from (his wish 2026-11-12): "MESSAGE  ·  R.A.T.S. Keel".
            PropertyInfo h = __instance.GetType().GetProperty("Header", Any);
            if (h?.CanWrite == true)
                h.SetValue(__instance, Branded(h.GetValue(__instance) as string));
            BepInEx.Logging.Logger.CreateLogSource(Keel.Name).LogInfo("The game's mod version dialog is shown with Keel's list.");
            Loading.LoadingWindow.WaitingFor = "the game asks about mods (in the game window)";
        }
        catch (Exception)
        {
            // the game's own text stays
        }
    }

    internal static string Branded(string header)
    {
        string h = string.IsNullOrEmpty(header) ? "MESSAGE" : header;
        return h.Contains(Keel.Name) ? h : $"{h}  ·  {Keel.Name}";
    }

    // The header can be set after the text: it keeps the brand on Keel's dialog.
    private static void HeaderPrefix(object __instance, ref string value)
    {
        if (__instance == box && box != null)
            value = Branded(value);
    }

    // The dialog's buttons are set after its text: wrapped so the loading window stops waiting when one is clicked.
    private static void CommandPrefix(object __instance, ref object value)
    {
        if (__instance != box || value == null || !(value is System.Windows.Input.ICommand inner))
            return;
        Action<object> then = x =>
        {
            Loading.LoadingWindow.WaitingFor = null;
            box = null;
            inner.Execute(x);
        };
        try
        {
            value = Activator.CreateInstance(commandType, then);
        }
        catch (Exception)
        {
            // the game's own command stays
        }
    }

    private static string Text(string section, string key)
    {
        Type handler = AccessTools.TypeByName("SeaPower.LanguageResourceHandler");
        Type singleton = AccessTools.TypeByName("SeaPower.Singleton`1")?.MakeGenericType(handler);
        object instance = singleton?.GetProperty("Instance", Any)?.GetValue(null);
        // getText(section, key) on 0.8.7, getText(section, key, logError, verboseError) on 0.9.0: fill what follows with defaults.
        MethodInfo get = handler?.GetMethods(Any).FirstOrDefault(m => m.Name == "getText" && m.GetParameters().Length >= 2
                                                                  && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[1].ParameterType == typeof(string));
        if (instance == null || get == null)
            return null;
        object[] args = get.GetParameters().Select((p, i) => i == 0 ? section : i == 1 ? key : p.HasDefaultValue ? p.DefaultValue : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)).ToArray();
        return get.Invoke(instance, args) as string;
    }

    /// <summary>The new text from the game's own list (the same test the game uses), or null.</summary>
    private static string Rewrite(string first)
    {
        Type managerType = AccessTools.TypeByName("SeaPower.FileManager");
        UnityEngine.Object manager = managerType == null ? null : UnityEngine.Object.FindObjectOfType(managerType);
        if (manager == null)
            return null;
        object list = managerType.GetProperty("Directories", Any)?.GetValue(manager) ?? managerType.GetField("directories", Any)?.GetValue(manager);
        if (!(list is IEnumerable dirs))
            return null;
        var code = new List<string>();
        var data = new List<string>();
        foreach (object d in dirs)
        {
            if (d == null)
                continue;
            Type t = d.GetType();
            bool settingsMod = t.GetProperty("IsSettingsMod", Any)?.GetValue(d) as bool? ?? false;
            bool on = t.GetProperty("IsChecked", Any)?.GetValue(d) as bool? ?? false;
            bool matches = t.GetMethod("MatchesVersion", Any, null, Type.EmptyTypes, null)?.Invoke(d, null) as bool? ?? true;
            if (!settingsMod || !on || matches)
                continue;
            string name = t.GetProperty("Name", Any)?.GetValue(d) as string;
            string path = (t.GetProperty("DirectoryInfo", Any)?.GetValue(d) as DirectoryInfo)?.FullName;
            if (string.IsNullOrEmpty(name))
                name = Path.GetFileName(path ?? "?");
            string line = "  •  " + name + Declared(t.GetField("_compatibility", Any)?.GetValue(d));
            (HasCode(path) ? code : data).Add(line);
        }
        if (code.Count + data.Count == 0)
            return null;
        var sb = new StringBuilder(first).Append("\n");
        if (code.Count > 0)
        {
            sb.Append("\nCode mods\n");
            sb.Append(string.Join("\n", code)).Append("\n");
        }
        if (data.Count > 0)
        {
            sb.Append("\nData mods  (units, sounds, pictures, missions)\n");
            sb.Append(string.Join("\n", data)).Append("\n");
        }
        sb.Append("\nIgnore keeps them on; the other button switches them off and reloads.");
        return sb.ToString();
    }

    private static bool HasCode(string path)
    {
        try
        {
            return path != null && Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    // What the mod's _info.ini says, in the game's own terms.
    private static string Declared(object c)
    {
        if (c == null)
            return "";
        string Field(string n) => c.GetType().GetField(n, Any)?.GetValue(c) as string;
        string exact = Field("ExactVersion"), approx = Field("ApproximateVersion");
        if (!string.IsNullOrEmpty(exact))
            return $"  -  made for {exact} only";
        if (!string.IsNullOrEmpty(approx))
            return $"  -  made for {approx}";
        var parts = new List<string>();
        foreach ((string key, string word) in new[] { ("GreaterThanEqualToVersion", ">="), ("GreaterThanVersion", ">"), ("LessThanEqualToVersion", "<="), ("LessThanVersion", "<") })
        {
            string v = Field(key);
            if (!string.IsNullOrEmpty(v))
                parts.Add(word + " " + v);
        }
        return parts.Count > 0 ? "  -  made for game " + string.Join(", ", parts) : "  -  excludes this version";
    }
}
