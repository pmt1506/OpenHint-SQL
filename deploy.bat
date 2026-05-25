@echo off
setlocal EnableDelayedExpansion

REM ============================================================
REM  OpenHintSQL — Deploy to all installed SSMS versions
REM  Run as Administrator
REM
REM  Supported: SSMS 18.12.1 / 19.3 / 20.2.1 / 21.x / 22.x
REM
REM  Usage:  deploy.bat          — deploys to every installed version
REM          deploy.bat 20       — deploys to SSMS 20 only
REM ============================================================

set BUILD_DIR=%~dp0src\OpenHintSQL\bin\Release\net48
set TARGET_VERSION=%1
set BUILD_LOG=%TEMP%\OpenHintSQL-build.log

echo.
echo  OpenHintSQL Deployment Script
echo  ================================
echo.

REM ── Check admin rights ─────────────────────────────────────
net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo  ERROR: Please run this script as Administrator!
    pause
    exit /b 1
)

tasklist 2>nul | find /I "Ssms.exe" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo  ERROR: SSMS is currently running.
    echo  Please save your work, close all SSMS windows, then run deploy.bat again.
    pause
    exit /b 1
)

REM ── Auto-build project ─────────────────────────────────────
echo  Building OpenHintSQL (Release)...
dotnet build "%~dp0src\OpenHintSQL\OpenHintSQL.csproj" -c Release --nologo --verbosity:minimal > "%BUILD_LOG%" 2>&1
if %ERRORLEVEL% neq 0 (
    echo  ERROR: Build failed! Check the errors above.
    type "%BUILD_LOG%"
    pause
    exit /b 1
)
echo  Build completed.

REM ── Double-check build output ──────────────────────────────
if not exist "%BUILD_DIR%\OpenHintSQL.dll" (
    echo  ERROR: Build output not found in %BUILD_DIR%.
    pause
    exit /b 1
)

REM ── SSMS version table ─────────────────────────────────────
REM Format: VERSION  PF_DIR  ISOSHELL_SUFFIX
REM SSMS 18-20 install to Program Files (x86)  [32-bit]
REM SSMS 21-22 install to Program Files        [64-bit]

set DEPLOYED=0

for %%V in (18 19 20 21 22) do (
    if "!TARGET_VERSION!"=="" (
        call :deploy_version %%V
    ) else if "!TARGET_VERSION!"=="%%V" (
        call :deploy_version %%V
    )
)

if %DEPLOYED%==0 (
    echo  No matching SSMS installation found.
    if not "%TARGET_VERSION%"=="" (
        echo  SSMS %TARGET_VERSION% does not appear to be installed.
    ) else (
        echo  None of SSMS 18/19/20/21/22 were found on this machine.
    )
    pause
    exit /b 1
)

echo.
echo  ================================================
echo   Deployment complete!  %DEPLOYED% SSMS version(s) updated.
echo   Open SSMS and check View ^> Output ^> OpenHint SQL.
echo  ================================================
echo.
pause
exit /b 0


REM ============================================================
:deploy_version
REM  Args: %1 = major version number (18/19/20/21/22)
REM ============================================================
set V=%1

REM SSMS 21+ are 64-bit — check both Program Files paths (prefer x86 first
REM to match Microsoft's historical convention; fall back to 64-bit)
set SSMS_DIR=
for %%P in (
    "%ProgramFiles(x86)%\Microsoft SQL Server Management Studio %V%\Common7\IDE"
    "%ProgramFiles%\Microsoft SQL Server Management Studio %V%\Common7\IDE"
    "%ProgramFiles(x86)%\Microsoft SQL Server Management Studio %V%\Release\Common7\IDE"
    "%ProgramFiles%\Microsoft SQL Server Management Studio %V%\Release\Common7\IDE"
) do (
    if "!SSMS_DIR!"=="" if exist "%%~P\Ssms.exe" set SSMS_DIR=%%~P
)

if "!SSMS_DIR!"=="" (
    echo  SSMS %V% : not installed, skipping.
    goto :eof
)

echo  SSMS %V% : found at !SSMS_DIR!

set EXT_DIR=!SSMS_DIR!\Extensions\OpenHintSQL

REM ── Copy files ────────────────────────────────────────────
echo             Copying files to !EXT_DIR!...
if exist "!EXT_DIR!" rmdir /S /Q "!EXT_DIR!"
mkdir "!EXT_DIR!"

copy /Y "%BUILD_DIR%\*.dll"                    "!EXT_DIR!\" >nul
copy /Y "%BUILD_DIR%\OpenHintSQL.pkgdef"       "!EXT_DIR!\" >nul
copy /Y "%~dp0src\OpenHintSQL\source.extension.vsixmanifest" "!EXT_DIR!\extension.vsixmanifest" >nul

if not exist "!EXT_DIR!\Config" mkdir "!EXT_DIR!\Config"
xcopy /Y /E "%BUILD_DIR%\Config" "!EXT_DIR!\Config\" >nul

REM ── Clear SSMS %V% caches ─────────────────────────────────
echo             Clearing caches...

set LEGACY_CACHE=%LOCALAPPDATA%\Microsoft\SQL Server Management Studio\%V%.0_IsoShell
call :clear_cache_root "!LEGACY_CACHE!"

REM SSMS 21+ moved local cache/private registry to Microsoft\SSMS\major.0_instanceId.
for /D %%C in ("%LOCALAPPDATA%\Microsoft\SSMS\%V%.0_*") do (
    call :clear_cache_root "%%~C"
)

echo  SSMS %V% : deployed successfully.
set /A DEPLOYED+=1
goto :eof


REM ============================================================
:clear_cache_root
REM  Args: %1 = SSMS local hive/cache root
REM ============================================================
set CACHE_ROOT=%~1
if "!CACHE_ROOT!"=="" goto :eof
if not exist "!CACHE_ROOT!" goto :eof

if exist "!CACHE_ROOT!\ComponentModelCache" (
    rmdir /S /Q "!CACHE_ROOT!\ComponentModelCache"
)

if exist "!CACHE_ROOT!\Extensions" (
    del /F /Q "!CACHE_ROOT!\Extensions\extensions.*.cache" >nul 2>&1
    del /F /Q "!CACHE_ROOT!\Extensions\ExtensionMetadata*.mpack" >nul 2>&1
    type nul > "!CACHE_ROOT!\Extensions\extensions.configurationchanged" 2>nul
)

REM VS 2022/SSMS 21+ cache pkgdef registration in privateregistry.bin.
if exist "!CACHE_ROOT!\privateregistry.bin" (
    del /F /Q "!CACHE_ROOT!\privateregistry.bin" >nul 2>&1
    del /F /Q "!CACHE_ROOT!\privateregistry.bin.LOG*" >nul 2>&1
)
goto :eof
