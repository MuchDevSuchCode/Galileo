<#
.SYNOPSIS
    Installs or updates the PhotosPlus default-app copy in one step.

.DESCRIPTION
    Stops any running instance, publishes a fresh self-contained build to
    %LocalAppData%\PhotosPlus\app, then (re)registers it with Windows.
    Re-run this any time you want the installed copy to match your latest code.

.PARAMETER Configuration
    Build configuration to publish (default: Release).

.PARAMETER SkipRegister
    Publish only; don't touch the registry.
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipRegister
)

$ErrorActionPreference = 'Stop'

$project = Resolve-Path (Join-Path $PSScriptRoot '..\src\PhotosPlus.App\PhotosPlus.App.csproj')
$dest    = Join-Path $env:LOCALAPPDATA 'PhotosPlus\app'
$exe     = Join-Path $dest 'Galileo.exe'

# 1) The exe is locked while running — stop it so publish can overwrite.
Get-Process 'Galileo' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping running PhotosPlus (pid $($_.Id))..." -ForegroundColor DarkGray
    $_ | Stop-Process -Force
}
Start-Sleep -Milliseconds 400

# 2) Publish a fresh self-contained copy to the stable location.
Write-Host "Publishing $Configuration build -> $dest" -ForegroundColor Cyan
dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $dest
if ($LASTEXITCODE -ne 0) { Write-Error "Publish failed (exit $LASTEXITCODE)."; return }
if (-not (Test-Path $exe)) { Write-Error "Publish did not produce $exe."; return }

# 3) (Re)register with Windows.
if ($SkipRegister) {
    Write-Host "Published. Skipped registration (-SkipRegister)." -ForegroundColor Green
} else {
    & (Join-Path $PSScriptRoot 'register-default.ps1') -ExePath $exe
}

Write-Host ""
Write-Host "Installed/updated at: $exe" -ForegroundColor Green
