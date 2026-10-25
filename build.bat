@echo off
set VERSION=1.6.0
set GO2RTC_VERSION=v1.9.14
set FFMPEG_ASSET=ffmpeg-n8.1-latest-win64-lgpl-8.1.zip
echo Installing PyInstaller...
py -m pip install pyinstaller
echo Installing app requirements...
py -m pip install -r requirements.txt

echo Fetching go2rtc (camera bridge for Bambu RTSPS printers)...
if not exist go2rtc.exe (
    powershell -NoProfile -Command "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; Invoke-WebRequest -Uri 'https://github.com/AlexxIT/go2rtc/releases/download/%GO2RTC_VERSION%/go2rtc_win64.zip' -OutFile go2rtc_win64.zip; Expand-Archive -Path go2rtc_win64.zip -DestinationPath . -Force; Get-ChildItem -Filter 'go2rtc_win64.exe' | Rename-Item -NewName go2rtc.exe -Force; Remove-Item go2rtc_win64.zip"
)

echo Fetching ffmpeg (go2rtc requires it to decode Bambu's RTSPS camera stream)...
if not exist ffmpeg.exe (
    powershell -NoProfile -Command "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; Invoke-WebRequest -Uri 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/%FFMPEG_ASSET%' -OutFile ffmpeg_build.zip; Expand-Archive -Path ffmpeg_build.zip -DestinationPath ffmpeg_extract -Force; Get-ChildItem -Path ffmpeg_extract -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1 | Copy-Item -Destination .\ffmpeg.exe; Remove-Item ffmpeg_build.zip, ffmpeg_extract -Recurse -Force"
)

for %%F in (go2rtc.exe ffmpeg.exe) do (
    if not exist %%F (
        echo ERROR: %%F is missing - its download failed. Check the URL above.
        pause
        exit /b 1
    )
)

echo Generating app icon...
py -c "from klipper_tray import create_tray_icon; create_tray_icon('complete', 1.0, size=256).save('app.ico', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (256, 256)])"

echo Writing version info...
powershell -NoProfile -Command "$v='%VERSION%'; $t=(($v -split '\.') + @('0','0','0','0'))[0..3] -join ', '; $c=(Get-Content version_info.template.txt -Raw).Replace('@VERSION_TUPLE@',$t).Replace('@VERSION@',$v); [IO.File]::WriteAllText((Join-Path (Get-Location) 'version_info.txt'), $c)"

echo Building portable app (one-folder build - avoids antivirus false positives from self-extracting exes)...
if exist "dist\KlipperTrayIcon" rmdir /s /q "dist\KlipperTrayIcon"
py -m PyInstaller --noconfirm --onedir --noconsole --noupx --name "KlipperTrayIcon" --version-file version_info.txt --icon app.ico --collect-data sv_ttk --hidden-import=tkinter --hidden-import=tkinter.messagebox --hidden-import=tkinter.simpledialog --hidden-import=PIL.ImageTk --collect-submodules=paho klipper_tray.py
if errorlevel 1 (
    echo PyInstaller build failed.
    pause
    exit /b 1
)

echo Copying go2rtc and ffmpeg next to the exe...
copy /y go2rtc.exe "dist\KlipperTrayIcon\" >nul
copy /y ffmpeg.exe "dist\KlipperTrayIcon\" >nul

echo Zipping release...
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; Compress-Archive -Path 'dist\KlipperTrayIcon' -DestinationPath 'dist\KlipperTrayIcon_v%VERSION%.zip' -Force"

echo Done! App folder: dist\KlipperTrayIcon\  Release zip: dist\KlipperTrayIcon_v%VERSION%.zip
pause
