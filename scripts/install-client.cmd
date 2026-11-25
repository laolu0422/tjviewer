@echo off
rem TjViewer client installer entry point (requires administrator)
rem For silent batch deployment append -Silent and other switches.
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-client.ps1" %*
exit /b %errorlevel%
