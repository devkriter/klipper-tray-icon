@echo off
set VERSION=1.4.0
set GO2RTC_VERSION=v1.9.14
echo Installing PyInstaller...
py -m pip install pyinstaller

echo Fetching go2rtc (camera bridge for Bambu RTSPS printers)...
if not exist go2rtc.exe (
    powershell -NoProfile -Command "$ErrorActionPreference='Stop'; Invoke-WebRequest -Uri 'https://github.com/AlexxIT/go2rtc/releases/download/%GO2RTC_VERSION%/go2rtc_win64.zip' -OutFile go2rtc_win64.zip; Expand-Archive -Path go2rtc_win64.zip -DestinationPath . -Force; Get-ChildItem -Filter 'go2rtc_win64.exe' | Rename-Item -NewName go2rtc.exe -Force; Remove-Item go2rtc_win64.zip"
)

echo building portable app...
py -m PyInstaller --onefile --noconsole --name "KlipperTrayIcon_v%VERSION%" --hidden-import=tkinter --hidden-import=tkinter.messagebox --hidden-import=tkinter.simpledialog --hidden-import=PIL.ImageTk --collect-submodules=paho --add-binary "go2rtc.exe;." klipper_tray.py

echo Done! Your executable is in the dist/ folder.
pause
