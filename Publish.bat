@echo off
chcp 65001 >nul
echo ========================================================
echo   LinkNexus 多通道硬件调试工作站 一键打包发布工具
echo ========================================================
echo.
echo 请选择发布模式：
echo [1] 完全独立单文件版 (Self-Contained) - 免装 .NET 运行时，双击即跑 [推荐]
echo [2] 依赖框架单文件版 (Framework-Dependent) - 体积极小 (~350KB)，需目标机安装 .NET 10
echo.
set /p choice="请输入数字 [1 或 2] 并按回车: "

if "%choice%"=="1" goto ModeSelfContained
if "%choice%"=="2" goto ModeFramework
echo 输入无效，默认执行完全独立单文件打包...
goto ModeSelfContained

:ModeSelfContained
echo.
echo [正在打包] 完全独立便携版 (win-x64)... 请稍候...
dotnet publish "LinkNexus\LinkNexus.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "Publish_SelfContained"
if %errorlevel% neq 0 (
    echo.
    echo [错误] 打包失败，请检查编译日志。
    pause
    exit /b %errorlevel%
)
echo.
echo ========================================================
echo 打包成功！
echo 输出目录: %~dp0Publish_SelfContained\
echo 生成文件: LinkNexus.exe (独立便携版) 与 HardwareConfig.json
echo ========================================================
explorer "%~dp0Publish_SelfContained"
pause
exit /b 0

:ModeFramework
echo.
echo [正在打包] 依赖框架轻量版 (win-x64)... 请稍候...
dotnet publish "LinkNexus\LinkNexus.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "Publish_FrameworkDependent"
if %errorlevel% neq 0 (
    echo.
    echo [错误] 打包失败，请检查编译日志。
    pause
    exit /b %errorlevel%
)
echo.
echo ========================================================
echo 打包成功！
echo 输出目录: %~dp0Publish_FrameworkDependent\
echo 生成文件: LinkNexus.exe (轻量版) 与 HardwareConfig.json
echo ========================================================
explorer "%~dp0Publish_FrameworkDependent"
pause
exit /b 0

