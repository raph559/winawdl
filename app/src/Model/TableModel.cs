using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Input;

namespace Winawdl.Model;

/// <summary>What fills the far side of the table.</summary>
public enum Across { EngineDown, RadioOff, RadioStarting, RadioProblem, Nearby }

/// <summary>Everything the window shows: the devices across the table, what they
/// want to send, and the files on this side.</summary>
public sealed class TableModel : Observable
{
    public const string Project = "https://github.com/raph559/winawdl";
    private const int MostReceived = 24;
    public const int MostTrayText = 127;
    private static readonly TimeSpan SentShownFor = TimeSpan.FromSeconds(4);
    private static readonly double[] Turns = [-2.5, 1.5, -1, 2, -1.5];

    private readonly IEngine _engine;
    private readonly IRadio _radio;
    private readonly IDesk _desk;
    private readonly AppSettings _settings;
    private int _sends;
    private bool _engineUp, _wantsLook, _linkUp, _looking, _beaconOn = true, _receiving, _settingsOpen;
    private bool _radioHasRun;
    private string _beaconWhy = "", _receivingProblem = "", _heldNotice = "", _radioNotice = "";
    private string _engineProblem = "", _engineDetail = "", _nameDraft = "", _nameError = "", _folderError = "";
    private int _unwilling;

    public TableModel(IEngine engine, IRadio radio, IDesk desk, AppSettings settings)
    {
        _engine = engine;
        _radio = radio;
        _desk = desk;
        _settings = settings;
        _nameDraft = Name;
        _engine.Said += OnSaid;
        _engine.Stopped += OnEngineStopped;
        _radio.Changed += OnRadioChanged;

        TurnOnRadio = new Command(() => RadioOn = true);
        ChooseFiles = new Command(() => Hold(_desk.PickFiles()));
        Paste = new Command(() => Hold(_desk.ClipboardFiles()));
        ClearHeld = new Command(() =>
        {
            Held.Clear();
            HeldChanged("");
        });
        OpenSettings = new Command(() => SettingsOpen = true);
        CloseSettings = new Command(() =>
        {
            CommitName();
            SettingsOpen = false;
        });
        ChangeFolder = new Command(PickFolder);
        OpenFolder = new Command(() => _desk.Open(Folder));
        OpenLog = new Command(() => _desk.Open(_radio.LogFolder));
        OpenProject = new Command(() => _desk.Open(Project));
        StartEngine = new Command(StartTheEngine);
        PanelAction = new Command(RunPanelAction);
        foreach (var device in _settings.AlwaysAccept) Trusted.Add(Listed(device));
    }

    public ObservableCollection<PeerModel> Peers { get; } = [];
    public ObservableCollection<IncomingModel> Incoming { get; } = [];
    public ObservableCollection<TrustedModel> Trusted { get; } = [];
    public ObservableCollection<FileItem> Held { get; } = [];
    public ObservableCollection<FileItem> Received { get; } = [];
    public ObservableCollection<string> Notices { get; } = [];

    public ICommand TurnOnRadio { get; }
    public ICommand ChooseFiles { get; }
    public ICommand Paste { get; }
    public ICommand ClearHeld { get; }
    public ICommand OpenSettings { get; }
    public ICommand CloseSettings { get; }
    public ICommand ChangeFolder { get; }
    public ICommand OpenFolder { get; }
    public ICommand OpenLog { get; }
    public ICommand OpenProject { get; }
    public ICommand StartEngine { get; }
    public ICommand PanelAction { get; }

    /// <summary>Something wants an answer; the window may be out of sight.</summary>
    public event Action<string>? Attention;
    /// <summary>Files have just been stored.</summary>
    public event Action? Landed;
    /// <summary>What a device sent has just been stored; the window may be out of sight.</summary>
    public event Action<ArrivalModel>? Arrived;

    public string Version { get; } =
        "winawdl " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");

