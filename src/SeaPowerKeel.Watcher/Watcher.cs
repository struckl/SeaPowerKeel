using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SeaPowerKeel;

/// <summary>
/// KeelWatcher.exe: started by Keel, waits for the game to end. A game that ended without a normal quit gets a window right away that
/// says which mod did it, lets the player keep that mod off or on, and offers a restart. It lives outside the game because nothing
/// inside a crashed process can show anything. Arguments: the game's process id, the BepInEx folder, the game's exe.
/// </summary>
internal static class Watcher
{
    private const string Title = "R.A.T.S. Keel";

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--logs")      // the report window's Save logs; a third argument saves there and shows nothing
        {
            string zip = LogBundle.Save(args[1], args.Length > 2 ? args[2] : null);
            if (args.Length == 2)
                LogBundle.Reveal(zip);
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--send")      // the report window's Send report: the send window, then nothing else
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Sender.Run(args[1], args.Length > 2 ? args[2] : null);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--relaunch")      // the report window's Restart the game: wait for the game to quit, then start it
        {
            if (int.TryParse(args[1], out int old))
                WaitFor(old);
            Restart(args[2]);
            return 0;
        }
        if (args.Length < 3 || !int.TryParse(args[0], out int pid))
            return 2;
        WaitFor(pid);
        string dir = args[1], exe = args[2];
        string[] journal = Lines(Path.Combine(dir, CrashText.JournalFile));
        if (journal.Length == 0 || journal.Any(l => l.StartsWith("quit\t", StringComparison.Ordinal)))
            return 0;

        string text = Explain(journal, dir, out string guid, out string name, out string detail, out string pack);
        SetProcessDPIAware();     // sharp text on a 4K screen
        Application.EnableVisualStyles();
        bool restart = Ask(text, name, detail, dir, out bool keepOff);
        if (guid != null && !keepOff)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, CrashText.KeepFile), guid);
            }
            catch (Exception)
            {
                // then the mod is held out as usual
            }
        }
        if (pack != null && keepOff)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, CrashText.PackOffFile), pack);
            }
            catch (Exception)
            {
                // then the pack stays on
            }
        }
        if (restart)
            Restart(exe);
        return 0;
    }

    private static void WaitFor(int pid)
    {
        try
        {
            using (Process game = Process.GetProcessById(pid))
                game.WaitForExit();
        }
        catch (ArgumentException)
        {
            // already gone
        }
    }

    private static bool Ask(string text, string name, string detail, string dir, out bool keepOff)
    {
        float dpi;
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            dpi = g.DpiX;
        int P(int v) => (int)Math.Round(v * dpi / 96f);
        int wide = P(460);

        using var form = new Form
        {
            Text = Title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, TopMost = true,
            StartPosition = FormStartPosition.CenterScreen, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White,
            Padding = new Padding(0, 0, P(26), P(20)), Font = new Font("Segoe UI", 10f),
        };
        // Placed by hand: an auto-sized form ignores its left and top padding for a control that is not docked.
        var column = new FlowLayoutPanel { Location = new Point(P(26), P(22)), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        Label Text2(string s, Font f, Color c, int below) => new Label { Text = s, Font = f, ForeColor = c, AutoSize = true, MinimumSize = new Size(wide, 0), MaximumSize = new Size(wide, 0), Margin = new Padding(0, 0, 0, P(below)) };

        column.Controls.Add(Text2("Sea Power crashed", new Font("Segoe UI Semibold", 15f), Color.FromArgb(32, 32, 32), 8));
        column.Controls.Add(Text2(text, form.Font, Color.FromArgb(48, 48, 48), 16));
        CheckBox off = null;
        if (name != null)
        {
            off = new CheckBox { Text = $"Keep {name} switched off for now", Checked = true, AutoSize = true, MaximumSize = new Size(wide, 0), Margin = new Padding(0, 0, 0, P(16)) };
            column.Controls.Add(off);
        }
        if (detail != null)
            column.Controls.Add(Text2("Technical detail: " + detail, new Font("Segoe UI", 8.5f), Color.FromArgb(120, 120, 120), 16));

        var start = new Button { Text = "Start the game", DialogResult = DialogResult.Yes, AutoSize = true, Padding = new Padding(P(10), P(2), P(10), P(2)), Margin = new Padding(0) };
        var close = new Button { Text = "Close", DialogResult = DialogResult.No, AutoSize = true, Padding = new Padding(P(10), P(2), P(10), P(2)), Margin = new Padding(P(8), 0, 0, 0) };
        // Right to left: the first one added sits at the right edge.
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
        row.Controls.Add(close);
        row.Controls.Add(start);
        var logs = new Button { Text = "Save logs", AutoSize = true, Padding = new Padding(P(10), P(2), P(10), P(2)), Margin = new Padding(0, 0, P(8), 0) };
        logs.Click += (s, e) =>
        {
            logs.Enabled = false;
            try
            {
                LogBundle.Reveal(LogBundle.Save(dir));
                logs.Text = "Saved to your Desktop";
            }
            catch (Exception)
            {
                logs.Text = "Could not save";
            }
        };
        row.Controls.Add(logs);
        var report = new Button { Text = "Send report", AutoSize = true, Padding = new Padding(P(10), P(2), P(10), P(2)), Margin = new Padding(0, 0, P(8), 0) };
        report.Click += (s, e) => Sender.Run(dir, null, form, crashed: true);
        row.Controls.Add(report);
        column.Controls.Add(row);
        form.Controls.Add(column);
        form.AcceptButton = start;
        form.CancelButton = close;
        form.Shown += (s, e) =>
        {
            form.Activate();
            form.ActiveControl = start;
        };
        DialogResult result = form.ShowDialog();
        keepOff = off == null || off.Checked;
        return result == DialogResult.Yes;
    }

    private static string FolderName(string fullPath, string folder)
    {
        try
        {
            string p = fullPath.Replace('\\', '/');
            int at = p.IndexOf("/" + folder + "/", StringComparison.OrdinalIgnoreCase);
            string ini = Path.Combine(p.Substring(0, at + folder.Length + 1), "_info.ini");
            string line = File.Exists(ini) ? File.ReadLines(ini).FirstOrDefault(l => l.StartsWith("Name=", StringComparison.OrdinalIgnoreCase)) : null;
            if (line != null && line.Length > 5)
                return line.Substring(5).Trim();
        }
        catch (Exception)
        {
            // the folder's own name will do
        }
        return folder;
    }

    internal static string Explain(string[] journal, string dir, out string guid, out string name, out string detail, out string pack)
    {
        guid = name = pack = null;
        string block = CrashText.ReadAll(Path.Combine(dir, CrashText.StackFile));
        if (string.IsNullOrWhiteSpace(block))
            block = null;
        detail = CrashText.CrashLine(block);

        // Id, display name and code root of each mod, in the order Keel uses for blame.
        List<string[]> mods = Lines(Path.Combine(dir, CrashText.ModsFile)).Select(l => l.Split('\t')).Where(p => p.Length == 3).ToList();

        string[] step = journal.LastOrDefault(l => l.StartsWith("load\t", StringComparison.Ordinal) || l.StartsWith("start\t", StringComparison.Ordinal) || l.StartsWith("done\t", StringComparison.Ordinal))?.Split('\t');
        if (step != null && step[0] != "done")
        {
            string id = step[1];
            guid = id;
            name = mods.FirstOrDefault(m => m[0] == id)?[1] ?? id;
            return $"The game stopped while {name} was {(step[0] == "load" ? "loading" : "starting")}, so this mod is the cause.";
        }
        int hit = CrashText.FirstHit(block, mods.Select(m => m[2]).ToList());
        if (hit >= 0)
        {
            guid = mods[hit][0];
            name = mods[hit][1];
            return $"{name} was running when the game crashed, so it is the most likely cause.";
        }

        if (CrashText.LastPack(block, out string folder, out string file))
        {
            pack = folder;
            name = FolderName(file, folder);
            return $"The game was loading {Path.GetFileName(file)} from {name} just before it crashed, so this mod is the most likely cause.";
        }

        return "Keel couldn't tell which mod caused this. It may be the game itself, or a mod's models or files.\n\n" +
               "If it keeps happening, click Send report to send the logs to the R.A.T.S. developer, or Save logs and attach the file to a bug report.";
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
