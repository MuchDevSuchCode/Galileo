<#
.SYNOPSIS
    Publishes a self-contained Galileo build and zips it to docs\Galileo-Latest.zip
    (the file the website's "Download for Windows" link points at).

.DESCRIPTION
    1) Stops any running Galileo.
    2) Publishes a self-contained Release build (bundles the .NET runtime + Windows App SDK) to a
       staging folder named "Galileo".
    3) Compresses that folder into docs\Galileo-Latest.zip (extracts to a "Galileo" folder; run
       Galileo.exe inside it).

.PARAMETER Configuration
    Build configuration to publish (default: Release).
#>
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'

$repo    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'src\Galileo.App\Galileo.App.csproj'
$staging = Join-Path $env:TEMP 'Galileo-pkg'
$appDir  = Join-Path $staging 'Galileo'        # zip root folder name
$docs    = Join-Path $repo 'docs'
$zip     = Join-Path $docs 'Galileo-Latest.zip'

# 1) Stop running instances so files aren't locked.
Get-Process 'Galileo' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping running Galileo (pid $($_.Id))..." -ForegroundColor DarkGray
    $_ | Stop-Process -Force
}
Start-Sleep -Milliseconds 400

# 2) Clean staging + publish self-contained.
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $appDir | Out-Null

Write-Host "Publishing self-contained $Configuration build..." -ForegroundColor Cyan
dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $appDir
if ($LASTEXITCODE -ne 0) { Write-Error "Publish failed (exit $LASTEXITCODE)."; return }
if (-not (Test-Path (Join-Path $appDir 'Galileo.exe'))) { Write-Error "Publish did not produce Galileo.exe."; return }

# 3) Zip into docs\Galileo-Latest.zip.
New-Item -ItemType Directory -Force -Path $docs | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }
Write-Host "Compressing -> $zip" -ForegroundColor Cyan
Compress-Archive -Path $appDir -DestinationPath $zip -CompressionLevel Optimal

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "Created $zip ($mb MB)" -ForegroundColor Green
if ($mb -gt 100) {
    Write-Warning "Zip is over 100 MB - GitHub blocks files >100 MB on push. Use Git LFS or host it as a GitHub Release asset instead of committing it to docs/."
}
