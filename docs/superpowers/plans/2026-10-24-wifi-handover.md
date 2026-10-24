# Wi-Fi handover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Radio switch of an installed winawdl takes the RTL8852CE from Windows' Wi-Fi when turned on and gives it back when the radio stops, on Quit, and at boot; installing no longer takes Wi-Fi.

**Architecture:** A small `ICard` seam (who holds the card; take it; give it back) with one Windows implementation over `pnputil` and `newdev.dll`. The elevated `RadioSupervisor` takes the card before the daemons start and gives it back after both have exited, reporting each step in `radio.status`. A new `--give-wifi-back` entry point serves the boot task, the installer and the "Give Wi-Fi back" press. `RadioControl` and `TableModel` grow three phases; a `Leaving` class keeps the app in the notification area after Quit until Wi-Fi is back.

**Tech Stack:** C# / .NET 10 WPF (`app/`), Windows PowerShell 5.1 scripts (`installer/`, `scripts/`), Inno Setup 6. Checks are the app's own `winawdl.exe --check` (`app/src/Dev/Checks.cs`), run by `scripts\build-app.ps1 -Check`.

**Spec:** [docs/superpowers/specs/2026-10-24-wifi-handover-design.md](../specs/2026-10-24-wifi-handover-design.md)

## Global Constraints

- One card, hardware ID `PCI\VEN_10EC&DEV_C852`.
- winawdl's own driver package: original name `winawdl_regproxy.inf`, signer name `winawdl driver (signed on this PC)` (certificate subject `CN=winawdl driver (signed on this PC)`). Only packages with that signer are ever bound or removed. The development package (`CN=winawdl driver signing (local test)`) is never touched.
- Only an installed copy swaps the card: when `Repo.IsSourceTree(root)` is true, no `ICard` is used, nothing about the card is shown, and the radio runs as today.
- No new permission prompt: the swap happens inside the existing elevated `--radio` copy; the app never elevates at start.
- The swap never happens while a daemon still runs: give back only after both daemon processes have exited.
- Logs and status: `%ProgramData%\winawdl\logs` (`RadioSupervisor.LogFolder`); the hand-back writes `wifi.log` there.
- Version 0.4.0.
- Words follow `app/DESIGN.md` ("Words": who and what; a failure names its cause and the next thing to do; plain verbs; the shield on a press that leads to a permission prompt). Load the antislop skills (mode: during) for Tasks 5, 6 and 8, per the project's memory.
- Never close the owner's running app to build: build with `dotnet build ... -p:OutputPath=<another folder>` if `build\app\winawdl\winawdl.exe` is in use. Anything that touches the real card is run by the owner (Task 9). Commit per task; never push without the owner's go.

## Review Focus

1. **A daemon that ignores Ctrl+C** and is killed after 5 s: the card must still be given back, and only after the kill has completed. Test in Task 2.
2. **Taking the card fails** (package missing, signature not trusted, card absent): the daemons are not started, the card ends with Wi-Fi, and the panel names the cause. Test in Task 2 and Task 5.
3. **Giving back fails, or Windows asks for a restart:** the phase ends as `WifiLeftOff` with the cause, and after Quit the app stays in the notification area. Tests in Tasks 2, 4 and 6.
4. **The app is killed** (owner process gone) while the radio runs: the supervisor still gives the card back. Test in Task 2.
5. **The switch is flipped on again while the card is being given back:** no second supervisor starts until the phase is `Off`. Test in Task 5.

---

### Task 1: The card seam and its Windows implementation

**Files:**
- Create: `app/src/Radio/WifiCard.cs`
- Modify: `app/src/Dev/Stage.cs` (add `ScriptedCard`)
- Test: `app/src/Dev/Checks.cs` (new `Card()` section, called from `Run` before `Supervisor`)

