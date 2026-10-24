winawdl
=======

AirDrop on Windows, through the PC's own Wi-Fi card: Apple's AWDL radio
protocol and AirDrop, receiving and sending, on the built-in Realtek
RTL8852CE. This is research code. The project, its source and its notes are at
https://github.com/raph559/winawdl


Before you install
------------------

- It works with one Wi-Fi card only, the Realtek RTL8852CE. The installer
  stops, having changed nothing, on a PC that has another.
- Installing leaves Wi-Fi as it is. The app's Radio switch gives the card to
  winawdl's driver while the radio is on, so Wi-Fi is off then. Wi-Fi comes
  back when the radio is turned off, when winawdl quits, and when Windows
  starts.
- The driver is signed on your PC, with a certificate the installer makes
  there and adds to the PC's trusted roots and trusted publishers. The
  installer then deletes that certificate's private key, so nothing else can
  ever be signed with it.
- While the app runs with its radio on, Apple devices in range that send to
  everyone can ask to send you files. The app asks you before it takes any,
  unless you change that in its settings.
- While the app's window is open it advertises over Bluetooth as an Apple
  device does before an AirDrop, and Apple devices in range that are visible
  to everyone turn their AWDL radio on for it.
- winawdl is an independent project, not affiliated with or endorsed by
  Apple. AirDrop is a trademark of Apple Inc.


Install
-------

If you are reading this in C:\Program Files\winawdl, winawdl is installed
already. From the zip:

1. Unpack this folder anywhere.
2. Double-click install.cmd. Windows asks for administrator rights, then the
   installer lists what it will change and waits for your answer.
3. Start winawdl from the Start Menu and turn its Radio switch on. Windows
   asks for administrator rights again, for the radio. Wi-Fi is off while
   the radio is on.

The installer needs three things on the PC and offers to fetch what is
missing with winget: the .NET Desktop Runtime 10, Python 3.10 or newer, and
the Visual C++ runtime.

"install.ps1 -DryRun", from any PowerShell prompt in this folder, says what
would be done and changes nothing.

To send to an iPhone, iPad or Mac, or to receive from one, set its AirDrop to
"Everyone for 10 Minutes".


Uninstall
---------

Right-click winawdl in the Start Menu and choose Uninstall, which opens
"Installed apps" in Settings: winawdl is in that list, with its own Uninstall.
An install made from the zip also has uninstall.cmd in this folder, which
does the same. Either closes the app first if it is running. The card goes back to its own driver and Wi-Fi
comes back. Files you received stay where they were stored.


What is in here
---------------

winawdl.exe          the app (MIT)
airdrop\             the AirDrop receiver and sender, in Python (MIT)
radio\               the radio daemon (GPL-2.0-only), which runs the card
awdl\                the AWDL daemon (GPL-3.0-or-later), and wintun.dll, the
                     network adapter it uses, under WireGuard's license
driver\              the driver for the card (MIT), not yet signed
licenses\            the license texts, and SOURCE.txt, which says exactly
                     which source each program here was built from
