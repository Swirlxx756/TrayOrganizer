@echo off
setlocal
chcp 65001 >nul
if not exist "%~dp0dist\TrayOrganizer\TrayOrganizer.exe" (
    call "%~dp0build.bat"
)
echo [TrayOrganizer] Starting TrayOrganizer.exe ...
start "" "%~dp0dist\TrayOrganizer\TrayOrganizer.exe" %*
endlocal