**Interfaces:**
- Produces:
  - `public enum CardHolder { Wifi, Winawdl, Missing }` (in `Winawdl.Radio`)
  - `public interface ICard { CardHolder Holder(); void Take(string inf); void GiveBack(); }`; `Take` and `GiveBack` throw `CardException` (message: the cause in the app's words).
  - `public sealed class CardException(string message) : Exception(message)`
  - `public static class WifiCard` with `const string HardwareId = @"PCI\VEN_10EC&DEV_C852"`, `const string Signer = "winawdl driver (signed on this PC)"`, `const string InfName = "winawdl_regproxy.inf"`, `static string PackageInf(string root)` (= `root\driver\winawdl_regproxy.inf`), `static IReadOnlyList<string> OwnPackages(string pnputilXml)` (published names, e.g. `oem66.inf`, whose `OriginalName` is `InfName` and `SignerName` is `Signer`).
  - `public sealed class WindowsCard : ICard`
  - `ScriptedCard : ICard` in `Stage.cs`: `CardHolder Now { get; set; }`, `string? TakeFails`, `string? GiveBackFails`, `List<string> Calls` (records `"take <inf>"`, `"give back"`).

- [ ] **Step 1: Write the failing checks** in `Checks.Card()`:

```csharp
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
```

- [ ] **Step 2: Build and run the checks; they fail to compile** (`WifiCard` does not exist).

Run: `dotnet build app\winawdl.csproj -c Release -nologo -v q`
Expected: error CS0103 for `WifiCard`.

- [ ] **Step 3: Implement `WifiCard`, `CardHolder`, `CardException`, `ICard`, `WindowsCard`, and `ScriptedCard`.**

`WindowsCard`:
- `Holder()`: the card from `Win32_PnPEntity` whose `PNPDeviceID` starts with `HardwareId` (none → `Missing`); its driver package from `Win32_PnPSignedDriver.InfName` for that device; `Winawdl` if that name is in `OwnPackages(pnputil /enum-drivers /format xml)`, else `Wifi`. Needs no administrator rights (run the check as the app does).
- `Take(inf)`: card missing → `CardException("The Wi-Fi card was not found.")`; `pnputil /add-driver "<inf>"` (non-zero exit → `CardException` naming the exit code and that the driver's signature may not be trusted); then `UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, HardwareId, inf, 1 /* INSTALLFLAG_FORCE */, out reboot)` as `installer\install.ps1` does; failure → `CardException` with the Win32 error in hex; `reboot` true → `CardException("Windows wants a restart before the card works with winawdl's driver.")`.
- `GiveBack()`: for each of `OwnPackages(...)`: `pnputil /delete-driver <name> /uninstall /force`; then `pnputil /scan-devices`; then poll every 500 ms for up to 20 s until `Holder()` is `Wifi` and the card's `ConfigManagerErrorCode` is 0. pnputil exit code 3010 (restart needed) or a timeout → `CardException("Windows wants a restart to finish giving Wi-Fi back.")` or `CardException("The Wi-Fi card did not come back within 20 seconds.")`.
- Run `pnputil.exe` from `Environment.SystemDirectory` with `UseShellExecute = false`, `CreateNoWindow = true`, output read in full.

- [ ] **Step 4: Build and run the checks.**

Run: `.\scripts\build-app.ps1 -Check`
Expected: the three new lines `PASS`, `ALL n PASSED` for check and drive.

- [ ] **Step 5: Commit**

```bash
git add app/src/Radio/WifiCard.cs app/src/Dev/Stage.cs app/src/Dev/Checks.cs
git commit -m "Know who holds the Wi-Fi card, and take it or give it back"
```

---

### Task 2: The supervisor takes the card first and gives it back last

**Files:**
- Modify: `app/src/Radio/RadioSupervisor.cs` (`Run`, `Supervise`, status file)
- Test: `app/src/Dev/Checks.cs` (`Supervisor`)

**Interfaces:**
- Consumes: `ICard`, `CardException`, `WifiCard.PackageInf`, `ScriptedCard` (Task 1).
- Produces:
  - `Supervise(int ownerPid, string stopName, Daemon radio, Daemon awdl, string logs, ICard? card = null, string inf = "")`. With `card` null it behaves exactly as today.
  - `radio.status` is JSON written at each step: `{"step":"taking"}` before `Take`, `{"step":"running"}` once both daemons are launched, `{"step":"giving"}` before `GiveBack`, and at the end `{"step":"done","ended":<as today, or "card" if Take failed>,"wifi":"back"|"off"|"untouched","why":<CardException message or "">}`. `"untouched"` when `card` is null.
  - Exit codes: 0 asked stop or owner gone, 3 a daemon ended (as today), 4 the card could not be taken, 5 the card could not be given back (wins over 0 and 3).
  - `Run` passes `new WindowsCard()` and `WifiCard.PackageInf(root)` unless `Repo.IsSourceTree(root)`.

- [ ] **Step 1: Write the failing checks** (in `Supervisor`, after the existing ones, with `ping` stand-ins as today and a `ScriptedCard`):
  - asked to stop → `card.Calls` is `["take <inf>", "give back"]`, exit 0, status `step` `done`, `wifi` `back`.
  - `"give back"` is recorded only after both stand-in daemons have exited: the `ScriptedCard` notes, when `GiveBack` runs, whether any `PING` process is still running; expect none.
  - a speaker daemon that ignores Ctrl+C (PowerShell `Start-Sleep 30` with `[Console]::TreatControlCAsInput = $true`) → still `"give back"` last, after the kill.
  - `TakeFails = "The Wi-Fi card was not found."` → no daemon started (no `PING` running during the run, `radio.log` absent), exit 4, `ended` `card`, `why` that message, no `"give back"` call (the card never left Wi-Fi), `card.Now` still `Wifi`.
  - `GiveBackFails = "Windows wants a restart to finish giving Wi-Fi back."` → exit 5, `wifi` `off`, `why` that message.
  - owner gone (the existing orphaned case) with a card → `"give back"` called, `wifi` `back`.
  - `card` null → status `wifi` `untouched` and the existing expectations unchanged.

- [ ] **Step 2: Run the checks; the new ones fail.**

Run: `.\scripts\build-app.ps1 -Check`
Expected: compile error on the new `Supervise` parameters, then (after adding them as unused) `FAIL` lines for each case above.

- [ ] **Step 3: Implement the order in `Supervise`**: write `taking`, `card.Take(inf)` (on `CardException`: write `done` with `ended` `card`, try `GiveBack` only if `Holder()` is `Winawdl`, return 4); launch as today; write `running`; wait as today; stop the daemons as today and `WaitForExit()` every one; write `giving`; `GiveBack()` (on `CardException` → `wifi` `off`, exit 5); write `done`. Append one line per step to `supervisor.log` as today's notes do.

- [ ] **Step 4: Run the checks.**

Run: `.\scripts\build-app.ps1 -Check`
Expected: `ALL n PASSED`.

- [ ] **Step 5: Commit**

```bash
git add app/src/Radio/RadioSupervisor.cs app/src/Dev/Checks.cs
git commit -m "Take the Wi-Fi card before the radio starts, give it back after it stops"
```

---

### Task 3: `--give-wifi-back`

**Files:**
- Modify: `app/src/Radio/WifiCard.cs`, `app/src/Program.cs`
- Test: `app/src/Dev/Checks.cs` (`Card`)

**Interfaces:**
- Consumes: `ICard`, `WindowsCard` (Task 1).
- Produces: `WifiCard.GiveBackFlag = "--give-wifi-back"`; `public static int WifiCard.GiveBackNow(ICard card, string logs)`: `Holder()` `Wifi` or `Missing` → appends `"<time> nothing to give back (<holder>)"` to `logs\wifi.log`, returns 0; `Winawdl` → `GiveBack()`, appends `"<time> gave Wi-Fi back"` and returns 0, or appends `"<time> could not give Wi-Fi back: <message>"` and returns 1. `Program.Main` maps `[WifiCard.GiveBackFlag]` to `WifiCard.GiveBackNow(new WindowsCard(), RadioSupervisor.LogFolder)`, before the single-instance mutex (it must run while the app is open).

- [ ] **Step 1: Write the failing checks:** with `ScriptedCard` `Now = Winawdl` → returns 0, one `"give back"` call, `wifi.log` last line ends with `gave Wi-Fi back`; `Now = Wifi` → 0, no call; `Now = Missing` → 0, no call; `GiveBackFails = "x"` → 1 and the line ends with `could not give Wi-Fi back: x`.
- [ ] **Step 2: Run; they fail** (`GiveBackNow` missing).
- [ ] **Step 3: Implement `GiveBackNow` and the `Program.Main` case.**
- [ ] **Step 4: Run.** Expected: `ALL n PASSED`.
- [ ] **Step 5: Commit**

```bash
git add app/src/Radio/WifiCard.cs app/src/Program.cs app/src/Dev/Checks.cs
git commit -m "Add --give-wifi-back, for the boot task, the installer and the app"
```

---

### Task 4: `RadioControl` follows the card

**Files:**
- Modify: `app/src/Model/Seams.cs`, `app/src/Radio/RadioControl.cs`, `app/src/Dev/Stage.cs` (`ScriptedRadio`)
- Test: `app/src/Dev/Checks.cs` (`States`)

**Interfaces:**
- Consumes: `radio.status` steps and exit codes (Task 2), `WifiCard.GiveBackFlag` (Task 3), `ICard.Holder()`.
- Produces:
  - `RadioPhase` gains `TakingCard`, `GivingBack`, `WifiLeftOff`: `{ Off, TakingCard, Starting, Running, Stopping, GivingBack, Refused, NotBuilt, Failed, WifiLeftOff }`.
  - `IRadio` gains `bool SwapsCard { get; }` (true for an installed copy) and `void GiveWifiBack()`.
  - `RadioControl(Dispatcher, string? repo, ICard? card = null)`: if `card` is not null and `card.Holder()` is `Winawdl` when constructed → phase `WifiLeftOff`, `LastLine` "Wi-Fi was left off when the radio last stopped." `Program.RunApp` passes `new WindowsCard()` unless the repo is a source tree.
  - While the supervisor runs, `Look()` maps `step` `taking` → `TakingCard`, `running` → `Starting` (then `Running` on `Linked(true)` as today), `giving` → `GivingBack`. On exit: `wifi` `off` → `WifiLeftOff` with `LastLine` = `why`; `ended` `card` → `Failed` with `LastLine` = `why`; else today's logic.
  - `GiveWifiBack()`: starts `winawdl.exe --give-wifi-back` with `Verb = "runas"`, phase `GivingBack`, waits for exit on the watch timer; 0 → `Off`; else `WifiLeftOff` with the last line of `wifi.log`; user cancelled (1223) → stays `WifiLeftOff`.
  - `ScriptedRadio` gains `SwapsCard { get; set; }`, `int GiveBacks`, `GiveWifiBack()` → `GiveBacks++` and `Become(GivingBack)`.

- [ ] **Step 1: Write the failing checks** in `States` against `ScriptedRadio` only for what the model reads (Task 5 covers the panel); and a `RadioControl` check: given a temp log folder with `radio.status` `{"step":"done","ended":"stopped","wifi":"off","why":"x"}`, the phase mapping function returns `WifiLeftOff` with `x`. Make the mapping a `internal static (RadioPhase, string) RadioControl.Ended(string statusJson, bool wasStopping)` so it can be checked without a process.
- [ ] **Step 2: Run; they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: `ALL n PASSED`.
- [ ] **Step 5: Commit**

```bash
git add app/src/Model/Seams.cs app/src/Radio/RadioControl.cs app/src/Dev/Stage.cs app/src/Dev/Checks.cs app/src/Program.cs
git commit -m "Follow the card in the radio's phases, and offer to give Wi-Fi back"
```

---

### Task 5: What the window says

**Files:**
- Modify: `app/src/Model/TableModel.cs` (`Across`, `PanelTitle`, `PanelBody`, `PanelActionLabel`, `PanelAsksWindows`, `RunPanelAction`, `RadioOn`, `TrayText`)
- Test: `app/src/Dev/Checks.cs` (`States`)

**Interfaces:**
- Consumes: the new phases and `SwapsCard`, `GiveWifiBack()` (Task 4).
- Produces (exact copy; antislop may tighten it but tests pin what lands):

| Phase | Across | Title | Body | Action |
| --- | --- | --- | --- | --- |
| `TakingCard` | `RadioStarting` | Starting the radio | Taking the Wi-Fi card from Windows. Wi-Fi is off until the radio is turned off. | none |
| `GivingBack` | `RadioOff` | Giving Wi-Fi back | The radio has stopped. The Wi-Fi card is going back to Windows. | none |
| `WifiLeftOff` | `RadioProblem` | Wi-Fi is still off | `LastLine` + " Restarting the PC also gives it back." | Give Wi-Fi back (shield) |
| `Off`, `SwapsCard` | `RadioOff` | The radio is off | Turn it on to see nearby iPhones, iPads and Macs, and to be seen by them. It uses the Wi-Fi card, so Wi-Fi is off while the radio is on. Windows asks for permission first. | Turn on the radio |

  - `RadioOn` reads true in `TakingCard`; setting it true does nothing in `Stopping`, `GivingBack` (Review Focus 5); in `WifiLeftOff` it starts the radio (the supervisor takes the card anyway).
  - `RunPanelAction` in `WifiLeftOff` calls `GiveWifiBack()`.
  - `TrayText` in `GivingBack`: `"winawdl\nGiving Wi-Fi back"`; in `WifiLeftOff`: `"winawdl\nWi-Fi is still off"`.
  - `SwapsCard` false (a clone): today's off body unchanged.

- [ ] **Step 1: Write the failing checks** with `ScriptedRadio { SwapsCard = true }`: each table row's title, body and action label; `PanelAsksWindows` true for Give Wi-Fi back; pressing it → `GiveBacks == 1`; `RadioOn = true` during `GivingBack` → `Starts` unchanged; tray texts; and with `SwapsCard = false` the off body equals today's text.
- [ ] **Step 2: Run; they fail.**
- [ ] **Step 3: Implement.** Read `app/DESIGN.md` "Words" first.
- [ ] **Step 4: Run.** Expected: `ALL n PASSED`. Then render the panel states with `winawdl.exe` and `Dev.Stage.Flag` (as `Stage.cs` does for the radio states; add `radio-taking`, `radio-giving-back`, `wifi-left-off` there) and look at the pictures in light and dark.
- [ ] **Step 5: Commit**

```bash
git add app/src/Model/TableModel.cs app/src/Dev/Stage.cs app/src/Dev/Checks.cs
git commit -m "Say when the Wi-Fi card moves, and when Wi-Fi was left off"
```

---

### Task 6: Quit waits for Wi-Fi

**Files:**
- Create: `app/src/Shell/Leaving.cs`
- Modify: `app/src/Program.cs` (the `Tray` quit action, `window.Closed`), `app/src/Views/MainWindow.cs` (`Tray.Say` already exists; use it)
- Test: `app/src/Dev/Checks.cs` (new `Quitting()` section)

**Interfaces:**
- Consumes: `IRadio` with the new phases (Task 4).
- Produces: `public sealed class Leaving(IRadio radio, Action shutdown, Action<string, Action?> say, Action showProblem)` with `void Quit()`: if the phase is `Off`, `Refused`, `NotBuilt`, `Failed` or `!radio.SwapsCard` → `shutdown()` now. Otherwise stop the radio if it runs, and on each `radio.Changed`: `Off` → `shutdown()`; `WifiLeftOff` → `say("Wi-Fi is still off. Press here to give it back.", showProblem)` once, and do not shut down. A second `Quit()` while `WifiLeftOff` → `shutdown()`.
  `Program.RunApp`: the tray's quit action becomes `window.Quitting = true; window.Close(); leaving.Quit();`, and `window.Closed` shuts the app down only when not quitting (closing to the tray already hides instead of closing when `KeepInTray` is on). `showProblem` = `window.Surface`.

- [ ] **Step 1: Write the failing checks** with `ScriptedRadio { SwapsCard = true }` in `Running`: `Quit()` → `shutdown` not yet called; `Become(GivingBack)` → not yet; `Become(Off)` → called once. Another run: `Become(WifiLeftOff, "x")` → `say` called once with that text, no shutdown; `Quit()` again → shutdown. Radio `Off` at Quit → shutdown at once. `SwapsCard = false` and `Running` → shutdown at once (the supervisor still stops the daemons when the owner goes, as today).
- [ ] **Step 2: Run; they fail.**
- [ ] **Step 3: Implement `Leaving` and the wiring.**
- [ ] **Step 4: Run.** Expected: `ALL n PASSED`.
- [ ] **Step 5: Commit**

```bash
git add app/src/Shell/Leaving.cs app/src/Program.cs app/src/Dev/Checks.cs
git commit -m "After Quit, stay in the notification area until Wi-Fi is back"
```

---

### Task 7: Install no longer takes Wi-Fi; a boot task gives it back

**Files:**
- Modify: `installer/install.ps1`, `installer/uninstall.ps1`, `installer/winawdl.iss`, `installer/before.txt`, `installer/README.txt`

**Interfaces:**
- Consumes: `winawdl.exe --give-wifi-back` (Task 3).
- Produces: scheduled task `winawdl Wi-Fi` (task path `\`), trigger at system startup, principal SYSTEM, highest run level, action `"<Target>\winawdl.exe" --give-wifi-back`, working directory `<Target>`, battery conditions off.

- [ ] **Step 1: `install.ps1`.** Remove the steps "add the driver package to Windows" and "give the Wi-Fi card to it" and their lines in the opening summary. Keep the certificate and signing step. Add, after the files are in place: "Earlier install: give Wi-Fi back if winawdl's driver has the card" (`& "$Target\winawdl.exe" --give-wifi-back`, waited for, exit code checked; non-zero → throw with the last line of `wifi.log`), and "a task that gives Wi-Fi back when Windows starts" (`Register-ScheduledTask ... -Force`). The summary says Wi-Fi stays as it is until the Radio switch is turned on. The final line: `"Installed. Start winawdl from the Start Menu and turn its Radio switch on: Wi-Fi is off while the radio is on."`
- [ ] **Step 2: `uninstall.ps1`.** After closing the app: run `--give-wifi-back` from `$Target` if present (if it fails, carry on to the package deletion, which gives the card back too), keep the package and certificate steps, add "remove the task that gives Wi-Fi back at startup" (`Unregister-ScheduledTask -TaskName 'winawdl Wi-Fi' -Confirm:$false -ErrorAction SilentlyContinue`).
- [ ] **Step 3: `winawdl.iss`.** Status label → `'Signing the driver on this PC...'`. Failure message: drop "The Wi-Fi card was given back to its own driver." and say Wi-Fi was left as it was. `before.txt` and `README.txt`: replace the "Wi-Fi is down from then until you uninstall" bullet with: the radio uses the Wi-Fi card, so Wi-Fi is off while winawdl's Radio switch is on and comes back when it is turned off, when winawdl quits, and when Windows starts.
- [ ] **Step 4: Verify without touching the card.**

Run (non-elevated): `powershell -NoProfile -ExecutionPolicy Bypass -File installer\install.ps1 -DryRun` from a staged copy (`scripts\build-installer.ps1 -AllowDirty` stages one under `build\installer\winawdl-0.4.0` once Task 8 bumps the version; before that, copy `installer\*.ps1` next to the 0.3.0 staged folder's files in a temp folder).
Expected: the summary has no "give the Wi-Fi card" line, lists the startup task, and ends `Dry run done: nothing was changed.` Same for `uninstall.ps1 -DryRun`, which lists the task removal.

- [ ] **Step 5: Commit**

```bash
git add installer
git commit -m "Leave Wi-Fi alone at install, and give it back at startup"
```

---

### Task 8: 0.4.0, and the documents

**Files:**
- Modify: `app/winawdl.csproj` (`<Version>0.4.0</Version>`), `CHANGELOG.md` (new `## 0.4.0` section), `README.md` (lines 31, 38-39, 158 and anywhere else that says Wi-Fi is down until uninstall), `app/DESIGN.md` only if a new word or state needs recording there.

- [ ] **Step 1:** Update the files. The changelog entry, in the file's own style: the Radio switch takes the Wi-Fi card and gives it back; Quit waits until Wi-Fi is back; Wi-Fi comes back at startup; installing no longer takes Wi-Fi.
- [ ] **Step 2: Verify.**

Run: `.\scripts\build-app.ps1 -Check` (or with `-p:OutputPath` if the owner's build is in use), then `grep -rn "until you uninstall" README.md installer`.
Expected: `ALL n PASSED` for check and drive; no match.

- [ ] **Step 3: Commit**

```bash
git add app/winawdl.csproj CHANGELOG.md README.md app/DESIGN.md
git commit -m "Call this 0.4.0, and say what the Radio switch now does to Wi-Fi"
```

---

### Task 9: On the real card (run by the owner)

**Files:** none changed unless a step fails (then: back to the task that owns the code, with the evidence).

- [ ] **Step 1:** Build the installer from the committed branch: `.\scripts\build-installer.ps1 -Build`. Expected: `build\installer\winawdl-0.4.0-setup.exe`.
- [ ] **Step 2:** The owner runs it (from Explorer, Run as administrator) over the installed 0.3.0, with the card on Wi-Fi. I read `%ProgramData%\winawdl\logs` and the card's holder afterwards. Expected: Wi-Fi up; task `winawdl Wi-Fi` present; no winawdl package bound.
- [ ] **Step 3:** The owner works through the spec's verification list, items 2 to 5 and 7 (item 6, upgrading with the card held, by turning the radio on in 0.3.0 first if they want it covered). After each, I read the card's driver, `supervisor.log`, `wifi.log` and `radio.status`. Expected: each result as the spec states it; no "restart required" anywhere.
- [ ] **Step 4:** Record what was run and seen in `DESIGN-NOTES.md`'s installer section, the way 0.3.0's first real install is recorded there, and commit.

```bash
git add DESIGN-NOTES.md
git commit -m "Record the first real handover of the Wi-Fi card"
```
