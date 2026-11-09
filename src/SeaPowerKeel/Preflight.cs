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
        /// <summary>Types and methods named by string (Harmony patch targets, AccessTools and reflection lookups) that this build lacks.</summary>
        public readonly List<string> Lookups = new();
        /// <summary>Harmony patches whose parameters do not fit the game method: they read or write the wrong kind of memory, which can take the game down.</summary>
        public readonly List<string> Fatal = new();
        public bool Clean => ClassLoad.Count == 0 && Uses.Count == 0 && Lookups.Count == 0 && Fatal.Count == 0;
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
        this.managedDir = managedDir;
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
            CheckPatches(mod, patches, r.Fatal, Game);
        }
        catch (Exception)
        {
            // The patch check is an extra; whatever it cannot read stays unchecked, as before it existed.
        }
        r.ClassLoad.AddRange(classLoad);
        r.Uses.AddRange(uses.Except(classLoad));
        r.Lookups.AddRange(patches);
        return r;
    }

    // ---- Targets named by string: Harmony patches and reflection lookups ------------------------------------------------
    // The IL scan above only sees what the code references. A Harmony patch or a reflection lookup names its target as a string,
    // so a method or type that this build renamed or removed passes it and fails when the code runs. Checked here:
    //  - [HarmonyPatch(typeof(X), "Name", ...)] (class and method level merged the way Harmony does, also ("Ns.X", "Name"));
    //  - AccessTools.Method/DeclaredMethod/Field/DeclaredField/PropertyGetter/PropertySetter with typeof(X) and a literal name,
    //    with "Ns.X:Name", or with a Type that came from a literal name (the usual way to reach an internal class);
    //  - AccessTools.TypeByName / Type.GetType / Assembly.GetType with a literal name.
    // Not checked: names built at run time, TargetMethod(), and whether a transpiler's code still matches the target's IL.

    private sealed class PatchSpec
    {
        public TypeReference Type;
        public string TypeName;
        public string Name;
        public List<TypeReference> Args;
        public int Kind;   // HarmonyLib.MethodType: 0 Normal, 1 Getter, 2 Setter, 3 Constructor, 4 StaticConstructor, 5 Enumerator

        public PatchSpec Over(PatchSpec outer) => new()
        {
            Type = Type ?? outer.Type,
            TypeName = TypeName ?? outer.TypeName,
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
            var strings = new List<string>();
            foreach (CustomAttributeArgument arg in a.ConstructorArguments)
            {
                switch (arg.Value)
                {
                    case TypeReference t:
                        spec.Type = t;
                        break;
                    case string s:
                        strings.Add(s);
                        break;
                    case CustomAttributeArgument[] list when arg.Type.GetElementType().Name == "Type":
                        spec.Args = list.Select(x => x.Value as TypeReference).ToList();
                        break;
                    case int kind when arg.Type.Name == "MethodType":
                        spec.Kind = kind;
                        break;
                }
            }
            if (strings.Count >= 2)
            {
                spec.TypeName = strings[0];
                spec.Name = strings[1];
            }
            else if (strings.Count == 1)
            {
                spec.Name = strings[0];
            }
        }
        return spec;
    }

    private void CheckPatches(ModuleDefinition mod, SortedSet<string> into, List<string> fatal, Func<TypeReference, bool> game)
    {
        var fieldTypes = new Dictionary<string, string>();   // static fields holding a Type found by a literal name
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
                string problem = MissingTarget(spec, game);
                if (problem != null)
                    into.Add(problem);
                else
                    CheckSignature(method, spec, game, fatal);
            }
        }
        // Twice: the first pass learns which static fields hold a Type found by name, the second reports.
        for (int pass = 0; pass < 2; pass++)
            foreach (TypeDefinition type in mod.GetTypes())
                foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                    CheckLookups(method, pass == 1 ? into : new SortedSet<string>(), game, fieldTypes);
    }

    // ---- Do a patch's parameters fit the method it patches? ----
    // Harmony passes values by position and name without comparing their types. A patch that takes an int where the game has a float,
    // or a struct where the game has an object, reads or writes the wrong memory, and that is a crash Keel cannot catch. Only sure cases
    // count: "object" takes anything, and an enum may be taken as its underlying number (the usual way to reach a private enum).

    private static TypeReference Plain(TypeReference t) => t is ByReferenceType r ? r.ElementType : t;

    private static TypeDefinition Definition(TypeReference t)
    {
        try
        {
            return t is GenericParameter || t.IsGenericInstance || t.IsArray ? null : t.Resolve();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Fits(TypeReference patch, TypeReference game)
    {
        patch = Plain(patch);
        game = Plain(game);
        if (patch.FullName == game.FullName || patch.FullName == "System.Object" || patch is GenericParameter || game is GenericParameter)
            return true;
        TypeDefinition a = Definition(patch), b = Definition(game);
        if (a == null || b == null || !(a.IsValueType || b.IsValueType))
            return true;   // references to objects (the runtime checks those), or something Keel cannot judge
        if (a.IsEnum != b.IsEnum && (a.IsEnum ? b : a).IsPrimitive)
            return true;
        return false;
    }

    private void CheckSignature(MethodDefinition patch, PatchSpec spec, Func<TypeReference, bool> game, List<string> fatal)
    {
        if (!patch.HasParameters || spec.Kind == 4 || spec.Kind == 5)
            return;
        TypeDefinition type = spec.Type != null ? (game(spec.Type) ? ResolveType(spec.Type) : null) : spec.TypeName != null ? FindType(spec.TypeName) : null;
        string name = spec.Kind switch { 1 => "get_" + spec.Name, 2 => "set_" + spec.Name, 3 => ".ctor", _ => spec.Name };
        if (type == null || name == null)
            return;
        List<MethodDefinition> found = type.Methods.Where(m => m.Name == name).ToList();
        if (spec.Args != null)
        {
            var wanted = spec.Args.Select(a => a?.FullName).ToList();
            found = found.Where(m => m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(wanted)).ToList();
        }
        if (found.Count != 1)
            return;   // Harmony's choice among overloads is not Keel's to guess
        MethodDefinition target = found[0];
        foreach (ParameterDefinition p in patch.Parameters)
        {
            TypeReference other = null;
            if (p.Name == "__result")
                other = target.ReturnType.FullName == "System.Void" ? null : target.ReturnType;
            else if (p.Name.StartsWith("___"))
            {
                string field = p.Name.Substring(3);
                for (TypeDefinition t = type; t != null && other == null; t = Base(t))
                    other = t.Fields.FirstOrDefault(f => f.Name == field)?.FieldType;
            }
            else if (p.Name.StartsWith("__") && int.TryParse(p.Name.Substring(2), out int index))
                other = index >= 0 && index < target.Parameters.Count ? target.Parameters[index].ParameterType : null;
            else if (!p.Name.StartsWith("__"))
                other = target.Parameters.FirstOrDefault(x => x.Name == p.Name)?.ParameterType;
            if (other != null && !Fits(p.ParameterType, other))
                fatal.Add($"{patch.DeclaringType.Name}.{patch.Name} takes '{p.Name}' as {Plain(p.ParameterType).Name}, but {type.Name}.{name} has {Plain(other).Name}");
        }
    }

    private string MissingTarget(PatchSpec spec, Func<TypeReference, bool> game)
    {
        TypeDefinition target;
        string typeName;
        if (spec.Type != null)
        {
            if (!game(spec.Type))
                return null;
            typeName = spec.Type.GetElementType().FullName;
            target = ResolveType(spec.Type);
            if (target == null)
                return typeName;
        }
        else if (spec.TypeName != null)
        {
            typeName = spec.TypeName;
            target = FindType(typeName);
            if (target == null)
                return IsGameName(typeName) ? typeName : null;
        }
        else
        {
            return null;
        }
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

    private static TypeDefinition ResolveType(TypeReference type)
    {
        try
        {
            return type.GetElementType().Resolve();
        }
        catch (Exception)
        {
            return null;
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

    // ---- Index of the game's type names, built the first time a mod looks a type up by name ----------------------------

    private Dictionary<string, TypeDefinition> typeIndex;
    private HashSet<string> gameRoots;
    private readonly List<ModuleDefinition> indexed = new();
    private readonly string managedDir;
    /// <summary>Milliseconds spent building the type index (0 until a mod needed it).</summary>
    public long IndexMs { get; private set; }

    private void EnsureIndex()
    {
        if (typeIndex != null)
            return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        typeIndex = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        gameRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in gameAssemblies)
        {
            try
            {
                ModuleDefinition m = ModuleDefinition.ReadModule(Path.Combine(managedDir, name + ".dll"),
                    new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Deferred });
                indexed.Add(m);
                foreach (TypeDefinition t in m.GetTypes())
                {
                    typeIndex.TryAdd(t.FullName.Replace('/', '+'), t);
                    if (t.Namespace.Length > 0)
                        gameRoots.Add(t.Namespace.Split('.')[0]);
                }
            }
            catch (Exception)
            {
                // A file Cecil cannot read just stays out of the index.
            }
        }
        gameRoots.ExceptWith(new[] { "System", "Microsoft", "Mono" });
        IndexMs = watch.ElapsedMilliseconds;
    }

    /// <summary>A type of the game by its full name ("Ns.Type", nested "Ns.Outer+Inner"; ", Assembly" is ignored).</summary>
    private TypeDefinition FindType(string name)
    {
        EnsureIndex();
        int comma = name.IndexOf(',');
        if (comma >= 0)
            name = name.Substring(0, comma);
        return typeIndex.TryGetValue(name.Trim(), out TypeDefinition t) ? t : null;
    }

    /// <summary>Does this name look like one of the game's own namespaces? (Names outside them are not ours to judge.)</summary>
    private bool IsGameName(string name)
    {
        EnsureIndex();
        int comma = name.IndexOf(',');
        if (comma >= 0)
            name = name.Substring(0, comma);
        int dot = name.IndexOf('.');
        return dot > 0 && gameRoots.Contains(name.Substring(0, dot));
    }

    // The literal name a call takes a type by: AccessTools.TypeByName, Type.GetType, Assembly.GetType.
    private static string TypeNameCall(MethodReference call, Mono.Collections.Generic.Collection<Instruction> ins, int i)
    {
        bool byName = (call.DeclaringType.FullName == "HarmonyLib.AccessTools" && call.Name == "TypeByName")
                   || (call.DeclaringType.FullName == "System.Type" && call.Name == "GetType")
                   || (call.DeclaringType.FullName == "System.Reflection.Assembly" && call.Name == "GetType");
        if (!byName || call.Parameters.Count == 0 || call.Parameters[0].ParameterType.FullName != "System.String")
            return null;
        for (int j = i - 1; j >= 0 && j >= i - 3; j--)
            if (ins[j].OpCode.Code == Code.Ldstr)
                return (string)ins[j].Operand;
        return null;
    }

    // The literal name of the type the instruction at k leaves on the stack, when that is a by-name call (or its dup).
    private static string TypeNameBefore(Mono.Collections.Generic.Collection<Instruction> ins, int k)
    {
        if (k >= 1 && ins[k].OpCode.Code == Code.Dup)
            k--;
        return k >= 0 && ins[k].Operand is MethodReference m ? TypeNameCall(m, ins, k) : null;
    }

    // Cecil gives the variable as the operand of ldloc/ldloc.s/stloc/stloc.s but not of the short forms ldloc.0 .. stloc.3.
    private static VariableDefinition LocalOf(Instruction ins, MethodBody body)
    {
        switch (ins.OpCode.Code)
        {
            case Code.Ldloc_0:
            case Code.Stloc_0:
                return body.Variables.Count > 0 ? body.Variables[0] : null;
            case Code.Ldloc_1:
            case Code.Stloc_1:
                return body.Variables.Count > 1 ? body.Variables[1] : null;
            case Code.Ldloc_2:
            case Code.Stloc_2:
                return body.Variables.Count > 2 ? body.Variables[2] : null;
            case Code.Ldloc_3:
            case Code.Stloc_3:
                return body.Variables.Count > 3 ? body.Variables[3] : null;
            default:
                return ins.Operand as VariableDefinition;
        }
    }

    private static bool IsStloc(Instruction ins) =>
        ins.OpCode.Code is Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3;

    private static bool IsLdloc(Instruction ins) =>
        ins.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3;

    private void CheckLookups(MethodDefinition method, SortedSet<string> into, Func<TypeReference, bool> game, Dictionary<string, string> fieldTypes)
    {
        var ins = method.Body.Instructions;
        var locals = new Dictionary<VariableDefinition, string>();   // locals holding a Type found by a literal name
        for (int i = 0; i < ins.Count; i++)
        {
            if (!(ins[i].Operand is MethodReference call))
                continue;

            string byName = TypeNameCall(call, ins, i);
            if (byName != null)
            {
                if (FindType(byName) == null && IsGameName(byName))
                    into.Add(byName);
                if (i + 1 < ins.Count)
                {
                    Instruction next = ins[i + 1];
                    if (IsStloc(next) && LocalOf(next, method.Body) != null)
                        locals[LocalOf(next, method.Body)] = byName;
                    else if (next.OpCode.Code == Code.Stsfld && next.Operand is FieldReference f)
                        fieldTypes[f.FullName] = byName;
                }
                continue;
            }

            if (call.DeclaringType.FullName != "HarmonyLib.AccessTools")
                continue;
            bool declaredOnly = call.Name.StartsWith("Declared");
            string shape = declaredOnly ? call.Name.Substring("Declared".Length) : call.Name;
            if (shape != "Method" && shape != "Field" && shape != "PropertyGetter" && shape != "PropertySetter")
                continue;
            if (call.Parameters.Count == 0)
                continue;
            string first = call.Parameters[0].ParameterType.FullName;

            // The name and where the type comes from: the nearest literal string before the call.
            int s = -1;
            for (int j = i - 1; j >= 0 && j >= i - 8; j--)
                if (ins[j].OpCode.Code == Code.Ldstr)
                {
                    s = j;
                    break;
                }
            if (s < 0)
                continue;
            string literal = (string)ins[s].Operand;
            TypeDefinition def = null;
            string typeName = null, member = null;
            bool missingType = false;

            if (first == "System.String")
            {
                // AccessTools.Method("Ns.Type:Name")
                int colon = literal.IndexOf(':');
                if (colon <= 0)
                    continue;
                typeName = literal.Substring(0, colon);
                member = literal.Substring(colon + 1);
                def = FindType(typeName);
                missingType = def == null && IsGameName(typeName);
            }
            else if (first == "System.Type" && s >= 1)
            {
                member = literal;
                Instruction before = ins[s - 1];
                if (before.Operand is MethodReference g && g.Name == "GetTypeFromHandle" && s >= 2
                    && ins[s - 2].OpCode.Code == Code.Ldtoken && ins[s - 2].Operand is TypeReference t)
                {
                    if (!game(t))
                        continue;
                    typeName = t.GetElementType().FullName;
                    def = ResolveType(t);
                    missingType = def == null;
                }
                else if (IsLdloc(before) && LocalOf(before, method.Body) != null && locals.TryGetValue(LocalOf(before, method.Body), out string viaLocal))
                {
                    typeName = viaLocal;
                    def = FindType(viaLocal);
                    missingType = def == null && IsGameName(viaLocal);
                }
                else if (TypeNameBefore(ins, s - 1) is string viaCall)
                {
                    // AccessTools.Method(AccessTools.TypeByName("Ns.X"), "Name"), or the same with the type kept on the stack (dup)
                    typeName = viaCall;
                    def = FindType(viaCall);
                    missingType = def == null && IsGameName(viaCall);
                }
                else if (before.OpCode.Code == Code.Ldsfld && before.Operand is FieldReference sf && fieldTypes.TryGetValue(sf.FullName, out string viaField))
                {
                    typeName = viaField;
                    def = FindType(viaField);
                    missingType = def == null && IsGameName(viaField);
                }
                else
                {
                    continue;   // a Type that is not known here: left alone rather than guessed
                }
            }
            else
            {
                continue;
            }

            if (def == null)
            {
                if (missingType)
                    into.Add(typeName);
                continue;
            }
            string memberName = shape == "PropertyGetter" ? "get_" + member : shape == "PropertySetter" ? "set_" + member : member;
            bool found = false;
            for (TypeDefinition t2 = def; t2 != null && !found; t2 = declaredOnly ? null : Base(t2))
                found = shape == "Field" ? t2.Fields.Any(f2 => f2.Name == member) : t2.Methods.Any(m => m.Name == memberName);
            if (!found)
                into.Add($"{typeName}::{memberName}");
        }
    }

    public void Dispose()
    {
        foreach (ModuleDefinition m in indexed)
            m.Dispose();
        resolver.Dispose();
    }
}
