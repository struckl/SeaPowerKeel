using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SeaPowerKeel;

/// <summary>
/// Catches a native crash the moment it happens: a Windows unhandled-exception filter writes what crashed and the managed stack of
/// the crashing thread to <c>Keel-crash-stack.txt</c>, then hands the crash on to whoever had the filter before (the game's crash
/// reporter). The game still dies, nothing can save a process in that state, but the watcher and the next start can name the mod.
/// <para>The code runs in a dying process. Everything it needs is prepared when it is installed (file open, API calls made once), and
/// a native timer ends the process if the write hangs, for example because the crash happened while the runtime held a lock.</para>
/// </summary>
internal static class CrashCatcher
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Filter(IntPtr exceptionPointers);

    private const uint TimerFlags = 0x00000008 | 0x00000020;   // WT_EXECUTEONLYONCE | WT_EXECUTEINTIMERTHREAD
    private const uint HangLimitMs = 4000;

    [DllImport("kernel32.dll")] private static extern IntPtr SetUnhandledExceptionFilter(IntPtr filter);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder name, uint size);
    [DllImport("kernel32.dll")] private static extern bool CreateTimerQueueTimer(out IntPtr timer, IntPtr queue, IntPtr callback, IntPtr parameter, uint due, uint period, uint flags);
    [DllImport("kernel32.dll")] private static extern bool DeleteTimerQueueTimer(IntPtr queue, IntPtr timer, IntPtr completion);

    private static Filter ours;             // kept in a field: the collector must never take it
    private static IntPtr oursPointer, terminate;
    private static IntPtr previousPointer;
    private static Filter previous;
    private static FileStream file;
    private static int entered;

    /// <summary>Installs the filter. Null when done, else why not.</summary>
    public static string Install(string path)
    {
        try
        {
            terminate = GetProcAddress(GetModuleHandleW("kernel32.dll"), "TerminateProcess");
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            ours = OnCrash;
            oursPointer = Marshal.GetFunctionPointerForDelegate(ours);

            // A rehearsal, so nothing has to be compiled or bound during a crash.
            Compose(IntPtr.Zero);
            ModuleAt(terminate);
            Write("");
            if (Arm(uint.MaxValue - 1, out IntPtr timer))
                DeleteTimerQueueTimer(IntPtr.Zero, timer, IntPtr.Zero);

            KeepInstalled();
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Called now and then: whoever installs a filter later (a crash reporter starting up) is chained instead of replacing Keel.</summary>
    public static void KeepInstalled()
    {
        if (oursPointer == IntPtr.Zero)
            return;
        IntPtr was = SetUnhandledExceptionFilter(oursPointer);
        if (was != oursPointer && was != IntPtr.Zero && was != previousPointer)
        {
            previousPointer = was;
            previous = Marshal.GetDelegateForFunctionPointer<Filter>(was);
        }
    }

    // TerminateProcess(handle, code) has the shape of a timer callback(parameter, fired): no managed code runs if the write hangs.
    private static bool Arm(uint dueMs, out IntPtr timer) =>
        CreateTimerQueueTimer(out timer, IntPtr.Zero, terminate, new IntPtr(-1), dueMs, 0, TimerFlags);

    private static int OnCrash(IntPtr pointers)
    {
        if (Interlocked.Exchange(ref entered, 1) != 0)
            return 0;   // EXCEPTION_CONTINUE_SEARCH: a second crash, or a chain that came back here
        IntPtr timer = IntPtr.Zero;
        try
        {
            Arm(HangLimitMs, out timer);
            Write(Compose(pointers));
        }
        catch (Exception)
        {
            // whatever fails here, the crash goes on to the game's reporter
        }
        if (timer != IntPtr.Zero)
            DeleteTimerQueueTimer(IntPtr.Zero, timer, IntPtr.Zero);
        return previous != null ? previous(pointers) : 0;
    }

    private static void Write(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        file.Write(bytes, 0, bytes.Length);
        file.Flush();
    }

    private static string Compose(IntPtr pointers)
    {
        var sb = new StringBuilder(4096);
        sb.Append(CrashText.CrashPrefix).Append(Describe(pointers)).Append('\n');
        sb.Append(new StackTrace(false).ToString().Replace("\r\n", "\n"));
        return sb.ToString();
    }

    private static string Describe(IntPtr pointers)
    {
        if (pointers == IntPtr.Zero)
            return "none";
        IntPtr record = Marshal.ReadIntPtr(pointers);       // EXCEPTION_POINTERS.ExceptionRecord
        uint code = (uint)Marshal.ReadInt32(record);         // EXCEPTION_RECORD.ExceptionCode
        IntPtr address = Marshal.ReadIntPtr(record, 16);     // EXCEPTION_RECORD.ExceptionAddress (64-bit)
        string what = code switch
        {
            0xC0000005 => "access violation",
            0xC00000FD => "stack overflow",
            0xC0000409 => "stack buffer overrun",
            0xC000001D => "illegal instruction",
            0xE06D7363 => "C++ exception",
            _ => "exception",
        };
        return $"{what} (0x{code:X8}) in {ModuleAt(address)}";
    }

    private static string ModuleAt(IntPtr address)
    {
        const uint FromAddress = 0x4, UnchangedRefCount = 0x2;
        if (!GetModuleHandleExW(FromAddress | UnchangedRefCount, address, out IntPtr module))
            return "managed or unknown code";
        var name = new StringBuilder(520);
        return GetModuleFileNameW(module, name, (uint)name.Capacity) > 0 ? Path.GetFileName(name.ToString()) : "an unnamed module";
    }
}
