@echo off
rem IEMAS Agent installer. Double-click after extracting the whole zip to a folder.
rem The work is done by Install-IEMAS-Agent.ps1 next to this file.
title IEMAS Agent - install
cd /d "%~dp0"
if not exist "%~dp0IemasAgent.exe" goto notextracted
if not exist "%~dp0iemas-agent-setup.json" goto notextracted
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-IEMAS-Agent.ps1"
set RESULT=%ERRORLEVEL%
echo.
if not "%RESULT%"=="0" (
  echo The installation did not finish. Read the message above, or send a photo of this window to your IEMAS administrator.
) else (
  echo Done. You can close this window.
)
pause
exit /b %RESULT%
:notextracted
echo.
echo Please extract the zip first: right-click the zip file, choose "Extract All...", then
echo double-click Install-IEMAS-Agent.bat in the extracted folder.
echo.
pause
exit /b 1
