@echo off
chcp 65001 >nul
rem ============================================================
rem  NG 保持窗口演示：OK ×2 → NG ×1 → OK ×4（700ms 间隔）
rem
rem  先启动 VisionStation（等它显示“等待检测结果”），再跑本脚本。
rem  预期：
rem    1) 前两件 OK 正常上屏；
rem    2) NG 上屏后画面**停住**约 2 秒，左上角出现“NG 保持中”角标，
rem       右下角出现 NG 缩略图；
rem    3) 期间到达的前 2 个 OK（0.7s、1.4s）不上屏，第 3 个（约 2.1s）才替换主图
rem       —— 这就是“抬头能看见刚才那件 NG”。
rem ============================================================
cd /d "%~dp0.."

set SRC=TestImages\VirtualCamera\2448x2048
set GOOD=%SRC%\01_good.bmp
set BAD=%SRC%\02_bad_missing_hole.bmp

if not exist "%GOOD%" (
    echo [失败] 找不到样本图：%GOOD%
    pause
    exit /b 1
)

echo === 1/3 两件 OK ===
dotnet run --project tools\VmSimulator -p:RunAnalyzers=false -- --verdict OK --count 2 --interval 900 --image "%GOOD%"
timeout /t 1 >nul

echo === 2/3 一件 NG（注意主图停住） ===
dotnet run --project tools\VmSimulator -p:RunAnalyzers=false -- --verdict NG --count 1 --image "%BAD%"
timeout /t 1 >nul

echo === 3/3 连续四件 OK（前 2 件应该不上屏） ===
dotnet run --project tools\VmSimulator -p:RunAnalyzers=false -- --verdict OK --count 4 --interval 700 --image "%GOOD%"

echo.
echo 演示结束。若主图没停住，先确认配方引擎是 Vm、VmLink 已监听 9100。
pause
