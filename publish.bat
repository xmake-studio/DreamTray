@echo off
setlocal
rem ---------------------------------------------------------------------------
rem Builds DreamTray into dist\. No administrator rights needed to build; the
rem resulting exe asks for them itself when it runs.
rem
rem Framework-dependent: needs the .NET 8 Desktop Runtime on the target machine,
rem which keeps the output ~2 MB instead of ~150 MB self-contained.
rem
rem ReadyToRun: the first panel open is the one that JITs the WPF XAML parser, the
rem theme dictionaries and every widget's construction path, and that cost lands on
rem whichever click gets there before the idle prewarm does. Precompiling moves it
rem to build time, for a few MB of output. It needs the -r below; a RID-less
rem publish accepts the flag and silently does nothing with it.
rem
rem Every project under plugins\ is picked up and staged into dist\plugins\<name>\
rem automatically -- DreamTray.App.csproj's StageBundledPluginsForPublish target
rem discovers them by wildcard, so adding a new plugin needs no edit here.
rem ---------------------------------------------------------------------------

cd /d "%~dp0"

echo Building DreamTray (Release, x64)...
dotnet publish src\DreamTray.App\DreamTray.App.csproj ^
    -c Release -r win-x64 --self-contained false ^
    -p:PublishSingleFile=false ^
    -p:PublishReadyToRun=true ^
    -o dist
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
