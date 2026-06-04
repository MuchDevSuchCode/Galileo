<#
.SYNOPSIS
    One-step update: stop running Galileo, pull the latest code, and publish a
    fresh self-contained Galileo.exe.

.DESCRIPTION
    1) Stops any running Galileo instance (the .exe is locked while running).
    2) Runs `git pull --ff-only` on the repo (skipped when there is no upstream,
       or when -NoPull is passed).
    3) Publishes a self-contained Release build by delegating to tools\install.ps1,
       so the publish settings live in one place. Output goes to
       %LocalAppData%\Galileo\app\Galileo.exe.

    Add -Register to also (re)register Galileo as a selectable default photo app.

.PARAMETER Configuration
    Build configuration to publish (default: Release).

.PARAMETER Register
    Also register file associations (runs install.ps1 without -SkipRegister).

.PARAMETER NoPull
    Skip the git pull step and just rebuild the current local code.

.PARAMETER Run
    Launch Galileo after a successful publish.

.EXAMPLE
    .\tools\update.ps1
    Stop, pull, and publish a self-contained exe.

.EXAMPLE
    .\tools\update.ps1 -Register -Run
    Same, but also register as a default photo app and launch when done.
#>
param(
    [string]$Configuration = 'Release',
    [switch]$Register,
    [switch]$NoPull,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'

$repo    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$install = Join-Path $PSScriptRoot 'install.ps1'
$exe     = Join-Path $env:LOCALAPPDATA 'Galileo\app\Galileo.exe'

# 1) Stop running instances so the publish can overwrite the exe.
$running = Get-Process 'Galileo' -ErrorAction SilentlyContinue
if ($running) {
    foreach ($p in $running) {
        Write-Host "Stopping running Galileo (pid $($p.Id))..." -ForegroundColor DarkGray
        $p | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 400
} else {
    Write-Host "No running Galileo instance found." -ForegroundColor DarkGray
}

# 2) Pull the latest code.
if ($NoPull) {
    Write-Host "Skipping git pull (-NoPull)." -ForegroundColor DarkGray
} else {
    Push-Location $repo
    try {
        # Only pull if this branch tracks an upstream; otherwise there's nothing to pull.
        git rev-parse --abbrev-ref --symbolic-full-name '@{u}' *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Pulling latest (git pull --ff-only)..." -ForegroundColor Cyan
            git pull --ff-only
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "git pull failed (exit $LASTEXITCODE). Building the current local code instead."
            }
        } else {
            Write-Warning "No upstream branch is configured - skipping git pull and building local code."
        }
    } finally {
        Pop-Location
    }
}

# 3) Publish a fresh self-contained exe (reuses install.ps1's publish settings).
$installArgs = @{ Configuration = $Configuration }
if (-not $Register) { $installArgs['SkipRegister'] = $true }
& $install @installArgs

if (-not (Test-Path $exe)) { Write-Error "Publish did not produce $exe."; return }

Write-Host ""
Write-Host "Update complete -> $exe" -ForegroundColor Green

# 4) Optionally launch the freshly built app.
if ($Run) {
    Write-Host "Launching Galileo..." -ForegroundColor Cyan
    Start-Process $exe
}
