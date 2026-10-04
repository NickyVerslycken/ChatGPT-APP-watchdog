@echo off
rem Builds the watchdog for local use/testing and writes everything to build.log.
rem Output: dist\ChatGPT-APP-watchdog\ChatGPT-APP-watchdog.exe (portable: settings and logs next to the exe)
setlocal
cd /d "%~dp0"
echo ==== %DATE% %TIME% ==== > build.log

tasklist /fi "imagename eq ChatGPT-APP-watchdog.exe" | find /i "ChatGPT-APP-watchdog.exe" >nul
if not errorlevel 1 (
  echo Stopping running watchdog so its files can be replaced... >> build.log
  taskkill /im ChatGPT-APP-watchdog.exe /f >> build.log 2>&1
  set WAS_RUNNING=1
  timeout /t 2 /nobreak >nul
)

dotnet --version >> build.log 2>&1
if errorlevel 1 (
  echo The .NET SDK was not found. Install it with: winget install Microsoft.DotNet.SDK.10 >> build.log
  echo The .NET SDK was not found. Install it with: winget install Microsoft.DotNet.SDK.10
  pause
  exit /b 1
)

echo ---- self-test ---- >> build.log
dotnet run --project tests\ChatGptWatchdog.SelfTest -c Release >> build.log 2>&1
if errorlevel 1 goto failed

echo ---- publish ---- >> build.log
dotnet publish src\ChatGptWatchdog.App\ChatGptWatchdog.App.csproj -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -o dist\ChatGPT-APP-watchdog >> build.log 2>&1
if errorlevel 1 goto failed
if not exist dist\ChatGPT-APP-watchdog\portable.txt echo Settings and logs are stored next to the exe while this file exists.> dist\ChatGPT-APP-watchdog\portable.txt

echo BUILD OK >> build.log
echo Build OK: dist\ChatGPT-APP-watchdog\ChatGPT-APP-watchdog.exe
rem Restart the watchdog if this script had to stop it.
if defined WAS_RUNNING start "" "dist\ChatGPT-APP-watchdog\ChatGPT-APP-watchdog.exe"
exit /b 0

:failed
echo BUILD FAILED >> build.log
echo Build failed - see build.log
timeout /t 20
exit /b 1
