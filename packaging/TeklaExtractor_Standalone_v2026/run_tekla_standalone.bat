@echo off
setlocal
title Tekla Structures 2026 - Standalone 2D Detailing Engine
cls
echo ===============================================================================
echo     TEKLA STRUCTURES 2026: STANDALONE OPEN API 2D DRAWING ENGINE
echo ===============================================================================
echo.
echo  Prerequisite: Tekla Structures 2026 must be RUNNING with your model open!
echo.

set "PROJECT_ROOT=%~dp0..\.."

echo  Project Root : %PROJECT_ROOT%
echo.
echo  Select an option:
echo    [1] Generate 2D Drawings ^& Booklet for W10-175 (PG1-3 + 7-Tier Fitter + Collate)
echo    [2] Generate 2D Drawings ^& Booklet for W10-78  (PG1-3 + 7-Tier Fitter + Collate)
echo    [3] Generate 2D Drawings ^& Booklet for Custom Piece Mark
echo    [4] Generate 2D Civil Drawings for Selected Cast Units in Tekla
echo    [5] Extract 3D Model Ground Truth to JSON + CSV
echo    [6] Re-build Standalone Engine (dotnet build Release x64)
echo    [7] Exit
echo.
set /p choice="Enter choice [1-7]: "

if "%choice%"=="1" goto run_w10_175
if "%choice%"=="2" goto run_w10_78
if "%choice%"=="3" goto run_custom
if "%choice%"=="4" goto run_civil
if "%choice%"=="5" goto run_extract
if "%choice%"=="6" goto run_build
if "%choice%"=="7" goto end

echo Invalid option. Exiting.
pause
goto end

:run_w10_175
echo.
echo ===============================================================================
echo   Generating 2D Civil Drawings ^& Collating Booklet for W10-175...
echo ===============================================================================
cd /d "%PROJECT_ROOT%"
bin\x64\Release\net48\TeklaExtractor.exe --civil-drawings --mark W10-175 --clean-mark
if %errorlevel% equ 0 (
    echo.
    echo Collating Multi-Page Fabrication Booklet...
    python Scripts\collate_production_booklet.py --mark W10-175
)
pause
goto end

:run_w10_78
echo.
echo ===============================================================================
echo   Generating 2D Civil Drawings ^& Collating Booklet for W10-78...
echo ===============================================================================
cd /d "%PROJECT_ROOT%"
bin\x64\Release\net48\TeklaExtractor.exe --civil-drawings --mark W10-78 --clean-mark
if %errorlevel% equ 0 (
    echo.
    echo Collating Multi-Page Fabrication Booklet...
    python Scripts\collate_production_booklet.py --mark W10-78
)
pause
goto end

:run_custom
echo.
set /p custom_mark="Enter Piece Mark (e.g. W10-175, W10-78, P6-1): "
echo ===============================================================================
echo   Generating 2D Civil Drawings ^& Collating Booklet for %custom_mark%...
echo ===============================================================================
cd /d "%PROJECT_ROOT%"
bin\x64\Release\net48\TeklaExtractor.exe --civil-drawings --mark %custom_mark% --clean-mark
if %errorlevel% equ 0 (
    echo.
    echo Collating Multi-Page Fabrication Booklet...
    python Scripts\collate_production_booklet.py --mark %custom_mark%
)
pause
goto end

:run_civil
echo.
echo Generating 2D Civil Drawings for Selected Cast Units via Open API...
cd /d "%PROJECT_ROOT%"
bin\x64\Release\net48\TeklaExtractor.exe --civil-drawings
pause
goto end

:run_extract
echo.
echo Extracting 3D Model Data to Ground Truth DB...
cd /d "%PROJECT_ROOT%"
bin\x64\Release\net48\TeklaExtractor.exe --extract
pause
goto end

:run_build
echo.
echo Building Release x64 Standalone Executable...
cd /d "%PROJECT_ROOT%"
dotnet build -c Release -p:PlatformTarget=x64
pause
goto end

:end
endlocal
