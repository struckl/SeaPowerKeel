using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SeaPowerKeel;

/// <summary>
/// A Restart button in the game's own "reload" dialog (Mods menu, Apply). The game only reloads its data there: a code mod switched on
/// or off does not start or stop until the game is started again. The dialog's message box has a middle button the game leaves unused;
/// Keel fills it, and marks the game's own Reload so Keel follows the new mod list after it (KeelHost.HotLoad). Restart saves the mod list the way the game's own button does, then quits and starts the game again through
/// KeelWatcher. The dialog is the same on both game builds (0.8.7 and the 3D globe build); everything is looked up by name, so a build
/// without it only loses the button.
/// </summary>
internal static class ModMenuRestart
{
    // The box's top bar names Keel ("MESSAGE · R.A.T.S. Keel", his rule 2026-11-12), so the text does not say it again.
    internal const string Note = "\n\nReload also starts code mods you switched on and takes switched-off ones out as far as the running game allows. " +
                                 "Restart is the clean way: it saves the mod list, closes the game and starts it again.";

    /// <summary>Set when the player clicks the game's Reload: Keel then follows the new mod list once the menu is back.</summary>
    internal static bool ReloadRequested;

    /// <summary>Set by Keel: quits the game and starts it again.</summary>
    internal static Action Restart;

    private static Type commandType;

    /// <summary>Hooks the Mods menu. Null when done, else why not.</summary>
    public static string Install()
    {
        try
        {
            Type menu = AccessTools.TypeByName("SeapowerUI.ViewModels.ModMenuViewModel");
            commandType = AccessTools.TypeByName("SeaPower.DelegateCommand");
            if (menu == null || commandType == null)
                return "the game's Mods menu is not in this build";
            ConstructorInfo ctor = AccessTools.Constructor(menu, Type.EmptyTypes);
            if (ctor == null)
                return "the Mods menu has no plain constructor in this build";
            new Harmony("dev.sewerlabs.seapowerkeel.modmenu").Patch(ctor, postfix: new HarmonyMethod(typeof(ModMenuRestart).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void Postfix(object __instance)
    {
        try
        {
            object box = FindDialog(__instance);
            if (box == null || Traverse.Create(box).Property("MiddleCommand").GetValue() != null)
                return;     // not found, or another mod already uses the middle button
            Action<object> run = _ => RestartFrom(__instance);
            object command = Activator.CreateInstance(commandType, run);
            Traverse t = Traverse.Create(box);
            t.Property("MiddleCommand").SetValue(command);
            t.Property("MiddleCommandLabel").SetValue("Restart");
            t.Property("AcknowledgeCommandLabel").SetValue("Reload");
            if (t.Property("AcknowledgeCommand").GetValue() is System.Windows.Input.ICommand reload)
            {
                Action<object> then = x => { ReloadRequested = true; reload.Execute(x); };
                t.Property("AcknowledgeCommand").SetValue(Activator.CreateInstance(commandType, then));
            }
            string message = t.Property("Message").GetValue() as string ?? "";
            if (!message.Contains(Note.Trim()))
                t.Property("Message").SetValue(message + Note);
            t.Property("Header").SetValue(CompatDialog.Branded(t.Property("Header").GetValue() as string));
        }
        catch (Exception ex)
        {
            BepInEx.Logging.Logger.CreateLogSource(Keel.Name).LogWarning($"No Restart button in the Mods menu: {ex.Message}");
        }
    }

    // The dialog is a local of the constructor, kept alive by the closure behind WriteLoadOrderCommand.
    private static object FindDialog(object menu)
    {
        object command = Traverse.Create(menu).Property("WriteLoadOrderCommand").GetValue();
        if (command == null)
            return null;
        object closure = (Traverse.Create(command).Field("_execute").GetValue() as Delegate)?.Target;
        if (closure == null)
            return null;
        FieldInfo field = closure.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.FieldType.Name == "MessageBoxViewModel");
        return field?.GetValue(closure);
    }

    private static void RestartFrom(object menu)
    {
        try
        {
            // What the game's own button saves first: the mod list, so the next start reads it.
            object directories = Traverse.Create(menu).Property("Directories").GetValue();
            Type manager = AccessTools.TypeByName("SeaPower.FileManager");
            Type singleton = AccessTools.TypeByName("SeaPower.Singleton`1")?.MakeGenericType(manager);
            object files = singleton?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (files == null || directories == null)
                throw new InvalidOperationException("the game's file manager was not found");
            AccessTools.Method(manager, "SaveDirectories").Invoke(files, new[] { directories });
        }
        catch (Exception ex)
        {
            // A restart now would lose the change: the player still has the game's own Reload.
            BepInEx.Logging.Logger.CreateLogSource(Keel.Name).LogWarning($"Restart from the Mods menu: the mod list could not be saved ({ex.InnerException?.Message ?? ex.Message}), so the game keeps running. Use Reload only, then restart the game yourself.");
            return;
        }
        Restart?.Invoke();
    }
}
