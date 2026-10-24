# Takes winawdl off this PC: gives the Wi-Fi card back to its own driver,
# removes the driver, the trust in the certificate it was signed with, the
# firewall rules, the startup task, the Start Menu entry and the files. What was received stays
# where it was stored. -RemoveData also deletes the app's settings and logs.
# -DryRun says what would be done and changes nothing. -Yes asks no questions.
# -Setup is how the setup program's own uninstaller runs this: it removes the
# shortcut, the list entry and the files itself.
param([switch]$Yes, [switch]$DryRun, [switch]$RemoveData, [switch]$Setup)
$ErrorActionPreference = 'Stop'
# A 32-bit program that starts this is shown the 32-bit Program Files and no
# pnputil, so it carries on in the 64-bit PowerShell.
if (-not [Environment]::Is64BitProcess -and [Environment]::Is64BitOperatingSystem) {
  $native = Join-Path $env:SystemRoot 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
  $again = @($PSBoundParameters.Keys | ForEach-Object { "-$_" })
  & $native -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @again
  exit $LASTEXITCODE
}
# Started from the folder it removes, it would hold that folder open.
Set-Location $env:SystemRoot
[Environment]::CurrentDirectory = $env:SystemRoot

$Target        = Join-Path $env:ProgramFiles 'winawdl'
$HardwareId    = 'PCI\VEN_10EC&DEV_C852'
$InfName       = 'winawdl_regproxy.inf'
$CertSubject   = 'CN=winawdl driver (signed on this PC)'
$TrustStores   = 'Root', 'TrustedPublisher'
$FirewallGroup = 'winawdl AirDrop'
$UninstallKey  = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\winawdl'
$Shortcut      = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\winawdl.lnk'
$Data          = (Join-Path $env:LOCALAPPDATA 'winawdl'), (Join-Path $env:ProgramData 'winawdl')
$GiveBackFlag  = '--give-wifi-back'
$LogFolder     = Join-Path $env:ProgramData 'winawdl\logs'
$StartupTask   = 'winawdl Wi-Fi'
$TempCopy      = 'winawdl-uninstall.ps1'

function Step([string]$what, [scriptblock]$do) {
  if ($DryRun) { "  would: $what"; return }
  "  $what"
  & $do
}

function Get-CardDriver {
  $card = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like "$HardwareId*" } | Select-Object -First 1
  if (-not $card) { return 'not found' }
  $inf = (Get-PnpDeviceProperty -InstanceId $card.InstanceId -KeyName DEVPKEY_Device_DriverInfPath).Data
  "$($card.FriendlyName), driver package $inf"
}

function Get-PublishedPackages {
  $store = [xml](pnputil /enum-drivers /format xml | Out-String)
  $store.PnpUtil.Driver | Where-Object { $_.OriginalName -eq $InfName } | ForEach-Object { $_.DriverName }
}

if ($DryRun) { 'Dry run: nothing on this PC is changed.'; '' }
$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $DryRun -and -not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'This needs administrator rights. Start uninstall.cmd, which asks Windows for them.'
}
$Programs = 'winawdl', 'awdld', 'winawdl_radio'
$running = [bool](Get-Process $Programs -ErrorAction SilentlyContinue)

"The Wi-Fi card now: $(Get-CardDriver)"
if ($running) { 'winawdl is running, and will be closed first.' }
if (-not $Yes -and -not $DryRun -and (Read-Host 'Take winawdl off this PC? (y/n)') -ne 'y') {
  'Nothing was changed.'
  exit 1
}

if ($running) {
  'The app'
  Step 'close it, and let it stop its radio' {
    # The copy that runs the radio stops both daemons when the window's process ends.
    Get-CimInstance Win32_Process -Filter "Name = 'winawdl.exe'" |
      Where-Object { $_.CommandLine -notmatch '--radio' } |
      ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($second in 1..15) {
      if (-not (Get-Process $Programs -ErrorAction SilentlyContinue)) { break }
      Start-Sleep -Seconds 1
    }
    Get-Process $Programs -ErrorAction SilentlyContinue | Stop-Process -Force
  }
}

'Wi-Fi'
Step 'give the Wi-Fi card back, if winawdl''s driver has it' {
  # Removing the driver below gives it back too; this way says why, if it cannot.
  $app = Join-Path $Target 'winawdl.exe'
  if (Test-Path $app) {
    $giver = Start-Process $app -ArgumentList $GiveBackFlag -Wait -PassThru
    if ($giver.ExitCode -ne 0) {
      "  could not: $(Get-Content (Join-Path $LogFolder 'wifi.log') -Tail 1 -ErrorAction SilentlyContinue)"
    }
  }
}
Step 'remove the task that gives Wi-Fi back when Windows starts' {
  Unregister-ScheduledTask -TaskName $StartupTask -Confirm:$false -ErrorAction SilentlyContinue
}

'Driver'
Step 'remove it, so that Windows gives the card back to its own driver' {
  foreach ($package in Get-PublishedPackages) {
    pnputil /delete-driver $package /uninstall /force | Out-Null
  }
  pnputil /scan-devices | Out-Null
}
Step 'stop trusting the certificate it was signed with' {
  foreach ($store in $TrustStores) {
    Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object { $_.Subject -eq $CertSubject } | Remove-Item
  }
  # An install that was cut short may have left the key behind. Remove-Item
  # only knows -DeleteKey when it is given the certificate's path, not from a pipeline.
  Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $CertSubject } |
    ForEach-Object { Remove-Item "Cert:\CurrentUser\My\$($_.Thumbprint)" -DeleteKey }
}

'Firewall'
Step 'remove the two rules' {
  Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

if (-not $Setup) {
  'Start Menu and files'
  Step "remove the shortcut, the entry in the list of installed apps, and $Target" {
    Remove-Item $Shortcut -ErrorAction SilentlyContinue
    Remove-Item $UninstallKey -Recurse -ErrorAction SilentlyContinue
    if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
  }
}
if ($RemoveData) {
  Step 'remove the app''s settings, its certificate and its logs' {
    $Data | Where-Object { Test-Path $_ } | ForEach-Object { Remove-Item $_ -Recurse -Force }
  }
}

''
if ($DryRun) { 'Dry run done: nothing was changed.'; exit 0 }
"The Wi-Fi card now: $(Get-CardDriver)"
'winawdl is off this PC. Files it received are still where they were stored.'
if (-not $RemoveData) { "Its settings and logs are still in $($Data -join ' and ')." }
# uninstall.cmd runs a copy of this file, made outside the folder that was removed.
if ((Split-Path $PSCommandPath -Leaf) -eq $TempCopy) { Remove-Item $PSCommandPath -ErrorAction SilentlyContinue }
