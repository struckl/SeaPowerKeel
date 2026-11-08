using System;
using System.Collections.Generic;
using System.Threading;

namespace SeaPowerKeel.Loading;

internal enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

internal sealed class OverlayOptions
{
    public Corner Corner = Corner.TopLeft;
    public float Opacity = 0.92f;
    public double MaxSeconds = 300;        // gone this long after the game process started, whatever happens
    public double LingerSeconds = 2.5;     // how long it stays once the mods are all started
    public bool ClickToHide = true;
    public bool Offscreen;                 // tests: the window is made far off every monitor
}

/// <summary>
/// The loading window: a topmost, never-activated Win32 popup on its own thread. The game's main thread (and with it Unity's GUI)
/// stands still while a slow mod starts, so only a window of our own can keep counting. Drawn with GDI into a memory bitmap, ten
/// times a second.
/// </summary>
internal sealed class Overlay
{
    private const string ClassName = "RatsLoadingOverlay";
    private const int Rows = 8;
    private static Native.WndProc proc;                     // kept alive: Win32 holds only the pointer

    private readonly Progress progress;
    private readonly OverlayOptions options;
    private IntPtr hwnd;
    private float scale = 1f;
    private IntPtr fontText, fontBold, fontSmall;
    public volatile bool Opened, Closed;

    public Overlay(Progress progress, OverlayOptions options)
    {
        this.progress = progress;
        this.options = options;
    }

    public void Start(Action<string> warn)
    {
        var thread = new Thread(() =>
        {
            try { Run(); }
            catch (Exception ex) { warn?.Invoke("Loading window stopped: " + ex.Message); }
        }) { IsBackground = true, Name = "R.A.T.S. Loading window" };
        thread.Start();
    }

    // ---- window ---------------------------------------------------------------------------------------------------------

