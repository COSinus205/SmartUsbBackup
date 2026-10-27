@echo off
setlocal

cd /d "%~dp0"

echo === Step 1: dotnet publish ===
dotnet publish -c Release
if errorlevel 1 (
    echo Publish failed. Aborting.
    pause
    exit /b 1
)

echo.
echo === Step 2: Inno Setup (ISCC) ===
set "ISCC=C:\Users\0000\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
    echo ISCC.exe not found at "%ISCC%".
    echo Install Inno Setup 6 and retry.
    pause
    exit /b 1
)
"%ISCC%" "installer\SmartUsbBackup.iss"
if errorlevel 1 (
    echo ISCC failed. Aborting.
    pause
    exit /b 1
)

echo.
echo === Done: installer is in installer\dist ===
pause