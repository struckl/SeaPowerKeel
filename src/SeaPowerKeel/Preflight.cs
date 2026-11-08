using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SeaPowerKeel;

/// <summary>
/// Checks a mod DLL against the game that is running, before it is loaded: every type, method and field it uses from the
/// game's own assemblies must exist in this build. The same check as the workspace's compat-check tool, done in the game.
/// <para>Two kinds of findings. A <b>class-load</b> problem (a type of the mod derives from, implements or has a field of a
/// type this build lacks) breaks more than the mod: Unity's ECS start-up walks every type of every loaded assembly, throws
/// on such a type, and then no mission loads at all. Such a DLL is never loaded. A missing <b>method or field use</b> only
/// fails when that code runs, so the mod may well work (it can check the build itself first); that is a warning.</para>
/// </summary>
internal sealed class Preflight : IDisposable
{
    public sealed class Result
    {
        public readonly List<string> ClassLoad = new();
        public readonly List<string> Uses = new();
        /// <summary>Harmony patch targets (attributes, and AccessTools lookups with literal names) this build lacks.</summary>
        public readonly List<string> Patches = new();
        public bool Clean => ClassLoad.Count == 0 && Uses.Count == 0 && Patches.Count == 0;
    }

    private readonly HashSet<string> gameAssemblies;
    private readonly DefaultAssemblyResolver resolver = new();
    private readonly Dictionary<string, bool> exists = new();
    private readonly Dictionary<string, Result> byDll = new(StringComparer.OrdinalIgnoreCase);

    public Preflight(string managedDir, string bepInExCore)
    {
        // The game's assemblies = what is in Managed, minus the runtime itself (the same on every build).
        gameAssemblies = new HashSet<string>(
            Directory.GetFiles(managedDir, "*.dll").Select(Path.GetFileNameWithoutExtension)
                     .Where(n => !(n == "mscorlib" || n == "netstandard" || n == "System" || n.StartsWith("System.") || n.StartsWith("Mono."))),
            StringComparer.OrdinalIgnoreCase);
        resolver.AddSearchDirectory(managedDir);
        resolver.AddSearchDirectory(bepInExCore);
    }

    public Result Check(string dll)
    {
        if (byDll.TryGetValue(dll, out Result cached))
            return cached;
        var r = new Result();
        byDll[dll] = r;
        using var stream = new MemoryStream(File.ReadAllBytes(dll));
        using ModuleDefinition mod = ModuleDefinition.ReadModule(stream, new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false });
        var classLoad = new SortedSet<string>();
        var uses = new SortedSet<string>();

        bool Game(TypeReference t) =>
            t?.GetElementType().Scope is AssemblyNameReference a && gameAssemblies.Contains(a.Name);

        bool Exists(MemberReference m)
        {
            string key = m.FullName;
            if (!exists.TryGetValue(key, out bool ok))
            {
                try
                {
                    ok = m switch
                    {
                        TypeReference t => t.Resolve() != null,
                        MethodReference x => x.Resolve() != null,
                        FieldReference f => f.Resolve() != null,
                        _ => true,
                    };
                }
                catch (Exception)
                {
                    ok = false;
                }
                exists[key] = ok;
            }
            return ok;
        }

        void Type(SortedSet<string> into, TypeReference t)
        {
            switch (t)
            {
                case null:
                case GenericParameter:
                    return;
                case GenericInstanceType g:
                    Type(into, g.ElementType);
                    foreach (TypeReference a in g.GenericArguments)
                        Type(into, a);
                    return;
                case TypeSpecification s:
                    Type(into, s.ElementType);
                    return;
            }
            if (Game(t) && !Exists(t))
                into.Add(t.FullName);
        }

