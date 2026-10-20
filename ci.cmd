@echo off
setlocal
cd /d "%~dp0"

if not exist artifacts\ci mkdir artifacts\ci
set LOG=artifacts\ci\build.log

echo == 1/4 build: src (analyzers on) ==
dotnet build src\VisionStation.App -v q --nologo --no-incremental > "%LOG%" 2>&1
if errorlevel 1 (
    echo BUILD FAILED - src
    findstr /C:"error " "%LOG%"
    exit /b 1
)

echo == 2/4 build: tests ==
dotnet build tests\VisionStation.UnitTests -v q --nologo --no-incremental >> "%LOG%" 2>&1
if errorlevel 1 (
    echo BUILD FAILED - tests
    findstr /C:"error " "%LOG%"
    exit /b 1
)

:: --no-dependencies: App is built in 1/4; a rebuild duplicates WPF _wpftmp warnings and breaks the ratchet.
dotnet build tests\VisionStation.App.Tests -v q --nologo --no-incremental --no-dependencies >> "%LOG%" 2>&1
if errorlevel 1 (
    echo BUILD FAILED - app tests
    findstr /C:"error " "%LOG%"
    exit /b 1
)

echo == 3/4 analyzer ratchet ==
where python >nul 2>nul
if errorlevel 1 (
    py -3 scripts\analyzer_gate.py --log "%LOG%"
) else (
    python scripts\analyzer_gate.py --log "%LOG%"
)
if errorlevel 1 goto fail

echo == 4/4 test ==
dotnet test tests\VisionStation.UnitTests --nologo --no-build
if errorlevel 1 goto fail

dotnet test tests\VisionStation.App.Tests --nologo --no-build
if errorlevel 1 goto fail

echo.
echo CI OK
exit /b 0

:fail
echo.
echo CI FAILED
exit /b 1
