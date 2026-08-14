# Installs optional build-time dependencies used by the ZV compiler.
# This script is invoked by the MSI when the user opts in to installing
# optional tools. Failures are intentionally non-fatal; the installer will
# continue and just report the issue.

$ErrorActionPreference = "Stop"

try {
    if (-not (Get-Command scoop -ErrorAction SilentlyContinue)) {
        Write-Host "Installing Scoop package manager..."
        Invoke-RestMethod -Uri https://get.scoop.sh | Invoke-Expression
    } else {
        Write-Host "Scoop is already installed."
    }

    Write-Host "Installing LLVM (provides clang and lld) via Scoop..."
    scoop install llvm

    Write-Host "Optional dependencies installed successfully."
} catch {
    Write-Warning "Optional dependency installation failed: $_"
    Write-Warning "You can install them manually later by running: scoop install llvm"
}
