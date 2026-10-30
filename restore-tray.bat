@echo off
setlocal
chcp 65001 >nul
echo [TrayOrganizer] Restoring all native tray icons ...
"%~dp0dist\TrayOrganizer\TrayOrganizer.exe" --restore-all
echo [OK] Done.
endlocal
