using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SeaPowerKeel;

/// <summary>
/// The game's <c>SeaPower.IniHandler</c>, bound by name and signature so one Keel DLL serves every game build (Keel
/// references no game assembly). What Anchor Chain's <c>[ACConfig]</c> handling needs from it is looked up once; whatever
/// this build lacks is listed in <see cref="Missing"/> and the caller degrades with a log line instead of failing.
/// Checked against 0.8.7, the globe build 26223125 and 261107 (<c>offline-check --inicheck</c>).
/// </summary>
internal sealed class IniApi
{
    public readonly Type Type;
    public readonly List<string> Missing = new();
    private readonly ConstructorInfo ctor;
    private readonly MethodInfo open, save, hasSection, hasKey, addSection, writeText, writeBool, readBool, readText, sectionValues;
    private readonly MemberInfo data;

    public IniApi(Type iniType)
    {
        Type = iniType;
        const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
        MethodInfo[] all = iniType.GetMethods(pub);
        ctor = iniType.GetConstructor(Type.EmptyTypes);
        // open(path, ...): the game added optional parameters between builds (five on 0.8.7, six on the globe builds);
        // take the overload whose parameters after the path are all optional and call it with its own defaults.
        open = all.Where(m => m.Name == "open" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(string)
                              && m.GetParameters().Skip(1).All(p => p.IsOptional))
                  .OrderBy(m => m.GetParameters().Length).FirstOrDefault();
        save = Find(all, "saveFile", typeof(bool));
        hasSection = Find(all, "doesSectionExist", typeof(string));
        hasKey = Find(all, "doesKeyExist", typeof(string), typeof(string));
        addSection = Find(all, "AddSection", typeof(string), typeof(Dictionary<string, string>));
        writeText = Find(all, "writeValue", typeof(string), typeof(string), typeof(string));
        writeBool = Find(all, "writeValue", typeof(string), typeof(string), typeof(bool));
        readBool = Find(all, "readValue", typeof(string), typeof(string), typeof(bool));
        readText = Find(all, "readValue", typeof(string), typeof(string), typeof(string));
        sectionValues = Find(all, "GetSectionKeyValues", typeof(string));
        data = (MemberInfo)iniType.GetProperty("Data", pub) ?? iniType.GetField("Data", pub);

        void Need(object member, string name)
        {
            if (member == null)
                Missing.Add(name);
        }
        Need(ctor, "IniHandler()");
        Need(open, "open(path, ...)");
        Need(save, "saveFile(bool)");
        Need(hasSection, "doesSectionExist(string)");
        Need(hasKey, "doesKeyExist(string, string)");
        Need(addSection, "AddSection(string, Dictionary)");
        Need(writeText, "writeValue(string, string, string)");
        Need(data, "Data");
        Need(sectionValues, "GetSectionKeyValues(string)");
        Need(readBool, "readValue(string, string, bool)");
        Need(readText, "readValue(string, string, string)");
        // writeValue(bool) is only a convenience: the game writes "True"/"False" through the string overload.
        if (writeBool == null && writeText != null)
            Missing.Add("writeValue(string, string, bool) (the string overload is used instead)");
    }

