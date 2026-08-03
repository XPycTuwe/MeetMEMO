@echo off
rem MeetMemo launcher.
rem NOTE: this file must stay ASCII-only. cmd.exe reads .bat files in the OEM code page,
rem so non-ASCII characters here break the parser on localized Windows.
rem
rem The app lives in the notification area: no window appears after launch,
rem look for the tray icon in the bottom-right corner.

setlocal
set "ROOT=%~dp0"
set "DIST=%ROOT%dist\MeetMemo\MeetMemo.exe"
set "REL=%ROOT%src\MeetMemo.App\bin\Release\net8.0-windows10.0.19041.0\MeetMemo.exe"
set "DBG=%ROOT%src\MeetMemo.App\bin\Debug\net8.0-windows10.0.19041.0\MeetMemo.exe"

set "TARGET="
if exist "%DIST%" set "TARGET=%DIST%"
if not defined TARGET if exist "%REL%" set "TARGET=%REL%"
if not defined TARGET if exist "%DBG%" set "TARGET=%DBG%"

if not defined TARGET (
    echo MeetMemo is not built yet. Run publish.cmd first.
    pause
    exit /b 1
)

rem A second instance would fight for the global hotkeys.
tasklist /fi "imagename eq MeetMemo.exe" 2>nul | find /i "MeetMemo.exe" >nul
if not errorlevel 1 (
    echo MeetMemo is already running - see the tray icon.
    rem ping instead of timeout: timeout fails when stdin is redirected.
    ping -n 4 127.0.0.1 >nul
    exit /b 0
)

start "" "%TARGET%"
exit /b 0
