# Installs or updates the IEMAS Agent for the signed-in Windows user. Started by Install-IEMAS-Agent.bat.
#   1. installs the .NET 8 Desktop Runtime if it is missing (the only step that may ask for administrator rights)
#   2. copies IemasAgent.exe to %LOCALAPPDATA%\Programs\IEMAS Agent
#   3. writes the server address and the installer key from iemas-agent-setup.json into the Agent's settings
#   4. adds a Start menu shortcut, turns on Start with Windows, and starts the Agent
# The Agent then registers with the installer key and connects without waiting for approval.
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
        Write-Host "         but an administrator must approve this PC in the CMS (People & Devices > Windows Agents)." -ForegroundColor Yellow
    }
}
Info "Server: $($setup.serverUrl)"
if ($setup.emailAddress) { Info "For:    $($setup.employeeName) <$($setup.emailAddress)>" }

# --- .NET 8 Desktop Runtime -------------------------------------------------------------------------------
function Test-DesktopRuntime {
    $key = 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App'
    if (Test-Path $key) {
        if ((Get-Item $key).Property | Where-Object { $_ -like '8.*' }) { return $true }
    }
    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $dotnet) {
        $list = & $dotnet --list-runtimes 2>$null
        if ($list | Where-Object { $_ -like 'Microsoft.WindowsDesktop.App 8.*' }) { return $true }
    }
    return $false
}

Step "Checking the .NET 8 Desktop Runtime"
if (Test-DesktopRuntime) {
    Ok "Already installed"
} else {
    Info "Not installed yet. Installing it now; Windows may ask you to allow the change - click Yes."
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        try {
            & winget install --id Microsoft.DotNet.DesktopRuntime.8 --exact --silent --accept-package-agreements --accept-source-agreements | Out-Host
        } catch { Info "winget did not finish: $($_.Exception.Message)" }
    }
    if (-not (Test-DesktopRuntime)) {
        Info "Downloading the runtime from Microsoft..."
        $installer = Join-Path $env:TEMP 'windowsdesktop-runtime-8-win-x64.exe'
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe' -OutFile $installer -UseBasicParsing
            $p = Start-Process -FilePath $installer -ArgumentList '/install', '/quiet', '/norestart' -Verb RunAs -Wait -PassThru
            Info "Runtime installer finished with code $($p.ExitCode)"
        } catch {
            Fail "Could not install the .NET 8 Desktop Runtime ($($_.Exception.Message)). Install it by hand from https://dotnet.microsoft.com/download/dotnet/8.0 (.NET Desktop Runtime, Windows x64), then run this installer again."
        }
    }
    if (Test-DesktopRuntime) { Ok "Installed" } else { Fail "The .NET 8 Desktop Runtime is still missing. Install it by hand from https://dotnet.microsoft.com/download/dotnet/8.0 (.NET Desktop Runtime, Windows x64), then run this installer again." }
}

# --- Program files ----------------------------------------------------------------------------------------
Step "Installing the IEMAS Agent"
$running = Get-Process IemasAgent -ErrorAction SilentlyContinue
if ($running) {
    Info "Closing the Agent that is running now (it is updated and started again)"
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\IEMAS Agent'
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
$exe = Join-Path $appDir 'IemasAgent.exe'
Copy-Item (Join-Path $here 'IemasAgent.exe') $exe -Force
Unblock-File $exe -ErrorAction SilentlyContinue
Ok "Installed to $appDir"

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
if ($setup.emailAddress) {
    Write-Host "The Agent connects by itself in a few seconds. The light at the top left turns green." -ForegroundColor White
} else {
    Write-Host "In the Agent window, type your company email address and click Register. It connects straight away." -ForegroundColor White
}
exit 0