    public string Name => _settings.Name ?? Environment.MachineName;
    public string Folder => _settings.Folder ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public void StartTheEngine()
    {
        _engineProblem = "";
        if (!Directory.Exists(Folder)) _settings.Folder = null;
        ChangedAll();
        _engine.Start(Name, Folder, _settings.Ask);
    }

    /// <summary>The window came into view or went out of it. Devices are only
    /// looked for while someone can see them.</summary>
    public void SetVisible(bool visible)
    {
        _wantsLook = visible;
        if (_engineUp) _engine.Tell(new() { ["do"] = "look", ["on"] = visible });
    }

    public Across Across
    {
        get
        {
            if (_engineProblem.Length > 0) return Across.EngineDown;
            if (_linkUp) return Across.Nearby;
            return _radio.Phase switch
            {
                RadioPhase.Starting or RadioPhase.TakingCard => Across.RadioStarting,
                RadioPhase.Refused or RadioPhase.NotBuilt or RadioPhase.Failed or RadioPhase.WifiLeftOff
                    => Across.RadioProblem,
                _ => Across.RadioOff,
            };
        }
    }

    public bool ShowsPanel => Across != Across.Nearby;
    public bool ShowsNearby => Across == Across.Nearby;
    public bool ShowsPeers => ShowsNearby && Peers.Count > 0;
    public bool ShowsNobody => ShowsNearby && Peers.Count == 0;

    public string PanelTitle => Across switch
    {
        Across.EngineDown => _engineProblem,
        Across.RadioStarting => "Starting the radio",
        Across.RadioProblem => _radio.Phase switch
        {
            RadioPhase.Refused => "The radio stayed off",
            RadioPhase.WifiLeftOff => "Wi-Fi is still off",
            RadioPhase.NotBuilt => _radio.FromSource
                ? "The radio has not been built yet"
                : "The radio's programs are missing",
            _ => _radioHasRun ? "The radio stopped" : "The radio did not start",
        },
        _ => _radio.Phase switch
        {
            RadioPhase.Stopping => "Stopping the radio",
            RadioPhase.GivingBack => "Giving Wi-Fi back",
            _ => "The radio is off",
        },
    };

    public string PanelBody => Across switch
    {
        Across.EngineDown => _engineDetail,
        Across.RadioStarting => _radio.Phase == RadioPhase.TakingCard
            ? "Taking the Wi-Fi card from Windows. Wi-Fi is off until the radio is turned off."
            : _radio.LastLine.Length > 0
                ? _radio.LastLine
                : "The Wi-Fi card is being set up for AirDrop.",
        Across.RadioProblem => _radio.Phase switch
        {
            RadioPhase.Refused => "Windows asked for permission and the answer was no.",
            RadioPhase.WifiLeftOff => $"{_radio.LastLine} Restarting the PC also gives it back.",
            RadioPhase.NotBuilt => _radio.FromSource
                ? "Run scripts\\build-radio.ps1 and scripts\\build-awdl.ps1 in the repository, then try again."
                : "This copy of winawdl is incomplete. Install it again.",
            _ => _radio.LastLine.Length > 0 ? _radio.LastLine : "It left nothing in its log.",
        },
        _ => _radio.Phase switch
        {
            RadioPhase.Stopping => "",
            RadioPhase.GivingBack => "The radio has stopped. The Wi-Fi card is going back to Windows.",
            _ => "Turn it on to see nearby iPhones, iPads and Macs, and to be seen by them. "
                 + (_radio.SwapsCard ? "It uses the Wi-Fi card, so Wi-Fi is off while the radio is on. " : "")
                 + "Windows asks for permission first.",
        },
    };

    public string PanelActionLabel => Across switch
    {
        Across.EngineDown => "Start it again",
        Across.RadioOff => _radio.Phase is RadioPhase.Stopping or RadioPhase.GivingBack ? "" : "Turn on the radio",
        Across.RadioProblem => _radio.Phase switch
        {
            RadioPhase.Refused => "Turn on the radio",
            RadioPhase.WifiLeftOff => "Give Wi-Fi back",
            _ => "Try again",
        },
        _ => "",
    };

