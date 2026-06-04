<#
.SYNOPSIS
    Registers PhotosPlus with Windows as a selectable photo app (per-user, no admin).

.DESCRIPTION
    Creates a ProgID, an "Applications" entry (for the Open with list), per-extension
    OpenWithProgids entries, and a Capabilities/RegisteredApplications entry (so PhotosPlus
    appears in Settings > Apps > Default apps). All writes are under HKCU and are reversible
    with tools\unregister-default.ps1.

    NOTE: Windows 10/11 does not allow apps to silently *become* the default. After running
    this, set PhotosPlus as the default in Settings > Default apps (or right-click a photo >
    Open with > Choose another app > Always).

.PARAMETER ExePath
    Full path to PhotosPlus.App.exe. Defaults to the published install under %LocalAppData%.
#>
param(
    [string]$ExePath = (Join-Path $env:LOCALAPPDATA 'PhotosPlus\app\Galileo.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    Write-Error "PhotosPlus.App.exe not found at: $ExePath`nPublish first, or pass -ExePath."
    return
}
$ExePath = (Resolve-Path $ExePath).Path

$progId  = 'PhotosPlus.Image'
$appName = 'Galileo.exe'
$exts = @(
    'jpg','jpeg','jpe','jfif','png','gif','bmp','dib','tif','tiff','webp',
    'heic','heif','avif','ico','cr2','cr3','nef','arw','dng','raf','orf','rw2'
)

$cmd  = '"' + $ExePath + '" "%1"'
$icon = '"' + $ExePath + '",0'

function Ensure-Key($path) { if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null } }
function Set-Default($path, $value) { Ensure-Key $path; Set-ItemProperty -Path $path -Name '(default)' -Value $value }

Write-Host "Registering PhotosPlus -> $ExePath" -ForegroundColor Cyan

# 1) ProgID: how to open a "Galileo image"
Set-Default "HKCU:\Software\Classes\$progId" 'Galileo Image'
Set-ItemProperty "HKCU:\Software\Classes\$progId" -Name 'FriendlyTypeName' -Value 'Galileo Image'
Set-Default "HKCU:\Software\Classes\$progId\DefaultIcon" $icon
Set-Default "HKCU:\Software\Classes\$progId\shell\open\command" $cmd

# 2) Application entry (Open with list) + supported types
Set-Default "HKCU:\Software\Classes\Applications\$appName\shell\open\command" $cmd
Set-ItemProperty "HKCU:\Software\Classes\Applications\$appName" -Name 'FriendlyAppName' -Value 'Galileo'
Ensure-Key "HKCU:\Software\Classes\Applications\$appName\SupportedTypes"

# 3) Capabilities (Settings > Default apps)
$cap = 'HKCU:\Software\PhotosPlus\Capabilities'
Ensure-Key $cap
Set-ItemProperty $cap -Name 'ApplicationName'        -Value 'Galileo'
Set-ItemProperty $cap -Name 'ApplicationDescription' -Value 'A modern Windows Explorer + Photos alternative.'
Ensure-Key "$cap\FileAssociations"

foreach ($ext in $exts) {
    $dot = ".$ext"
    # add PhotosPlus to the Open-with list for this extension (does not steal the default)
    Ensure-Key "HKCU:\Software\Classes\$dot\OpenWithProgids"
    Set-ItemProperty "HKCU:\Software\Classes\$dot\OpenWithProgids" -Name $progId -Value ([byte[]]@()) -Type None
    # advertise the supported type on the Application
    Set-ItemProperty "HKCU:\Software\Classes\Applications\$appName\SupportedTypes" -Name $dot -Value ''
    # map the extension to our ProgID for Default apps
    Set-ItemProperty "$cap\FileAssociations" -Name $dot -Value $progId
}

# 4) Register the capabilities so Windows lists the app
Ensure-Key 'HKCU:\Software\RegisteredApplications'
Set-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name 'PhotosPlus' -Value 'Software\PhotosPlus\Capabilities'

# 5) Tell the shell associations changed
$sig = '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);'
$shell = Add-Type -MemberDefinition $sig -Namespace 'PhotosPlusWin32' -Name 'Shell' -PassThru
$shell::SHChangeNotify(0x08000000, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero)  # SHCNE_ASSOCCHANGED

Write-Host "Done. $($exts.Count) extensions registered." -ForegroundColor Green
Write-Host ""
Write-Host "Next: set Galileo as default ->" -ForegroundColor Yellow
Write-Host "  Settings > Apps > Default apps > search 'Galileo' (or set per file type),"
Write-Host "  or right-click a photo > Open with > Choose another app > Galileo > Always."
