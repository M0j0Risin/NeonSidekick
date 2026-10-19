@echo off
rem Runs the app from source (JIT, no publish); any arguments are passed on (run.bat --headless).
cd /d "%~dp0"
dotnet run --project src/NeonSidekick -- %*
