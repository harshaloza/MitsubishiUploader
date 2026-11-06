@echo off
REM Installs the version built by Build-SingleEXE.bat (or Build-Release.bat).
REM Same as double-clicking MitsubishiUploader-Setup.exe. Uninstall from Settings > Apps.
setlocal
cd /d "%~dp0"
set "SOURCE=%~dp0bin\Release\net8.0-windows\win-x64\publish\MitsubishiUploader.exe"
if not exist "%SOURCE%" (
    echo ERROR: Published EXE was not found. Run Build-SingleEXE.bat first.
    pause
    exit /b 1
)
"%SOURCE%" --install
