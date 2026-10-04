@echo off
rem Builds the two release variants into artifacts\ (same as the GitHub Actions workflow).
rem  - ChatGPT-APP-watchdog-win-x64.zip            : self-contained single exe, no .NET install needed
rem  - ChatGPT-APP-watchdog-win-x64-framework.zip  : small exe, needs the .NET 10 Desktop Runtime
setlocal
cd /d "%~dp0"
if exist artifacts rmdir /s /q artifacts
dotnet publish src\ChatGptWatchdog.App\ChatGptWatchdog.App.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o artifacts\self-contained || goto failed
dotnet publish src\ChatGptWatchdog.App\ChatGptWatchdog.App.csproj -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -o artifacts\framework || goto failed
powershell -NoProfile -Command "Compress-Archive -Force artifacts\self-contained\ChatGPT-APP-watchdog.exe artifacts\ChatGPT-APP-watchdog-win-x64.zip; Compress-Archive -Force artifacts\framework\ChatGPT-APP-watchdog.exe artifacts\ChatGPT-APP-watchdog-win-x64-framework.zip" || goto failed
echo Done. See the artifacts folder.
pause
exit /b 0
:failed
echo Publish failed.
pause
exit /b 1
