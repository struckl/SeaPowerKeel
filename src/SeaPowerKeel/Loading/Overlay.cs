using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace SeaPowerKeel.Loading;

/// <summary>Where the window sits while the game starts. Marker = the blank band under the main menu's news box (the spot the author picked).</summary>
internal enum Spot { Marker, TopLeft, TopRight, BottomLeft, BottomRight }

internal sealed class OverlayOptions
{
    public Spot Spot = Spot.TopRight;
    public float Opacity = 0.92f;
    public float Scale;                    // 0 = by the game window's height
    public double MaxSeconds = 300;        // the loading window is gone (or a summary) this long after the game process started, whatever happens
    public double LingerSeconds = 2.0;     // how long the full window stays once the mods are all started, before it moves to the corner
    public double SummarySeconds = 0;      // 0 = the summary stays until clicked away (Keel always; >0 only for the preview tool)
    public bool ClickToHide = true;
    public bool Offscreen;                 // tests: the window is made far off every monitor
    public Action OnSummaryClick;          // a click on the summary (Keel opens its report)
}

/// <summary>
/// The loading window: a never-activated Win32 popup on its own thread. The game's main thread (and with it Unity's GUI) stands still while
/// a slow mod starts, so only a window of our own can keep counting. It is OWNED by the game's window: it stays above the game but not
/// above other programs, and goes when the game is minimized. Phases: the full window on the marker; when every mod has started it shrinks
/// and glides to the bottom left, above the game's version text, as a summary that stays until clicked away.
/// </summary>
internal sealed class Overlay
{
    private const string ClassName = "RatsLoadingOverlay";
    private const int Rows = 8, Tick = 16;
    private const double MoveSeconds = 0.8, FadeSeconds = 0.8;
    private static Native.WndProc proc;                     // kept alive: Win32 holds only the pointer
    private static Native.EnumWindowsProc enumProc;

    private enum Stage { Full, Moving, Summary, Away, Fading, HandOff }

    private const double HandOffSeconds = 0.25;

    private readonly Progress progress;
    private readonly OverlayOptions options;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private IntPtr hwnd, owner;
    private float scale = 1f;
    private IntPtr fontText, fontBold, fontSmall;
    private Native.RECT game;                                 // the game's window (or the main monitor until it exists)
    private Stage stage = Stage.Full;
    private double stageStart;
    private Native.RECT from, to, now;                        // the rectangles of the move
    private IntPtr snapDc, snapBmp, snapOld;                  // the full window as it was when the move began
    private int snapW, snapH;
    private Progress.Snapshot summary;
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

    private double Now => clock.Elapsed.TotalSeconds;

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

        MeasureGame();
        ApplyScale();
        Native.RECT full = FullRect();
        if (options.Offscreen) full = new Native.RECT { Left = -32000, Top = -32000, Right = -32000 + (full.Right - full.Left), Bottom = -32000 + (full.Bottom - full.Top) };
        now = full;

        // Not topmost: that put it above every program. The game's window becomes its owner as soon as that exists (see FindOwner).
        hwnd = Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED,
            ClassName, "R.A.T.S. Keel Loading", Native.WS_POPUP, full.Left, full.Top, full.Right - full.Left, full.Bottom - full.Top,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindow failed");
        SetAlpha(options.Opacity);
        Native.SetTimer(hwnd, new UIntPtr(1), Tick, IntPtr.Zero);
        Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
        Paint();
        Opened = true;

        while (Native.GetMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        FreeSnapshot();
        foreach (IntPtr f in new[] { fontText, fontBold, fontSmall })
            if (f != IntPtr.Zero) Native.DeleteObject(f);
        Closed = true;
        LoadingWindow.OverlayGone = true;
    }

    private void SetAlpha(float a) =>
        Native.SetLayeredWindowAttributes(hwnd, 0, (byte)(Math.Max(0f, Math.Min(1f, a)) * 255), Native.LWA_ALPHA);

