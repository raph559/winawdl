using Winawdl.Model;

namespace Winawdl.Shell;

/// <summary>Quit from the notification area. While the radio has the Wi-Fi card, the app
/// stays there, its window hidden, until Wi-Fi is back; if Wi-Fi cannot be given back, it
/// says so from there and stays until it is quit again.</summary>
public sealed class Leaving(IRadio radio, Action shutdown, Action<string, Action?> say, Action showProblem)
{
    private bool _waiting;
    private bool _told;

    public void Quit()
    {
        if (_waiting && radio.Phase == RadioPhase.WifiLeftOff || !radio.SwapsCard
            || radio.Phase is RadioPhase.Off or RadioPhase.Refused or RadioPhase.NotBuilt or RadioPhase.Failed
                           or RadioPhase.WifiLeftOff)
        {
            Go();
            return;
        }
        if (_waiting) return;
        _waiting = true;
        radio.Changed += Look;
        if (radio.Phase is RadioPhase.TakingCard or RadioPhase.Starting or RadioPhase.Running) radio.Stop();
        Look();
    }

    private void Look()
    {
        if (radio.Phase == RadioPhase.Off) Go();
        else if (radio.Phase == RadioPhase.WifiLeftOff && !_told)
        {
            _told = true;
            say("Wi-Fi is still off. Press here to give it back.", showProblem);
        }
    }

    private void Go()
    {
        radio.Changed -= Look;
        shutdown();
    }
}
