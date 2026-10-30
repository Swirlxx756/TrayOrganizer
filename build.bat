@echo off
setlocal
chcp 65001 >nul
echo [TrayOrganizer] Building Release TrayOrganizer.exe ...
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" "%~dp0src\TrayOrganizer\TrayOrganizer.csproj" /p:Configuration=Release /nologo /v:minimal
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Build failed with code %ERRORLEVEL%!
    exit /b %ERRORLEVEL%
)
echo [OK] Output executable: %~dp0dist\TrayOrganizer\TrayOrganizer.exe
endlocal
