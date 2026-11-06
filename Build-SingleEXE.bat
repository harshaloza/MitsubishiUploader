@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET 8 SDK is not installed.
  echo Install the .NET 8 SDK, then run this file again.
  pause
  exit /b 1
)

echo Cleaning old build...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj

echo Building single EXE...
dotnet publish MitsubishiUploader.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
if errorlevel 1 (
  echo.
  echo BUILD FAILED.
  pause
  exit /b 1
)

echo.
echo BUILD SUCCESSFUL.
echo EXE:
echo %CD%\bin\Release\net8.0-windows\win-x64\publish\MitsubishiUploader.exe
echo.
pause
