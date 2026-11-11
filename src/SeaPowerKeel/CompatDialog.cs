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
            header ??= Text("LoadScreen", "WarningMods");
            if (string.IsNullOrEmpty(header) || value == null || !value.StartsWith(header, StringComparison.Ordinal))
                return;
            string rewritten = Rewrite(header);
            if (rewritten == null)
                return;
            value = rewritten;
            box = __instance;
            BepInEx.Logging.Logger.CreateLogSource(Keel.Name).LogInfo("The game's mod version dialog is shown with Keel's list.");
            Loading.LoadingWindow.WaitingFor = "the game asks about mods (in the game window)";
        }
        catch (Exception)
        {
            // the game's own text stays
        }
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
            sb.Append("\nCode mods  (R.A.T.S. Keel checks each one against this game version before it starts it,\nand leaves out one that would stop missions from loading)\n");
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
