using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SeaPowerKeel;

/// <summary>
/// "Send report": packs the same logs as Save logs, with the Windows user name taken out of every path, shows the player what
/// goes out and sends it to the R.A.T.S. feedback board only after the player clicks Send. Nothing is sent without that click.
/// </summary>
internal static class Sender
{
    private const string Endpoint = "https://hub.sewerlabs.dev/api/report";
    private const string Board = "https://hub.sewerlabs.dev";
    private const long MaxBytes = 5 * 1024 * 1024;     // the board refuses more

    private static string KeelVersion =>
        typeof(Sender).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "";

    /// <summary>
    /// Shows the send window. <paramref name="game"/> is the game build if the caller knows it; <paramref name="crashed"/> says
    /// whether the game crashed (the crash window) or the player sends from the report window: the board titles its mail by it.
    /// </summary>
    public static void Run(string bepInEx, string game = null, IWin32Window owner = null, bool crashed = false)
    {
        (string build, string summary) = ReportFacts.Read(bepInEx, game);
        game = build;
        kind = crashed ? "crash" : "report";
        Sender.summary = summary;
        string folder = Path.Combine(Path.GetTempPath(), "RATS-Keel-report-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string zip = null;
        try
        {
            Directory.CreateDirectory(folder);
            zip = LogBundle.Save(bepInEx, folder, scrub: true);
            Ask(zip, game, owner);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Keel could not pack the logs: " + ex.Message + "\n\nYou can still use Save logs and attach the file to a post on " + Board + ".",
                            "R.A.T.S. Keel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception)
            {
                // Windows cleans its temp folder in time
            }
        }
    }

    private static void Ask(string zip, string game, IWin32Window owner)
    {
        float dpi;
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            dpi = g.DpiX;
        int P(int v) => (int)Math.Round(v * dpi / 96f);
        int wide = P(480);

        string[] files;
        using (ZipArchive archive = ZipFile.OpenRead(zip))
            files = archive.Entries.Select(e => $"{e.FullName}   ({Size(e.Length)})").ToArray();
        long size = new FileInfo(zip).Length;

        using var form = new Form
        {
            Text = "R.A.T.S. Keel", FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, TopMost = true,
            StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Padding = new Padding(0, 0, P(26), P(20)), Font = new Font("Segoe UI", 10f),
            ShowInTaskbar = owner == null,
        };
        var column = new FlowLayoutPanel { Location = new Point(P(26), P(22)), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        Label Text2(string s, Font f, Color c, int below) => new Label { Text = s, Font = f, ForeColor = c, AutoSize = true, MinimumSize = new Size(wide, 0), MaximumSize = new Size(wide, 0), Margin = new Padding(0, 0, 0, P(below)) };
        Color grey = Color.FromArgb(48, 48, 48);

        column.Controls.Add(Text2("Send logs to the R.A.T.S. developer?", new Font("Segoe UI Semibold", 15f), Color.FromArgb(32, 32, 32), 8));
        column.Controls.Add(Text2(
            "These files go to the R.A.T.S. feedback board (hub.sewerlabs.dev), where only the mod's developer can open them. " +
            "They are the logs of Keel, BepInEx and the game: which mods ran, errors, and your graphics card, Windows version and game settings as the game logs them. " +
            "Your Windows user name is replaced with %USERNAME% in every path. Nothing else is sent, and nothing is sent unless you click Send.",
            form.Font, grey, 10));
        var list = new ListBox
        {
            Width = wide, Height = P(110), IntegralHeight = false, Font = new Font("Consolas", 9f), Margin = new Padding(0, 0, 0, P(4)),
            HorizontalScrollbar = true, SelectionMode = SelectionMode.None,
        };
        list.Items.AddRange(files);
        column.Controls.Add(list);
        var look = new LinkLabel { Text = $"Show the zip ({Size(size)}) in Explorer", AutoSize = true, Margin = new Padding(0, 0, 0, P(12)) };
        look.LinkClicked += (s, e) => LogBundle.Reveal(zip);
        column.Controls.Add(look);
        column.Controls.Add(Text2("What happened? (optional)", form.Font, grey, 4));
        var note = new TextBox { Multiline = true, Width = wide, Height = P(64), MaxLength = 1000, ScrollBars = ScrollBars.Vertical, Margin = new Padding(0, 0, 0, P(14)) };
        column.Controls.Add(note);
        Label status = Text2("", form.Font, grey, 12);
        status.Visible = false;
        column.Controls.Add(status);

        var send = new Button { Text = "Send", AutoSize = true, Padding = new Padding(P(10), P(2), P(10), P(2)), Margin = new Padding(0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(P(10), P(2), P(10), P(2)), Margin = new Padding(P(8), 0, 0, 0) };
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
        row.Controls.Add(cancel);
        row.Controls.Add(send);
        column.Controls.Add(row);
        form.Controls.Add(column);
        form.CancelButton = cancel;

        if (size > MaxBytes)
        {
            send.Enabled = false;
            status.Text = $"The logs are {Size(size)}, more than the 5 MB the board takes. Please use Save logs and attach the file to a post on {Board}.";
            status.ForeColor = Color.FromArgb(170, 40, 30);
            status.Visible = true;
        }

        bool sent = false;
        send.Click += async (s, e) =>
        {
            if (sent)
            {
                form.Close();
                return;
            }
            send.Enabled = cancel.Enabled = note.Enabled = false;
            status.Text = "Sending …";
            status.ForeColor = grey;
            status.Visible = true;
            string answer = await Task.Run(() => Upload(zip, note.Text.Trim(), game));
            if (answer.StartsWith("#", StringComparison.Ordinal))
            {
                sent = true;
                status.Text = $"Sent, thank you. Your report is number {answer}. If you post about it on {Board}, mention that number.";
                status.ForeColor = Color.FromArgb(30, 120, 60);
                send.Text = "Close";
                send.Enabled = true;
                form.AcceptButton = send;
            }
            else
            {
                status.Text = answer + $"\n\nYou can still use Save logs and attach the file to a post on {Board}.";
                status.ForeColor = Color.FromArgb(170, 40, 30);
                send.Text = "Try again";
                send.Enabled = cancel.Enabled = note.Enabled = true;
            }
        };
        form.Shown += (s, e) =>
        {
            form.Activate();
            form.ActiveControl = note;
        };
        form.ShowDialog(owner);
    }

    /// <summary>"#id" when the board took the report, else a sentence for the player.</summary>
    private static string kind = "report", summary = "";

    private static string Upload(string zip, string note, string game)
    {
        try
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;     // 12288 = TLS 1.3
            }
            catch (NotSupportedException)
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"RATS-Keel/{(KeelVersion.Length > 0 ? KeelVersion : "0")}");
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(File.ReadAllBytes(zip));
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            form.Add(file, "logs", Path.GetFileName(zip));
            form.Add(new StringContent(note ?? ""), "note");
            form.Add(new StringContent(KeelVersion), "keel");
            form.Add(new StringContent(game ?? ""), "game");
            form.Add(new StringContent(kind), "kind");          // crash | report: the board's mail says "Crash report" only for a crash
            form.Add(new StringContent(summary ?? ""), "summary");
            using HttpResponseMessage response = http.PostAsync(Endpoint, form).GetAwaiter().GetResult();
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Match id = Regex.Match(body, "\"id\"\\s*:\\s*(\\d+)");
            if (response.IsSuccessStatusCode && id.Success)
                return "#" + id.Groups[1].Value;
            Match error = Regex.Match(body, "\"error\"\\s*:\\s*\"([^\"]*)\"");
            return error.Success ? "The board did not take the report: " + error.Groups[1].Value : $"The board did not take the report (HTTP {(int)response.StatusCode}).";
        }
        catch (Exception ex)
        {
            return "Could not reach the board: " + (ex.InnerException?.Message ?? ex.Message);
        }
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
