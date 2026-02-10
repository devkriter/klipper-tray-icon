@echo off
echo Installing PyInstaller...
py -m pip install pyinstaller

echo building portable app...
py -m PyInstaller --onefile --noconsole --name "Klipper Tray Icon" klipper_tray.py

echo Done! Your executable is in the dist/ folder.
pause
