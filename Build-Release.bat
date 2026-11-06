@echo off
setlocal
cd /d "%~dp0"

REM Builds the release files into dist\:
REM   MitsubishiUploader-Setup.exe   double-click to install (uninstall from Settings > Apps)
REM   MitsubishiUploader.exe         portable copy, no install
REM Both are the same program: named "...Setup.exe" it installs itself.

where dotnet >nul 2>nul || (echo ERROR: .NET 8 SDK is not installed.& goto :fail)

echo Building single EXE...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
dotnet publish MitsubishiUploader.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
if errorlevel 1 goto :fail

if not exist dist mkdir dist
copy /Y "bin\Release\net8.0-windows\win-x64\publish\MitsubishiUploader.exe" "dist\MitsubishiUploader-Setup.exe" >nul || goto :fail
copy /Y "bin\Release\net8.0-windows\win-x64\publish\MitsubishiUploader.exe" "dist\MitsubishiUploader.exe" >nul || goto :fail

echo.
echo BUILD SUCCESSFUL.
echo   %CD%\dist\MitsubishiUploader-Setup.exe
echo   %CD%\dist\MitsubishiUploader.exe
echo.
pause
exit /b 0

:fail
echo.
echo BUILD FAILED.
pause
exit /b 1
