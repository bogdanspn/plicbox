@echo off
rem Double-click to build and start Plicbox. Options are passed on, e.g.  run -Demo
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
if errorlevel 1 pause
