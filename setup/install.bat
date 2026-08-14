@echo off
:: One-click wrapper for the PowerShell installer.
:: Run this from the extracted release folder (it sits next to ZV.exe and lib/).
powershell -ExecutionPolicy Bypass -File "%~dp0install.ps1" -SourceDir "%~dp0.."
pause
