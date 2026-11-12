using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SeaPowerKeel;

/// <summary>
/// The end of the game's start-up load screen, for mods that wait for it.
/// <para>
/// Some mods add their menu entries when the load screen's coroutine (<c>SeaPower.LoadScreen.&lt;ExecuteLoadScreen&gt;d__N.MoveNext</c>)
/// returns false, with a Harmony postfix. Anchor Chain starts mods while the load screen runs, so they see that end. On the globe build
/// Keel starts mods after it on purpose (mods started during it can keep the unit data from loading), so such a postfix would wait
/// for an end that has passed: the Alternative mods' ALTERNATIVE MODS menu entry was missing on every start where nothing held the
/// load screen (0.9.1, 2026-11-12). Keel watches that end itself and, after its mods have started, calls once each postfix a mod
/// put on it too late, as the coroutine would have.
/// </para>
/// </summary>
internal static class LoadScreenEnd
{
    public const string HarmonyId = "dev.sewerlabs.seapowerkeel.loadscreenend";
    private static MethodInfo moveNext;

    /// <summary>True once the start-up load screen's coroutine has finished (its MoveNext returned false).</summary>
    public static volatile bool Ended;

    /// <summary>Hooks the coroutine's end. Null when done, else why not.</summary>
    public static string Install()
    {
        try
        {
            moveNext = Find();
            if (moveNext == null)
                return "the load screen coroutine is not in this build";
            new Harmony(HarmonyId).Patch(moveNext, postfix: new HarmonyMethod(typeof(LoadScreenEnd).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static MethodInfo Find()
    {
        Type screen = AccessTools.TypeByName("SeaPower.LoadScreen");
        Type routine = screen?.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                             .FirstOrDefault(t => t.Name.StartsWith("<ExecuteLoadScreen>", StringComparison.Ordinal));
        return routine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static void Postfix(bool __result)
    {
        if (!__result)
            Ended = true;
    }

    /// <summary>The Harmony owners with a postfix on the coroutine now (before the mods start).</summary>
    public static HashSet<string> Owners()
    {
        if (moveNext == null)
            return new HashSet<string>();
        Patches info = Harmony.GetPatchInfo(moveNext);
        return new HashSet<string>(info?.Postfixes.Select(p => p.owner) ?? Enumerable.Empty<string>());
    }

    /// <summary>
    /// After the mods started: when the load screen has already ended, calls each postfix that came in since <paramref name="before"/>
    /// with <c>__result = false</c>. Only static postfixes whose parameters Keel can fill (<c>__result</c>, <c>__instance</c> = null,
    /// <c>__state</c> = default); one that needs more is named and left. Returns one line per postfix for the log.
    /// </summary>
    public static List<string> Replay(HashSet<string> before)
    {
        var lines = new List<string>();
        if (moveNext == null || !Ended)
            return lines;
        Patches info = Harmony.GetPatchInfo(moveNext);
        foreach (Patch patch in info?.Postfixes ?? Enumerable.Empty<Patch>())
        {
            if (patch.owner == HarmonyId || before.Contains(patch.owner))
                continue;
            MethodInfo m = patch.PatchMethod;
            if (!m.IsStatic)
            {
                lines.Add($"{patch.owner}: {m.DeclaringType?.Name}.{m.Name} waits for the end of the load screen, which had passed; not called (not static).");
                continue;
            }
            object[] args = Args(m.GetParameters());
            if (args == null)
            {
                lines.Add($"{patch.owner}: {m.DeclaringType?.Name}.{m.Name} waits for the end of the load screen, which had passed; not called (needs more than __result).");
                continue;
            }
            try
            {
                m.Invoke(null, args);
                lines.Add($"{patch.owner}: the load screen had ended before it started; Keel called its {m.DeclaringType?.Name}.{m.Name} once.");
            }
            catch (Exception ex)
            {
                Exception e = ex is TargetInvocationException { InnerException: not null } t ? t.InnerException : ex;
                lines.Add($"{patch.owner}: calling {m.DeclaringType?.Name}.{m.Name} for the passed load screen failed: {e.GetType().Name}: {e.Message}");
            }
        }
        return lines;
    }

    /// <summary>The arguments for a postfix of the coroutine's MoveNext, or null when a parameter cannot be filled.</summary>
    internal static object[] Args(ParameterInfo[] ps)
    {
        var args = new object[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            Type t = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType() : ps[i].ParameterType;
            switch (ps[i].Name)
            {
                case "__result" when t == typeof(bool):
                    args[i] = false;
                    break;
                case "__instance":
                case "__state":
                case "__originalMethod" when t.IsAssignableFrom(typeof(MethodBase)):
                    args[i] = ps[i].Name == "__originalMethod" ? moveNext : t.IsValueType ? Activator.CreateInstance(t) : null;
                    break;
                default:
                    return null;
            }
        }
        return args;
    }
}
