# Installs or updates the IEMAS Agent for the signed-in Windows user. Started by Install-IEMAS-Agent.bat.
#   1. removes any earlier IEMAS Agent on this PC (see Remove-Old-IEMAS-Agent.ps1)
#   2. copies IemasAgent.exe to %LOCALAPPDATA%\Programs\IEMAS Agent
#   3. writes the server address and the installer key from iemas-agent-setup.json into the Agent's settings
#   4. adds a Start menu shortcut, turns on Start with Windows, and starts the Agent
# The Agent then registers with the installer key and connects without waiting for approval.
# The Agent carries its own .NET runtime, so nothing else is installed and no administrator rights are needed.
# Windows PowerShell 5.1 compatible; keep this file ASCII.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "   OK    $text" -ForegroundColor Green }
function Info($text) { Write-Host "         $text" -ForegroundColor Gray }
function Fail($text) { Write-Host ""; Write-Host "   FAILED  $text" -ForegroundColor Red; exit 1 }

Write-Host "IEMAS Agent installer" -ForegroundColor White

# --- Setup file -------------------------------------------------------------------------------------------
$setupPath = Join-Path $here 'iemas-agent-setup.json'
try { $setup = Get-Content $setupPath -Raw | ConvertFrom-Json } catch { Fail "Cannot read iemas-agent-setup.json. Download a new installer from the IEMAS CMS." }
if (-not $setup.serverUrl) { Fail "iemas-agent-setup.json has no server address. Download a new installer from the IEMAS CMS." }
if ($setup.expiresAt) {
    $expires = [DateTimeOffset]::Parse($setup.expiresAt)
    if ($expires -lt [DateTimeOffset]::UtcNow) {
        Write-Host "   NOTE  This installer expired on $($expires.LocalDateTime). The Agent will still install," -ForegroundColor Yellow
        Write-Host "         but an administrator must approve this PC in the CMS (Employees > PCs)." -ForegroundColor Yellow
    }
}
Info "Server: $($setup.serverUrl)"
if ($setup.emailAddress) { Info "For:    $($setup.employeeName) <$($setup.emailAddress)>" }

# --- Earlier versions -------------------------------------------------------------------------------------
# An update replaces the old copy instead of running next to it. This PC's registration is kept.
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\IEMAS Agent'
Step "Looking for an earlier IEMAS Agent on this PC"
$cleanup = Join-Path $here 'Remove-Old-IEMAS-Agent.ps1'
if (Test-Path $cleanup) {
    try { & $cleanup -NewInstallDir $appDir } catch { Info "Could not check for an earlier version: $($_.Exception.Message)" }
} else {
    $running = Get-Process IemasAgent -ErrorAction SilentlyContinue
    if ($running) { $running | Stop-Process -Force; Start-Sleep -Seconds 2 }
}

# --- Program files ----------------------------------------------------------------------------------------
Step "Installing the IEMAS Agent"
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
$exe = Join-Path $appDir 'IemasAgent.exe'
Copy-Item (Join-Path $here 'IemasAgent.exe') $exe -Force
Unblock-File $exe -ErrorAction SilentlyContinue
Ok "Installed to $appDir"
# Start with Windows points at this copy only (the cleanup above removed any entry for an old one).
try {
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'IemasAgent' -Value ('"' + $exe + '" --background')
} catch { Info "Could not set Start with Windows: $($_.Exception.Message)" }

# --- Settings ---------------------------------------------------------------------------------------------
Step "Connecting it to the IEMAS server"
$settingsDir = Join-Path $env:LOCALAPPDATA 'IemasAgent'
$settingsPath = Join-Path $settingsDir 'settings.json'
New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
$settings = $null
if (Test-Path $settingsPath) { try { $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json } catch { $settings = $null } }
if (-not $settings) { $settings = New-Object PSObject }
function Set-Setting($name, $value) {
    if ($settings.PSObject.Properties[$name]) { $settings.$name = $value } else { $settings | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}
Set-Setting 'ServerUrl' ([string]$setup.serverUrl)
Set-Setting 'AllowInsecureTls' ([bool]$setup.allowSelfSignedCertificate)
Set-Setting 'StartWithWindows' $true
Set-Setting 'StartupDefaultApplied' $true
Set-Setting 'InstallKey' ([string]$setup.installKey)
Set-Setting 'InstallEmail' $(if ($setup.emailAddress) { [string]$setup.emailAddress } else { $null })
Set-Setting 'SelfEnrollment' ([bool]$setup.selfEnrollment)
Set-Setting 'ServerCertificateThumbprint' $(if ($setup.serverCertificateThumbprint) { [string]$setup.serverCertificateThumbprint } else { $null })
[IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))

$credentialFile = Join-Path $settingsDir 'agent.dat'
if (Test-Path $credentialFile) {
    Ok "Server address saved. This PC is already registered; that registration is kept."
} else {
    Ok "Server address and installer key saved"
}

# --- Shortcut and start -----------------------------------------------------------------------------------
Step "Starting the Agent"
try {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'IEMAS Agent.lnk'))
    $link.TargetPath = $exe
    $link.WorkingDirectory = $appDir
    $link.Description = 'IEMAS Agent - customer inquiry alerts'
    $link.Save()
    Ok "Start menu shortcut: IEMAS Agent"
} catch { Info "Could not create the Start menu shortcut: $($_.Exception.Message)" }

Start-Process -FilePath $exe -WorkingDirectory $appDir
Ok "The IEMAS Agent is running. Its icon is next to the clock; it starts by itself with Windows."
Write-Host ""
if ($setup.selfEnrollment) {
    Write-Host "Last step: in the IEMAS Agent window, type your work email address and its password, then click Connect." -ForegroundColor White
} elseif ($setup.emailAddress) {
    Write-Host "The Agent connects by itself in a few seconds. The light at the top left turns green." -ForegroundColor White
} else {
    Write-Host "In the Agent window, type your company email address and click Register. It connects straight away." -ForegroundColor White
}
exit 0
