using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Winawdl.Radio;

/// <summary>Who has the RTL8852CE: Windows' Wi-Fi (its vendor's driver), or winawdl's driver.</summary>
public enum CardHolder { Wifi, Winawdl, Missing }

/// <summary>The cause, in the app's words, of a card that could not be moved.</summary>
public sealed class CardException(string message) : Exception(message);

/// <summary>The one Wi-Fi card, which winawdl's radio and Windows' Wi-Fi take turns with.</summary>
public interface ICard
{
    CardHolder Holder();
    /// <summary>Puts winawdl's driver package from `inf` on the card. Throws CardException.</summary>
    void Take(string inf);
    /// <summary>Gives the card back to its own driver and waits until it has started. Throws CardException.</summary>
    void GiveBack();
}

public static class WifiCard
{
    public const string HardwareId = @"PCI\VEN_10EC&DEV_C852";
    /// <summary>The installed copy's certificate. The development package is signed by another and is never touched.</summary>
    public const string Signer = "winawdl driver (signed on this PC)";
    public const string InfName = "winawdl_regproxy.inf";

    /// <summary>The entry point the startup task, the installers and the app's "Give Wi-Fi back" run.</summary>
    public const string GiveBackFlag = "--give-wifi-back";

    public const string Busy = "Another winawdl is still moving the Wi-Fi card. Try again in a moment.";
    /// <summary>Long enough for another copy to stop its radio and give the card back.</summary>
    public static readonly TimeSpan WaitForOthers = TimeSpan.FromSeconds(60);

    /// <summary>Held by whichever copy of winawdl is moving the card, across sessions and
    /// accounts: the app's radio copy, the startup task, an installer.</summary>
    public static IDisposable Lock(TimeSpan wait)
    {
        var mutex = new Mutex(false, @"Global\winawdl-wifi-card");
        try
        {
            if (!mutex.WaitOne(wait))
            {
                mutex.Dispose();
                throw new CardException(Busy);
            }
        }
        catch (AbandonedMutexException)
        {
            // A copy that held it was killed: the card is this one's to look at now.
        }
        return new Held(mutex);
    }

