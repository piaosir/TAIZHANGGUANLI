@echo off
chcp 936 >nul
setlocal enabledelayedexpansion
cd /d "%~dp0"

:menu
cls
echo ==========================================
echo    市场部台账管理系统 - 调试启动器
echo ==========================================
echo.
echo   [1] 运行桌面应用   (WPF App, Debug)
echo   [2] 运行 CLI 验证台 (打印统计口径, Debug)
echo   [3] 还原 NuGet 依赖 (dotnet restore)
echo   [4] 构建解决方案   (dotnet build)
echo   [5] 清理构建产物   (dotnet clean)
echo   [6] 发布自包含 exe  (win-x64, Release)
echo   [0] 退出
echo.
set /p "choice=请输入选项并回车: "

if "%choice%"=="1" goto run_app
if "%choice%"=="2" goto run_cli
if "%choice%"=="3" goto restore
if "%choice%"=="4" goto build
if "%choice%"=="5" goto clean
if "%choice%"=="6" goto publish
if "%choice%"=="0" exit /b 0
echo.
echo 无效选项，请重试。
pause
goto menu

:run_app
echo.
echo === 启动桌面应用 (Debug) ===
dotnet run --project "src\Weitong.Ledger.App\Weitong.Ledger.App.csproj"
goto done

:run_cli
echo.
echo === 运行 CLI 验证台 (Debug) ===
dotnet run --project "src\Weitong.Ledger.Cli\Weitong.Ledger.Cli.csproj"
goto done

:restore
echo.
echo === 还原依赖 ===
dotnet restore "src\WeitongLedger.sln"
goto done

:build
echo.
echo === 构建解决方案 (Debug) ===
dotnet build "src\WeitongLedger.sln"
goto done

:clean
echo.
echo === 清理构建产物 ===
dotnet clean "src\WeitongLedger.sln"
goto done

:publish
echo.
echo === 发布自包含 exe (win-x64, Release) ===
dotnet publish "src\Weitong.Ledger.App\Weitong.Ledger.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "发布\台账管理系统"
echo.
echo 输出目录: 发布\台账管理系统
goto done

:done
echo.
echo ------------------------------------------
echo 命令执行结束。按任意键返回菜单...
pause >nul
goto menu
