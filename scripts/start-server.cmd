@echo off
rem TjViewer 服务器启动脚本（Windows）
rem 用法：start-server.cmd [端口]，默认 5000，监听 0.0.0.0 便于局域网访问
setlocal
set PORT=%~1
if "%PORT%"=="" set PORT=5000
cd /d "%~dp0"
if not defined ASPNETCORE_ENVIRONMENT set ASPNETCORE_ENVIRONMENT=Production
echo.
echo  TjViewer Server  http://0.0.0.0:%PORT%
echo  本机访问         http://127.0.0.1:%PORT%
echo  管理后台         http://127.0.0.1:%PORT%/admin.html
echo  首次启动会把管理员口令写入 admin.credentials
echo.
"%~dp0TjViewer.Server.exe" --urls "http://0.0.0.0:%PORT%"
pause
