@echo off
rem ============================================================
rem  RainBot 发布脚本
rem  1) dotnet publish RainBot.csproj -c Release -o publish
rem     只发布主项目（不编译测试项目）；Release 发布会自动执行
rem     webui 的 npm install + vite build，产物在 wwwroot\webui
rem  2) 清理测试相关残留文件（旧脚本按解决方案发布会把测试项目带进来）
rem  3) 把前端产物 wwwroot\webui 同步到 publish\wwwroot\webui
rem ============================================================
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 dotnet 命令，请先安装 .NET SDK 并加入 PATH。
    goto :fail
)

echo [1/3] dotnet publish RainBot.csproj -c Release -o publish
echo        ^(Release 发布会自动构建 WebUI 前端：npm install + vite build^)
dotnet publish "RainBot.csproj" -c Release -o publish
if errorlevel 1 goto :fail

echo.
echo [2/3] 清理发布目录里的测试残留（测试项目 / xunit / TestPlatform / 覆盖率工具）
del /q "publish\RainBot.Tests.*" 2>nul
del /q "publish\xunit*.*" 2>nul
del /q "publish\testhost.*" 2>nul
del /q "publish\Microsoft.TestPlatform.*" 2>nul
del /q "publish\Microsoft.VisualStudio.*" 2>nul
del /q "publish\Microsoft.CodeCoverage*" 2>nul
del /q "publish\Microsoft.DiaSymReader.dll" 2>nul
del /q "publish\Mono.Cecil*" 2>nul
del /q "publish\Newtonsoft.Json.dll" 2>nul
if exist "publish\CodeCoverage" rd /s /q "publish\CodeCoverage"
if exist "publish\InstrumentationEngine" rd /s /q "publish\InstrumentationEngine"
if exist "publish\webui" rd /s /q "publish\webui"
for %%d in (cs de es fr it ja ko pl pt-BR ru tr zh-Hans zh-Hant) do (
    if exist "publish\%%d" (
        del /q "publish\%%d\Microsoft.TestPlatform.*.resources.dll" 2>nul
        del /q "publish\%%d\Microsoft.VisualStudio.*.resources.dll" 2>nul
        rd "publish\%%d" 2>nul
    )
)
echo       已清理。

echo.
echo [3/3] 同步 WebUI 前端产物到发布目录
if not exist "wwwroot\webui\index.html" (
    echo [警告] 未找到 wwwroot\webui\index.html，跳过同步。
    echo         请在 webui 目录执行 npm install ^&^& npm run build 生成前端产物。
    goto :done
)

if exist "publish\wwwroot\webui" rd /s /q "publish\wwwroot\webui"
xcopy "wwwroot\webui" "publish\wwwroot\webui" /E /I /Y >nul
if errorlevel 1 (
    echo [错误] 复制 WebUI 到发布目录失败。
    goto :fail
)

for /f %%c in ('dir /b "publish\wwwroot\webui\assets" 2^>nul ^| find /c /v ""') do set ASSET_COUNT=%%c
echo       已复制 %ASSET_COUNT% 个前端资源文件。

fc /b "wwwroot\webui\index.html" "publish\wwwroot\webui\index.html" >nul 2>nul
if errorlevel 1 (
    echo [警告] 发布目录的 index.html 与构建产物不一致，请检查。
) else (
    echo      校验通过：发布目录 index.html 与构建产物一致。
)

:done
echo.
echo ============================================================
echo  发布完成：%~dp0publish
echo  本地启动：dotnet "publish\RainBot.dll"
echo ============================================================
exit /b 0

:fail
echo.
echo ******** 发布失败，请检查上面的错误信息 ********
pause
exit /b 1
