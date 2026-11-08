using System;
using System.Linq;
using System.Reflection;

namespace SeaPowerKeel;

/// <summary>
/// Calls an Anchor Chain mod's entry point the way Anchor Chain does: through the <c>IAnchorChainMod</c> interface, not by
/// looking for a public method of that name. A mod that implements <c>TriggerEntryPoint</c> explicitly (or inherits it,
/// or has it non-public) has no public method of that name, but the interface call reaches it all the same.
/// </summary>
internal static class AcEntry
{
    public const string InterfaceName = "AnchorChain.IAnchorChainMod";
    public const string MethodName = "TriggerEntryPoint";

    /// <summary>The mod's <c>IAnchorChainMod</c> (matched by name: the interface lives in Anchor Chain's DLL), or null.</summary>
    public static Type FindInterface(Type modType) =>
        modType.GetInterfaces().FirstOrDefault(i => i.FullName == InterfaceName);

    /// <summary>The interface's entry-point method, or null when the type has no such interface or method.</summary>
    public static MethodInfo FindEntryPoint(Type modType) =>
        FindInterface(modType)?.GetMethod(MethodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

    /// <summary>Calls the entry point. Throws <see cref="MissingMethodException"/> when it cannot be found; an exception the mod throws is passed on (not wrapped).</summary>
    public static void Invoke(object mod)
    {
        MethodInfo entry = FindEntryPoint(mod.GetType());
        if (entry == null)
            throw new MissingMethodException($"{mod.GetType().FullName} has no {InterfaceName}.{MethodName}(): the entry point cannot be found.");
        try
        {
            entry.Invoke(mod, null);
        }
        catch (TargetInvocationException t) when (t.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(t.InnerException).Throw();
        }
    }
}
