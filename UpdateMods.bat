@echo off
chcp 65001 >nul

rem %~dp0 is the absolute path to this .bat folder (LocalMods),
rem so it does not depend on the current working directory.
set "MODS_DIR=%~dp0"
set "ROOT_DIR=%MODS_DIR%.."

rem If we were re-launched after this .bat updated itself, skip the pull
rem and jump straight to the work section (prevents pulling twice / loops).
if "%~1"=="__afterupdate" goto :work

rem === [1/4] Pull mod updates FIRST ===
rem We do this before anything else so that an updated UpdateMods.bat /
rem UpdateMods.ps1 / mod list can take effect on THIS run.
rem We hash this .bat before and after the pull: if git rewrote it, we
rem re-launch the fresh copy and stop executing the (now stale) old one.
echo [1/4] Pulling mod updates...
set "SELF_BEFORE="
set "SELF_AFTER="
for /f "delims=" %%H in ('git -C "%MODS_DIR%." hash-object "%~f0" 2^>nul') do set "SELF_BEFORE=%%H"
git -C "%MODS_DIR%." pull
for /f "delims=" %%H in ('git -C "%MODS_DIR%." hash-object "%~f0" 2^>nul') do set "SELF_AFTER=%%H"

if not "%SELF_BEFORE%"=="%SELF_AFTER%" goto :relaunch
goto :work

:relaunch
echo UpdateMods.bat was updated by the pull - restarting with the new version...
call "%~f0" __afterupdate
exit /b

:work

rem === [2/4] Install / update LuaCsForBarotrauma (Luatrauma AutoUpdater) ===
rem Runs from the game root so the patch is applied to the game files.
rem No game command is passed: the updater only patches here; the game
rem is launched later by %%command%% in the Steam launch options.
echo [2/4] Updating LuaCsForBarotrauma...
pushd "%ROOT_DIR%"
"%SystemRoot%\System32\curl.exe" -L -o Luatrauma.AutoUpdater.win-x64.exe https://github.com/Luatrauma/Luatrauma.AutoUpdater/releases/download/latest/Luatrauma.AutoUpdater.win-x64.exe
if exist Luatrauma.AutoUpdater.win-x64.exe Luatrauma.AutoUpdater.win-x64.exe
popd

rem === [3/4] Copy the mod list ===
echo [3/4] Copying mod list...
if not exist "%ROOT_DIR%\ModLists" mkdir "%ROOT_DIR%\ModLists"
copy /Y "%MODS_DIR%Nyblya.xml" "%ROOT_DIR%\ModLists\Nyblya.xml"

rem === [4/4] Run the PowerShell updater ===
echo [4/4] Running PowerShell updater...
powershell -NoProfile -ExecutionPolicy Bypass -File "%MODS_DIR%UpdateMods.ps1"

exit /b