    private int ticks;

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
                if (stage == Stage.Summary)
                {
                    int cx = (short)((long)lParam & 0xFFFF), cy = (short)(((long)lParam >> 16) & 0xFFFF);
                    int pad = (int)Math.Round(14 * scale), top = (int)Math.Round((8 + 24 + 20 + 20) * scale), lineH = (int)Math.Round(20 * scale);
                    if (cx >= pad && cx <= pad + (int)Math.Round(CreditWidth * scale) && cy >= top && cy <= top + lineH)
                    {
                        try { Process.Start(new ProcessStartInfo(ProfileUrl) { UseShellExecute = true }); } catch (Exception) { /* no browser: nothing to do */ }
                    }
                    else options.OnSummaryClick?.Invoke();
                }
                else if (options.ClickToHide && stage == Stage.Full) Begin(Stage.Fading);
                return IntPtr.Zero;
            case Native.WM_RBUTTONDOWN:
                if (options.ClickToHide) Begin(Stage.Fading);
                return IntPtr.Zero;
            case Native.WM_TIMER:
                try { OnTick(); }
                catch (Exception) { Native.KillTimer(h, new UIntPtr(1)); Native.DestroyWindow(h); }
                return IntPtr.Zero;
            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Native.DefWindowProcW(h, msg, wParam, lParam);
    }

    private void Begin(Stage next)
    {
        stage = next;
        stageStart = Now;
        if (next == Stage.Moving)
        {
            summaryStartTicks = DateTime.UtcNow.Ticks;
            summary = progress.Take(0);
            TakeSnapshot();
            from = now;
            to = SummaryRect();
        }
    }

    private long summaryStartTicks;
    private bool handedOffFull;

    private void OnTick()
    {
        // The summary card belongs to the main menu: when a mission runs or the game loads a scene of its own (mission, editor,
        // campaign), it goes.
        if ((stage == Stage.Moving || stage == Stage.Summary || stage == Stage.Away) && (LoadingWindow.MissionRunning || LoadingWindow.SceneLoadedTicks > summaryStartTicks))
            Begin(Stage.Fading);
        if (owner == IntPtr.Zero && ++ticks % 6 == 1) FindOwner();
        Progress.Snapshot s = progress.Take(0);
        double t = Now - stageStart;
        switch (stage)
        {
            case Stage.Full:
                if (s.Dismissed) { Begin(Stage.Fading); break; }
                if (LoadingWindow.ExternalProgress && !handedOffFull)
                {
                    handedOffFull = true;
                    Native.ShowWindow(hwnd, Native.SW_HIDE);
                }
                if (handedOffFull && s.Done)
                {
                    stage = Stage.Summary;
                    summaryStartTicks = DateTime.UtcNow.Ticks;
                    summary = s;
                    now = SummaryRect();
                    Native.SetWindowPos(hwnd, IntPtr.Zero, now.Left, now.Top, now.Right - now.Left, now.Bottom - now.Top,
                        Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
                    PublishSummary();
                    // The in-game summary will take over before this is shown again.
                    return;
                }
                if (s.Done && s.SinceDone > options.LingerSeconds) { Begin(Stage.Moving); break; }
                if (s.Elapsed > options.MaxSeconds) { Begin(s.Done ? Stage.Moving : Stage.Fading); break; }
                break;
            case Stage.Moving:
            {
                double k = Math.Min(1.0, t / MoveSeconds), e = 1 - Math.Pow(1 - k, 3);         // ease out
                now = Lerp(from, to, e);
                Native.SetWindowPos(hwnd, IntPtr.Zero, now.Left, now.Top, now.Right - now.Left, now.Bottom - now.Top, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
                if (k >= 1.0)
                {
                    stage = Stage.Summary; stageStart = Now;
                    PublishSummary();
                }
                break;
            }
            case Stage.HandOff:
            {
                double k = Math.Min(1.0, t / HandOffSeconds);
                SetAlpha(options.Opacity * (float)(1 - k));
                if (k >= 1.0) { Native.KillTimer(hwnd, new UIntPtr(1)); Native.DestroyWindow(hwnd); return; }
                break;
            }
            case Stage.Summary:
                if (LoadingWindow.GameDrawsSummary) { Begin(Stage.HandOff); break; }      // the game draws the card now
                if (LoadingWindow.OnMenuSubPage) { Native.ShowWindow(hwnd, Native.SW_HIDE); Begin(Stage.Away); return; }      // Scenarios, Options ...: gone at once
                if (options.SummarySeconds > 0 && t > options.SummarySeconds) Begin(Stage.Fading);
                break;
            case Stage.Away:
                if (LoadingWindow.GameDrawsSummary) { Native.KillTimer(hwnd, new UIntPtr(1)); Native.DestroyWindow(hwnd); return; }
                if (!LoadingWindow.OnMenuSubPage)      // back on the menu's top page: there again at once, with a fresh timer
                {
                    stage = Stage.Summary; stageStart = Now;
                    SetAlpha(options.Opacity);
                    Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
                    Paint();
                }
                return;
            case Stage.Fading:
            {
                double k = Math.Min(1.0, t / FadeSeconds);
                SetAlpha(options.Opacity * (float)(1 - k));
                if (k >= 1.0) { Native.KillTimer(hwnd, new UIntPtr(1)); Native.DestroyWindow(hwnd); return; }
                break;
            }
        }
        Paint();
    }

    private void PublishSummary()
    {
        LoadingWindow.SummaryX = now.Left - game.Left;
        LoadingWindow.SummaryY = now.Top - game.Top;
        LoadingWindow.SummaryW = now.Right - now.Left;
        LoadingWindow.SummaryH = now.Bottom - now.Top;
        LoadingWindow.SummaryScale = scale;
        LoadingWindow.SummaryReady = true;
    }

    // ---- where it goes ---------------------------------------------------------------------------------------------------

    /// <summary>The game's window (class UnityWndClass of this process): its rectangle sizes and places everything; it becomes the owner.</summary>
    private void FindOwner()
    {
        uint pid = (uint)Process.GetCurrentProcess().Id;
        IntPtr found = IntPtr.Zero;
        var name = new StringBuilder(64);
        enumProc = (w, l) =>
        {
            Native.GetWindowThreadProcessId(w, out uint p);
            if (p != pid || !Native.IsWindowVisible(w)) return true;
            name.Clear();
            Native.GetClassNameW(w, name, 64);
            if (name.ToString() != "UnityWndClass") return true;
            found = w;
            return false;
        };
        Native.EnumWindows(enumProc, IntPtr.Zero);
        if (found == IntPtr.Zero) return;
        owner = found;
        Native.SetOwner(hwnd, owner);
        // Above the game right now - but only when the game is the program in front: with another program in front it stays behind it.
        if (Native.GetForegroundWindow() == owner)
            Native.SetWindowPos(hwnd, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        // the game's window decides the size and the place from now on
        MeasureGame();
        ApplyScale();
        if (stage == Stage.Full && !options.Offscreen)
        {
            now = FullRect();
            Native.SetWindowPos(hwnd, IntPtr.Zero, now.Left, now.Top, now.Right - now.Left, now.Bottom - now.Top, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        }
    }

    private void MeasureGame()
    {
        if (owner != IntPtr.Zero && Native.GetWindowRect(owner, out game) && game.Right - game.Left > 200 && game.Bottom - game.Top > 200)
            return;
        game = new Native.RECT { Left = 0, Top = 0, Right = Native.GetSystemMetrics(Native.SM_CXSCREEN), Bottom = Native.GetSystemMetrics(Native.SM_CYSCREEN) };
    }

    private void ApplyScale()
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        int dpi = Math.Max(96, Native.GetDeviceCaps(screen, Native.LOGPIXELSX));
        Native.ReleaseDC(IntPtr.Zero, screen);
        int gh = game.Bottom - game.Top;
        // by the game's height (1.5 on a 4K screen: the window then fits the marker's band), never below the system's DPI scale
        float s = options.Scale > 0 ? options.Scale : Math.Max(Math.Max(1f, dpi / 96f), gh / 1080f * 0.75f);
        if (Math.Abs(s - scale) < 0.01f && fontText != IntPtr.Zero) return;
        scale = s;
        foreach (IntPtr f in new[] { fontText, fontBold, fontSmall })
            if (f != IntPtr.Zero) Native.DeleteObject(f);
        fontText = Font(12, 400, scale);
        fontBold = Font(14, 700, scale);
        fontSmall = Font(10, 400, scale);
    }

    private Native.RECT FullRect()
    {
        int gw = game.Right - game.Left, gh = game.Bottom - game.Top;
        int w = (int)(Layout.Width * scale), h = (int)(Layout.Height(Rows) * scale), margin = (int)(24 * scale);
        int x, y;
        switch (options.Spot)
        {
            case Spot.TopLeft: x = game.Left + margin; y = game.Top + margin; break;
            case Spot.TopRight: x = game.Right - w - margin; y = game.Top + margin; break;
            case Spot.BottomLeft: x = game.Left + margin; y = game.Bottom - h - margin; break;
            case Spot.BottomRight: x = game.Right - w - margin; y = game.Bottom - h - margin; break;
            default:                                            // the marker: centred in the blank band under the main menu's news box
                x = game.Left + (int)(gw * 0.588) - w / 2;
                y = game.Top + (int)(gh * 0.770) - h / 2;
                break;
        }
        x = Math.Max(game.Left, Math.Min(game.Right - w, x));
        y = Math.Max(game.Top, Math.Min(game.Bottom - h, y));
        return new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    /// <summary>Bottom left, just above the game's version text.</summary>
    private Native.RECT SummaryRect()
    {
        int gh = game.Bottom - game.Top;
        int w = (int)(Layout.SummaryWidth * scale), h = (int)(Layout.SummaryHeight * scale);
        int x = game.Left + (int)(8 * scale), y = game.Bottom - (int)(gh * 0.05) - h;
        return new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    private static Native.RECT Lerp(Native.RECT a, Native.RECT b, double e) => new Native.RECT
    {
        Left = (int)(a.Left + (b.Left - a.Left) * e), Top = (int)(a.Top + (b.Top - a.Top) * e),
        Right = (int)(a.Right + (b.Right - a.Right) * e), Bottom = (int)(a.Bottom + (b.Bottom - a.Bottom) * e),
    };

    // ---- painting ---------------------------------------------------------------------------------------------------------

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
        switch (stage)
        {
            case Stage.Full:
                Draw(mem, w, h, progress.Take(Rows), scale, fontText, fontBold, fontSmall);
                break;
            case Stage.Moving:
            {
                double k = Math.Min(1.0, (Now - stageStart) / MoveSeconds);
                if (k < 0.55 && snapDc != IntPtr.Zero)      // the full window, squeezed along while it shrinks
                {
                    Native.SetStretchBltMode(mem, Native.HALFTONE);
                    Native.StretchBlt(mem, 0, 0, w, h, snapDc, 0, 0, snapW, snapH, Native.SRCCOPY);
                }
                else DrawSummary(mem, w, h, summary ?? progress.Take(0), scale, fontText, fontBold, fontSmall);
                break;
            }
            default:
                DrawSummary(mem, w, h, summary ?? progress.Take(0), scale, fontText, fontBold, fontSmall);
                break;
        }
        Native.BitBlt(dc, 0, 0, w, h, mem, 0, 0, Native.SRCCOPY);
        Native.SelectObject(mem, old);
        Native.DeleteObject(bmp);
        Native.DeleteDC(mem);
        Native.ReleaseDC(hwnd, dc);
    }

    /// <summary>The full window as it is now, kept for the move.</summary>
    private void TakeSnapshot()
    {
        FreeSnapshot();
        Native.GetClientRect(hwnd, out Native.RECT rc);
        snapW = rc.Right; snapH = rc.Bottom;
        IntPtr dc = Native.GetDC(hwnd);
        snapDc = Native.CreateCompatibleDC(dc);
        snapBmp = Native.CreateCompatibleBitmap(dc, snapW, snapH);
        snapOld = Native.SelectObject(snapDc, snapBmp);
        Native.ReleaseDC(hwnd, dc);
        Draw(snapDc, snapW, snapH, progress.Take(Rows), scale, fontText, fontBold, fontSmall);
    }

    private void FreeSnapshot()
    {
        if (snapDc == IntPtr.Zero) return;
        Native.SelectObject(snapDc, snapOld);
        Native.DeleteObject(snapBmp);
        Native.DeleteDC(snapDc);
        snapDc = snapBmp = snapOld = IntPtr.Zero;
    }

    internal static IntPtr Font(int points, int weight, float scale) =>
        Native.CreateFontW(-(int)Math.Round(points * 96f / 72f * scale), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");

    // ---- drawing (shared with the preview tool) ---------------------------------------------------------------------------

    internal static class Layout
    {
        public const int Width = 460, SummaryWidth = 400, SummaryHeight = 94;
        public static int Height(int rows) => 14 + 24 + 22 + 22 + rows * 19 + 8 + 20 + 14;
    }

    private static readonly uint Back = Native.Rgb(0x1E1F22), Edge = Native.Rgb(0x3F4147), Track = Native.Rgb(0x34373B), Amber = Native.Rgb(0xD5A876),
        Text = Native.Rgb(0xD7D7D7), Dim = Native.Rgb(0x949AA1), Green = Native.Rgb(0x63B970), Red = Native.Rgb(0xC04A3C);

    /// <summary>Draws the full window's content into <paramref name="dc"/> (size w x h pixels, at <paramref name="scale"/> x 96 dpi).</summary>
    internal static void Draw(IntPtr dc, int w, int h, Progress.Snapshot s, float scale, IntPtr fontText, IntPtr fontBold, IntPtr fontSmall)
    {
        int S(int v) => (int)Math.Round(v * scale);
        Fill(dc, 0, 0, w, h, Edge);
        Fill(dc, 1, 1, w - 2, h - 2, Back);
        Native.SetBkMode(dc, Native.TRANSPARENT);
        int pad = S(14), y = S(12);

        Put(dc, fontBold, Amber, "R.A.T.S. Keel Loading", pad, y, w - 2 * pad - S(70), S(24), 0);
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
        if (!string.IsNullOrEmpty(LoadingWindow.PreparationStatus) && s.Recent.Count < Rows)
            Put(dc, fontText, Dim, LoadingWindow.PreparationStatus, pad, y, w - 2 * pad, rowH, Native.DT_END_ELLIPSIS);
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
            foot = "Starting " + s.CurrentIndex + "/" + s.Total + ": " + Shorten(s.Current, 42) + (s.CurrentFor >= 1 ? "  -  " + s.CurrentFor.ToString("0") + " s" : "");
            footColor = s.CurrentFor >= 5 ? Amber : Text;
        }
        else if (LoadingWindow.WaitingFor != null)
        {
            foot = "Waiting for you: " + LoadingWindow.WaitingFor;
            footColor = Amber;
        }
        else if (s.Idle >= 2.5)
        {
            foot = "Still loading  -  " + s.Idle.ToString("0") + " s since the last message" + (string.IsNullOrEmpty(s.LastEvent) ? "" : ": " + Shorten(s.LastEvent, 34));
            footColor = Amber;
        }
        else { foot = "Click to hide."; footColor = Dim; }
        Put(dc, fontSmall, footColor, foot, pad, y, w - 2 * pad, S(20), Native.DT_END_ELLIPSIS);
    }

    /// <summary>The summary card: what Keel did, how long the game took, what to look at.</summary>
    internal static void DrawSummary(IntPtr dc, int w, int h, Progress.Snapshot s, float scale, IntPtr fontText, IntPtr fontBold, IntPtr fontSmall)
    {
        int S(int v) => (int)Math.Round(v * scale);
        Fill(dc, 0, 0, w, h, Edge);
        Fill(dc, 1, 1, w - 2, h - 2, Back);
        string alert = LoadingWindow.Alert;
        Fill(dc, 1, 1, S(4), h - 2, alert != null || s.WithErrors + s.NotStarted > 0 ? Amber : Green);      // a colour bar down the left edge: green = clean start
        Native.SetBkMode(dc, Native.TRANSPARENT);
        int pad = S(14), y = S(8), lineH = S(20);

        string ver = string.IsNullOrEmpty(s.KeelVersion) ? "" : " " + s.KeelVersion;
        string head = "Keel" + ver + " started " + s.Running + (s.Running == 1 ? " mod" : " mods") + (s.KeelSeconds >= 1 ? " in " + s.KeelSeconds.ToString("0") + " s" : "");
        Put(dc, fontBold, Text, head, pad, y, w - 2 * pad, S(24), Native.DT_END_ELLIPSIS);
        y += S(24);

        var sb = new StringBuilder();
        sb.Append("Game ready after ").Append(Clock(s.DoneElapsed > 0 ? s.DoneElapsed : s.Elapsed));
        if (s.Skipped > 0) sb.Append("  -  ").Append(s.Skipped).Append(" skipped");
        Put(dc, fontText, Dim, sb.ToString(), pad, y, w - 2 * pad, lineH, Native.DT_END_ELLIPSIS);
        y += lineH;

        string line3;
        uint c3;
        if (alert != null)
        {
            line3 = alert + " - click for details";
            c3 = Amber;
        }
        else if (s.WithErrors + s.NotStarted > 0)
        {
            line3 = (s.WithErrors + s.NotStarted) + (s.WithErrors + s.NotStarted == 1 ? " mod has" : " mods have") + " problems - click for the report";
            c3 = Amber;
        }
        else { line3 = "No problems  -  click for the report"; c3 = Dim; }
        Put(dc, fontSmall, c3, line3, pad, y, w - 2 * pad, lineH, Native.DT_END_ELLIPSIS);
        y += lineH;
        Put(dc, fontSmall, Edge, "Made by Kanalratte", pad, y, S(CreditWidth), lineH, Native.DT_END_ELLIPSIS);      // quiet; a click opens his Steam profile
    }

    public const int CreditWidth = 130;
    private const string ProfileUrl = "https://steamcommunity.com/profiles/76561198169349193";

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
