# Changelog

What each tagged state of the project holds. The measurements behind the
numbers are in [DESIGN-NOTES.md](DESIGN-NOTES.md).

## 0.4.0, 2026-11-04

- **Received files drag out of the window.** A file on the Received shelf
  drags as Explorer drags one, under its own picture, and lands as a copy in
  a folder, a mail or a chat. Let go on a device in the window, it is sent.
- **The Radio switch takes the Wi-Fi card and gives it back.** Turning the
  radio on takes the card from Windows' Wi-Fi; turning it off gives it back,
  once the radio has let go of it, with no restart. The window says which
  way the card is going, and says so when Wi-Fi could not be given back, with
  a press that tries again.
- **Quit waits for Wi-Fi.** Quit with the radio on, and winawdl stays in the
  notification area until Wi-Fi is back, or says from there that it is not.
- **Wi-Fi comes back at startup.** A task that runs when Windows starts gives
  the card back if the PC stopped with the radio on.
- **Installing leaves Wi-Fi alone.** It no longer gives the card to
  winawdl's driver; an upgrade over 0.3.0 gives Wi-Fi back.

## 0.3.0, 2026-10-13

- **A request on screen.** While the app's window is not the one in front, a
  device that wants to send shows as a card in the corner of the screen, with
  Accept and Decline. It does not take the keyboard, and stays away while a
  full-screen app or a presentation has the screen. A setting turns it off.
- **Always accept from a device.** A request offers to accept everything from
  its device from then on. The settings list those devices and can forget
  each. A device is known by the name and model it gives, which another
  device could give too.
- **What arrived is said** on a card of its own when the window is not in
  front. Pressing it opens the file, or shows several in their folder. It
  leaves by itself after 10 seconds.
- **The icon in the notification area says what is moving** under the
  pointer: who is sending or receiving what, and how far along.
- An answer that reached the engine the instant it asked was lost, and the
  sender left waiting. Nothing answered that fast until now.

- The AWDL daemon logs what each device nearby announced whenever it changes
  the one it keeps time with. One transfer waited 22 s on a wrong choice
  there, and the cause is not yet known.

- **A setup program.** `winawdl-<version>-setup.exe` is a wizard, made with
  Inno Setup, around the same steps as the zip's `install.cmd`: it says what
  installing changes, fetches what the PC lacks if asked to, gives the card
  back if a step fails, and leaves a normal uninstaller. Installed with it on
  one PC, the app received a Live Photo.
- **The uninstaller works.** Its first real run stopped half way, with the
  card given back and the firewall rules, the shortcut and the files left.
  It also could not remove its folder when started from it, and the
  installer ended on a PC whose only Python is the Store's stand-in. All
  three are fixed, and the whole cycle has now been run on one PC.
- **Uninstalling from Windows.** Started by Windows' own Uninstall button, the
  uninstaller no longer asks a second time, and it closes the app and its
  radio itself where it used to refuse while they ran. The list of installed
  apps shows the size and the date.

The cards, always accepting and the icon's line were tried with an iPhone on
2026-10-13, by the owner.

## 0.2.0, 2026-10-13

- **An installer.** A zip with the programs already built, and `install.cmd`,
  which signs the driver on the PC it runs on with a certificate it makes
  there, deletes that certificate's private key, gives the card to the
  driver, adds the firewall rules and a Start Menu entry. `uninstall.cmd`
  undoes all of it. It has run in full on the PC it was made on, where the
  installed app then received a Live Photo and sent a file; its uninstaller
  has not yet been run for real.
- **A device is listed once.** An iPhone that announced itself under a second
  name was shown twice for up to 20 s, or for as long as a transfer to it ran.
- **A Live Photo is one photo** among what was received, and can play its
  video. It is still stored as two files, a still and a video.
- **The app runs from an installed folder.** It finds the daemons beside
  itself, keeps their logs in `C:\ProgramData\winawdl\logs`, and makes the
  certificate the receiver and sender present, so OpenSSL is no longer needed.
- **A device that will not answer this PC** is said to be there, with what to
  change on it, where it used to be asked again every 2 s and never shown.
- **What the daemons print** is stored as written; a dash in a line used to
  come out as three wrong letters.
- **An icon.**
- Nothing built says who built it: the account's name is kept out of the
  programs.

## 0.1.0, 2026-10-12

The first tagged state. All of it has run on one machine, with one RTL8852CE,
against one iPhone.

- **Driver.** A user-mode (UMDF 2) driver that takes the card from the vendor
  driver and relays register accesses, built and signed on the machine that
  uses it.
- **Radio.** devourer drives the chip from an ordinary program over a new
  Windows PCIe backend: firmware boot, monitor-mode receive and transmit,
  hardware acknowledgement, block ack in both directions, 80 MHz.
- **AWDL.** OWL's protocol core with a Windows I/O layer and a Wintun
  adapter, `awdl0`, that carries IPv6 to and from nearby Apple devices.
- **Receiving AirDrop.** Photos, videos and Live Photos from an iPhone, at
  about 16 MB/s.
- **Sending AirDrop.** Files into an iPhone's Photos at 9 to 14 MB/s, with
  the Bluetooth advertisement that wakes the receiver.
- **The app.** One window over both, which also starts and stops the two
  daemons. Tried over the air for starting and stopping the radio and one
  small file each way.

Not in it: Contacts Only in either direction, any other card, and a driver
signed for machines other than the one that built it.
