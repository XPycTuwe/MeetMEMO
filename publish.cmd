@echo off
rem Builds a ready-to-run MeetMemo into dist\.
rem NOTE: ASCII-only on purpose - cmd.exe reads .bat/.cmd in the OEM code page.
rem
rem   publish.cmd              self-contained build (no .NET install required)
rem   publish.cmd -Portable    lightweight build (needs .NET 8 Desktop Runtime)

setlocal
set "PS=pwsh.exe"
where %PS% >nul 2>nul || set "PS=powershell.exe"

%PS% -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*

echo.
pause
