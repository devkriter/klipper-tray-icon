@echo off
set VERSION=1.0.0
echo Installing PyInstaller...
py -m pip install pyinstaller

echo building portable app...
py -m PyInstaller --onefile --noconsole --name "KlipperTrayIcon_v%VERSION%" --hidden-import=tkinter --hidden-import=tkinter.messagebox --hidden-import=tkinter.simpledialog klipper_tray.py

echo Done! Your executable is in the dist/ folder.
pause
