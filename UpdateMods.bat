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

rem === [2/4] Apply LuaCsForBarotrauma from the local patch.zip (no AutoUpdater, no download) ===
rem Extracts LocalMods\patch.zip straight into the game folder (same file-replacement logic the
rem Luatrauma AutoUpdater used), so the LuaCs install is deterministic and does not depend on the
rem AutoUpdater exe succeeding. patch.zip is kept in the repo and updated manually per LuaCs release.
rem If the patch is NOT applied we PAUSE loudly so the failure is visible (C# mods won't work).
echo [2/4] Applying LuaCs patch from patch.zip...
powershell -NoProfile -ExecutionPolicy Bypass -File "%MODS_DIR%ApplyLuaCsPatch.ps1"
if errorlevel 1 (
    echo.
    echo  ************************************************************************
    echo   LuaCs patch was NOT applied - C# mods will NOT work until this is fixed.
    echo   Read the red message above. Common causes: game not fully closed,
    echo   antivirus, or patch.zip missing / wrong game version.
    echo  ************************************************************************
    echo.
    pause
)

rem === [3/4] Copy the mod list ===
echo [3/4] Copying mod list...
if not exist "%ROOT_DIR%\ModLists" mkdir "%ROOT_DIR%\ModLists"
copy /Y "%MODS_DIR%Nyblya.xml" "%ROOT_DIR%\ModLists\Nyblya.xml"

rem === [4/4] Run the PowerShell updater ===
echo [4/4] Running PowerShell updater...
powershell -NoProfile -ExecutionPolicy Bypass -File "%MODS_DIR%UpdateMods.ps1"

exit /b
