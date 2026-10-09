@echo off
rem Runs install.ps1 from this folder (PowerShell blocks double-clicked .ps1 files by default).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
