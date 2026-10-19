@echo off
setlocal
cd /d "%~dp0"

set SCOPE=%1
set ART=%2
if "%SCOPE%"=="" set SCOPE=audit\artifacts\scope-ops.json
if "%ART%"=="" set ART=artifacts

echo == 1/3 build ==
dotnet build tests\VisionStation.UnitTests -v q --nologo
if errorlevel 1 goto fail

echo == 2/3 test ==
dotnet test tests\VisionStation.UnitTests --nologo
if errorlevel 1 goto fail

echo == 3/3 gate ==
where python >nul 2>nul
if errorlevel 1 (
    py -3 audit\scripts\gate_check.py --art "%ART%" --scope "%SCOPE%"
) else (
    python audit\scripts\gate_check.py --art "%ART%" --scope "%SCOPE%"
)
if errorlevel 1 goto fail

echo.
echo VERIFY OK
exit /b 0

:fail
echo.
echo VERIFY FAILED
exit /b 1