    private void Run()
    {
        IntPtr instance = Native.GetModuleHandleW(null);
        proc = WndProc;
        var cls = new Native.WNDCLASSEXW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.WNDCLASSEXW)),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = instance,
            hCursor = Native.LoadCursorW(IntPtr.Zero, new IntPtr(32512)),   // IDC_ARROW
            lpszClassName = ClassName,
        };
        Native.RegisterClassExW(ref cls);                                      // already registered (a second start): fine

        IntPtr screen = Native.GetDC(IntPtr.Zero);
        int dpi = Math.Max(96, Native.GetDeviceCaps(screen, Native.LOGPIXELSX));
        Native.ReleaseDC(IntPtr.Zero, screen);
        scale = dpi / 96f;
        MakeFonts();

        int w = (int)(Layout.Width * scale), h = (int)(Layout.Height(Rows) * scale), margin = (int)(24 * scale);
        Native.SystemParametersInfo(Native.SPI_GETWORKAREA, 0, out Native.RECT area, 0);
        int x = options.Corner == Corner.TopLeft || options.Corner == Corner.BottomLeft ? area.Left + margin : area.Right - w - margin;
        int y = options.Corner == Corner.TopLeft || options.Corner == Corner.TopRight ? area.Top + margin : area.Bottom - h - margin;
        if (options.Offscreen) { x = -32000; y = -32000; }

        hwnd = Native.CreateWindowExW(Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED,
            ClassName, "R.A.T.S. Loading", Native.WS_POPUP, x, y, w, h, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindow failed");
        Native.SetLayeredWindowAttributes(hwnd, 0, (byte)(Math.Max(0.4f, Math.Min(1f, options.Opacity)) * 255), Native.LWA_ALPHA);
        Native.SetTimer(hwnd, new UIntPtr(1), 100, IntPtr.Zero);
        Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Paint();
        Opened = true;

        while (Native.GetMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        foreach (IntPtr f in new[] { fontText, fontBold, fontSmall })
            if (f != IntPtr.Zero) Native.DeleteObject(f);
        Closed = true;
    }

    private IntPtr WndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_ERASEBKGND:
                return new IntPtr(1);
            case Native.WM_MOUSEACTIVATE:
                return new IntPtr(3);                                         // MA_NOACTIVATE: a click never takes focus from the game
            case Native.WM_PAINT:
                Paint();
                Native.ValidateRect(h, IntPtr.Zero);
                return IntPtr.Zero;
            case Native.WM_LBUTTONDOWN:
                if (options.ClickToHide) progress.Dismiss();
                return IntPtr.Zero;
            case Native.WM_TIMER:
            {
                Progress.Snapshot s = progress.Take(0);
                if (s.Dismissed || s.Elapsed > options.MaxSeconds || (s.Done && s.SinceDone > options.LingerSeconds))
                {
                    Native.KillTimer(h, new UIntPtr(1));
                    Native.DestroyWindow(h);
                }
                else Paint();
                return IntPtr.Zero;
            }
            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Native.DefWindowProcW(h, msg, wParam, lParam);
    }

    private void Paint()
    {
        if (hwnd == IntPtr.Zero) return;
        Native.GetClientRect(hwnd, out Native.RECT rc);
        int w = rc.Right, h = rc.Bottom;
        if (w <= 0 || h <= 0) return;
        IntPtr dc = Native.GetDC(hwnd);
        IntPtr mem = Native.CreateCompatibleDC(dc);
        IntPtr bmp = Native.CreateCompatibleBitmap(dc, w, h);
        IntPtr old = Native.SelectObject(mem, bmp);
        Draw(mem, w, h, progress.Take(Rows), scale, fontText, fontBold, fontSmall);
        Native.BitBlt(dc, 0, 0, w, h, mem, 0, 0, Native.SRCCOPY);
        Native.SelectObject(mem, old);
        Native.DeleteObject(bmp);
        Native.DeleteDC(mem);
        Native.ReleaseDC(hwnd, dc);
    }

    private void MakeFonts()
    {
        fontText = Font(12, 400, scale);
        fontBold = Font(14, 700, scale);
        fontSmall = Font(10, 400, scale);
    }

    internal static IntPtr Font(int points, int weight, float scale) =>
        Native.CreateFontW(-(int)Math.Round(points * 96f / 72f * scale), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");

    // ---- drawing (shared with the preview tool) ---------------------------------------------------------------------------

    internal static class Layout
    {
        public const int Width = 460;
        public static int Height(int rows) => 14 + 24 + 22 + 22 + rows * 19 + 8 + 20 + 14;
    }

    private static readonly uint Back = Native.Rgb(0x1E1F22), Edge = Native.Rgb(0x3F4147), Track = Native.Rgb(0x34373B), Amber = Native.Rgb(0xD5A876),
        Text = Native.Rgb(0xD7D7D7), Dim = Native.Rgb(0x949AA1), Green = Native.Rgb(0x63B970), Red = Native.Rgb(0xC04A3C);

    /// <summary>Draws the window's content into <paramref name="dc"/> (size w x h pixels, at <paramref name="scale"/> x 96 dpi).</summary>
    internal static void Draw(IntPtr dc, int w, int h, Progress.Snapshot s, float scale, IntPtr fontText, IntPtr fontBold, IntPtr fontSmall)
    {
        int S(int v) => (int)Math.Round(v * scale);
        Fill(dc, 0, 0, w, h, Edge);
        Fill(dc, 1, 1, w - 2, h - 2, Back);
        Native.SetBkMode(dc, Native.TRANSPARENT);
        int pad = S(14), y = S(12);

        Put(dc, fontBold, Amber, "R.A.T.S. Loading", pad, y, w - 2 * pad - S(70), S(24), 0);
        Put(dc, fontText, Dim, Clock(s.Elapsed), pad, y, w - 2 * pad, S(24), Native.DT_RIGHT);
        y += S(24);

        Put(dc, fontText, Text, s.Phase, pad, y, w - 2 * pad, S(22), 0);
        y += S(22);

        // progress bar: a fill when the number of mods is known, else a block that travels
        int barW = w - 2 * pad - S(60), barH = S(8), barY = y + S(5);
        Fill(dc, pad, barY, barW, barH, Track);
        if (s.Total > 0)
        {
            int fill = (int)(barW * Math.Min(1.0, s.Handled / (double)s.Total));
            Fill(dc, pad, barY, fill, barH, s.Done ? Green : Amber);
            Put(dc, fontSmall, Dim, s.Handled + " / " + s.Total, pad + barW + S(8), y, S(52), S(18), Native.DT_RIGHT);
        }
        else
        {
            int block = barW / 5, x = (int)(((s.Elapsed * 0.6) % 1.0) * (barW - block));
            Fill(dc, pad + x, barY, block, barH, Amber);
        }
        y += S(22);

        int rowH = S(19);
        for (int i = 0; i < Rows; i++)
        {
            int idx = i - (Rows - s.Recent.Count);
            if (idx >= 0)
            {
                ModLine m = s.Recent[idx];
                uint c = m.State == ModState.Running ? Green : m.State == ModState.RunningWithErrors ? Amber : m.State == ModState.Skipped ? Dim : Red;
                string mark = m.State == ModState.Running ? "ok" : m.State == ModState.RunningWithErrors ? "!" : m.State == ModState.Skipped ? "-" : "x";
                Put(dc, fontSmall, c, mark, pad, y, S(22), rowH, 0);
                Put(dc, fontText, m.State == ModState.Skipped ? Dim : Text, m.Name, pad + S(26), y, w - 2 * pad - S(26) - S(64), rowH, Native.DT_END_ELLIPSIS);
                if (m.TookSeconds >= 0.5)
                    Put(dc, fontSmall, m.TookSeconds >= 5 ? Amber : Dim, m.TookSeconds.ToString("0.0") + " s", pad, y, w - 2 * pad, rowH, Native.DT_RIGHT);
            }
            y += rowH;
        }
        y += S(8);

        string foot;
        uint footColor;
        if (s.Done) { foot = "All mods started."; footColor = Green; }
        else if (s.Current != null)    // Keel logs "STARTING i/n: name" before each start, so the slow mod is named while it is slow
        {
            foot = "Starting " + s.CurrentIndex + "/" + s.Total + ": " + Shorten(s.Current, 42) + (s.CurrentFor >= 1 ? "  " + s.CurrentFor.ToString("0") + " s" : "");
            footColor = s.CurrentFor >= 5 ? Amber : Text;
        }
        else if (s.Idle >= 2.5)
        {
            foot = "Waiting " + s.Idle.ToString("0") + " s" + (string.IsNullOrEmpty(s.LastEvent) ? "" : " after " + Shorten(s.LastEvent, 34)) + " - the next step is slow";
            footColor = Amber;
        }
        else { foot = "Click to hide."; footColor = Dim; }
        Put(dc, fontSmall, footColor, foot, pad, y, w - 2 * pad, S(20), Native.DT_END_ELLIPSIS);
    }

    private static string Shorten(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "...";

    private static string Clock(double seconds) => ((int)seconds / 60) + ":" + ((int)seconds % 60).ToString("00");

    private static void Fill(IntPtr dc, int x, int y, int w, int h, uint rgb)
    {
        if (w <= 0 || h <= 0) return;
        var r = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        IntPtr brush = Native.CreateSolidBrush(rgb);
        Native.FillRect(dc, ref r, brush);
        Native.DeleteObject(brush);
    }

    private static void Put(IntPtr dc, IntPtr font, uint color, string text, int x, int y, int w, int h, uint flags)
    {
        if (string.IsNullOrEmpty(text)) return;
        IntPtr old = Native.SelectObject(dc, font);
        Native.SetTextColor(dc, color);
        var r = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        Native.DrawTextW(dc, text, text.Length, ref r, Native.DT_SINGLELINE | Native.DT_VCENTER | Native.DT_NOPREFIX | flags);
        Native.SelectObject(dc, old);
    }
}