    public bool PanelHasAction => PanelActionLabel.Length > 0;
    /// <summary>The press leads to a Windows permission prompt, which Windows marks with a shield.</summary>
    public bool PanelAsksWindows => PanelHasAction && Across != Across.EngineDown;
    public bool PanelHasLog => Across == Across.RadioProblem && _radio.Phase == RadioPhase.Failed;

    private void RunPanelAction()
    {
        if (Across == Across.EngineDown) StartTheEngine();
        else if (_radio.Phase == RadioPhase.WifiLeftOff) _radio.GiveWifiBack();
        else RadioOn = true;
    }

    public string NobodyTitle => _looking ? "No one nearby yet" : "Getting ready to look";
    public string NobodyBody => _looking
        ? "Set AirDrop to Everyone for 10 Minutes on the device (Settings, General, AirDrop) and keep "
          + "its screen on."
        : "Nearby devices are woken over Bluetooth first.";

    /// <summary>The radio switch. It also reads as on while the radio starts.</summary>
    public bool RadioOn
    {
        get => _linkUp || _radio.Phase is RadioPhase.TakingCard or RadioPhase.Starting or RadioPhase.Running;
        set
        {
            _radioNotice = "";
            // While the radio stops and Wi-Fi goes back, the card is still moving: nothing starts.
            if (value && !RadioOn && _radio.Phase is not (RadioPhase.Stopping or RadioPhase.GivingBack)) _radio.Start();
            else if (!value && _radio.Phase is RadioPhase.TakingCard or RadioPhase.Starting or RadioPhase.Running)
                _radio.Stop();
            else if (!value && _linkUp)
                _radioNotice = "The radio was started from a terminal. Stop it there with Ctrl+C.";
            Refresh();
        }
    }

    public string Presence => _linkUp && _receiving ? $"Visible as {Name}"
        : _linkUp ? "Not visible yet"
        : "Not visible";

    private void RefreshNotices()
    {
        var notices = new List<string>();
        if (_radioNotice.Length > 0) notices.Add(_radioNotice);
        if (_linkUp && !_receiving && _receivingProblem == "port_held")
            notices.Add("Another program is already receiving AirDrop on this PC. Close it "
                        + "(scripts\\airdrop-receive.ps1 in a terminal) and winawdl takes over.");
        else if (_linkUp && !_receiving && _receivingProblem.Length > 0)
            notices.Add($"Receiving could not start: {_receivingProblem}");
        if (_looking && !_beaconOn)
            notices.Add("Bluetooth could not wake nearby devices"
                        + (_beaconWhy.Length > 0 ? $" ({_beaconWhy})" : "")
                        + ". A device shows up here while its own share sheet is open.");
        // Said of what was seen: one that keeps its name to itself, or would not
        // have this PC's certificate. Contacts Only is the likely cause of both.
        if (_unwilling > 0)
            notices.Add(_unwilling == 1
                ? "A device nearby only answers devices it knows. Set its AirDrop to Everyone for 10 Minutes."
                : $"{_unwilling} devices nearby only answer devices they know. Set their AirDrop to "
                  + "Everyone for 10 Minutes.");
        if (!notices.SequenceEqual(Notices))
        {
            Notices.Clear();
            foreach (var notice in notices) Notices.Add(notice);
        }
    }

    private void Refresh()
    {
        RefreshNotices();
        foreach (var peer in Peers) peer.CanSend = Held.Count > 0;
        ChangedAll();
    }

    private void OnRadioChanged()
    {
        if (_radio.Phase == RadioPhase.Running) _radioHasRun = true;
        if (_radio.Phase == RadioPhase.Starting) _radioHasRun = false;
        Refresh();
    }

