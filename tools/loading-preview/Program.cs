using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SeaPowerKeel.Loading;

internal static class Program
{
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPels, biYPels, biClrUsed, biClrImportant;
    }

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "live") return Live();
        if (args.Length < 2) { Console.WriteLine("usage: preview <Keel-log.txt> <out folder> [scale]  |  preview live"); return 1; }
        float scale = args.Length > 2 ? float.Parse(args[2], CultureInfo.InvariantCulture) : 1f;
        Directory.CreateDirectory(args[1]);

        // "21:14:45.141 +41.826s [Info    : R.A.T.S. Keel] RUNNING: ..."
        var line = new Regex(@"^\d\d:\d\d:\d\d\.\d+ \+(\d+\.\d+)s \[(\w+)\s*: (.+?)\] (.*)$");
        double now = 0;
        var progress = new Progress(() => now);
        var events = new List<(double t, string src, string text)>();
        foreach (string raw in File.ReadLines(args[0]))
        {
            Match m = line.Match(raw);
            if (!m.Success) continue;
            events.Add((double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[3].Value, m.Groups[4].Value));
            if (events.Count > 4000) break;
        }

        // replay: draw at chosen moments (seconds into the game process)
        double[] shots = { 5, 12, 20, 33, 45, 58, 70, 80, 88.8, 92 };
        int next = 0, planned = 0, index = 0;
        double prevT = 0;
        foreach (var ev in events)
        {
            while (next < shots.Length && shots[next] < ev.t)
            {
                now = shots[next];
                Render(progress, scale, Path.Combine(args[1], "t" + ((int)shots[next]).ToString("000") + ".bmp"));
                next++;
            }
            now = ev.t;
            progress.Feed(ev.src, ev.text);
            // Logs from before Keel 0.1.5 have no "STARTING i/n" lines: make them the way Keel now does, right when the step before is
            // done (the first one when Keel has found its plugins), naming the next mod that logs "RUNNING".
            bool keel = ev.src.Contains("Keel");
            if (keel && ev.text.StartsWith("Found ")) planned = events.FindAll(e => e.src.Contains("Keel") && e.text.StartsWith("RUNNING: ")).Count;
            if (keel && planned > 0 && (ev.text.StartsWith("Found ") || ev.text.StartsWith("RUNNING: ")))
            {
                int from = events.IndexOf(ev) + 1;
                for (int j = from; j < events.Count; j++)
                {
                    if (!events[j].src.Contains("Keel") || !events[j].text.StartsWith("RUNNING: ")) continue;
                    string name = events[j].text.Substring(9);
                    int paren = name.LastIndexOf(" (", StringComparison.Ordinal);
                    progress.Feed(ev.src, "STARTING " + (++index) + "/" + planned + ": " + (paren > 0 ? name.Substring(0, paren) : name));
                    break;
                }
            }
            prevT = ev.t;
        }
        while (next < shots.Length)
        {
            now = shots[next];
            Render(progress, scale, Path.Combine(args[1], "t" + ((int)shots[next]).ToString("000") + ".bmp"));
            next++;
        }
        Console.WriteLine("events replayed: " + events.Count);
        return 0;
    }

    /// <summary>Smoke test of the real window thread: opens it far off every monitor, feeds a few lines, ends it with Keel's "Done" line.</summary>
    private static int Live()
    {
        var start = DateTime.Now;
        var progress = new Progress(() => (DateTime.Now - start).TotalSeconds);
        var overlay = new Overlay(progress, new OverlayOptions { Offscreen = true, LingerSeconds = 0.5 });
        string warning = null;
        overlay.Start(m => warning = m);
        for (int i = 0; i < 50 && !overlay.Opened && warning == null; i++) System.Threading.Thread.Sleep(100);
        Console.WriteLine("opened: " + overlay.Opened + (warning != null ? "  warning: " + warning : ""));
        progress.Feed("R.A.T.S. Keel", "Found 3 plugin(s) in 3 DLL(s) (10 ms).");
        progress.Feed("R.A.T.S. Keel", "RUNNING: Test Mod 1.0 (Workshop 1)");
        System.Threading.Thread.Sleep(1500);
        progress.Feed("R.A.T.S. Keel", "Done: 3 running. Report: x");
        for (int i = 0; i < 50 && !overlay.Closed; i++) System.Threading.Thread.Sleep(100);
        Console.WriteLine("closed by itself after Done + linger: " + overlay.Closed);
        return overlay.Opened && overlay.Closed ? 0 : 2;
    }

    private static void Render(Progress progress, float scale, string path)
    {
        int w = (int)(Overlay.Layout.Width * scale), h = (int)(Overlay.Layout.Height(8) * scale);
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr mem = Native.CreateCompatibleDC(screen);
        IntPtr bmp = Native.CreateCompatibleBitmap(screen, w, h);
        IntPtr old = Native.SelectObject(mem, bmp);
        IntPtr f1 = Overlay.Font(12, 400, scale), f2 = Overlay.Font(14, 700, scale), f3 = Overlay.Font(10, 400, scale);
        Overlay.Draw(mem, w, h, progress.Take(8), scale, f1, f2, f3);

        var bi = new BITMAPINFO { biSize = Marshal.SizeOf(typeof(BITMAPINFO)), biWidth = w, biHeight = h, biPlanes = 1, biBitCount = 32 };
        var bits = new byte[w * h * 4];
        Native.SelectObject(mem, old);
        GetDIBits(mem, bmp, 0, (uint)h, bits, ref bi, 0);

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((byte)'B'); bw.Write((byte)'M'); bw.Write(54 + bits.Length); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)32); bw.Write(0); bw.Write(bits.Length); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        bw.Write(bits);

        foreach (IntPtr f in new[] { f1, f2, f3 }) Native.DeleteObject(f);
        Native.DeleteObject(bmp);
        Native.DeleteDC(mem);
        Native.ReleaseDC(IntPtr.Zero, screen);
    }
}
