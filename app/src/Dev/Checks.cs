using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Documents;
using Winawdl.Engine;
using Winawdl.Model;
using Winawdl.Radio;
using Winawdl.Shell;
using Winawdl.Theme;
using Winawdl.Views;

namespace Winawdl.Dev;

/// <summary>The checks that need neither a radio nor the engine: the colors'
/// contrast for any accent, the wording, what each state of the radio and the
/// engine puts on screen, and the supervisor that runs the two daemons.</summary>
public static class Checks
{
    public const string Flag = "--check";

    private static StreamWriter _log = null!;
    private static int _passed, _failed;

    public static int Run(string[] args)
    {
        if (args.Length < 1) return 2;
        var folder = Directory.CreateDirectory(args[0]).FullName;
        using var log = new StreamWriter(Path.Combine(folder, "check.log")) { AutoFlush = true };
        _log = log;
        try
        {
            Colors();
            Wording();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Program.Dress(app).Apply(Palette.Light(Rgb.Hex("#0078D4")));
            Motion.Reduced = true;
            States(app);
            Card();
            RadioWatch(app);
            Supervisor(Path.Combine(folder, "supervised"));
        }
        catch (Exception error)
        {
            log.WriteLine($"STOPPED {error}");
            return 1;
        }
        log.WriteLine(_failed == 0 ? $"ALL {_passed} PASSED" : $"{_failed} FAILED, {_passed} passed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Expect(bool ok, string what)
    {
        if (ok) _passed++;
        else _failed++;
        _log.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    }

    /// <summary>The 48 accents Windows offers, and the extremes nobody offers.</summary>
    private static readonly string[] Accents =
    [
        "FFB900", "FF8C00", "F7630C", "CA5010", "DA3B01", "EF6950", "D13438", "FF4343", "E74856", "E81123",
        "EA005E", "C30052", "E3008C", "BF0077", "C239B3", "9A0089", "0078D4", "0063B1", "8E8CD8", "6B69D6",
        "8764B8", "744DA9", "B146C2", "881798", "0099BC", "2D7D9A", "00B7C3", "038387", "00B294", "018574",
        "00CC6A", "10893E", "7A7574", "5D5A58", "68768A", "515C6B", "567C73", "486860", "498205", "107C10",
        "767676", "4C4A48", "69797E", "4A5459", "647C64", "525E54", "847545", "7E735F",
        "FFFFFF", "000000", "FFFF00", "0000FF", "808080",
    ];

    private static void Colors()
    {
        double text = double.MaxValue, onFill = double.MaxValue, accentText = double.MaxValue,
               fill = double.MaxValue, tinted = double.MaxValue;
        foreach (var accent in Accents.Select(Rgb.Hex))
            foreach (var palette in (Palette[])[Palette.Light(accent), Palette.Night(accent)])
            {
                foreach (var surface in palette.Surfaces)
                {
                    foreach (var ink in (Rgb[])[palette.Ink, palette.InkSoft, palette.Ember])
                        text = Math.Min(text, Rgb.Contrast(ink, surface));
                    accentText = Math.Min(accentText, Rgb.Contrast(palette.AccentText, surface));
                    fill = Math.Min(fill, Rgb.Contrast(palette.Accent, surface));
                }
                onFill = Math.Min(onFill, Rgb.Contrast(palette.OnAccent, palette.Accent));
                tinted = Math.Min(tinted, Math.Min(Rgb.Contrast(palette.Ink, palette.AccentSoft),
                                                   Rgb.Contrast(palette.InkSoft, palette.AccentSoft)));
            }
        var cases = $"{Accents.Length} accents, light and dark";
        Expect(text >= Palette.TextContrast, $"ink, soft ink and ember on every surface: lowest {text:0.00} to 1");
        Expect(onFill >= Palette.TextContrast, $"text on an accent fill, {cases}: lowest {onFill:0.00} to 1");
        Expect(accentText >= Palette.TextContrast, $"accent as text or focus ring on every surface, {cases}: lowest {accentText:0.00} to 1");
        Expect(fill >= Palette.ShapeContrast, $"accent as a fill against every surface, {cases}: lowest {fill:0.00} to 1");
        Expect(tinted >= Palette.TextContrast, $"ink on the tint of a transfer in progress, {cases}: lowest {tinted:0.00} to 1");
    }

    private static void Wording()
    {
        Expect(Words.Size(512) == "512 bytes" && Words.Size(1) == "1 byte" && Words.Size(26_100_000) == "24.9 MB"
               && Words.Size(3 * 1024L * 1024 * 1024) == "3.00 GB" && Words.Size(20_000) == "20 KB",
               "sizes read as Windows writes them");
        Expect(Words.Things(["a.jpg"]) == "a photo" && Words.Things(["a.jpg", "b.HEIC"]) == "2 photos"
               && Words.Things(["a.mov"]) == "a video" && Words.Things(["a.jpg", "b.mov"]) == "2 photos and videos"
               && Words.Things(["a.pdf", "b.jpg"]) == "2 files" && Words.Things([], 1) == "a link"
               && Words.Things([], 3) == "3 links" && Words.Things([]) == "something",
               "what is being sent is named by its kind");
        Expect(Words.KindOfDevice("iPhone14,2") == DeviceKind.Phone && Words.KindOfDevice("iPad13,1") == DeviceKind.Tablet
               && Words.KindOfDevice("MacBookPro18,3") == DeviceKind.Computer && Words.KindOfDevice("Macmini9,1") == DeviceKind.Computer
               && Words.KindOfDevice(null) == DeviceKind.Other && Words.KindOfDevice("RealityDevice14,1") == DeviceKind.Other,
               "a device's kind comes from the model it reports");
        Expect(Words.Percent(0.999) == "99%" && Words.Percent(1) == "100%" && Words.Percent(-1) == "0%",
               "a percentage never shows 100 before the end");
    }

    private static MainView Show(Stage.World world)
    {
        var view = new MainView { DataContext = world.Model };
        var host = new AdornerDecorator { Child = view };
        host.Measure(new Size(460, 720));
        host.Arrange(new Rect(0, 0, 460, 720));
        host.UpdateLayout();
        return view;
    }

    private static bool Press(FrameworkElement view, string name)
    {
        view.UpdateLayout();
        var control = Stage.Controls(view).FirstOrDefault(one => one.Line.Contains($"\"{name}\"")).Control;
        if (control is null) return false;
        var peer = UIElementAutomationPeer.CreatePeerForElement(control);
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        else if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
        else return false;
        Stage.Pump(view.Dispatcher, 80);
        return true;
    }

    private static void States(Application app)
    {
        // The radio, from off to on and through each way it can fail.
        var world = Stage.NewWorld(app.Dispatcher);
        var view = Show(world);
        var model = world.Model;
        Expect(model.Across == Across.RadioOff && model.Presence == "Not visible" && !model.RadioOn,
               $"no link: \"{model.PanelTitle}\", header \"{model.Presence}\"");
        Expect(Press(view, "Turn on the radio") && world.Radio.Starts == 1 && model.Across == Across.RadioStarting && model.RadioOn,
               $"Turn on the radio -> the radio is started once: \"{model.PanelTitle}\"");
        world.Radio.Become(RadioPhase.Starting, "radio: card ready");
        Expect(model.PanelBody == "radio: card ready", "while it starts, the panel shows what the radio last wrote");
        world.Engine.Say(new { @event = "link", up = true });
        world.Engine.Say(new { @event = "receiving", on = true });
        Expect(model.Across == Across.Nearby && world.Radio.Phase == RadioPhase.Running && model.Presence == "Visible as STUDY-PC",
               $"link up -> the devices' side, header \"{model.Presence}\", \"{model.NobodyTitle}\"");
        world.Engine.Say(new { @event = "looking", on = true, beacon = false, why = "the radio is off" });
        world.Engine.Say(new { @event = "unwilling", count = 2 });
        world.Engine.Say(new { @event = "receiving", on = false, reason = "port_held" });
        Expect(model.Notices.Count == 3 && model.Presence == "Not visible yet",
               $"no Bluetooth, a held port and shy devices -> three notices: {string.Join(" | ", model.Notices)}");
        world.Engine.Say(new { @event = "unwilling", count = 1 });
        Expect(model.Notices.Contains("A device nearby only answers devices it knows. Set its AirDrop to Everyone for 10 Minutes."),
               "one device that refuses this PC or keeps its name -> the notice says what to change on it");
        world.Engine.Say(new { @event = "unwilling", count = 0 });
        Expect(model.Notices.Count == 2, "it stops refusing, or leaves -> the notice goes");
        world.Engine.Say(new { @event = "link", up = false });
        world.Radio.Become(RadioPhase.Failed, "radio: could not get 6 locked 2 MiB blocks below 4 GiB");
        Expect(model.Across == Across.RadioProblem && model.PanelTitle == "The radio stopped" && model.PanelHasLog,
               $"the radio ends by itself -> \"{model.PanelTitle}\": \"{model.PanelBody}\"");
        Expect(Press(view, "Open the logs") && world.Desk.Opened.Last().StartsWith("open "), "Open the logs -> the log folder is opened (recorded)");
        Expect(Press(view, "Try again") && world.Radio.Starts == 2, "Try again -> the radio is started again");
        world.Radio.Become(RadioPhase.Failed, "");
        Expect(model.PanelTitle == "The radio did not start" && model.PanelBody == "It left nothing in its log.",
               $"it fails before running, silent -> \"{model.PanelTitle}\": \"{model.PanelBody}\"");
        world.Radio.Become(RadioPhase.Refused);
        Expect(model.PanelTitle == "The radio stayed off" && !model.PanelHasLog && !model.RadioOn,
               $"Windows was told no -> \"{model.PanelTitle}\": \"{model.PanelBody}\"");
        world.Radio.Become(RadioPhase.NotBuilt);
        Expect(model.PanelBody.Contains("build-radio.ps1"), $"nothing built -> \"{model.PanelTitle}\": \"{model.PanelBody}\"");

        // The engine, each way it can be missing.
        foreach (var (why, detail, title) in new[]
                 {
                     ("no_python", "", "Python was not found"), ("no_engine", "", "winawdl's files are incomplete"),
                     ("no_certificate", "OpenSSL not found", "No certificate could be made"),
                     ("ended", "", "The AirDrop engine stopped"),
                 })
        {
            world.Engine.End(why, detail);
            Expect(model.Across == Across.EngineDown && model.PanelTitle == title && model.PanelBody.Length > 20 || detail.Length > 0 && model.PanelBody == detail,
                   $"engine {why} -> \"{model.PanelTitle}\": \"{model.PanelBody}\"");
        }
        var before = world.Engine.Told.Count;
        Expect(Press(view, "Start it again") && model.Across != Across.EngineDown && world.Engine.Told.Count > before,
               "Start it again -> the engine is started and told whether to look");

        // The switch does what the panel's button does, and takes it back.
        world = Stage.NewWorld(app.Dispatcher);
        view = Show(world);
        model = world.Model;
        Expect(Press(view, "Radio") && world.Radio.Starts == 1 && model.RadioOn, "the Radio switch, off -> the radio is started");
        Expect(Press(view, "Radio") && world.Radio.Phase == RadioPhase.Off && !model.RadioOn, "the Radio switch, on -> the radio the app started is stopped");

        // A transfer that the device walks away from, and one that is lost on the way in.
        world = Stage.NewWorld(app.Dispatcher);
        view = Show(world);
        model = world.Model;
        var copied = Stage.SampleFiles(Path.Combine(Path.GetTempPath(), "winawdl-check-files"));
        world.Desk.Picked = copied[..2];
        model.Paste.Execute(null);
        Expect(model.Held.Count == 2, "the command behind Ctrl+V takes the files on the clipboard in hand (the keys were not pressed)");
        model.ClearHeld.Execute(null);
        world.Engine.Say(new { @event = "link", up = true });
        world.Engine.Say(new { @event = "peer", id = "a", name = "Stand-in iPhone", model = "iPhone14,2" });
        var files = Stage.SampleFiles(Path.Combine(Path.GetTempPath(), "winawdl-check-files"));
        world.Desk.Picked = files[..2];
        Expect(Press(view, "Stand-in iPhone") && model.Held.Count == 2 && world.Engine.Told.Last().Contains("\"do\":\"send\""),
               "press a device with nothing in hand -> files are chosen and sent to it in one go");
        world.Engine.Say(new { @event = "peer_gone", id = "a" });
        Expect(model.Peers.Count == 1, "it stops announcing itself mid-transfer -> its plate stays until the transfer ends");
        world.Engine.Say(new { @event = "send", id = "s1", state = "failed", reason = "no_answer" });
        Expect(model.Peers.Count == 0, "the transfer fails -> the plate of the device that left goes too");
        world.Engine.Say(new { @event = "asked", id = "r1", sender = "", files = Array.Empty<object>(), links = 2, waits = true });
        Expect(model.Incoming[0].Headline == "A device nearby wants to send you 2 links", $"a nameless sender with links -> \"{model.Incoming[0].Headline}\"");
        world.Engine.Say(new { @event = "answered", id = "r1", how = "left" });
        Expect(model.Incoming.Count == 0, "the sender stops waiting -> its request goes by itself");
        world.Engine.Say(new { @event = "asked", id = "r2", sender = "Stand-in iPhone", files = new[] { new { name = "a.mov", size = 5, type = "" } }, links = 0, total = 5, waits = false });
        world.Engine.Say(new { @event = "lost", id = "r2", reason = "nothing arrived" });
        Expect(model.Incoming[0].IsLost && Press(view, "Dismiss") && model.Incoming.Count == 0,
               "a transfer lost on the way in says so, and Dismiss removes it");

        // A device that is announced again while a transfer to it runs.
        world.Engine.Say(new { @event = "peer", id = "b", name = "Stand-in iPad", model = "iPad13,1" });
        Press(view, "Stand-in iPad");
        world.Engine.Say(new { @event = "peer_gone", id = "b" });
        world.Engine.Say(new { @event = "peer", id = "b", name = "Stand-in iPad", model = "iPad13,1" });
        Expect(model.Peers.Count == 1 && model.Peers[0].IsBusy,
               "it is announced again mid-transfer -> the same plate, still sending");
        world.Engine.Say(new { @event = "send", id = "s2", state = "failed", reason = "no_answer" });
        Expect(model.Peers.Count == 1, "that transfer fails -> its plate stays, because the device is back");

        // A Live Photo is one photo, on the way in and on the shelf.
        world.Engine.Say(new { @event = "asked", id = "r3", sender = "Stand-in iPhone", files = new[] { new { name = "IMG_1.pvt", size = 0, type = "com.apple.private.live-photo-bundle" } }, links = 0, total = 0, waits = true });
        Expect(model.Incoming[0].Headline == "Stand-in iPhone wants to send you a photo" && model.Incoming[0].Detail == "IMG_1",
               $"a Live Photo is asked about -> \"{model.Incoming[0].Headline}\", \"{model.Incoming[0].Detail}\"");
        var shelf = Path.Combine(Path.GetTempPath(), "winawdl-check-files");
        world.Engine.Say(new
        {
            @event = "stored", id = "r3", folder = shelf, names = new[] { "IMG_1.MOV", "IMG_1.jpg", "note.txt" },
            live = new[] { new[] { "IMG_1.jpg", "IMG_1.MOV" } },
        });
        Expect(model.Received.Select(file => file.Name).SequenceEqual(["IMG_1.jpg", "note.txt"]),
               $"a Live Photo and a note are stored -> on the shelf: {string.Join(", ", model.Received.Select(file => file.Name))}");
        var live = model.Received.FirstOrDefault(file => file.Name == "IMG_1.jpg");
        live?.PlayVideo?.Execute(null);
        Expect(live is { HasVideo: true, Hint: "IMG_1.jpg, a Live Photo" } && world.Desk.Opened.Last() == "open " + Path.Combine(shelf, "IMG_1.MOV"),
               "the photo says it is a Live Photo, and Play the video opens its video (recorded)");
        Expect(model.Received.First(file => file.Name == "note.txt") is { HasVideo: false, Hint: "note.txt" }, "any other file has no video to play");

        Handover(app);
        Quitting();
        Always(app);
        Requests(app);
    }

    private static object Ask(string id, string sender, string model, bool waits = true) => new
    {
        @event = "asked", id, sender, model, links = 0, total = 5, waits,
        files = new[] { new { name = "a.jpg", size = 5, type = "public.jpeg" } },
    };

    /// <summary>What the window says while the Wi-Fi card moves, in an installed copy.</summary>
    private static void Handover(Application app)
    {
        const string plainOff = "Turn it on to see nearby iPhones, iPads and Macs, and to be seen by them. "
                                + "Windows asks for permission first.";
        var world = Stage.NewWorld(app.Dispatcher);
        world.Radio.SwapsCard = true;
        var view = Show(world);
        var model = world.Model;
        Expect(model.PanelTitle == "The radio is off" && model.PanelActionLabel == "Turn on the radio"
               && model.PanelBody == "Turn it on to see nearby iPhones, iPads and Macs, and to be seen by them. It uses the "
                                     + "Wi-Fi card, so Wi-Fi is off while the radio is on. Windows asks for permission first.",
               $"off, installed -> it says the radio turns Wi-Fi off: \"{model.PanelBody}\"");
        world.Radio.Become(RadioPhase.TakingCard);
        Expect(model.Across == Across.RadioStarting && model.RadioOn && model.PanelTitle == "Starting the radio" && !model.PanelHasAction
               && model.PanelBody == "Taking the Wi-Fi card from Windows. Wi-Fi is off until the radio is turned off.",
               $"taking the card -> \"{model.PanelTitle}\": \"{model.PanelBody}\"");
        world.Radio.Become(RadioPhase.GivingBack);
        Expect(model.Across == Across.RadioOff && model.PanelTitle == "Giving Wi-Fi back" && !model.PanelHasAction
               && model.PanelBody == "The radio has stopped. The Wi-Fi card is going back to Windows."
               && model.TrayText == "winawdl\nGiving Wi-Fi back",
               $"giving it back -> \"{model.PanelTitle}\": \"{model.PanelBody}\", icon \"{model.TrayText.Replace("\n", " / ")}\"");
        var starts = world.Radio.Starts;
        model.RadioOn = true;
        Expect(world.Radio.Starts == starts, "the switch flipped on while Wi-Fi is given back -> nothing starts until it is back");
        world.Radio.Become(RadioPhase.WifiLeftOff, "Windows wants a restart to finish giving Wi-Fi back.");
        Expect(model.Across == Across.RadioProblem && model.PanelTitle == "Wi-Fi is still off"
               && model.PanelBody == "Windows wants a restart to finish giving Wi-Fi back. Restarting the PC also gives it back."
               && model.PanelActionLabel == "Give Wi-Fi back" && model.PanelAsksWindows && !model.PanelHasLog
               && model.TrayText == "winawdl\nWi-Fi is still off",
               $"Wi-Fi left off -> \"{model.PanelTitle}\": \"{model.PanelBody}\", press \"{model.PanelActionLabel}\" with the shield");
        Expect(Press(view, "Give Wi-Fi back") && world.Radio.GiveBacks == 1, "Give Wi-Fi back -> Windows is asked to give the card back");

        var clone = Stage.NewWorld(app.Dispatcher);
        Expect(clone.Model.PanelBody == plainOff, "run from a clone -> the off panel says nothing about Wi-Fi");
    }

    /// <summary>Quit from the notification area while the radio has the Wi-Fi card.</summary>
    private static void Quitting()
    {
        (Leaving Leaving, ScriptedRadio Radio, List<string> Done) Quit(RadioPhase phase, bool swaps = true)
        {
            var radio = new ScriptedRadio { SwapsCard = swaps, StopsAtOnce = false };
            radio.Become(phase);
            var done = new List<string>();
            var leaving = new Leaving(radio, () => done.Add("shut down"), (text, _) => done.Add($"said: {text}"),
                                      () => done.Add("shown"));
            leaving.Quit();
            return (leaving, radio, done);
        }

        var (_, radio, done) = Quit(RadioPhase.Running);
        var stillThere = done.Count == 0 && radio.Phase == RadioPhase.Stopping;
        radio.Become(RadioPhase.GivingBack);
        stillThere &= done.Count == 0;
        radio.Become(RadioPhase.Off);
        Expect(stillThere && done is ["shut down"],
               $"Quit with the radio on -> it stops, the app stays until Wi-Fi is back, then goes: {string.Join(", ", done)}");

        var (leaving, stuck, said) = Quit(RadioPhase.Running);
        stuck.Become(RadioPhase.WifiLeftOff, "x");
        var waits = said is ["said: Wi-Fi is still off. Press here to give it back."];
        leaving.Quit();
        Expect(waits && said.Last() == "shut down",
               $"Wi-Fi cannot be given back -> it says so from the icon and stays, until Quit again: {string.Join(", ", said)}");

        var (_, taking, failed) = Quit(RadioPhase.TakingCard);
        taking.Become(RadioPhase.Failed, "The Wi-Fi card was not found.");
        Expect(failed is ["shut down"], $"Quit while the card is being taken, and taking it fails -> it goes: {string.Join(", ", failed)}");

        var (_, _, off) = Quit(RadioPhase.Off);
        var (_, _, left) = Quit(RadioPhase.WifiLeftOff);
        var (_, _, clone) = Quit(RadioPhase.Running, swaps: false);
        Expect(off is ["shut down"] && left is ["shut down"] && clone is ["shut down"],
               "radio off, Wi-Fi already known to be off, or a clone -> Quit goes at once");
    }

    /// <summary>A device whose requests are accepted without asking.</summary>
    private static void Always(Application app)
    {
        var world = Stage.NewWorld(app.Dispatcher);
        var view = Show(world);
        var model = world.Model;
        var saved = 0;
        world.Settings.Save = () => saved++;
        var attention = new List<string>();
        var arrived = new List<string>();
        model.Attention += attention.Add;
        model.Arrived += arrival => arrived.Add(arrival.Headline);
        world.Engine.Say(new { @event = "link", up = true });

        world.Engine.Say(Ask("r1", "Stand-in iPhone", "iPhone14,2"));
        Expect(model.Incoming[0].ShowsAlways && attention.Count == 1, "a request from a named device offers Always accept, and asks for attention");
        Expect(Press(view, "Always accept from Stand-in iPhone") && world.Engine.Told.Last().Contains("\"id\":\"r1\",\"accept\":true"),
               "Always accept from Stand-in iPhone -> this request is accepted");
        Expect(world.Settings.AlwaysAccept is [{ Name: "Stand-in iPhone", Model: "iPhone14,2" }] && saved == 1 && model.Trusted.Count == 1,
               "the device is remembered by its name and model, and that is written down");
        world.Engine.Say(new { @event = "answered", id = "r1", how = "accepted" });

        world.Engine.Say(Ask("r2", "Stand-in iPhone", "iPhone14,2"));
        Expect(world.Engine.Told.Last().Contains("\"id\":\"r2\",\"accept\":true") && !model.Incoming[0].IsAsking && attention.Count == 1,
               "its next request is accepted at once: no question, no call for attention");
        world.Engine.Say(new { @event = "stored", id = "r2", folder = Path.GetTempPath(), names = new[] { "a.jpg" } });
        Expect(arrived is ["Stand-in iPhone sent you a photo"], $"what it sent is said once it is stored: \"{arrived.LastOrDefault()}\"");

        var told = world.Engine.Told.Count;
        world.Engine.Say(Ask("r3", "Stand-in iPhone", "iPhone15,3"));
        Expect(model.Incoming[0].IsAsking && world.Engine.Told.Count == told && attention.Count == 2,
               "the same name from another model of device is asked about");
        world.Engine.Say(new { @event = "answered", id = "r3", how = "declined" });
        world.Engine.Say(Ask("r4", "", ""));
        Expect(model.Incoming[0].IsAsking && !model.Incoming[0].ShowsAlways, "a device that gives no name cannot be always accepted");
        world.Engine.Say(new { @event = "answered", id = "r4", how = "declined" });

        model.SettingsOpen = true;
        var listed = Stage.Controls(view).Select(one => one.Line).ToList();
        Expect(listed.Any(line => line.Contains("\"Stop always accepting from Stand-in iPhone\"")),
               "settings lists the device, with a way to stop");
        Expect(Press(view, "Stop always accepting from Stand-in iPhone") && world.Settings.AlwaysAccept.Count == 0
               && model.Trusted.Count == 0 && saved == 2, "Stop always accepting -> it is forgotten, and that is written down");
        model.SettingsOpen = false;
        world.Engine.Say(Ask("r5", "Stand-in iPhone", "iPhone14,2"));
        Expect(model.Incoming[0].IsAsking && model.Incoming[0].ShowsAlways, "its next request is asked about again");

        // Remembered across a restart: the model starts from what was written down.
        var kept = new AppSettings { AlwaysAccept = [new KnownDevice { Name = "Stand-in iPad", Model = "iPad13,1" }] };
        var again = new TableModel(world.Engine, world.Radio, world.Desk, kept);
        Expect(again.Trusted is [{ Name: "Stand-in iPad" }], "a remembered device is listed when the app starts again");

        // Taking everything asks nobody, so what arrives is said as well.
        world = Stage.NewWorld(app.Dispatcher);
        arrived.Clear();
        world.Model.Arrived += arrival => arrived.Add(arrival.Headline);
        world.Engine.Say(Ask("r1", "Stand-in Mac", "MacBookPro18,3", waits: false));
        world.Engine.Say(new { @event = "stored", id = "r1", folder = Path.GetTempPath(), names = new[] { "a.jpg" } });
        Expect(arrived is ["Stand-in Mac sent you a photo"], "with Take everything, what arrived is said too");
    }

    /// <summary>The card that shows a request while the window is not in front.</summary>
    private static void Requests(Application app)
    {
        var world = Stage.NewWorld(app.Dispatcher);
        var model = world.Model;
        var card = new RequestsView { DataContext = model };
        var host = new AdornerDecorator { Child = card };
        void Lay()
        {
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
        }

        world.Engine.Say(new { @event = "link", up = true });
        world.Engine.Say(Ask("r1", "Stand-in iPhone", "iPhone14,2"));
        Lay();
        var controls = Stage.Controls(card).Select(one => one.Line).ToList();
        Expect(controls.Count == 2 && controls[0].Contains("\"Decline\"") && controls[1].Contains("\"Accept\"")
               && controls.All(line => !line.Contains("NO FOCUS RING")),
               $"a waiting request is a card with two presses: {string.Join(", ", controls)}");
        Expect(Press(card, "Accept") && world.Engine.Told.Last().Contains("\"id\":\"r1\",\"accept\":true"), "Accept on the card -> the request is accepted");
        world.Engine.Say(new { @event = "answered", id = "r1", how = "accepted" });
        Lay();
        Expect(Stage.Controls(card).Count == 0 && !model.HasRequests, "once answered, the card is gone while the transfer runs in the window");
        world.Engine.Say(Ask("r2", "Stand-in iPhone", "iPhone14,2"));
        Lay();
        Expect(Press(card, "Decline") && world.Engine.Told.Last().Contains("\"id\":\"r2\",\"accept\":false"), "Decline on the card -> the request is declined");

        // The window that holds the cards, kept off screen.
        var inFront = false;
        var window = new RequestWindow(model, () => inFront) { Spot = new Point(-30000, -30000) };
        world.Settings.ShowRequests = false;
        Expect(!window.Announce() && !window.IsVisible, "with the setting off, a request is not shown on screen and is said another way");
        world.Settings.ShowRequests = true;
        // What Announce answers also depends on Windows: a full-screen app would have it say no.
        var announced = window.Announce();
        Stage.Pump(app.Dispatcher, 80);
        Expect(!announced || window.IsVisible && !window.IsActive && window.Left <= -29000,
               announced ? "a request while the window is not in front -> the card window shows, without taking the keyboard"
                         : "Windows takes no notices right now (a full-screen app or a presentation): nothing is shown");
        inFront = true;
        window.Reconsider();
        Expect(!window.IsVisible, "the app's window comes to the front -> the card window goes: the request is there already");
        inFront = false;
        window.Reconsider();
        Expect(!announced || window.IsVisible, "it leaves the front with the request still waiting -> the card window is back");
        world.Engine.Say(new { @event = "answered", id = "r2", how = "left" });
        Stage.Pump(app.Dispatcher, 80);
        Expect(!window.IsVisible && !model.HasRequests, "the sender stops waiting -> the card window goes by itself");

        // What arrived, said on a card that leads to it.
        var shelf = Path.Combine(Path.GetTempPath(), "winawdl-check-files");
        world.Engine.Say(new { @event = "stored", id = "r1", folder = shelf, names = Array.Empty<string>() });
        var arrivals = new List<ArrivalModel>();
        model.Arrived += arrivals.Add;
        world.Engine.Say(Ask("r3", "Stand-in iPhone", "iPhone14,2", waits: false));
        world.Engine.Say(new { @event = "incoming", id = "r3", done = 2, total = 5 });
        Expect(model.TrayText == "winawdl\nStand-in iPhone is sending you a photo: 40% of 5 bytes",
               $"the notification area icon says what is arriving and how far along: \"{model.TrayText.Replace("\n", " / ")}\"");
        world.Engine.Say(new { @event = "stored", id = "r3", folder = shelf, names = new[] { "IMG_1.jpg" } });
        Expect(arrivals is [{ Headline: "Stand-in iPhone sent you a photo", Single: true, PressLabel: "Open" }]
               && arrivals[0].Detail.StartsWith("IMG_1.jpg"), $"one file stored -> \"{arrivals.LastOrDefault()?.Headline}\", \"{arrivals.LastOrDefault()?.Detail}\"");
        Expect(model.TrayText == "winawdl\nNot visible yet", $"with nothing moving the icon says how visible this PC is: \"{model.TrayText.Replace("\n", " / ")}\"");
        var said = window.Announce(arrivals[0]);
        Stage.Pump(app.Dispatcher, 80);
        Expect(said == announced && (!said || window.IsVisible && model.Arrivals.Count == 1),
               said ? "what arrived while the window is not in front -> a card for it" : "Windows takes no notices right now: no card");
        if (!said) model.Arrivals.Add(arrivals[0]);
        Lay();
        controls = Stage.Controls(card).Select(one => one.Line).ToList();
        Expect(controls.Count == 4 && controls[0].Contains("\"Open IMG_1.jpg\"") && controls[1].Contains("\"Dismiss\"")
               && controls[2].Contains("\"Show in folder\"") && controls[3].Contains("\"Open\""),
               $"the card is itself the press, and holds three more: {string.Join(", ", controls)}");
        Expect(Press(card, "Open IMG_1.jpg") && world.Desk.Opened.Last() == "open " + Path.Combine(shelf, "IMG_1.jpg") && model.Arrivals.Count == 0,
               "press the card -> the file is opened (recorded), and the card goes");
        Stage.Pump(app.Dispatcher, 80);
        Expect(!window.IsVisible, "with no card left, the card window goes");

        world.Engine.Say(Ask("r4", "Stand-in iPhone", "iPhone14,2", waits: false));
        world.Engine.Say(new { @event = "stored", id = "r4", folder = shelf, names = new[] { "a.jpg", "b.jpg" } });
        Expect(arrivals.Last() is { Single: false, PressLabel: "Show in folder" }, "several files stored -> the card shows them in their folder");
        model.Arrivals.Add(arrivals.Last());
        Lay();
        Expect(Press(card, "Show what arrived in its folder") && world.Desk.Opened.Last() == "reveal " + Path.Combine(shelf, "a.jpg"),
               "press it -> the folder is opened on the first of them (recorded)");
        model.Arrivals.Add(arrivals.Last());
        Lay();
        Expect(Press(card, "Dismiss") && model.Arrivals.Count == 0, "Dismiss -> the card goes, and nothing is opened");
        model.Arrivals.Add(arrivals.Last());
        inFront = true;
        window.Reconsider();
        Expect(model.Arrivals.Count == 0 && !window.IsVisible, "the app's window comes to the front -> the card goes: the files are on its shelf");

        // Sending, as the icon says it.
        var held = Stage.SampleFiles(shelf);
        world.Engine.Say(new { @event = "peer", id = "a", name = "Stand-in iPad", model = "iPad13,1" });
        model.Hold(held[..2]);
        model.SendTo(model.Peers[0]);
        Expect(model.TrayText == "winawdl\nWaiting for Stand-in iPad to accept", $"a send that waits: \"{model.TrayText.Replace("\n", " / ")}\"");
        world.Engine.Say(new { @event = "send", id = "s1", state = "sending", done = 50, total = 200 });
        Expect(model.TrayText == "winawdl\nSending to Stand-in iPad: 25% of 200 bytes", $"a send under way: \"{model.TrayText.Replace("\n", " / ")}\"");
        world.Engine.Say(new { @event = "send", id = "s1", state = "sent" });
        Expect(model.TrayText == "winawdl\nSent 2 files to Stand-in iPad", $"a send that got through: \"{model.TrayText.Replace("\n", " / ")}\"");
        world.Engine.Say(Ask("r5", new string('n', 200), "iPhone14,2"));
        Expect(model.TrayText.Length == TableModel.MostTrayText && model.TrayText.EndsWith('…'),
               "a line too long for Windows is cut where Windows would cut it");
        window.Close();
    }

    /// <summary>What a log holds so far, read while its writer still has it open.</summary>
    private static string Written(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>Which driver packages in the store are the installed copy's own.</summary>
    private static void Card()
    {
        const string store = """
            <PnpUtil><Driver DriverName="oem66.inf"><OriginalName>winawdl_regproxy.inf</OriginalName>
            <SignerName>winawdl driver (signed on this PC)</SignerName></Driver>
            <Driver DriverName="oem70.inf"><OriginalName>winawdl_regproxy.inf</OriginalName>
            <SignerName>winawdl driver signing (local test)</SignerName></Driver>
            <Driver DriverName="oem92.inf"><OriginalName>netrtwlane6e.inf</OriginalName>
            <SignerName>Microsoft Windows Hardware Compatibility Publisher</SignerName></Driver></PnpUtil>
            """;
        Expect(WifiCard.OwnPackages(store) is ["oem66.inf"],
               "only the installed copy's own package counts as winawdl's, not the development one");
        Expect(WifiCard.OwnPackages("<PnpUtil />").Count == 0, "an empty driver store holds none of winawdl's");
        Expect(WifiCard.PackageInf(@"C:\Program Files\winawdl") == @"C:\Program Files\winawdl\driver\winawdl_regproxy.inf",
               "the installed copy's package is in its driver folder");
        string Card(string driver, string status) =>
            $"<PnpUtil><Device InstanceId=\"x\"><Status>{status}</Status><DriverName>{driver}</DriverName></Device></PnpUtil>";
        Expect(WifiCard.HolderFrom(Card("oem66.inf", "Started"), store) == CardHolder.Winawdl
               && WifiCard.HolderFrom(Card("oem70.inf", "Started"), store) == CardHolder.Wifi
               && WifiCard.HolderFrom(Card("oem92.inf", "Started"), store) == CardHolder.Wifi
               && WifiCard.HolderFrom("<PnpUtil />", store) == CardHolder.Missing,
               "the card is winawdl's only on the installed copy's package; no device is a missing card");
        Expect(WifiCard.StartedWithWifi(Card("oem92.inf", "Started"), store)
               && !WifiCard.StartedWithWifi(Card("oem92.inf", "Problem"), store)
               && !WifiCard.StartedWithWifi(Card("oem66.inf", "Started"), store),
               "Wi-Fi is back only once its own driver has the card and Windows has started it");

        // --give-wifi-back, as the startup task, the installer and the app's press run it.
        var logs = Path.Combine(Path.GetTempPath(), $"winawdl-check-wifi-{Environment.ProcessId}");
        Directory.CreateDirectory(logs);
        string Last() => File.ReadAllLines(Path.Combine(logs, "wifi.log")).Last();
        var held = new ScriptedCard { Now = CardHolder.Winawdl };
        Expect(WifiCard.GiveBackNow(held, logs) == 0 && held.Calls is ["give back"] && Last().EndsWith("gave Wi-Fi back"),
               $"winawdl's driver has the card -> it is given back: \"{Last()}\"");
        var free = new ScriptedCard { Now = CardHolder.Wifi };
        var none = new ScriptedCard { Now = CardHolder.Missing };
        Expect(WifiCard.GiveBackNow(free, logs) == 0 && free.Calls.Count == 0
               && WifiCard.GiveBackNow(none, logs) == 0 && none.Calls.Count == 0,
               "Wi-Fi already has the card, or there is none -> nothing is done");
        var failing = new ScriptedCard { Now = CardHolder.Winawdl, GiveBackFails = "x" };
        Expect(WifiCard.GiveBackNow(failing, logs) == 1 && Last().EndsWith("could not give Wi-Fi back: x"),
               $"it cannot be given back -> exit 1, and the log says why: \"{Last()}\"");
        var unreadable = new ScriptedCard { HolderBreaks = true };
        Expect(WifiCard.GiveBackNow(unreadable, logs) == 1 && Last().Contains("could not give Wi-Fi back") && Last().Contains("not XML"),
               $"Windows cannot even say who has the card -> exit 1, and the log says why: \"{Last()}\"");
        using (var other = new Hold())
        {
            var waiting = new ScriptedCard { Now = CardHolder.Winawdl };
            Expect(WifiCard.GiveBackNow(waiting, logs, TimeSpan.FromSeconds(1)) == 1 && waiting.Calls.Count == 0
                   && Last().EndsWith("Another winawdl is still moving the Wi-Fi card. Try again in a moment."),
                   $"another copy is moving the card -> it is left alone: \"{Last()}\"");
        }
        Directory.Delete(logs, recursive: true);
    }

    /// <summary>How the app reads the elevated copy's radio.status, and a card left with winawdl.</summary>
    private static void RadioWatch(Application app)
    {
        Expect(RadioControl.During("""{"step":"taking"}""") == RadioPhase.TakingCard
               && RadioControl.During("""{"step":"running"}""") == RadioPhase.Starting
               && RadioControl.During("""{"step":"giving"}""") == RadioPhase.GivingBack
               && RadioControl.During("") is null,
               "while the radio's copy runs, its step says whether the card is being taken or given back");
        Expect(RadioControl.Ended("""{"step":"done","ended":"stopped","wifi":"off","why":"x"}""", false) == (RadioPhase.WifiLeftOff, "x"),
               "it ends with Wi-Fi left off -> the app says so, with why");
        Expect(RadioControl.Ended("""{"step":"done","ended":"card","wifi":"back","why":"The Wi-Fi card was not found."}""", false)
               == (RadioPhase.Failed, "The Wi-Fi card was not found."),
               "the card could not be taken -> the radio did not start, and why");
        Expect(RadioControl.Ended("""{"step":"done","ended":"stopped","wifi":"back","why":""}""", true) == (RadioPhase.Off, "")
               && RadioControl.Ended("""{"step":"done","ended":"owner","wifi":"untouched","why":""}""", false) == (RadioPhase.Off, "")
               && RadioControl.Ended("""{"step":"done","ended":"radio","wifi":"back","why":""}""", false) == (RadioPhase.Failed, ""),
               "a stop that was asked for is off; a daemon that ended by itself is a failure, as before");
        var left = new RadioControl(app.Dispatcher, null, new ScriptedCard { Now = CardHolder.Winawdl });
        Expect(left.Phase == RadioPhase.WifiLeftOff && left.SwapsCard
               && left.LastLine == "Wi-Fi was left off when the radio last stopped.",
               "the app starts and winawdl's driver still has the card -> it says Wi-Fi was left off");
        var fine = new RadioControl(app.Dispatcher, null, new ScriptedCard { Now = CardHolder.Wifi });
        var clone = new RadioControl(app.Dispatcher, null);
        Expect(fine.Phase == RadioPhase.Off && !clone.SwapsCard && clone.Phase == RadioPhase.Off,
               "Wi-Fi has the card, or the app runs from a clone -> nothing to say");
        var blind = new RadioControl(app.Dispatcher, null, new ScriptedCard { HolderBreaks = true });
        Expect(blind.Phase == RadioPhase.Off, "Windows cannot say who has the card when the app starts -> it still starts");

        // The radio's copy ended without saying it gave the card back (killed, crashed): the card itself is asked.
        var held = new RadioControl(app.Dispatcher, null, new ScriptedCard { Now = CardHolder.Wifi });
        var heldCard = new ScriptedCard { Now = CardHolder.Winawdl };
        var killed = new RadioControl(app.Dispatcher, null, new ScriptedCard()) { };
        Expect(new RadioControl(app.Dispatcher, null, heldCard).AfterExit("""{"step":"giving"}""", true).Phase == RadioPhase.WifiLeftOff
               && new RadioControl(app.Dispatcher, null, heldCard).AfterExit("", false).Phase == RadioPhase.WifiLeftOff,
               "the radio's copy ends before saying it is done, and winawdl's driver still has the card -> Wi-Fi is still off");
        Expect(held.AfterExit("""{"step":"giving"}""", true).Phase == RadioPhase.Off
               && killed.AfterExit("""{"step":"done","ended":"stopped","wifi":"back","why":""}""", true).Phase == RadioPhase.Off,
               "it ends with Wi-Fi holding the card, or says it is done -> as before");
    }

    /// <summary>Another copy of winawdl in the middle of moving the card.</summary>
    private sealed class Hold : IDisposable
    {
        private readonly ManualResetEventSlim _held = new(), _release = new();
        private readonly Thread _thread;

        public Hold()
        {
            _thread = new Thread(() =>
            {
                using var held = WifiCard.Lock(TimeSpan.FromSeconds(5));
                _held.Set();
                _release.Wait();
            });
            _thread.Start();
            _held.Wait();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
        }
    }

    private static void Supervisor(string logs)
    {
        // Ping stands in for both daemons: it runs until interrupted, and says so when it is.
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        Daemon Forever(string name) => new(name, ping, ["-t", "127.0.0.1"]);

        (int Code, double Seconds, string Ended) Supervised(int owner, Daemon radio, Daemon awdl, Action<EventWaitHandle> during,
                                                            ICard? card = null, string? where = null, TimeSpan? cardWait = null)
        {
            var folder = where ?? logs;
            var name = $"winawdl-check-{Environment.ProcessId}-{Guid.NewGuid():N}";
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\" + name);
            var clock = Stopwatch.StartNew();
            var run = Task.Run(() => RadioSupervisor.Supervise(owner, name, radio, awdl, folder, card, @"C:\stand-in\winawdl_regproxy.inf",
                                                               cardWait));
            during(stop);
            var code = run.Wait(TimeSpan.FromSeconds(30)) ? run.Result : -1;
            var status = Path.Combine(folder, RadioSupervisor.StatusFile);
            return (code, clock.Elapsed.TotalSeconds, File.Exists(status) ? File.ReadAllText(status) : "");
        }

        var asked = Supervised(Environment.ProcessId, Forever("radio"), Forever("awdld"), stop =>
        {
            Thread.Sleep(2500);
            stop.Set();
        });
        var said = File.ReadAllText(Path.Combine(logs, "awdld.log"));
        Expect(asked.Code == 0 && asked.Ended.Contains("stopped") && said.Contains("Reply from"),
               $"asked to stop -> both daemons end, exit {asked.Code}, their output is in the logs");
        Expect(said.Contains("Control-C") && asked.Seconds < 2.5 + 4,
               $"they are interrupted the way Ctrl+C does, not killed: stopped {asked.Seconds - 2.5:0.0} s after the request");
        Expect(Process.GetProcessesByName("PING").Length == 0, "no daemon is left running");

        // A daemon that says one line with a dash in it on its error stream, then stays.
        var dashed = $"up {(char)0x2014} ready";
        var speaker = new Daemon("radio", Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        [
            "-NoProfile", "-Command",
            "$e = [Console]::OpenStandardError(); $b = [Text.Encoding]::UTF8.GetBytes(\"" + dashed + "`n\"); "
            + "$e.Write($b, 0, $b.Length); $e.Flush(); Start-Sleep 30",
        ]);
        var spoken = Supervised(Environment.ProcessId, speaker, Forever("awdld"), stop =>
        {
            // PowerShell takes its time to start on a busy machine: wait for the line, not for a clock.
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed.TotalSeconds < 25 && !Written(Path.Combine(logs, "radio.log")).Contains(dashed))
                Thread.Sleep(200);
            stop.Set();
        });
        Expect(spoken.Code == 0 && File.ReadAllText(Path.Combine(logs, "radio.log")).Contains(dashed),
               "what a daemon writes to its error stream in UTF-8 is in its log as written");

        var died = Supervised(Environment.ProcessId, new Daemon("radio", ping, ["-n", "1", "127.0.0.1"]), Forever("awdld"), _ => { });
        Expect(died.Code == 3 && died.Ended.Contains("radio"), $"a daemon ends by itself -> exit {died.Code}, the status names it, the other is stopped");

        using var owner = Process.Start(new ProcessStartInfo(ping, "-n 3 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var orphaned = Supervised(owner.Id, Forever("radio"), Forever("awdld"), _ => { });
        Expect(orphaned.Code == 0 && orphaned.Ended.Contains("owner"), $"the app is gone -> the daemons are stopped, exit {orphaned.Code}");

        var missing = Supervised(Environment.ProcessId, new Daemon("radio", Path.Combine(logs, "no-such.exe"), []), Forever("awdld"), _ => { });
        Expect(missing.Code == 3 && missing.Ended.Contains("launch"), $"a daemon that cannot be started -> exit {missing.Code}, nothing left running");
        Expect(Process.GetProcessesByName("PING").Length == 0, "and again no daemon is left running");

        // The Wi-Fi card: taken before the daemons start, given back once both have ended.
        static string Field(string json, string name) =>
            json.Length > 0 && JsonDocument.Parse(json).RootElement.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        const string inf = @"C:\stand-in\winawdl_regproxy.inf";
        var card = new ScriptedCard();
        var stillRunning = -1;
        card.OnGiveBack = () => stillRunning = Process.GetProcessesByName("PING").Length;
        var handed = Supervised(Environment.ProcessId, Forever("radio"), Forever("awdld"), stop =>
        {
            Thread.Sleep(1500);
            stop.Set();
        }, card);
        Expect(handed.Code == 0 && card.Calls.SequenceEqual([$"take {inf}", "give back"]) && Field(handed.Ended, "step") == "done"
               && Field(handed.Ended, "wifi") == "back" && card.Now == CardHolder.Wifi,
               $"asked to stop -> the card is taken first and given back last: {string.Join(", ", card.Calls)}, wifi {Field(handed.Ended, "wifi")}");
        Expect(stillRunning == 0, $"the card is given back only after both daemons have ended ({stillRunning} still running then)");

        // A daemon that does not take Ctrl+C is killed, and only then is the card given back.
        var stubborn = Path.Combine(logs, "stubborn.pid");
        File.Delete(stubborn);
        var deaf = new Daemon("radio", Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        [
            "-NoProfile", "-Command",
            $"[Console]::TreatControlCAsInput = $true; Set-Content '{stubborn}' $PID; Start-Sleep 60",
        ]);
        card = new ScriptedCard();
        var deafAlive = true;
        card.OnGiveBack = () =>
        {
            try { deafAlive = !Process.GetProcessById(int.Parse(File.ReadAllText(stubborn).Trim())).HasExited; }
            catch (Exception error) when (error is ArgumentException or IOException or FormatException) { deafAlive = false; }
        };
        var killed = Supervised(Environment.ProcessId, deaf, Forever("awdld"), stop =>
        {
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed.TotalSeconds < 25 && !File.Exists(stubborn)) Thread.Sleep(200);
            stop.Set();
        }, card);
        Expect(killed.Code == 0 && card.Calls.LastOrDefault() == "give back" && !deafAlive,
               $"a daemon deaf to Ctrl+C is killed first, then the card is given back (it was {(deafAlive ? "still running" : "gone")})");

        // The card cannot be taken: nothing is started, and Wi-Fi was never off.
        var untaken = Path.Combine(logs, "untaken");
        if (Directory.Exists(untaken)) Directory.Delete(untaken, recursive: true);
        Directory.CreateDirectory(untaken);
        card = new ScriptedCard { TakeFails = "The Wi-Fi card was not found." };
        var refused = Supervised(Environment.ProcessId, Forever("radio"), Forever("awdld"), _ => { }, card, untaken);
        Expect(refused.Code == 4 && Field(refused.Ended, "ended") == "card" && Field(refused.Ended, "why") == "The Wi-Fi card was not found."
               && card.Calls.SequenceEqual([$"take {inf}"]) && card.Now == CardHolder.Wifi
               && !File.Exists(Path.Combine(untaken, "radio.log")),
               $"the card cannot be taken -> exit {refused.Code}, no daemon started, Wi-Fi untouched: \"{Field(refused.Ended, "why")}\"");

        // The card cannot be given back.
        card = new ScriptedCard { GiveBackFails = "Windows wants a restart to finish giving Wi-Fi back." };
        var stuck = Supervised(Environment.ProcessId, Forever("radio"), Forever("awdld"), stop =>
        {
            Thread.Sleep(1500);
            stop.Set();
        }, card);
        Expect(stuck.Code == 5 && Field(stuck.Ended, "wifi") == "off"
               && Field(stuck.Ended, "why") == "Windows wants a restart to finish giving Wi-Fi back.",
               $"the card cannot be given back -> exit {stuck.Code}, wifi {Field(stuck.Ended, "wifi")}: \"{Field(stuck.Ended, "why")}\"");

        // The app is gone: Wi-Fi comes back all the same.
        using var gone = Process.Start(new ProcessStartInfo(ping, "-n 3 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        card = new ScriptedCard();
        var left = Supervised(gone.Id, Forever("radio"), Forever("awdld"), _ => { }, card);
        Expect(left.Code == 0 && Field(left.Ended, "ended") == "owner" && card.Calls.LastOrDefault() == "give back"
               && Field(left.Ended, "wifi") == "back", "the app is gone -> the daemons stop and Wi-Fi is given back");

        // Run from a clone of the repository: no card is touched.
        var plain = Supervised(Environment.ProcessId, Forever("radio"), Forever("awdld"), stop =>
        {
            Thread.Sleep(1500);
            stop.Set();
        });
        Expect(plain.Code == 0 && Field(plain.Ended, "wifi") == "untouched", "without a card to move, Wi-Fi is left untouched");
        Expect(Process.GetProcessesByName("PING").Length == 0, "and no daemon is left running after the card checks");

        // The elevated copy that the app starts moves the card in an installed copy only.
        var installedRoot = Directory.CreateDirectory(Path.Combine(logs, "installed-copy")).FullName;
        var (installedCard, installedInf) = RadioSupervisor.CardFor(installedRoot);
        var (cloneCard, cloneInf) = RadioSupervisor.CardFor(Repo.Find()!);
        Expect(installedCard is WindowsCard && installedInf == WifiCard.PackageInf(installedRoot)
               && cloneCard is null && cloneInf == "",
               "the radio's copy moves the card from an installed copy, and leaves it from a clone");

        // Windows fails in a way other than a CardException: the card still ends with Wi-Fi, or says it did not.
        card = new ScriptedCard { Breaks = new IOException("pnputil could not be started") };
        var broken = Supervised(Environment.ProcessId, Forever("radio"), Forever("awdld"), _ => { }, card, untaken);
        Expect(broken.Code == 4 && Field(broken.Ended, "ended") == "card" && Field(broken.Ended, "why").Contains("pnputil could not be started"),
               $"taking the card breaks -> exit {broken.Code}, nothing started: \"{Field(broken.Ended, "why")}\"");
        var breaksLater = new ScriptedCard();
        var cut = Supervised(Environment.ProcessId, Forever("radio"), Forever("awdld"), stop =>
        {
            Thread.Sleep(1500);
            breaksLater.Breaks = new System.Xml.XmlException("not XML");
            stop.Set();
        }, breaksLater);
        Expect(cut.Code == 5 && Field(cut.Ended, "step") == "done" && Field(cut.Ended, "wifi") == "off",
               $"giving the card back breaks -> exit {cut.Code}, wifi {Field(cut.Ended, "wifi")}: \"{Field(cut.Ended, "why")}\"");

        // Two copies never move the card at once: one that finds the card being moved waits, then gives up.
        using (var other = new Hold())
        {
            card = new ScriptedCard();
            var busy = Supervised(Environment.ProcessId, Forever("radio"), Forever("awdld"), _ => { }, card, untaken,
                                  TimeSpan.FromSeconds(1));
            Expect(busy.Code == 4 && card.Calls.Count == 0
                   && Field(busy.Ended, "why") == "Another winawdl is still moving the Wi-Fi card. Try again in a moment.",
                   $"another copy is moving the card -> this one does not touch it: \"{Field(busy.Ended, "why")}\"");
        }
        Expect(Process.GetProcessesByName("PING").Length == 0, "and no daemon is left running after the broken cards");

        var noRepo = new EngineProcess(System.Windows.Threading.Dispatcher.CurrentDispatcher, null, []);
        string? why = null;
        noRepo.Stopped += (reason, _) => why = reason;
        noRepo.Start("x", Path.GetTempPath(), true);
        Expect(why == "no_engine", "an app that cannot find its engine says so and starts nothing");
    }
}
