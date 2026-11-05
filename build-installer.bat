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
set "ISCC="

REM 1. Явное переопределение через переменную окружения (для CI и нестандартных установок)
if defined INNO_SETUP_ISCC if exist "%INNO_SETUP_ISCC%" set "ISCC=%INNO_SETUP_ISCC%"

REM 2. Per-user install (дефолт для non-admin)
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

REM 3. System-wide install (дефолт для admin)
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

REM 4. Inno Setup 5 (legacy, на случай старой установки)
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 5\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 5\ISCC.exe"

REM 5. Проверка: если не нашли — понятная ошибка
if not defined ISCC (
    echo [ERROR] Inno Setup ISCC.exe not found.
    echo Searched in:
    echo   - %%INNO_SETUP_ISCC%% ^(env var^)
    echo   - %%LOCALAPPDATA%%\Programs\Inno Setup 6\ISCC.exe
    echo   - %%ProgramFiles^(x86^)%%\Inno Setup 6\ISCC.exe
    echo   - %%ProgramFiles%%\Inno Setup 6\ISCC.exe
    echo   - %%ProgramFiles^(x86^)%%\Inno Setup 5\ISCC.exe
    echo.
    echo Install Inno Setup 6 from https://jrsoftware.org/isdl.php
    echo or set INNO_SETUP_ISCC environment variable to the full path of ISCC.exe.
    exit /b 1
)

echo Using ISCC: %ISCC%
"%ISCC%" "installer\SmartUsbBackup.iss"
if errorlevel 1 (
    echo ISCC failed. Aborting.
    pause
    exit /b 1
)

echo.
echo === Step 3: SHA-256 sidecars ===
powershell -NoProfile -Command "Get-ChildItem 'installer\dist\SmartUsbBackup-Setup-*.exe' | ForEach-Object { $h=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(); [IO.File]::WriteAllText($_.FullName + '.sha256', $h) }"
if errorlevel 1 (
    echo SHA-256 generation failed. Aborting.
    pause
    exit /b 1
)

echo.
echo === Done: installer is in installer\dist ===
pause