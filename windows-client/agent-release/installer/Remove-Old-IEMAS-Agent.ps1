# Finds earlier copies of the IEMAS Agent on this PC and removes them, so the new version replaces them instead of
# running next to them. Called by Install-IEMAS-Agent.ps1 before it installs; safe to run on its own.
#
# What it does, for the signed-in Windows user:
#   - stops every running IemasAgent.exe, wherever it was started from
#   - finds old copies: running programs, the Start-with-Windows entry, shortcuts (Start menu, Startup, Desktop),
#     an old uninstall entry, and the usual install folders
#   - deletes only the Agent's own files from those places (IemasAgent.exe and its .dll/.pdb/.json files), and a
#     folder only if that leaves it empty. It never deletes other files or folders.
#   - removes the old shortcuts and the old Start-with-Windows entry (the installer adds new ones)
#   - KEEPS %LOCALAPPDATA%\IemasAgent (settings and this PC's registration), so an update does not need a new approval
# Windows PowerShell 5.1 compatible; keep this file ASCII.

param(
    # The folder the new version is installed to. Left alone, apart from stopping the program in it.
    [Parameter(Mandatory = $true)][string]$NewInstallDir,
    # Only report what would be removed.
    [switch]$WhatIf
)

$ErrorActionPreference = 'Continue'
function Info($text) { Write-Host "         $text" -ForegroundColor Gray }
function Ok($text) { Write-Host "   OK    $text" -ForegroundColor Green }

$newDir = [IO.Path]::GetFullPath($NewInstallDir).TrimEnd('\')
$agentFiles = @('IemasAgent.exe', 'IemasAgent.pdb', 'IemasAgent.dll', 'IemasAgent.deps.json', 'IemasAgent.runtimeconfig.json')
$found = New-Object System.Collections.Generic.List[string]   # full paths of old IemasAgent.exe files

function Add-Found($path) {
    if (-not $path) { return }
    try { $full = [IO.Path]::GetFullPath($path) } catch { return }
    if ([IO.Path]::GetFileName($full) -ne 'IemasAgent.exe') { return }
    if (-not ($found -contains $full)) { $found.Add($full) }
}

# --- 1. Running copies ---------------------------------------------------------------------------------------
$running = @(Get-Process -Name IemasAgent -ErrorAction SilentlyContinue)
foreach ($p in $running) {
    $path = $null
    try { $path = $p.MainModule.FileName } catch { }
    Add-Found $path
    Info "Running: IEMAS Agent (process $($p.Id)) $path"
}
if ($running.Count -gt 0 -and -not $WhatIf) {
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Ok "Stopped $($running.Count) running IEMAS Agent program(s)"
}

# --- 2. Start with Windows entry ------------------------------------------------------------------------------
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = $null
try { $runValue = (Get-ItemProperty -Path $runKey -ErrorAction Stop).IemasAgent } catch { }
if ($runValue) {
    $exePath = if ($runValue -match '^\s*"([^"]+)"') { $Matches[1] } else { ($runValue -split '\s+--')[0].Trim() }
    Add-Found $exePath
    Info "Start-with-Windows entry points to: $exePath"
}

# --- 3. Shortcuts ---------------------------------------------------------------------------------------------
$shortcutFolders = @(
    [Environment]::GetFolderPath('Programs'),
    [Environment]::GetFolderPath('Startup'),
    [Environment]::GetFolderPath('Desktop'),
    [Environment]::GetFolderPath('CommonPrograms'),
    [Environment]::GetFolderPath('CommonStartup'),
    [Environment]::GetFolderPath('CommonDesktopDirectory')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

$oldShortcuts = New-Object System.Collections.Generic.List[string]
try { $shell = New-Object -ComObject WScript.Shell } catch { $shell = $null }
if ($shell) {
    foreach ($folder in $shortcutFolders) {
        Get-ChildItem -Path $folder -Filter '*.lnk' -File -Recurse -Depth 2 -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                $target = $shell.CreateShortcut($_.FullName).TargetPath
                if ($target -and [IO.Path]::GetFileName($target) -eq 'IemasAgent.exe') {
                    $oldShortcuts.Add($_.FullName)
                    Add-Found $target
                    Info "Shortcut: $($_.FullName)"
                }
            } catch { }
        }
    }
}

# --- 4. An uninstall entry from an installer that registered itself --------------------------------------------
$uninstallKeys = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)
foreach ($entry in (Get-ItemProperty -Path $uninstallKeys -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'IEMAS Agent*' })) {
    $cmd = if ($entry.QuietUninstallString) { $entry.QuietUninstallString } else { $null }
    Info "Uninstall entry: $($entry.DisplayName) $($entry.DisplayVersion)"
    if ($cmd -and -not $WhatIf) {
        try {
            $p = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $cmd -Wait -PassThru -WindowStyle Hidden
            Ok "Ran its uninstaller (exit code $($p.ExitCode))"
        } catch { Info "Its uninstaller could not run: $($_.Exception.Message)" }
    } elseif (-not $cmd) {
        Info "It has no silent uninstaller; its files are removed below if found."
    }
}

# --- 5. The usual folders -------------------------------------------------------------------------------------
$usual = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\IEMAS Agent'),
    (Join-Path $env:LOCALAPPDATA 'Programs\IemasAgent'),
    (Join-Path $env:LOCALAPPDATA 'IEMAS Agent'),
    (Join-Path $env:ProgramFiles 'IEMAS Agent'),
    (Join-Path $env:ProgramFiles 'IemasAgent'),
    (Join-Path ${env:ProgramFiles(x86)} 'IEMAS Agent')
) | Where-Object { $_ }
foreach ($dir in $usual) { Add-Found (Join-Path $dir 'IemasAgent.exe') }

# --- Remove ---------------------------------------------------------------------------------------------------
$removed = 0
foreach ($exe in $found) {
    if (-not (Test-Path -LiteralPath $exe)) { continue }
    $dir = Split-Path -Parent $exe
    $isNewDir = ($dir.TrimEnd('\') -ieq $newDir)

    $version = $null
    try { $version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion } catch { }
    if (-not $version) { $version = 'unknown version' }

    if ($isNewDir) {
        # The folder the update goes to: the installer overwrites the file, nothing to delete here.
        Info "Earlier version $version in $dir will be replaced by the new one"
        continue
    }

    if ($WhatIf) { Info "Would remove version $version from $dir"; continue }

    foreach ($name in $agentFiles) {
        $file = Join-Path $dir $name
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
    }
    if ((Test-Path -LiteralPath $dir) -and -not (Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $dir -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $exe) { Info "Could not remove $exe (in use or no permission); remove it by hand" }
    else { Ok "Removed IEMAS Agent $version from $dir"; $removed++ }
}

if (-not $WhatIf) {
    foreach ($link in $oldShortcuts) { Remove-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue }
    if ($oldShortcuts.Count -gt 0) { Ok "Removed $($oldShortcuts.Count) old shortcut(s)" }
    if ($runValue) { Remove-ItemProperty -Path $runKey -Name 'IemasAgent' -ErrorAction SilentlyContinue }
}

if ($found.Count -eq 0 -and $running.Count -eq 0) { Ok "No earlier IEMAS Agent found on this PC" }
elseif ($removed -eq 0 -and $running.Count -gt 0) { Ok "Earlier IEMAS Agent stopped; it will be updated in place" }
