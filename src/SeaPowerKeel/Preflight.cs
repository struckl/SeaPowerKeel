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
        public bool Clean => ClassLoad.Count == 0 && Uses.Count == 0;
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
        r.ClassLoad.AddRange(classLoad);
        r.Uses.AddRange(uses.Except(classLoad));
        return r;
    }

    public void Dispose() => resolver.Dispose();
}