    /// <summary>The <c>open</c> overload that is called, for the offline check's printout.</summary>
    public string OpenSignature => open == null ? "(none)" : "open(" + string.Join(", ", open.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";

    /// <summary>Everything the merge of user and default config needs.</summary>
    public bool CanMerge => ctor != null && open != null && save != null && hasSection != null && hasKey != null && addSection != null && writeText != null && data != null;

    /// <summary>What the "AnchorChain.ResetValues" step needs on top of the merge.</summary>
    public bool CanReset => CanMerge && sectionValues != null && readBool != null && readText != null;

    private static MethodInfo Find(MethodInfo[] all, string name, params Type[] ps) =>
        all.FirstOrDefault(m => m.Name == name && !m.IsGenericMethodDefinition
                                && m.GetParameters().Select(p => p.ParameterType).Take(ps.Length).SequenceEqual(ps)
                                && m.GetParameters().Length >= ps.Length
                                && m.GetParameters().Skip(ps.Length).All(p => p.IsOptional));

    public object New() => ctor.Invoke(null);

    public void Open(object ini, string path) => Call(open, ini, path);
    public void Save(object ini) => Call(save, ini, true);
    public bool HasSection(object ini, string s) => (bool)Call(hasSection, ini, s);
    public bool HasKey(object ini, string s, string k) => (bool)Call(hasKey, ini, s, k);
    public void AddSection(object ini, string s) => Call(addSection, ini, s, new Dictionary<string, string>());
    public bool ReadBool(object ini, string s, string k, bool d) => (bool)Call(readBool, ini, s, k, d);
    public string ReadText(object ini, string s, string k, string d) => (string)Call(readText, ini, s, k, d);
    public void WriteText(object ini, string s, string k, string v) => Call(writeText, ini, s, k, v);

    public void WriteBool(object ini, string s, string k, bool v)
    {
        if (writeBool != null)
            Call(writeBool, ini, s, k, v);
        else
            WriteText(ini, s, k, v ? "True" : "False");
    }

    public IDictionary<string, string> SectionValues(object ini, string s) => Call(sectionValues, ini, s) as IDictionary<string, string>;

    /// <summary>Section name, its keys and a copy of its values (the game's <c>Data</c> is its live cache, which writes change).</summary>
    public List<(string section, List<string> keys, IDictionary<string, string> values)> Sections(object ini)
    {
        object d = data is PropertyInfo p ? p.GetValue(ini) : ((FieldInfo)data).GetValue(ini);
        var list = new List<(string, List<string>, IDictionary<string, string>)>();
        if (d is not IEnumerable rows)
            return list;   // the globe builds return null when the file has no cached data: nothing to merge
        foreach (object row in rows)
        {
            Type rt = row.GetType();
            string section = rt.GetProperty("Key")?.GetValue(row) as string;
            var values = rt.GetProperty("Value")?.GetValue(row) as IDictionary<string, string>;
            if (section != null && values != null)
                list.Add((section, values.Keys.ToList(), new Dictionary<string, string>(values)));
        }
        return list;
    }

    private static object Call(MethodInfo m, object target, params object[] args)
    {
        // optional parameters after the ones given (a game build with an extra defaulted parameter)
        ParameterInfo[] ps = m.GetParameters();
        if (args.Length < ps.Length)
            args = args.Concat(ps.Skip(args.Length).Select(p => p.DefaultValue)).ToArray();
        try
        {
            return m.Invoke(target, args);
        }
        catch (TargetInvocationException t) when (t.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(t.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>
/// <c>[ACConfig]</c>, as Anchor Chain does it (its <c>LoadConfigs</c>): a mod with <c>[ACConfig]</c> ships a default config
/// <c>&lt;guid&gt;.ini</c> in its mod folder; the player's copy <c>&lt;guid&gt;_user.ini</c> lives in
/// <c>StreamingAssets\ACConfigs</c> and is made from the default on first use. Sections and keys the default has and the
/// user copy lacks are added to it; then the default's <c>[AnchorChain.ResetValues]</c> (once, remembered in
/// <c>[AnchorChain.State] hasReset</c>) overwrites chosen user values; both files are saved. A missing default config keeps
/// the mod from starting; a user config that cannot be made does so only for <c>[ACConfig(required: true)]</c>.
/// </summary>
internal sealed class AcConfig
{
    public const string ConfigFolder = "ACConfigs";
    private static readonly HashSet<string> ReservedSections = new() { "AnchorChain.State", "AnchorChain.ResetValues" };

    private readonly IniApi ini;
    private readonly List<string> searchDirs;
    private readonly Action<string> info;
    private readonly Action<string> warn;
    private readonly Action<string> error;
    public readonly string ConfigDir;
    private bool noted;

    /// <param name="iniType">The game's IniHandler type, or null when it cannot be found (the files are then only copied).</param>
    /// <param name="searchDirs">Where <c>&lt;guid&gt;.ini</c> / <c>&lt;guid&gt;_user.ini</c> are looked for: mod folders in order (top level only, as Anchor Chain does).</param>
    public AcConfig(Type iniType, string configDir, IEnumerable<string> searchDirs, Action<string> info, Action<string> warn, Action<string> error)
    {
        ini = iniType == null ? null : new IniApi(iniType);
        ConfigDir = configDir;
        this.searchDirs = searchDirs.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!this.searchDirs.Contains(configDir, StringComparer.OrdinalIgnoreCase))
            this.searchDirs.Add(configDir);   // Anchor Chain adds it too (only when it creates the folder; the game lists an existing one as a mod folder)
        this.info = info;
        this.warn = warn;
        this.error = error;
    }

    /// <summary>What this game build lacks of IniHandler (empty = everything Keel needs is there).</summary>
    public IReadOnlyList<string> MissingMembers => ini == null ? new[] { "SeaPower.IniHandler (type not found)" } : ini.Missing;

    private string FindFile(string name)
    {
        foreach (string dir in searchDirs)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>Prepares the config files of one mod. False (with <paramref name="reason"/>) = the mod must not start.</summary>
    public bool Load(PluginEntry p, out string reason)
    {
        reason = null;
        string label = $"{p.Display} ({p.Guid})";
        string userPath = FindFile(p.Guid + "_user.ini");
        string defaultPath = FindFile(p.Guid + ".ini");
        if (defaultPath == null)
        {
            reason = $"Its [ACConfig] needs a default config file {p.Guid}.ini in a mod folder, and there is none (Anchor Chain does not start such a mod either).";
            warn($"Required reference config file missing: {label}");
            return false;
        }
        if (userPath == null)
        {
            userPath = Path.Combine(ConfigDir, p.Guid + "_user.ini");
            info($"Writing local config file: {label}");
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.Copy(defaultPath, userPath, true);
            }
            catch (Exception ex)
            {
                error($"Failed to create local config file: {label} | {ex.Message}");
                if (p.AcConfigRequired)
                {
                    reason = $"Its [ACConfig] is required and the player's config file {Path.GetFileName(userPath)} could not be created: {ex.Message}";
                    return false;
                }
                info($"Failed to load config file: {label}");
                return true;
            }
        }
        if (ini == null || !ini.CanMerge)
        {
            NoteMissing();
            p.Warnings.Add("Config files were prepared (default copied to the player's file when missing), but not merged with newer default keys: this game build's IniHandler lacks " +
                           string.Join(", ", MissingMembers) + ".");
            return true;
        }
        try
        {
            Merge(p, label, userPath, defaultPath);
        }
        catch (Exception ex)
        {
            // The files exist and the mod reads them itself; a failed merge must not take the mod down.
            warn($"Could not merge the config of {label}: {ex.Message}");
            p.Warnings.Add($"Config merge failed ({ex.GetType().Name}: {ex.Message}); the mod starts with the files as they are.");
        }
        return true;
    }

    private void NoteMissing()
    {
        if (noted)
            return;
        noted = true;
        warn("[ACConfig] This game build's IniHandler lacks: " + string.Join(", ", MissingMembers) +
             ". Keel only copies default configs to the player's file; it does not merge new keys or apply reset values.");
    }

    private void Merge(PluginEntry p, string label, string userPath, string defaultPath)
    {
        object user = ini.New();
        object def = ini.New();
        ini.Open(user, userPath);
        ini.Open(def, defaultPath);

        foreach ((string section, List<string> keys, IDictionary<string, string> values) in ini.Sections(def))
        {
            if (ReservedSections.Contains(section))
                continue;
            if (!ini.HasSection(user, section))
            {
                ini.AddSection(user, section);
                info($"Plugin config missing section: {label}");
            }
            foreach (string key in keys)
            {
                if (!ini.HasKey(user, section, key))
                {
                    ini.WriteText(user, section, key, values[key]);
                    info($"Plugin config missing key: {label}");
                }
            }
        }

        // [AnchorChain.State] hasReset / [AnchorChain.ResetValues]: "section = key1,key2" copies the default's values over the player's, once.
        if (ini.CanReset)
        {
            if (!ini.ReadBool(def, "AnchorChain.State", "hasReset", false))
            {
                if (ini.HasSection(def, "AnchorChain.ResetValues"))
                {
                    IDictionary<string, string> resets = ini.SectionValues(def, "AnchorChain.ResetValues");
                    if (resets != null)
                        foreach (KeyValuePair<string, string> reset in resets.ToList())
                            foreach (string key in reset.Value.Split(','))
                                ini.WriteText(user, reset.Key, key, ini.ReadText(def, reset.Key, key, ""));
                }
                ini.WriteBool(def, "AnchorChain.State", "hasReset", true);
            }
        }
        else
        {
            NoteMissing();
            p.Warnings.Add("[AnchorChain.ResetValues] was not applied: this game build's IniHandler lacks " + string.Join(", ", MissingMembers) + ".");
        }
        ini.Save(user);
        ini.Save(def);
    }
}
