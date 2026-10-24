# The Radio switch hands the Wi-Fi card over and back

Design, 2026-10-24.

## Why

winawdl and Windows' Wi-Fi both need the one Realtek RTL8852CE card. Up to
0.3.0 the installer gives the card to winawdl's driver for good: Wi-Fi is down
from the install until the uninstall. The owner uses winawdl now and then and
Wi-Fi the rest of the time, and has been switching by hand with `pnputil` in
an administrator PowerShell. Done by hand it goes wrong: on 2026-10-24 the
driver was removed while the radio still ran, Windows put the card on the
vendor's driver on paper only, no Wi-Fi adapter came up, and Windows asked
for a restart.

The owner asked for the switch to be in the app, and agreed to this shape:
the existing **Radio** switch does it. There is no second control.

## Goals

- **Radio on:** the card is taken from Windows' Wi-Fi and given to winawdl's
  driver, then the radio starts. Wi-Fi goes off at that moment.
- **Radio off, Quit from the notification area icon, the app ending in any
  way, or the radio ending by itself:** the radio daemons stop first, the card
  is free, then it goes back to its own (vendor) driver and Wi-Fi comes back
  by itself, with no restart in the normal case.
- **At boot** (the owner's choice, 2026-10-24): if the PC restarted or
  crashed while the radio was on, Wi-Fi is given back when Windows starts,
  whether or not winawdl is ever opened.
- **Installing no longer takes Wi-Fi.** It puts the signed driver in place;
  Wi-Fi stays as it was until the Radio switch is turned on. Uninstalling
  still undoes everything.

## Non-goals

- Wi-Fi and AirDrop at the same time. The card does one or the other.
- Starting the radio by itself, at login or otherwise.
- Any card other than the RTL8852CE.
- Dragging files out of the Received shelf: a separate feature, designed
  separately.

## Anti-goals: it fails if

- the PC can be left without Wi-Fi with no way back but typed commands;
- "a restart is required" turns up in an ordinary on/off cycle;
- a new permission prompt appears beyond the one turning the radio on
  already causes, or the app asks for administrator rights when it starts
  without being asked to do anything;
- the card is swapped while the radio still has it.

## How it works (decided)

### Who does the swap

The copy of `winawdl.exe` that already runs elevated for the radio
(`RadioSupervisor`, started with `--radio`) does both swaps, because it is
the one place that already has administrator rights and already knows when
the daemons have stopped. So turning the Radio on costs the same one
elevation as today.

Order inside the supervisor:

1. Give the card to winawdl's driver. If the package is not in the driver
   store, add it from the folder it ships in, then force it onto the card
   (the vendor's driver ranks higher, so it has to be forced, as
   `install.ps1` does today with `UpdateDriverForPlugAndPlayDevicesW` and the
   force flag).
2. Start the two daemons, as today.
3. On stop, owner gone, or a daemon ending: stop the daemons as today
   (Ctrl+C, then kill after 5 s), and wait until both have exited.
4. Give the card back to the vendor's driver, and confirm Windows' Wi-Fi
   adapter is present and started.
5. Write the status file as today, including whether the card went back.

If step 1 fails, the daemons are not started and the card is left on (or put
back on) the vendor's driver.

### Where the driver package is, and the clone of the repository

The swap is done by an **installed copy** only: its package is
`<install folder>\driver\winawdl_regproxy.inf`, signed at install time with
the certificate made on the PC (as today), so adding it to the store never
raises Windows' "install this device software?" dialog.

Run from a clone of the repository, the app does no swap: the radio uses the
card as `scripts\install-driver.ps1` left it, as today, and the "Wi-Fi is
still off" panel is not shown. The clone stays a development mode; the
development scripts keep working as they are.

### Two packages with the same name

An installed copy and a clone can both have put a `winawdl_regproxy.inf` in
the driver store, signed with different certificates (`CN=winawdl driver
(signed on this PC)` and `CN=winawdl driver signing (local test)`). The
installed copy tells its own apart by its signer, and only ever adds, binds or
removes its own. "The card is on winawdl's driver" means on the installed
copy's package.

### At boot

`install.ps1` registers a scheduled task that runs at system startup as
SYSTEM: `winawdl.exe --give-wifi-back`. That entry point gives the card back
if it is on winawdl's driver and does nothing otherwise, writing one line to
the logs folder (`%ProgramData%\winawdl\logs`). It runs as SYSTEM, so no
prompt and no signature policy is involved. `uninstall.ps1` removes the task.
The same code path is used by the supervisor's step 4.

### Install, upgrade, uninstall

- `install.ps1` (and the setup program through `install.ps1 -Setup`): copies,
  makes the certificate and signs the driver, adds the package to the driver
  store, adds the firewall rules, the shortcut and the list entry, registers
  the boot task. It does **not** give the card to the driver. On an upgrade
  from 0.3.0, where the card is on winawdl's driver, it gives the card back
  (after the old app and its radio are closed, which setup already does).
- `uninstall.ps1`: closes the app and its radio (as today), gives the card
  back, removes the package, the certificate trust, the firewall rules, the
  boot task, the shortcut, the list entry and the files.
- Every place that tells the user Wi-Fi is down until uninstall changes:
  `installer\before.txt`, `installer\README.txt`, the status line in
  `installer\winawdl.iss` ("Signing the driver on this PC and giving the
  Wi-Fi card to it..."), its failure message ("The Wi-Fi card was given back
  to its own driver"), the repository `README.md` and `CHANGELOG.md`.
- `scripts\install-driver.ps1` / `uninstall-driver.ps1` (the source-tree
  path) keep working as they are for development.

### What the window says

In the app's words (`app/DESIGN.md`: who and what, a failure names its cause
and the next thing to do; plain verbs on buttons; the shield on anything that
leads to a permission prompt).

- **Radio off:** the panel says, before it is turned on, that the radio uses
  the Wi-Fi card, so Wi-Fi is off while the radio is on.
- **Turning on:** while the card is being taken, the panel says so (as the
  starting panel already shows what the radio last wrote), then carries on
  as today.
- **Turning off:** "Stopping the radio", then a line while Wi-Fi is given
  back, then the ordinary off panel once Wi-Fi is back.
- **The card could not be given back:** a problem panel. Title along the
  lines of "Wi-Fi is still off"; body with the cause and that restarting the
  PC also gives it back; a press "Give Wi-Fi back" with the shield. Pressing
  it starts an elevated copy that only gives the card back.
- **Quit while the radio is on** (the owner's choice, 2026-10-24): the window
  goes, but the app stays in the notification area until Wi-Fi is back, with
  the icon saying it is giving Wi-Fi back, then exits. If the hand-back
  fails, it says so from the notification area, offers the same "Give Wi-Fi
  back" press (opening the window on the problem panel), and does not exit
  until Wi-Fi is back or it is quit again.
- **The app starts and finds the card on winawdl's driver with no radio
  running** (the supervisor was killed, or the boot task has not run): the
  same problem panel. The app does not elevate by itself at start.

## Constraints

- One card, PCI hardware ID `PCI\VEN_10EC&DEV_C852`.
- The owner's PC enforces kernel code integrity (WDAC) and had
  `ValidateAdminCodeSignatures` set to 1 until 2026-10-20 (now 0). With it at
  1 the unsigned `winawdl.exe` cannot be elevated and the radio cannot start;
  this design does not change that.
- The app runs without administrator rights, so Explorer can drop files on
  it. That stays.
- Development rules for this repository: the owner's running app is never
  closed to rebuild (build to another output folder); anything that touches
  the real card (binding, unbinding, a real on/off cycle, a reboot test) is
  run by the owner; nothing is pushed without the owner's go.

## Verification

- `winawdl.exe --check` (`app/src/Dev/Checks.cs`) gains the new panel states
  and the supervisor's new order against stand-ins, the way the radio's
  states and the supervisor are checked today: no card is touched.
- `scripts\build-app.ps1 -Check` as today.
- On the real PC, run by the owner, each with the expected result:
  1. Fresh install of the new version: Wi-Fi stays up.
  2. Radio on: Wi-Fi goes off, the radio reaches "ready", a file arrives.
  3. Radio off: Wi-Fi adapter back and connected within seconds, no restart
     asked for.
  4. Radio on, Quit from the notification area icon: the icon stays until
     Wi-Fi is back, then goes.
  5. Radio on, restart the PC without quitting: Wi-Fi back at the sign-in
     screen.
  6. Upgrade over 0.3.0 with the card on winawdl's driver: Wi-Fi back after
     setup.
  7. Uninstall: Wi-Fi up, no task, no package, no certificate trust left.

## Left to the builder

- How the card is given back: deleting winawdl's package from the store and
  rescanning (what `uninstall.ps1` does, and what returned the card on
  2026-10-24 once nothing held it), or forcing the vendor's package back
  while keeping winawdl's in the store. Requirement: no restart in the normal
  case, and it still works after Windows Update replaces the vendor's
  package. Prove it on the real card through the owner.
- How long to wait for Windows' Wi-Fi adapter to appear before calling the
  hand-back failed.
- How the supervisor's progress lines reach the panel (today the panel reads
  the last line of `radio.log`, which the supervisor recreates when the
  daemon starts).
- The exact wording of the new panel lines, within `app/DESIGN.md`.
- Splitting the work into commits.

## Calls made without the owner

- The version: 0.4.0.
- The supervisor (not a third program) does the swap, so there is still only
  one elevation per radio start.
- The boot task runs `winawdl.exe` itself rather than a script, so the
  hand-back is one piece of code.
- When the app finds Wi-Fi left off, it shows a press for it instead of
  elevating at start.
- The off panel warns that the radio turns Wi-Fi off.
- Only an installed copy swaps the card; a clone of the repository is left
  as a development mode that uses the card as the scripts left it.
- The installed copy recognises its own driver package by its signer, so a
  development package on the same PC is never touched.
