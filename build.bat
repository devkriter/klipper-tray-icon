@echo off
set VERSION=1.4.2
set GO2RTC_VERSION=v1.9.14
set FFMPEG_ASSET=ffmpeg-n7.1-latest-win64-lgpl-7.1.zip
echo Installing PyInstaller...
py -m pip install pyinstaller

echo Fetching go2rtc (camera bridge for Bambu RTSPS printers)...
if not exist go2rtc.exe (
    powershell -NoProfile -Command "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; Invoke-WebRequest -Uri 'https://github.com/AlexxIT/go2rtc/releases/download/%GO2RTC_VERSION%/go2rtc_win64.zip' -OutFile go2rtc_win64.zip; Expand-Archive -Path go2rtc_win64.zip -DestinationPath . -Force; Get-ChildItem -Filter 'go2rtc_win64.exe' | Rename-Item -NewName go2rtc.exe -Force; Remove-Item go2rtc_win64.zip"
)

echo Fetching ffmpeg (go2rtc requires it to decode Bambu's RTSPS camera stream)...
if not exist ffmpeg.exe (
    powershell -NoProfile -Command "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; Invoke-WebRequest -Uri 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/%FFMPEG_ASSET%' -OutFile ffmpeg_build.zip; Expand-Archive -Path ffmpeg_build.zip -DestinationPath ffmpeg_extract -Force; Get-ChildItem -Path ffmpeg_extract -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1 | Copy-Item -Destination .\ffmpeg.exe; Remove-Item ffmpeg_build.zip, ffmpeg_extract -Recurse -Force"
)

echo building portable app...
py -m PyInstaller --onefile --noconsole --name "KlipperTrayIcon_v%VERSION%" --hidden-import=tkinter --hidden-import=tkinter.messagebox --hidden-import=tkinter.simpledialog --hidden-import=PIL.ImageTk --collect-submodules=paho --add-binary "go2rtc.exe;." --add-binary "ffmpeg.exe;." klipper_tray.py

echo Done! Your executable is in the dist/ folder.
pause
