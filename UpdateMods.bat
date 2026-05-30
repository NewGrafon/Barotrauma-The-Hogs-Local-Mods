@echo off
chcp 65001 >nul

rem %~dp0 is the absolute path to this .bat folder (LocalMods),
rem so it does not depend on the current working directory.
set "MODS_DIR=%~dp0"
set "ROOT_DIR=%MODS_DIR%.."

rem === Install / update LuaCsForBarotrauma (Luatrauma AutoUpdater) ===
rem Runs from the game root so the patch is applied to the game files.
rem No game command is passed: the updater only patches here; the game
rem is launched later by %%command%% in the Steam launch options.
echo [1/4] Updating LuaCsForBarotrauma...
pushd "%ROOT_DIR%"
"%SystemRoot%\System32\curl.exe" -L -o Luatrauma.AutoUpdater.win-x64.exe https://github.com/Luatrauma/Luatrauma.AutoUpdater/releases/download/latest/Luatrauma.AutoUpdater.win-x64.exe
if exist Luatrauma.AutoUpdater.win-x64.exe Luatrauma.AutoUpdater.win-x64.exe
popd

rem === Pull mod updates ===
echo [2/4] Pulling mod updates...
pushd "%MODS_DIR%"
git pull
popd

rem === Copy the mod list ===
echo [3/4] Copying mod list...
if not exist "%ROOT_DIR%\ModLists" mkdir "%ROOT_DIR%\ModLists"
copy /Y "%MODS_DIR%Nyblya.xml" "%ROOT_DIR%\ModLists\Nyblya.xml"

rem === Run the PowerShell updater ===
echo [4/4] Running PowerShell updater...
powershell -NoProfile -ExecutionPolicy Bypass -File "%MODS_DIR%UpdateMods.ps1"

exit /b