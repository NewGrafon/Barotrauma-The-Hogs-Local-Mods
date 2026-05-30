@echo off
chcp 65001 >nul

rem %~dp0 is the absolute path to this .bat folder (LocalMods),
rem so it does not depend on the current working directory.
set "MODS_DIR=%~dp0"
set "ROOT_DIR=%MODS_DIR%.."

rem === Pull mod updates ===
echo [1/3] Pulling mod updates...
pushd "%MODS_DIR%"
git pull
popd

rem === Copy the mod list ===
echo [2/3] Copying mod list...
if not exist "%ROOT_DIR%\ModLists" mkdir "%ROOT_DIR%\ModLists"
copy /Y "%MODS_DIR%Nyblya.xml" "%ROOT_DIR%\ModLists\Nyblya.xml"

rem === Run the PowerShell updater ===
echo [3/3] Running PowerShell updater...
powershell -NoProfile -ExecutionPolicy Bypass -File "%MODS_DIR%UpdateMods.ps1"

exit /b