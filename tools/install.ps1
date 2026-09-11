<#
.SYNOPSIS
    Installs or updates the Galileo default-app copy in one step.

.DESCRIPTION
    Stops any running instance, publishes a fresh self-contained build to
    %LocalAppData%\Galileo\app, then (re)registers it with Windows.
    Re-run this any time you want the installed copy to match your latest code.

.PARAMETER Configuration
    Build configuration to publish (default: Release).

.PARAMETER SkipRegister
    Publish only; don't touch the registry.

.PARAMETER NoShortcuts
    Don't create the Start-menu / Desktop shortcuts.
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipRegister,
    [switch]$NoShortcuts,
    # Force-kill instances that don't close gracefully. Off by default: a force-kill can interrupt a
    # vault commit or an unsaved-edit dialog mid-save, so refusing to install is the safe default.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$project = Resolve-Path (Join-Path $PSScriptRoot '..\src\Galileo.App\Galileo.App.csproj')
$dest    = Join-Path $env:LOCALAPPDATA 'Galileo\app'
$exe     = Join-Path $dest 'Galileo.exe'

# 1) The exe is locked while running — ask it to close gracefully (runs the app's shutdown path:
#    vault commit + lock, unsaved-edit prompts). A commit or a save dialog can take a while, so wait
#    generously and then ABORT rather than force-kill — killing mid-commit destroys vault/editor work.
Get-Process 'Galileo' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Asking Galileo (pid $($_.Id)) to close..." -ForegroundColor DarkGray
    try { $_.CloseMainWindow() | Out-Null } catch { }
}
for ($i = 0; $i -lt 120; $i++) {   # up to 30 s for saves/locks/dialogs
    if (-not (Get-Process 'Galileo' -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 250
}
$still = Get-Process 'Galileo' -ErrorAction SilentlyContinue
if ($still) {
    if ($Force) {
        foreach ($p in $still) {
            Write-Host "Force-stopping Galileo (pid $($p.Id)) (-Force)..." -ForegroundColor Yellow
            try { $p | Stop-Process -Force -ErrorAction Stop } catch { }
        }
        Start-Sleep -Milliseconds 400
    }
    if (Get-Process 'Galileo' -ErrorAction SilentlyContinue) {
        Write-Error ("Galileo is still running (it may be asking about unsaved work or securing a vault). " +
                     "Finish or close it, then re-run install. Use -Force only if you're sure nothing is being saved.")
        return
    }
}

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

# 4) Create Start menu + Desktop shortcuts (so it's searchable, pinnable, and on the desktop).
if (-not $NoShortcuts) {
    function New-GalileoShortcut([string]$LinkPath, [string]$Target) {
        $shell = New-Object -ComObject WScript.Shell
        $sc = $shell.CreateShortcut($LinkPath)
        $sc.TargetPath       = $Target
        $sc.WorkingDirectory = (Split-Path $Target)
        $sc.IconLocation     = "$Target,0"
        $sc.Description       = 'Galileo file explorer & media viewer'
        $sc.Save()
        [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }
    try {
        $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Galileo.lnk'
        $desktop   = Join-Path ([Environment]::GetFolderPath('Desktop'))  'Galileo.lnk'
        New-GalileoShortcut $startMenu $exe
        New-GalileoShortcut $desktop   $exe
        Write-Host "Shortcuts created: Start menu + Desktop." -ForegroundColor Green
        Write-Host "  (Pin it: right-click the Start-menu entry -> Pin to Start / Pin to taskbar.)" -ForegroundColor DarkGray
    }
    catch { Write-Warning "Could not create shortcuts: $($_.Exception.Message)" }
}

Write-Host ""
Write-Host "Installed/updated at: $exe" -ForegroundColor Green
