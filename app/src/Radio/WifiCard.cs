using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;

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
}

/// <summary>The card as Windows has it, moved with pnputil and newdev.dll.</summary>
public sealed class WindowsCard : ICard
{
    private const int RestartNeeded = 3010;
    private static readonly TimeSpan BackWithin = TimeSpan.FromSeconds(20);

    public CardHolder Holder() => WifiCard.HolderFrom(Devices(), Drivers());

    public void Take(string inf)
    {
        if (Holder() == CardHolder.Missing) throw new CardException("The Wi-Fi card was not found.");
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