    private sealed class Held(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    /// <summary>Gives the card back if winawdl's driver has it, and says what was done in wifi.log.
    /// Returns 0 when Wi-Fi has the card (or there is none), 1 when it could not be given back.</summary>
    public static int GiveBackNow(ICard card, string logs, TimeSpan? wait = null)
    {
        Directory.CreateDirectory(logs);
        void Say(string line) =>
            File.AppendAllLines(Path.Combine(logs, "wifi.log"), [$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}"]);
        try
        {
            using var held = Lock(wait ?? WaitForOthers);
            var holder = card.Holder();
            if (holder != CardHolder.Winawdl)
            {
                Say($"nothing to give back ({(holder == CardHolder.Wifi ? "Wi-Fi has the card" : "no card")})");
                return 0;
            }
            card.GiveBack();
            Say("gave Wi-Fi back");
            return 0;
        }
        catch (Exception error)
        {
            // Whatever Windows threw, the line says it: the installers and the app show it.
            Say($"could not give Wi-Fi back: {error.Message}");
            return 1;
        }
    }

    public static string PackageInf(string root) => Path.Combine(root, "driver", InfName);

    /// <summary>Published names (oemNN.inf) of the installed copy's packages, from `pnputil /enum-drivers /format xml`.</summary>
    public static IReadOnlyList<string> OwnPackages(string driversXml) =>
        [.. XDocument.Parse(driversXml).Descendants("Driver")
            .Where(driver => (string?)driver.Element("OriginalName") == InfName
                             && (string?)driver.Element("SignerName") == Signer)
            .Select(driver => (string?)driver.Attribute("DriverName") ?? "")
            .Where(name => name.Length > 0)];

    /// <summary>Who holds the card, from `pnputil /enum-devices /deviceid ... /format xml` and the driver store.</summary>
    public static CardHolder HolderFrom(string devicesXml, string driversXml)
    {
        var card = XDocument.Parse(devicesXml).Descendants("Device").FirstOrDefault();
        if (card is null) return CardHolder.Missing;
        var driver = (string?)card.Element("DriverName") ?? "";
        return OwnPackages(driversXml).Contains(driver, StringComparer.OrdinalIgnoreCase)
            ? CardHolder.Winawdl
            : CardHolder.Wifi;
    }

    /// <summary>The card has its own driver and Windows has started it.</summary>
    public static bool StartedWithWifi(string devicesXml, string driversXml) =>
        HolderFrom(devicesXml, driversXml) == CardHolder.Wifi
        && (string?)XDocument.Parse(devicesXml).Descendants("Device").First().Element("Status") == "Started";

    /// <summary>Whose saved networks a new Wi-Fi adapter gets: those of the adapter Wi-Fi had before
    /// the radio took the card, or, if it had none or none was noted, the newest set left by an
    /// adapter that is gone. Null when no adapter that is gone left any.</summary>
    public static string? ProfilesSource(string? before, IReadOnlyList<ProfileFolder> folders, IReadOnlyCollection<string> live)
    {
        var gone = folders.Where(folder => folder.Count > 0 && !live.Contains(folder.Adapter, StringComparer.OrdinalIgnoreCase))
                          .ToList();
        return gone.FirstOrDefault(folder => string.Equals(folder.Adapter, before, StringComparison.OrdinalIgnoreCase))?.Adapter
               ?? gone.OrderByDescending(folder => folder.Written).FirstOrDefault()?.Adapter;
    }
}

/// <summary>The folder where Windows keeps one adapter's saved Wi-Fi networks.</summary>
public sealed record ProfileFolder(string Adapter, DateTime Written, int Count);

/// <summary>The card as Windows has it, moved with pnputil and newdev.dll.</summary>
public sealed class WindowsCard : ICard
{
    private const int RestartNeeded = 3010;
    private static readonly TimeSpan BackWithin = TimeSpan.FromSeconds(20);

    public CardHolder Holder() => WifiCard.HolderFrom(Devices(), Drivers());

    public void Take(string inf)
    {
        var holder = Holder();
        if (holder == CardHolder.Missing) throw new CardException("The Wi-Fi card was not found.");
        // Wi-Fi's adapter goes with the card, and its saved networks are filed under it: noted to bring them back.
        if (holder == CardHolder.Wifi && Adapter() is { } adapter)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NotedAdapter)!);
            File.WriteAllText(NotedAdapter, adapter);
        }
        var (added, _) = PnpUtil("/add-driver", inf);
        if (added != 0)
            throw new CardException($"Windows would not take winawdl's driver (pnputil {added}). Its signature may not be trusted on this PC: install winawdl again.");
        const uint force = 1;
        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, WifiCard.HardwareId, inf, force, out var reboot))
            throw new CardException($"Windows would not give the Wi-Fi card to winawdl's driver (error 0x{Marshal.GetLastWin32Error():X8}).");
        if (reboot) throw new CardException("Windows wants a restart before the card works with winawdl's driver.");
    }

    public void GiveBack()
    {
        foreach (var package in WifiCard.OwnPackages(Drivers()))
        {
            var (code, _) = PnpUtil("/delete-driver", package, "/uninstall", "/force");
            if (code == RestartNeeded) throw new CardException("Windows wants a restart to finish giving Wi-Fi back.");
            if (code != 0) throw new CardException($"Windows would not remove winawdl's driver (pnputil {code}).");
        }
        PnpUtil("/scan-devices");
        var clock = Stopwatch.StartNew();
        while (!WifiCard.StartedWithWifi(Devices(), Drivers()))
        {
            if (clock.Elapsed > BackWithin) throw new CardException("The Wi-Fi card did not come back within 20 seconds.");
            Thread.Sleep(500);
        }
        // Wi-Fi is back whatever happens to the saved networks: a failure there is only written down.
        try
        {
            Say(CarryProfiles());
        }
        catch (Exception error)
        {
            Say($"saved networks not brought back: {error.Message}");
        }
    }

    private static string NotedAdapter => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "winawdl", "wifi-adapter.txt");
    private static string ProfileFolders => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Wlansvc", "Profiles", "Interfaces");

    private static void Say(string line) =>
        File.AppendAllLines(Path.Combine(RadioSupervisor.LogFolder, "wifi.log"), [$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}"]);

    /// <summary>The adapter Windows made for the card (its NetCfgInstanceId), while Wi-Fi's driver has it.</summary>
    private static string? Adapter()
    {
        var instance = (string?)XDocument.Parse(Devices()).Descendants("Device").FirstOrDefault()?.Attribute("InstanceId");
        if (instance is null) return null;
        using var device = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instance}");
        if (device?.GetValue("Driver") is not string driver) return null;
        using var software = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driver}");
        return software?.GetValue("NetCfgInstanceId") as string;
    }

    /// <summary>Gives the new adapter the saved networks of the one Wi-Fi had before (see WifiCard.ProfilesSource).</summary>
    private static string CarryProfiles()
    {
        var noted = File.Exists(NotedAdapter) ? File.ReadAllText(NotedAdapter).Trim() : null;
        File.Delete(NotedAdapter);
        var clock = Stopwatch.StartNew();
        string? adapter = null, name = null;
        while ((adapter = Adapter()) is null
               || (name = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(one => one.Id == adapter)?.Name) is null)
        {
            if (clock.Elapsed > BackWithin) return "saved networks not brought back: the new Wi-Fi adapter did not appear";
            Thread.Sleep(500);
        }
        if (!Directory.Exists(ProfileFolders)) return "no saved networks to bring back";
        var folders = Directory.GetDirectories(ProfileFolders)
            .Select(folder => new ProfileFolder(Path.GetFileName(folder), Directory.GetLastWriteTimeUtc(folder),
                                                Directory.GetFiles(folder, "*.xml").Length))
            .ToList();
        // A new adapter that already has saved networks is left as it is.
        if (folders.Any(folder => folder.Adapter.Equals(adapter, StringComparison.OrdinalIgnoreCase) && folder.Count > 0))
            return "the Wi-Fi adapter kept its saved networks";
        var live = NetworkInterface.GetAllNetworkInterfaces().Select(one => one.Id).ToList();
        if (WifiCard.ProfilesSource(noted, folders, live) is not { } source) return "no saved networks to bring back";
        var files = new DirectoryInfo(Path.Combine(ProfileFolders, source)).GetFiles("*.xml")
            .OrderBy(file => file.LastWriteTimeUtc).ToList();
        var added = 0;
        // Windows' Wi-Fi service may not know the new adapter for a moment yet: waited for once, not per network.
        var patience = Stopwatch.StartNew();
        foreach (var file in files)
        {
            bool taken;
            while (!(taken = Netsh("wlan", "add", "profile", $"filename={file.FullName}", $"interface={name}", "user=all") == 0)
                   && added == 0 && patience.Elapsed < TimeSpan.FromSeconds(10))
                Thread.Sleep(1000);
            if (taken) added++;
            else if (added == 0) break; // the service never took one: the rest would not go either
        }
        return $"brought back {added} of {files.Count} saved networks from adapter {source}";
    }

    private static int Netsh(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string Devices() => PnpUtil("/enum-devices", "/deviceid", WifiCard.HardwareId, "/format", "xml").Output;
    private static string Drivers() => PnpUtil("/enum-drivers", "/format", "xml").Output;

    private static (int Code, string Output) PnpUtil(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result);
    }

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(
        IntPtr window, string hardwareId, string infPath, uint flags, out bool rebootNeeded);
}
