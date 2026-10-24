using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Winawdl.Model;

namespace Winawdl.Radio;

/// <summary>Starts and stops the radio from the app, which itself has no
/// administrator rights: it starts a second copy of itself that asks Windows
/// for them, and signals that copy through a named event.</summary>
public sealed class RadioControl : IRadio
{
    private const int CancelledByUser = 1223;
    private const int TailBytes = 4096;
    private const int MostShownChars = 280;
    private const string LeftOff = "Wi-Fi was left off when the radio last stopped.";

    private readonly string? _repo;
    private readonly DispatcherTimer _watch;
    private Process? _supervisor;
    private Process? _giver;
    private EventWaitHandle? _stop;
    private int _starts;

    /// <summary>With a card, the radio moves it (an installed copy), and a card that winawdl's
    /// driver still holds when the app starts is said at once.</summary>
    public RadioControl(Dispatcher dispatcher, string? repo, ICard? card = null)
    {
        _repo = repo;
        SwapsCard = card is not null;
        _watch = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
                                     (_, _) => Look(), dispatcher) { IsEnabled = false };
        if (card?.Holder() == CardHolder.Winawdl) (Phase, LastLine) = (RadioPhase.WifiLeftOff, LeftOff);
    }

    public RadioPhase Phase { get; private set; }
    public string LastLine { get; private set; } = "";
    public string LogFolder => RadioSupervisor.LogFolder;
    public bool FromSource => _repo is not null && Engine.Repo.IsSourceTree(_repo);
    public bool SwapsCard { get; }
    public event Action? Changed;

    private void Become(RadioPhase phase, string lastLine = "")
    {
        if (Phase == phase && LastLine == lastLine) return;
        Phase = phase;
        LastLine = lastLine;
        Changed?.Invoke();
    }

    public void Start()
    {
        if (_supervisor is not null || _giver is not null) return;
        if (_repo is null || !File.Exists(RadioSupervisor.RadioProgram(_repo))
                          || !File.Exists(RadioSupervisor.AwdlProgram(_repo)))
        {
            Become(RadioPhase.NotBuilt);
            return;
        }
        var stopName = $"winawdl-radio-stop-{Environment.ProcessId}-{++_starts}";
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\" + stopName);
        try
        {
            _supervisor = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas",
                Arguments = $"{RadioSupervisor.Flag} {Environment.ProcessId} {stopName}",
            });
        }
        catch (Win32Exception error) when (error.NativeErrorCode == CancelledByUser)
        {
            Become(RadioPhase.Refused);
            return;
        }
        catch (Win32Exception error)
        {
            Become(RadioPhase.Failed, error.Message);
            return;
        }
        Become(RadioPhase.Starting);
        _watch.Start();
    }

    public void Stop()
    {
        if (_supervisor is null) return;
        _stop?.Set();
        Become(RadioPhase.Stopping);
    }

    public void GiveWifiBack()
    {
        if (_supervisor is not null || _giver is not null) return;
        try
        {
            _giver = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", Arguments = WifiCard.GiveBackFlag,
            });
        }
        catch (Win32Exception error) when (error.NativeErrorCode == CancelledByUser)
        {
            return;
        }
        catch (Win32Exception error)
        {
            Become(RadioPhase.WifiLeftOff, error.Message);
            return;
        }
        Become(RadioPhase.GivingBack);
        _watch.Start();
    }

    /// <summary>The engine saw the AWDL adapter come up or go away.</summary>
    public void Linked(bool up)
    {
        if (up && Phase == RadioPhase.Starting) Become(RadioPhase.Running);
    }

    /// <summary>The phase the elevated copy's step stands for while it runs, if any.</summary>
    internal static RadioPhase? During(string statusJson) => Field(statusJson, "step") switch
    {
        "taking" => RadioPhase.TakingCard,
        "running" => RadioPhase.Starting,
        "giving" => RadioPhase.GivingBack,
        _ => null,
    };

    /// <summary>The phase once the elevated copy has ended, and what to say. An empty line with a
    /// failure leaves the daemon's own last line to be said.</summary>
    internal static (RadioPhase Phase, string Line) Ended(string statusJson, bool wasStopping)
    {
        var why = Field(statusJson, "why");
        if (Field(statusJson, "wifi") == "off") return (RadioPhase.WifiLeftOff, why);
        if (Field(statusJson, "ended") == "card") return (RadioPhase.Failed, why);
        return wasStopping || Field(statusJson, "ended") is "stopped" or "owner"
            ? (RadioPhase.Off, "")
            : (RadioPhase.Failed, "");
    }

    private static string Field(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private void Look()
    {
        if (_giver is not null)
        {
            LookAtGiver();
            return;
        }
        if (_supervisor is null) return;
        bool ended;
        try
        {
            ended = _supervisor.WaitForExit(0);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            ended = true;
        }
        if (!ended)
        {
            var during = During(Status());
            if (during == RadioPhase.TakingCard && Phase == RadioPhase.Starting) Become(RadioPhase.TakingCard);
            else if (during == RadioPhase.Starting && Phase is RadioPhase.Starting or RadioPhase.TakingCard)
                Become(RadioPhase.Starting, Tail("radio.log"));
            else if (during == RadioPhase.GivingBack && Phase == RadioPhase.Stopping) Become(RadioPhase.GivingBack);
            return;
        }
        _watch.Stop();
        _supervisor.Dispose();
        _supervisor = null;
        _stop?.Dispose();
        _stop = null;
        var status = Status();
        var (phase, line) = Ended(status, Phase is RadioPhase.Stopping or RadioPhase.GivingBack);
        if (phase == RadioPhase.Failed && line.Length == 0)
            line = Tail(Field(status, "ended") == "awdld" ? "awdld.log" : "radio.log");
        Become(phase, line);
    }

    private void LookAtGiver()
    {
        bool ended;
        try
        {
            ended = _giver!.WaitForExit(0);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            ended = true;
        }
        if (!ended) return;
        _watch.Stop();
        var code = _giver!.ExitCode;
        _giver.Dispose();
        _giver = null;
        if (code == 0)
        {
            Become(RadioPhase.Off);
            return;
        }
        // What --give-wifi-back wrote last: "<date> <time> could not give Wi-Fi back: <why>".
        var said = Tail("wifi.log");
        var at = said.IndexOf(": ", StringComparison.Ordinal);
        var why = at >= 0 ? said[(at + 2)..] : "";
        Become(RadioPhase.WifiLeftOff, why.Length > 0 ? why : LeftOff);
    }

    private string Status()
    {
        try
        {
            using var file = new FileStream(Path.Combine(LogFolder, RadioSupervisor.StatusFile), FileMode.Open,
                                            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(file).ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>The last line a daemon wrote, which while it starts says what it
    /// is doing and after a failure says why.</summary>
    private string Tail(string logName)
    {
        try
        {
            using var log = new FileStream(Path.Combine(LogFolder, logName), FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite | FileShare.Delete);
            log.Seek(-Math.Min(TailBytes, log.Length), SeekOrigin.End);
            var text = new StreamReader(log, Encoding.UTF8).ReadToEnd();
            var line = text.Split('\n').Select(part => part.Trim()).LastOrDefault(part => part.Length > 0) ?? "";
            return line.Length > MostShownChars ? line[..MostShownChars] + "…" : line;
        }
        catch (IOException)
        {
            return "";
        }
    }
}