    private void OnEngineStopped(string why, string detail)
    {
        _engineUp = false;
        _linkUp = _looking = _receiving = false;
        Peers.Clear();
        Incoming.Clear();
        (_engineProblem, _engineDetail) = why switch
        {
            "no_python" => ("Python was not found",
                            "The part of winawdl that speaks AirDrop runs on Python 3.10 or newer. "
                            + "Install it from python.org, then start it again."),
            "no_engine" => ("winawdl's files are incomplete",
                            "The airdrop folder of the repository was not found above the folder "
                            + "this app runs from."),
            "no_certificate" => ("No certificate could be made", detail),
            _ => ("The AirDrop engine stopped",
                  detail.Length > 0 ? detail : "It ended without saying why."),
        };
        Refresh();
    }

    private static string Text(JsonElement said, string name) =>
        said.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long? Number(JsonElement said, string name) =>
        said.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static bool Flag(JsonElement said, string name) =>
        said.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private void OnSaid(JsonElement said)
    {
        switch (Text(said, "event"))
        {
            case "ready":
                _engineUp = true;
                AdoptSettings(said);
                _engine.Tell(new() { ["do"] = "look", ["on"] = _wantsLook });
                break;
            case "link":
                _linkUp = Flag(said, "up");
                _radio.Linked(_linkUp);
                if (!_linkUp)
                {
                    Peers.Clear();
                    Incoming.Clear();
                    _looking = _receiving = false;
                    _unwilling = 0;
                }
                break;
            case "receiving":
                _receiving = Flag(said, "on");
                _receivingProblem = Text(said, "reason");
                break;
            case "looking":
                _looking = Flag(said, "on");
                _beaconOn = !_looking || Flag(said, "beacon") || Text(said, "why") == "test receivers";
                _beaconWhy = Text(said, "why");
                break;
            case "unwilling":
                _unwilling = (int)(Number(said, "count") ?? 0);
                break;
            case "peer":
                AddPeer(Text(said, "id"), Text(said, "name"), Text(said, "model"));
                break;
            case "peer_gone":
                PeerGone(Text(said, "id"));
                break;
            case "asked":
                Asked(said);
                break;
            case "answered":
                Answered(Text(said, "id"), Text(said, "how"));
                break;
            case "incoming":
                Arriving(Text(said, "id"), Number(said, "done") ?? 0, Number(said, "total"));
                break;
            case "stored":
                Stored(said);
                break;
            case "lost":
                if (Find(Text(said, "id")) is { } lost)
                {
                    lost.State = IncomingState.Lost;
                    lost.Progress = 0;
                    lost.Status = "Nothing was kept. Ask for it to be sent again.";
                }
                break;
            case "send":
                Sent(said);
                break;
            case "settings":
                SettingsSaid(said);
                break;
            case "fatal":
                OnEngineStopped(Text(said, "reason"), Text(said, "detail"));
                return;
        }
        Refresh();
    }

    private void AdoptSettings(JsonElement said)
    {
        _settings.Name = Text(said, "name");
        _settings.Folder = Text(said, "folder");
        _settings.Ask = Flag(said, "ask");
        _settings.Save();
        _nameDraft = Name;
    }

    private void AddPeer(string id, string name, string model)
    {
        if (Peers.FirstOrDefault(known => known.Id == id) is { } known)
        {
            // Announced again while a transfer to it runs: its plate carries on, and stays.
            if (known.IsBusy)
            {
                known.Left = false;
                return;
            }
            Peers.Remove(known);
        }
        PeerModel? peer = null;
        peer = new PeerModel(id, name, model.Length > 0 ? model : null)
        {
            Press = new Command(() => SendTo(peer!)),
            Stop = new Command(() => _engine.Tell(new() { ["do"] = "cancel", ["id"] = peer!.SendId })),
        };
        peer.Status = peer.Resting;
        Peers.Add(peer);
    }

