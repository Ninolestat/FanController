$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pub = Join-Path $PSScriptRoot 'publish'

if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
New-Item -ItemType Directory -Path $pub | Out-Null

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) non trovato.' }
if (-not (Test-Path (Join-Path $PSScriptRoot 'PawnIO_setup.exe'))) {
    throw 'installer\PawnIO_setup.exe mancante.'
}

Write-Host 'Publishing FanCtl (self-contained win-x64)...'
dotnet publish (Join-Path $root 'src\FanCtl\FanCtl.csproj') -c Release -r win-x64 --self-contained true -o $pub
if ($LASTEXITCODE -ne 0) { throw "dotnet publish FanCtl fallito ($LASTEXITCODE)" }

Write-Host 'Publishing FanCtl.UI (self-contained win-x64)...'
dotnet publish (Join-Path $root 'src\FanCtl.UI\FanCtl.UI.csproj') -c Release -r win-x64 --self-contained true -o $pub
if ($LASTEXITCODE -ne 0) { throw "dotnet publish FanCtl.UI fallito ($LASTEXITCODE)" }

Write-Host 'Building installer...'
& $iscc (Join-Path $PSScriptRoot 'FanCtl.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC fallito ($LASTEXITCODE)" }

Write-Host "Completato: $(Join-Path $root 'dist\FanCtl-Setup.exe')"
