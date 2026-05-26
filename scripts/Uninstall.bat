@echo off
setlocal EnableDelayedExpansion

REM ============================================================
REM  OpenHintSQL - Uninstall from all installed SSMS versions
REM  Run as Administrator
REM
REM  Supported: SSMS 18.12.1 / 19.3 / 20.2.1 / 21.x / 22.x
REM
REM  Usage:  scripts\Uninstall.bat         - uninstalls from every detected version
REM          scripts\Uninstall.bat 20      - uninstalls from SSMS 20 only
REM ============================================================

set "TARGET_VERSION=%~1"
set "FOUND_SSMS=0"
set "REMOVED=0"
set "CLEARED=0"
set "MATCHED_TARGET=0"
set "FAILED=0"

echo.
echo  OpenHintSQL Uninstall Script
echo  ==================================
echo.

REM Check admin rights.
net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo  ERROR: Please run this script as Administrator.
    pause
    exit /b 1
)

tasklist 2>nul | find /I "Ssms.exe" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo  ERROR: SSMS is currently running.
    echo  Please save your work, close all SSMS windows, then run scripts\Uninstall.bat again.
    pause
    exit /b 1
)

for %%V in (18 19 20 21 22) do (
    if "!TARGET_VERSION!"=="" (
        call :uninstall_version %%V
        if "!FAILED!"=="1" goto :fail
    ) else if "!TARGET_VERSION!"=="%%V" (
        set MATCHED_TARGET=1
        call :uninstall_version %%V
        if "!FAILED!"=="1" goto :fail
    )
)

if not "%TARGET_VERSION%"=="" if "%MATCHED_TARGET%"=="0" (
    echo  ERROR: Unsupported SSMS version "%TARGET_VERSION%".
    echo  Supported versions are 18, 19, 20, 21, and 22.
    pause
    exit /b 1
)

if %FOUND_SSMS%==0 (
    echo  No matching SSMS installation found.
    if not "%TARGET_VERSION%"=="" (
        echo  SSMS %TARGET_VERSION% does not appear to be installed.
    ) else (
        echo  None of SSMS 18/19/20/21/22 were found on this machine.
    )
    pause
    exit /b 1
)

call :clear_openhintsql_cache

echo.
echo  ================================================
echo   Uninstall complete.
echo   SSMS versions checked : %FOUND_SSMS%
echo   Extension dirs removed: %REMOVED%
echo   Cache roots cleared   : %CLEARED%
echo  ================================================
echo.
pause
exit /b 0

:fail
echo.
echo  Uninstall failed. See errors above.
echo.
pause
exit /b 1


REM ============================================================
:uninstall_version
REM  Args: %1 = major version number (18/19/20/21/22)
REM ============================================================
set "V=%~1"

set "SSMS_DIR="
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

set /A FOUND_SSMS+=1
echo  SSMS %V% : found at !SSMS_DIR!

set "EXT_DIR=!SSMS_DIR!\Extensions\OpenHintSQL"

if exist "!EXT_DIR!" (
    echo             Removing !EXT_DIR!...
    rmdir /S /Q "!EXT_DIR!"
    if exist "!EXT_DIR!" (
        echo  ERROR: Could not remove !EXT_DIR!.
        echo  Check permissions and ensure SSMS is closed.
        set "FAILED=1"
        goto :eof
    )
    set /A REMOVED+=1
) else (
    echo             OpenHintSQL extension directory not found.
)

echo             Clearing caches...

set "LEGACY_CACHE=%LOCALAPPDATA%\Microsoft\SQL Server Management Studio\%V%.0_IsoShell"
call :clear_cache_root "!LEGACY_CACHE!"

REM SSMS 21+ moved local cache/private registry to Microsoft\SSMS\major.0_instanceId.
for /D %%C in ("%LOCALAPPDATA%\Microsoft\SSMS\%V%.0_*") do (
    call :clear_cache_root "%%~C"
)

echo  SSMS %V% : uninstall cleanup complete.
goto :eof


REM ============================================================
:clear_openhintsql_cache
REM  Removes OpenHintSQL's generated disk schema cache.
REM ============================================================
set "SCHEMA_CACHE=%LOCALAPPDATA%\OpenHintSQL\schemacache"
if exist "!SCHEMA_CACHE!" (
    echo  Clearing OpenHintSQL schema cache...
    rmdir /S /Q "!SCHEMA_CACHE!" >nul 2>&1
)

set "APP_DATA_DIR=%LOCALAPPDATA%\OpenHintSQL"
if exist "!APP_DATA_DIR!" (
    rmdir /Q "!APP_DATA_DIR!" >nul 2>&1
)
goto :eof


REM ============================================================
:clear_cache_root
REM  Args: %1 = SSMS local hive/cache root
REM ============================================================
set "CACHE_ROOT=%~1"
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

set /A CLEARED+=1
goto :eof
