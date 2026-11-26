# Builds the IEMAS Agent and publishes it to windows-client\agent-release, which the server serves.
#
#   .\Publish-Agent.ps1
#
# 1. reads the version from src\Iemas.Agent\Iemas.Agent.csproj (<Version>)
# 2. builds one self-contained IemasAgent.exe
# 3. copies it to agent-release\ and writes agent-release\version.txt
#
# After this:
#   - every installer downloaded from the CMS carries the new Agent, and
#   - PCs that already have the Agent update themselves (CMS: Settings > PC setup > "Update the Agent on PCs automatically"),
#     within about 30 minutes of being connected. No API restart is needed: the server reads this folder on each request.
# To release a new version, raise <Version> in the csproj first; PCs only update to a HIGHER version number.
# Windows PowerShell 5.1 compatible; keep this file ASCII.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\Iemas.Agent\Iemas.Agent.csproj'
$release = Join-Path $root 'agent-release'

$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version -or -not [Version]::TryParse($version, [ref]([Version]$null))) { throw "Could not read a version like 1.5.0 from $project" }
Write-Host "Publishing IEMAS Agent $version" -ForegroundColor Cyan

$out = Join-Path ([IO.Path]::GetTempPath()) ("iemas-agent-publish-" + [Guid]::NewGuid().ToString('N'))
try {
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
        -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    $exe = Join-Path $out 'IemasAgent.exe'
    if (-not (Test-Path $exe)) { throw "The build did not produce IemasAgent.exe" }

    New-Item -ItemType Directory -Force -Path $release | Out-Null
    Copy-Item $exe (Join-Path $release 'IemasAgent.exe') -Force
    [IO.File]::WriteAllText((Join-Path $release 'version.txt'), "$version`n", (New-Object Text.UTF8Encoding($false)))

    $hash = (Get-FileHash (Join-Path $release 'IemasAgent.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = [math]::Round((Get-Item (Join-Path $release 'IemasAgent.exe')).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Published IEMAS Agent $version ($size MB)" -ForegroundColor Green
    Write-Host "  $release\IemasAgent.exe"
    Write-Host "  sha256 $hash"
} finally {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue }
}
