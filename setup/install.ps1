# ZV per-user installer for Windows
# Copies the compiler binary and lib/ folder into %LOCALAPPDATA%\ZV and adds
# that directory to the user PATH.

param(
    [string]$SourceDir = $PSScriptRoot
)

$installRoot = Join-Path $env:LOCALAPPDATA "ZV"
$binDir = $installRoot

function Ensure-Dir {
    param([string]$Path)
    if (!(Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

Ensure-Dir $installRoot

$sourceExe = Join-Path $SourceDir "ZV.exe"
if (!(Test-Path $sourceExe)) {
    Write-Error "ZV.exe not found in '$SourceDir'. Run this script from the same folder as the published binary, or pass -SourceDir."
    exit 1
}

# Copy the executable and any files it needs from the publish folder.
Write-Host "Installing ZV to $installRoot ..."
Get-ChildItem -Path $SourceDir -File | Copy-Item -Destination $installRoot -Force

# Copy the standard library.
$sourceLib = Join-Path $SourceDir "lib"
$destLib = Join-Path $installRoot "lib"
if (Test-Path $sourceLib) {
    if (Test-Path $destLib) {
        Remove-Item -Path $destLib -Recurse -Force
    }
    Copy-Item -Path $sourceLib -Destination $destLib -Recurse -Force
    Write-Host "Copied lib/ to $destLib"
}

# Add to user PATH if not already present.
$userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
$pathEntries = $userPath -split [System.IO.Path]::PathSeparator | Where-Object { $_ -ne "" }
$alreadyInPath = $pathEntries | Where-Object { $_ -ieq $installRoot }

if (!$alreadyInPath) {
    [Environment]::SetEnvironmentVariable("PATH", "$userPath$([System.IO.Path]::PathSeparator)$installRoot", "User")
    Write-Host "Added $installRoot to your user PATH."
    Write-Host "Restart your terminal for the change to take effect."
} else {
    Write-Host "$installRoot is already on your user PATH."
}

Write-Host "ZV installed successfully. You can now run 'zv' from a new terminal."
