@echo off
setlocal
rem ---------------------------------------------------------------------------
rem Builds DreamTray into dist\. No administrator rights needed to build; the
rem resulting exe asks for them itself when it runs.
rem
rem Framework-dependent: needs the .NET 8 Desktop Runtime on the target machine,
rem which keeps the output ~2 MB instead of ~150 MB self-contained.
rem ---------------------------------------------------------------------------

cd /d "%~dp0"

echo Building DreamTray (Release, x64)...
dotnet publish src\DreamTray.App\DreamTray.App.csproj ^
    -c Release -r win-x64 --self-contained false ^
    -p:PublishSingleFile=false ^
    -o dist
if errorlevel 1 goto :failed

rem The publish output does not run the app project's plugin staging target, so
rem copy the bundled plugin across explicitly.
echo Staging bundled plugins...
dotnet build plugins\DreamTray.Plugin.CyberVfd\DreamTray.Plugin.CyberVfd.csproj -c Release
if errorlevel 1 goto :failed

if not exist "dist\plugins\CyberVfd" mkdir "dist\plugins\CyberVfd"
xcopy /Y /E /I /Q ^
    "plugins\DreamTray.Plugin.CyberVfd\bin\Release\net8.0-windows\*" ^
    "dist\plugins\CyberVfd\" >nul
if errorlevel 1 goto :failed

if not exist "dist\native" mkdir "dist\native"
copy /Y "src\DreamTray.App\native\README.md" "dist\native\" >nul

echo.
echo Done: dist\DreamTray.exe
echo.
echo Next steps:
echo   * For the TDP slider, install the PawnIO driver from https://pawnio.eu
echo     (no files to copy — see dist\native\README.md).
echo   * Run dist\DreamTray.exe, then turn on Settings ^> General ^> start at sign-in.
echo.

choice /c yn /n /m "Launch dist\DreamTray.exe now? [y/n] "
if errorlevel 2 goto :eof
echo.
echo Launching DreamTray...
start "" "dist\DreamTray.exe"
goto :eof

:failed
echo.
echo BUILD FAILED.
exit /b 1
