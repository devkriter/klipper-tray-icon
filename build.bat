@echo off
setlocal
rem Builds the release zip: publish\KlipperTrayIcon_v<VERSION>.zip
set VERSION=2.1.0
set OUT=publish\KlipperTrayIcon
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET 10 SDK is required: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Running tests...
dotnet test tests\KlipperTrayIcon.Tests -c Release --nologo -v q
if errorlevel 1 (
    echo Tests failed - not building a release.
    pause
    exit /b 1
)

echo Building...
if exist "%OUT%" rmdir /s /q "%OUT%"
dotnet publish src\KlipperTrayIcon -c Release -o "%OUT%" -p:Version=%VERSION% --nologo
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

echo Zipping release...
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; Compress-Archive -Path '%OUT%' -DestinationPath 'publish\KlipperTrayIcon_v%VERSION%.zip' -Force"

echo Done! App folder: %OUT%\  Release zip: publish\KlipperTrayIcon_v%VERSION%.zip
pause
