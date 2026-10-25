@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Install the Microsoft .NET 10 SDK, then run this file again:
  echo https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)
dotnet run --project tests\LanBridge.Tests -c Release
if errorlevel 1 goto fail
dotnet publish src\LanBridge.Windows\LanBridge.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o release\win-x64
if errorlevel 1 goto fail
copy /y README.md release\win-x64\README.md >nul
copy /y WINDOWS-ACCEPTANCE.md release\win-x64\WINDOWS-ACCEPTANCE.md >nul
copy /y scripts\Enable-LAN.cmd release\win-x64\Enable-LAN.cmd >nul
copy /y scripts\Enable-LAN.ps1 release\win-x64\Enable-LAN.ps1 >nul
copy /y scripts\Test-Drag.cmd release\win-x64\Test-Drag.cmd >nul
echo.
echo Build complete: release\win-x64\LanBridge.exe
pause
exit /b 0
:fail
echo Build or tests failed. Read the error above; no release was approved.
pause
exit /b 1
