using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Winawdl.Engine;

namespace Winawdl.Radio;

public sealed record Daemon(string Name, string Program, string[] Arguments);

/// <summary>The app started again with administrator rights and no window: it runs
/// the radio daemon and the AWDL daemon, and stops both when the app asks, when
/// the app is gone, or when either of them ends.</summary>
public static class RadioSupervisor
{
    public const string Flag = "--radio";
    public const string Channel = "44";
    public const string StatusFile = "radio.status";
    private const int InterruptWaitMs = 5000;
    private const int OwnLogLimitBytes = 64 * 1024;

    /// <summary>Where the daemons' output goes. The same for this elevated copy
    /// and for the app that reads it, whoever each runs as, and not a place the
    /// app could talk this copy into writing elsewhere.</summary>
    public static string LogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "winawdl", "logs");

    public static string RadioProgram(string root) =>
        Found(root, @"radio\winawdl_radio.exe", @"build\radio\Release\winawdl_radio.exe");
    public static string AwdlProgram(string root) => Found(root, @"awdl\awdld.exe", @"build\awdl\awdld.exe");

    /// <summary>A program where an installed copy keeps it, or else where a
    /// build of the repository leaves it.</summary>
    private static string Found(string root, string installed, string built)
    {
        var place = Path.Combine(root, installed);
        return File.Exists(place) ? place : Path.Combine(root, built);
    }

    /// <summary>The elevated entry point: `--radio owner-pid stop-event-name`.</summary>
    public static int Run(string[] arguments)
    {
        if (arguments.Length != 2 || !int.TryParse(arguments[0], out var owner) || Repo.Find() is not { } root)
            return 2;
        // The same two commands scripts\awdl-run.ps1 runs with its defaults.
        return Supervise(owner, arguments[1],
                         new Daemon("radio", RadioProgram(root), [Channel]),
                         new Daemon("awdld", AwdlProgram(root), ["-c", Channel]),
                         LogFolder);
    }

    /// <summary>Returns 0 after a stop that was asked for, 3 when a daemon ended by itself, 4 when
    /// the Wi-Fi card could not be taken, 5 when it could not be given back. With a card, it is
    /// taken before the daemons start and given back once both have ended; radio.status says
    /// which step is under way.</summary>
    public static int Supervise(int ownerPid, string stopName, Daemon radio, Daemon awdl, string logs,
                                ICard? card = null, string inf = "")
    {
        Directory.CreateDirectory(logs);
        var status = Path.Combine(logs, StatusFile);
        File.Delete(status);
        if (!EventWaitHandle.TryOpenExisting(@"Local\" + stopName, out var stop)) return 2;
        Process owner;
        try
        {
            owner = Process.GetProcessById(ownerPid);
        }
        catch (ArgumentException)
        {
            return 2;
        }

        void Say(Dictionary<string, string> what) => File.WriteAllText(status, JsonSerializer.Serialize(what));
        var wifi = card is null ? "untouched" : "back";
        var why = "";
        if (card is not null)
        {
            Say(new() { ["step"] = "taking" });
            try
            {
                card.Take(inf);
            }
            catch (CardException error)
            {
                why = error.Message;
                // Half taken: it goes back before anything else is tried.
                try
                {
                    if (card.Holder() == CardHolder.Winawdl) card.GiveBack();
                }
                catch (CardException)
                {
                    wifi = "off";
                }
                File.AppendAllLines(Path.Combine(logs, "supervisor.log"),
                                    [$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} the Wi-Fi card could not be taken: {why}"]);
                Say(new() { ["step"] = "done", ["ended"] = "card", ["wifi"] = wifi, ["why"] = why });
                return 4;
            }
        }

        using var job = Native.KillOnCloseJob();
        // A process started by one that ignores Ctrl+C ignores it too, and what
        // started this one may have: say plainly that it is not ignored here.
        Native.SetConsoleCtrlHandler(IntPtr.Zero, false);
        var running = new List<(Daemon Daemon, Process Process, StreamWriter Log)>();
        string ended;
        try
        {
            foreach (var daemon in (Daemon[])[radio, awdl])
            {
                var (process, log) = Launch(daemon, logs);
                Native.AssignProcessToJobObject(job, process.Handle);
                running.Add((daemon, process, log));
            }
            if (card is not null) Say(new() { ["step"] = "running" });
            WaitHandle[] any = [stop, Waitable(owner), .. running.Select(one => Waitable(one.Process))];
            ended = WaitHandle.WaitAny(any) switch
            {
                0 => "stopped",
                1 => "owner",
                var index => running[index - 2].Daemon.Name,
            };
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            ended = "launch";
            File.AppendAllText(Path.Combine(logs, "radio.log"), error.Message + Environment.NewLine);
        }

        // The AWDL daemon first: the radio daemon ends by itself when its client leaves.
        var notes = new List<string> { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ended by: {ended}" };
        foreach (var (daemon, process, _) in Enumerable.Reverse(running))
        {
            if (!process.HasExited) notes.Add($"{daemon.Name}: {Interrupt(process)}");
            if (process.WaitForExit(InterruptWaitMs)) continue;
            notes.Add($"{daemon.Name}: still running {InterruptWaitMs} ms later, killed");
            process.Kill();
        }
        foreach (var (_, process, log) in running)
        {
            process.WaitForExit();
            lock (log) log.Dispose();
        }
        // Nothing holds the card any more: only now can Windows have it back without a restart.
        if (card is not null)
        {
            Say(new() { ["step"] = "giving" });
            try
            {
                card.GiveBack();
                notes.Add("the Wi-Fi card: given back");
            }
            catch (CardException error)
            {
                (wifi, why) = ("off", error.Message);
                notes.Add($"the Wi-Fi card: could not be given back: {why}");
            }
        }
        var own = new FileInfo(Path.Combine(logs, "supervisor.log"));
        if (own.Exists && own.Length > OwnLogLimitBytes) own.Delete();
        File.AppendAllLines(own.FullName, notes);
        Say(new() { ["step"] = "done", ["ended"] = ended, ["wifi"] = wifi, ["why"] = why });
        return wifi == "off" ? 5 : ended is "stopped" or "owner" ? 0 : 3;
    }

    private static (Process, StreamWriter) Launch(Daemon daemon, string logs)
    {
        var log = new StreamWriter(new FileStream(Path.Combine(logs, daemon.Name + ".log"), FileMode.Create,
                                                  FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
        var start = new ProcessStartInfo(daemon.Program)
        {
            // A console of its own, never shown: without one there is nothing
            // to send Ctrl+C through, and the daemon could only be killed.
            UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            // Both daemons write UTF-8; read as the console's code page, a dash in a line comes out as three wrong letters.
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(daemon.Program),
        };
        foreach (var argument in daemon.Arguments) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        void Write(object _, DataReceivedEventArgs line)
        {
            if (line.Data is null) return;
            lock (log)
            {
                try
                {
                    log.WriteLine(line.Data);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
        process.OutputDataReceived += Write;
        process.ErrorDataReceived += Write;
        try
        {
            process.Start();
        }
        catch
        {
            log.Dispose();
            throw;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return (process, log);
    }

    private static WaitHandle Waitable(Process process) =>
        new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process.Handle, ownsHandle: false) };

    /// <summary>Ctrl+C, which both daemons take as the cue to leave cleanly.</summary>
    private static string Interrupt(Process process)
    {
        if (!Native.AttachConsole(process.Id))
            return $"its console could not be joined (error {Marshal.GetLastWin32Error()})";
        Native.SetConsoleCtrlHandler(IntPtr.Zero, true);
        var sent = Native.GenerateConsoleCtrlEvent(0, 0);
        var error = Marshal.GetLastWin32Error();
        Native.FreeConsole();
        // The event reaches this process as well, a moment later; it is ignored until then.
        Thread.Sleep(200);
        Native.SetConsoleCtrlHandler(IntPtr.Zero, false);
        return sent ? "sent Ctrl+C" : $"Ctrl+C could not be sent (error {error})";
    }

    private static class Native
    {
        private const int KillOnJobClose = 0x2000;
        private const int ExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        public static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GenerateConsoleCtrlEvent(int ctrlEvent, int processGroup);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, int length);

        [DllImport("kernel32.dll")]
        public static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public int LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public int ActiveProcessLimit;
            public UIntPtr Affinity;
            public int PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        /// <summary>A job whose processes end with this one, however it ends.</summary>
        public static SafeFileHandle KillOnCloseJob()
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            var info = new ExtendedLimits { Basic = { LimitFlags = KillOnJobClose } };
            SetInformationJobObject(job, ExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimits>());
            return job;
        }
    }
}