    private void PeerGone(string id)
    {
        if (Peers.FirstOrDefault(peer => peer.Id == id) is not { } peer) return;
        if (peer.IsBusy) peer.Left = true;
        else Peers.Remove(peer);
    }

    public void SendTo(PeerModel peer, IEnumerable<string>? dropped = null)
    {
        if (peer.IsBusy) return;
        if (dropped is not null) Hold(dropped);
        else if (Held.Count == 0) Hold(_desk.PickFiles());
        if (Held.Count == 0) return;
        peer.SendId = $"s{++_sends}";
        peer.Sending = Held.Select(file => file.Path).ToList();
        peer.Progress = 0;
        peer.State = PeerState.Asking;
        peer.Status = "Accept on the device to start";
        _engine.Tell(new()
        {
            ["do"] = "send", ["id"] = peer.SendId, ["peer"] = peer.Id, ["files"] = peer.Sending,
        });
        Refresh();
    }

    private void Sent(JsonElement said)
    {
        var id = Text(said, "id");
        if (Peers.FirstOrDefault(known => known.SendId == id) is not { } peer) return;
        var total = Number(said, "total") ?? 0;
        var done = Number(said, "done") ?? 0;
        switch (Text(said, "state"))
        {
            case "asking":
                break;
            case "sending":
                peer.State = PeerState.Sending;
                peer.Rate.Note(_desk.Now, done);
                peer.Progress = total > 0 ? (double)done / total : 0;
                peer.Status = $"{Words.Percent(peer.Progress)} of {Words.Size(total)}"
                              + (peer.Rate.BytesPerSecond > 0 ? $" · {Words.Rate(peer.Rate.BytesPerSecond)}" : "");
                break;
            case "finishing":
                peer.State = PeerState.Finishing;
                peer.Progress = 1;
                peer.Status = "Waiting for the device to confirm";
                break;
            case "sent":
                peer.State = PeerState.Sent;
                peer.Progress = 0;
                peer.Status = $"Sent {Words.Count(peer.Sending.Count, "file", "files")}";
                if (peer.Sending.SequenceEqual(Held.Select(file => file.Path)))
                {
                    Held.Clear();
                    HeldChanged("");
                }
                _desk.Later(SentShownFor, () =>
                {
                    if (peer.State == PeerState.Sent) Rest(peer);
                });
                break;
            case "declined":
                Stopped(peer, PeerState.Declined, "Declined on the device");
                break;
            case "refused":
                Stopped(peer, PeerState.Failed, "The device did not take the files");
                break;
            case "cancelled":
                Rest(peer);
                break;
            case "failed":
                Stopped(peer, PeerState.Failed, Text(said, "reason") switch
                {
                    "no_answer" => "No answer. Check that its screen is still on, then send again.",
                    "unreachable" => "It could not be reached. Keep its screen on and send again.",
                    "gone" => "It is no longer nearby.",
                    "not_a_file" => "One of these is no longer a file. Remove it and send again.",
                    "same_name" => "Two of the files have the same name. Remove one and send again.",
                    "unreadable" => "One of the files could not be read. Remove it and send again.",
                    _ => "The connection broke partway. Send again.",
                });
                break;
        }
    }

    private void Stopped(PeerModel peer, PeerState state, string status)
    {
        peer.State = state;
        peer.Progress = 0;
        peer.Status = status;
        if (peer.Left) Peers.Remove(peer);
    }

    private void Rest(PeerModel peer)
    {
        peer.State = PeerState.Idle;
        peer.Progress = 0;
        peer.Status = peer.Resting;
        if (peer.Left) Peers.Remove(peer);
        Refresh();
    }

    private IncomingModel? Find(string id) => Incoming.FirstOrDefault(item => item.Id == id);

