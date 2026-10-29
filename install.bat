@echo off
rem WindFlow installer for people who downloaded the source: builds the app and installs it for the
rem current user (no admin). Gets the .NET 10 SDK with winget if it is missing. Run it from the repo folder.
setlocal EnableExtensions
title WindFlow installer
cd /d "%~dp0"

echo.
echo   WindFlow installer
echo   ------------------
echo   Builds WindFlow from this folder and installs it for you. No admin rights needed.
echo.

if not exist "src\WindFlow.App\WindFlow.App.csproj" (
    echo   Run this file from the WindFlow folder, the one that contains "src".
    goto fail
)
if "%WINDFLOW_DIR%"=="" set "WINDFLOW_DIR=%LOCALAPPDATA%\Programs\WindFlow"

rem ---------------------------------------------------------------- 1. .NET 10 SDK
call :find_sdk
if not errorlevel 1 goto sdk_ok
echo   WindFlow needs the free Microsoft .NET 10 SDK to build, and it is not installed.
set "ANS=y"
set /p "ANS=  Download and install it now (about 250 MB)? [Y/n] "
if /i "%ANS%"=="n" goto no_sdk
where winget >nul 2>nul
if errorlevel 1 goto no_sdk
winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
call :find_sdk
if errorlevel 1 goto no_sdk
:sdk_ok
echo   .NET 10 SDK found.

rem ---------------------------------------------------------------- 2. graphics card (information only)
where nvidia-smi >nul 2>nul
if errorlevel 1 (
    echo   Note: no NVIDIA graphics driver found. WindFlow runs fastest on an NVIDIA card;
    echo   without one the simulation falls back to the processor and is much slower.
)

rem ---------------------------------------------------------------- 3. build and install
echo.
echo   Building and installing to "%WINDFLOW_DIR%" ^(takes a few minutes the first time^)...
"%DOTNET%" publish "src\WindFlow.App\WindFlow.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none -o "%WINDFLOW_DIR%" --nologo -v q
if errorlevel 1 (
    echo.
    echo   The build failed. If WindFlow is open, close it and run this again.
    goto fail
)
if not exist "%WINDFLOW_DIR%\WindFlow.App.exe" (
    echo   The build finished but WindFlow.App.exe is missing.
    goto fail
)

rem ---------------------------------------------------------------- 4. shortcuts
call :shortcut Programs "Start menu"
set "ANS=y"
set /p "ANS=  Put a WindFlow shortcut on the desktop? [Y/n] "
if /i not "%ANS%"=="n" call :shortcut Desktop "Desktop"

echo.
echo   Done. WindFlow is in the Start menu ^(and on the desktop if you chose it^).
set "ANS=y"
set /p "ANS=  Open WindFlow now? [Y/n] "
if /i not "%ANS%"=="n" start "" "%WINDFLOW_DIR%\WindFlow.App.exe"
echo   To uninstall: delete "%WINDFLOW_DIR%" and the WindFlow shortcuts.
pause
exit /b 0

rem ---------------------------------------------------------------- helpers
:find_sdk
rem Sets DOTNET to a dotnet.exe that has a 10.x SDK; errorlevel 1 if none. winget puts it in Program Files,
rem which this window's PATH does not see yet, so look there too.
for %%D in (dotnet.exe "%ProgramFiles%\dotnet\dotnet.exe") do (
    "%%~D" --list-sdks 2>nul | findstr /b "10." >nul && (set "DOTNET=%%~D" & exit /b 0)
)
exit /b 1

:shortcut
powershell -NoProfile -Command "$l=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('%1')+'\WindFlow.lnk'); $l.TargetPath='%WINDFLOW_DIR%\WindFlow.App.exe'; $l.WorkingDirectory='%WINDFLOW_DIR%'; $l.Description='WindFlow airflow simulation'; $l.Save()"
if errorlevel 1 (echo   Could not create the %~2 shortcut.) else (echo   %~2 shortcut created.)
exit /b 0

:no_sdk
echo.
echo   Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
echo   then run this file again.
:fail
echo.
pause
exit /b 1
