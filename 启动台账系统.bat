@echo off
chcp 936 >nul
setlocal
cd /d "%~dp0"
set "PROJ=%~dp0src\Weitong.Ledger.App\Weitong.Ledger.App.csproj"
set "APPDIR=%~dp0src\Weitong.Ledger.App\bin\Release\net9.0-windows"

echo ============================================
echo    市场部台账管理系统 - 启动
echo ============================================
echo.
echo   正在编译最新版本，请稍候……
echo   (首次编译约 1-3 分钟；以后无改动仅需数秒)
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [错误] 未检测到 dotnet（.NET SDK）。请先安装 .NET 9 SDK 后再运行本启动器。
  echo        若本机只想直接运行、不编译，请改用已发布的独立版 exe。
  pause
  exit /b 1
)

dotnet build "%PROJ%" -c Release -v quiet -clp:ErrorsOnly
if errorlevel 1 (
  echo.
  echo [提示] 编译未成功。最常见原因：系统已经在运行（程序文件被占用）。
  echo        如果系统已经打开，请先关闭它，再重新双击本启动程序。
  echo        若确认没有在运行仍然失败，请把上面的错误信息发给开发者。
  pause
  exit /b 1
)

set "EXE="
for %%F in ("%APPDIR%\*.exe") do set "EXE=%%F"
if not defined EXE (
  echo [错误] 未找到可执行文件，可能编译未成功。
  pause
  exit /b 1
)

echo   启动中……
start "" "%EXE%"
exit /b 0
