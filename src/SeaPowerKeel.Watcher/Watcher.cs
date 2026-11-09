using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SeaPowerKeel;

/// <summary>
/// KeelWatcher.exe: started by Keel, waits for the game to end. A game that ended without a normal quit gets a window right away that
/// says which mod did it, with a Restart. It lives outside the game because nothing inside a crashed process can show anything.
/// Arguments: the game's process id, the BepInEx folder, the game's exe.
/// </summary>
internal static class Watcher
{
    private const string Title = "R.A.T.S. Keel";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[0], out int pid))
            return 2;
        try
        {
            using (Process game = Process.GetProcessById(pid))
                game.WaitForExit();
        }
        catch (ArgumentException)
        {
            // already gone: judge it all the same
        }
        string dir = args[1], exe = args[2];
        string[] journal = Lines(Path.Combine(dir, CrashText.JournalFile));
        if (journal.Length == 0 || journal.Any(l => l.StartsWith("quit\t", StringComparison.Ordinal)))
            return 0;

        string text = Explain(journal, dir);
        DialogResult answer = MessageBox.Show(text + "\n\nStart the game again?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Error,
                                              MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
        if (answer == DialogResult.Yes)
            Restart(exe);
        return 0;
    }

    internal static string Explain(string[] journal, string dir)
    {
        string block = CrashText.ReadAll(Path.Combine(dir, CrashText.StackFile));
        if (string.IsNullOrWhiteSpace(block))
            block = null;
        string detail = CrashText.CrashLine(block);
        string first = "Sea Power crashed" + (detail != null ? $" ({detail})." : ".");

        bool hunting = Lines(Path.Combine(dir, CrashText.HuntFile)).Contains("phase=Round");
        if (hunting)
            return first + "\n\nThis was a round of the crash hunt: Keel has noted it and goes on at the next start.";

        // Pairs of plugin id and display name, in the order Keel uses for blame.
        List<string[]> mods = Lines(Path.Combine(dir, CrashText.ModsFile)).Select(l => l.Split('\t')).Where(p => p.Length == 3).ToList();

        string[] step = journal.LastOrDefault(l => l.StartsWith("load\t", StringComparison.Ordinal) || l.StartsWith("start\t", StringComparison.Ordinal) || l.StartsWith("done\t", StringComparison.Ordinal))?.Split('\t');
        if (step != null && step[0] != "done")
        {
            string name = mods.FirstOrDefault(m => m[0] == step[1])?[1] ?? step[1];
            return first + $"\n\nCause: {name}. The game died while Keel was {(step[0] == "load" ? "loading" : "starting")} it.\nKeel will keep it out from the next start, until the mod is updated.";
        }
        int hit = CrashText.FirstHit(block, mods.Select(m => m[2]).ToList());
        if (hit >= 0)
            return first + $"\n\nCause: {mods[hit][1]}. Its code was running when the game crashed.\nKeel will keep it out from the next start, until the mod is updated.";

        return first + "\n\nKeel could not tell which mod did it: no mod's code was running at that moment. The cause can be the game itself or a pack's models or files." +
               "\nAfter the restart, Ctrl+Shift+K has a 'Find crashing mod' button that narrows it down over a few restarts.";
    }

    private static void Restart(string exe)
    {
        // Steam counts this process as part of the game until it ends, so the start comes from a detached shell a few seconds later.
        bool steam = exe.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) >= 0 || exe.IndexOf("/steamapps/common/", StringComparison.OrdinalIgnoreCase) >= 0;
        string start = steam ? "start \"\" steam://rungameid/1286220" : $"start \"\" /d \"{Path.GetDirectoryName(exe)}\" \"{exe}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 4 /nobreak >nul & " + start) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }

    private static string[] Lines(string path)
    {
        string text = CrashText.ReadAll(path);
        return text == null ? new string[0] : text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToArray();
    }
}
