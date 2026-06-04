<#
.SYNOPSIS
    Removes the PhotosPlus Windows file-association registration created by register-default.ps1.
    All changes are under HKCU.
#>
$ErrorActionPreference = 'SilentlyContinue'

$progId  = 'PhotosPlus.Image'
$appName = 'Galileo.exe'
$exts = @(
    'jpg','jpeg','jpe','jfif','png','gif','bmp','dib','tif','tiff','webp',
    'heic','heif','avif','ico','cr2','cr3','nef','arw','dng','raf','orf','rw2'
)

Write-Host "Unregistering PhotosPlus..." -ForegroundColor Cyan

Remove-Item "HKCU:\Software\Classes\$progId" -Recurse -Force
Remove-Item "HKCU:\Software\Classes\Applications\$appName" -Recurse -Force
Remove-Item 'HKCU:\Software\PhotosPlus' -Recurse -Force
Remove-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name 'PhotosPlus' -Force

foreach ($ext in $exts) {
    Remove-ItemProperty "HKCU:\Software\Classes\.$ext\OpenWithProgids" -Name $progId -Force
}

$sig = '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);'
$shell = Add-Type -MemberDefinition $sig -Namespace 'PhotosPlusWin32' -Name 'ShellU' -PassThru
$shell::SHChangeNotify(0x08000000, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host "Removed. If PhotosPlus was set as a default, Windows will fall back to the previous app." -ForegroundColor Green
