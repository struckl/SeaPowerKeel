using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SeaPowerKeel;

/// <summary>
/// What KeelSetup.exe does to a Sea Power folder, kept apart from its window so it can be tested on a made-up folder. Everything it
/// changes is written to keel-setup.txt in the game folder, and Restore undoes exactly that and nothing else: a file Keel's setup did
/// not create is never deleted, and another loader's preloader is renamed, not removed.
/// </summary>
internal sealed class SetupCore
{
    public const string ExeName = "Sea Power.exe";
    private const string StubName = "SeaPowerKeel.Stub.dll";
    private const string PreloaderName = "AnchorChain.Preloader.dll";
    private const string SetAside = ".keel-off";
    private const string ManifestName = "keel-setup.txt";

    private readonly string game;

    public SetupCore(string gameFolder) => game = gameFolder;

    private string At(string relative) => Path.Combine(game, relative);
    private string Plugins => At(@"BepInEx\plugins");
    private string Manifest => At(ManifestName);

    public bool IsGameFolder => File.Exists(At(ExeName));
    public bool HasBepInEx => File.Exists(At("winhttp.dll")) && File.Exists(At(@"BepInEx\core\BepInEx.dll"));
    public bool HasStub => File.Exists(Path.Combine(Plugins, StubName));
    public bool HasOtherPreloader => File.Exists(Path.Combine(Plugins, PreloaderName));
    public bool CanRestore => File.Exists(Manifest);

    /// <summary>Alpine's Mod Loader on this PC: only one loader should be active.</summary>
    public bool HasAlpine =>
        Directory.Exists(At(@"Sea Power_Data\StreamingAssets\Alpine_Mod_Loader")) ||
        Directory.Exists(Path.GetFullPath(Path.Combine(game, @"..\..\workshop\content\1286220\3832006068")));

    private List<string> ReadManifest() => File.Exists(Manifest) ? File.ReadAllLines(Manifest).Where(l => l.Length > 0).ToList() : new List<string>();

    /// <summary>Installs the stub, adds BepInEx if the game has none, and sets another loader's preloader aside. Returns what it did, for the window.</summary>
    public List<string> Install(byte[] stub, byte[] bepInExZip)
    {
        var did = new List<string>();
        List<string> manifest = ReadManifest();
        void Note(string entry)
        {
            if (!manifest.Contains(entry))
                manifest.Add(entry);
            File.WriteAllLines(Manifest, manifest);     // after every step: an interrupted install can still be undone
        }

        if (!HasBepInEx)
        {
            using var zip = new ZipArchive(new MemoryStream(bepInExZip));
            foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.Name.Length > 0))
            {
                string relative = entry.FullName.Replace('/', '\\');
                string target = Path.GetFullPath(At(relative));
                if (!target.StartsWith(Path.GetFullPath(game) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(target))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                Note("created\t" + relative);
                entry.ExtractToFile(target);
            }
            did.Add("Added BepInEx.");
        }

        Directory.CreateDirectory(Plugins);
        string stubPath = Path.Combine(Plugins, StubName);
        if (!File.Exists(stubPath))
            Note(@"created\tBepInEx\plugins\".Replace(@"\t", "\t") + StubName);
        File.WriteAllBytes(stubPath, stub);
        did.Add("Installed Keel's starter file.");

        string preloader = Path.Combine(Plugins, PreloaderName);
        if (File.Exists(preloader))
        {
            string aside = preloader + SetAside;
            if (File.Exists(aside))
                File.Delete(aside);
            Note("renamed\t" + @"BepInEx\plugins\" + PreloaderName);
            File.Move(preloader, aside);
            did.Add("Set the other loader's preloader aside.");
        }
        return did;
    }

    /// <summary>Undoes what <see cref="Install"/> recorded.</summary>
    public void Restore()
    {
        List<string> manifest = ReadManifest();
        manifest.Reverse();
        foreach (string[] parts in manifest.Select(l => l.Split('\t')).Where(p => p.Length == 2))
        {
            string path = At(parts[1]);
            if (parts[0] == "created" && File.Exists(path))
            {
                File.Delete(path);
                RemoveEmptyParents(Path.GetDirectoryName(path));
            }
            else if (parts[0] == "renamed" && File.Exists(path + SetAside) && !File.Exists(path))
            {
                File.Move(path + SetAside, path);
            }
        }
        File.Delete(Manifest);
    }

    private void RemoveEmptyParents(string folder)
    {
        string root = Path.GetFullPath(game).TrimEnd('\\');
        for (string d = Path.GetFullPath(folder).TrimEnd('\\'); d.Length > root.Length && Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any(); d = Path.GetDirectoryName(d))
            Directory.Delete(d);
    }
}
