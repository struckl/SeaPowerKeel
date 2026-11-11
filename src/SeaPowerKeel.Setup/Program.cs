using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SeaPowerKeel;

/// <summary>KeelSetup.exe: one window that gets Keel running. A single file: the starter DLL and BepInEx are inside it.</summary>
internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    [STAThread]
    private static void Main()
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.Run(new SetupForm());
    }

    public static byte[] Resource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Sea Power in any Steam library on this PC, or null.</summary>
    public static string FindGame()
    {
        try
        {
            string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (steam == null)
                return null;
            var libraries = new List<string> { steam };
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\"").Cast<Match>().Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
            return libraries.Select(l => Path.Combine(l, "steamapps", "common", "Sea Power")).FirstOrDefault(p => File.Exists(Path.Combine(p, SetupCore.ExeName)));
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal sealed class SetupForm : Form
{
    private readonly TextBox folder;
    private readonly Label status;
    private readonly Button install, restore;
    private readonly int wide;

    public SetupForm()
    {
        float dpi;
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            dpi = g.DpiX;
        int P(int v) => (int)Math.Round(v * dpi / 96f);
        wide = P(500);

        Text = "R.A.T.S. Keel Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(0, 0, P(26), P(20));

        var column = new FlowLayoutPanel { Location = new Point(P(26), P(22)), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        Label Line(string s, Font f, Color c, int below) => new() { Text = s, Font = f, ForeColor = c, AutoSize = true, MinimumSize = new Size(wide, 0), MaximumSize = new Size(wide, 0), Margin = new Padding(0, 0, 0, P(below)) };
        column.Controls.Add(Line("R.A.T.S. Keel", new Font("Segoe UI Semibold", 15f), Color.FromArgb(32, 32, 32), 4));
        column.Controls.Add(Line("Sets up Keel for your copy of Sea Power. You can undo it here at any time.", Font, Color.FromArgb(70, 70, 70), 16));

        var pick = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 0, P(12)) };
        folder = new TextBox { Text = Program.FindGame() ?? "", Width = wide - P(100), Margin = new Padding(0, P(2), P(8), 0) };
        var browse = new Button { Text = "Browse...", AutoSize = true, Margin = new Padding(0) };
        browse.Click += (s, e) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Sea Power|" + SetupCore.ExeName, Title = "Find Sea Power.exe" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                folder.Text = Path.GetDirectoryName(dialog.FileName);
        };
        folder.TextChanged += (s, e) => Refresh2();
        pick.Controls.Add(folder);
        pick.Controls.Add(browse);
        column.Controls.Add(pick);

        status = Line("", Font, Color.FromArgb(48, 48, 48), 16);
        column.Controls.Add(status);

        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(P(10), P(2), P(10), P(2)), Margin = new Padding(P(8), 0, 0, 0) };
        install = new Button { Text = "Install", AutoSize = true, Padding = new Padding(P(10), P(2), P(10), P(2)), Margin = new Padding(0) };
        restore = new Button { Text = "Restore", AutoSize = true, Padding = new Padding(P(10), P(2), P(10), P(2)), Margin = new Padding(0, 0, P(8), 0) };
        install.Click += (s, e) => Do(c => "Done. " + string.Join(" ", c.Install(Program.Resource("stub"), Program.Resource("bepinex"), Program.Resource("patcher"))) + "\n\nStart Sea Power, switch R.A.T.S. Keel on in Mods, and restart the game.");
        restore.Click += (s, e) => Do(c => { c.Restore(); return "Everything Keel's setup changed is back as it was."; });
        close.Click += (s, e) => Close();
        row.Controls.Add(close);
        row.Controls.Add(install);
        row.Controls.Add(restore);
        column.Controls.Add(row);
        Controls.Add(column);
        CancelButton = close;
        AcceptButton = install;
        Refresh2();
    }

    private static bool GameRunning => Process.GetProcessesByName("Sea Power").Length > 0;

    private void Do(Func<SetupCore, string> action)
    {
        if (GameRunning)
        {
            status.Text = "Sea Power is running. Close it first.";
            return;
        }
        string result;
        try
        {
            result = action(new SetupCore(folder.Text.Trim()));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            result = "That did not work: " + ex.Message;
        }
        Refresh2();
        status.Text = result;
    }

    private void Refresh2()
    {
        var core = new SetupCore(folder.Text.Trim());
        bool ok = folder.Text.Trim().Length > 0 && core.IsGameFolder;
        install.Enabled = ok;
        restore.Enabled = ok && core.CanRestore;
        if (!ok)
        {
            status.Text = "Choose the folder that holds Sea Power.exe.";
            return;
        }
        var lines = new List<string> { core.HasStub ? "Keel is set up in this folder. Install again to repair it." : "Ready to install." };
        if (!core.HasBepInEx)
            lines.Add("BepInEx is missing and will be added.");
        if (core.HasOtherPreloader)
            lines.Add("Another loader's preloader will be set aside, so only one loader is active. Restore brings it back.");
        if (core.HasAlpine)
            lines.Add(core.HasOtherPreloader ? "Alpine's Mod Loader is on this PC. Keel will run your mods instead; Restore switches back." : "Alpine's Mod Loader is on this PC, but it is not active in this game.");
        status.Text = string.Join("\n", lines);
    }
}
