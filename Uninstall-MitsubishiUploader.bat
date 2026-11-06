@echo off
REM Uninstalls Mitsubishi Uploader: removes the right-click menu, the Start menu shortcut
REM and the program. Your settings file is kept. Same as Settings > Apps > Uninstall.
setlocal
set "INSTALLED=%SystemDrive%\Mitsubishi Uploader\MitsubishiUploader.exe"
if exist "%INSTALLED%" (
    "%INSTALLED%" --uninstall
) else (
    REM Program already gone: just clean up the right-click menu entries.
    reg.exe delete "HKCU\Software\Classes\*\shell\Mitsubishi Uploader" /f >nul 2>&1
    reg.exe delete "HKCU\Software\Classes\Directory\shell\Mitsubishi Uploader" /f >nul 2>&1
    reg.exe delete "HKCU\Software\Classes\*\shell\SendToMitsubishiCNC" /f >nul 2>&1
    reg.exe delete "HKCU\Software\Classes\Directory\shell\SendToMitsubishiCNC" /f >nul 2>&1
    echo Right-click menu entries removed.
    pause
)
