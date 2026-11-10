@echo off
rem Usage: couch-coop.bat [-Player1 Name] [-Player2 Name]
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch.ps1" %*
if errorlevel 1 pause