        foreach (TypeDefinition type in mod.GetTypes())
        {
            Type(classLoad, type.BaseType);
            foreach (InterfaceImplementation i in type.Interfaces)
                Type(classLoad, i.InterfaceType);
            foreach (FieldDefinition f in type.Fields)
                Type(classLoad, f.FieldType);
            foreach (MethodDefinition method in type.Methods)
            {
                Type(uses, method.ReturnType);
                foreach (ParameterDefinition p in method.Parameters)
                    Type(uses, p.ParameterType);
                if (!method.HasBody)
                    continue;
                foreach (VariableDefinition v in method.Body.Variables)
                    Type(uses, v.VariableType);
                foreach (Instruction ins in method.Body.Instructions)
                {
                    switch (ins.Operand)
                    {
                        case MethodReference m:
                            MethodReference element = m is GenericInstanceMethod gm ? gm.ElementMethod : m;
                            Type(uses, element.DeclaringType);
                            if (Game(element.DeclaringType) && !Exists(element))
                                uses.Add($"{element.DeclaringType.GetElementType().FullName}::{element.Name}({string.Join(", ", element.Parameters.Select(p => p.ParameterType.Name))})");
                            break;
                        case FieldReference f:
                            Type(uses, f.DeclaringType);
                            if (Game(f.DeclaringType) && !Exists(f))
                                uses.Add($"{f.DeclaringType.GetElementType().FullName}::{f.Name}");
                            break;
                        case TypeReference t:
                            Type(uses, t);
                            break;
                    }
                }
            }
        }
        var patches = new SortedSet<string>();
        try
        {
            CheckPatches(mod, patches, Game);
        }
        catch (Exception)
        {
            // The patch check is an extra; whatever it cannot read stays unchecked, as before it existed.
        }
        r.ClassLoad.AddRange(classLoad);
        r.Uses.AddRange(uses.Except(classLoad));
        r.Patches.AddRange(patches);
        return r;
    }

    // ---- Harmony patch targets -------------------------------------------------------------------------------------------
    // The IL scan above only sees what the code references. A Harmony patch names its target as a string, so a method that was
    // renamed or removed in this build passes it and fails when the patch is applied. Checked here, from the attributes
    // ([HarmonyPatch(typeof(X), "Name", ...)], class and method level merged the way Harmony does) and from AccessTools lookups
    // with a type and a literal name. Not checked: targets built at run time, TargetMethod(), "Type:Method" strings, and whether
    // a transpiler's code still matches the target's IL.

    private sealed class PatchSpec
    {
        public TypeReference Type;
        public string Name;
        public List<TypeReference> Args;
        public int Kind;   // HarmonyLib.MethodType: 0 Normal, 1 Getter, 2 Setter, 3 Constructor, 4 StaticConstructor, 5 Enumerator
        public bool Any => Type != null || Name != null || Args != null || Kind != 0;

        public PatchSpec Over(PatchSpec outer) => new()
        {
            Type = Type ?? outer.Type,
            Name = Name ?? outer.Name,
            Args = Args ?? outer.Args,
            Kind = Kind != 0 ? Kind : outer.Kind,
        };
    }

    private static readonly HashSet<string> PatchMethodAttributes = new() { "HarmonyPrefix", "HarmonyPostfix", "HarmonyTranspiler", "HarmonyFinalizer", "HarmonyReversePatch" };
    private static readonly HashSet<string> PatchMethodNames = new() { "Prefix", "Postfix", "Transpiler", "Finalizer" };

    private static PatchSpec ReadSpec(IEnumerable<CustomAttribute> attributes)
    {
        PatchSpec spec = null;
        foreach (CustomAttribute a in attributes.Where(x => x.AttributeType.Name == "HarmonyPatch" && x.AttributeType.Namespace == "HarmonyLib"))
        {
            spec ??= new PatchSpec();
            foreach (CustomAttributeArgument arg in a.ConstructorArguments)
            {
                switch (arg.Value)
                {
                    case TypeReference t:
                        spec.Type = t;
                        break;
                    case string s:
                        spec.Name = s;
                        break;
                    case CustomAttributeArgument[] list when arg.Type.GetElementType().Name == "Type":
                        spec.Args = list.Select(x => x.Value as TypeReference).ToList();
                        break;
                    case int kind when arg.Type.Name == "MethodType":
                        spec.Kind = kind;
                        break;
                }
            }
        }
        return spec;
    }

    private void CheckPatches(ModuleDefinition mod, SortedSet<string> into, Func<TypeReference, bool> game)
    {
        foreach (TypeDefinition type in mod.GetTypes())
        {
            PatchSpec onClass = ReadSpec(type.CustomAttributes);
            foreach (MethodDefinition method in type.Methods)
            {
                PatchSpec onMethod = ReadSpec(method.CustomAttributes);
                bool isPatch = onMethod != null
                    || (onClass != null && (PatchMethodNames.Contains(method.Name) || method.CustomAttributes.Any(a => PatchMethodAttributes.Contains(a.AttributeType.Name))));
                if (!isPatch)
                    continue;
                PatchSpec spec = (onMethod ?? new PatchSpec()).Over(onClass ?? new PatchSpec());
                if (spec.Type == null || !game(spec.Type))
                    continue;
                string problem = MissingTarget(spec);
                if (problem != null)
                    into.Add(problem);
            }
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                CheckLookups(method, into, game);
        }
    }

    private string MissingTarget(PatchSpec spec)
    {
        TypeDefinition target;
        try
        {
            target = spec.Type.GetElementType().Resolve();
        }
        catch (Exception)
        {
            target = null;
        }
        string typeName = spec.Type.GetElementType().FullName;
        if (target == null)
            return typeName;
        if (spec.Kind == 4)
            return target.Methods.Any(m => m.Name == ".cctor") ? null : $"{typeName}::.cctor";
        string name = spec.Kind switch
        {
            1 => "get_" + spec.Name,
            2 => "set_" + spec.Name,
            3 => ".ctor",
            _ => spec.Name,
        };
        if (name == null)
            return null;   // no method named (TargetMethod() or an incomplete attribute): nothing to compare with this build
        IEnumerable<MethodDefinition> candidates = target.Methods.Where(m => m.Name == name);
        if (spec.Args != null && spec.Kind != 5)
        {
            var wanted = spec.Args.Select(a => a?.FullName).ToList();
            candidates = candidates.Where(m => m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(wanted));
        }
        if (candidates.Any())
            return null;
        string args = spec.Args != null ? string.Join(", ", spec.Args.Select(a => a?.Name)) : "";
        return $"{typeName}::{name}({args})";
    }

    // AccessTools.Method / DeclaredMethod / Field / DeclaredField / PropertyGetter / PropertySetter (typeof(X), "Name", ...).
    private void CheckLookups(MethodDefinition method, SortedSet<string> into, Func<TypeReference, bool> game)
    {
        var instructions = method.Body.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (!(instructions[i].Operand is MethodReference call) || call.DeclaringType.FullName != "HarmonyLib.AccessTools")
                continue;
            string kind = call.Name;
            bool declaredOnly = kind.StartsWith("Declared");
            string shape = kind.StartsWith("Declared") ? kind.Substring("Declared".Length) : kind;
            if (shape != "Method" && shape != "Field" && shape != "PropertyGetter" && shape != "PropertySetter")
                continue;
            if (call.Parameters.Count == 0 || call.Parameters[0].ParameterType.FullName != "System.Type")
                continue;
            string name = null;
            TypeReference type = null;
            // Only the literal form counts: ldtoken X; call Type.GetTypeFromHandle; ldstr "name" (then the other arguments).
            // A Type held in a variable is not known here, so it is left alone rather than guessed from an earlier typeof.
            for (int j = i - 1; j >= 2 && j >= i - 8; j--)
            {
                if (instructions[j].OpCode.Code != Code.Ldstr)
                    continue;
                if (instructions[j - 1].Operand is MethodReference g && g.Name == "GetTypeFromHandle"
                    && instructions[j - 2].OpCode.Code == Code.Ldtoken && instructions[j - 2].Operand is TypeReference t)
                {
                    name = (string)instructions[j].Operand;
                    type = t;
                }
                break;
            }
            if (name == null || type == null || !game(type))
                continue;
            TypeDefinition def;
            try
            {
                def = type.GetElementType().Resolve();
            }
            catch (Exception)
            {
                def = null;
            }
            string typeName = type.GetElementType().FullName;
            if (def == null)
            {
                into.Add(typeName);
                continue;
            }
            string member = shape == "PropertyGetter" ? "get_" + name : shape == "PropertySetter" ? "set_" + name : name;
            bool found = false;
            for (TypeDefinition t = def; t != null && !found; t = declaredOnly ? null : Base(t))
                found = shape == "Field" ? t.Fields.Any(f => f.Name == name) : t.Methods.Any(m => m.Name == member);
            if (!found)
                into.Add($"{typeName}::{member}");
        }
    }

    private static TypeDefinition Base(TypeDefinition t)
    {
        try
        {
            return t.BaseType?.Resolve();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => resolver.Dispose();
}
