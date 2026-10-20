@echo off
setlocal
cd /d "%~dp0"

set SCOPE=%1
set ART=%2
if "%SCOPE%"=="" set SCOPE=audit\artifacts\scope-ops.json
if "%ART%"=="" set ART=artifacts

echo == 1/2 ci (build + tests + analyzer) ==
call ci.cmd
if errorlevel 1 goto fail

echo == 2/2 audit gate ==
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