    private void Asked(JsonElement said)
    {
        var id = Text(said, "id");
        var names = new List<string>();
        if (said.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            names.AddRange(files.EnumerateArray().Select(file => Text(file, "name")));
        var links = (int)(Number(said, "links") ?? 0);
        var total = Number(said, "total");
        var sender = Text(said, "sender");
        var model = Text(said, "model");
        var known = sender.Length > 0 && IsTrusted(sender, model);
        var waits = Flag(said, "waits") && !known;
        var detail = names.Count == 1 ? Words.Title(names[0]) : "";
        if (total is > 0) detail += (detail.Length > 0 ? " · " : "") + Words.Size(total.Value);
        var item = new IncomingModel
        {
            Id = id,
            Sender = sender.Length > 0 ? sender : "A device nearby",
            Model = model,
            What = Words.Things(names, links),
            Detail = detail,
            Total = total,
            Accept = new Command(() => Answer(id, true)),
            Decline = new Command(() => Answer(id, false)),
            Always = new Command(() =>
            {
                Trust(sender, model);
                Answer(id, true);
            }),
            Dismiss = new Command(() =>
            {
                if (Find(id) is { } shown) Incoming.Remove(shown);
            }),
            CanAlways = waits && sender.Length > 0,
            State = waits ? IncomingState.Asking : IncomingState.Receiving,
            Status = waits ? detail : "Waiting for the files",
        };
        Incoming.Insert(0, item);
        // The engine holds the request of a device that is always accepted, as it does any other.
        if (known && Flag(said, "waits")) Answer(id, true);
        if (waits) Attention?.Invoke(item.Headline);
    }

    private void Answer(string id, bool accept) =>
        _engine.Tell(new() { ["do"] = "answer", ["id"] = id, ["accept"] = accept });

    private bool IsTrusted(string name, string model) =>
        _settings.AlwaysAccept.Any(device => device.Name == name && device.Model == model);

    private TrustedModel Listed(KnownDevice device)
    {
        TrustedModel? listed = null;
        listed = new TrustedModel(device)
        {
            Forget = new Command(() =>
            {
                _settings.AlwaysAccept.Remove(device);
                _settings.Save();
                Trusted.Remove(listed!);
                Refresh();
            }),
        };
        return listed;
    }

    private void Trust(string name, string model)
    {
        if (name.Length == 0 || IsTrusted(name, model)) return;
        var device = new KnownDevice { Name = name, Model = model };
        _settings.AlwaysAccept.Add(device);
        _settings.Save();
        Trusted.Add(Listed(device));
        // Its other requests that wait no longer need the choice.
        foreach (var waiting in Incoming.Where(waiting => waiting.Sender == name && waiting.Model == model))
            waiting.CanAlways = false;
        Refresh();
    }

    public bool HasTrusted => Trusted.Count > 0;
    /// <summary>A request waits for an answer.</summary>
    public bool HasRequests => Incoming.Any(item => item.IsAsking);

    private void Answered(string id, string how)
    {
        if (Find(id) is not { } item) return;
        if (how != "accepted")
        {
            Incoming.Remove(item);
            return;
        }
        item.State = IncomingState.Receiving;
        item.Status = "Waiting for the files";
    }

    private void Arriving(string id, long done, long? total)
    {
        if (Find(id) is not { } item) return;
        item.Total = total ?? item.Total;
        item.Rate.Note(_desk.Now, done);
        if (item.Total is not > 0)
        {
            item.Status = Words.Size(done) + " so far";
            return;
        }
        item.Progress = (double)done / item.Total.Value;
        item.Status = done >= item.Total
            ? "Saving"
            : $"{Words.Percent(item.Progress)} of {Words.Size(item.Total.Value)}"
              + (item.Rate.BytesPerSecond > 0 ? $" · {Words.Rate(item.Rate.BytesPerSecond)}" : "");
    }

    private void Stored(JsonElement said)
    {
        var item = Find(Text(said, "id"));
        if (item is not null) Incoming.Remove(item);
        var folder = Text(said, "folder");
        if (!said.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array) return;
        // A Live Photo is stored as a still and a video, and is one thing here: its still.
        var videos = new Dictionary<string, string>();
        if (said.TryGetProperty("live", out var live) && live.ValueKind == JsonValueKind.Array)
            foreach (var pair in live.EnumerateArray())
                if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
                    videos[pair[0].GetString() ?? ""] = pair[1].GetString() ?? "";
        var landed = new List<FileItem>();
        foreach (var name in names.EnumerateArray().Select(name => name.GetString() ?? "").Reverse())
        {
            if (videos.ContainsValue(name)) continue;
            var path = Path.Combine(folder, name);
            var video = videos.TryGetValue(name, out var beside) ? Path.Combine(folder, beside) : null;
            landed.Insert(0, new FileItem(path, _desk)
            {
                Turn = Turns[Received.Count % Turns.Length],
                Press = new Command(() => _desk.Open(path)),
                Reveal = new Command(() => _desk.Reveal(path)),
                PlayVideo = video is null ? null : new Command(() => _desk.Open(video)),
            });
            Received.Insert(0, landed[0]);
        }
        while (Received.Count > MostReceived) Received.RemoveAt(Received.Count - 1);
        Landed?.Invoke();
        if (item is not null && landed.Count > 0) Arrived?.Invoke(Arrival(item, landed));
    }

    private ArrivalModel Arrival(IncomingModel from, List<FileItem> files)
    {
        var first = files[0];
        var size = Words.Size(files.Sum(file => file.Bytes));
        ArrivalModel? arrival = null;
        void Leave() => Arrivals.Remove(arrival!);
        arrival = new ArrivalModel
        {
            Headline = $"{from.Sender} sent you {from.What}",
            Detail = files.Count == 1 ? $"{first.Name} · {size}" : size,
            File = first,
            Single = files.Count == 1,
            Press = new Command(() =>
            {
                if (files.Count == 1) _desk.Open(first.Path);
                else _desk.Reveal(first.Path);
                Leave();
            }),
            Reveal = new Command(() =>
            {
                _desk.Reveal(first.Path);
                Leave();
            }),
            Dismiss = new Command(Leave),
        };
        return arrival;
    }

    /// <summary>What arrived and is being said on screen, outside the window.</summary>
    public ObservableCollection<ArrivalModel> Arrivals { get; } = [];

    /// <summary>The line the notification area shows under the pointer: what
    /// is moving and how far along, or how visible this PC is.</summary>
    public string TrayText
    {
        get
        {
            var lines = new List<string>();
            foreach (var item in Incoming)
                lines.Add(item.IsReceiving && item.Status.Length > 0 ? $"{item.Headline}: {item.Status}" : item.Headline);
            foreach (var peer in Peers.Where(peer => peer.IsBusy || peer.IsSent))
                lines.Add(peer.State switch
                {
                    PeerState.Asking => $"Waiting for {peer.Name} to accept",
                    PeerState.Sent => $"{peer.Status} to {peer.Name}",
                    _ => $"Sending to {peer.Name}: {peer.Status}",
                });
            if (lines.Count == 0)
                lines.Add(_radio.Phase switch
                {
                    RadioPhase.GivingBack => "Giving Wi-Fi back",
                    RadioPhase.WifiLeftOff => "Wi-Fi is still off",
                    _ => Presence,
                });
            var text = "winawdl\n" + string.Join("\n", lines);
            // Windows cuts what it shows there at 127 characters.
            return text.Length <= MostTrayText ? text : text[..(MostTrayText - 1)] + "…";
        }
    }

    /// <summary>Choosing files is the next thing to do: devices can be seen and
    /// nothing else is waiting for an answer.</summary>
    public bool ChooseIsNext => Across == Across.Nearby && !Incoming.Any(item => item.IsAsking);
    public bool TableInReach => !SettingsOpen;

    public bool HasHeld => Held.Count > 0;
    public bool HoldsNothing => Held.Count == 0;
    public bool HasReceived => Received.Count > 0;
    public string HeldNotice => _heldNotice;
    public bool HasHeldNotice => _heldNotice.Length > 0;
    public string HeldSummary =>
        $"{Words.Count(Held.Count, "file", "files")} · {Words.Size(Held.Sum(file => file.Bytes))}";
    public string HeldHint => Peers.Count > 0
        ? "Press a device above to send them"
        : "They go to the device you press, once one shows up";

    /// <summary>Take these files in hand. Folders and second files of the same
    /// name stay out, because an AirDrop archive cannot carry them.</summary>
    public void Hold(IEnumerable<string> paths)
    {
        int folders = 0, clashes = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                folders++;
                continue;
            }
            if (!File.Exists(path) || Held.Any(file => Same(file.Path, path))) continue;
            if (Held.Any(file => Same(file.Name, Path.GetFileName(path))))
            {
                clashes++;
                continue;
            }
            FileItem? item = null;
            item = new FileItem(path, _desk)
            {
                Turn = Turns[Held.Count % Turns.Length],
                Remove = new Command(() =>
                {
                    Held.Remove(item!);
                    HeldChanged("");
                }),
            };
            Held.Add(item);
        }
        HeldChanged(folders > 0 ? "Folders cannot be sent. Drop the files inside instead."
            : clashes > 0 ? "Two files with the same name cannot go together. The second was left out."
            : "");
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void HeldChanged(string notice)
    {
        _heldNotice = notice;
        Refresh();
    }

    public bool SettingsOpen
    {
        get => _settingsOpen;
        set
        {
            if (!Set(ref _settingsOpen, value)) return;
            if (value)
            {
                _nameDraft = Name;
                _nameError = _folderError = "";
            }
            ChangedAll();
        }
    }

    public string NameDraft { get => _nameDraft; set => Set(ref _nameDraft, value); }
    public string NameError => _nameError;
    public bool HasNameError => _nameError.Length > 0;
    public string FolderError => _folderError;
    public bool HasFolderError => _folderError.Length > 0;

    public bool AskFirst
    {
        get => _settings.Ask;
        set
        {
            if (_settings.Ask == value) return;
            _settings.Ask = value;
            _settings.Save();
            _engine.Tell(new() { ["do"] = "set", ["ask"] = value });
            ChangedAll();
        }
    }

    public bool TakeAll { get => !AskFirst; set => AskFirst = !value; }

    public bool ShowRequests
    {
        get => _settings.ShowRequests;
        set
        {
            _settings.ShowRequests = value;
            _settings.Save();
            Changed();
        }
    }

    public bool KeepInTray
    {
        get => _settings.KeepInTray;
        set
        {
            _settings.KeepInTray = value;
            _settings.Save();
            Changed();
        }
    }

    public void CommitName()
    {
        var wanted = NameDraft.Trim();
        if (wanted == Name) return;
        _engine.Tell(new() { ["do"] = "set", ["name"] = wanted });
    }

    private void PickFolder()
    {
        if (_desk.PickFolder(Folder) is { } folder)
            _engine.Tell(new() { ["do"] = "set", ["folder"] = folder });
    }

    private void SettingsSaid(JsonElement said)
    {
        var refused = said.TryGetProperty("refused", out var value) ? value : default;
        bool Refused(string name) =>
            refused.ValueKind == JsonValueKind.Object && refused.TryGetProperty(name, out _);
        _nameError = Refused("name")
            ? "Use 1 to 63 characters, and none of \\ / : * ? \" < > |"
            : "";
        _folderError = Refused("folder") ? "That folder could not be used. The old one stays." : "";
        _settings.Name = Text(said, "name");
        _settings.Folder = Text(said, "folder");
        _settings.Ask = Flag(said, "ask");
        _settings.Save();
        if (!Refused("name")) _nameDraft = Name;
    }
}
